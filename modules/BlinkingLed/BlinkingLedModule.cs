using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopPet.ModuleKit;
using DesktopPet.Modules;

namespace DesktopPet.BlinkingLed
{
    /// <summary>
    /// Blinking LED: the pet keeps the machine looking awake by blinking the keyboard's Scroll Lock light.
    ///
    /// A port of the standalone BlinkingLED tray app into a module. The engine is unchanged in substance
    /// (<see cref="ScrollLockBlinker"/> synthesizes a Scroll Lock keypress on a two-phase timer); everything
    /// AROUND it is deleted, because the host already provides it: the tray entry, the options pane, the
    /// settings file, single-instance behaviour and start-with-Windows all come from being a module. That is
    /// most of what the standalone app's 1000 lines were.
    ///
    /// <para>
    /// Two behaviours are deliberately different from the standalone app, both because a module is not a
    /// process. Caps Lock ON used to QUIT; here it stops the blinking, since a module cannot quit the pet.
    /// And it never speaks at startup: the module auto-starts, and the pet has its own opening line that a
    /// second bubble would talk over. It speaks only when the user turns it off or back on.
    /// </para>
    /// </summary>
    public sealed class BlinkingLedModule : IModule
    {
        private IHost _host;
        private ScrollLockBlinker _blinker;
        private TrayItem _trayToggle;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "blinkingled",
            Name = "Blinking LED",
            Version = "1.0.1",   // 1.0.1: a dozen remarks per speed instead of one, picked at random and
                                 //        never repeating the previous line, so changing the rate stops
                                 //        being a recording.
            // Nothing here is newer than the ABI the 1.4 hosts shipped: tray items, an options pane, settings
            // and SayAll are all original. Deliberately NOT raised to the current host, so this installs on
            // whatever the user already has.
            MinHostVersion = "1.4.0",
            // Storage for its settings, Speech for the on/off line. The Scroll Lock keypress needs no
            // permission because it never goes through the host -- the module P/Invokes SendInput itself.
            // There is no ModulePermissions flag for synthesizing input, so the consent screen cannot state
            // it; the module name and description carry that disclosure instead.
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            _blinker = new ScrollLockBlinker();
            _blinker.CapsLockStopRequested += OnCapsLockStop;

            host.AddTrayItems(new List<TrayItem>
            {
                // DynamicText rather than rewriting Label: the host re-evaluates it every time the menu
                // opens, so the on/off state cannot drift out of sync with the setting.
                (_trayToggle = new TrayItem
                {
                    Label = "Blinking LED",
                    Group = 50,
                    Order = 0,
                    DynamicText = TrayToggleText,
                    Click = ToggleFromTray,
                }),
                // The standalone app's Blink Rate preset menu, kept: picking a speed from the tray is the
                // thing people actually do, and BuildChildren rebuilds on open so the tick follows the
                // current setting for free.
                new TrayItem
                {
                    Label = "Blink rate",
                    Group = 50,
                    Order = 1,
                    BuildChildren = BuildRateMenu,
                },
                // The standalone app's two diagnostics lines, kept because they are the only way to tell
                // "not blinking" from "blinking, but Windows is refusing the input". DynamicText is
                // re-evaluated every time the menu opens, so both are current when you look at them. Click is
                // null, which makes them read-only labels rather than buttons.
                new TrayItem
                {
                    Label = "Next blink",
                    Group = 50,
                    Order = 2,
                    DynamicText = NextBlinkText,
                },
                new TrayItem
                {
                    Label = "Last keypress",
                    Group = 50,
                    Order = 3,
                    DynamicText = LastKeypressText,
                },
            });

