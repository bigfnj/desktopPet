using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopPet
{
    /// <summary>
    /// Shared pet enumeration, naming, and on-disk XML resolution, used by both the Options gallery
    /// (FormOptions) and the tray menu (ContextMenus) plus the loaded-pet-type registry, so pet ids,
    /// display names, and xml lookup live in one place. A pet "type" is a folder id under a pets root
    /// (AppPaths.BundledPetsDirectory beside the exe, then AppPaths.LibraryPetsDirectory for downloads),
    /// each folder holding an animations.xml. The built-in default (eSheep) has a null id.
    /// </summary>
    internal static class PetCatalog
    {
        internal sealed class PetInfo
        {
            public string Id;          // folder/catalog id; null for the built-in default
            public string DisplayName;
            public string XmlPath;     // null for the built-in default
            public bool IsBuiltIn;
        }

        internal const int MaximumPetXmlBytes = 12 * 1024 * 1024;   // matches AppSettingsDocument.MaximumXmlBytes

        // Explicit id for the built-in default pet (the embedded eSheep). Distinct from "" which means
        // "whatever pet is currently active" — a card/tray "Add" must add the specific pet it names,
        // not the active one, so those sites pass this id for the built-in.
        internal const string BuiltInPetId = "eSheep";

        // The colored-sheep pets ship as "<colour>_sheep" but each has its own character name in its
        // animations.xml. The thumbnail already shows the colour, so we show the name instead of a
        // redundant "Pink Sheep". Keyed by catalog/folder id.
        private static readonly Dictionary<string, string> CharacterNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "blue_sheep",   "Ben"    },
                { "green_sheep",  "Gus"    },
                { "orange_sheep", "Omar"   },
                { "pink_sheep",   "Pearl"  },
                { "purple_sheep", "Patsu"  },
                { "red_sheep",    "Rick"   },
                { "yellow_sheep", "Yogurt" },
            };

        /// <summary>
        /// Preferred label for a pet: a curated character name when we have one, then any name the
        /// catalog supplied, then a title-cased folder id. Used by the local list, the online download
        /// grid, and the tray so a pet reads the same everywhere.
        /// </summary>
        internal static string DisplayName(string folder, string catalogName)
        {
            string mapped;
            if (!string.IsNullOrWhiteSpace(folder) &&
                CharacterNames.TryGetValue(folder.Trim(), out mapped))
                return mapped;
            if (!string.IsNullOrWhiteSpace(catalogName))
                return catalogName.Trim();
            return PrettyName(folder);
        }

        internal static string PrettyName(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Pet";
            string spaced = folder.Replace('_', ' ').Replace('-', ' ');
            string[] words = spaced.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (string word in words)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1) builder.Append(word.Substring(1));
            }
            return builder.Length > 0 ? builder.ToString() : "Pet";
        }

        /// <summary>
        /// The pet's own display name from the START of its animations.xml. The header (with petname/title)
        /// always precedes the multi-MB base64 sprite sheet, so a bounded read is enough and we never load the
        /// whole file just to label a card. Prefers &lt;petname&gt;, then &lt;title&gt; minus a trailing
        /// " (converted)"; returns null when neither is present (caller falls back to the folder id).
        /// </summary>
        private static string ReadHeaderName(string xmlPath)
        {
            try
            {
                var buf = new char[32 * 1024];
                int read;
                using (var reader = new StreamReader(xmlPath, Encoding.UTF8, true))
                    read = reader.ReadBlock(buf, 0, buf.Length);
                string head = new string(buf, 0, Math.Max(0, read));
                string name = Between(head, "<petname>", "</petname>");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Between(head, "<title>", "</title>");
                    const string suffix = " (converted)";
                    if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(suffix, StringComparison.Ordinal))
                        name = name.Substring(0, name.Length - suffix.Length);
                }
                if (string.IsNullOrWhiteSpace(name)) return null;
                return DecodeEntities(name.Trim());
            }
            catch { return null; }
        }

        private static string Between(string s, string open, string close)
        {
            int i = s.IndexOf(open, StringComparison.Ordinal);
            if (i < 0) return null;
            i += open.Length;
            int j = s.IndexOf(close, i, StringComparison.Ordinal);
            return j < 0 ? null : s.Substring(i, j - i);
        }

        // The five predefined XML entities a serialized name can carry (&amp; decoded last so "&amp;lt;"
        // does not collapse to "<").
        private static string DecodeEntities(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                    .Replace("&apos;", "'").Replace("&amp;", "&");
        }

        /// <summary>
        /// The built-in default plus every safe pet folder under the bundled (beside-exe) and library
        /// (downloaded) roots. The built-in is first, with a null id. Mirrors the gallery's listing so
        /// the tray offers exactly the pets the user can see.
        /// </summary>
        internal static List<PetInfo> EnumerateLocal()
        {
            var list = new List<PetInfo>
            {
                new PetInfo { Id = null, DisplayName = "eSheep (default)", IsBuiltIn = true }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFrom(AppPaths.BundledPetsDirectory, list, seen);   // read-only, beside the exe
            AddFrom(AppPaths.LibraryPetsDirectory, list, seen);   // writable, downloaded pets
            return list;
        }

        private static void AddFrom(string root, List<PetInfo> list, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            List<string> directories;
            try { directories = new List<string>(Directory.EnumerateDirectories(root)); }
            catch { return; }
            directories.Sort(StringComparer.OrdinalIgnoreCase);

            const int maxPets = 256;
            foreach (string directory in directories)
            {
                if (list.Count > maxPets) break;
                string folder = Path.GetFileName(directory);
                if (!SecureDownload.IsSafeId(folder) || !seen.Add(folder)) continue;
                string xmlPath = Path.Combine(directory, "animations.xml");
                if (!File.Exists(xmlPath)) continue;
                list.Add(new PetInfo
                {
                    Id = folder,
                    // Prefer the pet's own name from its animations.xml header (so a converted shimeji reads
                    // "Bugcat Capoo", not the prettified folder id "Shimeji <id>"); fall back to the folder.
                    DisplayName = DisplayName(folder, ReadHeaderName(xmlPath)),
                    XmlPath = xmlPath,
                    IsBuiltIn = false,
                });
            }
        }

        /// <summary>
        /// Resolve a pet id to its raw animations.xml text. The built-in default (null/empty/"eSheep")
        /// returns the embedded default; a folder id is read (BOM-stripped by File.ReadAllText, size-
        /// bounded) from the library root first, then the bundled root. The text is validated by the
        /// caller (StartUp.TryStageRuntime) before use.
        /// </summary>
        internal static bool TryReadPetXml(string id, out string xml, out string error)
        {
            xml = null;
            error = null;
            if (string.IsNullOrEmpty(id) ||
                string.Equals(id, BuiltInPetId, StringComparison.OrdinalIgnoreCase))
            {
                xml = Properties.Resources.animations;
                return true;
            }
            if (!SecureDownload.IsSafeId(id))
            {
                error = "Unsafe pet id.";
                return false;
            }
            foreach (string root in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, id, "animations.xml");
                if (!File.Exists(path)) continue;
                try
                {
                    if (new FileInfo(path).Length > MaximumPetXmlBytes)
                    {
                        error = "Pet file too large.";
                        return false;
                    }
                    xml = File.ReadAllText(path);   // File.ReadAllText strips a leading UTF-8 BOM
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
            error = "Pet '" + id + "' was not found.";
            return false;
        }
    }
}
