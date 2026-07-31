using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DesktopPet
{
    public partial class Form1 : Form
    {
        string XmlContent = "";
        Xml XmlClass;
        Animations XmlAni;
        XmlData.RootNode XmlNode;
        readonly ValidationLoadCoordinator validationLoads =
            new ValidationLoadCoordinator();
        ValidationLoadSession activeDownloadSession;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly TimeSpan DownloadDeadline = TimeSpan.FromSeconds(15);
        private long lastPublishedValidationGeneration;

        internal Action<CancellationToken> ValidationWorkProbeForTest { get; set; }
        internal long LastPublishedValidationGeneration
        {
            get { return Interlocked.Read(ref lastPublishedValidationGeneration); }
        }

        private sealed class PetTesterValidationResult : IDisposable
        {
            public XmlData.RootNode Root;
            public Xml Xml;
            public Animations Animations;
            public readonly StringBuilder Output = new StringBuilder();
            public int Errors;
            public int Warnings;
            public int CheckedSpawns;
            public int TotalSpawns;
            public int CheckedAnimations;
            public int TotalAnimations;
            public int CheckedChildren;
            public int TotalChildren;
            public int CheckedLinks;
            public int TotalLinks;
            public bool Succeeded;

            public void Detach(out Xml xml, out Animations animations)
            {
                xml = Xml;
                animations = Animations;
                Xml = null;
                Animations = null;
            }

            public void Dispose()
            {
                Animations animations = Animations;
                Animations = null;
                if (animations != null) animations.Dispose();

                Xml xml = Xml;
                Xml = null;
                if (xml != null) xml.Dispose();
            }
        }

        public Form1()
        {
            InitializeComponent();
            FormClosed += delegate
            {
                validationLoads.Dispose();
                DisposeLoadedPet();
            };
        }

        internal static bool TryResolveDroppedPetFile(
            string path,
            out string canonicalPath,
            out string error)
        {
            canonicalPath = null;
            error = null;

            string fileName;
            try
            {
                fileName = Path.GetFileName(path ?? "");
            }
            catch (Exception ex)
            {
                error = "The dropped path is invalid: " + ex.Message;
                return false;
            }

            if (!string.Equals(
                    fileName,
                    "animations.xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "The animation must be inside a file called " +
                    "'animations.xml', not '" + (path ?? "") + "'.";
                return false;
            }

            return PetXmlValidator.TryResolveLocalXmlFile(
                path,
                out canonicalPath,
                out error);
        }

        private void Form1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void Form1_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if(files.Length != 1)
            { 
                MessageBox.Show("Please insert only 1 file.");
            }
            else
            {
                string canonicalPath;
                string error;
                if (!TryResolveDroppedPetFile(
                        files[0],
                        out canonicalPath,
                        out error))
                {
                    MessageBox.Show(error);
                }
                else
                {
                    tableLayoutPanel1.Visible = true;
                    OpenXMLFile(canonicalPath);
                }
            }
        }

        private void checkBox1_Click(object sender, EventArgs e)
        {
            switch((sender as CheckBox).Tag)
            {
                case 0:  (sender as CheckBox).CheckState = CheckState.Unchecked; break;
                case 1: (sender as CheckBox).CheckState = CheckState.Indeterminate; break;
                case 2: (sender as CheckBox).CheckState = CheckState.Checked; break;
            }
            (sender as CheckBox).Checked = !(sender as CheckBox).Checked;
        }

        private async void OpenXMLFile(string fileName)
        {
            ValidationLoadSession session = validationLoads.Begin();
            ResetValidationState();
            try
            {
                PetXmlValidator.RetainedLocalXmlFile retained;
                string pathError;
                if (!PetXmlValidator.TryOpenLocalXmlFile(
                        fileName,
                        out retained,
                        out pathError))
                    throw new InvalidDataException(pathError);
                using (retained)
                using (var stream = retained.OpenRead(65536))
                {
                    string content = await ReadBoundedUtf8Async(
                        stream,
                        PetXmlValidator.MaximumXmlBytes,
                        session.Token);
                    if (!CanPublish(session)) return;
                    XmlContent = content;
                }

                if (!CanPublish(session)) return;
                checkBox1.CheckState = CheckState.Checked;
                checkBox1.Tag = 2;
                label2.Text = "SUCCESS";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch(Exception ex)
            {
                if (!CanPublish(session)) return;
                checkBox1.CheckState = CheckState.Unchecked;
                checkBox1.Tag = 0;
                label2.Text = "FAILED: " + ex.Message;
            }

            if (checkBox1.CheckState == CheckState.Checked)
            {
                await AnalyseXMLFile(session);
            }
            validationLoads.Complete(session);
        }

        private async Task AnalyseXMLFile(ValidationLoadSession session)
        {
            if (!CanPublish(session)) return;
            checkBox2.CheckState = CheckState.Indeterminate;
            checkBox2.Tag = 1;
            checkBox3.CheckState = CheckState.Indeterminate;
            checkBox3.Tag = 1;

            PetTesterValidationResult result = null;
            try
            {
                string content = XmlContent;
                result = await Task.Run(
                    () => BuildValidationResult(content, session.Token),
                    session.Token);
                session.Token.ThrowIfCancellationRequested();
                if (!CanPublish(session)) return;
                PublishValidationResult(session, result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!CanPublish(session)) return;
                checkBox2.CheckState = CheckState.Unchecked;
                checkBox2.Tag = 0;
                checkBox3.CheckState = CheckState.Unchecked;
                checkBox3.Tag = 0;
                label3.Text = "FAILED: " + ex.Message;
                label4.Text = "FAILED";
                textBox1.Visible = true;
                textBox1.Text = "XML validation failed: " + ex.Message + "\r\n";
                DisposeLoadedPet();
                Interlocked.Exchange(
                    ref lastPublishedValidationGeneration,
                    session.Generation);
            }
            finally
            {
                if (result != null) result.Dispose();
            }
        }

        private PetTesterValidationResult BuildValidationResult(
            string content,
            CancellationToken cancellationToken)
        {
            Action<CancellationToken> probe = ValidationWorkProbeForTest;
            if (probe != null) probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            XmlData.RootNode root;
            string validationError;
            if (!PetXmlValidator.TryParse(
                content,
                out root,
                out validationError,
                cancellationToken))
            {
                throw new InvalidDataException(validationError);
            }

            var result = new PetTesterValidationResult { Root = root };
            try
            {
                result.Xml = new Xml();
                result.Animations = new Animations(result.Xml);
                byte[] imageBytes = DecodeBase64(root.Image.Png);
                cancellationToken.ThrowIfCancellationRequested();
                using (var imageStream = new MemoryStream(imageBytes, false))
                using (Image decoded = Image.FromStream(imageStream, true, true))
                using (var image = new Bitmap(decoded))
                {
                    int spriteWidth = image.Width / root.Image.TilesX;
                    int spriteHeight = image.Height / root.Image.TilesY;
                    IList<Bitmap> sprites = BuildSprites(
                        image,
                        spriteWidth,
                        spriteHeight,
                        cancellationToken);
                    try
                    {
                        result.Xml.ReplaceSpriteFrames(
                            sprites,
                            spriteWidth,
                            spriteHeight);
                        sprites = null;
                    }
                    finally
                    {
                        if (sprites != null)
                            foreach (Bitmap sprite in sprites)
                                if (sprite != null) sprite.Dispose();
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                result.Xml.bitmapIcon = new MemoryStream(
                    DecodeBase64(root.Header.Icon),
                    false);
                AnalyseAnimations(result, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void PublishValidationResult(
            ValidationLoadSession session,
            PetTesterValidationResult result)
        {
            if (!CanPublish(session)) return;

            checkBox2.CheckState = CheckState.Checked;
            checkBox2.Tag = 2;
            label3.Text = "XML IS VALID";
            textBox1.Visible = true;
            textBox1.Text =
                "XML schema, structure, and resource budgets: PASS\r\n" +
                result.Output +
                "Errors: " + result.Errors + ", Warnings: " +
                result.Warnings + "\r\n";
            XmlNode = result.Root;

            bool succeeded = result.Succeeded;
            checkBox3.CheckState = succeeded
                ? CheckState.Checked
                : CheckState.Unchecked;
            checkBox3.Tag = succeeded ? 2 : 0;
            if (succeeded)
            {
                result.Xml.bitmapIcon.Position = 0;
                var icon = new Bitmap(result.Xml.bitmapIcon);
                Xml xml;
                Animations animations;
                result.Detach(out xml, out animations);
                XmlClass = xml;
                XmlAni = animations;

                pictureBox1.Width = XmlClass.spriteWidth;
                pictureBox1.Height = XmlClass.spriteHeight;
                pictureBox2.Image = icon;
                timer1.Tag = 0;
                timer1.Enabled = true;
                timer1.Start();
                UpdateAnimationsState(
                    result.CheckedSpawns,
                    result.TotalSpawns,
                    result.CheckedAnimations,
                    result.TotalAnimations,
                    result.CheckedChildren,
                    result.TotalChildren,
                    result.CheckedLinks,
                    result.TotalLinks);
            }
            else
            {
                timer1.Stop();
                timer1.Enabled = false;
                pictureBox1.Image = null;
                Image oldIcon = pictureBox2.Image;
                pictureBox2.Image = null;
                if (oldIcon != null) oldIcon.Dispose();
                DisposeLoadedPet();
                label4.Text = "FAILED";
            }

            Interlocked.Exchange(
                ref lastPublishedValidationGeneration,
                session.Generation);
        }

        private static IList<Bitmap> BuildSprites(
            Bitmap spriteSheet,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var sprites = new List<Bitmap>();
            try
            {
                for (var yOffset = 0; yOffset < spriteSheet.Height; yOffset += height)
                {
                    for (var xOffset = 0; xOffset < spriteSheet.Width; xOffset += width)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Bitmap frame = null;
                        try
                        {
                            frame = new Bitmap(
                                width,
                                height,
                                PixelFormat.Format32bppPArgb);
                            using (var graphics = Graphics.FromImage(frame))
                            {
                                var sourceRectangle =
                                    new Rectangle(xOffset, yOffset, width, height);
                                graphics.DrawImage(
                                    spriteSheet,
                                    new Rectangle(0, 0, width, height),
                                    sourceRectangle,
                                    GraphicsUnit.Pixel);
                            }
                            sprites.Add(frame);
                            frame = null;
                        }
                        finally
                        {
                            if (frame != null) frame.Dispose();
                        }
                    }
                }
                return sprites;
            }
            catch
            {
                foreach (Bitmap sprite in sprites) sprite.Dispose();
                throw;
            }
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            ValidationLoadSession session = null;
            Uri uri;
            try
            {
                if (!Uri.TryCreate(
                    textBox2.Text,
                    UriKind.Absolute,
                    out uri) ||
                    (!string.Equals(
                        uri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase) &&
                     !(string.Equals(
                         uri.Scheme,
                         Uri.UriSchemeHttp,
                         StringComparison.OrdinalIgnoreCase) &&
                       uri.IsLoopback)))
                {
                    throw new InvalidDataException(
                        "Use an HTTPS URL (HTTP is allowed only for loopback development).");
                }

                session = validationLoads.Begin();
                activeDownloadSession = session;
                button1.Enabled = false;
                string content = await DownloadPetXmlAsync(
                    uri,
                    DownloadDeadline,
                    session.Token);

                if (!CanPublish(session)) return;
                XmlContent = content;
                tableLayoutPanel1.Visible = true;
                ResetValidationState(false);
                checkBox1.CheckState = CheckState.Checked;
                checkBox1.Tag = 2;
                label2.Text = "SUCCESS";
                await AnalyseXMLFile(session);
            }
            catch (TimeoutException ex)
            {
                if (session == null || CanPublish(session))
                    MessageBox.Show(ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Superseded loads and form closure are expected cancellation paths.
            }
            catch(Exception ex)
            {
                if (session == null || CanPublish(session))
                    MessageBox.Show("Error: " + ex.Message);
            }
            finally
            {
                if (session != null)
                    validationLoads.Complete(session);
                if (ReferenceEquals(activeDownloadSession, session))
                {
                    activeDownloadSession = null;
                }
                if (CanAccessControls() && activeDownloadSession == null)
                    button1.Enabled = true;
            }
        }

        private void ResetValidationState(bool clearContent = true)
        {
            timer1.Stop();
            timer1.Enabled = false;
            pictureBox1.Image = null;
            Image oldIcon = pictureBox2.Image;
            pictureBox2.Image = null;
            if (oldIcon != null) oldIcon.Dispose();
            DisposeLoadedPet();

            checkBox1.Checked = false;
            checkBox2.Checked = false;
            checkBox3.Checked = false;
            label2.Text = "-";
            label3.Text = "-";
            label4.Text = "-";
            checkBox1.Tag = 0;
            checkBox2.Tag = 0;
            checkBox3.Tag = 0;
            textBox1.Visible = false;
            textBox1.Text = "";
            XmlNode = null;
            if (clearContent) XmlContent = "";

            checkBox1.CheckState = CheckState.Indeterminate;
            checkBox1.Tag = 1;
        }

        private void DisposeLoadedPet()
        {
            Animations animations = XmlAni;
            XmlAni = null;
            if (animations != null) animations.Dispose();

            Xml xml = XmlClass;
            XmlClass = null;
            if (xml != null) xml.Dispose();
        }

        private bool CanPublish(ValidationLoadSession session)
        {
            return CanAccessControls() && validationLoads.IsCurrent(session);
        }

        private bool CanAccessControls()
        {
            return !IsDisposed && !Disposing && IsHandleCreated;
        }

        private static byte[] DecodeBase64(string value)
        {
            int marker = value == null
                ? -1
                : value.IndexOf(
                    ";base64,",
                    StringComparison.OrdinalIgnoreCase);
            return Convert.FromBase64String(
                marker >= 0 ? value.Substring(marker + 8) : value ?? "");
        }

        private static async Task<string> ReadBoundedUtf8Async(
            Stream stream,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (maximumBytes < 1)
                throw new ArgumentOutOfRangeException("maximumBytes");

            var buffer = new byte[65536];
            using (var output = new MemoryStream(
                Math.Min(maximumBytes, buffer.Length)))
            {
                while (true)
                {
                    int remaining = maximumBytes + 1 - (int)output.Length;
                    int read = await ReadWithCancellationAsync(
                        stream,
                        buffer,
                        0,
                        Math.Min(buffer.Length, remaining),
                        cancellationToken);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    if (output.Length > maximumBytes)
                        throw new InvalidDataException(
                            "Pet XML exceeds the 4 MiB limit.");
                }

                string value = StrictUtf8.GetString(output.ToArray());
                return value.Length > 0 && value[0] == '\uFEFF'
                    ? value.Substring(1)
                    : value;
            }
        }

        private static async Task<int> ReadWithCancellationAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<int> readTask = stream.ReadAsync(
                buffer,
                offset,
                count,
                cancellationToken);
            if (readTask.IsCompleted)
                return await readTask;

            var cancellationSignal = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                cancellationSignal))
            {
                Task completed = await Task.WhenAny(
                    readTask,
                    cancellationSignal.Task);
                if (completed != readTask)
                {
                    ObserveFault(readTask);
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            return await readTask;
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(
                completed =>
                {
                    completed.Exception.Handle(exception => true);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                    TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private static async Task<string> DownloadPetXmlAsync(
            Uri uri,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            if (uri == null) throw new ArgumentNullException("uri");
            if (deadline <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("deadline");

            using (var requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            using (var client = new HttpClient(handler)
            {
                // ResponseHeadersRead ends HttpClient's built-in timeout at the
                // headers. The linked deadline below intentionally owns both
                // the request and every streamed body read.
                Timeout = Timeout.InfiniteTimeSpan
            })
            {
                requestCancellation.CancelAfter(deadline);
                try
                {
                    using (HttpResponseMessage response = await client.GetAsync(
                        uri,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCancellation.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength.Value >
                                PetXmlValidator.MaximumXmlBytes)
                        {
                            throw new InvalidDataException(
                                "Pet XML exceeds the 4 MiB limit.");
                        }

                        using (Stream stream =
                            await response.Content.ReadAsStreamAsync())
                        {
                            return await ReadBoundedUtf8Async(
                                stream,
                                PetXmlValidator.MaximumXmlBytes,
                                requestCancellation.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested &&
                          requestCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The download timed out before the complete response body was received.");
                }
            }
        }

        private static void AnalyseAnimations(
            PetTesterValidationResult result,
            CancellationToken cancellationToken)
        {
            XmlData.RootNode root = result.Root;
            Xml xml = result.Xml;
            Animations loadedAnimations = result.Animations;
            StringBuilder output = result.Output;
            int errors = 0;
            int warnings = 0;
            int totalLinks = 0;
            var animationIds = new HashSet<int>();
            var spawnIds = new HashSet<int>();
            var animationById = new Dictionary<int, XmlData.AnimationNode>();

            result.TotalSpawns = root.Spawns.Spawn.Length;
            result.TotalAnimations = root.Animations.Animation.Length;
            result.TotalChildren = root.Childs == null ||
                root.Childs.Child == null
                ? 0
                : root.Childs.Child.Length;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (root.Spawns.Spawn.Length < 1)
                {
                    output.AppendLine(
                        "SPAWN ERROR: The animation need at least 1 spawn.");
                    errors++;
                }
                if (root.Animations.Animation.Length < 3)
                {
                    output.AppendLine(
                        "ANIMATION WARNING: This pet defines fewer than 3 animations.");
                    warnings++;
                }

                bool fall = false;
                bool drag = false;
                bool kill = false;
                bool sync = false;

                xml.bitmapIcon.Position = 0;
                using (var icon = new Bitmap(xml.bitmapIcon))
                {
                    if (icon.Width != 48 || icon.Height != 48)
                    {
                        output.AppendLine(
                            "ICON ERROR: Size must be 48x48 (not " +
                            icon.Width + "x" + icon.Height + ").");
                        errors++;
                    }
                }
                xml.bitmapIcon.Position = 0;

                foreach (var spawn in root.Spawns.Spawn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!spawnIds.Add(spawn.Id))
                    {
                        output.AppendLine(
                            "SPAWN ERROR: The spawn ID " + spawn.Id +
                            " is present twice.");
                        errors++;
                    }
                }

                foreach (var animation in root.Animations.Animation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (animation.Name == "fall") fall = true;
                    if (animation.Name == "drag") drag = true;
                    if (animation.Name == "kill") kill = true;
                    if (animation.Name == "sync") sync = true;

                    if (animation.Border != null &&
                        animation.Border.Next != null)
                        totalLinks += animation.Border.Next.Length;
                    if (animation.Gravity != null &&
                        animation.Gravity.Next != null)
                        totalLinks += animation.Gravity.Next.Length;
                    if (animation.Sequence != null &&
                        animation.Sequence.Next != null)
                        totalLinks += animation.Sequence.Next.Length;

                    if (!animationIds.Add(animation.Id))
                    {
                        output.AppendLine(
                            "ANIMATION ERROR: The animation ID " +
                            animation.Id + " is present twice.");
                        errors++;
                    }
                    else
                    {
                        animationById.Add(animation.Id, animation);
                    }
                }
                if (!fall)
                {
                    output.AppendLine(
                        "ANIMATION WARNING: Please add an animation with " +
                        "the name 'fall' for a falling pet.");
                    warnings++;
                }
                if (!drag)
                {
                    output.AppendLine(
                        "ANIMATION WARNING: Please add an animation with " +
                        "the name 'drag' for a pet that is taken with a mouse.");
                    warnings++;
                }
                if (!kill)
                {
                    output.AppendLine(
                        "ANIMATION WARNING: Please add an animation with " +
                        "the name 'kill' for a pet that will be removed.");
                    warnings++;
                }
                if (!sync)
                {
                    output.AppendLine(
                        "ANIMATION WARNING: Please add an animation with " +
                        "the name 'sync' for syncing the pets.");
                    warnings++;
                }

                if (errors == 0)
                {
                    int checkedSpawns = 0;
                    int checkedChildren = 0;
                    int checkedAnimations = 0;
                    int checkedLinks = 0;
                    string errorMessage = "";

                    try
                    {
                        errorMessage = "Loading Xml animations";
                        xml.AnimationXML = root;
                        cancellationToken.ThrowIfCancellationRequested();
                        xml.LoadAnimations(loadedAnimations);
                        cancellationToken.ThrowIfCancellationRequested();

                        var reachable = new HashSet<int>();
                        var pending = new Queue<int>();
                        var childTransitions =
                            new Dictionary<int, List<int>>();
                        int[] initialAnimationIds =
                        {
                            loadedAnimations.AnimationDrag,
                            loadedAnimations.AnimationFall,
                            loadedAnimations.AnimationKill,
                            loadedAnimations.AnimationSync
                        };
                        foreach (int initialAnimationId in initialAnimationIds)
                        {
                            if (animationIds.Contains(initialAnimationId) &&
                                reachable.Add(initialAnimationId))
                                pending.Enqueue(initialAnimationId);
                        }

                        foreach (var spawn in root.Spawns.Spawn)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            errorMessage = "spawn " + spawn.Id;
                            xml.GetXMLCompute(spawn.X, "spawn x");
                            xml.GetXMLCompute(spawn.Y, "spawn y");
                            if (spawn.Next == null ||
                                !animationIds.Contains(spawn.Next.Value))
                            {
                                output.AppendLine(
                                    "SPAWN ERROR: On spawn " + spawn.Id +
                                    ": target animation Id is not available.");
                                errors++;
                            }
                            if (spawn.Probability > 0 && spawn.Next != null &&
                                reachable.Add(spawn.Next.Value))
                                pending.Enqueue(spawn.Next.Value);
                            checkedSpawns++;
                        }

                        if (root.Childs != null && root.Childs.Child != null)
                        {
                            foreach (var child in root.Childs.Child)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                errorMessage = "child " + child.Id;
                                xml.GetXMLCompute(child.X, "spawn x");
                                xml.GetXMLCompute(child.Y, "spawn y");
                                bool parentExists =
                                    animationIds.Contains(child.Id);
                                if (!parentExists)
                                {
                                    output.AppendLine(
                                        "CHILD ERROR: On child " + child.Id +
                                        ": parent animation Id is not available.");
                                    errors++;
                                }
                                bool targetExists =
                                    animationIds.Contains(child.Next);
                                if (!targetExists)
                                {
                                    output.AppendLine(
                                        "CHILD ERROR: On child " + child.Id +
                                        ": next animation Id is not available.");
                                    errors++;
                                }
                                if (parentExists && targetExists)
                                {
                                    List<int> targets;
                                    if (!childTransitions.TryGetValue(
                                            child.Id,
                                            out targets))
                                    {
                                        targets = new List<int>();
                                        childTransitions.Add(
                                            child.Id,
                                            targets);
                                    }
                                    targets.Add(child.Next);
                                }
                                checkedChildren++;
                            }
                        }

                        foreach (var animation in root.Animations.Animation)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            errorMessage = "animation " + animation.Id +
                                " - " + animation.Name;
                            xml.GetXMLCompute(animation.Start.X, "start x");
                            xml.GetXMLCompute(animation.Start.Y, "start y");
                            xml.GetXMLCompute(animation.End.X, "end x");
                            xml.GetXMLCompute(animation.End.Y, "end y");
                            if (!animationIds.Contains(animation.Id))
                            {
                                output.AppendLine(
                                    "ANIMATION ERROR: On animation " +
                                    animation.Id +
                                    ": animation Id is not available.");
                                errors++;
                            }
                            if (animation.Border != null &&
                                animation.Border.Next != null)
                            {
                                foreach (var next in animation.Border.Next)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    checkedLinks++;
                                    if (!animationIds.Contains(next.Value))
                                    {
                                        output.AppendLine(
                                            "ANIMATION ERROR: On animation " +
                                            animation.Id + ": border Next Id " +
                                            next.Value + " is not available.");
                                        errors++;
                                    }
                                }
                            }
                            if (animation.Gravity != null &&
                                animation.Gravity.Next != null)
                            {
                                foreach (var next in animation.Gravity.Next)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    checkedLinks++;
                                    if (!animationIds.Contains(next.Value))
                                    {
                                        output.AppendLine(
                                            "ANIMATION ERROR: On animation " +
                                            animation.Id + ": gravity Next Id " +
                                            next.Value + " is not available.");
                                        errors++;
                                    }
                                }
                            }
                            if (animation.Sequence != null)
                            {
                                if (animation.Sequence.Next == null)
                                {
                                    if (animation.Name != "kill")
                                    {
                                        output.AppendLine(
                                            "ANIMATION WARNING: On animation " +
                                            animation.Id + ": this sequence " +
                                            "does not have a next node, pet will " +
                                            "respawn after this sequence.");
                                        warnings++;
                                    }
                                }
                                else
                                {
                                    foreach (var next in animation.Sequence.Next)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        checkedLinks++;
                                        if (!animationIds.Contains(next.Value))
                                        {
                                            output.AppendLine(
                                                "ANIMATION ERROR: On animation " +
                                                animation.Id +
                                                ": sequence Next Id " +
                                                next.Value +
                                                " is not available.");
                                            errors++;
                                        }
                                    }
                                }
                            }
                            checkedAnimations++;
                        }

                        while (pending.Count > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int animationId = pending.Dequeue();
                            errorMessage = "check links for id " + animationId;
                            XmlData.AnimationNode animation;
                            if (!animationById.TryGetValue(
                                    animationId,
                                    out animation))
                                continue;
                            EnqueueTransitions(
                                animation.Gravity == null
                                    ? null
                                    : animation.Gravity.Next,
                                reachable,
                                pending,
                                cancellationToken);
                            EnqueueTransitions(
                                animation.Border == null
                                    ? null
                                    : animation.Border.Next,
                                reachable,
                                pending,
                                cancellationToken);
                            EnqueueTransitions(
                                animation.Sequence == null
                                    ? null
                                    : animation.Sequence.Next,
                                reachable,
                                pending,
                                cancellationToken);
                            List<int> childTargets;
                            if (childTransitions.TryGetValue(
                                    animationId,
                                    out childTargets))
                            {
                                foreach (int childTarget in childTargets)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (reachable.Add(childTarget))
                                        pending.Enqueue(childTarget);
                                }
                            }
                        }

                        if (reachable.Count != animationIds.Count)
                        {
                            foreach (var animation in root.Animations.Animation)
                            {
                                if (!reachable.Contains(animation.Id))
                                {
                                    output.AppendLine(
                                        "ANIMATION WARNING: On animation " +
                                        animation.Id +
                                        ": This ID is never played.");
                                    warnings++;
                                }
                            }
                        }

                        result.CheckedSpawns = checkedSpawns;
                        result.CheckedChildren = checkedChildren;
                        result.CheckedAnimations = checkedAnimations;
                        result.CheckedLinks = checkedLinks;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        output.AppendLine("ERROR: " + errorMessage);
                        output.AppendLine(ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors++;
                output.AppendLine("ERROR: animation analysis");
                output.AppendLine(ex.Message);
            }

            result.Errors = errors;
            result.Warnings = warnings;
            result.TotalLinks = totalLinks;
            result.Succeeded = errors == 0;
        }

        private static void EnqueueTransitions(
            XmlData.NextNode[] transitions,
            HashSet<int> reachable,
            Queue<int> pending,
            CancellationToken cancellationToken)
        {
            if (transitions == null) return;
            foreach (var transition in transitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (transition == null || transition.Probability <= 0)
                    continue;
                if (reachable.Add(transition.Value))
                    pending.Enqueue(transition.Value);
            }
        }

        private void UpdateAnimationsState(
            int checkSpawn,
            int totSpawn,
            int checkAnimation,
            int totAnimation,
            int checkChild,
            int totChild,
            int checkLinks,
            int totLinks)
        {
            label4.Text =
                "Spawns: " + FormatProgress(checkSpawn, totSpawn) + "\r\n";
            if (totChild > 0)
                label4.Text +=
                    "Children: " + FormatProgress(checkChild, totChild) + "\r\n";
            label4.Text +=
                "Animations: " +
                FormatProgress(checkAnimation, totAnimation) + "\r\n";
            label4.Text +=
                "Animation links: " +
                FormatProgress(checkLinks, totLinks) + "\r\n";
        }

        private static string FormatProgress(int checkedCount, int totalCount)
        {
            int percentage = totalCount == 0
                ? 100
                : checkedCount * 100 / totalCount;
            return checkedCount + " / " + totalCount + " (" +
                percentage + "%)";
        }

        internal async Task<PetTesterValidationSnapshot>
            RunValidationSelfTestAsync(string xml)
        {
            await RunValidationOperationForTestAsync(xml);
            return CaptureValidationSnapshotForTest();
        }

        internal async Task<long> RunValidationOperationForTestAsync(string xml)
        {
            if (xml == null) throw new ArgumentNullException("xml");
            if (!IsHandleCreated)
            {
                IntPtr createdHandle = Handle;
                if (createdHandle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "PetTester self-test could not create a form handle.");
            }

            ValidationLoadSession session = validationLoads.Begin();
            try
            {
                ResetValidationState();
                XmlContent = xml;
                tableLayoutPanel1.Visible = true;
                checkBox1.CheckState = CheckState.Checked;
                checkBox1.Tag = 2;
                label2.Text = "SUCCESS";
                await AnalyseXMLFile(session);
                return session.Generation;
            }
            finally
            {
                validationLoads.Complete(session);
            }
        }

        internal PetTesterValidationSnapshot CaptureValidationSnapshotForTest()
        {
            return new PetTesterValidationSnapshot
            {
                XmlState = checkBox1.CheckState,
                XmlTag = Convert.ToInt32(checkBox1.Tag),
                ResourceState = checkBox2.CheckState,
                ResourceTag = Convert.ToInt32(checkBox2.Tag),
                AnimationState = checkBox3.CheckState,
                AnimationTag = Convert.ToInt32(checkBox3.Tag),
                Output = textBox1.Text
            };
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            int tag = (int)timer1.Tag;
            tag = (tag + 1) % XmlClass.SpriteCount;
            timer1.Tag = tag;

            pictureBox1.Image = XmlClass.GetSpriteFrame(tag, false);
        }
    }
}