            host.AddOptionsPane(new OptionsPane
            {
                Title = "Blinking LED",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "enabled",
                        Label = "Blink the Scroll Lock light",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "rate",
                        Label = "Blink rate",
                        Kind = SettingKind.Enum,
                        Options = ScrollLockBlinker.RateNames,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "capsStops",
                        Label = "Stop when Caps Lock is on",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                    new SettingField
                    {
                        Id = "announce",
                        Label = "Pet says when it is switched on or off",
                        Kind = SettingKind.Bool,
                        Group = "Blinking LED",
                    },
                    // Display-only (SettingKind.Info): the host renders the value as text and never collects
                    // it back. Read when the pane opens and after either button, which is as live as a module
                    // can be -- see NextBlinkText for why there is no ticking countdown.
                    new SettingField
                    {
                        Id = "nextBlink",
                        Label = "Next blink",
                        Kind = SettingKind.Info,
                        Group = "Diagnostics",
                    },
                    new SettingField
                    {
                        Id = "lastKeypress",
                        Label = "Last keypress",
                        Kind = SettingKind.Info,
                        Group = "Diagnostics",
                    },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Blink once now", InvokeAsync = BlinkOnceAsync, Group = "Diagnostics", ReloadPaneAfter = true },
                    new PaneAction { Label = "Refresh", InvokeAsync = RefreshDiagnosticsAsync, Group = "Diagnostics", ReloadPaneAfter = true },
                },
            });

