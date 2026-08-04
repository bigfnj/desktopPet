using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopPet
{
    /// <summary>
    /// A TabControl that paints its whole background dark in dark mode. The owner-drawn tabs
    /// (FormOptions.tabControl1_DrawItem) cover the tab buttons, but the strip's gutter and margins
    /// are otherwise erased by the native control in the system (light) colour -- which reads as a
    /// white block on a dark form. Filling the client on WM_ERASEBKGND covers the whole strip before
    /// the tabs and tab page paint over it. A no-op in light mode.
    /// </summary>
    internal sealed class DarkTabControl : TabControl
    {
        private const int WM_ERASEBKGND = 0x0014;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND && WindowTheme.IsDark())
            {
                using (var g = Graphics.FromHdc(m.WParam))
                using (var b = new SolidBrush(WindowTheme.Bg))
                    g.FillRectangle(b, ClientRectangle);
                m.Result = (IntPtr)1;   // handled
                return;
            }
            base.WndProc(ref m);
        }
    }
}
