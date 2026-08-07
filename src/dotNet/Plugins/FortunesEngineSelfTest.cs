using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --fortunes-engine-selftest: proves the relocated fortune ENGINE (S3c-1) works inside the Fortunes
    /// module's own load context. Loads the real bundled Fortunes.dll through the AssemblyLoadContext
    /// loader, then reflectively invokes the module's public static DesktopPet.FortunesModule.FortuneEngineProbe.Run
    /// (deterministic filter/pick over injected entries + the engine's own FilterSelfTest — dedup /
    /// classifier-parity / parser / custom ingestion / importer). The base itself keeps no reference to the
    /// module engine. Skips-pass if the module is absent.
    /// </summary>
    internal static class FortunesEngineSelfTest
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

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    IModule fortunes = FindModule(loader, "fortunes");
                    ok &= Check(sb, "fortunes module reports its id", fortunes != null);

                    if (fortunes != null)
                    {
                        Type probe = fortunes.GetType().Assembly.GetType("DesktopPet.FortunesModule.FortuneEngineProbe");
                        ok &= Check(sb, "module exposes FortuneEngineProbe", probe != null);
                        MethodInfo run = probe != null ? probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) : null;
                        ok &= Check(sb, "FortuneEngineProbe exposes Run", run != null);
                        if (run != null)
                        {
                            var args = new object[] { null };
                            bool engineOk = false;
                            try { engineOk = (bool)run.Invoke(null, args); }
                            catch (Exception ex) { sb.AppendLine("  FortuneEngineProbe.Run threw: " + ex.GetType().Name + ": " + ex.Message); }
                            string detail = args[0] as string;
                            if (!string.IsNullOrEmpty(detail))
                                foreach (string line in detail.Replace("\r", "").Split('\n'))
                                    if (line.Length > 0) sb.AppendLine("    " + line);
                            ok &= Check(sb, "engine self-test passed inside the module's load context", engineOk);
                        }
                    }
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
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-fortunes-engine-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        /// <summary>Minimal headless IHost so ModuleHost can load the module (the engine probe is static and
        /// does not depend on Init, but LoadFrom calls Init(host)).</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action<IdleContext> PetIdle;
            public event Action<AnimationInfo> AnimationStarted;
            public event Action HostShutdown;
            internal void TouchEvents() { PetSpawned?.Invoke(null); PetPoked?.Invoke(null); PetLanded?.Invoke(null); PetIdle?.Invoke(null); AnimationStarted?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { }
            public void SayAll(string text) { }
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
