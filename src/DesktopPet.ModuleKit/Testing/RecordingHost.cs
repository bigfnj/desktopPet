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
        /// <summary>Everything the module logged, as "&lt;moduleId&gt;: &lt;message&gt;" — assert on it instead of
        /// making the pet speak diagnostics.</summary>
        public List<string> LoggedLines { get; private set; }
        public List<Func<bool>> DropResponders { get; private set; }
        public List<Func<bool>> PokeResponders { get; private set; }
        /// <summary>Pet-aware responders (host 1.5.0+), kept separately from the legacy pair so a test can see
        /// which style the module registered. RaiseDrop/RaisePokeResponders run both.</summary>
        public List<Func<IPet, bool>> PetDropResponders { get; private set; }
        public List<Func<IPet, bool>> PetPokeResponders { get; private set; }
        /// <summary>Every targeted line and the pet it went to. This is how you assert a reaction reached ONE
        /// pet rather than all of them.</summary>
        public List<KeyValuePair<IPet, string>> SaidToPets { get; private set; }
        /// <summary>Lines sent via SayAll. Should be rare: announcements to the user, not pet reactions.</summary>
        public List<string> BroadcastLines { get; private set; }
        /// <summary>Backs IsPetAlive. Null means every non-null pet is alive.</summary>
        public Func<IPet, bool> PetAlivePredicate { get; set; }
        /// <summary>Audio buffers your module handed to PlaySound.</summary>
        public List<byte[]> PlayedSounds { get; private set; }
        /// <summary>Module ids passed to StopSound.</summary>
        public List<string> StoppedSoundOwners { get; private set; }
        /// <summary>What PlaySound returns; set false to drive the "nothing will be heard" branch.</summary>
        public bool PlaySoundResult { get; set; }
        public List<Func<SpeechRequest, bool>> SpeechResponders { get; private set; }
        /// <summary>Lines a responder handed back through SpeechRequest.ShowBubble.</summary>
        public List<string> ShownBubbles { get; private set; }
        public List<string> RegisteredHotkeys { get; private set; }

        // ---- what the host hands back (set these to steer a test) ----
        public string HostVersion { get; set; }
        public bool SpeechEnabled { get; set; }
        public double Volume { get; set; }
        public string OwnerName { get; set; }
        /// <summary>Set this to assert your window themes both ways without touching the machine's OS setting.</summary>
        public bool IsDarkTheme { get; set; }
        public ScreenContext ScreenContextValue { get; set; }
        public IPetManager PetManager { get; set; }
        public IReadOnlyList<string> PickedFiles { get; set; }
        public Dictionary<string, List<CatalogItem>> CatalogItems { get; private set; }
        public Dictionary<string, byte[]> CatalogPayloads { get; private set; }
        /// <summary>Context values a module published via PublishContext, keyed by key, for assertions.</summary>
        public Dictionary<string, string> PublishedContext { get; private set; }

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
            LoggedLines = new List<string>();
            DropResponders = new List<Func<bool>>();
            PokeResponders = new List<Func<bool>>();
            PetDropResponders = new List<Func<IPet, bool>>();
            PetPokeResponders = new List<Func<IPet, bool>>();
            SaidToPets = new List<KeyValuePair<IPet, string>>();
            BroadcastLines = new List<string>();
            PlayedSounds = new List<byte[]>();
            StoppedSoundOwners = new List<string>();
            SpeechResponders = new List<Func<SpeechRequest, bool>>();
            ShownBubbles = new List<string>();
            PlaySoundResult = true;
            RegisteredHotkeys = new List<string>();
            CatalogItems = new Dictionary<string, List<CatalogItem>>(StringComparer.OrdinalIgnoreCase);
            CatalogPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            PublishedContext = new Dictionary<string, string>(StringComparer.Ordinal);

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
        /// returns true once one claims the drop. Runs BOTH registration styles, so a test does not have to
        /// know which one the module chose.</summary>
        public bool RaiseDrop() { return RaiseDrop(null); }

        /// <summary>As <see cref="RaiseDrop()"/>, but naming the pet the drop belongs to.</summary>
        public bool RaiseDrop(IPet pet)
        {
            foreach (Func<bool> responder in DropResponders)
                if (responder != null && responder()) return true;
            foreach (Func<IPet, bool> responder in PetDropResponders)
                if (responder != null && responder(pet)) return true;
            return false;
        }

        /// <summary>Run the registered poke responders in registration order; true once one speaks.</summary>
        public bool RaisePokeResponders() { return RaisePokeResponders(null); }

        /// <summary>As <see cref="RaisePokeResponders()"/>, but naming the pet that was poked.</summary>
        public bool RaisePokeResponders(IPet pet)
        {
            foreach (Func<bool> responder in PokeResponders)
                if (responder != null && responder()) return true;
            foreach (Func<IPet, bool> responder in PetPokeResponders)
                if (responder != null && responder(pet)) return true;
            return false;
        }

        // ---- IHost services ----
        public void SetOwnerName(string name) { OwnerName = name ?? ""; }

        /// <summary>
        /// Every line, targeted or broadcast, in order. Kept as the union so existing tests keep working.
        /// To assert that a line went to ONE pet rather than to all of them, use <see cref="SaidToPets"/> and
        /// <see cref="BroadcastLines"/> -- Say and SayAll both wrote only here before, which made the
        /// difference between routing and broadcasting impossible to test at all.
        /// </summary>
        public void Say(IPet pet, string text)
        {
            SaidLines.Add(text ?? "");
            SaidToPets.Add(new KeyValuePair<IPet, string>(pet, text ?? ""));
        }

        public void SayAll(string text)
        {
            SaidLines.Add(text ?? "");
            BroadcastLines.Add(text ?? "");
        }

        // Styled overloads record identically to the plain ones (the style is a render-only concern the fake
        // does not paint); tests that assert on spoken text keep working unchanged.
        public void Say(IPet pet, string text, DesktopPet.Modules.SpeechStyle style) { Say(pet, text); }
        public void SayAll(string text, DesktopPet.Modules.SpeechStyle style) { SayAll(text); }

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

        public IDisposable RegisterPetDropResponder(int priority, Func<IPet, bool> onDrop)
        {
            PetDropResponders.Add(onDrop);
            return new NoopDisposable();
        }

        public IDisposable RegisterPetPokeResponder(string moduleId, int priority, Func<IPet, bool> onPoke)
        {
            PetPokeResponders.Add(onPoke);
            return new NoopDisposable();
        }

        /// <summary>Answers <see cref="PetAlivePredicate"/>; alive by default. Set the predicate to prove your
        /// module drops work whose pet went away instead of redirecting it to a different pet.</summary>
        public bool IsPetAlive(IPet pet)
        {
            if (pet == null) return false;
            Func<IPet, bool> predicate = PetAlivePredicate;
            return predicate == null || predicate(pet);
        }

        /// <summary>Answers <see cref="IHost.IsFullscreenActive"/>; false by default, because a test machine
        /// is not running a game. Prefer <see cref="RaiseFullscreenChanged"/> to flip it, so subscribers see
        /// the transition the way they would in the app.</summary>
        public bool IsFullscreenActive { get; set; }

        /// <summary>Raised by <see cref="RaiseFullscreenChanged"/>.</summary>
        public event Action<bool> FullscreenChanged;

        /// <summary>
        /// Flip the fullscreen state and notify subscribers, exactly as the host does when a game starts or
        /// exits. Use this to prove your module RELEASES whatever it is holding when a game appears -- the
        /// interesting behaviour is the transition, not the steady state.
        /// </summary>
        public void RaiseFullscreenChanged(bool active)
        {
            IsFullscreenActive = active;
            Action<bool> handler = FullscreenChanged;
            if (handler != null) handler(active);
        }

        /// <summary>Records the audio your module tried to play. Set <see cref="PlaySoundResult"/> to false to
        /// exercise the refused path -- no device, no permission, muted -- which is the branch that decides
        /// whether your module falls back to a bubble.</summary>
        public bool PlaySound(string moduleId, byte[] audio, double volume)
        {
            PlayedSounds.Add(audio ?? new byte[0]);
            return PlaySoundResult;
        }

        public bool StopSound(string moduleId)
        {
            StoppedSoundOwners.Add(moduleId ?? "");
            return true;
        }

        public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech)
        {
            SpeechResponders.Add(onSpeech);
            return new NoopDisposable();
        }

        /// <summary>Offer an utterance to the registered speech responders, as the host does. Returns true
        /// when one claimed it AND asked to suppress the bubble. Any ShowBubble call is recorded in
        /// <see cref="ShownBubbles"/>, so you can assert the no-silent-loss path.</summary>
        public bool RaiseSpeechRequest(string text, IPet pet)
        {
            var request = new SpeechRequest
            {
                Text = text,
                Pet = pet,
                ShowBubble = seconds => ShownBubbles.Add(text ?? ""),
            };
            foreach (Func<SpeechRequest, bool> responder in SpeechResponders)
                if (responder != null && responder(request)) return request.SuppressBubble;
            return false;
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

        public void Log(string moduleId, string message)
        {
            LoggedLines.Add((moduleId ?? "") + ": " + (message ?? ""));
        }

        public event Action<string> ContextChanged;

        public void PublishContext(string moduleId, string key, string valueJson)
        {
            PublishedContext[key ?? ""] = valueJson ?? "";
            Action<string> handler = ContextChanged;
            if (handler != null) handler(key ?? "");
        }

        public string ReadContext(string key)
        {
            string v;
            return PublishedContext.TryGetValue(key ?? "", out v) ? v : "";
        }

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