            ApplyState(false);   // false: starting up, so stay quiet whatever the state turns out to be
        }

        public void Shutdown()
        {
            if (_blinker != null)
            {
                _blinker.CapsLockStopRequested -= OnCapsLockStop;
                try { _blinker.Stop(); } catch { }   // leaves the LED off rather than stuck lit
                _blinker.Dispose();
                _blinker = null;
            }
            _host = null;
        }

        // ---- state ----------------------------------------------------------

        /// <summary>
        /// Bring the blinker in line with the settings. <paramref name="announce"/> is false for anything the
        /// USER did not just do (startup, a Caps Lock stop), which is what keeps the pet's opening quip from
        /// being talked over: the quiet path is structural, not a timing guess.
        /// </summary>
        private void ApplyState(bool announce)
        {
            if (_blinker == null) return;
            IModuleSettings s = Settings();
            bool enabled = s.GetBool("enabled", true);
            string rate = s.Get("rate", ScrollLockBlinker.DefaultRate);
            if (!ScrollLockBlinker.IsKnownRate(rate)) rate = ScrollLockBlinker.DefaultRate;

            _blinker.StopOnCapsLock = s.GetBool("capsStops", true);
            bool rateChanged = !string.Equals(rate, _appliedRate, StringComparison.Ordinal);
            _blinker.SetRate(rate);
            _appliedRate = rate;

            bool was = _blinker.IsRunning;
            if (enabled) _blinker.Start(); else _blinker.Stop();

            if (!announce) return;

            // At most ONE line per change, and on/off wins: flipping it off while also changing the speed
            // should not produce two bubbles talking over each other.
            if (was != _blinker.IsRunning) Announce(_blinker.IsRunning);
            else if (rateChanged) Say(PickRateQuip(rate));
        }

        // The rate the blinker is currently set to, so a change can be detected. Seeded on the first
        // ApplyState (which never announces), so startup can never be mistaken for a user changing the speed.
        private string _appliedRate;

        private void Announce(bool on)
        {
            Say(on
                ? "Keeping the lights on for you."
                : "Blinking off. You are on your own now.");
        }

        /// <summary>
        /// A dozen snarky lines per speed. Changing the rate is a deliberate act and the pet having an
        /// opinion about it is the point of this living in a pet rather than a tray app, but one fixed line
        /// per speed stops being funny the second time you see it.
        ///
        /// Each pool is written to the speed's actual cadence (Glacial really is one blink every four
        /// minutes; Hyper really is one a second), so the jokes stay true if someone re-tunes the intervals
        /// without re-reading these. The self-test pins that every pool is a dozen distinct lines and that no
        /// line is shared between speeds.
        /// </summary>
        private static string[] RateQuips(string rate)
        {
            switch (rate)
            {
                case "Glacial": return new[]
                {
                    "Glacial. I will blink again around the next ice age.",
                    "Glacial it is. See you in four minutes. Maybe.",
                    "At this rate the heat death of the universe gets here first.",
                    "Glacial. I have watched continents move faster.",
                    "One blink every four minutes. Riveting stuff.",
                    "Glacial. Wake me when the glaciers do.",
                    "Setting: barely alive. Excellent choice.",
                    "That is less a blink and more an occasional twitch.",
                    "Glacial. Your keyboard is now a very slow lighthouse.",
                    "I will be blinking. Eventually. Do not wait up.",
                    "Glacial. Somewhere, a sloth is taking notes.",
                    "Four minutes between blinks. Bold commitment to doing nothing.",
                };
                case "Sluggish": return new[]
                {
                    "Sluggish. Wake me if anything ever happens.",
                    "Two minutes between blinks. We are not in a hurry.",
                    "Sluggish. The pace of a Monday morning.",
                    "I will blink twice an hour and call it a career.",
                    "Sluggish it is. Low effort, high dignity.",
                    "That is the blink rate of someone avoiding their inbox.",
                    "Sluggish. Practically meditative.",
                    "Every two minutes. Just often enough to prove I am alive.",
                    "Sluggish. I respect the commitment to conserving energy.",
                    "Slow and steady loses the race but keeps the lights on.",
                    "Sluggish. This is the blink equivalent of a long sigh.",
                    "Two whole minutes. I will find something to do.",
                };
                case "Slow": return new[]
                {
                    "Slow, but dignified. We are pacing ourselves.",
                    "Slow. Unhurried. Faintly smug about it.",
                    "Every twelve seconds. Very reasonable of you.",
                    "Slow. The tempo of someone who knows lunch is coming.",
                    "I can work with slow. Barely.",
                    "Slow it is. No sudden movements.",
                    "Twelve seconds between blinks. Practically contemplative.",
                    "Slow. Like a metronome for people who dislike music.",
                    "A gentle blink. Nothing alarming. Very on brand.",
                    "Slow. The pace of someone actually reading the terms and conditions.",
                    "Fine, slow. I will pretend that was a considered decision.",
                    "Slow. Steady. Deeply unremarkable.",
                };
                case "Normal": return new[]
                {
                    "Normal. How refreshingly unambitious of you.",
                    "Normal. The setting for people who do not have opinions.",
                    "Straight down the middle. Bold.",
                    "Normal it is. Nobody was ever fired for choosing normal.",
                    "The default. Truly, a choice was made here.",
                    "Normal. I will try to contain my excitement.",
                    "You went back to normal. Character development.",
                    "Normal. Beige, but functional.",
                    "A perfectly adequate blink rate for a perfectly adequate day.",
                    "Normal. The blink rate equivalent of plain toast.",
                    "Middle of the road, where all the safest decisions live.",
                    "Normal. I would call it inspired if it were remotely inspired.",
                };
                case "Fast": return new[]
                {
                    "Fast. Someone is pretending to look busy.",
                    "Every two seconds. Who exactly are we performing for?",
                    "Fast it is. Deadline energy.",
                    "That is the blink rate of a person with a status meeting at three.",
                    "Fast. I hope your manager is watching, because I am.",
                    "Two seconds apart. This is caffeine in LED form.",
                    "Fast. Somebody has been asked for an update.",
                    "Fast. The light is working harder than you are.",
                    "Rapid blinking. Very convincing. Nobody suspects a thing.",
                    "Fast. We are simulating productivity at scale now.",
                    "Fast. I admire the commitment to appearing available.",
                    "Every two seconds. Frantic, but in a professional way.",
                };
                case "Hyper": return new[]
                {
                    "Hyper. Your keyboard is a strobe light now. Hope nobody is watching.",
                    "Hyper. This is no longer subtle.",
                    "Every second. This is a cry for help with extra steps.",
                    "Hyper. Your desk now has its own weather warning.",
                    "At this speed it stops looking like activity and starts looking like a distress signal.",
                    "Hyper. I hope nobody nearby has opinions about flashing lights.",
                    "Full strobe. Somewhere a colleague is squinting at your desk.",
                    "Hyper. Subtlety has left the building.",
                    "One blink a second. This is a nightclub now.",
                    "Hyper. Nobody has ever looked busy this aggressively.",
                    "Hyper. We have moved from present to alarming.",
                    "This is not blinking. This is Morse code for panic.",
                };
                default: return new[]
                {
                    "Fine, that speed then.",
                    "An unusual choice, but you are the one with the keyboard.",
                };
            }
        }

        // The last line spoken, so the same one never lands twice in a row. With a dozen options a repeat is
        // uncommon but not rare (roughly one change in twelve), and a repeat is exactly the thing that makes
        // a random pool feel broken.
        private string _lastQuip;

        private string PickRateQuip(string rate)
        {
            string[] pool = RateQuips(rate);
            if (pool.Length == 0) return "";
            int index = Random.Shared.Next(pool.Length);
            // Step to the neighbour rather than re-rolling: guaranteed to terminate, guaranteed different.
            if (pool.Length > 1 && string.Equals(pool[index], _lastQuip, StringComparison.Ordinal))
                index = (index + 1) % pool.Length;
            _lastQuip = pool[index];
            return _lastQuip;
        }

        private void Say(string line)
        {
            if (_host == null || string.IsNullOrEmpty(line)) return;
            if (!Settings().GetBool("announce", true)) return;
            if (!_host.SpeechEnabled) return;   // never talk over a deliberately silenced pet
            _host.SayAll(line);
        }

        private string TrayToggleText()
        {
            try { return Settings().GetBool("enabled", true) ? "Blinking LED: on" : "Blinking LED: off"; }
            catch { return "Blinking LED"; }
        }

        /// <summary>
        /// "Next blink: 3.4s (lit)" -- the countdown plus which phase it is counting through, as the
        /// standalone app's line did.
        ///
        /// Snapshot, not a live tick. The standalone app owned its menu and could refresh this every 250ms;
        /// a module ships DATA and the host renders it, and the ABI has no way to push into an open menu or
        /// pane. So it is accurate at the moment you open the menu and then goes stale, which for a value
        /// this short-lived is the honest way to present it.
        /// </summary>
        private string NextBlinkText()
        {
            try
            {
                if (_blinker == null || !_blinker.IsRunning) return "Next blink: not running";
                int ms = _blinker.MsUntilNextBlink;
                if (ms < 0) return "Next blink: not running";
                string phase = _blinker.PhaseOn ? "lit" : "dark";
                return "Next blink: " +
                       (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s (" + phase + ")";
            }
            catch { return "Next blink: unknown"; }
        }

        /// <summary>"Last keypress: sent 2" or the Win32 error that stopped it. Distinguishes a module that
        /// is doing nothing from one whose SendInput is being refused, which looks identical from outside.</summary>
        private string LastKeypressText()
        {
            try
            {
                if (_blinker == null || !_blinker.HasResult) return "Last keypress: none yet";
                if (_blinker.LastWin32Error != 0)
                    return "Last keypress: REFUSED (error " +
                           _blinker.LastWin32Error.ToString(CultureInfo.InvariantCulture) + ")";
                return "Last keypress: sent " +
                       _blinker.LastSentCount.ToString(CultureInfo.InvariantCulture) +
                       ", " + _blinker.ToggleCount.ToString(CultureInfo.InvariantCulture) + " total";
            }
            catch { return "Last keypress: unknown"; }
        }

        private IEnumerable<TrayItem> BuildRateMenu()
        {
            var items = new List<TrayItem>();
            string current;
            try { current = Settings().Get("rate", ScrollLockBlinker.DefaultRate); }
            catch { current = ScrollLockBlinker.DefaultRate; }

            int order = 0;
            foreach (string name in ScrollLockBlinker.RateNames)
            {
                string rate = name;   // capture per iteration, not the loop variable
                items.Add(new TrayItem
                {
                    Label = (string.Equals(rate, current, StringComparison.Ordinal) ? "✓ " : "    ") + rate,
                    Group = 0,
                    Order = order++,
                    Click = delegate { SetRateFromTray(rate); },
                });
            }
            return items;
        }

        private void SetRateFromTray(string rate)
        {
            try
            {
                if (!ScrollLockBlinker.IsKnownRate(rate)) return;
                IModuleSettings s = Settings();
                if (string.Equals(s.Get("rate", ScrollLockBlinker.DefaultRate), rate, StringComparison.Ordinal))
                    return;   // picking the speed it is already on is not worth a remark
                s.Set("rate", rate);
                s.Save();
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        private void ToggleFromTray()
        {
            try
            {
                IModuleSettings s = Settings();
                bool now = !s.GetBool("enabled", true);
                s.Set("enabled", now ? "true" : "false");
                s.Save();
                ApplyState(true);
            }
            catch { /* a module must never throw into the host */ }
        }

        /// <summary>Caps Lock came on and the setting says stop. The standalone app quit here; a module
        /// cannot, so it stops and persists that, otherwise the next settings read would restart it.</summary>
        private void OnCapsLockStop()
        {
            try
            {
                IModuleSettings s = Settings();
                s.Set("enabled", "false");
                s.Save();
                if (_trayToggle != null) _trayToggle.Label = "Blinking LED: off";
                // Deliberately silent: the user hit Caps Lock, which IS the feedback, and this can fire while
                // they are typing.
            }
            catch { }
        }

        /// <summary>Re-read the diagnostics. ReloadPaneAfter does the actual work by calling Load again; this
        /// exists so there is a button to press, because an Info field cannot refresh itself.</summary>
        private System.Threading.Tasks.Task<string> RefreshDiagnosticsAsync()
        {
            return System.Threading.Tasks.Task.FromResult(NextBlinkText());
        }

        private System.Threading.Tasks.Task<string> BlinkOnceAsync()
        {
            if (_blinker == null) return System.Threading.Tasks.Task.FromResult("Not running.");
            long before = _blinker.ToggleCount;
            _blinker.BlinkOnce();
            bool moved = _blinker.ToggleCount > before;
            return System.Threading.Tasks.Task.FromResult(moved
                ? "Toggled Scroll Lock (watch the light)."
                : "Windows refused the input (error " +
                  _blinker.LastWin32Error.ToString(CultureInfo.InvariantCulture) + ").");
        }

        // ---- settings -------------------------------------------------------

        // GetSettings can return null when a module is constructed outside a live host (the options schema is
        // built during Init), which is what made two modules fail to load with a NullReferenceException.
        private IModuleSettings Settings()
        {
            return (_host != null ? _host.GetSettings(Info.Id) : null) ?? new MemoryModuleSettings();
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            IModuleSettings s = Settings();
            string rate = s.Get("rate", ScrollLockBlinker.DefaultRate);
            if (!ScrollLockBlinker.IsKnownRate(rate)) rate = ScrollLockBlinker.DefaultRate;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "enabled", s.GetBool("enabled", true) ? "true" : "false" },
                { "rate", rate },
                { "capsStops", s.GetBool("capsStops", true) ? "true" : "false" },
                { "announce", s.GetBool("announce", true) ? "true" : "false" },
                { "nextBlink", NextBlinkText() },
                { "lastKeypress", LastKeypressText() },
            };
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IModuleSettings s = Settings();
            string v;
            if (values.TryGetValue("enabled", out v)) s.Set("enabled", v);
            if (values.TryGetValue("rate", out v) && ScrollLockBlinker.IsKnownRate(v)) s.Set("rate", v);
            if (values.TryGetValue("capsStops", out v)) s.Set("capsStops", v);
            if (values.TryGetValue("announce", out v)) s.Set("announce", v);
            bool ok = s.Save();
            ApplyState(true);   // the user was just here, so an on/off change may speak
            return ok;
        }

        // ---- self-test ------------------------------------------------------

        /// <summary>
        /// <c>DesktopPet.exe --module-selftest=blinkingled</c>.
        ///
        /// Found by REFLECTION on the assembly, which takes the FIRST method with this signature it finds, so
        /// this must be the only <c>SelfTest(out string)</c> in the module. Helpers are named SelfCheck.
        ///
        /// Deliberately asserts no LED: whether Scroll Lock physically toggles depends on the machine, the
        /// session and whether input is blocked, so a check on it would be flaky and would fail on a headless
        /// CI runner. What IS asserted is everything that can be wrong without touching hardware -- the
        /// contributions, the rate table, the settings round-trip, and the two behaviours that are easy to
        /// regress: silence at startup, and speech on a user toggle.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            var probe = new SelfTestProbe();
            try
            {
                var host = new DesktopPet.ModuleKit.Testing.RecordingHost();
                using (var storage = new DesktopPet.ModuleKit.Testing.TempModuleStorage("blinkingled"))
                {
                    host.UseStorage("blinkingled", storage);

                    var module = new BlinkingLedModule();
                    module.Init(host);

                    probe.Check("contributes the toggle, the rate menu and two diagnostics",
                        host.TrayItems.Count == 4);

                    // The diagnostics lines are labels, not buttons: a Click here would make them do
                    // something when a user inevitably clicks them.
                    probe.Check("the diagnostics tray lines are read-only labels",
                        host.TrayItems[2].Click == null && host.TrayItems[3].Click == null &&
                        host.TrayItems[2].DynamicText != null && host.TrayItems[3].DynamicText != null);
                    probe.Check("the countdown reports a phase while running",
                        host.TrayItems[2].DynamicText().StartsWith("Next blink:", StringComparison.Ordinal));
                    probe.Check("the keypress line answers even before the first blink",
                        host.TrayItems[3].DynamicText().StartsWith("Last keypress:", StringComparison.Ordinal));

                    // The rate submenu is built lazily, so it is only ever exercised if something opens it.
                    // Info fields must be present AND populated: an empty Info row renders as a blank line
                    // with a label, which reads like a bug.
                    IReadOnlyDictionary<string, string> diag = host.OptionsPanes[0].Load();
                    probe.Check("the pane carries both diagnostics",
                        diag.ContainsKey("nextBlink") && diag.ContainsKey("lastKeypress") &&
                        !string.IsNullOrWhiteSpace(diag["nextBlink"]) &&
                        !string.IsNullOrWhiteSpace(diag["lastKeypress"]));

                    TrayItem rateMenu = host.TrayItems[1];
                    probe.Check("the rate tray item is a submenu", rateMenu.BuildChildren != null);
                    var rateChildren = new List<TrayItem>(rateMenu.BuildChildren());
                    probe.Check("the submenu offers every rate",
                        rateChildren.Count == ScrollLockBlinker.RateNames.Length);
                    int ticked = 0;
                    foreach (TrayItem child in rateChildren)
                        if (child.Label != null && child.Label.StartsWith("✓", StringComparison.Ordinal)) ticked++;
                    probe.Check("exactly one rate is ticked as current", ticked == 1);
                    probe.Check("contributes a settings pane", host.OptionsPanes.Count == 1);
                    probe.Check("declares the permissions it uses",
                        module.Info.Permissions.HasFlag(ModulePermissions.Speech) &&
                        module.Info.Permissions.HasFlag(ModulePermissions.Storage));
                    probe.Check("declares no permission it does not use",
                        !module.Info.Permissions.HasFlag(ModulePermissions.Network) &&
                        !module.Info.Permissions.HasFlag(ModulePermissions.ScreenContext));

                    // The behaviour the maintainer asked for by name: the module auto-starts, so Init must
                    // NOT speak, or it talks over the pet's own opening line.
                    probe.Check("says nothing at startup", host.SaidLines.Count == 0);

                    OptionsPane pane = host.OptionsPanes[0];

                    // Rate table: every advertised option must resolve, and they must be ordered slowest to
                    // fastest. A typo'd name silently falling back to Normal is exactly the bug that would
                    // otherwise ship (SetRate accepts anything).
                    SettingField rateField = null;
                    foreach (SettingField f in pane.Schema)
                        if (f.Id == "rate") rateField = f;
                    probe.Check("the pane offers the rate list", rateField != null && rateField.Options != null);

                    bool everyRateKnown = true;
                    int previousOff = int.MaxValue;
                    bool descending = true;
                    foreach (string name in ScrollLockBlinker.RateNames)
                    {
                        if (!ScrollLockBlinker.IsKnownRate(name)) everyRateKnown = false;
                        int on, off;
                        ScrollLockBlinker.DurationsFor(name, out on, out off);
                        if (on <= 0 || off <= 0) everyRateKnown = false;
                        if (off > previousOff) descending = false;
                        previousOff = off;
                    }
                    probe.Check("every advertised rate resolves to a real interval", everyRateKnown);
                    probe.Check("rates run slowest to fastest", descending);
                    probe.Check("an unknown rate is rejected rather than silently accepted",
                        !ScrollLockBlinker.IsKnownRate("Blistering"));

                    // Settings round-trip through the pane's own delegates. Every speech assertion below
                    // measures a DELTA rather than an absolute count, so each one fails on its own merits:
                    // an absolute count makes them all cascade off whatever the first one did.
                    int said = host.SaidLines.Count;
                    probe.Check("the pane saves", pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "false" }, { "announce", "true" },
                    }));
                    IReadOnlyDictionary<string, string> loaded = pane.Load();
                    probe.Check("the pane reloads what it saved",
                        loaded["enabled"] == "false" && loaded["rate"] == "Hyper" &&
                        loaded["capsStops"] == "false");

                    // Turning it OFF is a user action, so it speaks exactly once.
                    probe.Check("speaks when the user switches it off", host.SaidLines.Count - said == 1);

                    // From here the state is: off, Hyper. Each save below changes exactly ONE thing, so a
                    // failure names the behaviour that actually broke instead of a bundle of them.

                    // A save that changes nothing must stay quiet, or every visit to the pane makes the pet
                    // talk.
                    int before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Hyper" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("stays quiet when nothing changed", host.SaidLines.Count == before);

                    // An invalid rate must neither corrupt the stored value nor provoke a remark.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Blistering" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("a bogus rate leaves the stored one alone", pane.Load()["rate"] == "Hyper");
                    probe.Check("a bogus rate provokes no remark", host.SaidLines.Count == before);

                    // Changing the SPEED gets its own remark, and a different one per speed.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("remarks when the speed changes", host.SaidLines.Count - before == 1);
                    probe.Check("the remark is one of the lines for the speed just picked",
                        host.SaidLines.Count > 0 &&
                        Array.IndexOf(RateQuips("Slow"), host.SaidLines[host.SaidLines.Count - 1]) >= 0);

                    // Each speed needs a POOL, or "so it is not the same thing every time" is not delivered.
                    bool poolsBigEnough = true;
                    bool poolsInternallyDistinct = true;
                    var allQuips = new HashSet<string>(StringComparer.Ordinal);
                    bool sharedAcrossSpeeds = false;
                    foreach (string name in ScrollLockBlinker.RateNames)
                    {
                        string[] pool = RateQuips(name);
                        if (pool.Length < 12) poolsBigEnough = false;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string line in pool)
                        {
                            if (string.IsNullOrWhiteSpace(line)) poolsInternallyDistinct = false;
                            if (!seen.Add(line)) poolsInternallyDistinct = false;
                            if (!allQuips.Add(line)) sharedAcrossSpeeds = true;
                        }
                    }
                    probe.Check("every speed has at least a dozen lines", poolsBigEnough);
                    probe.Check("no speed repeats a line within its own pool", poolsInternallyDistinct);
                    probe.Check("no line is shared between two speeds", !sharedAcrossSpeeds);
                    probe.Check("an unknown speed still has something to say",
                        RateQuips("Blistering").Length > 0);

                    // The picker must actually USE the pool. A picker hardwired to pool[0] passes every
                    // assertion above, so draw repeatedly and require both variety and no back-to-back
                    // repeat. 200 draws over 12 lines: seeing only one distinct line is not a flake, it is a
                    // bug, and a consecutive repeat is impossible by construction rather than by luck.
                    var drawn = new HashSet<string>(StringComparer.Ordinal);
                    string previous = null;
                    bool neverRepeatsBackToBack = true;
                    for (int i = 0; i < 200; i++)
                    {
                        string line = module.PickRateQuip("Hyper");
                        if (Array.IndexOf(RateQuips("Hyper"), line) < 0) neverRepeatsBackToBack = false;
                        if (previous != null && line == previous) neverRepeatsBackToBack = false;
                        previous = line;
                        drawn.Add(line);
                    }
                    // Require the WHOLE pool, not merely "more than one". A picker hardwired to pool[0] would
                    // still bounce between indexes 0 and 1 off the no-repeat guard and so satisfy "> 1";
                    // demanding every line closes that. Coupon collector over 12 lines expects ~37 draws, so
                    // missing one in 200 has probability around 3e-7. That is a bug, not a flake.
                    probe.Check("the picker eventually uses every line in the pool",
                        drawn.Count == RateQuips("Hyper").Length);
                    probe.Check("the picker never repeats the previous line back to back",
                        neverRepeatsBackToBack);

                    // Re-picking the SAME speed must not talk, or clicking through the tray menu gets chatty.
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("stays quiet when the speed did not actually change",
                        host.SaidLines.Count == before);

                    // Turning it on AND changing speed at once is ONE line, not two talking over each other.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Glacial" },
                        { "capsStops", "false" }, { "announce", "true" },
                    });
                    probe.Check("an on/off plus speed change speaks once, not twice",
                        host.SaidLines.Count - before == 1);

                    // And with announce off, a real toggle is silent.
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "false" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "false" },
                    });
                    before = host.SaidLines.Count;
                    pane.Save(new Dictionary<string, string>
                    {
                        { "enabled", "true" }, { "rate", "Slow" },
                        { "capsStops", "false" }, { "announce", "false" },
                    });
                    probe.Check("respects the announce setting", host.SaidLines.Count == before);

                    module.Shutdown();
                }
            }
            catch (Exception ex) { probe.Exception(ex); }
            return probe.Finish(out detail);
        }
    }
}
