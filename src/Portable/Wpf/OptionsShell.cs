using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopPet.Ai;      // AiSettings (random-drop fields)
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
                    new SettingField { Id = "scale", Label = "Size (1-3)", Kind = SettingKind.Int, Min = 1, Max = 3, Group = "Startup & window" },
                    new SettingField { Id = "volume", Label = "Volume (0-10, 0 = mute)", Kind = SettingKind.Int, Min = 0, Max = 10, Group = "Sound" },
                    new SettingField { Id = "audioDevice", Label = "Sound output device", Kind = SettingKind.Enum, Options = deviceNames.ToArray(), Group = "Sound" },
                    new SettingField { Id = "speech", Label = "Enable speech bubbles", Kind = SettingKind.Bool, Group = "Speech" },
                    new SettingField { Id = "speechSeconds", Label = "Speech duration (seconds)", Kind = SettingKind.Int, Min = 2, Max = 30, Group = "Speech" },
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
                        d["scale"] = data.GetScale().ToString(CultureInfo.InvariantCulture);
                        d["speech"] = data.GetSpeechEnabled() ? "true" : "false";
                        d["speechSeconds"] = data.GetSpeechDuration().ToString(CultureInfo.InvariantCulture);
                        string savedGuid = data.GetAudioDeviceId();
                        if (string.IsNullOrEmpty(savedGuid)) savedGuid = Guid.Empty.ToString();
                        string curName;
                        d["audioDevice"] = guidToName.TryGetValue(savedGuid, out curName)
                            ? curName
                            : (deviceNames.Count > 0 ? deviceNames[0] : "");
                    }
                    d["runAtStartup"] = StartupRegistration.IsEnabled() ? "true" : "false";
                    AiSettings ai = AiSettings.Load();
                    d["randomDrop"] = ai.RandomDropEnabled ? "true" : "false";
                    d["randomDropMinutes"] = ai.RandomDropMinutes.ToString(CultureInfo.InvariantCulture);
                    d["randomDropJitter"] = ai.RandomDropJitterMinutes.ToString(CultureInfo.InvariantCulture);
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
                    if (values.TryGetValue("scale", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetScale(Math.Max(1, Math.Min(3, n)));
                    if (values.TryGetValue("speech", out s) && bool.TryParse(s, out b)) ok &= data.SetSpeechEnabled(b);
                    if (values.TryGetValue("speechSeconds", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ok &= data.SetSpeechDuration(Math.Max(2, Math.Min(30, n)));
                    string devGuid;
                    if (values.TryGetValue("audioDevice", out s) && nameToGuid.TryGetValue(s, out devGuid))
                    {
                        Guid gg;
                        // Store "" for the default device so it keeps following the default across device changes.
                        string toStore = (Guid.TryParse(devGuid, out gg) && gg == Guid.Empty) ? "" : devGuid;
                        ok &= data.SetAudioDeviceId(toStore);
                        try { if (Program.Mainthread != null) Program.Mainthread.ApplyAudioDevice(toStore); } catch { }
                    }

                    // Random-drop lives in AiSettings; load-mutate-save then nudge the running pet to re-read.
                    AiSettings ai = AiSettings.Load();
                    if (values.TryGetValue("randomDrop", out s) && bool.TryParse(s, out b)) ai.RandomDropEnabled = b;
                    if (values.TryGetValue("randomDropMinutes", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ai.RandomDropMinutes = n;
                    if (values.TryGetValue("randomDropJitter", out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ai.RandomDropJitterMinutes = n;
                    ok &= ai.Save();

                    try { if (Program.Mainthread != null) ((DesktopPet.Options.IPetRuntime)Program.Mainthread).ReloadAiSettings(); } catch { }
                    try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                    return ok;
                },
                Actions = new List<PaneAction>
                {
                    new PaneAction { Label = "Test sound", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(TestSound()); }, Group = "Sound" },
                    new PaneAction { Label = "Restore default pet", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(RestoreDefaultPet()); } },
                },
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

        /// <summary>Replace the active pet with the built-in default (the classic "Restore pet" button).</summary>
        private static string RestoreDefaultPet()
        {
            try
            {
                string xml, err;
                if (!PetCatalog.TryReadPetXml(PetCatalog.BuiltInPetId, out xml, out err))
                    return "Couldn't read the default pet: " + err;
                var runtime = Program.Mainthread as DesktopPet.Options.IPetRuntime;
                if (runtime == null) return "No running pet to restore.";
                return runtime.LoadNewXMLFromString(xml) ? "Default pet restored." : "Couldn't restore the default pet.";
            }
            catch (Exception ex) { return "Failed: " + ex.Message; }
        }
    }
}
