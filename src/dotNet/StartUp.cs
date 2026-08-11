using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopPet.Ai;

#if !PORTABLE
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
#endif

namespace DesktopPet
{
    /// <summary>
    /// StartUp class. This class will initialize the entire application and define some constants.
    /// </summary>
    public sealed class StartUp : IDisposable, DesktopPet.Options.IPetRuntime
    {
        /// <summary>
        /// Maximal sheeps (too much sheeps will cover too much the screen and would not be nice to see).
        /// </summary>
        public const int MAX_SHEEPS = 16;

        /// <summary>
        /// DEBUG TYPE. If you press "SHIFT" by starting the application, a debug Window will appear.
        /// </summary>
        public enum DEBUG_TYPE
        {
            /// <summary>
            /// Only info, to show what is happening.
            /// </summary>
            info = 1,
            /// <summary>
            /// Something important happened or something that was not expected.
            /// </summary>
            warning = 2,
            /// <summary>
            /// An error is occurred. The application need to do something that was not expected.
            /// </summary>
            error = 3,
        }

        /// <summary>
        /// A timer to allow some times to the sheeps to die, before the application will close definitively.
        /// </summary>
        private readonly System.Windows.Forms.Timer timer1 =
            new System.Windows.Forms.Timer();

        /// <summary>
        /// Each sheep is in a different form.
        /// </summary>
        readonly FormPet[] sheeps = new FormPet[MAX_SHEEPS];
        readonly RetiringValueRegistry<FormPet> retiringPets =
            new RetiringValueRegistry<FormPet>();

        /// <summary>
        /// Debug window, used only if SHIFT was pressed by starting the application.
        /// </summary>
        static FormDebug debug = null;
        static readonly object debugLock = new object();

        /// <summary>
        /// Number of currently active sheeps.
        /// </summary>
        int iSheeps = 0;

        /// <summary>
        /// The XML file where all animations are defined.
        /// </summary>
        Xml xml;

        /// <summary>
        /// Class of the animations. All animations are stored there.
        /// </summary>
        Animations animations;

        // Extra pet types spawned "alongside" the active one (id -> loaded Xml/Animations + refcount).
        // The active pair above (xml/animations) is the default type and is never in the registry; only
        // extra types are reference-counted and disposed when their last pet closes. UI-thread only.
        readonly PetTypeRegistry registry = new PetTypeRegistry();
        readonly Dictionary<FormPet, PetTypeRegistry.Entry> petEntries =
            new Dictionary<FormPet, PetTypeRegistry.Entry>();
        AudioOutput audioOutput;   // host-owned audio output (B1): plays animation sounds, later TTS
        // Startup spawn plan: the persisted pet mix flattened to one id per pet, spawned one-per-tick.
        List<string> spawnPlan;
        int spawnPlanIndex;

        /// <summary>
        /// Process Icon. The tray icon on the taskbar.
        /// </summary>
        readonly ProcessIcon pi;

        bool isRealoadingSettings = false;

        /// <summary>
        /// AI brain (lazy-created on first use). Owns the Ollama backend and the
        /// capture -> OCR/vision -> response pipeline. Purely additive to the engine.
        /// </summary>
        readonly AiSessionManager aiSession = new AiSessionManager();
        readonly CancellationTokenSource lifetimeCancellation =
            new CancellationTokenSource();
        int aiConfigurationVersion;
        bool disposed;
        private static readonly TimeSpan ShutdownBudget =
            TimeSpan.FromSeconds(3);

        /// <summary>Cached AI-layer settings (loaded once at startup).</summary>
        AiSettings aiConfig;


        // Poke-escalation state (right-clicking the sheep). Thresholds are tunable; the sass lines
        // live in PokeReactions so more can be slotted in later.
        int pokeCount;
        DateTime lastPokeUtc = DateTime.MinValue;
        const double PokeResetSeconds = 7.0;   // a pause this long starts a fresh poke session
        const int PokeIgnoreFrom = 3;          // pokes 3-4: ignore (turn away, no words)
        const int PokeSassFrom   = 5;          // pokes 5-11: verbal sass
        const int PokeEscapeAt   = 12;         // poke 12: bathtub escape

        /// <summary>Polls the pet's fall on launch and speaks a fortune once it has settled (see below).</summary>
        System.Windows.Forms.Timer landTimer;
        int landPrevY = int.MinValue;   // pet's last Y; it's falling only while Y increases
        int landStable;                 // consecutive polls with no downward movement
        int landTicks;                  // total polls (for the min-delay + safety cap)

        /// <summary>Random-drop timer: periodically speaks a fortune at a randomized interval
        /// (the Fortunes module owns the fortune itself). Null when the feature is disabled.</summary>
        System.Windows.Forms.Timer dropTimer;
        EventHandler dropTimerHandler;

        /// <summary>Random source for the jittered random-drop interval.</summary>
        readonly Random aiRand = new Random();

        /// <summary>
        /// Constructor. Called when application is started.
        /// </summary>
        /// <param name="processIcon">ProcessIcon class, to change icon when a new pet is selected.</param>
        // Plugin host bridge + module loader (S1). Modules load in the ctor and receive lifecycle
        // events raised from the existing hook points below; the current features stay in place alongside.
        internal DesktopPet.Plugins.PetHost Host { get; private set; }
        private DesktopPet.Plugins.ModuleHost moduleHost;

