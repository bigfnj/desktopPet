using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.Ai;
using DesktopPet.Modules;

namespace DesktopPet.AiBrainModule
{
    /// <summary>
    /// The AI-brain module (S4): the optional, off-by-default screen-commentary LLM, now LIVE (S4b). It owns
    /// the "ask about my screen" flow — a global hotkey, the arbitrated periodic drop (outranking Fortunes),
    /// and an opt-in idle-commentary loop — plus the emotion->animation reaction, all through host services
    /// (CaptureScreenContext, RegisterHotkey, RegisterDropResponder, SayAll, PlayAnimationAll). The brain
    /// lifecycle (generation/supersede, prepare/retire) is the relocated AiSessionManager; settings + chat
    /// history are the module's own DPAPI-scoped store. It is OFF by default (its own AiBrainEnabled), so a
    /// fresh install does nothing until enabled. There is no tray/Options UI yet (accept-the-gap): the
    /// enable + config UI is rebuilt from module contributions in S5; until then it reads its settings file.
    /// </summary>
    public sealed class AiBrainModule : IModule
    {
        private IHost _host;
        private SynchronizationContext _ui;                 // captured on the UI thread in Init
        private readonly AiSessionManager _session = new AiSessionManager();
        private AiSettings _settings;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private int _generation;
        private IDisposable _dropResponder;
        private IDisposable _hotkey;
        private System.Windows.Forms.Timer _idleTimer;
        private EventHandler _idleTimerHandler;
        private IPet _lastPet;                              // most-recently-seen pet (screen-context anchor)
        private DateTime _lastInteractionUtc = DateTime.MinValue;
        private readonly Random _rand = new Random();

        private static readonly string[] NoAnimation = new string[0];

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "aibrain",
            Name = "AI Brain",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation |
                          ModulePermissions.ScreenContext | ModulePermissions.Network |
                          ModulePermissions.Hotkey | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _ui = SynchronizationContext.Current;   // the WinForms UI-thread context (host loads modules there)

            try
            {
                IModuleStorage storage = host.GetStorage("aibrain");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                {
                    AiPaths.SetRoot(storage.DataDirectory);
                    MigrateFromBaseIfNeeded(storage.DataDirectory);   // one-time, non-destructive
                }
                _settings = AiSettings.Load();
            }
            catch { _settings = new AiSettings(); }

            // Track the current pet for the screen-context anchor; harmless while the brain is off.
            host.PetSpawned += OnPetSeen;
            host.PetLanded += OnPetSeen;
            host.PetPoked += OnPetPoked;
            // Outrank Fortunes (priority 0) on the shared drop: when the brain is on, the drop is an AI
            // insight and this responder handles it; when off, it declines and Fortunes speaks instead.
            _dropResponder = host.RegisterDropResponder(10, OnDrop);

            // Contribute the AI tray items (S5a): the host merges these into the tray, re-evaluating
            // DynamicText/Visible on each open. This is the module's own enable/ask entry point now that the
            // base's AI tray items are gone (closes the S4 accept-the-gap).
            host.AddTrayItems(new[]
            {
                new TrayItem
                {
                    Label = "Enable AI", Group = 50, Order = 0,
                    DynamicText = delegate { return (_settings != null && _settings.AiBrainEnabled) ? "Disable AI" : "Enable AI"; },
                    Click = ToggleEnabled,
                },
                new TrayItem
                {
                    Label = "Ask about my screen", Group = 50, Order = 1,
                    Visible = delegate { return _settings != null && _settings.AiBrainEnabled; },
                    Click = delegate { Ask(true); },
                },
            });

