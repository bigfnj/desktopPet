using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --fortunes-selftest: proves the Fortunes module's personalized starter. Loads the real bundled
    /// Fortunes.dll (from &lt;baseDir&gt;\modules\fortunes) through the AssemblyLoadContext loader against a
    /// recording host, then asserts: the module loaded and subscribed to PetSpawned; the embedded welcome
    /// corpus parsed inside the module's load context (WelcomeCorpusCount > 0, via reflection); a raised
    /// PetSpawned speaks a personalized line (contains the current user name, no leftover "{name}" slot); the
    /// welcome fires only once per session; and Shutdown unsubscribed. Skips-pass if the module is absent.
    /// </summary>
    internal static class FortunesModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "fortunes")))
                {
                    sb.AppendLine("SKIP: no bundled fortunes module at " + Path.Combine(modulesRoot, "fortunes"));
                    return Finish(sb, true);
                }

                string expectedName = string.IsNullOrWhiteSpace(Environment.UserName) ? "friend" : Environment.UserName.Trim();
                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    IModule fortunes = FindModule(loader, "fortunes");
                    ok &= Check(sb, "fortunes module reports its id", fortunes != null);
                    ok &= Check(sb, "module subscribed to PetSpawned in Init", host.PetSpawnedHasSubscribers);

                    if (fortunes != null)
                    {
                        MethodInfo count = fortunes.GetType().GetMethod("WelcomeCorpusCount", BindingFlags.Public | BindingFlags.Instance);
                        ok &= Check(sb, "module exposes WelcomeCorpusCount", count != null);
                        if (count != null)
                        {
                            int n = 0;
                            try { n = (int)count.Invoke(fortunes, null); } catch (Exception ex) { sb.AppendLine("  WelcomeCorpusCount threw: " + ex.Message); }
                            sb.AppendLine("  welcome corpus lines = " + n);
                            ok &= Check(sb, "embedded welcome corpus parsed in the module's load context (>0 lines)", n > 0);
                        }
                    }

                    host.RaisePetSpawned(new FakePet(1));
                    string first = host.LastSayAll;
                    sb.AppendLine("  welcome said: " + (first ?? "<null>"));
                    ok &= Check(sb, "PetSpawned speaks a non-empty welcome", !string.IsNullOrEmpty(first));
                    ok &= Check(sb, "welcome is personalized with the user name", first != null && first.IndexOf(expectedName, StringComparison.Ordinal) >= 0);
                    ok &= Check(sb, "welcome substituted the {name} slot", first != null && first.IndexOf("{name}", StringComparison.Ordinal) < 0);

                    host.LastSayAll = null;
                    host.RaisePetSpawned(new FakePet(2));
                    ok &= Check(sb, "welcome fires only once per session", host.LastSayAll == null);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "module unsubscribed from PetSpawned on Shutdown", !host.PetSpawnedHasSubscribers);
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
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

        /// <summary>A headless IHost that records SayAll + PetSpawned subscription state.</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string LastSayAll;

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action<IdleContext> PetIdle;
            public event Action<AnimationInfo> AnimationStarted;
            public event Action HostShutdown;

            public bool PetSpawnedHasSubscribers { get { return PetSpawned != null; } }
            public void RaisePetSpawned(IPet pet) { var h = PetSpawned; if (h != null) h(pet); }
            internal void TouchEvents() { PetPoked?.Invoke(null); PetLanded?.Invoke(null); PetIdle?.Invoke(null); AnimationStarted?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { LastSayAll = text; }
            public void SayAll(string text) { LastSayAll = text; }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new MemStorage(); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new NoopDisposable(); }
            public void AddTrayItems(IEnumerable<TrayItem> items) { }
            public void AddOptionsPane(OptionsPane pane) { }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            private sealed class MemStorage : IModuleStorage { public string DataDirectory { get { return Path.GetTempPath(); } } }
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
