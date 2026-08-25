using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using DesktopPet.Tools.ShimejiConvert.Emit;
using DesktopPet.Tools.ShimejiConvert.Shimeji;

namespace DesktopPet.Tools.ShimejiConvert
{
    /// <summary>
    /// The public conversion-engine surface, shared by the ShimejiConvert CLI (tools/ShimejiConvert) and Pet
    /// Studio (modules/PetStudio, source-linked). Both reach the app's REAL validator and the
    /// reachability pass through here, so neither has to duplicate the rules -- the whole point of the
    /// source-linked validator is that a consumer's verdict cannot drift from what the host actually runs.
    ///
    /// PetXmlValidator is internal to this assembly (it is source-linked, not referenced), so callers in
    /// other assemblies cannot reach it directly; these wrappers are the sanctioned way in.
    /// </summary>
    public static class ShimejiEngine
    {
        /// <summary>
        /// Grade a pet XML string with the app's own validator (XSD + semantic limits). This is the oracle
        /// that makes conversion safe: emitted XML is only accepted if the app itself would load it.
        /// </summary>
        public static bool TryValidate(string xml, out XmlData.RootNode root, out string error)
        {
            return PetXmlValidator.TryParse(xml, out root, out error);
        }

        /// <summary>
        /// Reachability/terminal/edge report over the &lt;next&gt; graph. Reports rather than throws:
        /// the interesting output of a conversion is which animations a flattened behaviour tree orphaned.
        /// </summary>
        public static GraphReport Analyze(XmlData.RootNode root)
        {
            return PetGraph.Analyze(root);
        }

        /// <summary>
        /// Serialize a parsed pet back out through its own DTOs and re-validate the result. This is the
        /// emitter's foundation: the converter builds a RootNode and writes it the same way, so anything the
        /// DTOs cannot express faithfully shows up here first, on known-good input.
        /// </summary>
        public static bool RoundTrips(XmlData.RootNode root, out string error)
        {
            error = null;
            try
            {
                string emitted = Serialize(root);
                XmlData.RootNode reparsed;
                if (!PetXmlValidator.TryParse(emitted, out reparsed, out error)) return false;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Serialize a pet DTO graph to an animations.xml string (the same serializer the validator
        /// round-trips through).</summary>
        public static string Serialize(XmlData.RootNode root)
        {
            var serializer = new XmlSerializer(typeof(XmlData.RootNode));
            // A plain StringWriter reports UTF-16 as its Encoding, so XmlSerializer stamps the prolog with
            // encoding="utf-16" even though the string is later written to disk as UTF-8 (no BOM). The app
            // loads pets by parsing decoded text, so that lie is invisible there, but any consumer that reads
            // the file as a byte stream (XDocument.Load, XmlReader) then honours the prolog, finds no UTF-16
            // BOM, and throws. Report UTF-8 so the declared encoding matches how the pet is actually stored --
            // this is why the shipped pets say encoding="utf-8" and converted ones used to say utf-16.
            using (var writer = new Utf8StringWriter())
            {
                serializer.Serialize(writer, root);
                return writer.ToString();
            }
        }

        /// <summary>A StringWriter that reports UTF-8 so the XML prolog matches the on-disk byte encoding.</summary>
        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding
            {
                get { return new UTF8Encoding(false); }
            }
        }

        /// <summary>
        /// Full pipeline: parse a Shimeji conf dir, composite the skin's sprites from its img dir, and emit a
        /// desktopPet pet. Returns null with <paramref name="error"/> set if parsing or compositing fails;
        /// otherwise the result carries the pet, the residue report, and the acceptance verdict.
        /// </summary>
        public static ConversionResult ConvertSkin(string confDir, string imgDir, string skinName, out string error, bool alpha = true)
        {
            error = null;
            bool bundled = string.IsNullOrEmpty(confDir);
            ShimejiConfig config;
            try { config = bundled ? ShimejiParser.ParseBundledConf() : ShimejiParser.ParseConfDirectory(confDir); }
            catch (Exception ex) { error = "parse failed: " + ex.Message; return null; }

            SpriteSheet sheet;
            if (!SpriteSheetBuilder.Build(PetEmitter.PosesToComposite(config), SpriteSheetBuilder.FileLoader(imgDir), alpha, out sheet, out error))
                return null;

            ConversionResult result = PetEmitter.Emit(config, sheet, SpriteSheetBuilder.FileLoader(imgDir), skinName);
            if (bundled && result != null && result.Residue != null)
                result.Residue.Notes.Insert(0, "This skin shipped no behaviour config, so the bundled Shimeji base behaviour was used (Shimeji-EE, BSD-licensed -- see THIRD_PARTY_NOTICES).");
            return result;
        }
    }
}
