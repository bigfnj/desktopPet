using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.Ai;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    internal sealed class CatalogPet
    {
        public string Id;
        public string Name;
        public string Author;
        public string Url;
        public string Sha256;
        public int Bytes;
    }

    internal sealed class CatalogPack
    {
        public string Id;
        public string Name;
        public string Description;
        public string License;
        public string Url;
        public string Sha256;
        public int Bytes;
        public int Count;
        public int DataSchema;
    }

    internal sealed class RemoteCatalog
    {
        public readonly List<CatalogPet> Pets = new List<CatalogPet>();
        public readonly List<CatalogPack> Packs = new List<CatalogPack>();
    }

    /// <summary>
    /// Runtime-fetched content catalog. HTTPS-trusted: the catalog itself is fetched over TLS from the
    /// project repo, and every asset it lists is downloaded and SHA-256-verified against the catalog
    /// before install (pets also pass <see cref="PetXmlValidator"/>; packs pass the fortune importer).
    /// Content added to the repo appears live with no new build. The bundled/offline content is the
    /// fallback; this only reveals what is not already present locally.
    /// </summary>
    internal static class RemoteCatalogClient
    {
        internal const string Owner = "bigfnj";
        internal const string Repository = "desktopPet";

        // Branch-pinned so content published to the repo is visible without shipping a new app build.
        internal const string CatalogUrl =
            "https://raw.githubusercontent.com/bigfnj/desktopPet/main/catalog.json";

        private const int MaximumCatalogBytes = 512 * 1024;
        private const int MaximumEntries = 512;

        public static async Task<RemoteCatalog> FetchAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    CatalogUrl, Owner, Repository, out uri, out urlError))
                throw new InvalidDataException("Catalog URL is invalid: " + urlError);

            byte[] bytes = await SecureDownload.DownloadBytesAsync(
                uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
            return Parse(SecureDownload.DecodeUtf8(bytes));
        }

        /// <summary>Download one catalog asset and verify it against the catalog's SHA-256.</summary>
        public static async Task<byte[]> DownloadVerifiedAsync(
            string url,
            string sha256,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Uri uri;
            string urlError;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out urlError))
                throw new InvalidDataException("Asset URL is invalid: " + urlError);

            byte[] bytes = await SecureDownload.DownloadBytesAsync(
                uri, maximumBytes, cancellationToken).ConfigureAwait(false);
            SecureDownload.RequireSha256(bytes, sha256);
            return bytes;
        }

        internal static RemoteCatalog Parse(string json)
        {
            var catalog = new RemoteCatalog();
            JObject root = JObject.Parse(json);
            if ((int?)root["version"] != 1)
                throw new InvalidDataException("Unsupported catalog version.");

            var pets = root["pets"] as JArray;
            var packs = root["packs"] as JArray;
            if ((pets != null && pets.Count > MaximumEntries) ||
                (packs != null && packs.Count > MaximumEntries))
                throw new InvalidDataException("Catalog item count is invalid.");

            var petIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pets != null)
                foreach (JToken token in pets)
                {
                    var pet = new CatalogPet
                    {
                        Id = ((string)token["id"] ?? "").Trim(),
                        Name = ((string)token["name"] ?? "").Trim(),
                        Author = ((string)token["author"] ?? "").Trim(),
                        Url = ((string)token["url"] ?? "").Trim(),
                        Sha256 = ((string)token["sha256"] ?? "").Trim().ToLowerInvariant(),
                        Bytes = (int?)token["bytes"] ?? 0
                    };
                    if (!SecureDownload.IsSafeId(pet.Id) || !petIds.Add(pet.Id) ||
                        string.IsNullOrWhiteSpace(pet.Name) || pet.Name.Length > 128 ||
                        pet.Author.Length > 128 ||
                        pet.Bytes < 1 || pet.Bytes > PetXmlValidator.MaximumXmlBytes ||
                        !IsSha256(pet.Sha256) ||
                        !IsPetAssetUrl(pet.Url, pet.Id))
                        throw new InvalidDataException("Catalog contains an invalid pet entry.");
                    catalog.Pets.Add(pet);
                }

            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packs != null)
                foreach (JToken token in packs)
                {
                    var pack = new CatalogPack
                    {
                        Id = ((string)token["id"] ?? "").Trim(),
                        Name = ((string)token["name"] ?? "").Trim(),
                        Description = ((string)token["desc"] ?? "").Trim(),
                        License = ((string)token["license"] ?? "").Trim(),
                        Url = ((string)token["url"] ?? "").Trim(),
                        Sha256 = ((string)token["sha256"] ?? "").Trim().ToLowerInvariant(),
                        Bytes = (int?)token["bytes"] ?? 0,
                        Count = (int?)token["count"] ?? 0,
                        DataSchema = (int?)token["dataSchema"] ?? 0
                    };
                    if (!SecureDownload.IsSafeId(pack.Id) || !packIds.Add(pack.Id) ||
                        string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 128 ||
                        pack.Description.Length > 1024 || pack.License.Length > 256 ||
                        (pack.DataSchema != 1 && pack.DataSchema != 2) ||
                        !IsSha256(pack.Sha256) ||
                        !IsPackAssetUrl(pack.Url, pack.Id))
                        throw new InvalidDataException("Catalog contains an invalid pack entry.");
                    // Reuse the same runtime-loadability bounds the embedded catalog enforces.
                    string packError;
                    if (!FortunePackLoadPolicy.TryValidatePackMetadata(
                            pack.Bytes, pack.Count, out packError))
                        throw new InvalidDataException(
                            "Catalog pack '" + pack.Id + "' " + packError + ".");
                    catalog.Packs.Add(pack);
                }

            return catalog;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool IsPetAssetUrl(string url, string id)
        {
            Uri uri;
            string error;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out error))
                return false;
            string[] p = uri.AbsolutePath.Trim('/').Split('/');
            // owner / repo / <ref> / Pets / <id> / animations.xml
            return p.Length >= 6 &&
                string.Equals(p[p.Length - 3], "Pets", StringComparison.Ordinal) &&
                string.Equals(p[p.Length - 2], id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p[p.Length - 1], "animations.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPackAssetUrl(string url, string id)
        {
            Uri uri;
            string error;
            if (!SecureDownload.TryValidateBranchRawGitHubUrl(
                    url, Owner, Repository, out uri, out error))
                return false;
            string[] p = uri.AbsolutePath.Trim('/').Split('/');
            // owner / repo / <ref> / packs / <id>.txt
            return p.Length >= 5 &&
                string.Equals(p[p.Length - 2], "packs", StringComparison.Ordinal) &&
                string.Equals(p[p.Length - 1], id + ".txt", StringComparison.OrdinalIgnoreCase);
        }

        // ---- diagnostics ----------------------------------------------------

        private const string PetUrlBase =
            "https://raw.githubusercontent.com/bigfnj/desktopPet/main/Pets/";
        private const string PackUrlBase =
            "https://raw.githubusercontent.com/bigfnj/desktopPet/main/packs/";
        private static readonly string SampleSha =
            new string('a', 64);

        internal static bool SelfTest()
        {
            var report = new StringBuilder();
            bool ok = true;

            string validJson =
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                "\"author\": \"Michelle\", \"url\": \"" + PetUrlBase +
                "fox/animations.xml\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 33556 } ], " +
                "\"packs\": [ { \"id\": \"tech\", \"name\": \"Tech\", \"desc\": \"quips\", " +
                "\"license\": \"LicenseRef-DesktopPet-Community\", \"url\": \"" + PackUrlBase +
                "tech.txt\", \"sha256\": \"" + SampleSha + "\", \"bytes\": 308767, " +
                "\"count\": 620, \"dataSchema\": 2 } ] }";
            try
            {
                RemoteCatalog catalog = Parse(validJson);
                if (catalog.Pets.Count != 1 || catalog.Packs.Count != 1)
                {
                    ok = false;
                    report.AppendLine("CATALOG FAIL valid catalog produced wrong counts");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("CATALOG FAIL valid catalog rejected: " + ex.Message);
            }

            // Each of these must be rejected.
            var rejects = new[]
            {
                "{ \"version\": 2, \"pets\": [], \"packs\": [] }",                       // bad version
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"" + PetUrlBase + "fox/animations.xml\", " +
                    "\"sha256\": \"notahash\", \"bytes\": 33556 } ], \"packs\": [] }",   // bad sha
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"https://evil.example.com/x/animations.xml\", " +
                    "\"sha256\": \"" + SampleSha + "\", \"bytes\": 10 } ], \"packs\": [] }",   // bad host
                "{ \"version\": 1, \"pets\": [ { \"id\": \"fox\", \"name\": \"Fox\", " +
                    "\"url\": \"" + PetUrlBase + "notfox/animations.xml\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10 } ], \"packs\": [] }",                // id/path mismatch
                "{ \"version\": 1, \"pets\": [], \"packs\": [ { \"id\": \"../etc\", " +
                    "\"name\": \"x\", \"url\": \"" + PackUrlBase + "x.txt\", \"sha256\": \"" +
                    SampleSha + "\", \"bytes\": 10, \"count\": 1, \"dataSchema\": 2 } ] }"  // unsafe id
            };
            for (int i = 0; i < rejects.Length; i++)
            {
                bool rejected = false;
                try { Parse(rejects[i]); }
                catch (InvalidDataException) { rejected = true; }
                catch (Newtonsoft.Json.JsonException) { rejected = true; }
                if (!rejected)
                {
                    ok = false;
                    report.AppendLine("CATALOG FAIL reject-case " + i + " was accepted");
                }
            }

            try
            {
                string path = Path.Combine(Path.GetTempPath(), "dp-catalog-selftest.txt");
                report.AppendLine("catalog_parse=" + (ok ? "PASS" : "FAIL"));
                File.WriteAllText(path, report.ToString());
            }
            catch { }
            return ok;
        }
    }
}
