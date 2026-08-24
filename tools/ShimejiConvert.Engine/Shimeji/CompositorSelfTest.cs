using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free test of the sprite compositor on SYNTHETIC images (solid rectangles), so the gate
    /// exercises the real compositing path without any copyrighted skin art. It proves: equal cells within the
    /// 256 px cap, uniform downscale when a frame is oversized, alpha hard-thresholded onto magenta, genuine
    /// magenta art nudged off the key, and the sheet fitting the XML budget. Compositing a REAL skin is the
    /// dev command `ShimejiConvert composite &lt;conf&gt; &lt;img&gt; &lt;out.png&gt;`.
    /// </summary>
    public static class CompositorSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            // Four synthetic frames. "big" is 400x300 so it forces a downscale below the 256 px cap.
            var owned = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
            {
                { "red",        Solid(40, 60, Color.FromArgb(255, 255, 0, 0)) },
                { "magentaArt", Solid(20, 20, Color.FromArgb(255, 255, 0, 255)) },
                { "transp",     Transparent(30, 30) },
                { "big",        Solid(400, 300, Color.FromArgb(255, 0, 255, 0)) },
            };
            var poses = new List<ShimejiPose>
            {
                new ShimejiPose { Image = "red",        AnchorX = 20,  AnchorY = 60 },
                new ShimejiPose { Image = "magentaArt", AnchorX = 10,  AnchorY = 20 },
                new ShimejiPose { Image = "transp",     AnchorX = 15,  AnchorY = 30 },
                new ShimejiPose { Image = "big",        AnchorX = 200, AnchorY = 300 },
            };

            try
            {
                Func<string, Bitmap> load = delegate(string name) { return new Bitmap(owned[name]); };

                SpriteSheet sheet;
                string error;
                bool ok = SpriteSheetBuilder.Build(poses, load, out sheet, out error);
                if (!ok) { detail = "compositor self-test: Build failed -- " + error; return false; }

                if (sheet.CellWidth > SpriteSheetBuilder.MaxCell || sheet.CellHeight > SpriteSheetBuilder.MaxCell)
                    failures.Add(string.Format("cell {0}x{1} exceeds the {2} px cap", sheet.CellWidth, sheet.CellHeight, SpriteSheetBuilder.MaxCell));
                if (sheet.Scale >= 1.0)
                    failures.Add("expected a downscale (Scale < 1) because a 400x300 frame is present, got " + sheet.Scale);
                if (sheet.FrameIndexByKey.Count != 4)
                    failures.Add("expected 4 distinct frames, got " + sheet.FrameIndexByKey.Count);
                if (sheet.TilesX * sheet.TilesY < 4)
                    failures.Add(string.Format("grid {0}x{1} cannot hold 4 frames", sheet.TilesX, sheet.TilesY));
                if (sheet.ProjectedXmlBytes >= SpriteSheetBuilder.XmlBudgetBytes)
                    failures.Add("projected XML " + sheet.ProjectedXmlBytes + " exceeds the budget");
                if (sheet.PngBytes == null || sheet.PngBytes.Length == 0)
                    failures.Add("no PNG bytes produced");

                // Decode the produced sheet and count key colours.
                if (sheet.PngBytes != null && sheet.PngBytes.Length > 0)
                {
                    int red, green, magenta, nudged;
                    CountColours(sheet.PngBytes, out red, out green, out magenta, out nudged);
                    if (red == 0) failures.Add("opaque red art was not preserved");
                    if (green == 0) failures.Add("opaque green art (the downscaled big frame) was not preserved");
                    if (magenta == 0) failures.Add("no magenta key pixels (transparent areas were not keyed)");
                    if (nudged == 0) failures.Add("a genuine magenta art pixel was not nudged off the key (254,0,255)");
                }
            }
            finally
            {
                foreach (Bitmap b in owned.Values) b.Dispose();
            }

            var sb = new StringBuilder();
            sb.AppendLine("compositor self-test: 4 synthetic frames -> equal-cell magenta-keyed sheet");
            if (failures.Count == 0) { sb.Append("  cells capped, downscale applied, keying + collision correct, within budget"); detail = sb.ToString(); return true; }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static Bitmap Solid(int w, int h, Color c)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.Clear(c); }
            return bmp;
        }

        private static Bitmap Transparent(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.Clear(Color.FromArgb(0, 0, 0, 0)); }
            return bmp;
        }

        private static void CountColours(byte[] png, out int red, out int green, out int magenta, out int nudged)
        {
            red = green = magenta = nudged = 0;
            using (var ms = new MemoryStream(png))
            using (var raw = new Bitmap(ms))
            using (var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(raw, 0, 0); }
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = Math.Abs(data.Stride) * bmp.Height;
                    var buf = new byte[bytes];
                    Marshal.Copy(data.Scan0, buf, 0, bytes);
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        int rowStart = y * data.Stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            int i = rowStart + x * 4; // BGRA
                            byte b = buf[i + 0], gg = buf[i + 1], r = buf[i + 2];
                            if (r == 255 && gg == 0 && b == 0) red++;
                            else if (r == 0 && gg == 255 && b == 0) green++;
                            else if (r == 255 && gg == 0 && b == 255) magenta++;
                            else if (r == 254 && gg == 0 && b == 255) nudged++;
                        }
                    }
                }
                finally { bmp.UnlockBits(data); }
            }
        }
    }
}
