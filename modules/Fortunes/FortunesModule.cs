using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopPet.Ai;
using DesktopPet.Modules;

namespace DesktopPet.FortunesModule
{
    /// <summary>
    /// The Fortunes module (S3). Owns the pet's fortune voice: a personalized welcome on the first spawn,
    /// then a fortune on land / poke (1-2) / the periodic drop — spoken from the module's relocated engine
    /// (dumb random + smart ONNX-semantic pick). It ships NO fortune content, so with no installed pack it's
    /// silent except the welcome; the engine reads packs from the module's own storage. Since S3d it is the
    /// LIVE source (the base no longer speaks fortunes); the poke escalation's ignore/sass/escape stay in the
    /// base engine, which just raises PetPoked with the count.
    /// </summary>
    public sealed class FortunesModule : IModule
    {
        private IHost _host;
        private string[] _welcome;
        private readonly Random _rand = new Random();
        private bool _welcomed;

        private FortuneProvider _provider;   // the relocated engine (packs -> filtered pool)
        private SmartFortunes _smart;        // optional ONNX semantic picker (null when disabled/unavailable)
        private IPet _lastPet;               // most-recently-seen pet, for screen-context capture on the drop path
        private IDisposable _dropResponder;

        // Poke escalation (base raises PetPoked with the count): the module speaks a fortune only on the
        // first pokes, matching the old StartUp.OnPetPoked (3-4 ignore / 5-11 sass / 12 escape stay in base).
        private const int PokeFortuneUpTo = 2;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _welcome = LoadWelcomeCorpus();

            // Point the engine at the module's own storage, then build the pool from the user's packs (empty
            // by default = silent) and warm the smart picker when enabled. All best-effort: a failure here
            // leaves the module welcome-only rather than breaking the host.
            try
            {
                IModuleStorage storage = host.GetStorage("fortunes");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                    FortunePaths.SetRoot(storage.DataDirectory);
            }
            catch { }
            RebuildEngine();

            host.PetSpawned += OnPetSpawned;
            host.PetLanded += OnPetLanded;
            host.PetPoked += OnPetPoked;
            _dropResponder = host.RegisterDropResponder(0, OnDrop);   // lowest priority; the AI brain (S4) will outrank

            // Contribute the fortunes settings as a schema-driven OptionsPane (S5b): the host renders it in
            // the WPF settings window and round-trips values through Load/Save, which persist to the module's
            // own host.GetSettings("fortunes") store and rebuild the live engine so a change takes effect on
            // the running pet at once. The richer sources / genres / packs list is a follow-up (it needs a
            // list-card primitive); this pane covers the selection + content-level toggles.
            host.AddOptionsPane(BuildOptionsPane());
        }

        /// <summary>(Re)build the engine from the current saved settings: rebuild the pool from the user's
        /// packs and, when smart picks are on, (re)warm the semantic index. Called at Init and after the
        /// Options pane saves, so a settings change (or a pack added to the folder) applies without a restart.</summary>
        private void RebuildEngine()
        {
            try
            {
                FortuneSettings settings = LoadFortuneSettings(_host);
                _provider = new FortuneProvider(settings);
                SmartFortunes old = _smart;
                _smart = null;
                if (old != null) { try { old.Dispose(); } catch { } }
                if (settings.SmartFortunes)
                {
                    var sm = new SmartFortunes();
                    sm.Warm(_provider.PoolEntries());
                    _smart = sm;
                }
            }
            catch { _provider = null; _smart = null; }
        }

        // The first pet of the session gets a personalized greeting; later spawns don't re-welcome.
        private void OnPetSpawned(IPet pet)
        {
            _lastPet = pet ?? _lastPet;
            if (_welcomed) return;
            _welcomed = true;
            IHost host = _host;
            if (host == null) return;
            string line = PickWelcome(GreetingName(host));
            if (!string.IsNullOrEmpty(line)) host.SayAll(line);
        }

        private void OnPetLanded(IPet pet) { _lastPet = pet ?? _lastPet; SpeakFortune(); }

        private void OnPetPoked(PokeInfo info)
        {
            if (info == null) return;
            _lastPet = info.Pet ?? _lastPet;
            if (info.PokeCount >= 1 && info.PokeCount <= PokeFortuneUpTo) SpeakFortune();
        }

