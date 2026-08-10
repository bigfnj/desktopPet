using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using DesktopPet.Ai;
using DesktopPet.Modules;
using Newtonsoft.Json;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// The live <see cref="IHost"/> that loaded modules bind to. Bridges the plugin ABI to the running
    /// app: StartUp raises the lifecycle events (Raise* below); services delegate to StartUp / FormPet /
    /// Program.MyData; contributions are collected here (the tray/options renderer consumes them in the
    /// WPF-shell phase). Everything runs on the UI thread; a throwing module never breaks the host.
    /// </summary>
    internal sealed class PetHost : IHost
    {
        private readonly StartUp _startUp;
        private readonly ConditionalWeakTable<FormPet, PetHandle> _handles = new ConditionalWeakTable<FormPet, PetHandle>();
        private int _nextPetId;
        private readonly List<KeyValuePair<int, Func<bool>>> _dropResponders = new List<KeyValuePair<int, Func<bool>>>();

        public readonly List<TrayItem> TrayItems = new List<TrayItem>();
        public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

        public PetHost(StartUp startUp) { _startUp = startUp; }

        public string HostVersion { get { return Application.ProductVersion; } }
        public bool SpeechEnabled { get { return Program.MyData != null && Program.MyData.GetSpeechEnabled(); } }
        public double Volume { get { return Program.MyData != null ? Program.MyData.GetVolume() : 0.0; } }

        // Preferred user display name, published by a module (the AI brain) and read by others (the fortunes
        // welcome). In-memory + host-owned; "" = none set (consumers fall back to their own default).
        private volatile string _ownerName = "";
        public string OwnerName { get { return _ownerName ?? ""; } }
        public void SetOwnerName(string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length > 64) trimmed = trimmed.Substring(0, 64);   // a display name, not an essay
            _ownerName = trimmed;
        }

        // ---- lifecycle events (raised by StartUp at the existing hook points) ----
        public event Action<IPet> PetSpawned;
        public event Action<PokeInfo> PetPoked;
        public event Action<IPet> PetLanded;
        public event Action<IdleContext> PetIdle;
        public event Action<AnimationInfo> AnimationStarted;
        public event Action HostShutdown;

        internal IPet HandleFor(FormPet pet)
        {
            if (pet == null) return null;
            return _handles.GetValue(pet, p => new PetHandle(p, ++_nextPetId));
        }
        internal void RaisePetSpawned(FormPet pet) { var h = PetSpawned; if (h != null) Safe(() => h(HandleFor(pet))); }
        internal void RaisePetPoked(FormPet pet, int count) { var h = PetPoked; if (h != null) Safe(() => h(new PokeInfo { Pet = HandleFor(pet), PokeCount = count })); }
        internal void RaisePetLanded(FormPet pet) { var h = PetLanded; if (h != null) Safe(() => h(HandleFor(pet))); }
        internal void RaisePetIdle(FormPet pet, ScreenContext ctx) { var h = PetIdle; if (h != null) Safe(() => h(new IdleContext { Pet = HandleFor(pet), Screen = ctx })); }
        internal void RaiseAnimationStarted(FormPet pet, int animationId, byte[] soundData, int soundLoop) { var h = AnimationStarted; if (h != null) Safe(() => h(new AnimationInfo { Pet = HandleFor(pet), AnimationId = animationId, SoundData = soundData, SoundLoop = soundLoop })); }
        internal void RaiseShutdown() { var h = HostShutdown; if (h != null) Safe(() => h()); }

        /// <summary>Offer a drop tick to responders by priority (highest first) until one handles it.</summary>
        internal bool RaiseDropTick()
        {
            foreach (KeyValuePair<int, Func<bool>> r in _dropResponders)
            {
                bool handled = false;
                Func<bool> fn = r.Value;
                Safe(() => { handled = fn(); });
                if (handled) return true;
            }
            return false;
        }
        private static void Safe(Action a) { try { a(); } catch { /* a bad module must not break the host */ } }

        // ---- services ----
        public void Say(IPet pet, string text) { var p = pet as PetHandle; if (p != null && p.Pet != null) p.Pet.Say(text); }
        public void SayAll(string text) { if (_startUp != null) _startUp.SayAll(text); }
        public bool TryPlayAnimation(IPet pet, string name) { var p = pet as PetHandle; return p != null && p.Pet != null && p.Pet.TryPlayAnimation(name); }
        public ScreenContext CaptureScreenContext(IPet pet)
        {
            var p = pet as PetHandle;
            if (p == null || p.Pet == null) return null;
            ScreenCaptureContext ctx = ActiveWindow.CaptureContext(p.Pet.CaptureScreenBounds);
            System.Drawing.Rectangle b = ctx.MonitorBounds;
            return new ScreenContext
            {
                WindowTitle = ctx.ActiveWindowTitle,
                ProcessName = ActiveWindow.ProcessName(),
                MonitorBounds = new PixelRect(b.X, b.Y, b.Width, b.Height),
                WindowUnderPet = p.Pet.WindowUnderPet,
            };
        }
        public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { if (_startUp != null) _startUp.PlayAnimationOnAll(animationCandidates); }
        public IDisposable RegisterHotkey(string combo, Action onPressed)
        {
            // The ABI makes global-hotkey registration a host service (it needs a UI-thread message
            // window + pump), so the host owns the registrar and a module just calls this. Wraps the
            // proven HotkeyListener; a bad/taken combo degrades to a no-op handle (the hotkey simply
            // never fires) rather than throwing into the module. Called on the UI thread.
            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return new Noop();
            HotkeyListener listener = null;
            try
            {
                listener = new HotkeyListener();
                listener.Pressed += delegate { Safe(() => onPressed()); };
                if (!listener.Register(combo))
                {
                    listener.Dispose();
                    return new Noop();
                }
            }
            catch
            {
                if (listener != null) { try { listener.Dispose(); } catch { } }
                return new Noop();
            }
            HotkeyListener registered = listener;
            return new Remover(() => { try { registered.Dispose(); } catch { } });
        }
        public IModuleStorage GetStorage(string moduleId) { return new ModuleStorage(ModuleDataDir(moduleId)); }
        public IModuleSettings GetSettings(string moduleId) { return new ModuleSettings(Path.Combine(ModuleDataDir(moduleId), "settings.json")); }
        public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop)
        {
            var entry = new KeyValuePair<int, Func<bool>>(priority, onDrop);
            _dropResponders.Add(entry);
            _dropResponders.Sort((x, y) => y.Key.CompareTo(x.Key));
            return new Remover(() => _dropResponders.Remove(entry));
        }
        public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
        public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }

        private static string ModuleDataDir(string moduleId)
        {
            string dir = Path.Combine(AppPaths.DataRoot, "modules", SafeId(moduleId));
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
        private static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "_";
            var sb = new StringBuilder();
            foreach (char c in id) sb.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
            return sb.ToString();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Remover : IDisposable
        {
            private Action _a;
            public Remover(Action a) { _a = a; }
            public void Dispose() { Action a = _a; _a = null; if (a != null) a(); }
        }
        private sealed class ModuleStorage : IModuleStorage
        {
            public ModuleStorage(string dir) { DataDirectory = dir; }
            public string DataDirectory { get; private set; }
        }
        private sealed class ModuleSettings : IModuleSettings
        {
            private readonly string _path;
            private readonly Dictionary<string, string> _d;
            public ModuleSettings(string path) { _path = path; _d = Load(path); }
            public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
            public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
            public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
            public void Set(string key, string value) { _d[key] = value ?? ""; }
            public bool Save()
            {
                try { File.WriteAllText(_path, JsonConvert.SerializeObject(_d), new UTF8Encoding(false)); return true; }
                catch { return false; }
            }
            private static Dictionary<string, string> Load(string path)
            {
                try { if (File.Exists(path)) return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>(); }
                catch { }
                return new Dictionary<string, string>();
            }
        }
    }

    /// <summary>Opaque per-pet handle over a FormPet, as seen by modules.</summary>
    internal sealed class PetHandle : IPet
    {
        private readonly FormPet _pet;
        public PetHandle(FormPet pet, int id) { _pet = pet; Id = id; }
        public int Id { get; private set; }
        public bool IsBusy { get { return _pet != null && _pet.IsBusy; } }
        internal FormPet Pet { get { return _pet; } }
    }
}
