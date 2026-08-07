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
            return new OptionsPane
            {
                Title = "Preferences",
                Schema = new List<SettingField>
                {
                    new SettingField { Id = "runAtStartup", Label = "Run at Windows startup", Kind = SettingKind.Bool },
                    new SettingField { Id = "volume", Label = "Volume (0-10, 0 = mute)", Kind = SettingKind.Int, Min = 0, Max = 10 },
                    new SettingField { Id = "windowForeground", Label = "Bring collided window to front", Kind = SettingKind.Bool },
                    new SettingField { Id = "stealFocus", Label = "Keep pet above the taskbar", Kind = SettingKind.Bool },
                    new SettingField { Id = "multiscreen", Label = "Allow multiple screens", Kind = SettingKind.Bool },
                    new SettingField { Id = "petsAtStartup", Label = "Pets at startup", Kind = SettingKind.Int, Min = 1, Max = 16 },
                    new SettingField { Id = "scale", Label = "Size (1-3)", Kind = SettingKind.Int, Min = 1, Max = 3 },
                    new SettingField { Id = "speech", Label = "Enable speech bubbles", Kind = SettingKind.Bool },
                    new SettingField { Id = "speechSeconds", Label = "Speech duration (seconds)", Kind = SettingKind.Int, Min = 2, Max = 30 },
                    new SettingField { Id = "randomDrop", Label = "Randomly drop a fortune / insight", Kind = SettingKind.Bool },
                    new SettingField { Id = "randomDropMinutes", Label = "…every (minutes)", Kind = SettingKind.Int, Min = 1, Max = 9999 },
                    new SettingField { Id = "randomDropJitter", Label = "…plus or minus (minutes)", Kind = SettingKind.Int, Min = 0, Max = 9998 },
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
                    new PaneAction { Label = "Restore default pet", InvokeAsync = delegate { return System.Threading.Tasks.Task.FromResult(RestoreDefaultPet()); } },
                },
            };
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
