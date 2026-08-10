using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.Modules;   // ABI ScreenContext / PixelRect (replaces the base ScreenCaptureContext)
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
        private readonly ChatHistory _history;   // rolling conversation memory (5.3/5.4); null when disabled

        private byte[] _lastFrameSignature;   // change-detection gate (used by the idle loop, phase 3)
        private int _disposeStarted;

        // Vision images are downscaled to this width before sending — full-screen frames make a
        // vision model crawl (tens of seconds). OCR keeps the larger capture for legibility.
        private const int VisionMaxWidth = 896;
        private const int MaximumCaptureWidth = 2048;
        private const int MaximumCaptureHeight = 2048;
        private const int MaximumCapturePixels = 4 * 1024 * 1024;
        private const int MaximumResponseCharacters = 512;
        private const int MaximumEmotionCharacters = 32;
        private const int Srccopy = 0x00CC0020;
        private const int Captureblt = 0x40000000;
        private const int Halftone = 4;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            IntPtr source,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            int rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);

        /// <summary>
        /// Build the system prompt fresh each call so it reflects the current persona (name, user,
        /// personality — backlog 5.5) and the time of day (5.2).
        /// </summary>
        private string BuildSystemPrompt()
        {
            string name    = string.IsNullOrWhiteSpace(_settings.PetName)     ? "a tiny desktop pet"    : _settings.PetName.Trim();
            string persona = string.IsNullOrWhiteSpace(_settings.Personality) ? "friendly and curious"  : _settings.Personality.Trim();
            string userName = string.IsNullOrWhiteSpace(_settings.UserName) ? "" : _settings.UserName.Trim();
            // Force the configured name and forbid reading a name off the screen — window titles and paths
            // ("Administrator", "C:\\Users\\Admin", ...) were being picked up as the user's name.
            string user = userName.Length == 0
                ? " You do not know your human's name, so never invent one or read a name, username or handle off the screen."
                : (" Your human is called " + userName + ". Always address them as " + userName +
                   "; never use any other name, username or handle you see on the screen.");
            string speech  = Personas.SpeechInstruction(_settings.SpeechPattern);
            string speechClause = speech.Length == 0 ? "" :
                (" Speech style (apply to the remark text only, keep the JSON exactly as specified): " + speech);

            return
                "You are " + name + ", a tiny pet living on the user's screen. " +
                "Your personality: " + persona + ". Commit to it fully and stay in character in every word." + user +
                " It is currently " + TimeOfDay() + ". " +
                "You glance at what is on screen and make one short, in-character remark about it. " +
                "Be vivid and specific to your personality; never bland, generic or merely polite. " +
                "Keep it under 15 words. Do not use quotation marks in the remark. " +
                "Never say that you are an AI or a language model." + speechClause + " " +
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
            string normalizedModel;
            _textModel = AiModelPolicy.TryNormalize(
                _settings.TextModel, out normalizedModel)
                ? normalizedModel
                : "llama3.1:8b";
            _visionModel = AiModelPolicy.TryNormalize(
                _settings.VisionModel, out normalizedModel)
                ? normalizedModel
                : "gemma3:4b";
            _useVision = _settings.UseVision;
            _tesseractPath = _settings.TesseractPath;
            _history = _settings.MemoryEnabled ? ChatHistory.Load(_settings) : null;
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
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// Ask the backend to unload this pet's text and vision models. Ollama evicts its
        /// keep-alive models; generic OpenAI-compatible providers intentionally do nothing.
        /// Best-effort.
        /// </summary>
        public async Task UnloadAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                await _backend.UnloadAsync(_textModel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (!string.Equals(_visionModel, _textModel, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _backend.UnloadAsync(_visionModel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        /// <summary>
        /// React to whatever is on screen. Returns null when the backend is unavailable or errors,
        /// so the caller can simply stay silent without special-casing exceptions.
        /// </summary>
        public async Task<BrainResponse> AskAboutScreenAsync(
            ScreenContext captureContext,
            string petZone = null,
            bool allowVision = true,
            CancellationToken ct = default(CancellationToken))
        {
            try
            {
                if (!await _backend.IsAvailableAsync(ct).ConfigureAwait(false))
                    return null;

                Rectangle captureBounds;
                if (captureContext != null)
                {
                    PixelRect mb = captureContext.MonitorBounds;
                    captureBounds = new Rectangle(mb.X, mb.Y, mb.Width, mb.Height);
                }
                else
                {
                    System.Windows.Forms.Screen primary =
                        System.Windows.Forms.Screen.PrimaryScreen;
                    if (primary == null)
                        throw new InvalidOperationException(
                            "No display is available for screen capture.");
                    captureBounds = primary.Bounds;
                }
                using (Bitmap shot = CaptureScreen(captureBounds, 1280))
                {
                    List<ChatMessage> messages = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()) };

                    // Memory (backlog 5.3): replay recent exchanges so the pet stays continuous.
                    if (_history != null)
                        foreach (ChatMessage prior in _history.RecentMessages())
                            messages.Add(prior);

                    string model;

                    // Context: the front window (5.1) and the window the pet is standing on (5.6).
                    string win = captureContext != null
                        ? captureContext.WindowTitle
                        : "";
                    string ctx = "";
                    if (!string.IsNullOrWhiteSpace(win))     ctx += "The active window is: " + win + "\n";
                    if (!string.IsNullOrWhiteSpace(petZone)) ctx += "You are standing on the window: " + petZone.Trim() + "\n";
                    if (ctx.Length > 0) ctx += "\n";

                    // Routing (backlog 6.2): vision only for explicit asks; idle stays on the fast
                    // text path since a vision glance can take tens of seconds.
                    if (_useVision && allowVision)
                    {
                        string b64 = ToBase64PngScaled(shot, VisionMaxWidth);
                        messages.Add(ChatMessage.User(ctx + "Look at my screen and react.", new[] { b64 }));
                        model = _visionModel;
                    }
                    else
                    {
                        string ocr = await RunOcrAsync(shot, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(ocr)) ocr = "(the screen has no readable text)";
                        ocr = UnicodeTextProgress.TruncateAtCodePointBoundary(
                            ocr,
                            1500);
                        messages.Add(ChatMessage.User(ctx + "Here is the text currently visible on my screen:\n\n" + ocr, null));
                        model = _textModel;
                    }

                    string raw = await ChatWithRetryAsync(model, messages, ct).ConfigureAwait(false);
                    BrainResponse resp = Parse(raw);

                    // Memory (backlog 5.4): remember this exchange (compact context + reply).
                    if (_history != null && resp != null && !string.IsNullOrWhiteSpace(resp.Text))
                        _history.Add(string.IsNullOrWhiteSpace(win) ? "(the screen)" : win, resp.Text);

                    return resp;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;   // never crash the app over the AI layer
            }
        }

        private async Task<string> ChatWithRetryAsync(string model, IList<ChatMessage> messages, CancellationToken ct)
        {
            return await ChatWithRetryForDiagnosticsAsync(
                _backend,
                model,
                messages,
                ct).ConfigureAwait(false);
        }

        internal static async Task<string> ChatWithRetryForDiagnosticsAsync(
            IPetBrainBackend backend,
            string model,
            IList<ChatMessage> messages,
            CancellationToken ct)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            try
            {
                return await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient reports its own timeout as TaskCanceledException.
            }
            catch (TimeoutException)
            {
                // AiEndpointPolicy reports its explicit end-to-end deadline this way.
            }
            catch (AiBackendHttpException ex) when (ex.IsTransient) { }
            catch (HttpRequestException ex) when (!(ex is AiBackendHttpException)) { }

            ct.ThrowIfCancellationRequested();
            return await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Change-detection gate for the (future) idle loop: true when the screen differs from the
        /// last checked frame by more than <paramref name="thresholdPercent"/> of average luma.
        /// First call always returns true. Cheap: compares a 16x16 grayscale signature.
        /// </summary>
        public bool ScreenChanged(
            Rectangle captureBounds,
            int thresholdPercent = 4)
        {
            byte[] sig = ComputeSignature(captureBounds);
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

        private static Bitmap CaptureScreen(Rectangle b, int maxWidth)
        {
            if (b.Width <= 0 || b.Height <= 0 ||
                b.Width > 32768 || b.Height > 32768)
                throw new InvalidOperationException("The selected display dimensions are invalid.");

            int targetWidth = Math.Min(
                b.Width,
                Math.Max(1, Math.Min(MaximumCaptureWidth, maxWidth)));
            int targetHeight = Math.Max(
                1,
                (int)Math.Round(b.Height * (targetWidth / (double)b.Width)));
            if (targetHeight > MaximumCaptureHeight)
            {
                targetWidth = Math.Max(
                    1,
                    (int)Math.Round(targetWidth *
                        (MaximumCaptureHeight / (double)targetHeight)));
                targetHeight = MaximumCaptureHeight;
            }
            if ((long)targetWidth * targetHeight > MaximumCapturePixels)
                throw new InvalidOperationException("The screen capture exceeds its pixel budget.");

            Bitmap capture = null;
            IntPtr sourceDc = IntPtr.Zero;
            try
            {
                capture = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                sourceDc = GetDC(IntPtr.Zero);
                if (sourceDc == IntPtr.Zero)
                    throw new InvalidOperationException("The screen device context is unavailable.");

                using (Graphics graphics = Graphics.FromImage(capture))
                {
                    IntPtr destinationDc = graphics.GetHdc();
                    try
                    {
                        SetStretchBltMode(destinationDc, Halftone);
                        if (!StretchBlt(
                                destinationDc,
                                0,
                                0,
                                targetWidth,
                                targetHeight,
                                sourceDc,
                                b.Left,
                                b.Top,
                                b.Width,
                                b.Height,
                                Srccopy | Captureblt))
                            throw new InvalidOperationException("Screen capture failed.");
                    }
                    finally
                    {
                        graphics.ReleaseHdc(destinationDc);
                    }
                }

                Bitmap result = capture;
                capture = null;
                return result;
            }
            finally
            {
                if (sourceDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sourceDc);
                if (capture != null) capture.Dispose();
            }
        }

        private static string ToBase64Png(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// Base64 PNG of the bitmap, first downscaled to <paramref name="maxWidth"/> so a vision
        /// model doesn't choke on a full-screen frame. Returns the unscaled PNG if already small.
        /// </summary>
        private static string ToBase64PngScaled(Bitmap bmp, int maxWidth)
        {
            if (bmp.Width <= maxWidth) return ToBase64Png(bmp);

            int h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
            using (Bitmap scaled = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, maxWidth, h);
                }
                return ToBase64Png(scaled);
            }
        }

        private static byte[] ComputeSignature(Rectangle captureBounds)
        {
            const int N = 16;
            using (Bitmap shot = CaptureScreen(captureBounds, 256))
            using (Bitmap small = new Bitmap(N, N, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, 0, 0, N, N);
                }
                byte[] sig = new byte[N * N];
                // Read the whole 16x16 in one LockBits pass instead of 256 GetPixel calls.
                // Format24bppRgb stores pixels as B,G,R (3 bytes each); rows are padded to Stride.
                BitmapData data = small.LockBits(
                    new Rectangle(0, 0, N, N),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    byte[] pixels = new byte[stride * N];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    int k = 0;
                    for (int y = 0; y < N; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < N; x++)
                        {
                            int p = row + x * 3;
                            byte blue  = pixels[p];
                            byte green = pixels[p + 1];
                            byte red   = pixels[p + 2];
                            sig[k++] = (byte)((red * 30 + green * 59 + blue * 11) / 100);
                        }
                    }
                }
                finally
                {
                    small.UnlockBits(data);
                }
                return sig;
            }
        }

        // ---- OCR via the tesseract executable ------------------------------

        private async Task<string> RunOcrAsync(Bitmap bmp, CancellationToken ct)
        {
            string exe = ResolveTesseract();
            if (string.IsNullOrEmpty(exe)) return "";
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

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.EnableRaisingEvents = true;

                    var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    p.Exited += delegate
                    {
                        try { exited.TrySetResult(p.ExitCode); }
                        catch { exited.TrySetResult(-1); }
                    };

                    if (!p.Start()) return "";
                    using (ProcessJob job = ProcessJob.TryAttach(p))
                    {
                        if (p.HasExited) exited.TrySetResult(p.ExitCode);

                        Task<string> stdout = ReadBoundedAsync(p.StandardOutput, 32768);
                        Task<string> stderr = ReadBoundedAsync(p.StandardError, 8192);
                        Task timeout = Task.Delay(TimeSpan.FromSeconds(8), ct);
                        Task finished = await Task.WhenAny(exited.Task, timeout).ConfigureAwait(false);

                        if (finished != exited.Task)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            ObserveFailure(stdout);
                            ObserveFailure(stderr);
                            ct.ThrowIfCancellationRequested();
                            return "";
                        }

                        int exitCode = await exited.Task.ConfigureAwait(false);
                        Task drain = Task.WhenAll(stdout, stderr);
                        Task drained = await Task.WhenAny(
                            drain,
                            Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
                        if (drained != drain)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            ObserveFailure(drain);
                            ct.ThrowIfCancellationRequested();
                            return "";
                        }

                        await drain.ConfigureAwait(false);
                        if (exitCode != 0) return "";
                        return CleanOcr(stdout.Result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
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

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
        {
            char[] buffer = new char[1024];
            StringBuilder retained = new StringBuilder(Math.Min(maxCharacters, 4096));
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                int remaining = maxCharacters - retained.Length;
                if (remaining > 0)
                    retained.Append(buffer, 0, Math.Min(remaining, read));
            }
            return retained.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                int processId = process.Id;
                using (Process killer = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = "/PID " + processId + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (killer != null) killer.WaitForExit(2000);
                }
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }
        }

        private static void ObserveFailure(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                delegate(Task failed)
                {
                    if (failed.Exception != null)
                        failed.Exception.Handle(delegate(Exception ignored) { return true; });
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private string ResolveTesseract()
        {
            if (!string.IsNullOrWhiteSpace(_tesseractPath))
                return AiExecutablePolicy.ResolveConfigured(
                    _tesseractPath,
                    "tesseract.exe");

            string[] candidates =
            {
                Environment.ExpandEnvironmentVariables(
                    @"%ProgramFiles%\Tesseract-OCR\tesseract.exe"),
                Environment.ExpandEnvironmentVariables(
                    @"%LOCALAPPDATA%\Programs\Tesseract-OCR\tesseract.exe")
            };
            foreach (string candidate in candidates)
            {
                string resolved = AiExecutablePolicy.ResolveConfigured(
                    candidate,
                    "tesseract.exe");
                if (resolved != null) return resolved;
            }

            return AiExecutablePolicy.ResolveFromPath(
                Environment.GetEnvironmentVariable("PATH"),
                "tesseract.exe");
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
                string text = SanitizeResponseText((string)o["text"]);
                string emotion = NormalizeEmotion((string)o["emotion"]);
                if (!string.IsNullOrWhiteSpace(text))
                    return new BrainResponse(text, emotion);
            }
            catch
            {
                // not JSON -> fall through to plain-text fallback
            }
            string fallback = SanitizeResponseText(raw);
            return string.IsNullOrWhiteSpace(fallback)
                ? null
                : new BrainResponse(fallback, "neutral");
        }

        internal static string SanitizeResponseText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var clean = new StringBuilder(Math.Min(value.Length, MaximumResponseCharacters));
            bool pendingSpace = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = clean.Length > 0;
                    continue;
                }

                int codeUnits = 1;
                if (char.IsHighSurrogate(c))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                        continue;
                    codeUnits = 2;
                }
                else if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                int spaceUnits = pendingSpace && clean.Length > 0 ? 1 : 0;
                if (clean.Length + spaceUnits + codeUnits > MaximumResponseCharacters)
                    break;
                if (spaceUnits != 0)
                    clean.Append(' ');
                pendingSpace = false;
                clean.Append(c);
                if (codeUnits == 2)
                    clean.Append(value[++index]);
            }
            return clean.ToString().Trim();
        }

        private static string NormalizeEmotion(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEmotionCharacters)
                return "neutral";
            switch (value.Trim().ToLowerInvariant())
            {
                case "happy":
                case "sad":
                case "thinking":
                case "excited":
                case "confused":
                case "neutral":
                    return value.Trim().ToLowerInvariant();
                default:
                    return "neutral";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            if (_backend != null) _backend.Dispose();
        }

        /// <summary>
        /// Best-effort job containment for OCR. Closing the job kills descendants, including the
        /// case where the OCR parent exits while a child keeps redirected pipes open forever.
        /// </summary>
        private sealed class ProcessJob : IDisposable
        {
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private const int JobObjectExtendedLimitInformation = 9;
            private IntPtr _handle;

            private ProcessJob(IntPtr handle)
            {
                _handle = handle;
            }

            public static ProcessJob TryAttach(Process process)
            {
                IntPtr job = IntPtr.Zero;
                IntPtr info = IntPtr.Zero;
                try
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return null;

                    var limits = new JobObjectExtendedLimitInformationData();
                    limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationData));
                    info = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(limits, info, false);
                    if (!SetInformationJobObject(
                            job,
                            JobObjectExtendedLimitInformation,
                            info,
                            (uint)size) ||
                        !AssignProcessToJobObject(job, process.Handle))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                        return null;
                    }

                    ProcessJob result = new ProcessJob(job);
                    job = IntPtr.Zero;
                    return result;
                }
                catch
                {
                    if (job != IntPtr.Zero) CloseHandle(job);
                    return null;
                }
                finally
                {
                    if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                }
            }

            public void Terminate()
            {
                if (_handle == IntPtr.Zero) return;
                try { TerminateJobObject(_handle, 1); } catch { }
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(
                IntPtr jobAttributes,
                string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(
                IntPtr job,
                IntPtr process);

            [DllImport("kernel32.dll")]
            private static extern bool TerminateJobObject(
                IntPtr job,
                uint exitCode);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformationData
            {
                public JobObjectBasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
