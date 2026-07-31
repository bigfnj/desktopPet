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
        private bool   _tailOnTop;         // true when the bubble sits below the pet and the tail points up
        private int    _totalH;            // cached bubble height (body + tail); text is fixed while shown
        private int    _lastX = int.MinValue, _lastY = int.MinValue; // last placement, to skip no-op moves

        private readonly Timer _typeTimer    = new Timer { Interval = 25 };
        private readonly Timer _dismissTimer = new Timer();

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                // No WS_EX_LAYERED: shape comes from Form.Region, not colour-keying.
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        internal FormSpeech()
        {
            FormBorderStyle = FormBorderStyle.None;
            // Manual placement: SetBounds() runs before the handle exists on the first
            // ShowSpeech call. Without this the first Show() uses CW_USEDEFAULT and lands
            // top-left, ignoring those bounds; every later call repositions fine because
            // the handle already exists. Manual makes the first show honour Location too.
            StartPosition   = FormStartPosition.Manual;
            // BackColor white so any sub-pixel gap between the painted bubble and
            // the clipping Region shows white (matching the bubble), not magenta.
            // The Region — not a TransparencyKey — defines the visible shape.
            BackColor       = Color.White;
            TopMost         = true;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;
            if (Program.ResourceChurnSelfTestActive)
                Opacity = 0d;

            _typeTimer.Tick    += TypeTimer_Tick;
            _dismissTimer.Tick += DismissTimer_Tick;
        }

        /// <summary>
        /// Show a speech bubble above the pet.
        /// </summary>
        /// <param name="text">Text to display.</param>
        /// <param name="anchorX">Screen X of the pet's mouth (tail tip will point here).</param>
        /// <param name="petTopY">Top edge of the pet window (screen coords).</param>
        /// <param name="petBottomY">Bottom edge of the pet window (screen coords); used to place the bubble below when there's no room above.</param>
        /// <param name="durationSeconds">Seconds before auto-dismiss.</param>
        /// <param name="faceLeft">True when the pet is facing left.</param>
        internal void ShowSpeech(string text, int anchorX, int petTopY, int petBottomY, int durationSeconds, bool faceLeft)
        {
            _dismissed  = false;
            _fullText   = text ?? "";
            _displayLen = 0;

            _typeTimer.Stop();
            _dismissTimer.Stop();

            // Text is fixed for the life of this bubble, so measure the height once here;
            // Reposition() reuses it every tick instead of re-measuring.
            _totalH = MeasureTextHeight(_fullText, BubbleWidth - TextPad * 2) + TextPad * 2 + TailHeight;
            _lastX = _lastY = int.MinValue;   // force the first placement to apply
            Reposition(anchorX, petTopY, petBottomY, faceLeft);

            _dismissTimer.Interval = Math.Max(1000, durationSeconds * 1000);
            _typeTimer.Start();

            if (!Visible) Show();
            else          Invalidate();
        }

        /// <summary>True while a bubble is on screen and not yet auto-dismissed.</summary>
        internal bool IsShowing => Visible && !_dismissed;

        /// <summary>
        /// Keep the bubble in the same z-order policy as its pet. The pet polls fullscreen state
        /// even while stationary and propagates transitions here.
        /// </summary>
        internal void SetFullscreenSuppressed(bool suppressed)
        {
            if (IsDisposed) return;
            TopMost = !suppressed;
        }

        /// <summary>
        /// Place (or re-place) the bubble over the pet's mouth. FormPet calls this every tick
        /// so the bubble follows the pet as it walks or falls, instead of being orphaned at the
        /// spot where it first spoke. Recomputes position/tail only; leaves the typewriter and
        /// dismiss timers alone. A no-op when the placement hasn't changed.
        /// </summary>
        internal void Reposition(int anchorX, int petTopY, int petBottomY, bool faceLeft)
        {
            if (_dismissed) return;
            _faceLeft = faceLeft;
            int totalH = _totalH;

            // Position bubble so the tail tip sits over anchorX
            int tailXLocal = _faceLeft ? TailInset : BubbleWidth - TailInset;
            int x = anchorX - tailXLocal;

            Rectangle wa = Screen.FromPoint(new Point(anchorX, petTopY)).WorkingArea;

            // Prefer above the pet (tail down). Flip below (tail up) only when the
            // bubble wouldn't fit above and does fit below — otherwise keep it above.
            int yAbove = petTopY    - totalH - 4;
            int yBelow = petBottomY + 4;
            bool tailOnTop = yAbove < wa.Top && yBelow + totalH <= wa.Bottom;
            int y = tailOnTop ? yBelow : yAbove;

            x = Math.Max(wa.Left, Math.Min(x, wa.Right  - BubbleWidth));
            y = Math.Max(wa.Top,  Math.Min(y, wa.Bottom - totalH));

            // After clamping, recalculate tail so it still points at the mouth
            int tailMargin = CornerRadius + TailBase + 2;
            int tailX = Math.Max(tailMargin, Math.Min(BubbleWidth - tailMargin, anchorX - x));

            // Skip when nothing moved — avoids churning the window/region every tick while idle.
            if (x == _lastX && y == _lastY && tailX == _tailX && tailOnTop == _tailOnTop) return;

            _tailX = tailX; _tailOnTop = tailOnTop; _lastX = x; _lastY = y;
            SetBounds(x, y, BubbleWidth, totalH);
            UpdateRegion();
        }

        private void TypeTimer_Tick(object sender, EventArgs e)
        {
            if (_displayLen < _fullText.Length)
            {
                _displayLen = UnicodeTextProgress.NextCodePointBoundary(
                    _fullText,
                    _displayLen);
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

        // One closed path: rounded body with the tail notched into one edge.
        // Tail is on the bottom (pointing down) normally, or on the top (pointing up)
        // when the bubble sits below the pet. Walked clockwise from a top-left corner.
        private GraphicsPath BuildBubblePath()
        {
            int d     = CornerRadius * 2;
            int right = BubbleWidth - 1;
            var path  = new GraphicsPath();

            if (_tailOnTop)
            {
                int top = TailHeight;   // body starts below the up-pointing tail
                int bot = Height - 1;
                path.AddArc(0,         top,       d, d, 180, 90); // top-left corner
                // Top edge → tail (up to tip at y=0) → top edge
                path.AddLine(_tailX - TailBase, top, _tailX, 0);       // up to tip
                path.AddLine(_tailX, 0, _tailX + TailBase, top);       // down to right base
                path.AddArc(right - d, top,       d, d, 270, 90); // top-right corner
                path.AddArc(right - d, bot - d,   d, d,   0, 90); // bottom-right
                path.AddArc(0,         bot - d,   d, d,  90, 90); // bottom-left
                path.CloseFigure();
            }
            else
            {
                int bot = Height - TailHeight - 1;   // body bottom (bodyH - 1)
                path.AddArc(0,         0,         d, d, 180, 90); // top-left
                path.AddArc(right - d, 0,         d, d, 270, 90); // top-right
                path.AddArc(right - d, bot - d,   d, d,   0, 90); // bottom-right
                // Bottom edge → tail (down to tip) → bottom edge, then bottom-left corner
                path.AddLine(_tailX + TailBase, bot, _tailX, Height - 1); // down to tip
                path.AddLine(_tailX, Height - 1, _tailX - TailBase, bot);  // up to left base
                path.AddArc(0,         bot - d,   d, d,  90, 90); // bottom-left
                path.CloseFigure();
            }
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
            // When the tail is on top, the body is shifted down by TailHeight.
            float textTop  = (_tailOnTop ? TailHeight : 0) + TextPad;
            string visible = _fullText.Substring(0, _displayLen);
            var textRect   = new RectangleF(TextPad, textTop,
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
