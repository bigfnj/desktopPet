using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>The composited sprite sheet plus the mapping every pose needs to find its tile.</summary>
    public sealed class SpriteSheet
    {
        public byte[] PngBytes;
        public string Base64Png;
        public int TilesX;
        public int TilesY;
        public int CellWidth;
        public int CellHeight;
        public double Scale;             // uniform scale applied to fit the caps/budget (1.0 = none)
        public bool IsAlpha;             // true = real alpha channel preserved (no magenta key); host renders per-pixel
        public int ProjectedXmlBytes;    // base64 length + a fixed markup allowance
        public readonly Dictionary<string, int> FrameIndexByKey =
            new Dictionary<string, int>(StringComparer.Ordinal); // ShimejiPose.FrameKey -> row-major tile index
    }

    /// <summary>
    /// Composites a Shimeji skin's individual pose PNGs into ONE equal-cell sprite sheet in the exact shape
    /// the desktopPet engine slices (Xml.ReadImages): tilesx * tilesy equal cells, row-major 0-based indices,
    /// magenta (#FF00FF) as the transparency KEY -- the engine keys on colour, not alpha (FormPet.Designer.cs).
    ///
    /// Two things it must get right, both baked into pixels because animations.xml cannot express them:
    ///   * Anchor alignment. A Shimeji pose has an ImageAnchor hotspot (x,y) that stays fixed as frames change;
    ///     desktopPet has no per-frame anchor. So every frame is placed so its anchor lands at the SAME point
    ///     in the cell -- this bakes the x-offset the format's y-only &lt;offsety&gt; cannot carry.
    ///   * Transparency. Alpha is hard-thresholded onto magenta (below the cutoff -> keyed, at/above -> opaque).
    ///     A hard cutoff avoids the magenta halo a blend would leave; the cost is that anti-aliased edges go
    ///     jagged. Genuinely-magenta art pixels are nudged to (254,0,255) so they are not keyed out.
    ///
    /// The caps come straight from PetXmlValidator: cells <= 256 px, <= 1024 tiles, a 4096 px sheet, and the
    /// whole XML (which is mostly this sheet's base64) <= 12 MiB. It downscales uniformly to fit and fails
    /// loudly if it cannot.
    /// </summary>
    public static class SpriteSheetBuilder
    {
        public const int MaxCell = 256;                       // PetXmlValidator.MaximumSpriteFrameDimension
        public const int MaxTiles = 1024;                     // SpriteFrameStore.MaximumFrames
        public const int MaxSheetDimension = 4096;            // PetXmlValidator.MaximumImageDimension
        public const int XmlBudgetBytes = 12 * 1024 * 1024;   // PetXmlValidator.MaximumXmlBytes (raised from 4:
                                                              // lets a frame-heavy skin fill the 4096 sheet up to
                                                              // the 256px cell cap instead of being squeezed under)
        public const int MarkupAllowanceBytes = 256 * 1024;   // header/icon/animations markup + headroom
        public const int AlphaThreshold = 128;                // >= is opaque, < is keyed to magenta

        private const int MinCell = 8;                        // refuse to shrink a sheet into mush

        private static readonly int KeyB = 255, KeyG = 0, KeyR = 255; // magenta in RGB

        /// <summary>A loader that reads a pose image name (e.g. "/shime1.png") from a skin's img directory.</summary>
        public static Func<string, Bitmap> FileLoader(string imgDir)
        {
            return delegate(string image)
            {
                string name = (image ?? "").TrimStart('/', '\\');
                string path = Path.Combine(imgDir, name);
                if (!File.Exists(path)) throw new FileNotFoundException("Sprite not found: " + path);
                using (var raw = new Bitmap(path))
                    return new Bitmap(raw); // detach from the file handle
            };
        }

        public static bool Build(IList<ShimejiPose> poses, Func<string, Bitmap> load, bool alpha,
            out SpriteSheet sheet, out string error)
        {
            sheet = null;
            error = null;

            // 1. distinct frames by (image, anchor), first-seen order.
            var frames = new List<ShimejiPose>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShimejiPose p in poses)
            {
                if (p == null || string.IsNullOrEmpty(p.Image)) continue;
                if (seen.Add(p.FrameKey)) frames.Add(p);
            }
            if (frames.Count == 0) { error = "the skin has no sprite poses to composite."; return false; }
            if (frames.Count > MaxTiles)
            {
                error = string.Format("{0} distinct frames exceeds the {1}-tile limit.", frames.Count, MaxTiles);
                return false;
            }

            // 2. load images once, keyed by name.
            var images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            try
            {
                foreach (ShimejiPose f in frames)
                {
                    if (images.ContainsKey(f.Image)) continue;
                    Bitmap bmp;
                    try { bmp = load(f.Image); }
                    catch (Exception ex) { error = "could not load sprite '" + f.Image + "': " + ex.Message; return false; }
                    if (bmp == null) { error = "sprite loader returned null for '" + f.Image + "'."; return false; }
                    images[f.Image] = bmp;
                }

                // 3. anchor-aligned unscaled cell size. Every frame's anchor maps to O=(Ox,Oy); the cell must
                //    hold the widest left/right and top/bottom extents any frame has around its anchor.
                int ox = 0, right = 0, oy = 0, below = 0;
                foreach (ShimejiPose f in frames)
                {
                    Bitmap b = images[f.Image];
                    ox = Math.Max(ox, f.AnchorX);
                    right = Math.Max(right, b.Width - f.AnchorX);
                    oy = Math.Max(oy, f.AnchorY);
                    below = Math.Max(below, b.Height - f.AnchorY);
                }
                int cellW = Math.Max(1, ox + right);
                int cellH = Math.Max(1, oy + below);

                int n = frames.Count;
                int tilesX = (int)Math.Ceiling(Math.Sqrt(n));
                int tilesY = (int)Math.Ceiling((double)n / tilesX);

                // initial scale: satisfy the 256 px cell cap and the 4096 px sheet cap.
                double scale = Math.Min(1.0, Math.Min((double)MaxCell / cellW, (double)MaxCell / cellH));
                scale = ClampSheet(scale, cellW, cellH, tilesX, tilesY);

                // 4. compose, encode, and shrink until the base64 fits the XML budget.
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    int scaledCellW = Math.Max(1, (int)Math.Round(cellW * scale));
                    int scaledCellH = Math.Max(1, (int)Math.Round(cellH * scale));

                    SpriteSheet built = Compose(frames, images, ox, oy, scale, scaledCellW, scaledCellH, tilesX, tilesY, alpha);
                    int projected = built.Base64Png.Length + MarkupAllowanceBytes;
                    if (projected <= XmlBudgetBytes)
                    {
                        built.ProjectedXmlBytes = projected;
                        sheet = built;
                        return true;
                    }
                    if (scaledCellW <= MinCell || scaledCellH <= MinCell)
                    {
                        error = string.Format(
                            "skin is too large: {0} frames project to {1:N0} bytes of XML, over the {2:N0} limit, " +
                            "even at the minimum cell size. Reduce the sprite count or resolution.",
                            n, projected, XmlBudgetBytes);
                        return false;
                    }
                    scale *= 0.85;
                }
                error = "could not fit the sheet within the XML budget after downscaling.";
                return false;
            }
            finally
            {
                foreach (Bitmap b in images.Values) b.Dispose();
            }
        }

        private static double ClampSheet(double scale, int cellW, int cellH, int tilesX, int tilesY)
        {
            double sheetW = tilesX * cellW * scale;
            double sheetH = tilesY * cellH * scale;
            if (sheetW > MaxSheetDimension) scale *= MaxSheetDimension / sheetW;
            if (sheetH > MaxSheetDimension) scale *= MaxSheetDimension / sheetH;
            return scale;
        }

        private static SpriteSheet Compose(List<ShimejiPose> frames, Dictionary<string, Bitmap> images,
            int ox, int oy, double scale, int scaledCellW, int scaledCellH, int tilesX, int tilesY, bool alpha)
        {
            var result = new SpriteSheet
            {
                TilesX = tilesX,
                TilesY = tilesY,
                CellWidth = scaledCellW,
                CellHeight = scaledCellH,
                Scale = scale,
                IsAlpha = alpha,
            };

            int sheetW = tilesX * scaledCellW;
            int sheetH = tilesY * scaledCellH;

            using (var sheet = new Bitmap(sheetW, sheetH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(sheet))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(alpha
                        ? Color.FromArgb(0, 0, 0, 0)                  // transparent background (real alpha preserved)
                        : Color.FromArgb(255, KeyR, KeyG, KeyB));     // magenta background = the transparency key
                }

                for (int i = 0; i < frames.Count; i++)
                {
                    ShimejiPose f = frames[i];
                    Bitmap src = images[f.Image];
                    int col = i % tilesX;
                    int row = i / tilesX;
                    result.FrameIndexByKey[f.FrameKey] = i;

                    int sw = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int sh = Math.Max(1, (int)Math.Round(src.Height * scale));

                    using (var scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.FromArgb(0, 0, 0, 0));
                            g.DrawImage(src, new Rectangle(0, 0, sw, sh));
                        }
                        if (!alpha) KeyToMagenta(scaled);            // alpha mode keeps the sprite's real anti-aliased edges

                        int placeX = col * scaledCellW + (int)Math.Round((ox - f.AnchorX) * scale);
                        int placeY = row * scaledCellH + (int)Math.Round((oy - f.AnchorY) * scale);
                        BlitOpaque(sheet, scaled, placeX, placeY);
                    }
                }

                using (var ms = new MemoryStream())
                {
                    sheet.Save(ms, ImageFormat.Png);
                    result.PngBytes = ms.ToArray();
                }
            }
            result.Base64Png = Convert.ToBase64String(result.PngBytes);
            return result;
        }

        // Hard-threshold alpha onto magenta, in place, on a Format32bppArgb bitmap.
        private static void KeyToMagenta(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
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
                        byte a = buf[i + 3];
                        if (a < AlphaThreshold)
                        {
                            buf[i + 0] = (byte)KeyB; buf[i + 1] = (byte)KeyG; buf[i + 2] = (byte)KeyR; buf[i + 3] = 255;
                        }
                        else
                        {
                            // opaque; avoid a genuine magenta art pixel being keyed out.
                            if (buf[i + 0] == 255 && buf[i + 1] == 0 && buf[i + 2] == 255) buf[i + 2] = 254;
                            buf[i + 3] = 255;
                        }
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, bytes);
            }
            finally { bmp.UnlockBits(data); }
        }

        // Copy every pixel of an opaque source onto the sheet at (dx,dy), clipped to the sheet bounds.
        private static void BlitOpaque(Bitmap sheet, Bitmap src, int dx, int dy)
        {
            using (var g = Graphics.FromImage(sheet))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(dx, dy, src.Width, src.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
        }
    }
}
