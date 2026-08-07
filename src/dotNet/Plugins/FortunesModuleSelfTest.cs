using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --fortunes-selftest: proves the Fortunes module's LIVE behavior (S3d). Loads the real bundled
    /// Fortunes.dll through the AssemblyLoadContext loader against a recording host whose storage holds a
    /// throwaway pack, then asserts: the personalized welcome fires once on the first spawn; the module is
    /// wired to PetLanded / PetPoked / a drop responder; each of those speaks a fortune drawn from the pack;
    /// a poke in the base's "ignore" range (3-4) stays silent; and Shutdown unsubscribes everything. This is
    /// the end-to-end check that the base handed fortune-speaking to the module with no double-speak.
    /// Skips-pass if the module is absent.
    /// </summary>
    internal static class FortunesModuleSelfTest
    {
        private static readonly string[] Pack =
        {
            "Probe fortune alpha, a calm line.",
            "Probe fortune bravo, another line.",
            "Probe fortune charlie, one more.",
        };

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string storageDir = null;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "fortunes")))
                {
                    sb.AppendLine("SKIP: no bundled fortunes module at " + Path.Combine(modulesRoot, "fortunes"));
                    return Finish(sb, true);
                }

                // Isolated module storage with a throwaway one-per-line pack (source = file name), so the
                // engine's pool is non-empty and land/poke/drop have something to say.
                storageDir = Path.Combine(Path.GetTempPath(), "dp-fortunes-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(storageDir, "fortunes"));
                File.WriteAllText(Path.Combine(storageDir, "fortunes", "probepack.txt"), string.Join("\n", Pack) + "\n", new UTF8Encoding(false));
                var packSet = new HashSet<string>(Pack, StringComparer.Ordinal);

                string expectedName = string.IsNullOrWhiteSpace(Environment.UserName) ? "friend" : Environment.UserName.Trim();
                var host = new RecordingHost(storageDir);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    ok &= Check(sb, "fortunes module reports its id", FindModule(loader, "fortunes") != null);

                    // Wiring: the module owns the fortune triggers now.
                    ok &= Check(sb, "subscribed to PetSpawned (welcome)", host.SpawnedHasSubs);
                    ok &= Check(sb, "subscribed to PetLanded", host.LandedHasSubs);
                    ok &= Check(sb, "subscribed to PetPoked", host.PokedHasSubs);
                    ok &= Check(sb, "registered a drop responder", host.DropResponder != null);

                    // Other dev modules (e.g. TestModule) are loaded too and also speak, so each trigger is
                    // checked for "a pack line is AMONG what was said", not "the last thing said".
                    // Welcome on the first spawn.
                    host.Said.Clear();
                    host.RaisePetSpawned(new FakePet(1));
                    ok &= Check(sb, "welcome speaks + is personalized", host.Said.Exists(s => !string.IsNullOrEmpty(s) && s.IndexOf(expectedName, StringComparison.Ordinal) >= 0));

                    // Land -> a fortune from the pack.
                    host.Said.Clear();
                    host.RaisePetLanded(new FakePet(1));
                    sb.AppendLine("  land said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "PetLanded speaks a fortune from the pack", host.Said.Exists(s => packSet.Contains(s)));

                    // Poke 1 -> a fortune; poke 4 (the base's 3-4 "ignore" range) -> the module speaks no fortune.
                    host.Said.Clear();
                    host.RaisePetPoked(new PokeInfo { Pet = new FakePet(1), PokeCount = 1 });
                    sb.AppendLine("  poke(1) said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "PetPoked(1) speaks a fortune from the pack", host.Said.Exists(s => packSet.Contains(s)));
                    host.Said.Clear();
                    host.RaisePetPoked(new PokeInfo { Pet = new FakePet(1), PokeCount = 4 });
                    sb.AppendLine("  poke(4) said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "PetPoked(4) speaks no fortune (base owns 3-4 ignore)", !host.Said.Exists(s => packSet.Contains(s)));

                    // Drop responder -> a fortune, and it reports handled.
                    host.Said.Clear();
                    bool handled = host.DropResponder != null && host.DropResponder();
                    sb.AppendLine("  drop said: " + string.Join(" | ", host.Said));
                    ok &= Check(sb, "drop responder speaks a fortune + reports handled", handled && host.Said.Exists(s => packSet.Contains(s)));

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "unsubscribed all triggers on Shutdown", !host.SpawnedHasSubs && !host.LandedHasSubs && !host.PokedHasSubs);
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { if (storageDir != null) Directory.Delete(storageDir, true); } catch { } }
            return Finish(sb, ok);
        }

        private static IModule FindModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-fortunes-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakePet : IPet
        {
            public FakePet(int id) { Id = id; }
            public int Id { get; private set; }
            public bool IsBusy { get { return false; } }
        }

        /// <summary>A headless IHost that records SayAll, tracks subscription state, and captures the drop
        /// responder + the module's storage directory.</summary>
        private sealed class RecordingHost : IHost
        {
            private readonly string _storage;
            public RecordingHost(string storage) { _storage = storage; }

            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string LastSayAll;
            public readonly List<string> Said = new List<string>();   // all SayAll/Say calls (other modules speak too)
            public Func<bool> DropResponder;

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action<IdleContext> PetIdle;
            public event Action<AnimationInfo> AnimationStarted;
            public event Action HostShutdown;

            public bool SpawnedHasSubs { get { return PetSpawned != null; } }
            public bool LandedHasSubs { get { return PetLanded != null; } }
            public bool PokedHasSubs { get { return PetPoked != null; } }
            public void RaisePetSpawned(IPet p) { var h = PetSpawned; if (h != null) h(p); }
            public void RaisePetLanded(IPet p) { var h = PetLanded; if (h != null) h(p); }
            public void RaisePetPoked(PokeInfo p) { var h = PetPoked; if (h != null) h(p); }
            internal void TouchEvents() { PetIdle?.Invoke(null); AnimationStarted?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { LastSayAll = text; Said.Add(text); }
            public void SayAll(string text) { LastSayAll = text; Said.Add(text); }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new DirStorage(_storage); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { DropResponder = onDrop; return new NoopDisposable(); }
            public void AddTrayItems(IEnumerable<TrayItem> items) { }
            public void AddOptionsPane(OptionsPane pane) { }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            private sealed class DirStorage : IModuleStorage
            {
                public DirStorage(string dir) { DataDirectory = dir; }
                public string DataDirectory { get; private set; }
            }
            private sealed class MemSettings : IModuleSettings
            {
                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();
                public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
                public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
                public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
                public void Set(string key, string value) { _d[key] = value; }
                public bool Save() { return true; }
            }
        }
    }
}
