using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopPet.Modules;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Assembles the settings panes for the WPF window (S5b): a core Preferences pane (backed by LocalData)
    /// plus every module-contributed <see cref="OptionsPane"/> collected by the plugin host. Opened from the
    /// tray; coexists with the classic FormOptions dialog during the transition (FormOptions retires in a
    /// later S5 step). Kept tiny + separate so the pane assembly is unit-testable (--wpf-options-selftest).
    /// </summary>
    internal static class OptionsShell
    {
        public static void Open()
        {
            try
            {
                var window = new OptionsWindow(CollectPanes());
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "WPF settings window failed: " + ex.Message);
            }
        }

        /// <summary>The window's sections: core Preferences (schema) + the host Pets gallery (custom control)
        /// first, then each module's contributed schema pane (in load order).</summary>
        internal static IReadOnlyList<ShellPane> CollectPanes()
        {
            var panes = new List<ShellPane>
            {
                new SchemaShellPane(BuildPreferencesPane()),
                new CustomShellPane("Pets", delegate { return new PetsPaneControl(); }),
            };
            DesktopPet.Plugins.PetHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
            if (host != null && host.OptionsPanes != null)
            {
                foreach (OptionsPane p in host.OptionsPanes)
                    if (p != null) panes.Add(new SchemaShellPane(p));
            }
            return panes;
        }

        /// <summary>The core Preferences pane, rendered by the same schema mechanism as module panes and
        /// persisted through LocalData. A minimal safe subset for the first cut (S5b-1); the fuller
        /// preferences move over as FormOptions is retired.</summary>
        internal static OptionsPane BuildPreferencesPane()
        {
            // Audio output devices for the picker (enumerated fresh each open; first entry = default device).
            // Display names are de-duplicated so the enum options are unique; each maps back to its GUID.
            var devices = AudioOutput.EnumerateDevices();
            var deviceNames = new List<string>();
            var nameToGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            var guidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in devices)
            {
                string baseName = string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value;
                string display = baseName;
                int suffix = 2;
                while (nameToGuid.ContainsKey(display)) display = baseName + " (" + (suffix++) + ")";
                deviceNames.Add(display);
                nameToGuid[display] = kv.Key;
                if (!guidToName.ContainsKey(kv.Key)) guidToName[kv.Key] = display;
            }

            return new OptionsPane
            {
                Title = "Preferences",
                Schema = new List<SettingField>
                {
                    new SettingField { Id = "runAtStartup", Label = "Run at Windows startup", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "windowForeground", Label = "Bring collided window to front", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "stealFocus", Label = "Keep pet above the taskbar", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "multiscreen", Label = "Allow multiple screens", Kind = SettingKind.Bool, Group = "Startup & window" },
                    new SettingField { Id = "petsAtStartup", Label = "Pets at startup", Kind = SettingKind.Int, Min = 1, Max = 16, Group = "Startup & window" },
                    // Per-pet size lives in the Pets module now (the size cycle on each pet card); the global
                    // scale stays only as the internal fallback for pets without an override, so it's no longer
                    // a Preferences field.
                    new SettingField { Id = "volume", Label = "Volume (0-10, 0 = mute)", Kind = SettingKind.Int, Min = 0, Max = 10, Group = "Sound" },
                    new SettingField { Id = "audioDevice", Label = "Sound output device", Kind = SettingKind.Enum, Options = deviceNames.ToArray(), Group = "Sound" },
                    new SettingField { Id = "speech", Label = "Enable speech bubbles", Kind = SettingKind.Bool, Group = "Speech" },
                    new SettingField { Id = "speechSeconds", Label = "Speech duration (seconds)", Kind = SettingKind.Int, Min = 2, Max = 30, Group = "Speech" },
                    new SettingField { Id = "noRepeat", Label = "Don't repeat the same message twice in a row", Kind = SettingKind.Bool, Group = "Speech" },
                    new SettingField { Id = "randomDrop", Label = "Randomly drop a fortune / insight", Kind = SettingKind.Bool, Group = "Fortune / insight drop" },
                    new SettingField { Id = "randomDropMinutes", Label = "…every (minutes)", Kind = SettingKind.Int, Min = 1, Max = 9999, Group = "Fortune / insight drop" },
                    new SettingField { Id = "randomDropJitter", Label = "…plus or minus (minutes)", Kind = SettingKind.Int, Min = 0, Max = 9998, Group = "Fortune / insight drop" },
                },
                Load = delegate
                {
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    LocalData data = Program.MyData;
                    if (data != null)
                    {
                        d["volume"] = ((int)Math.Round(data.GetVolume() * 10.0)).ToString(CultureInfo.InvariantCulture);
                        d["windowForeground"] = data.GetWindowForeground() ? "true" : "false";
                        d["stealFocus"] = data.GetStealTaskbarFocus() ? "true" : "false";
                        d["multiscreen"] = data.GetMultiscreen() ? "true" : "false";
                        d["petsAtStartup"] = data.GetAutoStartPets().ToString(CultureInfo.InvariantCulture);
                        d["speech"] = data.GetSpeechEnabled() ? "true" : "false";
                        d["speechSeconds"] = data.GetSpeechDuration().ToString(CultureInfo.InvariantCulture);
                        d["noRepeat"] = data.GetSuppressRepeats() ? "true" : "false";
                        string savedGuid = data.GetAudioDeviceId();
                        if (string.IsNullOrEmpty(savedGuid)) savedGuid = Guid.Empty.ToString();
                        string curName;
                        d["audioDevice"] = guidToName.TryGetValue(savedGuid, out curName)
                            ? curName
                            : (deviceNames.Count > 0 ? deviceNames[0] : "");
                    }
                    d["runAtStartup"] = StartupRegistration.IsEnabled() ? "true" : "false";
                    if (data != null)
                    {
                        d["randomDrop"] = data.GetRandomDropEnabled() ? "true" : "false";
                        d["randomDropMinutes"] = data.GetRandomDropMinutes().ToString(CultureInfo.InvariantCulture);
                        d["randomDropJitter"] = data.GetRandomDropJitterMinutes().ToString(CultureInfo.InvariantCulture);
                    }
                    return d;
                },
                Save = delegate(IReadOnlyDictionary<string, string> values)
                {
                    LocalData data = Program.MyData;
                    if (data == null || values == null) return false;
                    bool ok = true;
                    string s; int n; bool b;
                    if (values.TryGetValue("runAtStartup", out s) && bool.TryParse(s, out b)) StartupRegistration.Set(b);
                    if (values.TryGetValue("volume", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetVolume(Math.Max(0, Math.Min(10, n)) / 10.0);
                    if (values.TryGetValue("windowForeground", out s) && bool.TryParse(s, out b)) ok &= data.SetWindowForeground(b);
                    if (values.TryGetValue("stealFocus", out s) && bool.TryParse(s, out b)) ok &= data.SetStealTaskbarFocus(b);
                    if (values.TryGetValue("multiscreen", out s) && bool.TryParse(s, out b)) ok &= data.SetMultiscreen(b);
                    if (values.TryGetValue("petsAtStartup", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetAutoStartPets(Math.Max(1, Math.Min(16, n)));
                    if (values.TryGetValue("speech", out s) && bool.TryParse(s, out b)) ok &= data.SetSpeechEnabled(b);
                    if (values.TryGetValue("speechSeconds", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetSpeechDuration(Math.Max(2, Math.Min(30, n)));
                    if (values.TryGetValue("noRepeat", out s) && bool.TryParse(s, out b)) ok &= data.SetSuppressRepeats(b);
                    string devGuid;
                    if (values.TryGetValue("audioDevice", out s) && nameToGuid.TryGetValue(s, out devGuid))
                    {
                        Guid gg;
                        // Store "" for the default device so it keeps following the default across device changes.
                        string toStore = (Guid.TryParse(devGuid, out gg) && gg == Guid.Empty) ? "" : devGuid;
                        ok &= data.SetAudioDeviceId(toStore);
                        try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(toStore); } catch { }
                    }

                    // Random-drop cadence lives in settings.json now (S5c); edit the three fields as a set
                    // then nudge the running pet to re-arm its drop timer.
                    bool rdEnabled = data.GetRandomDropEnabled();
                    int rdMinutes = data.GetRandomDropMinutes();
                    int rdJitter = data.GetRandomDropJitterMinutes();
                    if (values.TryGetValue("randomDrop", out s) && bool.TryParse(s, out b)) rdEnabled = b;
                    if (values.TryGetValue("randomDropMinutes", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) rdMinutes = n;
                    if (values.TryGetValue("randomDropJitter", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) rdJitter = n;
                    ok &= data.SetRandomDrop(rdEnabled, rdMinutes, rdJitter);

                    try { if (Program.Mainthread != null) ((DesktopPet.Options.IPetRuntime)Program.Mainthread).ReloadAiSettings(); } catch { }
                    try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                    return ok;
                },
                Actions = BuildPreferencesActions(),
            };
        }

        /// <summary>The Preferences pane's action buttons: a sound test, and a reset that restores the
        /// preferences on this page to their defaults (behind a confirmation).</summary>
        private static List<PaneAction> BuildPreferencesActions()
        {
            PaneAction reset = new PaneAction { Label = "Reset to default settings" };
            reset.InvokeAsync = delegate
            {
                var choice = System.Windows.MessageBox.Show(
                    "Reset all preferences on this page to their defaults?\n\n" +
                    "This restores the startup, window, sound, speech, and fortune-drop settings shown here. " +
                    "It does not remove any pets, per-pet sizes, or the AI Brain module's settings.",
                    "Reset settings",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (choice != System.Windows.MessageBoxResult.Yes)
                {
                    reset.ReloadPaneAfter = false;
                    return System.Threading.Tasks.Task.FromResult("Cancelled — nothing was reset.");
                }
                string status = ResetToDefaultSettings();
                // Rebuild the pane so the fields visibly snap to their defaults (the reset is already saved).
                reset.ReloadPaneAfter = true;
                return System.Threading.Tasks.Task.FromResult(status);
            };
            return new List<PaneAction>
            {
                new PaneAction { Label = "Test sound", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(TestSound()); }, Group = "Sound" },
                reset,
            };
        }

        /// <summary>Play a short test tone through the current output device (the "Test sound" button).</summary>
        private static string TestSound()
        {
            try
            {
                if (Program.Mainthread == null) return "No running pet to play through.";
                Program.Mainthread.PlayTestSound();
                return "Played a test tone on the selected output.";
            }
            catch (Exception ex) { return "Couldn't play: " + ex.Message; }
        }

        /// <summary>Restore the preferences shown on this page to their defaults. Scoped on purpose: the
        /// pet payload (loaded pet XML/images), per-pet sizes/mutes, and the AI Brain module's own settings
        /// are left alone — only the core preference fields + the fortune-drop fields (AiSettings) shown here
        /// are reset, then persisted. The pane is rebuilt afterward so the new values show.</summary>
        private static string ResetToDefaultSettings()
        {
            try
            {
                LocalData data = Program.MyData;
                if (data == null) return "Settings are unavailable.";

                // Core preferences: pull each default from a fresh document, apply via the validated setters
                // (so nothing outside the preference fields — pet XML, pet mix, etc. — is touched).
                AppSettingsDocument def = AppSettingsDocument.CreateDefault();
                data.SetVolume(def.Volume);
                data.SetWindowForeground(def.WindowForeground);
                data.SetStealTaskbarFocus(def.StealTaskbarFocus);
                data.SetMultiscreen(def.MultiScreen);
                data.SetAutoStartPets(def.AutoStartPets);
                data.SetScale(def.ScaleLevel);                 // the internal size fallback
                data.SetSpeechEnabled(def.SpeechEnabled);
                data.SetSpeechDuration(def.SpeechDurationSeconds);
                data.SetSuppressRepeats(def.SuppressRepeats ?? true);
                data.SetThemeMode(def.ThemeMode);
                data.SetAudioDeviceId(def.AudioDeviceId);

                // Run-at-startup lives in the registry, not the settings doc; default is off.
                try { StartupRegistration.Set(false); } catch { }
                // Apply the reset output device to the running pet right away (theme applies on next open).
                try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(def.AudioDeviceId ?? ""); } catch { }

                // Fortune/insight drop cadence (settings.json, S5c): reset the three drop fields shown on
                // this page to their defaults and re-arm the running pet's drop timer.
                try
                {
                    data.SetRandomDrop(def.RandomDropEnabled ?? false, def.RandomDropMinutes ?? 15, def.RandomDropJitterMinutes ?? 3);
                    if (Program.Mainthread != null) ((DesktopPet.Options.IPetRuntime)Program.Mainthread).ReloadAiSettings();
                }
                catch { }

                try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                return "";   // no status text needed: the pane rebuild shows the restored values
            }
            catch (Exception ex) { return "Reset failed: " + ex.Message; }
        }
    }
}
