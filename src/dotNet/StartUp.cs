using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopPet.Ai;
using static DesktopPet.StartUp;

#if !PORTABLE
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
#endif

namespace DesktopPet
{
    /// <summary>
    /// StartUp class. This class will initialize the entire application and define some constants.
    /// </summary>
    public sealed class StartUp : IDisposable
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
        static public System.Windows.Forms.Timer timer1 = new System.Windows.Forms.Timer();

        /// <summary>
        /// Each sheep is in a different form.
        /// </summary>
        readonly FormPet[] sheeps = new FormPet[MAX_SHEEPS];

        /// <summary>
        /// Debug window, used only if SHIFT was pressed by starting the application.
        /// </summary>
        static FormDebug debug = null;

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

        /// <summary>
        /// Process Icon. The tray icon on the taskbar.
        /// </summary>
        readonly ProcessIcon pi;

        bool isRealoadingSettings = false;

        /// <summary>
        /// AI brain (lazy-created on first use). Owns the Ollama backend and the
        /// capture -> OCR/vision -> response pipeline. Purely additive to the engine.
        /// </summary>
        AiBrain aiBrain;

        /// <summary>Cached AI-layer settings (loaded once at startup).</summary>
        AiSettings aiConfig;

        /// <summary>Bundled fortunes (offline default response). Lazily built from the corpus.</summary>
        FortuneProvider fortunes;

        // Poke-escalation state (right-clicking the sheep). Thresholds are tunable; the sass lines
        // live in PokeReactions so more can be slotted in later.
        int pokeCount;
        DateTime lastPokeUtc = DateTime.MinValue;
        const double PokeResetSeconds = 7.0;   // a pause this long starts a fresh poke session
        const int PokeIgnoreFrom = 3;          // pokes 3-4: ignore (turn away, no words)
        const int PokeSassFrom   = 5;          // pokes 5-11: verbal sass
        const int PokeEscapeAt   = 12;         // poke 12: bathtub escape

        /// <summary>One-shot "just landed" fortune timer (fires shortly after launch).</summary>
        System.Windows.Forms.Timer landTimer;

        /// <summary>Global hotkey that fires the reactive ask (phase 3.1).</summary>
        HotkeyListener aiHotkey;

        /// <summary>Idle-commentary timer (phase 3.4). Null when idle commentary is disabled.</summary>
        System.Windows.Forms.Timer aiIdleTimer;

        /// <summary>UTC of the last AI interaction, used by the idle gate (phase 3.5).</summary>
        DateTime aiLastInteractionUtc = DateTime.MinValue;

        /// <summary>Random source for the jittered idle interval.</summary>
        readonly Random aiRand = new Random();

        /// <summary>
        /// True once the AI backend has been confirmed reachable (launch warmup or a successful
        /// ask). The pet's right-click greeting reads this to point first-run users at the tray to
        /// set up the AI brain. Written from background threads, read on the UI thread.
        /// </summary>
        volatile bool aiReady;

        /// <summary>Whether the local AI backend is reachable (best-effort). See <see cref="aiReady"/>.</summary>
        public bool AiReady { get { return aiReady; } }

        /// <summary>
        /// Error message for exceptions. It is shown in the options if an error occurs.
        /// </summary>
        public struct TError
        {
                /// <summary>
                /// The message to show in the option dialog.
                /// </summary>
            public string AudioErrorMessage;
        }

        /// <summary>
        /// Error messages (used for debug), visible in the option dialog.
        /// </summary>
        public TError ErrorMessages;
        
