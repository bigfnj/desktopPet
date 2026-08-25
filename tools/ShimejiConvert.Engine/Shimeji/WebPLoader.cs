using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Decodes an Android-Shimeji bundle sprite (a WebP with alpha) into a <see cref="System.Drawing.Bitmap"/>
    /// the existing compositor understands.
    ///
    /// System.Drawing / GDI+ cannot decode WebP, so this goes through the Windows Imaging Component via WPF's
    /// <c>BitmapDecoder</c> (verified in-process on Win11; a pure decode needs no message pump). The frame is
    /// converted to straight (non-premultiplied) BGRA -- the exact byte layout of System.Drawing's
    /// Format32bppArgb -- and copied row by row, preserving the real alpha channel so the sheet builder can
    /// run in ALPHA mode. WIC also decodes PNG/JPEG the same way, which the bundle self-test relies on.
    ///
    /// Deliberately no NuGet/native dependency (no SkiaSharp): WIC handles these frames natively.
    /// </summary>
    public static class WebPLoader
    {
        /// <summary>A loader (matching <see cref="SpriteSheetBuilder"/>'s <c>Func&lt;string,Bitmap&gt;</c>) that
        /// reads a pose image name like "/0005.webp" from <paramref name="spritesDir"/> and WIC-decodes it.</summary>
        public static Func<string, Bitmap> ForDirectory(string spritesDir)
        {
            if (string.IsNullOrEmpty(spritesDir)) throw new ArgumentNullException("spritesDir");
            return delegate(string image)
            {
                string name = (image ?? "").TrimStart('/', '\\');
                string path = Path.Combine(spritesDir, name);
                if (!File.Exists(path)) throw new FileNotFoundException("Sprite not found: " + path);
                return Decode(path);
            };
        }

        /// <summary>Decode a single image file (WebP/PNG/... anything WIC handles) to a Format32bppArgb bitmap,
        /// preserving its alpha channel.</summary>
        public static Bitmap Decode(string path)
        {
            System.Windows.Media.Imaging.BitmapFrame frame;
            // OnLoad caches the pixels immediately, so the frame stays usable after the stream closes; a stream
            // (rather than a file:// Uri) sidesteps URI-escaping of paths with spaces or special characters.
            using (FileStream fs = File.OpenRead(path))
            {
                System.Windows.Media.Imaging.BitmapDecoder decoder =
                    System.Windows.Media.Imaging.BitmapDecoder.Create(
                        fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (decoder.Frames == null || decoder.Frames.Count == 0)
                    throw new InvalidOperationException("no frames decoded from " + path);
                frame = decoder.Frames[0];
            }

            // Force straight BGRA (not the premultiplied Pbgra32) so the bytes map 1:1 onto Format32bppArgb.
            System.Windows.Media.Imaging.FormatConvertedBitmap converted =
                new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
    }
}
