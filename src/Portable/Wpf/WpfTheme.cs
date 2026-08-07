using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // TextBoxBase.CaretBrushProperty
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// A light/dark theme for the WPF settings window (S5b), following the user's preference
    /// ("system" / "light" / "dark"). System mode reads the OS setting via <see cref="WindowTheme.IsDark"/>
    /// (the same registry check the WinForms tray dialogs use). Dark mode paints the window and installs
    /// implicit control styles so the chrome (nav, buttons, inputs) follows; light mode keeps the stock WPF
    /// look (lower risk than fighting the default light templates). The immersive dark title bar is applied
    /// once the window has a handle. Applied when the window opens; a preference change takes effect on the
    /// next open.
    /// </summary>
    internal static class WpfTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        // Dark palette (mirrors WindowTheme's WinForms colours so the two UIs match).
        private static readonly Brush Bg = Freeze(Color.FromRgb(0x20, 0x20, 0x20));
        private static readonly Brush Surface = Freeze(Color.FromRgb(0x2D, 0x2D, 0x30));   // inputs / lists / buttons
        private static readonly Brush Text = Freeze(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush Border = Freeze(Color.FromRgb(0x46, 0x46, 0x4A));

        private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Whether the effective theme is dark for the given preference ("system" consults the OS).</summary>
        public static bool EffectiveDark(string mode)
        {
            if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) return false;
            try { return WindowTheme.IsDark(); } catch { return false; }
        }

        public static void Apply(Window window)
        {
            if (window == null) return;
            string mode = "system";
            try { if (Program.MyData != null) mode = Program.MyData.GetThemeMode(); } catch { }
            bool dark = EffectiveDark(mode);

            // The immersive title bar needs a window handle; set it once the source is initialized.
            window.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int v = dark ? 1 : 0;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref v, sizeof(int));
                }
                catch { }
            };

            if (!dark) return;   // light mode: keep the stock WPF look

            window.Background = Bg;
            window.Foreground = Text;
            ResourceDictionary res = window.Resources;
            Implicit(res, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, Text));
            Implicit(res, typeof(Label), new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(Button),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(TextBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border),
                new Setter(TextBoxBase.CaretBrushProperty, Text));
            Implicit(res, typeof(PasswordBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(ComboBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(ListBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(CheckBox), new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(Separator), new Setter(Control.BackgroundProperty, Border));
        }

        private static void Implicit(ResourceDictionary res, Type target, params Setter[] setters)
        {
            var style = new Style(target);
            foreach (Setter s in setters) style.Setters.Add(s);
            res[target] = style;   // no x:Key => implicit style for every instance of the type in this window
        }
    }
}