        /// <summary>
        /// Constructor. Called when application is started.
        /// </summary>
        /// <param name="processIcon">ProcessIcon class, to change icon when a new pet is selected.</param>
        public StartUp(ProcessIcon processIcon)
        {
            pi = processIcon;
                        
                // Init XML class
            xml = new Xml((int)Math.Pow(2, Program.MyData.GetScale() - 1));
                // Init Animations class
            animations = new Animations(xml);

                // If SHIFT key was pressed, open Debug window
            Keys ks = Control.ModifierKeys;
            if (ks == Keys.Shift)
            {
                debug = new FormDebug();
                debug.Show();
                AddDebugInfo(DEBUG_TYPE.info, "debug window started");
            }
            
                // Read XML file and start new sheep in 1 second
            if(!xml.ReadXML())
            {
                Program.MyData.SetXml(Properties.Resources.animations, "esheep64");
                xml.ReadXML();
            }

                // Set animation icon
            pi.SetIcon(xml.bitmapIcon, 
                        xml.AnimationXML.Header.Petname, 
                        xml.AnimationXML.Header.Author, 
                        xml.AnimationXML.Header.Title, 
                        xml.AnimationXML.Header.Version, 
                        xml.AnimationXML.Header.Info
                        );

                // Wait 1 second, before starting first animation
            timer1.Tag = "A";
            timer1.Tick += new EventHandler(Timer1_Tick);
            timer1.Interval = 1000;
            timer1.Enabled = true;

            Program.MyData.ListenOnXMLChanged(XmlFileChanged);
            Program.MyData.ListenOnOptionsChanged(OptionFileChanged);

            InitAiTriggers();
        }


        private void XmlFileChanged(object source, FileSystemEventArgs e)
        {
            Thread.Sleep(200);
            Program.MyData.LoadXML();
            Program.Mainthread.LoadNewXMLFromString(Program.MyData.GetXml());
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
            xml.Dispose();
            pi.Dispose();
            if (aiHotkey != null) aiHotkey.Dispose();
            if (aiIdleTimer != null) { aiIdleTimer.Stop(); aiIdleTimer.Dispose(); }
            if (landTimer != null) { landTimer.Stop(); landTimer.Dispose(); }
            if (aiBrain != null) aiBrain.Dispose();
        }
        
