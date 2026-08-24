using System;
using System.IO;
using System.Xml.Serialization;

namespace DesktopPet.Tools.ShimejiConvert
{
    /// <summary>
    /// The public conversion-engine surface, shared by the ShimejiConvert CLI (tools/ShimejiConvert) and the
    /// Shimeji Importer module (modules/ShimejiImporter). Both reach the app's REAL validator and the
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
                var serializer = new XmlSerializer(typeof(XmlData.RootNode));
                string emitted;
                using (var writer = new StringWriter())
                {
                    serializer.Serialize(writer, root);
                    emitted = writer.ToString();
                }

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
    }
}
