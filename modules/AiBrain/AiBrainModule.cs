using System;
using System.Drawing;
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
            try { _lifetime.Cancel(); } catch { }
            try { _session.Dispose(); } catch { }
            _host = null;
        }
    }
}
