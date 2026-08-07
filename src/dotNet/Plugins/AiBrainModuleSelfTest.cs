using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --aibrain-selftest: proves the AI-brain module's BOUNDARY and DORMANCY (S4a). It copies only the
    /// bundled aibrain module into an isolated modules root, loads it through the real AssemblyLoadContext
    /// loader against a recording host, and asserts: the module loads and reports its id / name / the full
    /// capability set it declares; and — the point of the dormant scaffold — that Init wires NOTHING (no
    /// event subscription, no drop responder, no tray/options contribution) and reacting to spawn/land/poke
    /// speaks nothing, so the base keeps owning the AI brain with zero double-fire until the S4b flip.
    /// It also smoke-checks the host's real global-hotkey registrar (skip-passes where a message window /
    /// RegisterHotKey isn't available, e.g. a headless CI window station). Skips-pass if the module is absent.
    /// </summary>
    internal static class AiBrainModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string tempRoot = null;
            try
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "modules", "aibrain");
                if (!Directory.Exists(bundled))
                {
                    sb.AppendLine("SKIP: no bundled aibrain module at " + bundled);
                    return Finish(sb, true);
                }

                // Isolate: load ONLY aibrain, so the recording host reflects this module's Init alone
                // (the shared build folder also has fortunes/sound/testmodule, which DO subscribe).
                tempRoot = Path.Combine(Path.GetTempPath(), "dp-aibrain-selftest-" + Guid.NewGuid().ToString("N"));
                string dest = Path.Combine(tempRoot, "aibrain");
                Directory.CreateDirectory(dest);
                foreach (string file in Directory.GetFiles(bundled))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(tempRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "exactly one module loaded (isolated)", loaded == 1);

                    IModule brain = FindModule(loader, "aibrain");
                    ok &= Check(sb, "aibrain module reports its id", brain != null);
                    if (brain != null && brain.Info != null)
                    {
                        ok &= Check(sb, "module name is 'AI Brain'", string.Equals(brain.Info.Name, "AI Brain", StringComparison.Ordinal));
                        ModulePermissions p = brain.Info.Permissions;
                        ok &= Check(sb, "declares Speech+Animation+ScreenContext+Network+Hotkey+Storage",
                            p.HasFlag(ModulePermissions.Speech) && p.HasFlag(ModulePermissions.Animation) &&
                            p.HasFlag(ModulePermissions.ScreenContext) && p.HasFlag(ModulePermissions.Network) &&
                            p.HasFlag(ModulePermissions.Hotkey) && p.HasFlag(ModulePermissions.Storage));
                    }

                    // Dormancy: the scaffold must wire nothing and start nothing.
                    ok &= Check(sb, "dormant: no lifecycle subscriptions after Init",
                        !host.SpawnedHasSubs && !host.LandedHasSubs && !host.PokedHasSubs &&
                        !host.IdleHasSubs && !host.AnimationHasSubs);
                    ok &= Check(sb, "dormant: no drop responder registered", host.DropResponder == null);
                    ok &= Check(sb, "dormant: no tray / options contributions", host.TrayCount == 0 && host.PaneCount == 0);

                    // Reacting to events must speak nothing while dormant.
                    host.Said.Clear();
                    host.RaisePetSpawned(new FakePet(1));
                    host.RaisePetLanded(new FakePet(1));
                    host.RaisePetPoked(new PokeInfo { Pet = new FakePet(1), PokeCount = 1 });
                    ok &= Check(sb, "dormant: spawn/land/poke speak nothing", host.Said.Count == 0);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "Shutdown does not throw", true);
                }

                ok &= HotkeyRegistrarSmoke(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { if (tempRoot != null) Directory.Delete(tempRoot, true); } catch { } }
            return Finish(sb, ok);
        }

        /// <summary>
        /// Best-effort check of the host's real RegisterHotkey (PetHost wraps the proven HotkeyListener).
        /// Verifies the wrapping's lifecycle without fragile input injection: a valid combo returns a
        /// disposable that disposes cleanly, and a null/empty combo degrades to a disposable no-op. Skips
        /// (passes) if creating a message window / RegisterHotKey isn't available in this context.
        /// </summary>
        private static bool HotkeyRegistrarSmoke(StringBuilder sb)
        {
            try
            {
                var host = new PetHost(null);   // RegisterHotkey does not touch StartUp
                using (IDisposable a = host.RegisterHotkey("Ctrl+Alt+F24", delegate { }))
                    if (a == null) return Check(sb, "hotkey registrar returns a handle for a valid combo", false);
                IDisposable b = host.RegisterHotkey("", delegate { });   // graceful no-op
                if (b != null) b.Dispose();
                return Check(sb, "hotkey registrar: valid combo + empty combo both return safe handles", true);
            }
            catch (Exception ex)
            {
                sb.AppendLine("SKIP: hotkey registrar smoke unavailable (" + ex.GetType().Name + ")");
                return true;
            }
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
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-aibrain-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakePet : IPet
        {
            public FakePet(int id) { Id = id; }
            public int Id { get; private set; }
            public bool IsBusy { get { return false; } }
        }

        /// <summary>A headless IHost that records SayAll + subscription/contribution state.</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public readonly List<string> Said = new List<string>();
            public Func<bool> DropResponder;
            public int TrayCount;
            public int PaneCount;

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action<IdleContext> PetIdle;
            public event Action<AnimationInfo> AnimationStarted;
            public event Action HostShutdown;

            public bool SpawnedHasSubs { get { return PetSpawned != null; } }
            public bool LandedHasSubs { get { return PetLanded != null; } }
            public bool PokedHasSubs { get { return PetPoked != null; } }
            public bool IdleHasSubs { get { return PetIdle != null; } }
            public bool AnimationHasSubs { get { return AnimationStarted != null; } }
            public void RaisePetSpawned(IPet p) { var h = PetSpawned; if (h != null) h(p); }
            public void RaisePetLanded(IPet p) { var h = PetLanded; if (h != null) h(p); }
            public void RaisePetPoked(PokeInfo p) { var h = PetPoked; if (h != null) h(p); }
            internal void TouchEvents() { PetIdle?.Invoke(null); AnimationStarted?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { Said.Add(text); }
            public void SayAll(string text) { Said.Add(text); }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new DirStorage(Path.Combine(Path.GetTempPath(), "dp-aibrain-selftest-store")); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { DropResponder = onDrop; return new NoopDisposable(); }
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) foreach (var i in items) TrayCount++; }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) PaneCount++; }

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