            // Contribute the AI config as a schema-driven OptionsPane (S5b): the host renders it in the WPF
            // settings window and round-trips values through this Load/Save, which persist to the module's
            // own AiSettings store. Exercises every field kind (bool/int/text/enum/secret).
            var providerIds = new List<string>();
            foreach (AiProviders.Preset p in AiProviders.All) providerIds.Add(p.Id);
            host.AddOptionsPane(new OptionsPane
            {
                Title = "AI Brain",
                Schema = new[]
                {
                    new SettingField { Id = "enabled", Label = "Enable AI brain", Kind = SettingKind.Bool, Group = "AI brain" },
                    new SettingField { Id = "petName", Label = "Pet name", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "userName", Label = "Your name (optional)", Kind = SettingKind.Text, Group = "Persona" },
                    new SettingField { Id = "personality", Label = "Personality", Kind = SettingKind.Enum, Options = PersonalityLabels(), Group = "Persona" },
                    new SettingField { Id = "speechStyle", Label = "Speech style", Kind = SettingKind.Enum, Options = SpeechStyleNames(), Group = "Persona" },
                    new SettingField { Id = "memory", Label = "Remember recent remarks", Kind = SettingKind.Bool, Group = "Persona" },
                    new SettingField { Id = "provider", Label = "Provider", Kind = SettingKind.Enum, Options = providerIds.ToArray(), Group = "Provider" },
                    new SettingField { Id = "endpoint", Label = "Endpoint / base URL", Kind = SettingKind.Text, Group = "Provider" },
                    new SettingField { Id = "cloudConsent", Label = "Allow cloud data sharing", Kind = SettingKind.Bool, Group = "Provider" },
                    new SettingField { Id = "textModel", Label = "Text model", Kind = SettingKind.Text, Group = "Provider" },
                    new SettingField { Id = "visionModel", Label = "Vision model", Kind = SettingKind.Text, Group = "Provider" },
                    new SettingField { Id = "useVision", Label = "Use vision on explicit asks", Kind = SettingKind.Bool, Group = "Provider" },
                    new SettingField { Id = "apiKey", Label = "API key (cloud providers)", Kind = SettingKind.Secret, Group = "Provider" },
                    new SettingField { Id = "hotkey", Label = "Ask hotkey", Kind = SettingKind.Text, Group = "Triggers" },
                    new SettingField { Id = "idle", Label = "Idle commentary", Kind = SettingKind.Bool, Group = "Triggers" },
                    new SettingField { Id = "idleMin", Label = "Idle min (seconds)", Kind = SettingKind.Int, Min = 15, Max = 3600, Group = "Triggers" },
                    new SettingField { Id = "idleMax", Label = "Idle max (seconds)", Kind = SettingKind.Int, Min = 15, Max = 3600, Group = "Triggers" },
                    new SettingField { Id = "autoStart", Label = "Start Ollama automatically", Kind = SettingKind.Bool, Group = "Local server (Ollama)" },
                    new SettingField { Id = "preload", Label = "Preload model on launch", Kind = SettingKind.Bool, Group = "Local server (Ollama)" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Test connection", InvokeAsync = TestConnectionAsync, Group = "Provider" },
                    new PaneAction { Label = "Test OCR", InvokeAsync = TestOcrAsync, Group = "Provider" },
                    new PaneAction { Label = "Clear chat history", InvokeAsync = ClearHistoryAsync, Group = "Persona" },
                },
            });

            ApplyState();
        }

