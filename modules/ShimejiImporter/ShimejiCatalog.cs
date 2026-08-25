using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace DesktopPet.ShimejiImporterModule
{
    /// <summary>One curated catalog entry: a ready-to-install pet plus its attribution.</summary>
    internal sealed class CatalogEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string SourceUrl { get; set; }
        public bool AiGenerated { get; set; }
        public string Pet { get; set; }          // the embedded pet xml resource file name (catalog/<Pet>)
    }

    /// <summary>
    /// Reads the module's embedded catalog: a manifest (catalog/catalog.json) listing entries, and each
    /// entry's pre-converted pet animations.xml (catalog/&lt;Pet&gt;). No network, no converter -- the store
    /// ships ready pets we have permission to redistribute; the host validates on install.
    /// </summary>
    internal static class ShimejiCatalog
    {
        private const string ManifestResource = "catalog/catalog.json";

        private sealed class Manifest
        {
            public int SchemaVersion { get; set; }
            public string Source { get; set; }
            public int Count { get; set; }
            public List<CatalogEntry> Entries { get; set; }
        }

        public static List<CatalogEntry> LoadEntries()
        {
            string json = ReadResourceText(ManifestResource);
            if (string.IsNullOrEmpty(json)) return new List<CatalogEntry>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Manifest m = JsonSerializer.Deserialize<Manifest>(json, options);
            return m != null && m.Entries != null ? m.Entries : new List<CatalogEntry>();
        }

        /// <summary>The pre-converted pet animations.xml for an entry, or null if missing.</summary>
        public static string ReadPetXml(CatalogEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Pet)) return null;
            return ReadResourceText("catalog/" + entry.Pet);
        }

        private static string ReadResourceText(string logicalName)
        {
            Assembly asm = typeof(ShimejiCatalog).Assembly;
            using (Stream s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s))
                    return r.ReadToEnd();
            }
        }
    }
}
