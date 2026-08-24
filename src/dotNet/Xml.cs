using System;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DesktopPet
{
    
        /// <summary>
        /// Xml class contains all functions to read the XML file and functions to parse it.
        /// </summary>
    public sealed class Xml : IDisposable
    {
            /// <summary>
            /// XML Document, containing the animations xml.
            /// </summary>
        public XmlData.RootNode AnimationXML;

            /// <summary>
            /// XML String, used for the current running animation.
            /// </summary>
        public string AnimationXMLString;

            /// <summary>
            /// List of sprite images for animations.
            /// </summary>
        private SpriteFrameStore spriteFrames;

            /// <summary>
            /// Width of sprite in pixels.
            /// </summary>
        public int spriteWidth;

            /// <summary>
            /// Height of sprite in pixels.
            /// </summary>
        public int spriteHeight;

            /// <summary>
            /// A memory stream containing the animation icon. This is visible in the taskbar and tray icon.
            /// </summary>
        public MemoryStream bitmapIcon;
            
            /// <summary>
            /// X position of the parent image. Used to set the child position.
            /// </summary>
        private int parentX;
            /// <summary>
            /// Y position of the parent image. Used to set the child position.
            /// </summary>
        private int parentY;
            /// <summary>
            /// If the parent is flipped. If so, the image will be flipped and screen-mirrored.
            /// </summary>
        private bool parentFlipped;
            /// <summary>
            /// Random spawn, this value changes each time the XML is reloaded. Used in the animation xml.
            /// </summary>
        int iRandomSpawn = 10;
            /// <summary>
            /// Scale the pet on HD monitors.
            /// </summary>
        int iScale = 1;
            /// <summary>
            /// True when the pet's &lt;transparency&gt; is the reserved value "Alpha": the sprite sheet
            /// carries a real alpha channel and the host renders it per-pixel (UpdateLayeredWindow)
            /// instead of colour-keying magenta. Any other value keeps the colour-key path.
            /// </summary>
        private bool usesAlpha;
        private readonly Random random = new Random();
        private bool disposed;
        private const int MaximumGeneratedFrames = SpriteFrameStore.MaximumFrames;
        private const long MaximumGeneratedPixels = SpriteFrameStore.MaximumOriginalPixels;
        private const long MaximumGeneratedBytes = 64L * 1024L * 1024L;
        private const int GeneratedBytesPerPixel = 4;

        /// <summary>Effective 1x/2x/4x rendering and movement factor.</summary>
        public int ScaleFactor { get { return iScale; } }

        /// <summary>True when this pet opts into per-pixel alpha rendering (&lt;transparency&gt;Alpha).</summary>
        public bool UsesAlpha { get { return usesAlpha; } }

        /// <summary>The reserved &lt;transparency&gt; value that selects the per-pixel alpha render path.</summary>
        public const string AlphaTransparencyKeyword = "Alpha";

            /// <summary>
            /// Constructor. Initialize member variables.
            /// </summary>
        public Xml(int scaleFactor = 1)
        {
            spriteFrames = new SpriteFrameStore(new List<Bitmap>());
            iScale = ScalePolicy.ClampFactor(scaleFactor);

            parentX = -1;                   // -1 means it is not a child.
            parentY = -1;
            parentFlipped = false;

            iRandomSpawn = random.Next(10, 90);

		}

        /// <summary>
        /// Diagnostic constructor used by the built-in resource-ownership regression. Ownership of
        /// <paramref name="frames"/> transfers to this <see cref="Xml"/> instance on success.
        /// </summary>
        internal Xml(IList<Bitmap> frames, int frameWidth, int frameHeight)
            : this(1)
        {
            ReplaceSpriteFrames(frames, frameWidth, frameHeight);
        }

        /// <summary>
        /// Replaces the owned atlas used by linked diagnostic tooling. Ownership of
        /// <paramref name="frames"/> transfers to this instance only when the method succeeds.
        /// </summary>
        internal void ReplaceSpriteFrames(
            IList<Bitmap> frames,
            int frameWidth,
            int frameHeight)
        {
            if (disposed) throw new ObjectDisposedException("Xml");
            if (frames == null) throw new ArgumentNullException("frames");
            if (frameWidth < 1) throw new ArgumentOutOfRangeException("frameWidth");
            if (frameHeight < 1) throw new ArgumentOutOfRangeException("frameHeight");

            SpriteFrameStore replacement = new SpriteFrameStore(frames);
            SpriteFrameStore previous = spriteFrames;
            spriteFrames = replacement;
            spriteWidth = frameWidth;
            spriteHeight = frameHeight;
            if (previous != null) previous.Dispose();
        }

        internal int SpriteCount
        {
            get { return spriteFrames == null ? 0 : spriteFrames.Count; }
        }

        internal Bitmap GetSpriteFrame(int index, bool flipped)
        {
            if (disposed) throw new ObjectDisposedException("Xml");
            if (spriteFrames == null)
                throw new InvalidOperationException("Pet sprite frames are unavailable.");
            return spriteFrames.GetFrame(index, flipped);
        }

        internal int MaterializedFlippedFrameCount
        {
            get
            {
                return spriteFrames == null
                    ? 0
                    : spriteFrames.MaterializedFlippedCount;
            }
        }

            /// <summary>
            /// Dispose class and created objects.
            /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DisposeAssets();
            AnimationXML = null;
            AnimationXMLString = null;
        }

        /// <summary>
        /// Validates and stages a complete pet definition without changing persisted settings or
        /// any currently running pet. The instance is usable only after this method succeeds.
        /// </summary>
        public bool TryReadXml(string xmlText, out string error)
        {
            error = null;
            if (disposed)
            {
                error = "The XML loader has already been disposed.";
                return false;
            }

            XmlData.RootNode parsed;
            if (!PetXmlValidator.TryParse(xmlText, out parsed, out error))
                return false;

            IList<Bitmap> stagedSprites = null;
            SpriteFrameStore stagedFrameStore = null;
            MemoryStream stagedIcon = null;
            try
            {
                int stagedWidth;
                int stagedHeight;
                int stagedScale;
                ReadImages(
                    parsed,
                    out stagedSprites,
                    out stagedIcon,
                    out stagedWidth,
                    out stagedHeight,
                    out stagedScale);

                parsed.Header.Petname =
                    UnicodeTextProgress.TruncateAtCodePointBoundary(
                        parsed.Header.Petname,
                        16);

                stagedFrameStore = new SpriteFrameStore(stagedSprites);
                stagedSprites = null;
                DisposeAssets();
                AnimationXML = parsed;
                AnimationXMLString = xmlText;
                spriteFrames = stagedFrameStore;
                stagedFrameStore = null;
                bitmapIcon = stagedIcon;
                stagedIcon = null;
                spriteWidth = stagedWidth;
                spriteHeight = stagedHeight;
                iScale = stagedScale;

                // The source string remains the canonical persisted definition. Do not retain a
                // second multi-megabyte base64 copy in the deserialized runtime graph.
                AnimationXML.Image.Png = string.Empty;
                AnimationXML.Header.Icon = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                if (stagedFrameStore != null) stagedFrameStore.Dispose();
                if (stagedSprites != null)
                    foreach (Bitmap sprite in stagedSprites)
                        if (sprite != null) sprite.Dispose();
                if (stagedIcon != null) stagedIcon.Dispose();
                error = ex.Message;
                return false;
            }
        }

            /// <summary>
            /// Load the animations (read them from XML file)
            /// </summary>
            /// <param name="animations">Animation class where the animations should be saved</param>
        public void LoadAnimations(Animations animations)
        {
            if(AnimationXML.Animations == null)
            {
                StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error, "No animations for this pet");
                return;
            }
                // for each animation
            foreach (XmlData.AnimationNode node in AnimationXML.Animations.Animation)
            {
                TAnimation ani = animations.AddAnimation(node.Id, node.Id.ToString());
                ani.Border = node.Border != null;
                ani.Gravity = node.Gravity != null;

                ani.Name = node.Name;
                switch (ani.Name)
                {
                    case "fall": animations.AnimationFall = node.Id; break;
                    case "drag": animations.AnimationDrag = node.Id; break;
                    case "kill": animations.AnimationKill = node.Id; break;
                    case "sync": animations.AnimationSync = node.Id; break;
                }

                ani.Start.X = GetXMLCompute(node.Start.X, "animation " + node.Id + ": node.start.X");
                ani.Start.Y = GetXMLCompute(node.Start.Y, "animation " + node.Id + ": node.start.Y");
                ani.Start.Interval = GetXMLCompute(node.Start.Interval, "animation " + node.Id + ": node.start.Interval");
                ani.Start.OffsetY = node.Start.OffsetY;
                ani.Start.UnscaledOffsetY = node.Start.OffsetY;
                ani.Start.Opacity = node.Start.Opacity;

                ani.End.X = GetXMLCompute(node.End.X, "animation " + node.Id + ": node.end.X");
                ani.End.Y = GetXMLCompute(node.End.Y, "animation " + node.Id + ": node.end.Y");
                ani.End.Interval = GetXMLCompute(node.End.Interval, "animation " + node.Id + ": node.end.Interval");
                ani.End.OffsetY = node.End.OffsetY;
                ani.End.UnscaledOffsetY = node.End.OffsetY;
                ani.End.Opacity = node.End.Opacity;

                ani.Sequence.RepeatFrom = node.Sequence.RepeatFromFrame;
                ani.Sequence.Action = node.Sequence.Action;
                ani.Sequence.Repeat = GetXMLCompute(node.Sequence.RepeatCount, "animation " + node.Id + ": node.sequence.Repeat");
                ani.Sequence.Repeat.Value =
                    AnimationRuntimeLimits.ClampRepeat(ani.Sequence.Repeat.Value);
                ani.Sequence.Frames.AddRange(node.Sequence.Frame);
                ani.Sequence.TotalSteps = AnimationRuntimeLimits.CalculateTotalSteps(
                    ani.Sequence.Frames.Count,
                    ani.Sequence.RepeatFrom,
                    ani.Sequence.Repeat.Value);
                if (node.Sequence.Next != null)
                {
                    foreach (XmlData.NextNode nextNode in node.Sequence.Next)
                    {
                        TNextAnimation.TOnly where;
                        switch (nextNode.OnlyFlag)
                        {
                            case "taskbar": where = TNextAnimation.TOnly.TASKBAR; break;
                            case "window": where = TNextAnimation.TOnly.WINDOW; break;
                            case "horizontal": where = TNextAnimation.TOnly.HORIZONTAL; break;
                            case "horizontal+": where = TNextAnimation.TOnly.HORIZONTAL_; break;
                            case "vertical": where = TNextAnimation.TOnly.VERTICAL; break;
                            default: where = TNextAnimation.TOnly.NONE; break;
                        }

                        ani.EndAnimation.Add(
                            new TNextAnimation(
                                nextNode.Value,
                                nextNode.Probability,
                                where
                            )
                        );
                    }
                }

                if (ani.Border)
                {
                    foreach (XmlData.NextNode nextNode in node.Border.Next)
                    {
                        TNextAnimation.TOnly where;
                        switch (nextNode.OnlyFlag)
                        {
                            case "taskbar": where = TNextAnimation.TOnly.TASKBAR; break;
                            case "window": where = TNextAnimation.TOnly.WINDOW; break;
                            case "horizontal": where = TNextAnimation.TOnly.HORIZONTAL; break;
                            case "horizontal+": where = TNextAnimation.TOnly.HORIZONTAL_; break;
                            case "vertical": where = TNextAnimation.TOnly.VERTICAL; break;
                            default: where = TNextAnimation.TOnly.NONE; break;
                        }
                        ani.Border = true;
                        ani.EndBorder.Add(
                            new TNextAnimation(
                                nextNode.Value,
                                nextNode.Probability,
                                where
                            )
                        );
                    }
                }

                if (ani.Gravity)
                {
                    foreach (XmlData.NextNode nextNode in node.Gravity.Next)
                    {
                        TNextAnimation.TOnly where;
                        switch (nextNode.OnlyFlag)
                        {
                            case "taskbar": where = TNextAnimation.TOnly.TASKBAR; break;
                            case "window": where = TNextAnimation.TOnly.WINDOW; break;
                            case "horizontal": where = TNextAnimation.TOnly.HORIZONTAL; break;
                            case "horizontal+": where = TNextAnimation.TOnly.HORIZONTAL_; break;
                            case "vertical": where = TNextAnimation.TOnly.VERTICAL; break;
                            default: where = TNextAnimation.TOnly.NONE; break;
                        }
                        ani.Gravity = true;
                        ani.EndGravity.Add(
                            new TNextAnimation(
                                nextNode.Value,
                                nextNode.Probability,
                                where
                            )
                        );
                    }
                }
                
                animations.SaveAnimation(ani, node.Id);
            }

            // for each spawn
            if (AnimationXML.Spawns.Spawn != null)
            {
                foreach (XmlData.SpawnNode node in AnimationXML.Spawns.Spawn)
                {
                    TSpawn ani = animations.AddSpawn(
                        node.Id,
                        node.Probability);

                    ani.Start.X = GetXMLCompute(node.X, "spawn " + node.Id + ": node.X");
                    ani.Start.Y = GetXMLCompute(node.Y, "spawn " + node.Id + ": node.X");
                    ani.Next = node.Next.Value;

                    animations.SaveSpawn(ani, node.Id);
                }
            }

            // for each child
            if (AnimationXML.Childs.Child != null)
            {
                foreach (XmlData.ChildNode node in AnimationXML.Childs.Child)
                {
                    TChild aniChild = animations.AddChild(node.Id);
                    aniChild.AnimationID = node.Id;

                    aniChild.Position.X = GetXMLCompute(node.X, "child " + node.Id + ": node.X");
                    aniChild.Position.Y = GetXMLCompute(node.Y, "child " + node.Id + ": node.Y");
                    aniChild.Next = node.Next;

                    animations.SaveChild(aniChild, node.Id);
                }
            }

            // for each sound
            if (AnimationXML.Sounds != null && AnimationXML.Sounds.Sound != null)
            {
                foreach (XmlData.SoundNode node in AnimationXML.Sounds.Sound)
                {
                    animations.AddSound(node.Id, node.Probability, node.Loop, node.Base64);
                }
            }
        }

            /// <summary>
            /// Get the value from XML file. If special keys are used (like screenW) it will be converted.
            /// </summary>
            /// <param name="text">XML text value.</param>
            /// <param name="debugInfo">Info text to show if this function fails.</param>
            /// <returns>A structure with the values.</returns>
        public TValue GetXMLCompute(string text, string debugInfo)
        {
            TValue v = new TValue();

            v.Evaluator = this;
            v.Compute = text;
            v.IsDynamic = (v.Compute.IndexOf("random") >= 0 || v.Compute.IndexOf("randS") >= 0 || v.Compute.IndexOf("imageX") >= 0 || v.Compute.IndexOf("imageY") >= 0);
            v.IsScreen = (v.Compute.IndexOf("screen") >= 0 || v.Compute.IndexOf("area") >= 0);
            v.Value = ParseValue(v.Compute, debugInfo);

            return v;
        }

        /// <summary>
        /// Parse a value, converting keys like screenW, imageH, random,... to integers.
        /// </summary>
        /// <remarks>
        /// See <a href="https://msdn.microsoft.com/en-us/library/9za5w1xw(v=vs.100).aspx">https://msdn.microsoft.com/en-us/library/9za5w1xw(v=vs.100).aspx</a>
        /// for more information of what you can write as sText (expression to compute).
        /// </remarks> 
        /// <param name="parsingText">The text to parse and convert.</param>
        /// <param name="debugInfo">Debug text to show if this function fails.</param>
        /// <param name="screenIndex">If set, the xml will be parsed with the screen dimension.</param>
        /// <returns>The integer value from the parsed text expression.</returns>
        public int ParseValue(string parsingText, string debugInfo, int screenIndex = -1)
        {
            var screen = Screen.PrimaryScreen;
            if (screenIndex >= 0 && screenIndex < Screen.AllScreens.Length)
                screen = Screen.AllScreens[screenIndex];
            ScreenMetrics metrics = DesktopGeometry.Metrics(screen.Bounds, screen.WorkingArea);
            try
            {
                return SafeExpression.Evaluate(parsingText, delegate(string name)
                {
                    switch (name)
                    {
                        case "screenW": return metrics.ScreenWidth;
                        case "screenH": return metrics.ScreenHeight;
                        case "areaW": return metrics.WorkAreaWidth;
                        case "areaH": return metrics.WorkAreaHeight;
                        case "imageW": return parentFlipped ? -spriteWidth : spriteWidth;
                        case "imageH": return spriteHeight;
                        case "imageX": return parentX;
                        case "imageY": return parentY;
                        case "random": return random.Next(0, 100);
                        case "randS": return iRandomSpawn;
                        case "scale": return iScale;
                        default: throw new FormatException("Unknown expression variable: " + name);
                    }
                });
            }
            catch (Exception ex)
            {
                StartUp.AddDebugInfo(
                    StartUp.DEBUG_TYPE.warning,
                    "Animation expression rejected (" + debugInfo + "): " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Temporarily supplies the parent context used by child expressions. The previous context
        /// is restored even when parsing fails, so one child cannot affect another pet.
        /// </summary>
        public IDisposable PushParentContext(Point parentPosition, bool flipped)
        {
            int oldX = parentX;
            int oldY = parentY;
            bool oldFlipped = parentFlipped;
            parentX = parentPosition.X;
            parentY = parentPosition.Y;
            parentFlipped = flipped;
            return new ParentContext(this, oldX, oldY, oldFlipped);
        }

        private void ReadImages(
            XmlData.RootNode root,
            out IList<Bitmap> stagedSprites,
            out MemoryStream stagedIcon,
            out int stagedWidth,
            out int stagedHeight,
            out int stagedScale)
        {
            byte[] imageBytes = DecodeBase64(root.Image.Png);
            byte[] iconBytes = DecodeBase64(root.Header.Icon);
            usesAlpha = string.Equals(
                (root.Image.Transparency ?? string.Empty).Trim(),
                AlphaTransparencyKeyword,
                StringComparison.OrdinalIgnoreCase);
            stagedIcon = new MemoryStream(iconBytes, false);
            stagedSprites = null;
            stagedWidth = 0;
            stagedHeight = 0;
            stagedScale = iScale;

            try
            {
                using (var imageStream = new MemoryStream(imageBytes, false))
                using (var decoded = new Bitmap(imageStream))
                {
                    int sourceWidth = decoded.Width / root.Image.TilesX;
                    int sourceHeight = decoded.Height / root.Image.TilesY;
                    stagedScale = ScalePolicy.FitFactorForFrame(
                        stagedScale,
                        sourceWidth,
                        sourceHeight,
                        256);
                    if ((long)sourceWidth * stagedScale > 256 ||
                        (long)sourceHeight * stagedScale > 256)
                        throw new InvalidDataException("A sprite frame exceeds the 256-pixel runtime limit.");

                    stagedWidth = checked(sourceWidth * stagedScale);
                    stagedHeight = checked(sourceHeight * stagedScale);
                    ValidateSpriteBudget(
                        root.Image.TilesX,
                        root.Image.TilesY,
                        stagedWidth,
                        stagedHeight);
                    stagedSprites = BuildSprites(
                        decoded,
                        root.Image.TilesX,
                        root.Image.TilesY,
                        sourceWidth,
                        sourceHeight,
                        stagedWidth,
                        stagedHeight);
                }
            }
            catch
            {
                stagedIcon.Dispose();
                stagedIcon = null;
                throw;
            }
        }

        private static void ValidateSpriteBudget(
            int tilesX,
            int tilesY,
            int destinationWidth,
            int destinationHeight)
        {
            long frameCount = checked((long)tilesX * tilesY);
            if (frameCount > MaximumGeneratedFrames)
                throw new InvalidDataException(
                    "Sprite sheet expands to more than " +
                    MaximumGeneratedFrames +
                    " runtime frames.");

            long generatedPixels = checked(
                checked(frameCount * destinationWidth) * destinationHeight);
            if (generatedPixels > MaximumGeneratedPixels)
                throw new InvalidDataException(
                    "Expanded sprite frames exceed the runtime pixel budget.");

            long generatedBytes = checked(generatedPixels * GeneratedBytesPerPixel);
            if (generatedBytes > MaximumGeneratedBytes)
                throw new InvalidDataException(
                    "Expanded sprite frames exceed the 64 MiB runtime memory budget.");
        }

        private static IList<Bitmap> BuildSprites(
            Bitmap spriteSheet,
            int tilesX,
            int tilesY,
            int sourceWidth,
            int sourceHeight,
            int destinationWidth,
            int destinationHeight)
        {
            var result = new List<Bitmap>(checked(tilesX * tilesY));
            try
            {
                for (int tileY = 0; tileY < tilesY; tileY++)
                {
                    for (int tileX = 0; tileX < tilesX; tileX++)
                    {
                        Bitmap frame = null;
                        try
                        {
                            frame = new Bitmap(
                                destinationWidth,
                                destinationHeight,
                                PixelFormat.Format32bppPArgb);
                            using (Graphics graphics = Graphics.FromImage(frame))
                            {
                                graphics.CompositingMode = CompositingMode.SourceCopy;
                                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                                graphics.SmoothingMode = SmoothingMode.None;
                                graphics.DrawImage(
                                    spriteSheet,
                                    new Rectangle(0, 0, destinationWidth, destinationHeight),
                                    new Rectangle(
                                        tileX * sourceWidth,
                                        tileY * sourceHeight,
                                        sourceWidth,
                                        sourceHeight),
                                    GraphicsUnit.Pixel);
                            }
                            result.Add(frame);
                            frame = null;
                        }
                        finally
                        {
                            if (frame != null) frame.Dispose();
                        }
                    }
                }
                return result;
            }
            catch
            {
                foreach (Bitmap frame in result) frame.Dispose();
                throw;
            }
        }

        private static byte[] DecodeBase64(string value)
        {
            int marker = value == null
                ? -1
                : value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            string encoded = marker >= 0 ? value.Substring(marker + 8) : value;
            return Convert.FromBase64String(encoded ?? "");
        }

        private void DisposeAssets()
        {
            if (bitmapIcon != null)
            {
                bitmapIcon.Dispose();
                bitmapIcon = null;
            }
            if (spriteFrames != null)
            {
                spriteFrames.Dispose();
                spriteFrames = null;
            }
        }

        private sealed class ParentContext : IDisposable
        {
            private Xml owner;
            private readonly int x;
            private readonly int y;
            private readonly bool flipped;

            public ParentContext(Xml owner, int x, int y, bool flipped)
            {
                this.owner = owner;
                this.x = x;
                this.y = y;
                this.flipped = flipped;
            }

            public void Dispose()
            {
                Xml current = owner;
                if (current == null) return;
                owner = null;
                current.parentX = x;
                current.parentY = y;
                current.parentFlipped = flipped;
            }
        }
    }

    /// <summary>
    /// Owns the immutable sprite atlas for one parsed pet. Original frames are shared by every root
    /// and child form. A flipped frame is cloned from its original only when first requested, then
    /// shared as well; therefore ownership is bounded to at most two bitmaps per source frame.
    /// </summary>
    internal sealed class SpriteFrameStore : IDisposable
    {
        internal const int MaximumFrames = 1024;
        internal const long MaximumOriginalPixels = 16L * 1024L * 1024L;

        private readonly object sync = new object();
        private Bitmap[] originals;
        private Bitmap[] flipped;
        private int materializedFlippedCount;
        private bool disposed;

        public SpriteFrameStore(IList<Bitmap> frames)
        {
            if (frames == null) throw new ArgumentNullException("frames");
            if (frames.Count > MaximumFrames)
                throw new InvalidDataException(
                    "Sprite sheet expands to more than " +
                    MaximumFrames +
                    " runtime frames.");

            var staged = new Bitmap[frames.Count];
            long pixels = 0;
            for (int index = 0; index < frames.Count; index++)
            {
                Bitmap frame = frames[index];
                if (frame == null)
                    throw new InvalidDataException("A runtime sprite frame is missing.");
                pixels = checked(pixels + (long)frame.Width * frame.Height);
                if (pixels > MaximumOriginalPixels)
                    throw new InvalidDataException(
                        "Expanded sprite frames exceed the runtime pixel budget.");
                staged[index] = frame;
            }
            originals = staged;
        }

        public int Count
        {
            get
            {
                lock (sync)
                    return disposed || originals == null ? 0 : originals.Length;
            }
        }

        public int MaterializedFlippedCount
        {
            get
            {
                lock (sync)
                    return disposed ? 0 : materializedFlippedCount;
            }
        }

        public Bitmap GetFrame(int index, bool isFlipped)
        {
            lock (sync)
            {
                if (disposed || originals == null)
                    throw new ObjectDisposedException("SpriteFrameStore");
                if (index < 0 || index >= originals.Length)
                    throw new ArgumentOutOfRangeException("index");
                if (!isFlipped) return originals[index];

                if (flipped == null)
                    flipped = new Bitmap[originals.Length];
                Bitmap cached = flipped[index];
                if (cached != null) return cached;

                Bitmap created = null;
                try
                {
                    created = (Bitmap)originals[index].Clone();
                    created.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    flipped[index] = created;
                    materializedFlippedCount++;
                    return created;
                }
                catch
                {
                    if (created != null) created.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            Bitmap[] ownedOriginals;
            Bitmap[] ownedFlipped;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                ownedOriginals = originals;
                ownedFlipped = flipped;
                originals = null;
                flipped = null;
                materializedFlippedCount = 0;
            }

            if (ownedFlipped != null)
                foreach (Bitmap frame in ownedFlipped)
                    if (frame != null) frame.Dispose();
            if (ownedOriginals != null)
                foreach (Bitmap frame in ownedOriginals)
                    if (frame != null) frame.Dispose();
        }
    }
}