        public StartUp(ProcessIcon processIcon)
        {
            pi = processIcon ?? throw new ArgumentNullException("processIcon");

                // If SHIFT key was pressed, open Debug window
            Keys ks = Control.ModifierKeys;
            if (ks == Keys.Shift)
            {
                var debugWindow = new FormDebug();
                debugWindow.FormClosed += delegate
                {
                    lock (debugLock)
                        if (ReferenceEquals(debug, debugWindow))
                            debug = null;
                };
                lock (debugLock) debug = debugWindow;
                debugWindow.Show();
                AddDebugInfo(DEBUG_TYPE.info, "debug window started");
            }
            
            string candidate = Program.InitialXmlOverride;
            bool externalCandidate = !string.IsNullOrWhiteSpace(candidate);
            if (!externalCandidate) candidate = Program.MyData.GetXml();
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = Properties.Resources.animations;

            string error;
            // Key the active/default pet by its real id (persisted activePetId) so per-pet size/sound follow
            // the actual pet, not the "" active-slot placeholder. Defaults to the built-in pet.
            string activeId = Program.MyData != null ? Program.MyData.GetActivePetId() : PetCatalog.BuiltInPetId;
            int activeFactor = Program.MyData.GetEffectivePetScaleFactor(activeId);
            if (!TryStageRuntime(candidate, activeFactor, out xml, out animations, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Configured pet rejected: " + error);
                candidate = Properties.Resources.animations;
                if (!TryStageRuntime(candidate, activeFactor, out xml, out animations, out error))
                    throw new InvalidDataException("The built-in pet failed validation: " + error);
            }
            animations.PetTypeId = activeId;
            animations.Activate();
            if (!Program.MyData.SetXml(
                    candidate,
                    externalCandidate ? "external" : ""))
                AddDebugInfo(
                    DEBUG_TYPE.warning,
                    "The active pet could not be persisted; the previous pet will return next launch.");

                // Set animation icon
            ApplyTrayIcon(xml);

                // Wait 1 second, before starting first animation
            timer1.Tag = "A";
            timer1.Tick += new EventHandler(Timer1_Tick);
            timer1.Interval = 1000;
            timer1.Enabled = true;

            Program.MyData.ListenOnXMLChanged(XmlFileChanged);
            Program.MyData.ListenOnOptionsChanged(OptionFileChanged);

            InitAiTriggers();

            // Plugin host: load modules from <baseDir>\modules; each receives lifecycle events + host
            // services. A load/init failure is isolated so a bad module never stops the pet from starting.
            Host = new DesktopPet.Plugins.PetHost(this);
            moduleHost = new DesktopPet.Plugins.ModuleHost();
            // B1: play the engine-selected animation sound through the host-owned AudioOutput directly.
            // The base owns playback now (Option B); the S2 module-era AnimationStarted routing is retired,
            // so the Sound module is inert (removed in B4). Volume comes from the user's setting. Cleared in
            // Dispose so a torn-down output is never held.
            audioOutput = new AudioOutput();
            audioOutput.SetDevice(Program.MyData != null ? Program.MyData.GetAudioDeviceId() : "");
            Animations.SoundSink = (petTypeId, animId, data, loop) =>
            {
                AudioOutput a = audioOutput;
                LocalData d = Program.MyData;
                if (a != null && d != null && d.IsPetSoundEnabled(petTypeId))   // per-pet mute (B3)
                    a.Play(data, loop, d.GetVolume());
            };
            try
            {
                string modulesDir = System.IO.Path.Combine(AppContext.BaseDirectory, "modules");
                int loadedModules = moduleHost.LoadFrom(modulesDir, Host, msg => AddDebugInfo(DEBUG_TYPE.info, "[module] " + msg));
                if (loadedModules > 0) AddDebugInfo(DEBUG_TYPE.info, loadedModules + " module(s) loaded");
            }
            catch (Exception moduleEx) { AddDebugInfo(DEBUG_TYPE.warning, "module host init failed: " + moduleEx.Message); }
        }

        private static bool TryStageRuntime(
            string source,
            int scaleFactor,
            out Xml stagedXml,
            out Animations stagedAnimations,
            out string error)
        {
            stagedXml = new Xml(scaleFactor);
            stagedAnimations = null;
            error = null;
            try
            {
                if (!stagedXml.TryReadXml(source, out error))
                    throw new InvalidDataException(error);

                stagedAnimations = new Animations(stagedXml);
                stagedXml.LoadAnimations(stagedAnimations);
                if (stagedAnimations.SheepAnimations.Count == 0 ||
                    stagedAnimations.SheepSpawn.Count == 0)
                    throw new InvalidDataException(
                        "Pet XML did not produce a runnable animation and spawn set.");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (stagedAnimations != null) stagedAnimations.Dispose();
                stagedXml.Dispose();
                stagedAnimations = null;
                stagedXml = null;
                return false;
            }
        }

        private void ApplyTrayIcon(Xml source)
        {
            if (source == null ||
                source.AnimationXML == null ||
                source.bitmapIcon == null)
                return;
            using (var iconCopy =
                new MemoryStream(source.bitmapIcon.ToArray(), false))
            {
                pi.SetIcon(
                    iconCopy,
                    source.AnimationXML.Header.Petname,
                    source.AnimationXML.Header.Author,
                    source.AnimationXML.Header.Title,
                    source.AnimationXML.Header.Version,
                    source.AnimationXML.Header.Info);
            }
        }

        internal bool RefreshTrayIconForResourceChurn()
        {
            if (!Program.ResourceChurnSelfTestActive ||
                disposed ||
                xml == null)
                return false;
            ApplyTrayIcon(xml);
            return true;
        }


        private void XmlFileChanged(object source, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            LoadNewXMLFromString(Program.MyData.LoadXML());
        }

        private void OptionFileChanged(object source, FileSystemEventArgs e)
        {
            if (isRealoadingSettings) return;
            isRealoadingSettings = true;
            Thread.Sleep(1000);
            Program.MyData.LoadSettings();
            Thread.Sleep(200);
            isRealoadingSettings = false;
        }

        /// <summary>
        /// Dispose class -> used to dispose xml class
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Shut modules down first (unsubscribe + unload their load contexts) before the host tears down.
            Animations.SoundSink = null;   // stop routing engine sounds before the output goes away
            if (audioOutput != null) { audioOutput.Dispose(); audioOutput = null; }   // B1 host-owned output
            if (Host != null) Host.RaiseShutdown();
            if (moduleHost != null) { moduleHost.Dispose(); moduleHost = null; }

            timer1.Stop();
            timer1.Tick -= Timer1_Tick;
            if (dropTimer != null)
            {
                dropTimer.Stop();
                if (dropTimerHandler != null)
                    dropTimer.Tick -= dropTimerHandler;
                dropTimer.Dispose();
                dropTimer = null;
                dropTimerHandler = null;
            }
            if (landTimer != null)
            {
                landTimer.Stop();
                landTimer.Tick -= LandTimer_Tick;
                landTimer.Dispose();
                landTimer = null;
            }

            lifetimeCancellation.Cancel();
            Stopwatch shutdown = Stopwatch.StartNew();
            aiSession.DisposeWithin(
                RemainingShutdownBudget(
                    ShutdownBudget,
                    shutdown.Elapsed));
            lifetimeCancellation.Dispose();

            CloseAllPetsImmediate();
            registry.DisposeAll();   // extra pet types (their FormClosed already released most)
            if (animations != null) { animations.Dispose(); animations = null; }
            if (xml != null) { xml.Dispose(); xml = null; }
            FormDebug debugWindow;
            lock (debugLock)
            {
                debugWindow = debug;
                debug = null;
            }
            if (debugWindow != null && !debugWindow.IsDisposed)
            {
                try { debugWindow.Close(); } catch { }
            }
            timer1.Dispose();
            pi.Dispose();
        }

        internal static TimeSpan RemainingShutdownBudget(
            TimeSpan budget,
            TimeSpan elapsed)
        {
            if (budget <= TimeSpan.Zero || elapsed >= budget)
                return TimeSpan.Zero;
            if (elapsed <= TimeSpan.Zero) return budget;
            return budget - elapsed;
        }
        
            /// <summary>
            /// Calling this function will add another sheep on the desktop, if MAX_SHEEP was not reached.
            /// </summary>
        public void AddSheep()
        {
            AddSheepCore(xml, animations, null);
        }

