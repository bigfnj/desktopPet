using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopPet.Modules;

namespace DesktopPet.ModuleKit.Testing
{
    /// <summary>
    /// A headless <see cref="IHost"/> for module self-tests: it records what a module contributed, lets the
    /// test raise the pet lifecycle events, and stands in for the services a module calls — with no window,
    /// no pet, and no network.
    ///
    /// Every module self-test in this repo was writing its own; this is that host, once. Typical use:
    /// <code>
    /// var host = new RecordingHost();
    /// module.Init(host);
    /// // assert what Init contributed
    /// host.TrayItems.Count == 1;
    /// // then drive behaviour
    /// host.RaisePetPoked(new PokeInfo());
    /// host.SaidLines.Count == 1;
    /// </code>
    ///
    /// <see cref="HostVersion"/> defaults to a very high sentinel so the loader's MinHostVersion gate stays
    /// quiet and a test exercises the module rather than the gate; set it to assert refusal behaviour.
    /// </summary>
    public class RecordingHost : IHost
    {
        // ---- what the module contributed ----
        public List<TrayItem> TrayItems { get; private set; }
        public List<OptionsPane> OptionsPanes { get; private set; }
        public List<string> SaidLines { get; private set; }
        public List<string> PlayedAnimations { get; private set; }
        public List<string> OpenedLinks { get; private set; }
        public List<Func<bool>> DropResponders { get; private set; }
        public List<Func<bool>> PokeResponders { get; private set; }
        public List<string> RegisteredHotkeys { get; private set; }

        // ---- what the host hands back (set these to steer a test) ----
        public string HostVersion { get; set; }
        public bool SpeechEnabled { get; set; }
        public double Volume { get; set; }
        public string OwnerName { get; set; }
        public ScreenContext ScreenContextValue { get; set; }
        public IPetManager PetManager { get; set; }
        public IReadOnlyList<string> PickedFiles { get; set; }
        public Dictionary<string, List<CatalogItem>> CatalogItems { get; private set; }
        public Dictionary<string, byte[]> CatalogPayloads { get; private set; }

        private readonly Dictionary<string, FakeModuleSettings> _settings =
            new Dictionary<string, FakeModuleSettings>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IModuleStorage> _storage =
            new Dictionary<string, IModuleStorage>(StringComparer.OrdinalIgnoreCase);

        public RecordingHost()
        {
            TrayItems = new List<TrayItem>();
            OptionsPanes = new List<OptionsPane>();
            SaidLines = new List<string>();
            PlayedAnimations = new List<string>();
            OpenedLinks = new List<string>();
            DropResponders = new List<Func<bool>>();
            PokeResponders = new List<Func<bool>>();
            RegisteredHotkeys = new List<string>();
            CatalogItems = new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
            CatalogPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            // High enough that no realistic MinHostVersion refuses the module under test.
            HostVersion = "9999.0.0";
            SpeechEnabled = true;
            Volume = 0.5;
            OwnerName = "";
            PetManager = new DenyingPetManager();
            PickedFiles = new List<string>();
            ScreenContextValue = new ScreenContext
            {
                WindowTitle = "",
                ProcessName = "",
                MonitorBounds = new PixelRect(0, 0, 1920, 1080),
            };
        }

        /// <summary>Give this module a real temp data directory. The caller owns disposal.</summary>
        public void UseStorage(string moduleId, IModuleStorage storage)
        {
            if (moduleId == null) return;
            _storage[moduleId] = storage;
        }

        /// <summary>The settings this module has been reading and writing, for assertions.</summary>
        public FakeModuleSettings SettingsFor(string moduleId)
        {
            FakeModuleSettings settings;
            string key = moduleId ?? "";
            if (!_settings.TryGetValue(key, out settings))
                _settings[key] = settings = new FakeModuleSettings();
            return settings;
        }

        // ---- events, and the way a test raises them ----
        public event Action<IPet> PetSpawned;
        public event Action<PokeInfo> PetPoked;
        public event Action<IPet> PetLanded;
        public event Action HostShutdown;