        /// <summary>Test-connection action for the WPF pane: build a backend from the current settings, probe
        /// availability + a tiny chat, and report a status line. Async so the pane stays responsive.</summary>
        private async Task<string> TestConnectionAsync()
        {
            AiSettings s = _settings;
            if (s == null) return "No settings.";
            string endpoint = SelectedEndpoint(s);
            string normalized, err;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out err)) return "✗ " + err;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                return "✗ Approve cloud data sharing first.";
            try
            {
                TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(120, s.TimeoutSeconds)));
                IPetBrainBackend backend = IsOllama(s)
                    ? new OllamaClient(normalized, timeout, s.OllamaPath)
                    : (IPetBrainBackend)new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
                using (backend)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (!await backend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
                        return "✗ Not reachable at " + normalized;
                    string model = string.IsNullOrWhiteSpace(s.TextModel) ? "llama3.1:8b" : s.TextModel.Trim();
                    var msgs = new List<ChatMessage> { ChatMessage.System("Reply with OK."), ChatMessage.User("OK?", null) };
                    await backend.ChatAsync(model, msgs, false, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    return "✓ connected · " + model + " OK " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        /// <summary>"Test OCR" action: run the OCR self-test (resolve tesseract + read a known image) so a
        /// missing/broken engine surfaces as a red status instead of silently making remarks screen-blind.</summary>
        private async Task<string> TestOcrAsync()
        {
            AiSettings s = _settings ?? new AiSettings();
            try
            {
                using (var probe = new AiBrain(null, s))
                    return await probe.SelfTestOcrAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { return "✗ OCR test failed: " + ex.Message; }
        }

        /// <summary>Clear-history action: delete the module's persisted chat history.</summary>
        private Task<string> ClearHistoryAsync()
        {
            try
            {
                ChatHistoryDeleteResult r = ChatHistory.DeletePersisted();
                return Task.FromResult(r.Succeeded ? "Chat history cleared." : ("Could not clear: " + r.Error));
            }
            catch (Exception ex) { return Task.FromResult("Failed: " + ex.Message); }
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            AiSettings s = _settings;
            if (s != null)
            {
                d["enabled"] = s.AiBrainEnabled ? "true" : "false";
                d["petName"] = s.PetName ?? "";
                d["userName"] = s.UserName ?? "";
                d["personality"] = PersonalityLabelForBlurb(s.Personality);
                d["speechStyle"] = SpeechNameForId(s.SpeechPattern);
                d["memory"] = s.MemoryEnabled ? "true" : "false";
                d["provider"] = s.Provider ?? "ollama";
                d["endpoint"] = SelectedEndpoint(s) ?? "";
                d["cloudConsent"] = s.CloudDataConsent ? "true" : "false";
                d["textModel"] = s.TextModel ?? "";
                d["visionModel"] = s.VisionModel ?? "";
                d["useVision"] = s.UseVision ? "true" : "false";
                d["hotkey"] = s.Hotkey ?? "";
                d["idle"] = s.IdleCommentaryEnabled ? "true" : "false";
                d["idleMin"] = s.IdleMinSeconds.ToString(CultureInfo.InvariantCulture);
                d["idleMax"] = s.IdleMaxSeconds.ToString(CultureInfo.InvariantCulture);
                d["autoStart"] = s.AutoStartServer ? "true" : "false";
                d["preload"] = s.WarmUpOnLaunch ? "true" : "false";
                d["apiKey"] = string.IsNullOrEmpty(s.ApiKey) ? "" : "set";   // hint only; never the plaintext
            }
            return d;
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            AiSettings s = _settings;
            if (s == null || values == null) return false;
            string v;
            bool b; int n;
            if (values.TryGetValue("enabled", out v) && bool.TryParse(v, out b)) s.AiBrainEnabled = b;
            if (values.TryGetValue("petName", out v)) s.PetName = (v ?? "").Trim();
            if (values.TryGetValue("userName", out v)) s.UserName = (v ?? "").Trim();
            if (values.TryGetValue("personality", out v)) s.Personality = PersonalityBlurbForLabel(v);
            if (values.TryGetValue("speechStyle", out v)) s.SpeechPattern = SpeechIdForName(v);
            if (values.TryGetValue("memory", out v) && bool.TryParse(v, out b)) s.MemoryEnabled = b;
            // Provider + endpoint: switching provider prefills its default endpoint (the stale endpoint field
            // is ignored on a switch); keeping the provider honors an edited endpoint.
            bool providerChanged = false;
            if (values.TryGetValue("provider", out v) && !string.IsNullOrWhiteSpace(v))
            {
                providerChanged = !string.Equals(v, s.Provider, StringComparison.OrdinalIgnoreCase);
                s.SelectProviderEndpoint(v, providerChanged);
            }
            if (!providerChanged && values.TryGetValue("endpoint", out v) && !string.IsNullOrWhiteSpace(v))
                s.UpdateSelectedProviderEndpoint(v.Trim());
            if (values.TryGetValue("cloudConsent", out v) && bool.TryParse(v, out b)) s.CloudDataConsent = b;
            if (values.TryGetValue("textModel", out v) && !string.IsNullOrWhiteSpace(v)) s.TextModel = v.Trim();
            if (values.TryGetValue("visionModel", out v) && !string.IsNullOrWhiteSpace(v)) s.VisionModel = v.Trim();
            if (values.TryGetValue("useVision", out v) && bool.TryParse(v, out b)) s.UseVision = b;
            if (values.TryGetValue("hotkey", out v) && !string.IsNullOrWhiteSpace(v)) s.Hotkey = v.Trim();
            if (values.TryGetValue("idle", out v) && bool.TryParse(v, out b)) s.IdleCommentaryEnabled = b;
            if (values.TryGetValue("idleMin", out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.IdleMinSeconds = n;
            if (values.TryGetValue("idleMax", out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.IdleMaxSeconds = n;
            if (values.TryGetValue("autoStart", out v) && bool.TryParse(v, out b)) s.AutoStartServer = b;
            if (values.TryGetValue("preload", out v) && bool.TryParse(v, out b)) s.WarmUpOnLaunch = b;
            // Secret: only present when the user typed a new key; best-effort (needs a non-ollama scope).
            if (values.TryGetValue("apiKey", out v) && !string.IsNullOrEmpty(v)) { string err; s.TrySetApiKey(v, out err); }
            bool ok = s.Save();
            ApplyState();   // re-apply triggers/backend to reflect the new config
            return ok;
        }

        // Personality presets: the dropdown shows the Label; the Blurb is what goes into the system prompt
        // ("Your personality: <blurb>."). A canned list keeps the persona realistic + prompt-safe instead of
        // free text a user might phrase in a way that doesn't read well. The first entry's blurb matches the
        // AiSettings default so a fresh install round-trips; an older free-text value that matches no preset
        // falls back to the first preset (the user just re-picks).
        private static readonly string[][] PersonalityPresets = new[]
        {
            new[] { "Friendly & upbeat",  "warm, upbeat and irrepressibly cheerful" },
            new[] { "Dry & sarcastic",    "dry, sarcastic and razor-witted, delivered deadpan" },
            new[] { "Cheerful & bubbly",  "bubbly, hyper-enthusiastic and relentlessly positive" },
            new[] { "Calm & zen",         "serene, deeply thoughtful and quietly philosophical" },
            new[] { "Sassy & bold",       "sassy, brash and unapologetically dramatic" },
            new[] { "Shy & sweet",        "shy, soft-spoken and achingly earnest" },
            new[] { "Grumpy but lovable", "grumpy, gruff and impossible to impress, but secretly caring" },
            new[] { "Curious & nerdy",    "curious, geeky and obsessed with tiny details" },
            new[] { "Wise mentor",        "warm, wise and encouraging, like a patient mentor" },
            new[] { "Chaotic & goofy",    "goofy, unhinged and bursting with chaotic energy" },
            new[] { "Cool & aloof",       "cool, aloof and utterly unbothered by everything" },
            new[] { "Motivational coach", "loud, high-energy and relentlessly motivating" },
            new[] { "Samuel",             "intense, blunt and effortlessly cool, with commanding swagger and constant, unfiltered profanity, exactly like Samuel L. Jackson" },
            new[] { "Triumph",            "Triumph the Insult Comic Dog: treat everything on screen and everything about the user as material for a savage roast. Open each remark with a mock-compliment, then tear it apart, and land the catchphrase 'for me to POOP on!' when it fits. Never sincere, always a put-down. (Pair with the Samuel speech style for a relentlessly profane insult act.)" },
        };
        private static string[] PersonalityLabels()
        {
            var labels = new List<string>(PersonalityPresets.Length);
            foreach (string[] p in PersonalityPresets) labels.Add(p[0]);
            return labels.ToArray();
        }
        private static string PersonalityBlurbForLabel(string label)
        {
            foreach (string[] p in PersonalityPresets)
                if (string.Equals(p[0], label, StringComparison.Ordinal)) return p[1];
            return PersonalityPresets[0][1];
        }
        private static string PersonalityLabelForBlurb(string blurb)
        {
            string b = (blurb ?? "").Trim();
            foreach (string[] p in PersonalityPresets)
                if (string.Equals(p[1], b, StringComparison.OrdinalIgnoreCase)) return p[0];
            return PersonalityPresets[0][0];   // unknown/older free-text value -> first preset
        }

        // Speech-style enum: the pane shows the friendly names, the setting stores the id.
        private static string[] SpeechStyleNames()
        {
            var names = new List<string>();
            foreach (Personas.Speech sp in Personas.SpeechPatterns) names.Add(sp.Name);
            return names.ToArray();
        }
        private static string SpeechNameForId(string id)
        {
            foreach (Personas.Speech sp in Personas.SpeechPatterns)
                if (string.Equals(sp.Id, id, StringComparison.OrdinalIgnoreCase)) return sp.Name;
            return Personas.SpeechPatterns[0].Name;
        }
        private static string SpeechIdForName(string name)
        {
            foreach (Personas.Speech sp in Personas.SpeechPatterns)
                if (string.Equals(sp.Name, name, StringComparison.Ordinal)) return sp.Id;
            return "none";
        }

        /// <summary>Tray toggle: flip the module's own AiBrainEnabled, persist, and (re)build the brain.</summary>
        private void ToggleEnabled()
        {
            AiSettings s = _settings;
            if (s == null) return;
            s.AiBrainEnabled = !s.AiBrainEnabled;
            try { s.Save(); } catch { }
            ApplyState();
        }

        private void OnPetSeen(IPet pet) { if (pet != null) _lastPet = pet; }
        private void OnPetPoked(PokeInfo info) { if (info != null && info.Pet != null) _lastPet = info.Pet; }

        /// <summary>Drop responder: when the brain is enabled, take the tick as an AI insight (return
        /// handled so Fortunes stays quiet); otherwise decline so Fortunes handles it.</summary>
        private bool OnDrop()
        {
            if (!_session.Enabled) return false;
            Ask(true);
            return true;
        }

        // ---- the ask flow (mirrors the old StartUp.AskAboutScreen) --------------------------------

        /// <summary>Kick off one screen-commentary turn. Call on the UI thread (drop/hotkey/idle tick).</summary>
        private void Ask(bool allowVision)
        {
            IHost host = _host;
            AiSessionManager session = _session;
            if (host == null || !session.Enabled || !host.SpeechEnabled) return;
            if (session.RequestInProgress) return;
            IPet pet = _lastPet;
            if (pet == null) return;

            _lastInteractionUtc = DateTime.UtcNow;
            ScreenContext ctx;
            try { ctx = host.CaptureScreenContext(pet); } catch { ctx = null; }
            if (ctx == null) return;

            // A "pondering" cue while the model responds (we are on the UI thread here).
            try { host.PlayAnimationAll(EmotionAnimations("thinking")); host.SayAll("…"); } catch { }

            _ = AskCoreAsync(session, ctx, ctx.WindowUnderPet, allowVision);
        }

        private async Task AskCoreAsync(AiSessionManager session, ScreenContext ctx, string petZone, bool allowVision)
        {
            BrainResponse r;
            try { r = await session.AskAsync(ctx, petZone, allowVision, _lifetime.Token).ConfigureAwait(false); }
            catch { r = null; }
            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;

            // Apply on the UI thread: map the emotion to an animation, then speak.
            PostToUi(delegate
            {
                IHost host = _host;
                if (host == null) return;
                try
                {
                    host.PlayAnimationAll(EmotionAnimations(r.Emotion));
                    host.SayAll(r.Text);
                }
                catch { }
            });
        }

        // ---- state application (mirrors ApplyAiBrainState + ApplyAiTriggers) ----------------------

        /// <summary>Build/retire the backend and (re)arm the hotkey + idle loop from the current settings.</summary>
        private void ApplyState()
        {
            AiSettings s = _settings ?? new AiSettings();
            // Publish the user's name so other modules (the fortunes welcome) address them the same when the
            // brain is on; clear it when off so they fall back to their own default (the Windows user name).
            try { if (_host != null) _host.SetOwnerName((s.AiBrainEnabled && !string.IsNullOrWhiteSpace(s.UserName)) ? s.UserName.Trim() : ""); }
            catch { }
            string err;
            bool allowed = s.AiBrainEnabled && CanUse(s, out err);
            int gen = ++_generation;
            bool prepare = allowed && (s.AutoStartServer || s.WarmUpOnLaunch);
            AiSettings snapshot = s;

            // Fire-and-forget: the session serializes generations, so a stale config can never apply.
            _ = _session.ReconfigureAsync(
                allowed ? (Func<AiBrain>)delegate { return CreateBrain(snapshot); } : null,
                allowed,
                prepare,
                _lifetime.Token);

            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            if (allowed && s.HotkeyEnabled && _host != null)
                _hotkey = _host.RegisterHotkey(s.Hotkey, delegate { Ask(true); });

            StopIdle();
            if (allowed && s.IdleCommentaryEnabled)
            {
                var timer = new System.Windows.Forms.Timer();
                int g = gen;
                EventHandler handler = null;
                handler = delegate { IdleTick(timer, g); };
                timer.Tick += handler;
                _idleTimer = timer;
                _idleTimerHandler = handler;
                ScheduleIdle(gen, timer);
            }
        }

        private void StopIdle()
        {
            if (_idleTimer == null) return;
            try
            {
                _idleTimer.Stop();
                if (_idleTimerHandler != null) _idleTimer.Tick -= _idleTimerHandler;
                _idleTimer.Dispose();
            }
            catch { }
            _idleTimer = null;
            _idleTimerHandler = null;
        }

        private void ScheduleIdle(int gen, System.Windows.Forms.Timer timer)
        {
            if (timer == null || gen != _generation || !ReferenceEquals(_idleTimer, timer)) return;
            AiSettings s = _settings ?? new AiSettings();
            int lo = Math.Min(86400, Math.Max(15, s.IdleMinSeconds));
            int hi = Math.Min(86400, Math.Max(lo, s.IdleMaxSeconds));
            timer.Interval = _rand.Next(lo, hi + 1) * 1000;
            timer.Start();
        }

        /// <summary>Idle-commentary tick + gate (only when a pet is present, speech is on, no recent
        /// interaction, the pet isn't busy, and the screen actually changed). Mirrors StartUp.IdleTimer_Tick,
        /// but the AiSessionManager's own generation guards replace the base's GenerationAwareIdleSchedule.</summary>
        private async void IdleTick(System.Windows.Forms.Timer timer, int gen)
        {
            try { timer.Stop(); } catch { }
            try
            {
                if (gen != _generation || !_session.Enabled) return;
                bool recentlyInteracted = (DateTime.UtcNow - _lastInteractionUtc).TotalSeconds < 30;
                IHost host = _host;
                IPet pet = _lastPet;
                if (host != null && host.SpeechEnabled && !recentlyInteracted && pet != null && !pet.IsBusy)
                {
                    ScreenContext ctx = null;
                    try { ctx = host.CaptureScreenContext(pet); } catch { }
                    if (ctx != null)
                    {
                        PixelRect mb = ctx.MonitorBounds;
                        bool changed = await _session.ScreenChangedAsync(
                            new Rectangle(mb.X, mb.Y, mb.Width, mb.Height),
                            (_settings ?? new AiSettings()).IdleChangeThresholdPercent,
                            _lifetime.Token).ConfigureAwait(false);
                        if (changed && gen == _generation)
                            PostToUi(delegate { Ask(false); });   // idle stays on the fast text path
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { PostToUi(delegate { ScheduleIdle(gen, timer); }); }
        }

        // ---- brain construction (mirrors StartUp.CreateBrain / CanUseAiConfiguration) -------------

        private static AiBrain CreateBrain(AiSettings s)
        {
            string endpoint = SelectedEndpoint(s);
            string normalized, error;
            if (!AiEndpointPolicy.TryNormalize(endpoint, out normalized, out error))
                throw new InvalidDataException(error);
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
                throw new InvalidOperationException("Cloud data consent is required for a non-local AI endpoint.");

            TimeSpan timeout = TimeSpan.FromSeconds(s.TimeoutSeconds);
            IPetBrainBackend backend = IsOllama(s)
                ? new OllamaClient(normalized, timeout, s.OllamaPath)
                : (IPetBrainBackend)new OpenAiCompatBackend(normalized, s.ApiKey, timeout);
            return new AiBrain(backend, s);
        }

        private static bool CanUse(AiSettings s, out string error)
        {
            error = null;
            if (s == null) { error = "AI settings are unavailable."; return false; }
            string normalized;
            if (!AiEndpointPolicy.TryNormalize(SelectedEndpoint(s), out normalized, out error)) return false;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) && !s.CloudDataConsent)
            {
                error = "Approve cloud data sharing before using a non-local AI endpoint.";
                return false;
            }
            return true;
        }

        private static bool IsOllama(AiSettings s)
        {
            return string.IsNullOrEmpty(s.Provider) ||
                   string.Equals(s.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        }

        private static string SelectedEndpoint(AiSettings s)
        {
            return IsOllama(s) ? s.Endpoint : s.OpenAiBaseUrl;
        }

        /// <summary>Prioritized candidate animations per emotion (data lifted from the old StartUp table);
        /// the host plays the first one each pet's XML defines. Neutral/unknown => no forced animation.</summary>
        private static string[] EmotionAnimations(string emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return NoAnimation;
            switch (emotion.Trim().ToLowerInvariant())
            {
                case "happy":    return new string[] { "flower", "jump", "boing" };
                case "excited":  return new string[] { "run", "jump", "boing" };
                case "sad":      return new string[] { "sleep1a", "sleep2a" };
                case "thinking": return new string[] { "sleep1a" };
                case "confused": return new string[] { "rotate1a", "boing" };
                default:         return NoAnimation;
            }
        }

        /// <summary>One-time, non-destructive migration: if the module has no settings yet but the base
        /// ai-settings.json exists, copy it (including the DPAPI-encrypted keys, decryptable by the same
        /// Windows user) into the module store. The base file is left intact — the base still reads its
        /// fortune fields from it, and the copied fortune fields are simply unused by this module.</summary>
        private static void MigrateFromBaseIfNeeded(string moduleDir)
        {
            try
            {
                string moduleFile = Path.Combine(moduleDir, "ai-settings.json");
                if (File.Exists(moduleFile)) return;

                string baseRoot = Environment.GetEnvironmentVariable("DESKTOPPET_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(baseRoot))
                    baseRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DesktopPet");
                string baseFile = Path.Combine(baseRoot, "ai-settings.json");
                if (!File.Exists(baseFile)) return;

                File.Copy(baseFile, moduleFile, false);
            }
            catch { }
        }

        private void PostToUi(Action action)
        {
            if (action == null) return;
            SynchronizationContext ui = _ui;
            if (ui != null) ui.Post(delegate { action(); }, null);
            else action();
        }

        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.PetSpawned -= OnPetSeen;
                host.PetLanded -= OnPetSeen;
                host.PetPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_hotkey != null) { try { _hotkey.Dispose(); } catch { } _hotkey = null; }
            StopIdle();
            try { _lifetime.Cancel(); _lifetime.Dispose(); } catch { }
            try { _session.Dispose(); } catch { }
            _host = null;
        }
    }
}
