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

        /// <summary>Core Preferences pane first, then the module contributions (in load order).</summary>
        internal static IReadOnlyList<OptionsPane> CollectPanes()
        {
            var panes = new List<OptionsPane> { BuildPreferencesPane() };
            DesktopPet.Plugins.PetHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
            if (host != null && host.OptionsPanes != null)
            {
                foreach (OptionsPane p in host.OptionsPanes)
                    if (p != null) panes.Add(p);
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
                    new SettingField { Id = "speech", Label = "Speech bubbles enabled", Kind = SettingKind.Bool },
                    new SettingField { Id = "speechSeconds", Label = "Speech duration (seconds)", Kind = SettingKind.Int, Min = 2, Max = 30 },
                    new SettingField { Id = "volume", Label = "Volume (0-10)", Kind = SettingKind.Int, Min = 0, Max = 10 },
                },
                Load = delegate
                {
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    LocalData data = Program.MyData;
                    if (data != null)
                    {
                        d["speech"] = data.GetSpeechEnabled() ? "true" : "false";
                        d["speechSeconds"] = data.GetSpeechDuration().ToString(CultureInfo.InvariantCulture);
                        d["volume"] = ((int)Math.Round(data.GetVolume() * 10.0)).ToString(CultureInfo.InvariantCulture);
                    }
                    return d;
                },
                Save = delegate(IReadOnlyDictionary<string, string> values)
                {
                    LocalData data = Program.MyData;
                    if (data == null || values == null) return false;
                    bool ok = true;
                    string s;
                    if (values.TryGetValue("speech", out s))
                    {
                        bool b;
                        if (bool.TryParse(s, out b)) ok &= data.SetSpeechEnabled(b);
                    }
                    if (values.TryGetValue("speechSeconds", out s))
                    {
                        int n;
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            ok &= data.SetSpeechDuration(Math.Max(2, Math.Min(30, n)));
                    }
                    if (values.TryGetValue("volume", out s))
                    {
                        int n;
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            ok &= data.SetVolume(Math.Max(0, Math.Min(10, n)) / 10.0);
                    }
                    try { ContextMenus.RefreshSpeechMenuItem(); } catch { }
                    return ok;
                },
            };
        }
    }
}
