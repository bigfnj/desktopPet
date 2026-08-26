using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using System.Text.Json;
using DesktopPet.Ai;
using DesktopPet.Modules;

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
        // Both arbitrated chains, each holding BOTH registration styles in ONE list. A legacy
        // Func<bool> registration is wrapped as `pet => f()` at registration time, so priority ordering stays
        // global: a module that has migrated to the pet-aware overload and one that has not still compete
        // fairly. Keeping two parallel lists would have made "which fires first" depend on which style was
        // used, which is the kind of ordering bug nobody finds by reading.
        private readonly List<Responder> _dropResponders = new List<Responder>();
        // Poke-1 responders, each also tagged with its module id so the "Trigger Speech" preference can force
        // one specific source (or pick randomly among all of them).
        private readonly List<Responder> _pokeResponders = new List<Responder>();
        private sealed class Responder
        {
            public string ModuleId;
            public int Priority;
            // Monotonic registration order, so equal priorities keep the order they registered in. This used
            // to be recovered with IndexOf against the very list being replaced -- correct only because the
            // sort ran over a copy, O(n^2), and one refactor away from a silent ordering change in the
            // "Default & Random" pick. A counter states the intent directly.
            public int Seq;
            public Func<IPet, bool> OnFire;
        }
        private int _nextResponderSeq;

        // Speech responders: offered every utterance before a bubble is drawn, highest priority first.
        private readonly List<SpeechResponder> _speechResponders = new List<SpeechResponder>();
        private sealed class SpeechResponder
        {
            public string ModuleId;
            public int Priority;
            public int Seq;
            public Func<SpeechRequest, bool> OnSpeech;
        }
        // Reentrancy latch. A responder that claims a line and then says something itself -- which the
        // reminders feature does, drawing its own bubble -- would otherwise be offered its own line forever.
        private bool _raisingSpeech;
        // Bumped per utterance, so a ShowBubble callback held past its moment can tell it has been superseded
        // and quietly decline rather than drawing a stale bubble.
        private int _speechGeneration;

        /// <summary>Highest priority first, then registration order. List.Sort is not stable, so the
        /// tie-break is explicit rather than assumed.</summary>
        private static void SortResponders(List<Responder> responders)
        {
            responders.Sort((x, y) =>
            {
                int byPriority = y.Priority.CompareTo(x.Priority);
                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);
            });
        }

        private IDisposable AddResponder(List<Responder> list, string moduleId, int priority, Func<IPet, bool> onFire)
        {
            if (onFire == null) return new Noop();
            var entry = new Responder
            {
                ModuleId = (moduleId ?? "").Trim(),
                Priority = priority,
                Seq = _nextResponderSeq++,
                OnFire = onFire,
            };
            list.Add(entry);
            SortResponders(list);
            return new Remover(() => list.Remove(entry));
        }

        /// <summary>Offer one arbitrated chain to its responders, highest priority first, until one handles
        /// it. <paramref name="only"/> restricts the offer to a single module id (the user's explicit
        /// "Trigger Speech" choice); <paramref name="shuffle"/> is the "Default &amp; Random" pick.</summary>
        private bool RaiseChain(List<Responder> chain, FormPet subject, string only, bool shuffle)
        {
            var candidates = new List<Responder>(chain);
            string preferred = (only ?? "").Trim();
            if (preferred.Length > 0)
                candidates.RemoveAll(r => !string.Equals(r.ModuleId, preferred, StringComparison.OrdinalIgnoreCase));
            else if (shuffle)
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    Responder swap = candidates[i];
                    candidates[i] = candidates[j];
                    candidates[j] = swap;
                }

            IPet handle = subject != null ? HandleFor(subject) : null;
            foreach (Responder r in candidates)
            {
                bool handled = false;
                Func<IPet, bool> fn = r.OnFire;
                Safe(() => { handled = fn(handle); });
                if (handled) return true;
            }
            return false;
        }

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
        public event Action HostShutdown;

        internal IPet HandleFor(FormPet pet)
        {
            if (pet == null) return null;
            return _handles.GetValue(pet, p => new PetHandle(p, ++_nextPetId));
        }
        internal void RaisePetSpawned(FormPet pet) { var h = PetSpawned; if (h != null) Safe(() => h(HandleFor(pet))); }
        internal void RaisePetPoked(FormPet pet, int count) { var h = PetPoked; if (h != null) Safe(() => h(new PokeInfo { Pet = HandleFor(pet), PokeCount = count })); }
        internal void RaisePetLanded(FormPet pet) { var h = PetLanded; if (h != null) Safe(() => h(HandleFor(pet))); }
        internal void RaiseShutdown() { var h = HostShutdown; if (h != null) Safe(() => h()); }

        /// <summary>
        /// Offer a drop tick to responders by priority (highest first) until one handles it. The drop belongs
        /// to <paramref name="subject"/> -- the host picks it, because an unprompted remark has no clicked pet
        /// to inherit -- and honours that pet's "Trigger Speech" choice, exactly as a poke does. Routing used
        /// to apply to pokes only, so a per-pet choice was silently ignored by half the things that speak.
        /// </summary>
        internal bool RaiseDropTick(FormPet subject)
        {
            return RaiseChain(_dropResponders, subject, PreferredModuleFor(subject), shuffle: false);
        }

        /// <summary>The speech source the user chose for this pet, falling back to the all-pets choice, or ""
        /// for "default and random". Resolved host-side so the poke and drop chains cannot disagree.</summary>
        private string PreferredModuleFor(FormPet pet)
        {
            try
            {
                if (Program.MyData == null) return "";
                return Program.MyData.GetTriggerSpeechModule(SpeechRoutingKey(pet));
            }
            catch { return ""; }
        }

        /// <summary>
        /// The key a pet's speech preference is stored under. This is NOT the pet-mix id: the mix writes the
        /// active/default pet as "", while "" in triggerSpeech already means the ALL-PETS entry. Keying a real
        /// pet as "" would silently rewrite the global preference and still look right, because the lookup
        /// falls back to global. So the active pet resolves to its real type id, which is also what
        /// IPet.TypeId and the per-pet size/sound settings already use.
        /// </summary>
        internal static string SpeechRoutingKey(FormPet pet)
        {
            string typeId = pet != null ? (pet.PetTypeId ?? "") : "";
            if (typeId.Length > 0) return typeId;
            try { return Program.MyData != null ? (Program.MyData.GetActivePetId() ?? "") : ""; }
            catch { return ""; }
        }
        private static void Safe(Action a) { try { a(); } catch { /* a bad module must not break the host */ } }

        // ---- services ----
        // Guarded on IsDisposed and wrapped in Safe, unlike before. A module holds an IPet for as long as it
        // likes -- both Fortunes and the AI brain keep a _lastPet field, and there is no PetRemoved event to
        // tell them it went away -- so a pet the user removed mid-answer is a normal case, not an exotic one.
        // Unguarded, FormPet.Say would build a fresh FormSpeech on a disposed form and throw out of the
        // module's call. SayAll is structurally immune because it walks the live pet list; Say was not.
        public void Say(IPet pet, string text)
        {
            var p = pet as PetHandle;
            if (p == null || p.Pet == null || p.Pet.IsDisposed) return;
            // Offer the speech chain here too: this path bypasses SayAll entirely, so a voice module would
            // silently miss every targeted line -- which, after per-pet routing, is most of them.
            if (RaiseSpeechRequest(p.Pet, text)) return;
            Safe(() => p.Pet.Say(text));
        }
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
        // The legacy pair: the pet is discarded, because these callers never learn it. Wrapped into the same
        // list as the pet-aware pair so one priority order governs both.
        public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop)
        {
            if (onDrop == null) return new Noop();
            return AddResponder(_dropResponders, "", priority, pet => onDrop());
        }
        public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke)
        {
            if (onPoke == null) return new Noop();
            return AddResponder(_pokeResponders, moduleId, priority, pet => onPoke());
        }

        // The pet-aware pair (1.5.0+). NOT permission-gated at registration: ModuleHost calls Init BEFORE
        // adding the module to LoadedModules, so ModuleDeclares would answer false for the very module
        // registering here and every module would be silently refused while holding a healthy-looking handle.
        public IDisposable RegisterPetDropResponder(int priority, Func<IPet, bool> onDrop)
        {
            return AddResponder(_dropResponders, "", priority, onDrop);
        }
        public IDisposable RegisterPetPokeResponder(string moduleId, int priority, Func<IPet, bool> onPoke)
        {
            return AddResponder(_pokeResponders, moduleId, priority, onPoke);
        }

        public bool PlaySound(string moduleId, byte[] audio, double volume)
        {
            try
            {
                if (!ModuleDeclares(moduleId, ModulePermissions.Audio)) return false;
                if (_startUp == null) return false;   // host not running (self-test path)
                // The user's slider always wins: a module's volume is a fraction of it, so it can be quieter
                // than the pet but never louder, and a master of 0 really is silence.
                double effective = Math.Max(0.0, Math.Min(1.0, volume)) * Volume;
                if (effective <= 0.0) return false;
                return _startUp.PlayModuleSound(moduleId ?? "", audio, effective);
            }
            catch { return false; }
        }

        public bool StopSound(string moduleId)
        {
            // Not permission-gated: going quiet is strictly weaker than the play that made the sound.
            try { return _startUp != null && _startUp.StopModuleSound(moduleId ?? ""); }
            catch { return false; }
        }

        public IDisposable RegisterSpeechResponder(string moduleId, int priority, Func<SpeechRequest, bool> onSpeech)
        {
            if (onSpeech == null) return new Noop();
            var entry = new SpeechResponder
            {
                ModuleId = (moduleId ?? "").Trim(),
                Priority = priority,
                Seq = _nextResponderSeq++,
                OnSpeech = onSpeech,
            };
            _speechResponders.Add(entry);
            _speechResponders.Sort((x, y) =>
            {
                int byPriority = y.Priority.CompareTo(x.Priority);
                return byPriority != 0 ? byPriority : x.Seq.CompareTo(y.Seq);
            });
            return new Remover(() => _speechResponders.Remove(entry));
        }

        public bool IsPetAlive(IPet pet)
        {
            var p = pet as PetHandle;
            if (p == null || p.Pet == null || p.Pet.IsDisposed) return false;
            try { return _startUp != null && _startUp.IsLivePet(p.Pet); }
            catch { return false; }
        }

        /// <summary>Module ids that registered a poke responder, highest priority first — the source list
        /// the "Trigger Speech" preference offers (plus the base's own "default &amp; random" entry).</summary>
        internal IReadOnlyList<string> PokeResponderModuleIds
        {
            get { return ResponderModuleIds(_pokeResponders); }
        }

        /// <summary>
        /// Every module that can speak for a pet: the union of the poke and drop chains, highest priority
        /// first. Union rather than poke-only, because a module registering only a drop responder would
        /// otherwise be routable-but-invisible -- selectable in settings it never appears in.
        /// </summary>
        internal IReadOnlyList<string> SpeechSourceModuleIds
        {
            get
            {
                var ids = new List<string>(ResponderModuleIds(_pokeResponders));
                foreach (string id in ResponderModuleIds(_dropResponders))
                    if (!ContainsIgnoreCase(ids, id)) ids.Add(id);
                return ids;
            }
        }

        private static IReadOnlyList<string> ResponderModuleIds(List<Responder> chain)
        {
            var ids = new List<string>(chain.Count);
            foreach (Responder r in chain)
                if (!string.IsNullOrEmpty(r.ModuleId) && !ContainsIgnoreCase(ids, r.ModuleId))
                    ids.Add(r.ModuleId);
            return ids;
        }

        private static bool ContainsIgnoreCase(List<string> ids, string candidate)
        {
            foreach (string id in ids)
                if (string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Offer the first poke of a fresh session to the poke responders, on behalf of the pet that was
        /// actually clicked. The preference is resolved HERE from that pet rather than passed in, so the poke
        /// and drop chains cannot drift apart on what "this pet's speech source" means.
        ///
        /// An empty choice is the default random pick (shuffled, so with both Fortunes and the AI brain
        /// installed either can win, and a session where none of them chooses to speak stays silent);
        /// otherwise only that module is offered the poke, and if it declines nothing else speaks -- an
        /// explicit choice is a restriction, not a preference.
        /// </summary>
        internal bool RaisePokeReaction(FormPet subject)
        {
            return RaisePokeReactionFor(subject, PreferredModuleFor(subject));
        }

        /// <summary>
        /// Offer one utterance to the speech responders before any bubble is drawn. Returns true when a
        /// responder claimed it AND asked for the bubble to be suppressed -- i.e. the caller should not draw.
        ///
        /// <paramref name="target"/> is the pet about to speak, or null for a broadcast. Fast-paths to false
        /// when nothing is registered, so a host with no voice module behaves exactly as it did before.
        /// </summary>
        internal bool RaiseSpeechRequest(FormPet target, string text)
        {
            if (_speechResponders.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            // The user's master speech switch covers voice too: SayAll does not check it (the gate lives in
            // FormPet.Say), so without this a module could voice lines the user had silenced.
            if (!SpeechEnabled) return false;
            if (_raisingSpeech) return false;

            int generation = ++_speechGeneration;
            var pending = new PendingBubble(this, target, text, generation);
            var request = new SpeechRequest
            {
                Text = text,
                Pet = target != null ? HandleFor(target) : null,
                ShowBubble = pending.Show,
            };

            _raisingSpeech = true;
            try
            {
                foreach (SpeechResponder r in _speechResponders)
                {
                    if (!ModuleDeclares(r.ModuleId, ModulePermissions.Voice)) continue;   // raise-time gate
                    bool claimed = false;
                    Func<SpeechRequest, bool> fn = r.OnSpeech;
                    Safe(() => { claimed = fn(request); });
                    if (claimed) return request.SuppressBubble;   // read only AFTER a claim
                }
            }
            finally { _raisingSpeech = false; }
            return false;
        }

        /// <summary>
        /// The host-supplied one-shot behind <see cref="SpeechRequest.ShowBubble"/>. Bypasses both the
        /// responder chain and the repeat guard, which is exactly why it has to live here: a module cannot
        /// hand a line back by calling Say/SayAll, because the guard would swallow the identical replay.
        /// </summary>
        private sealed class PendingBubble
        {
            private readonly PetHost _host;
            private readonly FormPet _target;
            private readonly string _text;
            private readonly int _generation;
            private bool _used;

            internal PendingBubble(PetHost host, FormPet target, string text, int generation)
            { _host = host; _target = target; _text = text; _generation = generation; }

            internal void Show(double seconds)
            {
                if (_used) return;
                _used = true;
                // A newer utterance has already been offered, so this one is stale: drawing it now would put
                // an old line on screen after a newer one.
                if (_host == null || _host._speechGeneration != _generation) return;
                int dwell = seconds > 0 ? (int)Math.Max(2, Math.Min(30, Math.Round(seconds))) : 0;
                Action draw = delegate
                {
                    if (_target != null) { if (!_target.IsDisposed) _target.SayWithDwell(_text, dwell); }
                    else if (_host._startUp != null) _host._startUp.ShowBubbleOnAll(_text, dwell);
                };
                // A module resuming a synthesis await will normally already be on the UI thread, but a
                // Task.Run continuation would not be, and touching a window off-thread corrupts it.
                try
                {
                    if (_target != null && _target.InvokeRequired) _target.BeginInvoke(draw);
                    else draw();
                }
                catch { }
            }
        }

        /// <summary>Test seam: the same arbitration with the preference supplied directly, so the chain's
        /// semantics (explicit choice is a restriction, unknown id stays silent, random offers everyone) can be
        /// asserted without a live pet and a settings file. Production callers use the overload above.</summary>
        internal bool RaisePokeReactionFor(FormPet subject, string preferredModuleId)
        {
            return RaiseChain(_pokeResponders, subject, preferredModuleId, shuffle: true);
        }
        private readonly Random _random = new Random();
        // Last successfully fetched catalog, so downloading N items after a browse doesn't re-fetch the
        // catalog N times. Explicit-refresh only (a fresh FetchCatalogItemsAsync replaces it) — same shape
        // as the AI brain's model-list cache, no TTL.
        private RemoteCatalog _catalogCache;

        public async System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind)
        {
            RemoteCatalog catalog = await RemoteCatalogClient
                .FetchAsync(System.Threading.CancellationToken.None)
                .ConfigureAwait(false);
            _catalogCache = catalog;
            var items = new List<CatalogItem>();
            if (IsPackKind(kind))
                foreach (CatalogPack pack in catalog.Packs)
                    items.Add(new CatalogItem
                    {
                        Id = pack.Id,
                        Name = pack.Name,
                        Group = pack.Group,
                        Description = pack.Description,
                        Bytes = pack.Bytes,
                        Count = pack.Count,
                    });
            else if (IsPetKind(kind))
                foreach (CatalogPet pet in catalog.Pets)
                    items.Add(new CatalogItem
                    {
                        Id = pet.Id,
                        Name = pet.Name,
                        Group = pet.Author ?? "",   // pets have no collection; the author is the useful grouping
                        Description = "",
                        Bytes = pet.Bytes,
                        Count = 0,
                    });
            return items;
        }

        public async System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id)
        {
            if (!IsPackKind(kind) && !IsPetKind(kind))
                throw new InvalidDataException("Unknown catalog kind: " + (kind ?? ""));
            RemoteCatalog catalog = _catalogCache;
            if (catalog == null)
            {
                catalog = await RemoteCatalogClient
                    .FetchAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(false);
                _catalogCache = catalog;
            }

            if (IsPetKind(kind))
            {
                CatalogPet foundPet = null;
                foreach (CatalogPet pet in catalog.Pets)
                    if (string.Equals(pet.Id, id, StringComparison.OrdinalIgnoreCase)) { foundPet = pet; break; }
                if (foundPet == null) throw new InvalidDataException("No catalog pet with id '" + (id ?? "") + "'.");
                // Same verified path the host's own Pets gallery uses, bounded by the pet-XML size limit.
                return await RemoteCatalogClient.DownloadVerifiedAsync(
                    foundPet.Url,
                    foundPet.Sha256,
                    PetCatalog.MaximumPetXmlBytes,
                    System.Threading.CancellationToken.None).ConfigureAwait(false);
            }

            CatalogPack found = null;
            foreach (CatalogPack pack in catalog.Packs)
                if (string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase)) { found = pack; break; }
            if (found == null) throw new InvalidDataException("No catalog pack with id '" + (id ?? "") + "'.");
            // Re-validates the asset URL and enforces the recorded SHA-256 before returning any bytes.
            return await RemoteCatalogClient.DownloadVerifiedAsync(
                found.Url,
                found.Sha256,
                FortunePackLoadPolicy.MaximumFileBytes,
                System.Threading.CancellationToken.None).ConfigureAwait(false);
        }

        private static bool IsPackKind(string kind)
        {
            return string.Equals(kind, CatalogKinds.Pack, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPetKind(string kind)
        {
            return string.Equals(kind, CatalogKinds.Pet, StringComparison.OrdinalIgnoreCase);
        }

        public bool OpenLink(string moduleId, string httpsUrl)
        {
            try
            {
                if (!ModuleDeclares(moduleId, ModulePermissions.Network)) return false;
                string normalized;
                if (!WebLinks.TryNormalizeHttpsLink(httpsUrl, out normalized)) return false;
                WebLinks.TryOpen(normalized);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The pet inspection/authoring/placement service. Permission-gated on the module's own declared
        /// ModulePermissions.Pets: a module without it gets a refusing instance rather than an exception,
        /// the same way RegisterHotkey hands back a no-op handle. Cached per module id so a module can hold
        /// the reference it is given.
        /// </summary>
        public IPetManager GetPetManager(string moduleId)
        {
            string key = (moduleId ?? "").Trim();
            IPetManager cached;
            if (_petManagers.TryGetValue(key, out cached)) return cached;
            IPetManager manager = ModuleDeclares(key, ModulePermissions.Pets)
                ? (IPetManager)new PetManagerBridge(_startUp, this)
                : new DenyingPetManager();
            _petManagers[key] = manager;
            return manager;
        }
        private readonly Dictionary<string, IPetManager> _petManagers =
            new Dictionary<string, IPetManager>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The app's effective theme, resolving the user's light/dark/system preference exactly as the
        /// host's own WPF windows do — so a module-owned window agrees with them instead of second-guessing the
        /// OS. Defaults to light if the preference cannot be read; a wrong-but-readable window beats a throw.</summary>
        public bool IsDarkTheme
        {
            get
            {
                try
                {
                    string mode = Program.MyData != null ? Program.MyData.GetThemeMode() : "system";
                    return DesktopPet.Wpf.WpfTheme.EffectiveDark(mode);
                }
                catch { return false; }
            }
        }

        /// <summary>Tag a module's line and drop it in the app's diagnostic log. Best-effort by contract: a
        /// module calling this must never be punished for the log being unavailable.</summary>
        public void Log(string moduleId, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                string id = string.IsNullOrWhiteSpace(moduleId) ? "module" : moduleId.Trim();
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.info, "[" + id + "] " + message);
            }
            catch { }
        }

        /// <summary>True when the named loaded module declared the capability in its own ModuleInfo. A
        /// module that isn't loaded (or declares nothing) gets nothing — the declaration is the gate.</summary>
        private bool ModuleDeclares(string moduleId, ModulePermissions required)
        {
            if (_startUp == null || string.IsNullOrWhiteSpace(moduleId)) return false;
            foreach (IModule m in _startUp.LoadedModules)
                if (m != null && m.Info != null &&
                    string.Equals(m.Info.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                    return (m.Info.Permissions & required) == required;
            return false;
        }

        public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions)
        {
            try
            {
                var patterns = new List<string>();
                if (extensions != null)
                    foreach (string ext in extensions)
                    {
                        string bare = (ext ?? "").Trim().TrimStart('.', '*');
                        if (bare.Length > 0) patterns.Add("*." + bare);
                    }
                string label = string.IsNullOrWhiteSpace(fileKindLabel) ? "Files" : fileKindLabel.Trim();
                string filter = patterns.Count > 0
                    ? label + " (" + string.Join(";", patterns) + ")|" + string.Join(";", patterns) + "|All files (*.*)|*.*"
                    : "All files (*.*)|*.*";

                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "Choose files" : title.Trim(),
                    Filter = filter,
                    Multiselect = true,
                    CheckFileExists = true,
                };
                bool? picked = dialog.ShowDialog();
                if (picked != true || dialog.FileNames == null) return new List<string>();
                return new List<string>(dialog.FileNames);
            }
            catch { return new List<string>(); }
        }

        public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
        public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }

        /// <summary>The module's own data directory (settings/storage) — separate from its install folder
        /// under <c>modules/&lt;id&gt;/</c>. Exposed so an uninstall action can remove both and not orphan data.</summary>
        public static string ModuleDataDirectory(string moduleId) { return ModuleDataDir(moduleId); }

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
                try { File.WriteAllText(_path, JsonSerializer.Serialize(_d), new UTF8Encoding(false)); return true; }
                catch { return false; }
            }
            private static Dictionary<string, string> Load(string path)
            {
                try { if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>(); }
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
        public string TypeId { get { return _pet != null ? _pet.PetTypeId : ""; } }
        internal FormPet Pet { get { return _pet; } }
    }

    /// <summary>
    /// IPetManager over StartUp's pet orchestration plus the on-disk pet library. The host keeps owning the
    /// persisted mix, the per-pet preferences, the active-pet id and the MAX_SHEEPS cap; this bridge only
    /// exposes the verbs, so the Pets capability can live in a module. Every call is best-effort and never
    /// throws into a module.
    /// </summary>
    internal sealed class PetManagerBridge : IPetManager
    {
        private readonly StartUp _startUp;
        private readonly PetHost _host;
        internal PetManagerBridge(StartUp startUp, PetHost host) { _startUp = startUp; _host = host; }

        public int MaxPets { get { return StartUp.MAX_SHEEPS; } }
        public bool IsAtMax { get { return _startUp != null && _startUp.IsAtMaxPets; } }

        public string PetsDirectory
        {
            get { try { return AppPaths.LibraryPetsDirectory ?? ""; } catch { return ""; } }
        }

        public IReadOnlyList<PetTypeInfo> InstalledTypes()
        {
            var list = new List<PetTypeInfo>();
            try
            {
                foreach (PetCatalog.PetInfo p in PetCatalog.EnumerateLocal())
                    list.Add(new PetTypeInfo
                    {
                        TypeId = p.IsBuiltIn ? PetCatalog.BuiltInPetId : (p.Id ?? ""),
                        DisplayName = p.DisplayName,
                        IsBuiltIn = p.IsBuiltIn,
                    });
            }
            catch { }
            return list;
        }

        public bool TryReadTypeXml(string typeId, out string animationsXml, out string error)
        {
            animationsXml = null;
            error = null;
            // The base resolver owns the id safety check and the library -> bundled -> built-in lookup, so the
            // module never touches the host's folder layout (and cannot reach the bundled/beside-exe root).
            try { return PetCatalog.TryReadPetXml(typeId, out animationsXml, out error); }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public IReadOnlyList<PetCount> OnScreenMix()
        {
            var list = new List<PetCount>();
            try
            {
                if (_startUp != null)
                    foreach (PetCountEntry e in _startUp.OnScreenMix())
                        list.Add(new PetCount { TypeId = e.Id ?? "", Count = e.Count });
            }
            catch { }
            return list;
        }

        public bool SpawnOne(string typeId)
        {
            try { return _startUp != null && _startUp.AddPetFromTray(typeId ?? ""); }
            catch { return false; }
        }

        public bool RemoveOne(string typeId)
        {
            try { return _startUp != null && _startUp.RemoveOnePet(typeId ?? ""); }
            catch { return false; }
        }

        public bool ValidateXml(string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(animationsXml)) { error = "No pet XML was supplied."; return false; }
                XmlData.RootNode parsed;
                return PetXmlValidator.TryParse(animationsXml, out parsed, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public IPetPreview SpawnPreview(string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (_startUp == null) { error = "No pet runtime."; return null; }
                FormPet pet = _startUp.SpawnPreviewPet(animationsXml, out error);
                if (pet == null) return null;
                return new PreviewHandle(_startUp, _host, pet);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public bool InstallType(string typeId, string animationsXml, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(typeId) || !SecureDownload.IsSafeId(typeId))
                { error = "Unsafe pet id."; return false; }
                if (string.IsNullOrWhiteSpace(animationsXml)) { error = "No pet data."; return false; }

                // Strip a leading BOM/whitespace so an authored string and a decoded download behave the same.
                string xml = animationsXml.TrimStart('﻿', ' ', '\t', '\r', '\n');
                byte[] bytes = new UTF8Encoding(false).GetBytes(xml);
                if (bytes.Length > PetXmlValidator.MaximumXmlBytes) { error = "Pet file too large."; return false; }

                // Never trust the caller: validate structure before anything lands on disk.
                XmlData.RootNode parsed;
                string validationError;
                if (!PetXmlValidator.TryParse(xml, out parsed, out validationError))
                { error = validationError; return false; }

                string directory = SafeLibraryDir(typeId);
                Directory.CreateDirectory(directory);
                SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public bool UninstallType(string typeId, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(typeId) || !SecureDownload.IsSafeId(typeId))
                { error = "Unsafe pet id."; return false; }
                string directory = SafeLibraryDir(typeId);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Contain every write inside the writable pet library (mirrors PetsPaneControl.SafeLibraryDir).
        private static string SafeLibraryDir(string id)
        {
            if (!SecureDownload.IsSafeId(id)) throw new InvalidDataException("Unsafe pet id.");
            string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pet path escapes the library.");
            return directory;
        }
    }

    /// <summary>What a module without ModulePermissions.Pets gets: every verb refuses, nothing throws.</summary>
    internal sealed class DenyingPetManager : IPetManager
    {
        private const string Denied = "This module has not declared the Pets permission.";
        public int MaxPets { get { return StartUp.MAX_SHEEPS; } }
        public bool IsAtMax { get { return true; } }
        public string PetsDirectory { get { return ""; } }
        public IReadOnlyList<PetTypeInfo> InstalledTypes() { return new List<PetTypeInfo>(); }
        public bool TryReadTypeXml(string typeId, out string animationsXml, out string error) { animationsXml = null; error = Denied; return false; }
        public IReadOnlyList<PetCount> OnScreenMix() { return new List<PetCount>(); }
        public bool SpawnOne(string typeId) { return false; }
        public bool RemoveOne(string typeId) { return false; }
        public bool ValidateXml(string animationsXml, out string error) { error = Denied; return false; }
        public IPetPreview SpawnPreview(string animationsXml, out string error) { error = Denied; return null; }
        public bool InstallType(string typeId, string animationsXml, out string error) { error = Denied; return false; }
        public bool UninstallType(string typeId, out string error) { error = Denied; return false; }
    }

    /// <summary>
    /// A module's handle on one transient preview pet. Holds the FormPet directly (not an id) so Remove
    /// targets exactly the pet this module spawned — the tray's remove verb works BY TYPE and could pick a
    /// different pet. Idempotent, and it goes dead by itself if the pet closes for any other reason.
    /// </summary>
    internal sealed class PreviewHandle : IPetPreview
    {
        private readonly StartUp _startUp;
        private readonly PetHost _host;
        private FormPet _pet;

        internal PreviewHandle(StartUp startUp, PetHost host, FormPet pet)
        {
            _startUp = startUp;
            _host = host;
            _pet = pet;
        }

        public IPet Pet
        {
            get
            {
                FormPet pet = _pet;
                if (pet == null || pet.IsDisposed) return null;
                return _host != null ? _host.HandleFor(pet) : null;
            }
        }

        public bool IsAlive
        {
            get { FormPet pet = _pet; return pet != null && !pet.IsDisposed; }
        }

        public void Remove()
        {
            FormPet pet = _pet;
            _pet = null;
            if (pet == null) return;
            try { if (_startUp != null) _startUp.RemovePetInstance(pet); }
            catch { }
        }

        public void Dispose() { Remove(); }
    }
}
