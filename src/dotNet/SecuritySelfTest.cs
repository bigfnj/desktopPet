using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    /// <summary>Dependency-free security regression checks run by CI and release packaging.</summary>
    internal static class SecuritySelfTest
    {
        public static bool Run(TextWriter output)
        {
            output = output ?? TextWriter.Null;
            int failures = 0;

            CheckAiSettingsPersistence(ref failures, output);
            CheckAiCredentialScoping(ref failures, output);
            CheckCrossSessionLock(ref failures, output);
            CheckEndpoint("http://localhost:11434", true, ref failures, output);
            CheckEndpoint("http://127.0.0.1:8080/v1", true, ref failures, output);
            CheckEndpoint("http://[::1]:8080/v1", true, ref failures, output);
            CheckEndpoint("https://api.openai.com/v1", true, ref failures, output);
            CheckEndpoint("http://example.com/v1", false, ref failures, output);
            CheckEndpoint("http://192.168.1.20/v1", false, ref failures, output);
            CheckEndpoint("ftp://localhost/model", false, ref failures, output);
            CheckEndpoint("https://user:password@example.com/v1", false, ref failures, output);
            CheckEndpoint("https://example.com/v1?token=secret", false, ref failures, output);

            Check(SecureDownload.IsSafeId("dadjokes"), "safe catalog id", ref failures, output);
            Check(!SecureDownload.IsSafeId("../escape"), "path traversal id rejected", ref failures, output);
            Check(!SecureDownload.IsSafeId("CON"), "Windows device id rejected", ref failures, output);

            Uri pinned;
            string uriError;
            Check(SecureDownload.TryValidatePinnedRawGitHubUrl(
                    "https://raw.githubusercontent.com/bigfnj/desktopPet/" +
                    "b52c11d184532364019ffc1756f3f0868ec99997/packs/dadjokes.txt",
                    "bigfnj", "desktopPet", out pinned, out uriError),
                "commit-pinned catalog URL", ref failures, output);
            Check(!SecureDownload.TryValidatePinnedRawGitHubUrl(
                    "https://raw.githubusercontent.com/bigfnj/desktopPet/master/packs/dadjokes.txt",
                    "bigfnj", "desktopPet", out pinned, out uriError),
                "mutable catalog URL rejected", ref failures, output);

            CheckExpression("1+2*3", 7, ref failures, output);
            CheckExpression("Convert(101/2,System.Int32)%30", 20, ref failures, output);
            CheckExpression("(screenW-imageW)/2", 40, ref failures, output);
            string expressionError;
            Check(!SafeExpression.IsValid("IIF(1=1,1,0)", out expressionError),
                "DataTable expression functions rejected", ref failures, output);
            Check(!SafeExpression.IsValid("1/0", out expressionError),
                "division by zero rejected", ref failures, output);
            Check(SafeExpression.IsValid("1/(screenW-17)", out expressionError),
                "variable-dependent divisor is syntax-valid independent of screen state",
                ref failures, output);
            Check(SafeExpression.IsValid("1/(screenW-3440)", out expressionError),
                "runtime-zero divisor is not fabricated during validation",
                ref failures, output);
            Check(!SafeExpression.IsValid("missingVariable+1", out expressionError),
                "unknown expression variable rejected", ref failures, output);
            Check(!SafeExpression.IsValid("2147483648", out expressionError),
                "known constant result overflow rejected", ref failures, output);
            Check(Throws<DivideByZeroException>(delegate
                {
                    SafeExpression.Evaluate(
                        "1/(screenW-3440)",
                        delegate(string name)
                        {
                            if (name == "screenW") return 3440.0;
                            return 17.0;
                        });
                }),
                "runtime expression still rejects the actual zero divisor",
                ref failures, output);

            XmlData.RootNode parsed;
            string xmlError;
            string defaultXml = Properties.Resources.animations;
            Check(PetXmlValidator.TryParse(defaultXml, out parsed, out xmlError),
                "bundled pet XML validates" + FormatError(xmlError), ref failures, output);
            string canonicalPetPath;
            string petPathError;
            Check(
                !PetXmlValidator.TryResolveLocalXmlFile(
                    @"\\attacker.invalid\share\pet.xml",
                    out canonicalPetPath,
                    out petPathError) &&
                !PetXmlValidator.TryResolveLocalXmlFile(
                    @"\\?\UNC\attacker.invalid\share\pet.xml",
                    out canonicalPetPath,
                    out petPathError),
                "UNC and device pet XML paths rejected before probing",
                ref failures,
                output);

            string variableDivisorXml = new Regex(
                @"<x>\s*screenW\+10\s*</x>").Replace(
                    defaultXml, "<x>1/(screenW-17)</x>", 1);
            Check(PetXmlValidator.TryParse(
                    variableDivisorXml, out parsed, out xmlError),
                "pet XML admits variable-dependent divisors" + FormatError(xmlError),
                ref failures, output);

            string constantZeroDivisorXml = new Regex(
                @"<x>\s*screenW\+10\s*</x>").Replace(
                    defaultXml, "<x>1/0</x>", 1);
            Check(!PetXmlValidator.TryParse(
                    constantZeroDivisorXml, out parsed, out xmlError),
                "pet XML rejects known zero divisors",
                ref failures, output);

            string dtd = "<?xml version=\"1.0\"?><!DOCTYPE animations [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>" +
                         "<animations xmlns=\"https://esheep.petrucci.ch/\"><header><author>&xxe;</author></header></animations>";
            Check(!PetXmlValidator.TryParse(dtd, out parsed, out xmlError),
                "DTD input rejected", ref failures, output);

            string zeroTiles = new Regex(@"<tilesx>\s*\d+\s*</tilesx>").Replace(
                defaultXml, "<tilesx>0</tilesx>", 1);
            Check(!PetXmlValidator.TryParse(zeroTiles, out parsed, out xmlError),
                "zero tile count rejected", ref failures, output);

            CheckRetainedLocalXmlAdmission(ref failures, output);
            CheckIconDirectoryPreflight(defaultXml, ref failures, output);
            CheckPetXmlResourceLimits(defaultXml, ref failures, output);
            CheckAudioValidation(defaultXml, ref failures, output);
            CheckAboutLinkPolicy(ref failures, output);
            CheckSharedSpriteFrameOwnership(ref failures, output);
            CheckAiNormalization(ref failures, output);
            CheckAiResponseBounds(ref failures, output);
            CheckAiResponseDeadline(ref failures, output);
            // Smart-fortune lifecycle tests moved to the Fortunes module with the engine (S3d);
            // exercised there via --fortunes-engine-selftest. The idle-schedule test stays (AI-brain).
            CheckIdleScheduleGeneration(ref failures, output);
            CheckOllamaStartupDeadline(ref failures, output);
            CheckAiHttpStatusPolicy(ref failures, output);
            CheckAiRetirementBound(ref failures, output);
            CheckAiReconfigureDisposeRace(ref failures, output);
            CheckAiAfterRetireDurability(ref failures, output);
            CheckSecureDownloadDeadline(ref failures, output);
            CheckRestartLifecycle(ref failures, output);

            output.WriteLine(failures == 0
                ? "Security self-test: PASS"
                : "Security self-test: FAIL (" + failures + " checks)");
            return failures == 0;
        }

        private static void CheckRetainedLocalXmlAdmission(
            ref int failures,
            TextWriter output)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-retained-xml-" +
                    Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "pet");
            string movedDirectory = Path.Combine(root, "pet-moved");
            string path = Path.Combine(directory, "animations.xml");
            const string expected = "<animations />";
            bool writeBlocked = false;
            bool directorySwapBlocked = false;
            PetXmlValidator.RetainedLocalXmlFile retained = null;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    path,
                    expected,
                    new UTF8Encoding(false, true));
                PetXmlValidator.LocalXmlHandleOpenedForDiagnostics =
                    delegate(string retainedPath)
                    {
                        try
                        {
                            File.WriteAllText(
                                retainedPath,
                                "<attacker />",
                                new UTF8Encoding(false, true));
                        }
                        catch (IOException)
                        {
                            writeBlocked = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            writeBlocked = true;
                        }

                        try
                        {
                            Directory.Move(directory, movedDirectory);
                        }
                        catch (IOException)
                        {
                            directorySwapBlocked = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            directorySwapBlocked = true;
                        }
                    };

                string error;
                bool opened = PetXmlValidator.TryOpenLocalXmlFile(
                    path,
                    out retained,
                    out error);
                string observed = null;
                if (opened)
                {
                    using (retained)
                    using (var stream = retained.OpenRead(4096))
                    using (var reader = new StreamReader(
                        stream,
                        new UTF8Encoding(false, true),
                        true,
                        1024))
                        observed = reader.ReadToEnd();
                    retained = null;
                }
                Check(
                    opened &&
                    writeBlocked &&
                    directorySwapBlocked &&
                    observed == expected,
                    "local pet admission retains the validated file and directory chain" +
                        FormatError(error),
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "retained local pet admission threw " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                PetXmlValidator.LocalXmlHandleOpenedForDiagnostics = null;
                if (retained != null) retained.Dispose();
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch { }
            }
        }

        private static void CheckIconDirectoryPreflight(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            byte[] hugePngHeader = BuildPngHeader(8192, 8192);
            byte[] onePixelPngHeader = BuildPngHeader(1, 1);

            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1 },
                    new[] { 1 },
                    new[] { hugePngHeader },
                    null),
                "ICO embedded dimensions cannot exceed their directory declaration",
                ref failures,
                output);

            byte[] excessiveEntries = new byte[22];
            excessiveEntries[2] = 1;
            WriteLittleEndianUInt16(
                excessiveEntries,
                4,
                PetXmlValidator.MaximumIconEntries + 1);
            CheckRejectedIcon(
                defaultXml,
                excessiveEntries,
                "ICO entry count is bounded before decoding",
                ref failures,
                output);

            int sharedOffset = 6 + 2 * 16;
            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1, 1 },
                    new[] { 1, 1 },
                    new[] { onePixelPngHeader, onePixelPngHeader },
                    new[] { sharedOffset, sharedOffset }),
                "overlapping ICO payloads are rejected before decoding",
                ref failures,
                output);

            CheckRejectedIcon(
                defaultXml,
                BuildIcon(
                    new[] { 1 },
                    new[] { 1 },
                    new[] { onePixelPngHeader },
                    new[] { 4096 }),
                "out-of-range ICO payloads are rejected before decoding",
                ref failures,
                output);
        }

        private static void CheckRejectedIcon(
            string defaultXml,
            byte[] icon,
            string name,
            ref int failures,
            TextWriter output)
        {
            string replacement =
                "<icon><![CDATA[" +
                Convert.ToBase64String(icon) +
                "]]></icon>";
            string xml = new Regex(
                @"<icon\b[^>]*>.*?</icon>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Replace(
                    defaultXml,
                    replacement,
                    1);
            XmlData.RootNode parsed;
            string error;
            Check(
                !PetXmlValidator.TryParse(
                    xml,
                    out parsed,
                    out error) &&
                !string.IsNullOrWhiteSpace(error) &&
                error.IndexOf(
                    "ICO",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                name + FormatError(error),
                ref failures,
                output);
        }

        private static byte[] BuildIcon(
            int[] widths,
            int[] heights,
            byte[][] payloads,
            int[] forcedOffsets)
        {
            int count = payloads.Length;
            int directoryBytes = checked(6 + count * 16);
            int payloadBytes = 0;
            for (int index = 0; index < count; index++)
                payloadBytes = checked(payloadBytes + payloads[index].Length);
            var icon = new byte[checked(directoryBytes + payloadBytes)];
            icon[2] = 1;
            WriteLittleEndianUInt16(icon, 4, count);
            int nextOffset = directoryBytes;
            for (int index = 0; index < count; index++)
            {
                int entry = 6 + index * 16;
                icon[entry] = widths[index] == 256
                    ? (byte)0
                    : (byte)widths[index];
                icon[entry + 1] = heights[index] == 256
                    ? (byte)0
                    : (byte)heights[index];
                icon[entry + 4] = 1;
                icon[entry + 6] = 32;
                WriteLittleEndianUInt32(
                    icon,
                    entry + 8,
                    (uint)payloads[index].Length);
                int declaredOffset =
                    forcedOffsets == null
                        ? nextOffset
                        : forcedOffsets[index];
                WriteLittleEndianUInt32(
                    icon,
                    entry + 12,
                    (uint)declaredOffset);
                Buffer.BlockCopy(
                    payloads[index],
                    0,
                    icon,
                    nextOffset,
                    payloads[index].Length);
                nextOffset += payloads[index].Length;
            }
            return icon;
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            var header = new byte[24];
            byte[] signature =
            {
                0x89, 0x50, 0x4e, 0x47,
                0x0d, 0x0a, 0x1a, 0x0a
            };
            Buffer.BlockCopy(signature, 0, header, 0, signature.Length);
            header[11] = 13;
            header[12] = 0x49;
            header[13] = 0x48;
            header[14] = 0x44;
            header[15] = 0x52;
            WriteBigEndianInt32(header, 16, width);
            WriteBigEndianInt32(header, 20, height);
            return header;
        }

        private static void WriteLittleEndianUInt16(
            byte[] bytes,
            int offset,
            int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteLittleEndianUInt32(
            byte[] bytes,
            int offset,
            uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteBigEndianInt32(
            byte[] bytes,
            int offset,
            int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static void CheckAboutLinkPolicy(
            ref int failures,
            TextWriter output)
        {
            string normalized;
            bool allowed = AboutBox.TryNormalizeHttpsLink(
                "https://example.com/pet",
                out normalized);
            bool httpRejected = !AboutBox.TryNormalizeHttpsLink(
                "http://example.com/pet",
                out normalized);
            bool userInfoRejected = !AboutBox.TryNormalizeHttpsLink(
                "https://user:secret@example.com/pet",
                out normalized);
            bool nonWebRejected = !AboutBox.TryNormalizeHttpsLink(
                "file:///C:/Windows/win.ini",
                out normalized);
            Check(
                allowed &&
                httpRejected &&
                userInfoRejected &&
                nonWebRejected,
                "pet-supplied About links allow only HTTPS without userinfo",
                ref failures,
                output);
        }

        private static void CheckPetXmlResourceLimits(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            Check(
                PetXmlValidator.MaximumSpriteTiles ==
                SpriteFrameStore.MaximumFrames,
                "pet validator and runtime share the sprite-tile limit",
                ref failures,
                output);

            XmlData.RootNode parsed;
            string error;
            string excessiveTiles = new Regex(
                @"<tilesx>\s*\d+\s*</tilesx>").Replace(
                    defaultXml,
                    "<tilesx>256</tilesx>",
                    1);
            excessiveTiles = new Regex(
                @"<tilesy>\s*\d+\s*</tilesy>").Replace(
                    excessiveTiles,
                    "<tilesy>5</tilesy>",
                    1);
            Check(
                !PetXmlValidator.TryParse(
                    excessiveTiles,
                    out parsed,
                    out error) &&
                error != null &&
                error.IndexOf(
                    "too many tiles",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects sprite sheets over the runtime's 1,024-tile limit",
                ref failures,
                output);

            string maximumTransitions = BuildTransitionLimitXml(
                defaultXml,
                PetXmlValidator.MaximumTransitions);
            Check(
                PetXmlValidator.TryParse(
                    maximumTransitions,
                    out parsed,
                    out error),
                "pet XML accepts exactly 256 transitions" + FormatError(error),
                ref failures,
                output);

            string excessiveTransitions = BuildTransitionLimitXml(
                defaultXml,
                PetXmlValidator.MaximumTransitions + 1);
            Check(
                !PetXmlValidator.TryParse(
                    excessiveTransitions,
                    out parsed,
                    out error) &&
                error != null &&
                error.IndexOf(
                    "too many transitions",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects transition sets over 256 entries",
                ref failures,
                output);
        }

        private static string BuildTransitionLimitXml(string source, int count)
        {
            var sequence = new StringBuilder(
                "<sequence repeat=\"1\" repeatfrom=\"0\"><frame>0</frame>");
            for (int index = 0; index < count; index++)
                sequence.Append("<next probability=\"1\">1</next>");
            sequence.Append("</sequence>");
            return new Regex(
                @"<sequence\b[^>]*>.*?</sequence>",
                RegexOptions.Singleline).Replace(
                    source,
                    sequence.ToString(),
                    1);
        }

        private static void CheckAudioValidation(
            string defaultXml,
            ref int failures,
            TextWriter output)
        {
            byte[] invalidAudio = Encoding.ASCII.GetBytes("not-an-mp3");
            string audioError;
            Check(
                !TSound.LooksLikeMp3(invalidAudio, out audioError) &&
                !string.IsNullOrWhiteSpace(audioError),
                "invalid MP3 data fails the base header sanity check",
                ref failures,
                output);

            int rootClose = defaultXml.LastIndexOf(
                "</animations>",
                StringComparison.OrdinalIgnoreCase);
            string invalidAudioXml =
                defaultXml.Insert(
                    rootClose,
                    "<sounds><sound animationid=\"1\">" +
                    "<probability>100</probability><loop>0</loop>" +
                    "<base64>" + Convert.ToBase64String(invalidAudio) +
                    "</base64></sound></sounds>");
            XmlData.RootNode parsed;
            string xmlError;
            Check(
                !PetXmlValidator.TryParse(
                    invalidAudioXml,
                    out parsed,
                    out xmlError) &&
                xmlError != null &&
                xmlError.IndexOf(
                    "usable MP3",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "pet XML rejects audio that cannot be decoded as MP3",
                ref failures,
                output);

            using (var xml = new Xml())
            using (var animations = new Animations(xml))
            {
                animations.AddSound(
                    1,
                    100,
                    0,
                    Convert.ToBase64String(invalidAudio));
                Check(
                    !animations.SheepSound.ContainsKey(1),
                    "failed sounds are not inserted into the animation dictionary",
                    ref failures,
                    output);

                // A structurally-valid MP3 (MPEG frame sync) is accepted and its raw bytes are carried for
                // the Sound module; the base itself does not decode or open an audio device.
                byte[] validAudio = Convert.FromBase64String(
                    "/+MYxAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxDsAAANIAAAAAFVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "/+MYxHYAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxLEAAANIAAAAAFVVVVVVVVVVVVVV" +
                    "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV");
                animations.AddSound(2, 100, 3, Convert.ToBase64String(validAudio));
                List<TSound> variants;
                Check(
                    animations.SheepSound.TryGetValue(2, out variants) &&
                    variants.Count == 1 &&
                    variants[0].Data != null &&
                    variants[0].Data.Length == validAudio.Length,
                    "valid MP3 is accepted and its raw bytes are carried (no decode in the base)",
                    ref failures,
                    output);
            }
        }

        private static void CheckSharedSpriteFrameOwnership(
            ref int failures,
            TextWriter output)
        {
            SynchronizationContext previousSynchronizationContext =
                SynchronizationContext.Current;
            Bitmap unownedFrame = null;
            Xml xml = null;
            Animations animations = null;
            FormPet firstRoot = null;
            FormPet secondRoot = null;
            FormPet child = null;
            FormPet failedRoot = null;
            try
            {
                unownedFrame = new Bitmap(3, 1);
                unownedFrame.SetPixel(0, 0, Color.Red);
                unownedFrame.SetPixel(1, 0, Color.Green);
                unownedFrame.SetPixel(2, 0, Color.Blue);

                xml = new Xml(
                    new List<Bitmap> { unownedFrame },
                    unownedFrame.Width,
                    unownedFrame.Height);
                unownedFrame = null;
                animations = new Animations(xml);
                firstRoot = new FormPet(animations, xml);
                secondRoot = new FormPet(animations, xml);

                Bitmap firstOriginal =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Bitmap secondOriginal =
                    (Bitmap)secondRoot.SpriteFrameForDiagnostics(0);
                Check(
                    ReferenceEquals(firstOriginal, secondOriginal),
                    "root pets share the original sprite frame",
                    ref failures,
                    output);

                firstRoot.FlipOrientationForDiagnostics();
                Bitmap firstFlipped =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Bitmap cachedFlipped =
                    (Bitmap)firstRoot.SpriteFrameForDiagnostics(0);
                Check(
                    firstOriginal.GetPixel(0, 0).ToArgb() ==
                        Color.Red.ToArgb() &&
                    firstOriginal.GetPixel(2, 0).ToArgb() ==
                        Color.Blue.ToArgb(),
                    "flipping a pet leaves the original frame unchanged",
                    ref failures,
                    output);
                Check(
                    firstFlipped.GetPixel(0, 0).ToArgb() ==
                        Color.Blue.ToArgb() &&
                    firstFlipped.GetPixel(2, 0).ToArgb() ==
                        Color.Red.ToArgb(),
                    "shared flipped sprite frame mirrors asymmetric pixels",
                    ref failures,
                    output);
                Check(
                    ReferenceEquals(firstFlipped, cachedFlipped),
                    "flipped sprite frame is cached by identity",
                    ref failures,
                    output);
                Check(
                    xml.MaterializedFlippedFrameCount == 1 &&
                    xml.MaterializedFlippedFrameCount <= xml.SpriteCount,
                    "lazy flipped-frame ownership remains bounded",
                    ref failures,
                    output);

                child = firstRoot.CreateUnshownChildForDiagnostics();
                Check(
                    child.IsMovingLeftForDiagnostics ==
                        firstRoot.IsMovingLeftForDiagnostics &&
                    ReferenceEquals(
                        child.SpriteFrameForDiagnostics(0),
                        firstFlipped),
                    "child inherits orientation and shared sprite identity",
                    ref failures,
                    output);

                bool initializationFailed = false;
                try
                {
                    StartUp.CreateAndInitializeOwnedPet(
                        delegate
                        {
                            failedRoot = new FormPet(animations, xml);
                            return failedRoot;
                        },
                        delegate(FormPet ignored)
                        {
                            throw new InvalidOperationException(
                                "diagnostic initialization failure");
                        });
                }
                catch (InvalidOperationException)
                {
                    initializationFailed = true;
                }
                Check(
                    initializationFailed &&
                    failedRoot != null &&
                    failedRoot.IsDisposed,
                    "failed root-form initialization disposes the unowned form",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "shared sprite-frame ownership regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (child != null) child.Dispose();
                if (secondRoot != null) secondRoot.Dispose();
                if (firstRoot != null) firstRoot.Dispose();
                if (failedRoot != null) failedRoot.Dispose();
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
                if (unownedFrame != null) unownedFrame.Dispose();
                SynchronizationContext.SetSynchronizationContext(
                    previousSynchronizationContext);
            }
        }

        private static void CheckAiSettingsPersistence(
            ref int failures,
            TextWriter output)
        {
            string previousOverride = Environment.GetEnvironmentVariable(
                AppPaths.DataRootOverrideEnvironmentVariable);
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-ai-settings-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                Environment.SetEnvironmentVariable(
                    AppPaths.DataRootOverrideEnvironmentVariable,
                    directory);
                string path = Path.Combine(directory, "ai-settings.json");
                File.WriteAllText(
                    path,
                    "{\n" +
                    "  \"SchemaVersion\": 1,\n" +
                    "  \"TimeoutSeconds\": 120,\n" +
                    "  \"MemoryEnabled\": false,\n" +
                    "  \"futureSameSchema\": { \"keep\": true }\n" +
                    "}",
                    new UTF8Encoding(false));

                Ai.AiSettings first = Ai.AiSettings.Load();
                Ai.AiSettings second = Ai.AiSettings.Load();
                first.TimeoutSeconds = 77;
                bool firstSaved = first.Save();
                second.MemoryEnabled = true;
                bool secondSaved = second.Save();
                JObject merged = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                Check(
                    firstSaved &&
                    secondSaved &&
                    (int)merged["TimeoutSeconds"] == 77 &&
                    (bool)merged["MemoryEnabled"] &&
                    (bool)merged["futureSameSchema"]["keep"],
                    "AI settings stale writers merge and preserve unknown fields",
                    ref failures,
                    output);

                Ai.AiSettings keyWriterA = Ai.AiSettings.Load();
                Ai.AiSettings keyWriterB = Ai.AiSettings.Load();
                keyWriterA.Provider = "openai";
                keyWriterA.OpenAiBaseUrl = "https://api.openai.com/v1";
                keyWriterA.ApiKey = "stale-writer-openai-key";
                keyWriterB.Provider = "openrouter";
                keyWriterB.OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                keyWriterB.ApiKey = "stale-writer-router-key";
                bool keyWriterASaved = keyWriterA.Save();
                bool keyWriterBSaved = keyWriterB.Save();
                Ai.AiSettings mergedKeys = Ai.AiSettings.Load();
                bool routerKeyPreserved =
                    mergedKeys.ApiKey == "stale-writer-router-key";
                mergedKeys.Provider = "openai";
                mergedKeys.OpenAiBaseUrl = "https://api.openai.com/v1";
                Check(
                    keyWriterASaved &&
                    keyWriterBSaved &&
                    routerKeyPreserved &&
                    mergedKeys.ApiKey == "stale-writer-openai-key",
                    "AI settings stale writers merge provider-scoped keys",
                    ref failures,
                    output);

                const string customEndpoint =
                    "https://gateway.example/TenantA/v1";
                const string customKey =
                    "custom-endpoint-key-do-not-persist";
                Ai.AiSettings customSettings = Ai.AiSettings.Load();
                customSettings.SelectProviderEndpoint("custom", true);
                customSettings.UpdateSelectedProviderEndpoint(customEndpoint);
                customSettings.ApiKey = customKey;
                string openAiEndpoint =
                    customSettings.SelectProviderEndpoint("openai", true);
                string restoredCustomEndpoint =
                    customSettings.SelectProviderEndpoint("custom", true);
                bool customSaved = customSettings.Save();
                Ai.AiSettings customReloaded = Ai.AiSettings.Load();
                Check(
                    string.Equals(
                        openAiEndpoint,
                        "https://api.openai.com/v1",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        restoredCustomEndpoint,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    customSettings.ApiKey == customKey &&
                    customSaved &&
                    string.Equals(
                        customReloaded.Provider,
                        "custom",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        customReloaded.OpenAiBaseUrl,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        customReloaded.CustomOpenAiBaseUrl,
                        customEndpoint,
                        StringComparison.Ordinal) &&
                    customReloaded.ApiKey == customKey,
                    "Custom provider endpoint and scoped key survive switching and reload",
                    ref failures,
                    output);

                Stopwatch lockWait = Stopwatch.StartNew();
                bool boundedSaveRejected;
                using (var contention = new FileStream(
                    path + ".lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    customReloaded.Personality = "bounded save contention";
                    boundedSaveRejected = !customReloaded.SaveWithin(125);
                }
                lockWait.Stop();
                Check(
                    boundedSaveRejected &&
                    lockWait.Elapsed < TimeSpan.FromSeconds(2),
                    "UI-budgeted AI settings save rejects a held lock promptly",
                    ref failures,
                    output);

                string undecryptable =
                    Convert.ToBase64String(new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 });
                string openAiScope = Ai.AiSettings.BuildCredentialScope(
                    "openai",
                    "https://api.openai.com/v1");
                byte[] normalizedPrimary = File.ReadAllBytes(path);
                byte[] normalizedBackup = File.ReadAllBytes(path + ".bak");

                JObject scopedFailure = JObject.Parse(
                    File.ReadAllText(path, Encoding.UTF8));
                scopedFailure["Provider"] = "openai";
                scopedFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                scopedFailure["ApiKeyEnc"] = "";
                scopedFailure["ApiKeysEnc"] = new JObject {
                    [openAiScope] = undecryptable
                };
                File.WriteAllText(
                    path,
                    scopedFailure.ToString(Newtonsoft.Json.Formatting.Indented),
                    new UTF8Encoding(false));
                byte[] scopedBeforeLoad = File.ReadAllBytes(path);
                Ai.AiSettings scopedLoaded = Ai.AiSettings.Load();
                JObject scopedAfterLoad = JObject.Parse(
                    File.ReadAllText(path, Encoding.UTF8));
                Check(
                    string.IsNullOrEmpty(scopedLoaded.ApiKey) &&
                    string.Equals(
                        (string)scopedAfterLoad["ApiKeysEnc"][openAiScope],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(scopedBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")),
                    "AI settings preserve provider-scoped ciphertext on DPAPI failure",
                    ref failures,
                    output);

                JObject legacyFailure = JObject.Parse(
                    Encoding.UTF8.GetString(normalizedPrimary));
                legacyFailure["Provider"] = "openai";
                legacyFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                legacyFailure["ApiKeysEnc"] = new JObject();
                legacyFailure["ApiKeyEnc"] = undecryptable;
                File.WriteAllText(
                    path,
                    legacyFailure.ToString(Newtonsoft.Json.Formatting.Indented),
                    new UTF8Encoding(false));
                byte[] legacyBeforeLoad = File.ReadAllBytes(path);
                Ai.AiSettings legacyLoaded = Ai.AiSettings.Load();
                JObject legacyAfterLoad = JObject.Parse(
                    File.ReadAllText(path, Encoding.UTF8));
                Check(
                    string.IsNullOrEmpty(legacyLoaded.ApiKey) &&
                    string.Equals(
                        (string)legacyAfterLoad["ApiKeyEnc"],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(legacyBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")),
                    "AI settings preserve legacy ciphertext on DPAPI failure",
                    ref failures,
                    output);
                File.WriteAllBytes(path, normalizedPrimary);

                string backupPath = path + ".bak";
                byte[] validBackup = File.ReadAllBytes(backupPath);
                JObject expectedBackup = JObject.Parse(
                    File.ReadAllText(backupPath, Encoding.UTF8));
                File.WriteAllText(
                    path,
                    "{ corrupt primary",
                    new UTF8Encoding(false));
                Ai.AiSettings recovered = Ai.AiSettings.Load();
                JObject repairedPrimary = JObject.Parse(
                    File.ReadAllText(path, Encoding.UTF8));
                Check(
                    recovered.TimeoutSeconds == (int)expectedBackup["TimeoutSeconds"] &&
                    (int)repairedPrimary["TimeoutSeconds"] ==
                        (int)expectedBackup["TimeoutSeconds"] &&
                    ByteArraysEqual(validBackup, File.ReadAllBytes(backupPath)),
                    "AI settings corrupt-primary recovery preserves the valid backup",
                    ref failures,
                    output);

                Ai.ChatHistory.DeletePersisted();
                var historySettings = new Ai.AiSettings
                {
                    Provider = "ollama",
                    Endpoint = "http://localhost:11434",
                    TextModel = "history-test",
                    VisionModel = "history-vision"
                };
                Ai.ChatHistory history = Ai.ChatHistory.Load(historySettings);
                history.Add("first context", "first reply");
                history.Add("second context", "second reply");
                string historyPath = Path.Combine(directory, "chat-history.json");
                string historyBackupPath = historyPath + ".bak";
                byte[] historyBackup = File.ReadAllBytes(historyBackupPath);
                File.WriteAllText(
                    historyPath,
                    "corrupt encrypted history",
                    new UTF8Encoding(false));
                Ai.ChatHistory recoveredHistory =
                    Ai.ChatHistory.Load(historySettings);
                IList<Ai.ChatMessage> recoveredMessages =
                    recoveredHistory.RecentMessages();
                bool recoveredFirstTurn =
                    recoveredMessages.Count == 2 &&
                    recoveredMessages[1].Content == "first reply";
                bool historyBackupPreserved =
                    ByteArraysEqual(
                        historyBackup,
                        File.ReadAllBytes(historyBackupPath));
                recoveredHistory.Add("third context", "third reply");
                IList<Ai.ChatMessage> persistedMessages =
                    Ai.ChatHistory.Load(historySettings).RecentMessages();
                Check(
                    recoveredFirstTurn &&
                    historyBackupPreserved &&
                    persistedMessages.Count == 4 &&
                    persistedMessages[3].Content == "third reply",
                    "chat history recovers from backup and remains writable",
                    ref failures,
                    output);

                string future =
                    "{\n  \"SchemaVersion\": 99,\n" +
                    "  \"TimeoutSeconds\": 42,\n" +
                    "  \"futureOnly\": true\n}";
                File.WriteAllText(path, future, new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                Ai.AiSettings futureSettings = Ai.AiSettings.Load();
                futureSettings.TimeoutSeconds = 50;
                bool blocked = !futureSettings.Save();
                byte[] after = File.ReadAllBytes(path);
                Check(
                    blocked && ByteArraysEqual(before, after),
                    "AI settings future schema remains byte-for-byte untouched",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "AI settings persistence self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    AppPaths.DataRootOverrideEnvironmentVariable,
                    previousOverride);
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    Check(
                        false,
                        "AI settings persistence self-test cleanup",
                        ref failures,
                        output);
                }
            }
        }

        private static void CheckAiCredentialScoping(
            ref int failures,
            TextWriter output)
        {
            const string openAiKey = "selftest-openai-key-do-not-persist";
            const string routerKey = "selftest-router-key-do-not-persist";
            const string customKey = "selftest-custom-key-do-not-persist";
            try
            {
                var settings = new Ai.AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                settings.ApiKey = openAiKey;
                bool openAiStored = settings.ApiKey == openAiKey;

                settings.Provider = "openrouter";
                settings.OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                bool providerIsolated = string.IsNullOrEmpty(settings.ApiKey);
                settings.ApiKey = routerKey;

                settings.SelectProviderEndpoint("custom", true);
                settings.UpdateSelectedProviderEndpoint(
                    "https://gateway.example/TenantA/v1");
                bool customEndpointIsolated = string.IsNullOrEmpty(settings.ApiKey);
                settings.ApiKey = customKey;

                settings.SelectProviderEndpoint("openai", true);
                bool openAiRestored = settings.ApiKey == openAiKey;
                settings.SelectProviderEndpoint("custom", true);
                bool customRestored =
                    settings.OpenAiBaseUrl ==
                        "https://gateway.example/TenantA/v1" &&
                    settings.ApiKey == customKey;
                settings.SelectProviderEndpoint("openrouter", true);
                bool routerRestored = settings.ApiKey == routerKey;
                Check(
                    openAiStored &&
                    providerIsolated &&
                    customEndpointIsolated &&
                    openAiRestored &&
                    customRestored &&
                    routerRestored,
                    "API keys are isolated by provider and endpoint",
                    ref failures,
                    output);

                var credentialA = new Ai.AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                credentialA.ApiKey = openAiKey;
                var credentialB = new Ai.AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                credentialB.ApiKey = routerKey;
                string partitionA =
                    Ai.ChatHistory.PartitionKeyForSelfTest(credentialA);
                string partitionB =
                    Ai.ChatHistory.PartitionKeyForSelfTest(credentialB);
                Check(
                    !string.Equals(
                        partitionA,
                        partitionB,
                        StringComparison.Ordinal),
                    "chat history is partitioned by credential identity",
                    ref failures,
                    output);

                var pathCaseA = new Ai.AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/TenantA/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                var pathCaseB = new Ai.AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/tenanta/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                var modelCaseB = new Ai.AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/TenantA/v1",
                    TextModel = "model-a",
                    VisionModel = "Vision-A"
                };
                string pathPartitionA =
                    Ai.ChatHistory.PartitionKeyForSelfTest(pathCaseA);
                Check(
                    pathPartitionA !=
                        Ai.ChatHistory.PartitionKeyForSelfTest(pathCaseB) &&
                    pathPartitionA !=
                        Ai.ChatHistory.PartitionKeyForSelfTest(modelCaseB),
                    "history identity preserves endpoint-path and model casing",
                    ref failures,
                    output);

                string serialized =
                    Newtonsoft.Json.JsonConvert.SerializeObject(credentialA);
                string scope = Ai.AiSettings.BuildCredentialScope(
                    credentialA.Provider,
                    credentialA.OpenAiBaseUrl);
                Check(
                    serialized.IndexOf(openAiKey, StringComparison.Ordinal) < 0 &&
                    scope.IndexOf(openAiKey, StringComparison.Ordinal) < 0 &&
                    partitionA.IndexOf(openAiKey, StringComparison.Ordinal) < 0,
                    "credential scope, persistence, and history identity omit plaintext keys",
                    ref failures,
                    output);

                var boundedCredentials = new Ai.AiSettings
                {
                    Provider = "custom"
                };
                bool admittedAllScopes = true;
                string admissionError = "";
                for (int index = 0;
                    index < Ai.AiSettings.MaximumApiKeyScopes;
                    index++)
                {
                    boundedCredentials.OpenAiBaseUrl =
                        "https://credentials.example/scope/" +
                        index.ToString(CultureInfo.InvariantCulture);
                    admittedAllScopes &=
                        boundedCredentials.TrySetApiKey(
                            "bounded-key-" +
                            index.ToString(CultureInfo.InvariantCulture),
                            out admissionError);
                }
                boundedCredentials.OpenAiBaseUrl =
                    "https://credentials.example/scope/overflow";
                bool overflowRejected =
                    !boundedCredentials.TrySetApiKey(
                        "must-not-be-silently-discarded",
                        out admissionError);
                boundedCredentials.OpenAiBaseUrl =
                    "https://credentials.example/scope/0";
                string updateError;
                bool existingScopeUpdated =
                    boundedCredentials.TrySetApiKey(
                        "updated-existing-key",
                        out updateError) &&
                    boundedCredentials.ApiKey == "updated-existing-key";
                Check(
                    admittedAllScopes &&
                    overflowRejected &&
                    boundedCredentials.ApiKeysEnc.Count ==
                        Ai.AiSettings.MaximumApiKeyScopes &&
                    !string.IsNullOrWhiteSpace(admissionError) &&
                    existingScopeUpdated,
                    "API key scope limit rejects new keys explicitly and permits updates",
                    ref failures,
                    output);

                string emoji = char.ConvertFromUtf32(0x1F642);
                string history256 = Ai.ChatHistory.NormalizeFieldForSelfTest(
                    new string('a', 255) + emoji,
                    256);
                string history512 = Ai.ChatHistory.NormalizeFieldForSelfTest(
                    new string('a', 511) + emoji,
                    512);
                string identity256 = Ai.ChatHistory.LimitIdentityForSelfTest(
                    new string('a', 255) + emoji,
                    256);
                Check(
                    history256.Length == 255 &&
                    history512.Length == 511 &&
                    identity256.Length == 255 &&
                    IsWellFormedUtf16(history256) &&
                    IsWellFormedUtf16(history512) &&
                    IsWellFormedUtf16(identity256),
                    "chat-history field and identity truncation preserve surrogate pairs",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "AI credential scoping self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
        }

        private static void CheckCrossSessionLock(
            ref int failures,
            TextWriter output)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-cross-session-lock-" + Guid.NewGuid().ToString("N"));
            IDisposable held = null;
            try
            {
                Directory.CreateDirectory(directory);
                string dataPath = Path.Combine(directory, "settings.json");
                string name = CrossSessionLock.BuildGlobalMutexName(
                    "SecuritySelfTest",
                    dataPath);
                held = CrossSessionLock.TryAcquire(name, dataPath, 1000);
                bool blockedWhileHeld = !Task.Run(delegate
                {
                    using (IDisposable contender =
                        CrossSessionLock.TryAcquire(name, dataPath, 100))
                        return contender != null;
                }).GetAwaiter().GetResult();

                if (held != null)
                {
                    held.Dispose();
                    held = null;
                }
                bool acquiredAfterRelease = Task.Run(delegate
                {
                    using (IDisposable contender =
                        CrossSessionLock.TryAcquire(name, dataPath, 1000))
                        return contender != null;
                }).GetAwaiter().GetResult();
                Check(
                    name.StartsWith(@"Global\", StringComparison.Ordinal) &&
                    blockedWhileHeld &&
                    acquiredAfterRelease,
                    "cross-session lock is global, exclusive, and reusable",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "cross-session lock self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (held != null) held.Dispose();
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    Check(
                        false,
                        "cross-session lock self-test cleanup",
                        ref failures,
                        output);
                }
            }
        }

        private static void CheckAiNormalization(ref int failures, TextWriter output)
        {
            var settings = new Ai.AiSettings
            {
                SchemaVersion = 0,
                Endpoint = " " + new string('e', 3000) + "\0 ",
                TextModel = new string('m', 400),
                VisionModel = new string('v', 400),
                TesseractPath = new string('t', 2000),
                PetName = new string('p', 200),
                UserName = new string('u', 200),
                Personality = new string('x', 700),
                Provider = "NOT-A-PROVIDER",
                TimeoutSeconds = int.MaxValue,
                IdleMinSeconds = -1,
                IdleMaxSeconds = int.MaxValue,
                IdleChangeThresholdPercent = int.MaxValue,
                DisabledSources = new List<string>()
            };
            for (int i = 0; i < 300; i++)
                settings.DisabledSources.Add(" source-" + i + " ");
            settings.Normalize();

            Check(settings.SchemaVersion == Ai.AiSettings.CurrentSchemaVersion,
                "AI settings schema normalized", ref failures, output);
            Check(settings.Endpoint.Length <= 2048 &&
                  settings.TextModel.Length <= 256 &&
                  settings.VisionModel.Length <= 256 &&
                  settings.TesseractPath.Length <= 1024 &&
                  settings.PetName.Length <= 80 &&
                  settings.UserName.Length <= 80 &&
                  settings.Personality.Length <= 512,
                "AI settings strings bounded", ref failures, output);
            Check(settings.Provider == "ollama" &&
                  settings.TimeoutSeconds == 600 &&
                  settings.IdleMinSeconds == 15 &&
                  settings.IdleMaxSeconds == 3600 &&
                  settings.IdleChangeThresholdPercent == 100,
                "AI settings values clamped", ref failures, output);
            Check(settings.DisabledSources.Count == 128,
                "AI disabled-source list bounded", ref failures, output);

            string normalizedModel;
            Check(
                Ai.AiModelPolicy.TryNormalize(" owner/model:tag ", out normalizedModel) &&
                normalizedModel == "owner/model:tag" &&
                !Ai.AiModelPolicy.TryNormalize("model\r\ninjected", out normalizedModel),
                "AI model identifiers bounded and sanitized",
                ref failures,
                output);
            Check(
                Ai.AiExecutablePolicy.ResolveConfigured(
                    "ollama.exe",
                    "ollama.exe") == null,
                "relative configured AI executable rejected",
                ref failures,
                output);
            CheckAiExecutablePathPolicy(ref failures, output);

            settings.Provider = "openai";
            settings.OpenAiBaseUrl = "https://api.openai.com/v1";
            string apiKeyError;
            bool oversizedApiKeyRejected =
                !settings.TrySetApiKey(
                    new string('k', 9000),
                    out apiKeyError);
            Check(
                oversizedApiKeyRejected &&
                !string.IsNullOrWhiteSpace(apiKeyError) &&
                string.IsNullOrEmpty(settings.ApiKeyEnc) &&
                settings.ApiKeysEnc.Count == 0,
                "oversized API key rejected", ref failures, output);
        }

        private static void CheckAiExecutablePathPolicy(
            ref int failures,
            TextWriter output)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-ExecutablePolicy-" +
                    Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                string executable = Path.Combine(directory, "ollama.exe");
                File.WriteAllBytes(executable, new byte[] { 0 });
                string canonical = Path.GetFullPath(executable);
                string pathWithRemotePrefix =
                    @"\\server.invalid\share" +
                    Path.PathSeparator +
                    directory;

                Check(
                    string.Equals(
                        Ai.AiExecutablePolicy.ResolveConfigured(
                            executable,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Ai.AiExecutablePolicy.ResolveFromPath(
                            pathWithRemotePrefix,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    Ai.AiExecutablePolicy.IsReparseFreeLocalFile(
                        executable),
                    "local absolute AI executable paths remain supported",
                    ref failures,
                    output);

                Check(
                    !Ai.AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\server.invalid\share\ollama.exe") &&
                    !Ai.AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\?\C:\Apps\Ollama\ollama.exe") &&
                    !Ai.AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\.\C:\Apps\Ollama\ollama.exe") &&
                    !Ai.AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\??\C:\Apps\Ollama\ollama.exe") &&
                    Ai.AiExecutablePolicy.ResolveConfigured(
                        @"\\server.invalid\share\ollama.exe",
                        "ollama.exe") == null,
                    "UNC and device AI executable paths rejected before probing",
                    ref failures,
                    output);
                Check(
                    Ai.AiExecutablePolicy
                        .ReparseScanStopsBeforeTraversalForDiagnostics(),
                    "AI executable reparse point rejected before descendant probing",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "AI executable path policy regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    Check(
                        false,
                        "AI executable path policy cleanup",
                        ref failures,
                        output);
                }
            }
        }

        private static void CheckAiResponseBounds(ref int failures, TextWriter output)
        {
            string sanitized = Ai.AiBrain.SanitizeResponseText(
                " hello\r\n" + new string('x', 700) + "\0tail ");
            Check(sanitized.Length <= 512 &&
                  sanitized.IndexOf('\r') < 0 &&
                  sanitized.IndexOf('\n') < 0 &&
                  sanitized.IndexOf('\0') < 0,
                "assistant response text bounded and sanitized", ref failures, output);

            string astral = char.ConvertFromUtf32(0x1F642);
            string boundary = Ai.AiBrain.SanitizeResponseText(
                new string('x', 511) + astral + "tail");
            string exact = Ai.AiBrain.SanitizeResponseText(
                new string('x', 510) + astral + "tail");
            Check(
                boundary.Length == 511 &&
                exact.Length == 512 &&
                IsWellFormedUtf16(boundary) &&
                IsWellFormedUtf16(exact),
                "assistant response truncation preserves surrogate pairs",
                ref failures,
                output);

            byte[] oversized = Encoding.UTF8.GetBytes(new string('a', 2048));
            using (var content = new ByteArrayContent(oversized))
            {
                content.Headers.ContentLength = 1;
                Check(Throws<InvalidDataException>(delegate
                {
                    Ai.AiEndpointPolicy.ReadResponseStringAsync(
                        content,
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }), "chunked/misleading AI response rejected", ref failures, output);
            }

            using (var invalidUtf8 = new ByteArrayContent(new byte[] { 0xC3, 0x28 }))
            {
                invalidUtf8.Headers.ContentLength = null;
                Check(Throws<DecoderFallbackException>(delegate
                {
                    Ai.AiEndpointPolicy.ReadResponseStringAsync(
                        invalidUtf8,
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }), "invalid UTF-8 AI response rejected", ref failures, output);
            }
        }

        private static void CheckAiResponseDeadline(
            ref int failures,
            TextWriter output)
        {
            bool bodyTimedOut = false;
            var bodyHandler = new BlockingBodyHandler();
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (bodyHandler)
                using (var client = new HttpClient(bodyHandler, false)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                })
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ai-body"))
                {
                    Ai.AiEndpointPolicy.SendAndReadResponseStringAsync(
                        client,
                        request,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                bodyTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                bodyTimedOut &&
                bodyHandler.StreamDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "AI deadline bounds cancellation-ignoring response reads and disposes them",
                ref failures,
                output);

            bool streamAcquisitionTimedOut = false;
            var streamHandler = new BlockingReadAsStreamHandler();
            stopwatch.Restart();
            try
            {
                using (streamHandler)
                using (var client = new HttpClient(streamHandler, false)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                })
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ai-stream"))
                {
                    Ai.AiEndpointPolicy.SendAndReadResponseStringAsync(
                        client,
                        request,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None,
                        1024).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                streamAcquisitionTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                streamAcquisitionTimedOut &&
                streamHandler.ContentDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "AI deadline bounds cancellation-ignoring response stream acquisition",
                ref failures,
                output);
        }

        private static void CheckIdleScheduleGeneration(
            ref int failures,
            TextWriter output)
        {
            var schedule = new GenerationAwareIdleSchedule();
            var tickEntered = new ManualResetEventSlim(false);
            var tickRelease = new ManualResetEventSlim(false);
            int staleRearmed = 0;
            bool tickCompleted = false;
            try
            {
                schedule.Reconfigure(1, true);
                bool initiallyArmed = schedule.TryArm(1);
                Task tick = Task.Run(delegate
                {
                    if (!schedule.TryBeginTick(1)) return;
                    tickEntered.Set();
                    tickRelease.Wait();
                    if (schedule.TryArm(1))
                        Interlocked.Exchange(ref staleRearmed, 1);
                });
                bool entered = tickEntered.Wait(TimeSpan.FromSeconds(2));

                // Simulate disabling the brain or invalidating its endpoint policy while the
                // asynchronous tick is between admission and its finally/rearm path.
                schedule.Reconfigure(2, false);
                tickRelease.Set();
                tickCompleted = tick.Wait(TimeSpan.FromSeconds(2));

                schedule.Reconfigure(3, true);
                bool reenabledOnce = schedule.TryArm(3);
                bool duplicateRejected = !schedule.TryArm(3);
                bool retiredRejected = !schedule.TryArm(1);
                Check(
                    initiallyArmed &&
                    entered &&
                    tickCompleted &&
                    Volatile.Read(ref staleRearmed) == 0 &&
                    reenabledOnce &&
                    duplicateRejected &&
                    retiredRejected &&
                    schedule.IsArmedForDiagnostics,
                    "idle scheduling rejects stale rearm and arms one current generation",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "idle schedule generation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                tickRelease.Set();
                tickRelease.Dispose();
                tickEntered.Dispose();
            }
        }

        private static void CheckOllamaStartupDeadline(
            ref int failures,
            TextWriter output)
        {
            var boundedHandler = new FirstUnavailableThenBlockingHandler();
            bool boundedResult = true;
            bool boundedCompleted = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (var client = new Ai.OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    boundedHandler,
                    TimeSpan.FromMilliseconds(300),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
                {
                    boundedResult = client.EnsureServerAsync(
                        CancellationToken.None).GetAwaiter().GetResult();
                    boundedCompleted = true;
                }
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "Ollama startup deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            stopwatch.Stop();
            Check(
                boundedCompleted &&
                !boundedResult &&
                boundedHandler.RequestCount >= 2 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                "Ollama startup uses short probes within one overall deadline",
                ref failures,
                output);

            var starterEnteredEvent = new ManualResetEventSlim(false);
            var starterReleaseEvent = new ManualResetEventSlim(false);
            var starterExitedEvent = new ManualResetEventSlim(false);
            bool blockingStarterCompleted = false;
            bool blockingStarterResult = true;
            bool blockingStarterEntered = false;
            bool blockingStarterExited = false;
            stopwatch.Restart();
            try
            {
                using (var starterHandler =
                    new FirstUnavailableThenBlockingHandler())
                using (var client = new Ai.OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    starterHandler,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored)
                    {
                        starterEnteredEvent.Set();
                        try
                        {
                            starterReleaseEvent.Wait();
                            return true;
                        }
                        finally
                        {
                            starterExitedEvent.Set();
                        }
                    }))
                {
                    blockingStarterResult = client.EnsureServerAsync(
                        CancellationToken.None).GetAwaiter().GetResult();
                    blockingStarterCompleted = true;
                }
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "Ollama blocking-starter deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                stopwatch.Stop();
                starterReleaseEvent.Set();
                blockingStarterExited = starterExitedEvent.Wait(
                    TimeSpan.FromSeconds(3));
                blockingStarterEntered = starterEnteredEvent.IsSet;
                starterExitedEvent.Dispose();
                starterReleaseEvent.Dispose();
                starterEnteredEvent.Dispose();
            }
            Check(
                blockingStarterCompleted &&
                !blockingStarterResult &&
                blockingStarterEntered &&
                blockingStarterExited &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                "Ollama overall deadline bounds a synchronous blocking starter",
                ref failures,
                output);

            var lateStarterEntered = new ManualResetEventSlim(false);
            var lateStarterRelease = new ManualResetEventSlim(false);
            var lateStarterExited = new ManualResetEventSlim(false);
            int lateLaunchCount = 0;
            int lateStarterObservedCancellation = 0;
            bool lateStarterCanceled = false;
            bool lateStarterFinished = false;
            stopwatch.Restart();
            try
            {
                using (var lateHandler =
                    new FirstUnavailableThenBlockingHandler())
                using (var callerCancellation =
                    new CancellationTokenSource())
                using (var client = new Ai.OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    lateHandler,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken starterToken)
                    {
                        lateStarterEntered.Set();
                        try
                        {
                            lateStarterRelease.Wait();
                            if (starterToken.IsCancellationRequested)
                                Interlocked.Exchange(
                                    ref lateStarterObservedCancellation,
                                    1);
                            starterToken.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref lateLaunchCount);
                            return true;
                        }
                        finally
                        {
                            lateStarterExited.Set();
                        }
                    }))
                {
                    Task<bool> pending = client.EnsureServerAsync(
                        callerCancellation.Token);
                    lateStarterEntered.Wait(TimeSpan.FromSeconds(2));
                    callerCancellation.Cancel();
                    try { pending.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        lateStarterCanceled = true;
                    }
                    lateStarterRelease.Set();
                    lateStarterFinished = lateStarterExited.Wait(
                        TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "Ollama late-starter cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                stopwatch.Stop();
                lateStarterRelease.Set();
                if (!lateStarterFinished)
                    lateStarterFinished = lateStarterExited.Wait(
                        TimeSpan.FromSeconds(2));
                if (lateStarterFinished)
                {
                    lateStarterExited.Dispose();
                    lateStarterRelease.Dispose();
                    lateStarterEntered.Dispose();
                }
            }
            Check(
                lateStarterCanceled &&
                lateStarterFinished &&
                Volatile.Read(ref lateStarterObservedCancellation) == 1 &&
                Volatile.Read(ref lateLaunchCount) == 0 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                "Ollama cancellation prevents a queued starter's late launch",
                ref failures,
                output);

            bool callerCancellationObserved = false;
            stopwatch.Restart();
            try
            {
                using (var callerHandler = new BlockingHeadersHandler())
                using (var callerCancellation = new CancellationTokenSource())
                using (var client = new Ai.OllamaClient(
                    "http://localhost:11434",
                    TimeSpan.FromSeconds(600),
                    "",
                    callerHandler,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(10),
                    delegate(CancellationToken ignored) { return true; }))
                {
                    callerCancellation.CancelAfter(
                        TimeSpan.FromMilliseconds(75));
                    client.EnsureServerAsync(
                        callerCancellation.Token).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException)
            {
                callerCancellationObserved = true;
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "Ollama caller-cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            stopwatch.Stop();
            Check(
                callerCancellationObserved &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                "Ollama startup preserves caller cancellation",
                ref failures,
                output);
        }

        private static void CheckAiHttpStatusPolicy(ref int failures, TextWriter output)
        {
            using (var badRequest = new HttpResponseMessage(HttpStatusCode.BadRequest))
            {
                bool deterministic = false;
                try { Ai.AiEndpointPolicy.EnsureSuccess(badRequest); }
                catch (Ai.AiBackendHttpException ex)
                {
                    deterministic = ex.StatusCode == 400 && !ex.IsTransient;
                }
                Check(deterministic, "HTTP 400 is not retryable", ref failures, output);
            }

            using (var throttled = new HttpResponseMessage((HttpStatusCode)429))
            {
                bool transient = false;
                try { Ai.AiEndpointPolicy.EnsureSuccess(throttled); }
                catch (Ai.AiBackendHttpException ex)
                {
                    transient = ex.StatusCode == 429 && ex.IsTransient;
                }
                Check(transient, "HTTP 429 is retryable", ref failures, output);
            }

            using (var redirect = new HttpResponseMessage(HttpStatusCode.Redirect))
            {
                bool deterministicRedirect = false;
                try { Ai.AiEndpointPolicy.EnsureSuccess(redirect); }
                catch (Ai.AiBackendHttpException ex)
                {
                    deterministicRedirect = ex.StatusCode == 302 && !ex.IsTransient;
                }
                Check(
                    deterministicRedirect,
                    "AI redirect rejected as non-retryable before credential forwarding",
                    ref failures,
                    output);
            }

            var backend = new DeterministicFailureBackend();
            bool failedWithoutRetry = Throws<Ai.AiBackendHttpException>(delegate
            {
                Ai.AiBrain.ChatWithRetryForDiagnosticsAsync(
                    backend,
                    "model",
                    new List<Ai.ChatMessage>
                    {
                        Ai.ChatMessage.User("test", null)
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            });
            Check(
                failedWithoutRetry && backend.ChatCalls == 1,
                "deterministic AI failure was not retried",
                ref failures,
                output);
        }

        private static void CheckAiRetirementBound(ref int failures, TextWriter output)
        {
            var manager = new Ai.AiSessionManager();
            try
            {
                var settings = new Ai.AiSettings
                {
                    TextModel = "text-model",
                    VisionModel = "vision-model"
                };
                manager.ReconfigureAsync(
                    delegate
                    {
                        return new Ai.AiBrain(
                            new CancellationIgnoringBackend(),
                            settings);
                    },
                    true,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();

                Stopwatch stopwatch = Stopwatch.StartNew();
                manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();
                stopwatch.Stop();
                Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    "cancellation-ignoring AI retirement bounded",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(false,
                    "AI retirement test threw " + ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                manager.Dispose();
            }
        }

        private static void CheckAiReconfigureDisposeRace(
            ref int failures,
            TextWriter output)
        {
            var admitted = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var manager = new Ai.AiSessionManager();
            Task<bool> reconfigure = null;
            try
            {
                manager.ReconfigureAdmittedForDiagnostics =
                    delegate
                    {
                        admitted.Set();
                        release.Wait();
                    };
                reconfigure = Task.Run(delegate
                {
                    return manager.ReconfigureAsync(
                        null,
                        false,
                        false,
                        CancellationToken.None).GetAwaiter().GetResult();
                });
                bool reachedAdmission =
                    admitted.Wait(TimeSpan.FromSeconds(2));
                manager.DisposeForDiagnostics(
                    TimeSpan.FromMilliseconds(250));
                release.Set();
                bool completed =
                    reconfigure.Wait(TimeSpan.FromSeconds(2));
                Check(
                    reachedAdmission &&
                    completed &&
                    reconfigure.Status == TaskStatus.RanToCompletion &&
                    !reconfigure.Result,
                    "AI reconfiguration returns false when disposal wins before semaphore wait",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(
                    false,
                    "AI reconfigure/dispose race threw " +
                        ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                release.Set();
                if (reconfigure != null)
                    try { reconfigure.Wait(TimeSpan.FromSeconds(2)); }
                    catch { }
                manager.Dispose();
                release.Dispose();
                admitted.Dispose();
            }
        }

        private static void CheckAiAfterRetireDurability(
            ref int failures,
            TextWriter output)
        {
            CheckAiAfterRetireSupersession(ref failures, output);
            CheckAiAfterRetireMultipleSupersessions(ref failures, output);
            CheckAiAfterRetireNormalDispose(ref failures, output);
            CheckAiAfterRetireDeferredDispose(ref failures, output);
        }

        private static void CheckAiAfterRetireSupersession(
            ref int failures,
            TextWriter output)
        {
            RetirementTrackingBackend backend;
            Ai.AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int callbackCount = 0;
                bool observedRetired = false;
                bool observedSerialized = false;
                Task<bool> first = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref callbackCount);
                        observedRetired =
                            backend.UnloadCalls == 1 &&
                            backend.DisposeCount == 1;
                        observedSerialized = operation.CurrentCount == 0;
                    });
                Task<bool> second = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);
                bool supersededWhileHeld = first.Wait(TimeSpan.FromSeconds(1));

                operation.Release();
                held = false;
                Task.WaitAll(new Task[] { first, second });

                Check(
                    supersededWhileHeld &&
                    callbackCount == 1 &&
                    observedRetired &&
                    observedSerialized,
                    "after-retire action survives a superseding generation",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(false,
                    "after-retire supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
        }

        private static void CheckAiAfterRetireMultipleSupersessions(
            ref int failures,
            TextWriter output)
        {
            var manager = new Ai.AiSessionManager();
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int firstCount = 0;
                int secondCount = 0;
                int order = 0;
                int firstOrder = 0;
                int secondOrder = 0;
                bool callbacksSerialized = true;

                Task<bool> first = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref firstCount);
                        firstOrder = Interlocked.Increment(ref order);
                        callbacksSerialized &= operation.CurrentCount == 0;
                    });
                Task<bool> second = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None,
                    delegate
                    {
                        Interlocked.Increment(ref secondCount);
                        secondOrder = Interlocked.Increment(ref order);
                        callbacksSerialized &= operation.CurrentCount == 0;
                    });
                Task<bool> third = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);
                Task<bool> fourth = manager.ReconfigureAsync(
                    null,
                    false,
                    false,
                    CancellationToken.None);

                operation.Release();
                held = false;
                Task.WaitAll(new Task[] { first, second, third, fourth });

                Check(
                    firstCount == 1 &&
                    secondCount == 1 &&
                    firstOrder == 1 &&
                    secondOrder == 2 &&
                    callbacksSerialized,
                    "after-retire actions survive multiple superseding generations exactly once",
                    ref failures,
                    output);
            }
            catch (Exception ex)
            {
                Check(false,
                    "after-retire multi-supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
        }

        private static void CheckAiAfterRetireNormalDispose(
            ref int failures,
            TextWriter output)
        {
            RetirementTrackingBackend backend;
            Ai.AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            try
            {
                operation.Wait();
                held = true;
                int callbackCount = 0;
                bool observedRetired = false;
                bool observedSerialized = false;
                using (var cancellation = new CancellationTokenSource())
                {
                    Task<bool> pending = manager.ReconfigureAsync(
                        null,
                        false,
                        false,
                        cancellation.Token,
                        delegate
                        {
                            Interlocked.Increment(ref callbackCount);
                            observedRetired =
                                backend.UnloadCalls == 1 &&
                                backend.DisposeCount == 1;
                            observedSerialized = operation.CurrentCount == 0;
                        });
                    cancellation.Cancel();
                    bool canceledWhileHeld =
                        pending.Wait(TimeSpan.FromSeconds(1));

                    operation.Release();
                    held = false;
                    // Exercise the production disposal budget here. A zero diagnostic
                    // budget intentionally skips the optional unload wait and only
                    // disposes the backend, so it cannot prove normal retirement.
                    manager.Dispose();

                    Check(
                        canceledWhileHeld &&
                        callbackCount == 1 &&
                        observedRetired &&
                        observedSerialized,
                        "normal dispose drains pending after-retire actions",
                        ref failures,
                        output);
                }
            }
            catch (Exception ex)
            {
                Check(false,
                    "after-retire normal-dispose test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    ref failures,
                    output);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
        }

        private static void CheckAiAfterRetireDeferredDispose(
            ref int failures,
            TextWriter output)
        {
            RetirementTrackingBackend backend;
            Ai.AiSessionManager manager = CreateRetirementTestManager(out backend);
            SemaphoreSlim operation = GetManagerOperation(manager);
            bool held = false;
            using (var completed = new ManualResetEventSlim(false))
            {
                try
                {
                    operation.Wait();
                    held = true;
                    int callbackCount = 0;
                    bool observedRetired = false;
                    bool observedSerialized = false;
                    using (var cancellation = new CancellationTokenSource())
                    {
                        Task<bool> pending = manager.ReconfigureAsync(
                            null,
                            false,
                            false,
                            cancellation.Token,
                            delegate
                            {
                                Interlocked.Increment(ref callbackCount);
                                observedRetired =
                                    backend.UnloadCalls == 1 &&
                                    backend.DisposeCount == 1;
                                observedSerialized =
                                    operation.CurrentCount == 0;
                                completed.Set();
                            });
                        cancellation.Cancel();
                        bool canceledWhileHeld =
                            pending.Wait(TimeSpan.FromSeconds(1));

                        manager.DisposeForDiagnostics(TimeSpan.Zero);
                        operation.Release();
                        held = false;
                        bool deferredCompleted =
                            completed.Wait(TimeSpan.FromSeconds(3));

                        Check(
                            canceledWhileHeld &&
                            deferredCompleted &&
                            callbackCount == 1 &&
                            observedRetired &&
                            observedSerialized,
                            "deferred dispose drains pending after-retire actions",
                            ref failures,
                            output);
                    }
                }
                catch (Exception ex)
                {
                    Check(false,
                        "after-retire deferred-dispose test threw " +
                        ex.GetType().Name + ": " + ex.Message,
                        ref failures,
                        output);
                }
                finally
                {
                    if (held) operation.Release();
                    manager.Dispose();
                }
            }
        }

        private static Ai.AiSessionManager CreateRetirementTestManager(
            out RetirementTrackingBackend backend)
        {
            var manager = new Ai.AiSessionManager();
            var createdBackend = new RetirementTrackingBackend();
            var settings = new Ai.AiSettings
            {
                TextModel = "retirement-model",
                VisionModel = "retirement-model"
            };
            manager.ReconfigureAsync(
                delegate
                {
                    return new Ai.AiBrain(createdBackend, settings);
                },
                true,
                false,
                CancellationToken.None).GetAwaiter().GetResult();
            backend = createdBackend;
            return manager;
        }

        private static SemaphoreSlim GetManagerOperation(
            Ai.AiSessionManager manager)
        {
            FieldInfo field = typeof(Ai.AiSessionManager).GetField(
                "_operation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(
                    typeof(Ai.AiSessionManager).FullName,
                    "_operation");
            return (SemaphoreSlim)field.GetValue(manager);
        }

        private static void CheckSecureDownloadDeadline(
            ref int failures,
            TextWriter output)
        {
            bool headersTimedOut = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (var handler = new BlockingHeadersHandler())
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/headers"))
                {
                    SecureDownload.DownloadBytesAsync(
                        handler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                headersTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                headersTimedOut && stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds response headers",
                ref failures,
                output);

            bool ignoredHeadersTimedOut = false;
            var ignoredHeadersHandler =
                new CancellationIgnoringHeadersHandler();
            stopwatch.Restart();
            try
            {
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/ignored-headers"))
                {
                    SecureDownload.DownloadBytesAsync(
                        ignoredHeadersHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                ignoredHeadersTimedOut = true;
            }
            stopwatch.Stop();
            ignoredHeadersHandler.CompleteResponse();
            bool lateResponseDisposed = SpinWait.SpinUntil(
                delegate
                {
                    return ignoredHeadersHandler.ResponseContentDisposed;
                },
                TimeSpan.FromSeconds(2));
            ignoredHeadersHandler.Dispose();
            Check(
                ignoredHeadersTimedOut &&
                lateResponseDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline races cancellation-ignoring headers and disposes the late response",
                ref failures,
                output);

            bool streamAcquisitionTimedOut = false;
            var streamHandler = new BlockingReadAsStreamHandler();
            stopwatch.Restart();
            try
            {
                using (streamHandler)
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/stream-acquisition"))
                {
                    SecureDownload.DownloadBytesAsync(
                        streamHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                streamAcquisitionTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                streamAcquisitionTimedOut &&
                streamHandler.ContentDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds cancellation-ignoring response stream acquisition",
                ref failures,
                output);

            bool bodyTimedOut = false;
            var bodyHandler = new BlockingBodyHandler();
            stopwatch.Restart();
            try
            {
                using (bodyHandler)
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.invalid/body"))
                {
                    SecureDownload.DownloadBytesAsync(
                        bodyHandler,
                        request,
                        1024,
                        TimeSpan.FromMilliseconds(150),
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (TimeoutException)
            {
                bodyTimedOut = true;
            }
            stopwatch.Stop();
            Check(
                bodyTimedOut &&
                bodyHandler.StreamDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "catalog deadline bounds cancellation-ignoring response body and disposes it",
                ref failures,
                output);
        }

        private static void CheckRestartLifecycle(
            ref int failures,
            TextWriter output)
        {
            var events = new List<string>();
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { events.Add("lease"); }),
                delegate
                {
                    events.Add("consume");
                    return true;
                },
                delegate { events.Add("launch"); });
            Check(
                events.Count == 3 &&
                events[0] == "lease" &&
                events[1] == "consume" &&
                events[2] == "launch",
                "restart launches only after the instance lease is released",
                ref failures,
                output);

            bool launchedWithoutRequest = false;
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { }),
                delegate { return false; },
                delegate { launchedWithoutRequest = true; });
            Check(
                !launchedWithoutRequest,
                "restart lifecycle does not launch without a request",
                ref failures,
                output);

            bool requestedAfterFailedSave = false;
            bool saveAccepted = Program.TryRequestRestartAfterSave(
                delegate { return false; },
                delegate { requestedAfterFailedSave = true; });
            bool launchedAfterFailedSave = false;
            Program.CompleteInstanceLifecycle(
                new CallbackDisposable(delegate { }),
                delegate { return requestedAfterFailedSave; },
                delegate { launchedAfterFailedSave = true; });
            Check(
                !saveAccepted &&
                !requestedAfterFailedSave &&
                !launchedAfterFailedSave,
                "failed persistence neither requests nor launches a restart",
                ref failures,
                output);
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }

        private static bool IsWellFormedUtf16(string value)
        {
            if (value == null) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index])
                    return false;
            return true;
        }

        private sealed class DiagnosticOwnedValue : IDisposable
        {
            private int disposeCount;

            internal int DisposeCount
            {
                get { return Volatile.Read(ref disposeCount); }
            }

            public void Dispose()
            {
                Interlocked.Increment(ref disposeCount);
            }
        }

        private sealed class BlockingHeadersHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken)
                    .ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class CancellationIgnoringHeadersHandler
            : HttpMessageHandler
        {
            private readonly TaskCompletionSource<HttpResponseMessage> _pending =
                new TaskCompletionSource<HttpResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private DisposalTrackingContent _content;

            public bool ResponseContentDisposed
            {
                get { return _content != null && _content.IsDisposed; }
            }

            public void CompleteResponse()
            {
                _content = new DisposalTrackingContent();
                _pending.TrySetResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = _content
                    });
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                // Deliberately ignore cancellation until the test releases the response.
                return _pending.Task;
            }
        }

        private sealed class DisposalTrackingContent : HttpContent
        {
            public bool IsDisposed { get; private set; }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context)
            {
                return Task.CompletedTask;
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class FirstUnavailableThenBlockingHandler
            : HttpMessageHandler
        {
            private int requestCount;

            public int RequestCount
            {
                get { return Volatile.Read(ref requestCount); }
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                    return new HttpResponseMessage(
                        HttpStatusCode.ServiceUnavailable);

                await Task.Delay(Timeout.Infinite, cancellationToken)
                    .ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class BlockingBodyHandler : HttpMessageHandler
        {
            private BlockingReadStream _stream;

            public bool StreamDisposed
            {
                get { return _stream != null && _stream.IsDisposed; }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _stream = new BlockingReadStream();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(_stream)
                });
            }
        }

        private sealed class BlockingReadAsStreamHandler : HttpMessageHandler
        {
            private BlockingReadAsStreamContent _content;

            public bool ContentDisposed
            {
                get { return _content != null && _content.IsDisposed; }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _content = new BlockingReadAsStreamContent();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = _content
                });
            }
        }

        private sealed class BlockingReadAsStreamContent : HttpContent
        {
            private readonly TaskCompletionSource<Stream> _pending =
                new TaskCompletionSource<Stream>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsDisposed { get; private set; }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                // .NET Framework exposes no cancellation token for this operation.
                return _pending.Task;
            }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context)
            {
                return Task.FromException(
                    new NotSupportedException("Serialization is not used by this test."));
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                if (disposing)
                    _pending.TrySetException(
                        new ObjectDisposedException("BlockingReadAsStreamContent"));
                base.Dispose(disposing);
            }
        }

        private sealed class BlockingReadStream : Stream
        {
            private readonly TaskCompletionSource<int> _pending =
                new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsDisposed { get; private set; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(
                byte[] buffer,
                int offset,
                int count)
            {
                throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                // Deliberately ignore cancellation to reproduce .NET Framework transport streams
                // that leave ReadAsync pending after the supplied token has been canceled.
                return _pending.Task;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                if (disposing)
                    _pending.TrySetException(
                        new ObjectDisposedException("BlockingReadStream"));
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private Action _callback;

            public CallbackDisposable(Action callback)
            {
                _callback = callback;
            }

            public void Dispose()
            {
                Action callback = Interlocked.Exchange(ref _callback, null);
                if (callback != null) callback();
            }
        }

        private sealed class RetirementTrackingBackend : Ai.IPetBrainBackend
        {
            private int unloadCalls;
            private int disposeCount;

            public int UnloadCalls
            {
                get { return Volatile.Read(ref unloadCalls); }
            }

            public int DisposeCount
            {
                get { return Volatile.Read(ref disposeCount); }
            }

            public Task<string> ChatAsync(
                string model,
                IList<Ai.ChatMessage> messages,
                bool jsonFormat,
                CancellationToken cancellationToken)
            {
                return Task.FromResult("");
            }

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task WarmUpAsync(
                string model,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task UnloadAsync(
                string model,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref unloadCalls);
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Interlocked.Increment(ref disposeCount);
            }
        }

        private sealed class CancellationIgnoringBackend : Ai.IPetBrainBackend
        {
            private readonly TaskCompletionSource<bool> never =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<string> ChatAsync(
                string model,
                IList<Ai.ChatMessage> messages,
                bool jsonFormat,
                CancellationToken cancellationToken)
            {
                return Task.FromResult("");
            }

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task WarmUpAsync(
                string model,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task UnloadAsync(
                string model,
                CancellationToken cancellationToken)
            {
                return never.Task;
            }

            public void Dispose()
            {
            }
        }

        private sealed class DeterministicFailureBackend : Ai.IPetBrainBackend
        {
            public int ChatCalls { get; private set; }

            public Task<string> ChatAsync(
                string model,
                IList<Ai.ChatMessage> messages,
                bool jsonFormat,
                CancellationToken cancellationToken)
            {
                ChatCalls++;
                throw new Ai.AiBackendHttpException(302, false);
            }

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task<bool> EnsureServerAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task WarmUpAsync(string model, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task UnloadAsync(string model, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private static void CheckEndpoint(
            string value,
            bool expected,
            ref int failures,
            TextWriter output)
        {
            string normalized;
            string error;
            bool actual = DesktopPet.Ai.AiEndpointPolicy.TryNormalize(
                value, out normalized, out error);
            Check(actual == expected, "endpoint policy: " + value, ref failures, output);
        }

        private static void CheckExpression(
            string expression,
            int expected,
            ref int failures,
            TextWriter output)
        {
            int actual;
            try
            {
                actual = SafeExpression.Evaluate(expression, delegate(string name)
                {
                    if (name == "screenW") return 100.0;
                    if (name == "imageW") return 20.0;
                    return 17.0;
                });
            }
            catch (Exception ex)
            {
                Check(false, "expression " + expression + " threw " + ex.Message,
                    ref failures, output);
                return;
            }
            Check(actual == expected, "expression: " + expression, ref failures, output);
        }

        private static void Check(
            bool condition,
            string name,
            ref int failures,
            TextWriter output)
        {
            if (condition)
            {
                output.WriteLine("[PASS] " + name);
            }
            else
            {
                failures++;
                output.WriteLine("[FAIL] " + name);
            }
        }

        private static string FormatError(string error)
        {
            return string.IsNullOrWhiteSpace(error) ? "" : " (" + error + ")";
        }
    }
}
