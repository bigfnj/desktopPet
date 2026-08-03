using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    internal sealed class PackCollection
    {
        public string Id;
        public string Name;
        public string Description;
        public string Vibe;
        public string License;
        public readonly List<string> Sources = new List<string>();
    }

    /// <summary>
    /// The embedded collection map (packs/collections.json): which per-source fortune packs belong to
    /// which named collection. Groups the Sources tree and the fortune-packs download tree, and is
    /// available offline (embedded) even before the runtime catalog is fetched. Parsed once and cached.
    /// </summary>
    internal static class PackCollections
    {
        private const string ResourceName = "DesktopPet.Collections.json";
        private const int MaximumBytes = 256 * 1024;

        private static readonly object Lock = new object();
        private static List<PackCollection> _collections;
        private static Dictionary<string, string> _sourceToName;

        public static IList<PackCollection> All()
        {
            EnsureLoaded();
            return _collections;
        }

        /// <summary>The collection name for a source id, or "" when the source is in no collection.</summary>
        public static string CollectionName(string sourceId)
        {
            EnsureLoaded();
            string name;
            return sourceId != null && _sourceToName.TryGetValue(sourceId, out name) ? name : "";
        }

        private static void EnsureLoaded()
        {
            if (_collections != null) return;
            lock (Lock)
            {
                if (_collections != null) return;
                var collections = new List<PackCollection>();
                var sourceToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string json = ReadEmbedded();
                    if (json != null)
                    {
                        JObject root = JObject.Parse(json);
                        var array = root["collections"] as JArray;
                        if (array != null)
                            foreach (JToken token in array)
                            {
                                var collection = new PackCollection
                                {
                                    Id = ((string)token["id"] ?? "").Trim(),
                                    Name = ((string)token["name"] ?? "").Trim(),
                                    Description = ((string)token["desc"] ?? "").Trim(),
                                    Vibe = ((string)token["vibe"] ?? "").Trim(),
                                    License = ((string)token["license"] ?? "").Trim(),
                                };
                                if (collection.Name.Length == 0) collection.Name = collection.Id;
                                var sources = token["sources"] as JArray;
                                if (sources != null)
                                    foreach (JToken entry in sources)
                                    {
                                        string source = ((string)entry ?? "").Trim();
                                        if (source.Length == 0) continue;
                                        collection.Sources.Add(source);
                                        if (!sourceToName.ContainsKey(source))
                                            sourceToName[source] = collection.Name;
                                    }
                                collections.Add(collection);
                            }
                    }
                }
                catch
                {
                    collections.Clear();
                    sourceToName.Clear();
                }
                _collections = collections;
                _sourceToName = sourceToName;
            }
        }

        private static string ReadEmbedded()
        {
            Assembly assembly = typeof(PackCollections).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null || stream.Length > MaximumBytes) return null;
                using (var reader = new StreamReader(
                    stream, new UTF8Encoding(false, true), true, 4096, false))
                    return reader.ReadToEnd();
            }
        }
    }
}
