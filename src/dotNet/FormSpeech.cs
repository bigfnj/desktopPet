using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopPet
{
    internal class FormSpeech : Form
    {
        private const int BubbleWidth  = 220;
        private const int TailHeight   = 12;
        private const int TextPad      = 10;
        private const int CornerRadius = 10;

        private string _fullText  = "";
        private int    _displayLen;
        private bool   _dismissed;

        private readonly Timer _typeTimer    = new Timer { Interval = 25 };
        private readonly Timer _dismissTimer = new Timer();

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW  — hide from Alt+Tab
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED     — paint performance
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        internal FormSpeech()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.Magenta;
            TransparencyKey = Color.Magenta;
            TopMost         = true;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;

            _typeTimer.Tick    += TypeTimer_Tick;
            _dismissTimer.Tick += DismissTimer_Tick;
        }

        /// <summary>
        /// Show a speech bubble above the pet.
        /// </summary>
        /// <param name="text">Text to display.</param>
        /// <param name="petCenterX">Horizontal centre of the pet window (screen coords).</param>
        /// <param name="petTopY">Top edge of the pet window (screen coords).</param>
        /// <param name="durationSeconds">Seconds to display before auto-dismissing.</param>
        internal void ShowSpeech(string text, int petCenterX, int petTopY, int durationSeconds)
        {
            _dismissed  = false;
            _fullText   = text ?? "";
            _displayLen = 0;

            _typeTimer.Stop();
            _dismissTimer.Stop();

            int bodyH  = MeasureTextHeight(_fullText, BubbleWidth - TextPad * 2) + TextPad * 2;
            int totalH = bodyH + TailHeight;

            int x = petCenterX - BubbleWidth / 2;
            int y = petTopY - totalH - 2;

            // Clamp to the screen the pet is on
            Rectangle wa = Screen.FromPoint(new Point(petCenterX, petTopY)).WorkingArea;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right  - BubbleWidth));
            y = Math.Max(wa.Top,  Math.Min(y, wa.Bottom - totalH));

            SetBounds(x, y, BubbleWidth, totalH);

            _dismissTimer.Interval = Math.Max(1000, durationSeconds * 1000);
            _typeTimer.Start();

            if (!Visible) Show();
            else          Invalidate();
        }

        private void TypeTimer_Tick(object sender, EventArgs e)
        {
            if (_displayLen < _fullText.Length)
            {
                _displayLen++;
                Invalidate();
            }
            else
            {
                _typeTimer.Stop();
                _dismissTimer.Start();
            }
        }

        private void DismissTimer_Tick(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            _dismissed = true;
            Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(_fullText) || _dismissed) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int bodyH = Height - TailHeight;

            // Bubble body — white rounded rectangle
            var bodyRect = new Rectangle(1, 1, BubbleWidth - 3, bodyH - 2);
            using (GraphicsPath path = RoundedRect(bodyRect, CornerRadius))
            {
                g.FillPath(Brushes.White, path);
                using (var pen = new Pen(Color.FromArgb(80, 80, 80), 1.5f))
                    g.DrawPath(pen, path);
            }

            // Tail triangle pointing down toward the pet
            int tailX   = BubbleWidth / 2;
            var tail    = new[] {
                new Point(tailX - 7, bodyH - 1),
                new Point(tailX + 7, bodyH - 1),
                new Point(tailX,     Height - 1),
            };
            g.FillPolygon(Brushes.White, tail);
            using (var pen = new Pen(Color.FromArgb(80, 80, 80), 1.5f))
            {
                g.DrawLine(pen, tail[0], tail[2]);
                g.DrawLine(pen, tail[1], tail[2]);
            }
            // Erase the body bottom border between the two tail base points so it blends
            using (var erase = new Pen(Color.White, 2.5f))
                g.DrawLine(erase, tail[0].X + 1, bodyH - 1, tail[1].X - 1, bodyH - 1);

            // Text (typewriter reveal)
            string visible  = _fullText.Substring(0, _displayLen);
            var textRect    = new RectangleF(TextPad, TextPad,
                                             BubbleWidth - TextPad * 2,
                                             bodyH       - TextPad * 2);
            using (var font = new Font("Segoe UI", 8.5f))
            using (var sf   = new StringFormat {
                                  Alignment     = StringAlignment.Near,
                                  LineAlignment = StringAlignment.Near })
                g.DrawString(visible, font, Brushes.Black, textRect, sf);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d    = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X,         r.Y,          d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
            path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private static int MeasureTextHeight(string text, int maxWidth)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g   = Graphics.FromImage(bmp))
            using (var f   = new Font("Segoe UI", 8.5f))
            {
                SizeF sz = g.MeasureString(text, f, maxWidth);
                return (int)Math.Ceiling(sz.Height);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _typeTimer.Dispose();
                _dismissTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
