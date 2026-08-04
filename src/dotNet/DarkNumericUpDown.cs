using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopPet
{
    /// <summary>
    /// A NumericUpDown whose inner edit honors the control's BackColor/ForeColor. The stock control
    /// lets its themed inner edit paint a white background regardless of BackColor, which looks broken
    /// on a dark form; answering the edit's WM_CTLCOLOR* message with a matching brush fixes it. In
    /// light mode the colors are the WinForms defaults, so it renders exactly like the stock control.
    /// </summary>
    internal sealed class DarkNumericUpDown : NumericUpDown
    {
        private const int WM_CTLCOLOREDIT = 0x0133;
        private const int WM_CTLCOLORSTATIC = 0x0138;   // the edit when read-only / disabled

        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int colorRef);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr hdc, int colorRef);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int colorRef);

        private IntPtr _brush = IntPtr.Zero;
        private int _brushColor = -1;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CTLCOLOREDIT || m.Msg == WM_CTLCOLORSTATIC)
            {
                int bk = ColorTranslator.ToWin32(BackColor);
                SetBkColor(m.WParam, bk);
                SetTextColor(m.WParam, ColorTranslator.ToWin32(ForeColor));
                if (_brush == IntPtr.Zero || _brushColor != bk)   // cache the brush, rebuild on color change
                {
                    if (_brush != IntPtr.Zero) DeleteObject(_brush);
                    _brush = CreateSolidBrush(bk);
                    _brushColor = bk;
                }
                m.Result = _brush;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (_brush != IntPtr.Zero) { DeleteObject(_brush); _brush = IntPtr.Zero; }
            base.Dispose(disposing);
        }
    }
}