            /// <summary>
            /// Calling this function will add another sheep on the desktop, if MAX_SHEEP was not reached.
            /// </summary>
        public void AddSheep()
        {
            if (iSheeps < MAX_SHEEPS)
            {
                var newSheep = new FormPet(animations, xml);
                foreach (var sprite in xml.sprites)
                {
                    newSheep.AddImage(sprite);
                }
                sheeps[iSheeps] = newSheep;
                sheeps[iSheeps].Show(xml.spriteWidth, xml.spriteHeight);
                AddDebugInfo(DEBUG_TYPE.info, "new pet...");
                AddDebugInfo(DEBUG_TYPE.info, xml.sprites.Count.ToString() + " frames added");
                
                    // Start the animation of the pet
                sheeps[iSheeps].Play(true);
                iSheeps++;
            }
            else
            {
                AddDebugInfo(DEBUG_TYPE.warning, "max PETs reached");
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
                Random rand = new Random();
                for (int i = 0; i < iSheeps; i++)
                {
                    Thread.Sleep(rand.Next(100, 200));
                    sheeps[i].Kill();
                    Application.DoEvents();
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
                    sheeps[i].Kill();
                    for (int j = i; j < iSheeps - 1; j++) sheeps[j] = sheeps[j + 1];
                    iSheeps--;
                    Application.DoEvents();
                    bSheepRemoved = true;
                    break;
                }
            }

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
                // "A" when application starts. Add a sheep.
            if (timer1.Tag.ToString() == "A")
            {
				if (iSheeps < Program.MyData.GetAutoStartPets() && iSheeps < MAX_SHEEPS)
				{
					if (iSheeps == 0)
					{
						AddDebugInfo(DEBUG_TYPE.info, "init application...");
						xml.LoadAnimations(animations);
					}

					AddSheep();
				}
				else
				{
					timer1.Enabled = false;
					timer1.Tag = "B";
				}
            }
                // "0" when application should be stopped.
            else if (timer1.Tag.ToString() == "0")
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
        public void LoadNewXMLFromString(string strXml)
        {
            AddDebugInfo(DEBUG_TYPE.info, "load new XML string");

            if (sheeps[0].InvokeRequired)
            {
                sheeps[0].BeginInvoke(new MethodInvoker(delegate{
                    LoadNewXMLFromString(strXml);
                }));
                return;
            }

            // Close all sheeps
            for (int i = 0; i < iSheeps; i++)
            {
                sheeps[i].Kill();
                /*
                sheeps[i].Close();
                sheeps[i].Dispose();
                */
            }
            iSheeps = 0;

                // reload XML and Animations
            xml = new Xml(Program.MyData.GetScale());
            animations = new Animations(xml);
                        
            if (!xml.ReadXML())
            {
                Program.MyData.SetXml(Properties.Resources.animations, "esheep64");
                xml.ReadXML();
            }

            pi.SetIcon(
                xml.bitmapIcon, 
                xml.AnimationXML.Header.Petname, 
                xml.AnimationXML.Header.Author, 
                xml.AnimationXML.Header.Title, 
                xml.AnimationXML.Header.Version, 
                xml.AnimationXML.Header.Info);

                // start animation in 1 second.
            timer1.Tag = "A";
            timer1.Enabled = true;
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
            if(debug != null)
            {
                if (debug.InvokeRequired)
                {
                    debug.BeginInvoke(new MethodInvoker(delegate {
                        debug.AddDebugInfo(type, text);
                    }));
                }
                else
                {
                    debug.AddDebugInfo(type, text);
                }
            }
        }

            /// <summary>
            /// If the application is started with the SHIFT key pressed, some extra features are activated.
            /// </summary>
            /// <returns>true if the application is running with debug window.</returns>
        public static bool IsDebugActive()
        {
            return (debug != null);
        }

        /// <summary>
        /// Show a speech bubble above every active pet.
        /// Does nothing when speech bubbles are disabled in Options.
        /// </summary>
        public void SayAll(string text)
        {
            for (int i = 0; i < iSheeps; i++)
                sheeps[i].Say(text);
        }

        /// <summary>Lazily build the fortune provider from the current corpus setting (SFW/Spicy).</summary>
        private FortuneProvider EnsureFortunes()
        {
            if (aiConfig == null) aiConfig = AiSettings.Load();
            if (fortunes == null) fortunes = new FortuneProvider(aiConfig.SpicyFortunes);
            return fortunes;
        }

        /// <summary>Speak a random fortune — the always-available, offline default response.</summary>
        public void SayFortune()
        {
            if (iSheeps == 0 || !Properties.Settings.Default.SpeechEnabled) return;
            string f = EnsureFortunes().Pick();
            if (!string.IsNullOrWhiteSpace(f)) SayAll(f);
        }

        /// <summary>
        /// The pet was right-clicked ("poked"). Timing-based escalation: a pause resets it; rapid
        /// pokes climb from fortunes -> being ignored -> verbal sass -> a bathtub escape.
        /// (Poke 1 becomes an AI insight when a brain is configured — wired in Phase C.)
        /// </summary>
        public void OnPetPoked()
        {
            if (iSheeps == 0 || !Properties.Settings.Default.SpeechEnabled) return;

            DateTime now = DateTime.UtcNow;
            if ((now - lastPokeUtc).TotalSeconds > PokeResetSeconds) pokeCount = 0;
            lastPokeUtc = now;
            pokeCount++;

            if (pokeCount >= PokeEscapeAt)          // 12: the finale
            {
                pokeCount = 0;                      // reset after escaping
                if (!EscapeAllToBath()) SayFortune();   // fall back if this pet has no bath spawn
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
            SayFortune();                           // 1-2: a fortune
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

        /// <summary>Land greeting: a fortune a few seconds after launch, once the pet has settled.</summary>
        private void LandTimer_Tick(object sender, EventArgs e)
        {
            if (landTimer != null) landTimer.Stop();
            SayFortune();
        }

        /// <summary>
        /// Ask the AI brain to look at the screen and have the pets speak its reaction.
        /// Fire-and-forget: stays silent if Ollama is unavailable, marshals the answer back
        /// to the UI thread. The emotion hint is captured for the (upcoming) animation mapping.
        /// </summary>
        public async void AskAboutScreen(bool allowVision = true)
        {
            if (iSheeps == 0) return;
            if (!Properties.Settings.Default.SpeechEnabled) return;

            aiLastInteractionUtc = DateTime.UtcNow;
            AiBrain brain = EnsureBrain();

            // Screen-zone awareness (5.6): capture (on the UI thread) which window the pet stands on.
            string petZone = (iSheeps > 0 && sheeps[0] != null) ? sheeps[0].WindowUnderPet : null;

            EmoteAll("thinking");   // backlog 3.6: a "pondering" cue while the model responds
            SayAll("…");            // ellipsis placeholder alongside it

            BrainResponse r = await brain.AskAboutScreenAsync(petZone, allowVision).ConfigureAwait(false);
            if (r == null || string.IsNullOrWhiteSpace(r.Text)) return;

            aiReady = true;   // a response came back, so the backend + model are working

            // backlog 2.8: map the emotion hint to an animation, then speak — both on the UI thread.
            FormPet ui = sheeps[0];
            MethodInvoker apply = delegate { EmoteAll(r.Emotion); SayAll(r.Text); };
            if (ui != null && ui.InvokeRequired)
                ui.BeginInvoke(apply);
            else
                apply();
        }

        /// <summary>
        /// Backlog 2.8 — map an emotion hint to an animation and play it on every pet.
        /// Each emotion resolves to a prioritized list of candidate animation names; the first
        /// one the pet's XML actually defines is played (<see cref="FormPet.TryPlayAnimation"/>).
        /// Unknown or "neutral" emotions play nothing, so the pet just keeps roaming. Must run on
        /// the UI thread. Never throws — the AI layer must never disturb the physics engine.
        /// </summary>
        private void EmoteAll(string emotion)
        {
            string[] candidates = EmotionAnimations(emotion);
            if (candidates == null || candidates.Length == 0) return;
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
        /// Prioritized candidate animation names per emotion. Names follow the default eSheep XML
        /// (walk/run/jump/boing/sleep/rotate/flower); pets without them simply fall through and
        /// keep their current animation. The emotion vocabulary matches the brain's system prompt
        /// (happy, sad, thinking, excited, confused, neutral).
        /// </summary>
        private static string[] EmotionAnimations(string emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion)) return null;
            switch (emotion.Trim().ToLowerInvariant())
            {
                case "happy":    return new string[] { "flower", "jump", "boing" };
                case "excited":  return new string[] { "run", "jump", "boing" };
                case "sad":      return new string[] { "sleep1a", "sleep2a" };
                case "thinking": return new string[] { "sleep1a" };
                case "confused": return new string[] { "rotate1a", "boing" };
                default:         return null;   // neutral / unknown -> no forced animation
            }
        }

        /// <summary>Lazily build the AI brain from cached settings.</summary>
        private AiBrain EnsureBrain()
        {
            if (aiConfig == null) aiConfig = AiSettings.Load();
            if (aiBrain == null)
            {
                OllamaClient backend = new OllamaClient(aiConfig.Endpoint, TimeSpan.FromSeconds(aiConfig.TimeoutSeconds), aiConfig.OllamaPath);
                aiBrain = new AiBrain(backend, aiConfig);
            }
            return aiBrain;
        }

        /// <summary>
        /// Wire up the phase-3 triggers at launch: warm up the backend, then apply the
        /// global hotkey (3.1) and the opt-in idle commentary loop (3.4). Any failure here
        /// is non-fatal — the pet still runs.
        /// </summary>
        private void InitAiTriggers()
        {
            try
            {
                aiConfig = AiSettings.Load();

                // Warm up the backend on a background thread so the first ask is fast and the
                // UI never blocks: start the Ollama server if needed, then preload the model.
                if (aiConfig.AutoStartServer || aiConfig.WarmUpOnLaunch)
                {
                    AiBrain brain = EnsureBrain();
                    Task.Run(async () =>
                    {
                        try { aiReady = await brain.PrepareAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch { }
                    });
                }

                ApplyAiTriggers();

                // Land greeting: a fortune a few seconds after launch, once it's fallen and settled.
                landTimer = new System.Windows.Forms.Timer();
                landTimer.Interval = 3000;
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
        /// dialog (Phase 4) when it closes. Reloads the JSON, drops the cached brain so the
        /// next ask picks up endpoint/model/timeout/vision changes, and re-applies the hotkey
        /// and idle-loop triggers. UI thread; never throws.
        /// </summary>
        public void ReloadAiSettings()
        {
            try
            {
                aiConfig = AiSettings.Load();
                if (aiBrain != null) { aiBrain.Dispose(); aiBrain = null; }
                fortunes = null;   // rebuild on next use so a SFW/Spicy change takes effect
                ApplyAiTriggers();
            }
            catch (Exception ex)
            {
                AddDebugInfo(DEBUG_TYPE.warning, "AI settings reload failed: " + ex.Message);
            }
        }

        /// <summary>
        /// (Re)apply the hotkey (3.1) and idle-commentary loop (3.4) from the current
        /// <see cref="aiConfig"/>. Idempotent: safe to call at launch and again on every
        /// settings change. Must run on the UI thread (the hotkey owns a message window).
        /// </summary>
        private void ApplyAiTriggers()
        {
            // Global hotkey: drop any existing registration, then re-register if enabled.
            if (aiHotkey != null) { aiHotkey.Dispose(); aiHotkey = null; }
            if (aiConfig.HotkeyEnabled)
            {
                aiHotkey = new HotkeyListener();
                aiHotkey.Pressed += delegate { AskAboutScreen(); };
                bool ok = aiHotkey.Register(aiConfig.Hotkey);
                AddDebugInfo(ok ? DEBUG_TYPE.info : DEBUG_TYPE.warning,
                    "AI hotkey '" + aiConfig.Hotkey + "' " + (ok ? "registered" : "NOT registered (invalid or already in use)"));
            }

            // Idle-commentary loop: create the timer once, then arm or stop it per the setting.
            if (aiConfig.IdleCommentaryEnabled)
            {
                if (aiIdleTimer == null)
                {
                    aiIdleTimer = new System.Windows.Forms.Timer();
                    aiIdleTimer.Tick += IdleTimer_Tick;
                }
                ScheduleIdle();
            }
            else if (aiIdleTimer != null)
            {
                aiIdleTimer.Stop();
            }
        }

        /// <summary>Arm the idle timer for a random interval within the configured bounds.</summary>
        private void ScheduleIdle()
        {
            if (aiIdleTimer == null || aiConfig == null || !aiConfig.IdleCommentaryEnabled) return;
            int lo = Math.Max(15, aiConfig.IdleMinSeconds);
            int hi = Math.Max(lo, aiConfig.IdleMaxSeconds);
            aiIdleTimer.Interval = aiRand.Next(lo, hi + 1) * 1000;
            aiIdleTimer.Start();
        }

        /// <summary>
        /// Idle-commentary tick (3.4) with the gate (3.5): only speak when a pet is present,
        /// speech is enabled, the user hasn't interacted in the last 30s, no pet is being
        /// dragged, and the screen actually changed since the last check.
        /// </summary>
        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            aiIdleTimer.Stop();
            try
            {
                bool recentlyInteracted = (DateTime.UtcNow - aiLastInteractionUtc).TotalSeconds < 30;
                if (iSheeps > 0
                    && Properties.Settings.Default.SpeechEnabled
                    && aiConfig != null && aiConfig.IdleCommentaryEnabled
                    && !recentlyInteracted
                    && !AnyPetBusy())
                {
                    AiBrain brain = EnsureBrain();
                    if (brain.ScreenChanged(aiConfig.IdleChangeThresholdPercent))
                        AskAboutScreen(false);   // idle stays on the fast text path (6.2)
                }
            }
            catch { }
            finally { ScheduleIdle(); }
        }

        /// <summary>True if any pet is currently being handled by the user (idle gate, 3.5).</summary>
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
}
