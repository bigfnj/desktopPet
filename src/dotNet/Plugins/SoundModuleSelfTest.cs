using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --sound-selftest: proves the Sound module extraction end-to-end. Loads the real bundled Sound.dll
    /// (from &lt;baseDir&gt;\modules\sound) through the AssemblyLoadContext loader against a recording host,
    /// then asserts: the module shipped its NAudio dependency (dll + deps.json in its folder), it loaded
    /// and subscribed to AnimationStarted, NAudio resolved in the MODULE's load context and decoded a real
    /// MP3 (via the module's DecodeProbe, invoked reflectively — the base itself has no NAudio), a raised
    /// AnimationStarted never throws into the host, and Shutdown unsubscribed. Actual audible playback is
    /// not asserted (a CI runner has no audio device; the module swallows device errors by design).
    /// Skips-pass if the module folder is absent.
    /// </summary>
    internal static class SoundModuleSelfTest
    {
        // A structurally-valid minimal MP3 (MPEG frame sync); same fixture the security self-test uses.
        private const string ValidMp3Base64 =
            "/+MYxAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
            "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxDsAAANIAAAAAFVVVVVVVVVVVVVV" +
            "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
            "/+MYxHYAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
            "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxLEAAANIAAAAAFVVVVVVVVVVVVVV" +
            "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV";

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                string soundDir = Path.Combine(modulesRoot, "sound");
                if (!Directory.Exists(soundDir))
                {
                    sb.AppendLine("SKIP: no bundled sound module at " + soundDir);
                    return Finish(sb, true);
                }

                // The module must carry its own codec (proving NAudio left the base).
                ok &= Check(sb, "module ships NAudio.dll", File.Exists(Path.Combine(soundDir, "NAudio.dll")));
                ok &= Check(sb, "module ships a deps.json (for its dependency resolver)", File.Exists(Path.Combine(soundDir, "Sound.deps.json")));

                byte[] validMp3 = Convert.FromBase64String(ValidMp3Base64);
                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    IModule sound = FindModule(loader, "sound");
                    ok &= Check(sb, "sound module reports its id", sound != null);
                    ok &= Check(sb, "module subscribed to AnimationStarted in Init", host.AnimationHasSubscribers);

                    // Prove NAudio resolved in the module's OWN load context and decoded a real MP3.
                    if (sound != null)
                    {
                        MethodInfo probe = sound.GetType().GetMethod("DecodeProbe", BindingFlags.Public | BindingFlags.Static);
                        ok &= Check(sb, "module exposes DecodeProbe", probe != null);
                        if (probe != null)
                        {
                            bool decoded = false;
                            try { decoded = (bool)probe.Invoke(null, new object[] { validMp3 }); }
                            catch (Exception ex) { sb.AppendLine("  DecodeProbe threw: " + ex.GetType().Name + ": " + ex.Message); }
                            ok &= Check(sb, "NAudio decodes a real MP3 inside the module's load context", decoded);
                        }
                    }

                    // Raising the event must never throw into the host, whatever the payload (device errors
                    // and undecodable/empty bytes are swallowed by the module).
                    bool raiseThrew = false;
                    try
                    {
                        host.RaiseAnimationStarted(new AnimationInfo { Pet = null, AnimationId = 1, SoundData = validMp3, SoundLoop = 0 });
                        host.RaiseAnimationStarted(new AnimationInfo { Pet = null, AnimationId = 2, SoundData = null, SoundLoop = 0 });
                        host.RaiseAnimationStarted(new AnimationInfo { Pet = null, AnimationId = 3, SoundData = new byte[0], SoundLoop = 0 });
                    }
                    catch (Exception ex) { raiseThrew = true; sb.AppendLine("  raise threw: " + ex.GetType().Name + ": " + ex.Message); }
                    ok &= Check(sb, "raising AnimationStarted never throws into the host", !raiseThrew);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "module unsubscribed from AnimationStarted on Shutdown", !host.AnimationHasSubscribers);
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
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-sound-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        /// <summary>A headless IHost that records subscription state and can raise AnimationStarted.</summary>
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

            public bool AnimationHasSubscribers { get { return AnimationStarted != null; } }
            public void RaiseAnimationStarted(AnimationInfo info) { var h = AnimationStarted; if (h != null) h(info); }
            // Referencing the other events keeps the compiler from warning them unused.
            internal void TouchEvents() { PetSpawned?.Invoke(null); PetPoked?.Invoke(null); PetLanded?.Invoke(null); PetIdle?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { }
            public void SayAll(string text) { }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
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