        // The periodic drop responder: speak a fortune. Returns true when it actually spoke (handled), so the
        // arbitrated drop chain stops here (fortunes are the lowest-priority default).
        private bool OnDrop() { return SpeakFortune(); }

        /// <summary>
        /// Speak a fortune — smart/contextual pick when the picker is ready, else random from the pool.
        /// Mirrors the old StartUp.SayFortune. Returns true if a line was spoken.
        /// </summary>
        private bool SpeakFortune()
        {
            IHost host = _host;
            FortuneProvider provider = _provider;
            if (host == null || provider == null || !host.SpeechEnabled) return false;

            string f = null;
            // Roughly a third of the time draw from the whole pool even when smart is ready, so a rarely-
            // changing foreground window doesn't lock the pet onto the same handful of context matches.
            bool goRandom = _rand.Next(3) == 0;
            SmartFortunes picker = _smart;
            if (!goRandom && picker != null && picker.Ready)
            {
                try
                {
                    ScreenContext ctx = _lastPet != null ? host.CaptureScreenContext(_lastPet) : null;
                    if (ctx != null) f = picker.Pick(ctx.WindowTitle, ctx.ProcessName);
                }
                catch { f = null; }
            }
            if (string.IsNullOrWhiteSpace(f)) f = provider.Pick();
            if (string.IsNullOrWhiteSpace(f)) return false;
            host.SayAll(f);
            return true;
        }

        private static FortuneSettings LoadFortuneSettings(IHost host)
        {
            var s = new FortuneSettings();
            try
            {
                IModuleSettings ms = host.GetSettings("fortunes");
                if (ms != null)
                {
                    s.SpicyFortunes = ms.GetBool("spicyFortunes", s.SpicyFortunes);
                    s.SpicyTier = ms.Get("spicyTier", s.SpicyTier);
                    s.SpicyOnly = ms.GetBool("spicyOnly", s.SpicyOnly);
                    s.NoProfanity = ms.GetBool("noProfanity", s.NoProfanity);
                    s.SmartFortunes = ms.GetBool("smartFortunes", s.SmartFortunes);
                    s.DisabledSources = SplitList(ms.Get("disabledSources", ""));
                    s.DisabledGenres = SplitList(ms.Get("disabledGenres", ""));
                }
            }
            catch { }
            return s;
        }

        // ---- Options pane (S5b): selection + content-level toggles ---------------------------------

