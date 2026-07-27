using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Orchestrates one "look at the screen and react" turn:
    /// capture -> (OCR text | downscaled image) -> Ollama -> parse {text, emotion}.
    /// Purely additive: it observes the screen and calls the backend; it never touches the
    /// pet physics engine. Any failure results in a null response (the pet stays silent).
    /// </summary>
    internal sealed class AiBrain : IDisposable
    {
        private readonly IPetBrainBackend _backend;
        private readonly AiSettings _settings;
        private readonly string _textModel;
        private readonly string _visionModel;
        private readonly bool _useVision;
        private readonly string _tesseractPath;

        private byte[] _lastFrameSignature;   // change-detection gate (used by the idle loop, phase 3)

        /// <summary>
        /// Build the system prompt fresh each call so it reflects the current persona (name, user,
        /// personality — backlog 5.5) and the time of day (5.2).
        /// </summary>
        private string BuildSystemPrompt()
        {
            string name    = string.IsNullOrWhiteSpace(_settings.PetName)     ? "a tiny desktop pet"    : _settings.PetName.Trim();
            string persona = string.IsNullOrWhiteSpace(_settings.Personality) ? "friendly and curious"  : _settings.Personality.Trim();
            string user    = string.IsNullOrWhiteSpace(_settings.UserName)    ? ""                      : (" Your human is called " + _settings.UserName.Trim() + ".");

            return
                "You are " + name + ", a tiny pet living on the user's screen. " +
                "Your personality: " + persona + "." + user + " It is currently " + TimeOfDay() + ". " +
                "You glance at what is on screen and make one short, in-character remark about it. " +
                "Keep it under 15 words. Do not use quotation marks in the remark. " +
                "Never say that you are an AI or a language model. " +
                "Reply ONLY with compact JSON of the form " +
                "{\"text\":\"<your remark>\",\"emotion\":\"<one of: happy, sad, thinking, excited, confused, neutral>\"}.";
        }

        /// <summary>Coarse time-of-day label for the persona (backlog 5.2).</summary>
        private static string TimeOfDay()
        {
            int h = DateTime.Now.Hour;
            if (h < 5)  return "late at night";
            if (h < 12) return "the morning";
            if (h < 17) return "the afternoon";
            if (h < 21) return "the evening";
            return "night";
        }

        public AiBrain(IPetBrainBackend backend, AiSettings settings)
        {
            _backend = backend;
            _settings = settings ?? new AiSettings();
            _textModel = string.IsNullOrWhiteSpace(_settings.TextModel) ? "llama3.1:8b" : _settings.TextModel;
            _visionModel = string.IsNullOrWhiteSpace(_settings.VisionModel) ? "mistral-small3.1:24b" : _settings.VisionModel;
            _useVision = _settings.UseVision;
            _tesseractPath = _settings.TesseractPath;
        }

        /// <summary>
        /// Launch-time preparation (fire-and-forget): optionally start the backend server, then
        /// preload the active model so the first ask doesn't pay the cold-start cost. Never throws.
        /// Returns true when the backend is reachable (used to drive the "AI ready" hint).
        /// </summary>
        public async Task<bool> PrepareAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                bool up = _settings.AutoStartServer
                    ? await _backend.EnsureServerAsync(ct).ConfigureAwait(false)
                    : await _backend.IsAvailableAsync(ct).ConfigureAwait(false);

                if (up && _settings.WarmUpOnLaunch)
                    await _backend.WarmUpAsync(_useVision ? _visionModel : _textModel, ct).ConfigureAwait(false);

                return up;
            }
            catch { return false; }
        }

        /// <summary>
        /// React to whatever is on screen. Returns null when the backend is unavailable or errors,
        /// so the caller can simply stay silent without special-casing exceptions.
        /// </summary>
        public async Task<BrainResponse> AskAboutScreenAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                if (!await _backend.IsAvailableAsync(ct).ConfigureAwait(false))
                    return null;

                using (Bitmap shot = CapturePrimaryScreen(1280))
                {
                    List<ChatMessage> messages = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()) };
                    string model;

                    // Context (backlog 5.1): tell the pet which window is currently in front.
                    string win = ActiveWindow.Title();
                    string winLine = string.IsNullOrWhiteSpace(win) ? "" : ("The active window is: " + win + "\n\n");

                    if (_useVision)
                    {
                        string b64 = ToBase64Png(shot);
                        messages.Add(ChatMessage.User(winLine + "Look at my screen and react.", new[] { b64 }));
                        model = _visionModel;
                    }
                    else
                    {
                        string ocr = RunOcr(shot);
                        if (string.IsNullOrWhiteSpace(ocr)) ocr = "(the screen has no readable text)";
                        if (ocr.Length > 1500) ocr = ocr.Substring(0, 1500);
                        messages.Add(ChatMessage.User(winLine + "Here is the text currently visible on my screen:\n\n" + ocr, null));
                        model = _textModel;
                    }

                    string raw = await ChatWithRetryAsync(model, messages, ct).ConfigureAwait(false);
                    return Parse(raw);
                }
            }
            catch
            {
                return null;   // never crash the app over the AI layer
            }
        }

        private async Task<string> ChatWithRetryAsync(string model, IList<ChatMessage> messages, CancellationToken ct)
        {
            try
            {
                return await _backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
            }
            catch
            {
                // one retry — transient socket/timeout hiccups are common on cold model loads
                return await _backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Change-detection gate for the (future) idle loop: true when the screen differs from the
        /// last checked frame by more than <paramref name="thresholdPercent"/> of average luma.
        /// First call always returns true. Cheap: compares a 16x16 grayscale signature.
        /// </summary>
        public bool ScreenChanged(int thresholdPercent = 4)
        {
            byte[] sig = ComputeSignature();
            if (_lastFrameSignature == null || _lastFrameSignature.Length != sig.Length)
            {
                _lastFrameSignature = sig;
                return true;
            }
            long delta = 0;
            for (int i = 0; i < sig.Length; i++) delta += Math.Abs(sig[i] - _lastFrameSignature[i]);
            _lastFrameSignature = sig;
            double avgDelta = delta / (double)sig.Length;             // 0..255
            return (avgDelta / 255.0 * 100.0) >= thresholdPercent;
        }

        // ---- screen capture ------------------------------------------------

        private static Bitmap CapturePrimaryScreen(int maxWidth)
        {
            Rectangle b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Bitmap full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(full))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size);

            if (b.Width <= maxWidth) return full;

            int h = (int)(b.Height * (maxWidth / (double)b.Width));
            Bitmap scaled = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, maxWidth, h);
            }
            full.Dispose();
            return scaled;
        }

        private static string ToBase64Png(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static byte[] ComputeSignature()
        {
            const int N = 16;
            using (Bitmap shot = CapturePrimaryScreen(256))
            using (Bitmap small = new Bitmap(N, N, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, 0, 0, N, N);
                }
                byte[] sig = new byte[N * N];
                int k = 0;
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        sig[k++] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                    }
                return sig;
            }
        }

        // ---- OCR via the tesseract executable ------------------------------

        private string RunOcr(Bitmap bmp)
        {
            string exe = ResolveTesseract();
            string tmpPng = Path.Combine(Path.GetTempPath(), "pet_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                bmp.Save(tmpPng, ImageFormat.Png);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "\"" + tmpPng + "\" stdout",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Help tesseract find its language data when running from a portable/toolbox layout.
                try
                {
                    string exeDir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        string tessdata = Path.Combine(exeDir, "tessdata");
                        if (Directory.Exists(tessdata))
                            psi.EnvironmentVariables["TESSDATA_PREFIX"] = tessdata;
                    }
                }
                catch { }

                using (Process p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return CleanOcr(outText);
                }
            }
            catch
            {
                return "";   // tesseract missing or failed -> no OCR text
            }
            finally
            {
                try { File.Delete(tmpPng); } catch { }
            }
        }

        private string ResolveTesseract()
        {
            if (!string.IsNullOrWhiteSpace(_tesseractPath) && File.Exists(_tesseractPath))
                return _tesseractPath;
            return "tesseract";   // rely on PATH; a missing exe throws in Process.Start and is caught
        }

        private static string CleanOcr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        // ---- response parsing ----------------------------------------------

        private static BrainResponse Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                JObject o = JObject.Parse(raw);
                string text = (string)o["text"];
                string emotion = (string)o["emotion"];
                if (!string.IsNullOrWhiteSpace(text))
                    return new BrainResponse(text, emotion);
            }
            catch
            {
                // not JSON -> fall through to plain-text fallback
            }
            return new BrainResponse(raw.Trim(), "neutral");
        }

        public void Dispose()
        {
            if (_backend != null) _backend.Dispose();
        }
    }
}