        /// <summary>
        /// Add a pet of a specific type by id, alongside any pets already on screen. A null/empty id
        /// (or the active pet) spawns the active/default type; a folder id loads that pet type on demand
        /// (reference-counted, disposed when its last pet closes). Returns false if the type could not
        /// be loaded or the max-pets cap was reached.
        /// </summary>
        public bool AddSheep(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return AddSheepCore(xml, animations, null) != null;
            }

            PetTypeRegistry.Entry entry = ResolveExtraType(id);
            if (entry == null) return false;

            FormPet spawned = AddSheepCore(entry.Xml, entry.Animations, entry);
            if (spawned == null)
            {
                registry.DropIfUnused(entry);   // slot was full: don't leak an unspawned type
                return false;
            }
            return true;
        }

        // Shared spawn: create+show a pet of the given (Xml, Animations). When entry != null the pet is
        // an "extra" type, so its use is reference-counted and released on FormClosed. Returns the new
        // pet, or null when the max-pets cap is reached.
        private FormPet AddSheepCore(Xml petXml, Animations petAnimations, PetTypeRegistry.Entry entry)
        {
            if (iSheeps >= MAX_SHEEPS)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "max PETs reached");
                return null;
            }

            FormPet newSheep = CreateAndInitializeOwnedPet(
                delegate { return new FormPet(petAnimations, petXml); },
                delegate(FormPet pet)
                {
                    pet.Show(petXml.spriteWidth, petXml.spriteHeight);
                    pet.Play(true);
                });
            sheeps[iSheeps] = newSheep;
            iSheeps++;

            if (entry != null)
            {
                petEntries[newSheep] = entry;
                registry.Increment(entry);
                newSheep.FormClosed += ExtraPet_FormClosed;
            }

            AddDebugInfo(DEBUG_TYPE.info, "new pet...");
            AddDebugInfo(DEBUG_TYPE.info, petXml.SpriteCount.ToString() + " shared frames ready");
            if (Host != null) Host.RaisePetSpawned(newSheep);
            return newSheep;
        }

        // Load (or reuse) an extra pet type by id. Reuses the validated staging path so an untrusted
        // pet XML is never run without validation; does NOT call Animations.Activate() (only the active
        // type owns the Animations.Xml "current type" static). Returns null on any failure (logged).
        private PetTypeRegistry.Entry ResolveExtraType(string id)
        {
            PetTypeRegistry.Entry existing;
            if (registry.TryGet(id, out existing)) return existing;

            string xmlText, error;
            if (!PetCatalog.TryReadPetXml(id, out xmlText, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Pet '" + id + "' could not be loaded: " + error);
                return null;
            }

            Xml stagedXml;
            Animations stagedAnimations;
            int factor = Program.MyData.GetEffectivePetScaleFactor(id);
            if (!TryStageRuntime(xmlText, factor, out stagedXml, out stagedAnimations, out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "Pet '" + id + "' failed validation: " + error);
                return null;
            }
            stagedAnimations.PetTypeId = id;   // extra type -> keyed by its folder id (per-pet settings)
            return registry.Add(id, stagedXml, stagedAnimations);
        }

        // A pet of an extra type closed: release its reference so the type's shared Xml/Animations are
        // disposed once the last pet of that type is gone. Never disposes for the active/default type
        // (those pets aren't in petEntries).
        private void ExtraPet_FormClosed(object sender, FormClosedEventArgs e)
        {
            FormPet pet = sender as FormPet;
            if (pet == null) return;
            pet.FormClosed -= ExtraPet_FormClosed;
            PetTypeRegistry.Entry entry;
            if (petEntries.TryGetValue(pet, out entry))
            {
                petEntries.Remove(pet);
                registry.Decrement(entry);
            }
        }

        // Flatten the persisted pet mix into a one-id-per-pet spawn plan (total capped at MAX_SHEEPS).
        // "" = the active/default pet. When there is no persisted mix (fresh install or after a reset),
        // fall back to the classic behaviour: GetAutoStartPets() copies of the active pet.
        private List<string> BuildStartupSpawnPlan()
        {
            var plan = new List<string>();
            List<PetCountEntry> mix = Program.MyData.GetPetMix();
            if (mix != null)
            {
                foreach (PetCountEntry entry in mix)
                {
                    if (entry == null) continue;
                    for (int i = 0; i < entry.Count && plan.Count < MAX_SHEEPS; i++)
                        plan.Add(entry.Id ?? "");
                }
            }
            if (plan.Count == 0)
            {
                int count = Math.Min(MAX_SHEEPS, Math.Max(1, Program.MyData.GetAutoStartPets()));
                for (int i = 0; i < count; i++) plan.Add("");
            }
            return plan;
        }

        /// <summary>True when the max-pets cap is reached (no more can be added).</summary>
        public bool IsAtMaxPets { get { return iSheeps >= MAX_SHEEPS; } }

        /// <summary>The persisted active/default pet's animations.xml (for the Options seam / IPetRuntime).</summary>
        public string ActivePetXml { get { return Program.MyData != null ? Program.MyData.GetXml() : ""; } }

        /// <summary>
        /// The current on-screen pet mix: each live root pet counted under its type id ("" = the
        /// active/default pet), in first-appearance order. Used by the tray Remove submenu and to
        /// persist the mix for next launch.
        /// </summary>
        internal List<PetCountEntry> OnScreenMix()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            for (int i = 0; i < iSheeps; i++)
            {
                FormPet pet = sheeps[i];
                if (pet == null) continue;
                PetTypeRegistry.Entry entry;
                string id = petEntries.TryGetValue(pet, out entry) ? (entry.Id ?? "") : "";
                if (!counts.ContainsKey(id)) { counts[id] = 0; order.Add(id); }
                counts[id]++;
            }
            var mix = new List<PetCountEntry>();
            foreach (string id in order)
                mix.Add(new PetCountEntry { Id = id, Count = counts[id] });
            return mix;
        }

        /// <summary>Spawn one pet of the given type from the tray and remember the new on-screen mix.</summary>
        public bool AddPetFromTray(string id)
        {
            bool added = AddSheep(id);
            if (added) PersistMix();
            return added;
        }

        /// <summary>
        /// Remove one on-screen pet of a given type (the most recently added), routing through KillSheep
        /// so the kill animation + reference release + persistence all run. False when none is present.
        /// </summary>
        public bool RemoveOnePet(string id)
        {
            string target = id ?? "";
            for (int i = iSheeps - 1; i >= 0; i--)
            {
                FormPet pet = sheeps[i];
                if (pet == null) continue;
                PetTypeRegistry.Entry entry;
                string petId = petEntries.TryGetValue(pet, out entry) ? (entry.Id ?? "") : "";
                if (string.Equals(petId, target, StringComparison.OrdinalIgnoreCase))
                    return KillSheep(pet);   // KillSheep persists the reduced mix
            }
            return false;
        }

        /// <summary>
        /// Set a pet type's size override (level 1/2/3, or 0 to follow the global size) and persist it.
        /// The size is baked in when the type is staged, so it takes effect the next time this pet is
        /// added (or on the next launch); pets of this type already on screen keep their current size
        /// until then. A staged-but-unspawned copy is dropped here so a fresh add re-stages at the new
        /// factor immediately. Returns true if the stored value changed. id "" is the active/default pet.
        /// </summary>
        public bool SetPetSize(string id, int level)
        {
            bool changed = Program.MyData.SetPetSizeLevel(id ?? "", level);
            PetTypeRegistry.Entry entry;
            if (!string.IsNullOrEmpty(id) && registry.TryGet(id, out entry))
                registry.DropIfUnused(entry);   // only drops when no pet is using it (safe)
            return changed;
        }

        /// <summary>Route host audio to the given output device GUID ("" = default). Applied live so a
        /// Preferences change takes effect immediately (B1.5).</summary>
        public void ApplyAudioDevice(string deviceId)
        {
            AudioOutput a = audioOutput;
            if (a != null) a.SetDevice(deviceId);
        }

        /// <summary>Play a short test tone through the current output device (the Preferences "Test sound" button).</summary>
        public void PlayTestSound()
        {
            AudioOutput a = audioOutput;
            if (a != null) a.PlayTestTone();
        }

        /// <summary>Mute/unmute a pet type's animation sound (per-pet sound toggle, B3). Persists only; the
        /// sink checks it at play time so it takes effect on the next sound, no restage. id "" = active pet.</summary>
        public bool SetPetSound(string id, bool enabled)
        {
            return Program.MyData != null && Program.MyData.SetPetSoundEnabled(id, enabled);
        }

        // Persist the current on-screen mix so the same set is restored next launch. Called after
        // user-initiated changes (tray add/remove, KillSheep, replace-all), never during the startup
        // restore itself.
        private void PersistMix()
        {
            if (disposed) return;
            try { Program.MyData.SetPetMix(OnScreenMix()); } catch { }
        }

        internal bool RunResourceChurnPetCycle(string speech)
        {
            if (!Program.ResourceChurnSelfTestActive ||
                disposed ||
                animations == null ||
                xml == null)
                return false;

            FormPet pet = null;
            try
            {
                pet = CreateAndInitializeOwnedPet(
                    delegate { return new FormPet(animations, xml); },
                    delegate(FormPet candidate)
                    {
                        candidate.ShowInTaskbar = false;
                        candidate.Opacity = 0d;
                        candidate.Show(xml.spriteWidth, xml.spriteHeight);
                        candidate.Play(true);
                    });
                pet.Say(speech);
                Application.DoEvents();
                bool speechPainted =
                    pet.PaintSpeechForResourceChurn();
                using (var rendered = new Bitmap(
                    Math.Max(1, pet.Width),
                    Math.Max(1, pet.Height)))
                {
                    pet.DrawToBitmap(
                        rendered,
                        new Rectangle(Point.Empty, rendered.Size));
                }
                return speechPainted;
            }
            finally
            {
                if (pet != null)
                {
                    try { pet.Close(); } catch { }
                    try { pet.Dispose(); } catch { }
                    Application.DoEvents();
                }
            }
        }

            /// <summary>
            /// Close all sheeps on the desktop and eventually closes the application.
            /// </summary>
            /// <param name="exit">If true, the application will close after 1 second (leaving time to the sheeps to die).</param>
        public void KillSheeps(bool exit)
        {
            AddDebugInfo(DEBUG_TYPE.info, "Killing all sheeps");
            timer1.Tag = "0";
            pi.Dispose();

                // Leave open application to show some kill animations. 
                // Only if there is a pet on the desktop.
            if (iSheeps > 0)
            {
                for (int i = 0; i < iSheeps; i++)
                {
                    FormPet pet = sheeps[i];
                    sheeps[i] = null;
                    if (pet != null && !pet.IsDisposed)
                    {
                        TrackRetiringPet(pet);
                        pet.Kill();
                    }
                }
                iSheeps = 0;

                if (exit)
                {
                    timer1.Interval = 1100;
                    timer1.Enabled = true;
                }
            }
            else
            {
                timer1.Interval = 100;
                timer1.Enabled = true;
            }
        }


        /// <summary>
        /// Handles of every live sheep window, so the fullscreen scan can ignore the pets themselves
        /// (a sheep sitting on top of a borderless game must not be mistaken for the top window there).
        /// </summary>
        public HashSet<IntPtr> SheepHandles()
        {
            var handles = new HashSet<IntPtr>();
            for (int i = 0; i < iSheeps; i++)
            {
                FormPet sheep = sheeps[i];
                if (sheep != null && !sheep.IsDisposed && sheep.IsHandleCreated)
                    handles.Add(sheep.Handle);
            }
            return handles;
        }

        /// <summary>
        /// Bring every sheep to top most again
        /// </summary>
        public void TopMostSheeps()
        {
            AddDebugInfo(DEBUG_TYPE.info, "Top most all sheeps");

            for (int i = 0; i < iSheeps; i++)
            {
                sheeps[i].TopMost = true;
            }
        }

        /// <summary>
        /// Close a single sheep on the desktop.
        /// </summary>
        /// <param name="sheep">The sheep-form to close.</param>
        public bool KillSheep(FormPet sheep)
        {
            bool bSheepRemoved = false;

            AddDebugInfo(DEBUG_TYPE.info, "Kill one sheep");
            
            for (int i = 0; i < iSheeps; i++)
            {
                if(sheeps[i] == sheep)
                {
                    TrackRetiringPet(sheeps[i]);
                    sheeps[i].Kill();
                    for (int j = i; j < iSheeps - 1; j++) sheeps[j] = sheeps[j + 1];
                    iSheeps--;
                    sheeps[iSheeps] = null;
                    bSheepRemoved = true;
                    break;
                }
            }

            if (bSheepRemoved) PersistMix();   // remember the reduced on-screen mix for next launch

            /*
             * This will close application if all Sheeps are removed. But Maybe the user want see the try icon to add a sheep later.
             * Maybe in future you can choose 0 to 10 sheeps at startup, so this is commented out for the moment.
            if (iSheeps <= 0)
            {
                timer1.Tag = "0";
                pi.Dispose();

                timer1.Interval = 1100;
                timer1.Enabled = true;
            }
            */

            return bSheepRemoved;
        }

            /// <summary>
            /// Timer used to init and close the application.
            /// </summary>
            /// <param name="sender">Caller as object.</param>
            /// <param name="e">Timer event values.</param>
        private void Timer1_Tick(object sender, EventArgs e)
        {
            if (disposed) return;
            string state = timer1.Tag as string;
                // "A" when application starts. Add a sheep.
            if (state == "A")
            {
                if (spawnPlan == null)
                {
                    spawnPlan = BuildStartupSpawnPlan();
                    spawnPlanIndex = 0;
                }

                if (spawnPlanIndex < spawnPlan.Count && iSheeps < MAX_SHEEPS)
                {
                    if (iSheeps == 0)
                        AddDebugInfo(DEBUG_TYPE.info, "init application...");
                    // "" spawns the active/default pet; a folder id spawns that type alongside.
                    AddSheep(spawnPlan[spawnPlanIndex]);
                    spawnPlanIndex++;
                }
                else
                {
                    timer1.Enabled = false;
                    timer1.Tag = "B";
                    spawnPlan = null;
                    spawnPlanIndex = 0;
                }
            }
                // "0" when application should be stopped.
            else if (state == "0")
            {
                timer1.Tag = "1";
            }
            else
            {
                Application.Exit();
            }
        }
        
            /// <summary>
            /// Load new XML (from XML string).
            /// </summary>
            /// <param name="strXml">A string with the xml content.</param>
        public bool LoadNewXMLFromString(string strXml)
        {
            AddDebugInfo(DEBUG_TYPE.info, "load new XML string");
            if (disposed) return false;

            FormPet marshal = iSheeps > 0
                ? sheeps[0]
                : retiringPets.FirstOrDefault();
            if (marshal != null && marshal.InvokeRequired)
            {
                marshal.BeginInvoke(new MethodInvoker(delegate
                {
                    LoadNewXMLFromString(strXml);
                }));
                return true;
            }

            Xml stagedXml;
            Animations stagedAnimations;
            string error;
            // "Use this pet" -> the pet becomes the active/default type; callers persist activePetId first,
            // so key it by that real id (per-pet size/sound follow the pet).
            string useId = Program.MyData != null ? Program.MyData.GetActivePetId() : PetCatalog.BuiltInPetId;
            if (!TryStageRuntime(
                    strXml,
                    Program.MyData.GetEffectivePetScaleFactor(useId),
                    out stagedXml,
                    out stagedAnimations,
                    out error))
            {
                AddDebugInfo(DEBUG_TYPE.warning, "New pet rejected: " + error);
                return false;
            }
            stagedAnimations.PetTypeId = useId;

            timer1.Stop();
            Xml oldXml = xml;
            Animations oldAnimations = animations;
            string oldPersistedXml = Program.MyData.GetXml();
            string oldPersistedImages = Program.MyData.GetImages();
            string oldPersistedIcon = Program.MyData.GetIcon();
            bool persisted = false;
            try
            {
                // Complete every fallible activation/commit step before closing the current pets.
                stagedAnimations.Activate();
                ApplyTrayIcon(stagedXml);
                if (!Program.MyData.TrySetPetAssets(strXml, "", ""))
                    throw new IOException("The staged pet could not be saved atomically.");
                persisted = true;

                CloseAllPetsImmediate();
                // "Use this pet" resets the desktop to a single active type: drop every extra type
                // (their pets' FormClosed already released most; this clears any stragglers). Never
                // touches the staged or old active pair -- neither is in the registry.
                registry.DisposeAll();
                petEntries.Clear();
                xml = stagedXml;
                animations = stagedAnimations;
                // Reset the persisted mix to "just the active pet": all pets are closed here (iSheeps==0
                // -> empty mix), so the re-armed autostart below respawns GetAutoStartPets() copies of
                // the new active pet, and next launch restores the same.
                spawnPlan = null;
                spawnPlanIndex = 0;
                PersistMix();
                timer1.Tag = "A";
                timer1.Interval = 1000;
                timer1.Start();
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.error, "Could not activate the staged pet: " + ex.Message);
                if (persisted)
                    Program.MyData.TrySetPetAssets(
                        oldPersistedXml,
                        oldPersistedImages,
                        oldPersistedIcon);
                animations = oldAnimations;
                xml = oldXml;
                if (animations != null) animations.Activate();
                ApplyTrayIcon(xml);
                stagedAnimations.Dispose();
                stagedXml.Dispose();
                timer1.Tag = "A";
                timer1.Start();
                return false;
            }

            if (oldAnimations != null) oldAnimations.Dispose();
            if (oldXml != null) oldXml.Dispose();
            return true;
        }

        private void CloseAllPetsImmediate()
        {
            var ownedPets = new HashSet<FormPet>();
            for (int i = 0; i < sheeps.Length; i++)
            {
                FormPet pet = sheeps[i];
                sheeps[i] = null;
                if (pet != null) ownedPets.Add(pet);
            }
            foreach (FormPet pet in retiringPets.Drain())
            {
                if (pet == null) continue;
                pet.FormClosed -= RetiringPet_FormClosed;
                ownedPets.Add(pet);
            }
            foreach (FormPet pet in ownedPets)
            {
                if (pet.IsDisposed) continue;
                try { pet.Close(); } catch { }
                try { pet.Dispose(); } catch { }
            }
            iSheeps = 0;
        }

        private void TrackRetiringPet(FormPet pet)
        {
            if (pet == null || pet.IsDisposed || !retiringPets.Add(pet))
                return;
            pet.FormClosed += RetiringPet_FormClosed;
            if (pet.IsDisposed)
                RetiringPet_FormClosed(pet, null);
        }

        private void RetiringPet_FormClosed(object sender, FormClosedEventArgs e)
        {
            FormPet pet = sender as FormPet;
            if (pet == null) return;
            pet.FormClosed -= RetiringPet_FormClosed;
            retiringPets.Remove(pet);
        }

            /// <summary>
            /// Returns the Animation class.
            /// </summary>
            /// <returns>Member variable to access all animations of the current pet.</returns>
        public Animations GetAnimations()
        {
            return animations;
        }

            /// <summary>
            /// If the application is started with the SHIFT key pressed, warnings and errors are reported on a window.
            /// </summary>
            /// <param name="type">See <see cref="StartUp.DEBUG_TYPE"/> for the possible values. </param>
            /// <param name="text">Text to show in the dialog window.</param>
        public static void AddDebugInfo(DEBUG_TYPE type, string text)
        {
            FormDebug target;
            lock (debugLock) target = debug;
            if (target == null || target.IsDisposed || target.Disposing) return;
            try
            {
                MethodInvoker append = delegate
                {
                    if (!target.IsDisposed && !target.Disposing)
                        target.AddDebugInfo(type, text);
                };
                if (target.InvokeRequired)
                {
                    if (target.IsHandleCreated) target.BeginInvoke(append);
                }
                else
                {
                    append();
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

            /// <summary>
            /// If the application is started with the SHIFT key pressed, some extra features are activated.
            /// </summary>
            /// <returns>true if the application is running with debug window.</returns>
        public static bool IsDebugActive()
        {
            lock (debugLock)
                return debug != null && !debug.IsDisposed && !debug.Disposing;
        }

        /// <summary>
        /// Show a speech bubble above every active pet.
        /// Does nothing when speech bubbles are disabled in Options.
        /// </summary>
        private string _lastSaidAll;   // last broadcast remark, for the optional back-to-back repeat guard

        public void SayAll(string text)
        {
            // Master repeat guard (Preferences): any speaker — AI brain, fortunes, welcome — broadcasts
            // through here, so suppressing a line identical to the one just said covers every module. Only
            // track/compare lines with real content, so a transient "…" thinking cue between two remarks
            // doesn't reset the guard (which would let quip / … / quip slip through as "not back-to-back").
            string trimmed = (text ?? "").Trim();
            if (HasContent(trimmed))
            {
                bool dupe = string.Equals(trimmed, _lastSaidAll, StringComparison.OrdinalIgnoreCase);
                _lastSaidAll = trimmed;
                if (dupe)
                {
                    try { if (Program.MyData != null && Program.MyData.GetSuppressRepeats()) return; }
                    catch { }
                }
            }
            for (int i = 0; i < iSheeps; i++)
                sheeps[i].Say(text);
        }

        // Real content = at least one letter or digit; "…"/punctuation-only cues are transient and ignored.
        private static bool HasContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        /// <summary>
        /// Rebuild the smart-fortune weight vectors for the current selection: reloads settings,
        /// rebuilds the filtered pool, and re-warms the embedder (re-embeds any new lines from the
        /// cache, recomputes the pool mean/centering). Background; leaves the AI brain untouched.
        /// </summary>
        public void RebuildSmartFortunes()
        {
            try
            {
                aiConfig = AiSettings.Load();
                ApplyRandomDrop();   // fortunes moved to the module (S3d); just resync the drop timer (settings.json now owns the cadence)
            }
            catch (Exception ex) { AddDebugInfo(DEBUG_TYPE.warning, "smart-fortune rebuild failed: " + ex.Message); }
        }


        /// <summary>
        /// (Re)arm the random-drop timer from the current settings. Idempotent: safe at launch and on
        /// every settings change. A stale timer is retired via the ReferenceEquals guard below.
        /// </summary>
        private void ApplyRandomDrop()
        {
            if (dropTimer != null)
            {
                dropTimer.Stop();
                if (dropTimerHandler != null) dropTimer.Tick -= dropTimerHandler;
                dropTimer.Dispose();
                dropTimer = null;
                dropTimerHandler = null;
            }
            if (disposed || Program.MyData == null || !Program.MyData.GetRandomDropEnabled()) return;

            var timer = new System.Windows.Forms.Timer();
            EventHandler handler = null;
            handler = delegate { DropTimer_Tick(timer); };
            timer.Tick += handler;
            dropTimer = timer;
            dropTimerHandler = handler;
            ScheduleDrop(timer);
        }

        /// <summary>Arm the drop timer for a fresh random interval of center ± jitter minutes.</summary>
        private void ScheduleDrop(System.Windows.Forms.Timer timer)
        {
            if (timer == null || disposed || Program.MyData == null ||
                !ReferenceEquals(dropTimer, timer))
                return;
            int center = Math.Min(9999, Math.Max(1, Program.MyData.GetRandomDropMinutes()));
            int jitter = Math.Min(center - 1, Math.Max(0, Program.MyData.GetRandomDropJitterMinutes()));
            int minutes = aiRand.Next(center - jitter, center + jitter + 1);
            timer.Interval = Math.Max(60000, minutes * 60000);   // at least one minute
            timer.Start();
        }

        /// <summary>
        /// Random-drop tick: when a pet is present, speech is on, and no pet is busy, speak an AI
        /// insight if the brain is enabled, otherwise a fortune. Then reschedule a fresh interval.
        /// </summary>
        private void DropTimer_Tick(System.Windows.Forms.Timer timer)
        {
            if (!ReferenceEquals(dropTimer, timer)) return;
            timer.Stop();
            try
            {
                if (iSheeps > 0 && Program.MyData.GetSpeechEnabled() && !AnyPetBusy())
                {
                    // Always raise the arbitrated drop tick: the AI-brain module's drop responder takes it as
                    // an AI insight when its brain is enabled, otherwise the Fortunes module speaks. The base
                    // no longer drives the AI brain itself (S4b) — that moved to the AiBrain module.
                    if (Host != null) Host.RaiseDropTick();
                }
            }
            catch { /* a single missed drop must never crash the pet */ }
            ScheduleDrop(timer);
        }


        /// <summary>
        /// The pet was right-clicked ("poked"). Timing-based escalation: a pause resets it; rapid
        /// pokes climb from fortunes -> being ignored -> verbal sass -> a bathtub escape.
        /// (Poke 1 becomes an AI insight when a brain is configured — wired in Phase C.)
        /// </summary>
        public void OnPetPoked()
        {
            if (iSheeps == 0 || !Program.MyData.GetSpeechEnabled()) return;

            DateTime now = DateTime.UtcNow;
            if ((now - lastPokeUtc).TotalSeconds > PokeResetSeconds) pokeCount = 0;
            lastPokeUtc = now;
            pokeCount++;
            if (Host != null && iSheeps > 0) Host.RaisePetPoked(sheeps[0], pokeCount);

            if (pokeCount >= PokeEscapeAt)          // 12: the finale
            {
                pokeCount = 0;                      // reset after escaping
                if (!EscapeAllToBath() && Host != null) Host.RaiseDropTick();   // no bath spawn -> a fortune (module)
                return;
            }
            if (pokeCount >= PokeSassFrom)          // 5-11: verbal sass
            {
                string s = PokeReactions.RandomSass();
                if (!string.IsNullOrWhiteSpace(s)) SayAll(s);
                return;
            }
            if (pokeCount >= PokeIgnoreFrom)        // 3-4: ignore — turn away, no bubble
            {
                PlayFirstAnimation("rotate1a", "look_down", "sleep1a");
                return;
            }
            // 1-2: a fortune - spoken by the Fortunes module via the PetPoked event raised above.
        }

        /// <summary>Flee via the bathtub spawn on every pet. True if at least one could.</summary>
        private bool EscapeAllToBath()
        {
            bool any = false;
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && sheeps[i].EscapeToBath()) any = true;
            return any;
        }

        /// <summary>Play the first of the named animations that each pet actually defines.</summary>
        private void PlayFirstAnimation(params string[] names)
        {
            for (int i = 0; i < iSheeps; i++)
            {
                FormPet pet = sheeps[i];
                if (pet == null) continue;
                foreach (string n in names)
                    if (pet.TryPlayAnimation(n)) break;
            }
        }

        /// <summary>
        /// Land greeting: wait for the spawned pet to stop falling, then speak a fortune. The pet is
        /// descending only while its Y grows, so once Y stops increasing for a couple of polls it has
        /// landed (or is walking/climbing). Fires after ~0.5s of no descent, with a ~10s safety cap.
        /// </summary>
        private void LandTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                landTicks++;
                FormPet pet = (iSheeps > 0) ? sheeps[0] : null;
                if (pet == null)
                {
                    if (landTicks > 40 && landTimer != null) landTimer.Stop();   // no pet after ~10s: give up
                    return;
                }

                int y = pet.Top;
                if (landPrevY != int.MinValue && y <= landPrevY) landStable++; else landStable = 0;
                landPrevY = y;

                if ((landStable >= 2 && landTicks >= 3) || landTicks >= 40)
                {
                    if (landTimer != null) landTimer.Stop();
                    if (Host != null) Host.RaisePetLanded(pet);   // the Fortunes module speaks the land fortune
                }
            }
            // Never let a fortune/speech throw escape a WinForms timer tick as an unhandled UI exception.
            catch { try { if (landTimer != null) landTimer.Stop(); } catch { } }
        }

        /// <summary>
        /// Play a prioritized set of candidate animations on every live pet: for each pet, the first
        /// candidate its XML actually defines is played (<see cref="FormPet.TryPlayAnimation"/>). The
        /// caller owns any emotion->candidate-names mapping. This backs the plugin host's
        /// <c>PlayAnimationAll</c> service, so a module (the AI brain) can emote every pet without
        /// owning the live-pet list — the same reason <see cref="SayAll"/> is a host service. Must run
        /// on the UI thread. Never throws — the pet physics engine must never be disturbed.
        /// </summary>
        internal void PlayAnimationOnAll(System.Collections.Generic.IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;
            try
            {
                for (int i = 0; i < iSheeps; i++)
                {
                    FormPet pet = sheeps[i];
                    if (pet == null) continue;
                    foreach (string name in candidates)
                        if (pet.TryPlayAnimation(name)) break;   // first defined candidate wins
                }
            }
            catch { }
        }

        /// <summary>
        /// Launch-time setup for the base's residual AI-layer seam: load the AI settings, arm the
        /// shared random-drop timer (the Fortunes module speaks the drop), retire any prior brain,
        /// and start the land-greeting poll. The base never builds a brain (the AiBrain module owns
        /// it); any failure here is non-fatal — the pet still runs.
        /// </summary>
        private void InitAiTriggers()
        {
            try
            {
                aiConfig = AiSettings.Load();

                // Fortunes moved to the Fortunes module (S3d); the base only arms the shared random-drop
                // timer here (the module's fortune drop responder speaks it). Cadence lives in settings.json.
                ApplyRandomDrop();

                // Retire any prior base brain. The base never builds a brain (the AiBrain module owns it),
                // so no provider is ever contacted on launch.
                ApplyAiBrainState();

                // Land greeting: poll the pet's fall and speak a fortune only once it has settled,
                // so the first bubble never appears mid-air.
                landTimer = new System.Windows.Forms.Timer();
                landTimer.Interval = 250;
                landTimer.Tick += LandTimer_Tick;
                landTimer.Start();
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "AI triggers init failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-apply the AI-layer settings while the pet is running — called by the options
        /// shell when it closes. Reloads the JSON, resyncs the random-drop timer, and retires
        /// any prior base brain (clearing history when memory is off). UI thread; never throws.
        /// </summary>
        public void ReloadAiSettings()
        {
            try
            {
                aiConfig = AiSettings.Load();
                ApplyRandomDrop();   // fortunes moved to the module (S3d); resync the drop timer (settings.json owns the cadence)
                ApplyAiBrainState(!aiConfig.MemoryEnabled);
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "AI settings reload failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Cancel and retire the active AI generation, delete all persisted conversation memory,
        /// then construct a fresh generation from the current settings.
        /// </summary>
        internal Task<ChatHistoryDeleteResult> ClearAiHistory()
        {
            var completion =
                new TaskCompletionSource<ChatHistoryDeleteResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (aiConfig == null) aiConfig = AiSettings.Load();
                ApplyAiBrainState(true, completion);
            }
            catch (Exception ex)
            {
                AddDebugInfo(
                    DEBUG_TYPE.warning,
                    "AI history clear failed: " + ex.Message);
                completion.TrySetResult(
                    ChatHistoryDeleteResult.Failure(ex.Message));
            }
            return completion.Task;
        }

        /// <summary>
        /// Keeps ownership local until a newly constructed form is fully initialized. If showing or
        /// starting the form fails, no array slot owns it, so close and dispose it before rethrowing.
        /// </summary>
        internal static FormPet CreateAndInitializeOwnedPet(
            Func<FormPet> create,
            Action<FormPet> initialize)
        {
            if (create == null) throw new ArgumentNullException("create");
            if (initialize == null) throw new ArgumentNullException("initialize");

            FormPet pet = null;
            try
            {
                pet = create();
                if (pet == null)
                    throw new InvalidOperationException(
                        "The pet form factory returned no form.");
                initialize(pet);
                return pet;
            }
            catch
            {
                if (pet != null)
                {
                    try { pet.Close(); } catch { }
                    try { pet.Dispose(); } catch { }
                }
                throw;
            }
        }

        /// <summary>Human-readable smart-fortunes state for the Options UI. Fortunes moved to the Fortunes
        /// module (S3); the base no longer runs the engine, so this is a static placeholder until the WPF
        /// module-manager surfaces the module's own status (S5).</summary>
        public string SmartFortunesStatus()
        {
            return "Fortunes are provided by the Fortunes module.";
        }

        /// <summary>Retire any prior base brain (residual seam; the base never builds one).</summary>
        private void ApplyAiBrainState()
        {
            ApplyAiBrainState(false);
        }

        private void ApplyAiBrainState(bool clearHistoryAfterRetire)
        {
            ApplyAiBrainState(clearHistoryAfterRetire, null);
        }

        private void ApplyAiBrainState(
            bool clearHistoryAfterRetire,
            TaskCompletionSource<ChatHistoryDeleteResult> historyClearCompletion)
        {
            // S4b + residual strip: the AiBrain module owns the brain now. The base never builds or runs
            // its own brain; this path only RETIRES any prior base brain (and clears history on request),
            // so a base brain and the module brain can never both be live.
            int version = Interlocked.Increment(ref aiConfigurationVersion);
            Action afterRetire = null;
            if (clearHistoryAfterRetire)
            {
                ChatHistoryDeleteResult request =
                    ChatHistory.RequestPersistedDeletion();
                if (!request.Pending)
                {
                    AddDebugInfo(
                        DEBUG_TYPE.warning,
                        "AI history deletion request failed: " + request.Error);
                    if (historyClearCompletion != null)
                        historyClearCompletion.TrySetResult(request);
                }
                afterRetire = delegate
                {
                    ChatHistoryDeleteResult result =
                        ChatHistory.DeletePersisted();
                    if (!result.Succeeded)
                        AddDebugInfo(
                            DEBUG_TYPE.warning,
                            "AI history deletion incomplete: " + result.Error);
                    if (historyClearCompletion != null)
                        historyClearCompletion.TrySetResult(result);
                };
            }
            Task<bool> configure = aiSession.ReconfigureAsync(
                null,
                false,
                false,
                lifetimeCancellation.Token,
                afterRetire);
            configure.ContinueWith(
                delegate(Task<bool> completed)
                {
                    if (version != Volatile.Read(ref aiConfigurationVersion)) return;
                    if (completed.IsFaulted)
                        AddDebugInfo(
                            DEBUG_TYPE.warning,
                            "AI configuration failed: " +
                            completed.Exception.GetBaseException().Message);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>True if any pet is currently being handled by the user (drop gate).</summary>
        private bool AnyPetBusy()
        {
            for (int i = 0; i < iSheeps; i++)
                if (sheeps[i] != null && sheeps[i].IsBusy) return true;
            return false;
        }

        /// <summary>
        /// Calling this function, all sheeps will execute the same animation (if the sync-word is present in the XML).
        /// </summary>
        public void SyncSheeps()
        {
            AddDebugInfo(DEBUG_TYPE.info, "synchronize sheeps");
            for (int i = 0; i < iSheeps; i++)
            {
                sheeps[i].Sync();
            }
        }
    }

    /// <summary>
    /// Owns one asynchronously-built value per monotonically increasing generation. Publication is
    /// atomic, stale candidates are released, and cancellation never makes an older value current.
    /// </summary>
    internal sealed class GenerationOwnedValue<T> : IDisposable where T : class
    {
        private sealed class Work
        {
            internal int Generation;
            internal CancellationTokenSource Cancellation;
            internal CancellationToken Token;
            internal Task Task;
        }

        private readonly object _sync = new object();
        private readonly CancellationToken _lifetimeToken;
        private readonly Action<T> _release;
        private readonly Action<Exception> _reportFailure;
        private readonly Action<T, TimeSpan> _releaseWithin;
        private readonly List<Task> _tasks = new List<Task>();
        private Work _currentWork;
        private T _current;
        private int _generation;
        private int _publishedGeneration = -1;
        private bool _disposed;

        internal GenerationOwnedValue(
            CancellationToken lifetimeToken,
            Action<T> release,
            Action<Exception> reportFailure,
            Action<T, TimeSpan> releaseWithin = null)
        {
            _lifetimeToken = lifetimeToken;
            _release = release ?? throw new ArgumentNullException("release");
            _reportFailure = reportFailure;
            _releaseWithin = releaseWithin;
        }

        internal Task Start(Func<CancellationToken, T> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            var work = new Work
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeToken)
            };
            work.Token = work.Cancellation.Token;
            Work previousWork;
            lock (_sync)
            {
                if (_disposed)
                {
                    work.Cancellation.Dispose();
                    throw new ObjectDisposedException(
                        typeof(GenerationOwnedValue<T>).Name);
                }
                work.Generation = ++_generation;
                work.Task = new Task(
                    delegate { Run(work, factory); },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach);
                _tasks.Add(work.Task);
                previousWork = _currentWork;
                _currentWork = work;
            }

            CancelAndDisposeWhenComplete(previousWork);
            try
            {
                work.Task.Start(TaskScheduler.Default);
                return work.Task;
            }
            catch
            {
                lock (_sync)
                {
                    _tasks.Remove(work.Task);
                    if (ReferenceEquals(_currentWork, work))
                        _currentWork = null;
                }
                Cancel(work.Cancellation);
                work.Cancellation.Dispose();
                throw;
            }
        }

        private void Run(Work work, Func<CancellationToken, T> factory)
        {
            T candidate = null;
            T retired = null;
            try
            {
                CancellationToken token = work.Token;
                token.ThrowIfCancellationRequested();
                candidate = factory(token);
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (!_disposed &&
                        work.Generation == _generation &&
                        ReferenceEquals(work, _currentWork) &&
                        candidate != null)
                    {
                        retired = _current;
                        _current = candidate;
                        _publishedGeneration = work.Generation;
                        candidate = null;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                if (!work.Token.IsCancellationRequested)
                    Report(ex);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                SafeRelease(candidate);
                SafeRelease(retired);
                lock (_sync) _tasks.Remove(work.Task);
            }
        }

        internal bool TryGetCurrent(out T value)
        {
            lock (_sync)
            {
                if (!_disposed &&
                    _publishedGeneration == _generation &&
                    _current != null)
                {
                    value = _current;
                    return true;
                }
                value = null;
                return false;
            }
        }

        internal void Shutdown(TimeSpan wait)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Work work;
            T value;
            Task[] tasks;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                _publishedGeneration = -1;
                work = _currentWork;
                _currentWork = null;
                value = _current;
                _current = null;
                tasks = _tasks.ToArray();
            }

            CancelAndDisposeWhenComplete(work);
            if (tasks.Length > 0)
            {
                try
                {
                    int milliseconds = wait <= TimeSpan.Zero
                        ? 0
                        : (int)Math.Min(int.MaxValue, wait.TotalMilliseconds);
                    Task.WaitAll(tasks, milliseconds);
                }
                catch (AggregateException) { }
                catch (OperationCanceledException) { }
            }
            SafeRelease(
                value,
                StartUp.RemainingShutdownBudget(wait, stopwatch.Elapsed));
        }

        public void Dispose()
        {
            Shutdown(TimeSpan.FromSeconds(3));
        }

        private void SafeRelease(T value)
        {
            if (value == null) return;
            try { _release(value); }
            catch (Exception ex) { Report(ex); }
        }

        private void SafeRelease(T value, TimeSpan wait)
        {
            if (value == null) return;
            if (_releaseWithin == null)
            {
                SafeRelease(value);
                return;
            }
            try { _releaseWithin(value, wait); }
            catch (Exception ex) { Report(ex); }
        }

        private void Report(Exception failure)
        {
            if (failure == null || _reportFailure == null) return;
            try { _reportFailure(failure); } catch { }
        }

        private static void CancelAndDisposeWhenComplete(Work work)
        {
            if (work == null) return;
            Cancel(work.Cancellation);
            if (work.Task == null || work.Task.IsCompleted)
            {
                work.Cancellation.Dispose();
                return;
            }
            work.Task.ContinueWith(
                delegate { work.Cancellation.Dispose(); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void Cancel(
            CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            try { cancellation.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Small generation gate shared by idle initial-arm, tick admission, and finally-rearm paths.
    /// Reconfiguration invalidates every pending operation from the prior policy generation.
    /// </summary>
    internal sealed class GenerationAwareIdleSchedule
    {
        private readonly object _sync = new object();
        private int _generation;
        private bool _enabled;
        private bool _armed;

        internal void Reconfigure(int generation, bool enabled)
        {
            lock (_sync)
            {
                _generation = generation;
                _enabled = enabled;
                _armed = false;
            }
        }

        internal bool TryArm(int expectedGeneration)
        {
            lock (_sync)
            {
                if (!CanRunLocked(expectedGeneration) || _armed)
                    return false;
                _armed = true;
                return true;
            }
        }

        internal bool TryBeginTick(int expectedGeneration)
        {
            lock (_sync)
            {
                if (!CanRunLocked(expectedGeneration) || !_armed)
                    return false;
                _armed = false;
                return true;
            }
        }

        internal bool CanRun(int expectedGeneration)
        {
            lock (_sync)
                return CanRunLocked(expectedGeneration);
        }

        internal bool IsArmedForDiagnostics
        {
            get { lock (_sync) return _armed; }
        }

        private bool CanRunLocked(int expectedGeneration)
        {
            return _enabled && expectedGeneration == _generation;
        }
    }
}