        // The spice tier is stored as "edgy" | "nsfw"; the pane shows friendly labels and maps back on save.
        private const string TierEdgyDisplay = "Edgy + NSFW";
        private const string TierNsfwDisplay = "True NSFW only";
        private static string TierToDisplay(string tier)
        {
            return string.Equals(tier, "nsfw", StringComparison.OrdinalIgnoreCase) ? TierNsfwDisplay : TierEdgyDisplay;
        }
        private static string DisplayToTier(string display)
        {
            return string.Equals(display, TierNsfwDisplay, StringComparison.Ordinal) ? "nsfw" : "edgy";
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Fortunes",
                Schema = new[]
                {
                    new SettingField { Id = "smartFortunes", Label = "Smart, context-aware picks", Kind = SettingKind.Bool, Group = "Selection" },
                    new SettingField { Id = "spicyFortunes", Label = "Enable spicy content", Kind = SettingKind.Bool, Group = "Content level" },
                    new SettingField { Id = "spicyTier", Label = "Spice level (when spicy is on)", Kind = SettingKind.Enum, Options = new[] { TierEdgyDisplay, TierNsfwDisplay }, Group = "Content level" },
                    new SettingField { Id = "spicyOnly", Label = "Skip the tame ones", Kind = SettingKind.Bool, Group = "Content level" },
                    new SettingField { Id = "noProfanity", Label = "Remove profanity / explicit words", Kind = SettingKind.Bool, Group = "Content level" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Rebuild smart index", InvokeAsync = RebuildSmartIndexAsync, Group = "Selection" },
                },
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Fortune packs",
                        LoadItems = LoadSourceItems,
                        SetChecked = SetSourceActive,
                        EmptyHint = "No fortune packs yet. Click “Open fortunes folder”, drop a .txt pack in, then Rescan.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Open fortunes folder", InvokeAsync = OpenFortunesFolderAsync },
                            new PaneAction { Label = "Rescan folder", InvokeAsync = RescanAsync, ReloadPaneAfter = true },
                        },
                    },
                    new ListCard
                    {
                        Title = "Genres",
                        LoadItems = LoadGenreItems,
                        SetChecked = SetGenreActive,
                        EmptyHint = "Genres appear here once you add a pack.",
                    },
                },
            };
        }

        // ---- list cards: fortune packs (sources) + genres -----------------------------------------

        private IReadOnlyList<ListItem> LoadSourceItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledSources")), StringComparer.OrdinalIgnoreCase);
                foreach (SourceStat st in FortuneProvider.Sources())
                {
                    string detail = st.Count + (st.Count == 1 ? " line" : " lines");
                    if (st.HasSpicy) detail += " · spicy";
                    items.Add(new ListItem { Id = st.Id, Label = PrettySource(st.Id), Detail = detail, Checked = !disabled.Contains(st.Id) });
                }
            }
            catch { }
            return items;
        }

        private IReadOnlyList<ListItem> LoadGenreItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledGenres")), StringComparer.OrdinalIgnoreCase);
                foreach (GenreStat g in FortuneProvider.Genres())
                    items.Add(new ListItem { Id = g.Id, Label = g.Id, Detail = g.Count + (g.Count == 1 ? " line" : " lines"), Checked = !disabled.Contains(g.Id) });
            }
            catch { }
            return items;
        }

        private void SetSourceActive(string id, bool active) { SetDisabled("disabledSources", id, !active); }
        private void SetGenreActive(string id, bool active) { SetDisabled("disabledGenres", id, !active); }

        // Toggle an id in a persisted "disabled" list, then rebuild the live engine so the change applies now.
        private void SetDisabled(string key, string id, bool disabled)
        {
            IHost host = _host;
            if (host == null || string.IsNullOrEmpty(id)) return;
            try
            {
                IModuleSettings ms = host.GetSettings("fortunes");
                if (ms == null) return;
                var set = new List<string>();
                foreach (string x in SplitList(ms.Get(key, "")))
                    if (!string.Equals(x, id, StringComparison.OrdinalIgnoreCase)) set.Add(x);
                if (disabled) set.Add(id);
                ms.Set(key, string.Join("\n", set));
                ms.Save();
                RebuildEngine();
            }
            catch { }
        }

        private Task<string> OpenFortunesFolderAsync()
        {
            try
            {
                string dir = FortunePaths.FortunesDir;   // created on access
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Task.FromResult("Opened the fortunes folder — drop .txt packs there, then Rescan.");
            }
            catch (Exception ex) { return Task.FromResult("Couldn't open the folder: " + ex.Message); }
        }

        private Task<string> RescanAsync()
        {
            try
            {
                RebuildEngine();
                int sources = 0;
                try { sources = FortuneProvider.Sources().Count; } catch { }
                return Task.FromResult(sources == 0
                    ? "No packs found yet."
                    : ("Rescanned — " + sources + (sources == 1 ? " pack" : " packs") + " loaded."));
            }
            catch (Exception ex) { return Task.FromResult("Rescan failed: " + ex.Message); }
        }

        private string GetSetting(string key)
        {
            try { IModuleSettings ms = _host != null ? _host.GetSettings("fortunes") : null; return ms != null ? ms.Get(key, "") : ""; }
            catch { return ""; }
        }

        // Persisted disabled-list format: ids joined by '\n' (source ids/genres never contain newlines).
        private static List<string> SplitList(string joined)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(joined)) return list;
            foreach (string part in joined.Split('\n'))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        private static string PrettySource(string id)
        {
            return string.IsNullOrEmpty(id) ? id : id.Replace('-', ' ').Replace('_', ' ');
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            FortuneSettings s = LoadFortuneSettings(_host);
            d["smartFortunes"] = s.SmartFortunes ? "true" : "false";
            d["spicyFortunes"] = s.SpicyFortunes ? "true" : "false";
            d["spicyTier"] = TierToDisplay(s.SpicyTier);
            d["spicyOnly"] = s.SpicyOnly ? "true" : "false";
            d["noProfanity"] = s.NoProfanity ? "true" : "false";
            return d;
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IHost host = _host;
            if (host == null || values == null) return false;
            IModuleSettings ms = host.GetSettings("fortunes");
            if (ms == null) return false;
            string v; bool b;
            if (values.TryGetValue("smartFortunes", out v) && bool.TryParse(v, out b)) ms.Set("smartFortunes", b ? "true" : "false");
            if (values.TryGetValue("spicyFortunes", out v) && bool.TryParse(v, out b)) ms.Set("spicyFortunes", b ? "true" : "false");
            if (values.TryGetValue("spicyTier", out v) && !string.IsNullOrEmpty(v)) ms.Set("spicyTier", DisplayToTier(v));
            if (values.TryGetValue("spicyOnly", out v) && bool.TryParse(v, out b)) ms.Set("spicyOnly", b ? "true" : "false");
            if (values.TryGetValue("noProfanity", out v) && bool.TryParse(v, out b)) ms.Set("noProfanity", b ? "true" : "false");
            bool ok = ms.Save();
            RebuildEngine();   // re-read + rebuild so the running pet uses the new settings at once
            return ok;
        }

        /// <summary>"Rebuild smart index" action: reload packs from disk and (when smart is on) re-warm the
        /// semantic index, then report status. Also the way to pick up a pack dropped straight into the
        /// folder until the sources/packs card lands.</summary>
        private Task<string> RebuildSmartIndexAsync()
        {
            try
            {
                RebuildEngine();
                return Task.FromResult(SmartStatusText());
            }
            catch (Exception ex) { return Task.FromResult("Rebuild failed: " + ex.Message); }
        }

        private string SmartStatusText()
        {
            SmartFortunes sm = _smart;
            if (sm == null) return "Smart picks are off (random selection).";
            bool ready, complete; int indexed, total;
            sm.WarmProgress(out ready, out complete, out indexed, out total);
            if (total == 0) return "No fortunes yet — add a pack, then rebuild.";
            if (complete) return "Smart index ready — " + indexed + " fortunes indexed.";
            if (ready) return "Smart index warming — " + indexed + " of " + total + " ready (usable now).";
            return "Smart index building… (" + total + " fortunes)";
        }

        /// <summary>Pick a welcome line and substitute the name into its {name} slot (fallback "friend").</summary>
        internal string PickWelcome(string name)
        {
            string[] corpus = _welcome;
            if (corpus == null || corpus.Length == 0) return null;
            string who = string.IsNullOrWhiteSpace(name) ? "friend" : name.Trim();
            string line = corpus[_rand.Next(corpus.Length)];
            return line == null ? null : line.Replace("{name}", who);
        }

        private static string CurrentUserName()
        {
            try
            {
                string u = Environment.UserName;
                return string.IsNullOrWhiteSpace(u) ? "friend" : u;
            }
            catch { return "friend"; }
        }

        // Who to greet: the host-published owner name (set by the AI brain when it's on) wins; otherwise fall
        // back to the Windows user name. Keeps the out-of-box welcome, but lets the configured AI name override.
        private static string GreetingName(IHost host)
        {
            try
            {
                string owner = host != null ? host.OwnerName : null;
                if (!string.IsNullOrWhiteSpace(owner)) return owner.Trim();
            }
            catch { }
            return CurrentUserName();
        }

        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.PetSpawned -= OnPetSpawned;
                host.PetLanded -= OnPetLanded;
                host.PetPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_smart != null) { try { _smart.Dispose(); } catch { } _smart = null; }
            _provider = null;
            _host = null;
        }

        /// <summary>
        /// Load the embedded welcome corpus (a JSON array of "{name}"-templated one-liners). Returns an empty
        /// array on any failure so the module simply stays quiet rather than throwing into the host.
        /// </summary>
        private static string[] LoadWelcomeCorpus()
        {
            try
            {
                Assembly asm = typeof(FortunesModule).Assembly;
                string resource = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("welcome.json", StringComparison.OrdinalIgnoreCase)) { resource = n; break; }
                if (resource == null) return Array.Empty<string>();
                using (Stream s = asm.GetManifestResourceStream(resource))
                {
                    if (s == null) return Array.Empty<string>();
                    byte[] buf;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        buf = ms.ToArray();
                    }
                    string[] lines = JsonSerializer.Deserialize<string[]>(new ReadOnlySpan<byte>(buf));
                    return lines ?? Array.Empty<string>();
                }
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Self-test hook (NOT the ABI): number of welcome lines loaded.</summary>
        public int WelcomeCorpusCount() { return _welcome == null ? 0 : _welcome.Length; }
    }
}
