using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopPet.PetStudioModule
{
    /// <summary>
    /// A light/dark theme for the Pet Studio window that matches the host's WPF settings window. The module
    /// cannot reference the host's internal WpfTheme, so this mirrors its palette (#202020 / #2D2D30 / #F0F0F0
    /// / #46464A) and its approach: follow the OS setting, paint the window and install implicit control styles
    /// in dark mode, and set the immersive dark title bar once the window has a handle. Light mode keeps the
    /// stock WPF look, exactly as the host does. A DESKTOPPET_FORCE_THEME env var (dark|light) overrides the OS
    /// read, matching the repo's env-override testing pattern (DESKTOPPET_DATA_ROOT and friends).
    ///
    /// One caveat: the module reads the OS theme, not the host's own light/dark/system PREFERENCE (that lives
    /// in host settings the module can't see). So it matches whenever the host is on "system" (the default);
    /// a host forced to the opposite of the OS would diverge. Exposing IHost.IsDarkTheme would close that gap.
    /// </summary>
    internal sealed class PetStudioTheme
    {
        internal bool Dark;

        internal Brush WindowBg, Text, Muted, Surface, Border, PreviewBg;
        internal Brush RootFill, RootStroke, LiveFill, LiveStroke, DeadFill, DeadStroke, ChipText;

        internal static PetStudioTheme Current()
        {
            return EffectiveDark() ? BuildDark() : BuildLight();
        }

        private static bool EffectiveDark()
        {
            string forced = Environment.GetEnvironmentVariable("DESKTOPPET_FORCE_THEME");
            if (!string.IsNullOrEmpty(forced))
            {
                if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
            }
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return v is int i && i == 0;
            }
            catch { return false; }
        }

        private static Brush B(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static PetStudioTheme BuildLight()
        {
            return new PetStudioTheme
            {
                Dark = false,
                WindowBg = null,                 // keep the stock WPF light look
                Text = B(0x11, 0x11, 0x11),
                Muted = B(0x80, 0x80, 0x80),
                Surface = null,
                Border = Brushes.Gray,
                PreviewBg = B(0xE8, 0xE8, 0xE8),
                RootFill = B(0xCF, 0xE3, 0xF7), RootStroke = B(0x3A, 0x6E, 0xA5),
                LiveFill = B(0xD9, 0xEA, 0xD3), LiveStroke = B(0x4C, 0x9A, 0x5A),
                DeadFill = B(0xF4, 0xCC, 0xCC), DeadStroke = B(0xC0, 0x39, 0x2B),
                ChipText = B(0x22, 0x22, 0x22),
            };
        }

        private static PetStudioTheme BuildDark()
        {
            return new PetStudioTheme
            {
                Dark = true,
                WindowBg = B(0x20, 0x20, 0x20),
                Text = B(0xF0, 0xF0, 0xF0),
                Muted = B(0xAA, 0xAA, 0xAA),
                Surface = B(0x2D, 0x2D, 0x30),
                Border = B(0x46, 0x46, 0x4A),
                PreviewBg = B(0x3A, 0x3A, 0x3D),
                RootFill = B(0x24, 0x40, 0x5C), RootStroke = B(0x4E, 0x86, 0xC4),
                LiveFill = B(0x2C, 0x4A, 0x34), LiveStroke = B(0x5F, 0xB0, 0x6E),
                DeadFill = B(0x5C, 0x2B, 0x27), DeadStroke = B(0xD8, 0x69, 0x5B),
                ChipText = B(0xF0, 0xF0, 0xF0),
            };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        /// <summary>Paint the window to match the host. The title bar is set for both themes; the control
        /// tree is recoloured only in dark mode (light keeps the stock templates, like the host).</summary>
        internal void Apply(Window window)
        {
            if (window == null) return;

            window.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int v = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref v, sizeof(int));
                }
                catch { }
            };

            if (!Dark) return;

            window.Background = WindowBg;
            window.Foreground = Text;
            ResourceDictionary res = window.Resources;
            Implicit(res, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, Text));
            Implicit(res, typeof(Button),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(TextBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border),
                new Setter(TextBoxBase.CaretBrushProperty, Text));
            res[typeof(ScrollBar)] = BuildScrollBarStyle();
        }

        private static void Implicit(ResourceDictionary res, Type target, params Setter[] setters)
        {
            var style = new Style(target);
            foreach (Setter s in setters) style.Setters.Add(s);
            res[target] = style;
        }

        // A minimal dark vertical scrollbar (thin dark track + rounded grey thumb), matching the host's.
        private static Style BuildScrollBarStyle()
        {
            const string xaml =
                "<ControlTemplate TargetType=\"{x:Type ScrollBar}\" " +
                "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<Grid Background=\"#FF202020\">" +
                "<Track Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">" +
                "<Track.Thumb><Thumb><Thumb.Template>" +
                "<ControlTemplate TargetType=\"{x:Type Thumb}\">" +
                "<Border Background=\"#FF5A5A5E\" CornerRadius=\"4\" Margin=\"2,1,2,1\"/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate>";
            var style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            return style;
        }
    }
}
