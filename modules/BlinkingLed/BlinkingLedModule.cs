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
            Version = "1.0.0",
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
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Blink once now", InvokeAsync = BlinkOnceAsync, Group = "Blinking LED" },
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
            else if (rateChanged) Say(RateQuip(rate));
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

        /// <summary>One snarky line per speed, because changing the rate is a deliberate act and the pet
        /// having an opinion about it is the point of this living in a pet rather than a tray app.</summary>
        private static string RateQuip(string rate)
        {
            switch (rate)
            {
                case "Glacial": return "Glacial. I will blink again around the next ice age.";
                case "Sluggish": return "Sluggish. Wake me if anything ever happens.";
                case "Slow": return "Slow, but dignified. We are pacing ourselves.";
                case "Normal": return "Normal. How refreshingly unambitious of you.";
                case "Fast": return "Fast. Someone is pretending to look busy.";
                case "Hyper": return "Hyper. Your keyboard is a strobe light now. Hope nobody is watching.";
                default: return "Fine, that speed then.";
            }
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

                    probe.Check("contributes the toggle and the rate menu", host.TrayItems.Count == 2);

                    // The rate submenu is built lazily, so it is only ever exercised if something opens it.
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
                    probe.Check("the remark is about the speed just picked",
                        host.SaidLines.Count > 0 &&
                        host.SaidLines[host.SaidLines.Count - 1] == RateQuip("Slow"));

                    // Every speed needs its own line, or the "snarky remark per speed" is a single line with
                    // extra steps. Also catches a copy-paste that leaves two rates sharing a quip.
                    var quips = new HashSet<string>(StringComparer.Ordinal);
                    bool everyQuipDistinct = true;
                    foreach (string name in ScrollLockBlinker.RateNames)
                        if (!quips.Add(RateQuip(name))) everyQuipDistinct = false;
                    probe.Check("every speed has its own remark", everyQuipDistinct);
                    probe.Check("an unknown speed still has something to say",
                        !string.IsNullOrEmpty(RateQuip("Blistering")));

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
