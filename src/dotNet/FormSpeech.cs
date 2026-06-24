using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopPet
{
    internal class FormSpeech : Form
    {
        private const int BubbleWidth  = 220;
        private const int TailHeight   = 16;
        private const int TextPad      = 12;
        private const int CornerRadius = 14;
        private const int BorderWidth  = 4;
        private const int TailInset    = 36; // distance from left/right edge to tail centre

        private static readonly Color BorderColor = Color.FromArgb(255, 200, 0);

        private string _fullText  = "";
        private int    _displayLen;
        private bool   _dismissed;
        private bool   _faceLeft;

        private readonly Timer _typeTimer    = new Timer { Interval = 25 };
        private readonly Timer _dismissTimer = new Timer();

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
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
        /// <param name="anchorX">Screen X of the pet's mouth (tail tip will point here).</param>
        /// <param name="petTopY">Top edge of the pet window (screen coords).</param>
        /// <param name="durationSeconds">Seconds before auto-dismiss.</param>
        /// <param name="faceLeft">True when the pet is facing left.</param>
        internal void ShowSpeech(string text, int anchorX, int petTopY, int durationSeconds, bool faceLeft)
        {
            _dismissed = false;
            _faceLeft  = faceLeft;
            _fullText  = text ?? "";
            _displayLen = 0;

            _typeTimer.Stop();
            _dismissTimer.Stop();

            int bodyH  = MeasureTextHeight(_fullText, BubbleWidth - TextPad * 2) + TextPad * 2;
            int totalH = bodyH + TailHeight;

            // Position bubble so the tail tip sits over anchorX
            int tailXLocal = _faceLeft ? TailInset : BubbleWidth - TailInset;
            int x = anchorX - tailXLocal;
            int y = petTopY - totalH - 4;

            Rectangle wa = Screen.FromPoint(new Point(anchorX, petTopY)).WorkingArea;
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

            int bodyH    = Height - TailHeight;
            int tailX    = _faceLeft ? TailInset : BubbleWidth - TailInset;
            int tailBase = 11; // half-width of tail at body junction

            // ── Bubble body ────────────────────────────────────────────────
            var bodyRect = new Rectangle(
                BorderWidth / 2,
                BorderWidth / 2,
                BubbleWidth - BorderWidth,
                bodyH       - BorderWidth);

            using (GraphicsPath path = RoundedRect(bodyRect, CornerRadius))
            {
                g.FillPath(Brushes.White, path);
                using (var pen = new Pen(BorderColor, BorderWidth) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            }

            // ── Tail ───────────────────────────────────────────────────────
            // Tip points straight down; base straddles tailX on body bottom
            var tailPts = new[]
            {
                new PointF(tailX - tailBase, bodyH - BorderWidth / 2f),
                new PointF(tailX + tailBase, bodyH - BorderWidth / 2f),
                new PointF(tailX,            Height - 1),
            };

            // Fill tail white, then draw border on the two outer edges only
            g.FillPolygon(Brushes.White, tailPts);

            using (var pen = new Pen(BorderColor, BorderWidth) { LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, tailPts[0], tailPts[2]);
                g.DrawLine(pen, tailPts[1], tailPts[2]);
            }

            // Erase the body border between the tail base points so it blends
            using (var erase = new Pen(Color.White, BorderWidth + 1))
                g.DrawLine(erase,
                    tailX - tailBase + 1, bodyH - 1,
                    tailX + tailBase - 1, bodyH - 1);

            // ── Text ───────────────────────────────────────────────────────
            string visible = _fullText.Substring(0, _displayLen);
            var textRect   = new RectangleF(TextPad, TextPad,
                                            BubbleWidth - TextPad * 2,
                                            bodyH       - TextPad * 2);
            using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (var sf   = new StringFormat {
                                  Alignment     = StringAlignment.Near,
                                  LineAlignment = StringAlignment.Near })
                g.DrawString(visible, font, Brushes.Black, textRect, sf);
        }

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
            using (var f   = new Font("Segoe UI", 9f))
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
