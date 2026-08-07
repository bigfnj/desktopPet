using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

            // Point the engine at the module's own storage, build the pool from the user's packs (empty by
            // default = silent), and warm the smart picker in the background when enabled. All best-effort:
            // a failure here leaves the module welcome-only rather than breaking the host.
            try
            {
                IModuleStorage storage = host.GetStorage("fortunes");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                    FortunePaths.SetRoot(storage.DataDirectory);

                FortuneSettings settings = LoadFortuneSettings(host);
                _provider = new FortuneProvider(settings);
                if (settings.SmartFortunes)
                {
                    _smart = new SmartFortunes();
                    _smart.Warm(_provider.PoolEntries());
                }
            }
            catch { _provider = null; _smart = null; }

            host.PetSpawned += OnPetSpawned;
            host.PetLanded += OnPetLanded;
            host.PetPoked += OnPetPoked;
            _dropResponder = host.RegisterDropResponder(0, OnDrop);   // lowest priority; the AI brain (S4) will outrank
        }

        // The first pet of the session gets a personalized greeting; later spawns don't re-welcome.
        private void OnPetSpawned(IPet pet)
        {
            _lastPet = pet ?? _lastPet;
            if (_welcomed) return;
            _welcomed = true;
            IHost host = _host;
            if (host == null) return;
            string line = PickWelcome(CurrentUserName());
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
                    // Disabled source/genre lists get a real UI in S5; defaults (all enabled) until then.
                }
            }
            catch { }
            return s;
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