        public void RaisePetSpawned(IPet pet) { Action<IPet> h = PetSpawned; if (h != null) h(pet); }
        public void RaisePetPoked(PokeInfo poke) { Action<PokeInfo> h = PetPoked; if (h != null) h(poke); }
        public void RaisePetLanded(IPet pet) { Action<IPet> h = PetLanded; if (h != null) h(pet); }
        public void RaiseHostShutdown() { Action h = HostShutdown; if (h != null) h(); }

        /// <summary>Run the registered drop responders in registration order, as the host arbitrates them;
        /// returns true once one claims the drop.</summary>
        public bool RaiseDrop()
        {
            foreach (Func<bool> responder in DropResponders)
                if (responder != null && responder()) return true;
            return false;
        }

        /// <summary>Run the registered poke responders in registration order; true once one speaks.</summary>
        public bool RaisePokeResponders()
        {
            foreach (Func<bool> responder in PokeResponders)
                if (responder != null && responder()) return true;
            return false;
        }

        // ---- IHost services ----
        public void SetOwnerName(string name) { OwnerName = name ?? ""; }
        public void Say(IPet pet, string text) { SaidLines.Add(text ?? ""); }
        public void SayAll(string text) { SaidLines.Add(text ?? ""); }

        public bool TryPlayAnimation(IPet pet, string animationName)
        {
            PlayedAnimations.Add(animationName ?? "");
            return true;
        }

        public void PlayAnimationAll(IReadOnlyList<string> animationCandidates)
        {
            if (animationCandidates == null) return;
            foreach (string candidate in animationCandidates) PlayedAnimations.Add(candidate ?? "");
        }

        public ScreenContext CaptureScreenContext(IPet pet) { return ScreenContextValue; }

        public IDisposable RegisterHotkey(string combo, Action onPressed)
        {
            RegisteredHotkeys.Add(combo ?? "");
            return new NoopDisposable();
        }

        public IModuleStorage GetStorage(string moduleId)
        {
            IModuleStorage storage;
            return _storage.TryGetValue(moduleId ?? "", out storage) ? storage : null;
        }

        public IModuleSettings GetSettings(string moduleId) { return SettingsFor(moduleId); }

        public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop)
        {
            DropResponders.Add(onDrop);
            return new NoopDisposable();
        }

        public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke)
        {
            PokeResponders.Add(onPoke);
            return new NoopDisposable();
        }

        public Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
        {
            List<CatalogItem> items;
            if (!CatalogItems.TryGetValue(kind ?? "", out items)) items = new List<CatalogItem>();
            return Task.FromResult((IReadOnlyList<CatalogItem>)items);
        }

        public Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
        {
            byte[] payload;
            if (!CatalogPayloads.TryGetValue((kind ?? "") + "/" + (id ?? ""), out payload)) payload = new byte[0];
            return Task.FromResult(payload);
        }

        public IPetManager GetPetManager(string moduleId) { return PetManager; }

        public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions)
        {
            return PickedFiles ?? new List<string>();
        }

        public bool OpenLink(string moduleId, string httpsUrl)
        {
            OpenedLinks.Add(httpsUrl ?? "");
            return true;
        }

        public void AddTrayItems(IEnumerable<TrayItem> items)
        {
            if (items != null) TrayItems.AddRange(items);
        }

        public void AddOptionsPane(OptionsPane pane)
        {
            if (pane != null) OptionsPanes.Add(pane);
        }

        /// <summary>
        /// Never called. It exists so the compiler sees every declared event as USED: an event with no
        /// raiser is CS0067, and this repo builds with warnings-as-errors, so a fake host that only
        /// declares the interface's events fails to compile without something like this.
        /// </summary>
        internal void TouchEvents()
        {
            RaisePetSpawned(null);
            RaisePetPoked(null);
            RaisePetLanded(null);
            RaiseHostShutdown();
        }
    }
}
