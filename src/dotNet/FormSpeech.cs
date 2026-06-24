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
        private const int TailInset    = 36; // preferred distance from left/right edge to tail centre
        private const int TailBase     = 11; // half-width of tail at body junction
        private const int BorderWidth  = 4;  // solid black outline thickness

        private string _fullText  = "";
        private int    _displayLen;
        private bool   _dismissed;
        private bool   _faceLeft;
        private int    _tailX;             // tail centre in local bubble coords, computed after clamping

        private readonly Timer _typeTimer    = new Timer { Interval = 25 };
        private readonly Timer _dismissTimer = new Timer();

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                // No WS_EX_LAYERED: shape comes from Form.Region, not colour-keying.
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        internal FormSpeech()
        {
            FormBorderStyle = FormBorderStyle.None;
            // BackColor white so any sub-pixel gap between the painted bubble and
            // the clipping Region shows white (matching the bubble), not magenta.
            // The Region — not a TransparencyKey — defines the visible shape.
            BackColor       = Color.White;
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

            // After clamping, recalculate tail so it still points at the mouth
            int tailMargin = CornerRadius + TailBase + 2;
            _tailX = Math.Max(tailMargin, Math.Min(BubbleWidth - tailMargin, anchorX - x));

            SetBounds(x, y, BubbleWidth, totalH);
            UpdateRegion();

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

        // Clip the window to the bubble outline so the OS handles transparency.
        private void UpdateRegion()
        {
            using (GraphicsPath path = BuildBubblePath())
            {
                Region old = Region;
                Region = new Region(path);
                old?.Dispose();
            }
        }

        // One closed path: rounded body with the tail notched into the bottom edge.
        // Walked clockwise from the top-left corner.
        private GraphicsPath BuildBubblePath()
        {
            int bodyH = Height - TailHeight;
            int d     = CornerRadius * 2;
            int right = BubbleWidth - 1;
            int bot   = bodyH - 1;

            var path = new GraphicsPath();
            path.AddArc(0,         0,         d, d, 180, 90); // top-left
            path.AddArc(right - d, 0,         d, d, 270, 90); // top-right
            path.AddArc(right - d, bot - d,   d, d,   0, 90); // bottom-right
            // Bottom edge → tail → bottom edge, then bottom-left corner
            path.AddLine(_tailX + TailBase, bot, _tailX, Height - 1); // down to tip
            path.AddLine(_tailX, Height - 1, _tailX - TailBase, bot);  // up to left base
            path.AddArc(0,         bot - d,   d, d,  90, 90); // bottom-left
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(_fullText) || _dismissed) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int bodyH = Height - TailHeight;

            // ── Bubble: white fill + thick solid black outline ──────────────
            using (GraphicsPath path = BuildBubblePath())
            {
                g.FillPath(Brushes.White, path);
                using (var pen = new Pen(Color.Black, BorderWidth) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            }

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
