using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DesktopPet.Modules;

namespace DesktopPet.FortunesModule
{
    /// <summary>
    /// The Fortunes module (S3). It ships the fortune ENGINE + a personalized starter corpus, and bundles
    /// NO real fortune content: a fresh install speaks the built-in sheep-themed welcome lines (filled with
    /// the Windows username) until the user adds a pack (see BACKLOG.md).
    ///
    /// This increment delivers the personalized starter: on the first pet spawn of the session it picks a
    /// welcome line and substitutes the current Windows user's name — the "landing quote" tailored to who's
    /// logged in. The heavier engine relocation (FortuneProvider / SmartFortunes / Embedder, the land/poke/
    /// drop fortune loop, and moving those OUT of the base) lands in the next S3 increment.
    /// </summary>
    public sealed class FortunesModule : IModule
    {
        private IHost _host;
        private string[] _welcome;
        private readonly Random _rand = new Random();
        private bool _welcomed;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "0.2.0",   // engine relocation raises this to 1.0.0
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _welcome = LoadWelcomeCorpus();
            host.PetSpawned += OnPetSpawned;
        }

        // The first pet of the session gets a personalized greeting; later spawns don't re-welcome. Fires on
        // the host UI thread, so SayAll is safe to call synchronously here.
        private void OnPetSpawned(IPet pet)
        {
            if (_welcomed) return;
            _welcomed = true;
            IHost host = _host;
            if (host == null) return;
            string line = PickWelcome(CurrentUserName());
            if (!string.IsNullOrEmpty(line)) host.SayAll(line);
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
            if (_host != null) { _host.PetSpawned -= OnPetSpawned; _host = null; }
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

        /// <summary>
        /// Self-test hook (NOT part of the plugin ABI): the number of welcome lines loaded, so --fortunes-selftest
        /// can prove the embedded corpus parsed inside the module's load context.
        /// </summary>
        public int WelcomeCorpusCount() { return _welcome == null ? 0 : _welcome.Length; }
    }
}
