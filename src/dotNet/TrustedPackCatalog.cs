using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DesktopPet.Ai;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    internal sealed class TrustedPack
    {
        public string Id;
        public string Name;
        public string Description;
        public string Vibe;
        public string License;
        public string Url;
        public string Sha256;
        public int Count;
        public int Bytes;
        public int DataSchema;
        public bool RedistributionApproved;
    }

    /// <summary>
    /// Loads the build-embedded catalog. The catalog may point only at immutable commit-pinned assets;
    /// entries without documented redistribution approval remain visible as held but cannot download.
    /// </summary>
    internal static class TrustedPackCatalog
    {
        private const string ResourceName = "DesktopPet.PackCatalog.json";
        private const int MaximumCatalogBytes = 256 * 1024;
        private static readonly Regex Sha256Pattern =
            new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RevisionPattern =
            new Regex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

        public static bool TryLoad(out List<TrustedPack> packs, out string error)
        {
            packs = new List<TrustedPack>();
            error = null;
            try
            {
                string json;
                Assembly assembly = typeof(TrustedPackCatalog).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) throw new InvalidDataException("Trusted pack catalog is not embedded.");
                    if (stream.Length > MaximumCatalogBytes)
                        throw new InvalidDataException("Trusted pack catalog exceeds its size limit.");
                    using (var reader = new StreamReader(
                        stream, new UTF8Encoding(false, true), true, 4096, false))
                        json = reader.ReadToEnd();
                }

                JObject root = JObject.Parse(json);
                if ((int?)root["version"] != 2)
                    throw new InvalidDataException("Unsupported trusted pack catalog version.");
                JToken revisionToken = root["revision"];
                if (revisionToken != null &&
                    revisionToken.Type != JTokenType.Null &&
                    revisionToken.Type != JTokenType.String)
                    throw new InvalidDataException(
                        "Trusted pack catalog revision is invalid.");
                string revision = ((string)revisionToken ?? "").Trim();

                JArray entries = root["packs"] as JArray;
                if (entries == null ||
                    entries.Count > FortunePackLoadPolicy.MaximumFiles)
                    throw new InvalidDataException("Trusted pack catalog item count is invalid.");

                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyRedistributionApproved = false;
                foreach (JToken token in entries)
                {
                    var pack = new TrustedPack
                    {
                        Id = ((string)token["id"] ?? "").Trim(),
                        Name = ((string)token["name"] ?? "").Trim(),
                        Description = ((string)token["desc"] ?? "").Trim(),
                        Vibe = ((string)token["vibe"] ?? "clean").Trim(),
                        License = ((string)token["license"] ?? "").Trim(),
                        Url = ((string)token["url"] ?? "").Trim(),
                        Sha256 = ((string)token["sha256"] ?? "").Trim().ToLowerInvariant(),
                        Count = (int?)token["count"] ?? 0,
                        Bytes = (int?)token["bytes"] ?? 0,
                        DataSchema = (int?)token["dataSchema"] ?? 0,
                        RedistributionApproved = (bool?)token["redistributionApproved"] ?? false
                    };

                    if (!SecureDownload.IsSafeId(pack.Id) || !ids.Add(pack.Id) ||
                        string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 128 ||
                        pack.Description.Length > 1024 || pack.License.Length > 256 ||
                        (pack.DataSchema != 1 && pack.DataSchema != 2) ||
                         !Sha256Pattern.IsMatch(pack.Sha256))
                        throw new InvalidDataException("Trusted pack catalog contains an invalid entry.");

                    anyRedistributionApproved |= pack.RedistributionApproved;
                    packs.Add(pack);
                }

                string revisionError;
                if (!TryValidateRevision(
                        anyRedistributionApproved,
                        revision,
                        out revisionError))
                    throw new InvalidDataException(revisionError);

                foreach (TrustedPack pack in packs)
                {
                    string urlError;
                    if (!TryValidateDistributionUrl(
                            pack,
                            revision,
                            out urlError))
                        throw new InvalidDataException(urlError);
                }

                string limitsError;
                if (!TryValidateLoadLimits(packs, out limitsError))
                    throw new InvalidDataException(limitsError);
                return true;
            }
            catch (Exception ex)
            {
                packs.Clear();
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryValidateRevision(
            bool anyRedistributionApproved,
            string revision,
            out string error)
        {
            error = null;
            revision = (revision ?? "").Trim();
            if (!anyRedistributionApproved)
            {
                if (revision.Length == 0) return true;
                error =
                    "An all-held trusted pack catalog must not declare a revision.";
                return false;
            }

            if (RevisionPattern.IsMatch(revision)) return true;
            error =
                "A trusted pack catalog with redistribution-approved entries " +
                "requires a lowercase 40-hex revision.";
            return false;
        }

        internal static bool TryValidateDistributionUrl(
            TrustedPack pack,
            string revision,
            out string error)
        {
            error = null;
            if (pack == null)
            {
                error = "Trusted pack catalog contains a null entry.";
                return false;
            }

            if (!pack.RedistributionApproved)
            {
                if (string.IsNullOrWhiteSpace(pack.Url)) return true;
                error =
                    "A held trusted pack must not expose a download URL.";
                return false;
            }

            revision = (revision ?? "").Trim();
            if (!RevisionPattern.IsMatch(revision))
            {
                error =
                    "A redistribution-approved trusted pack requires a " +
                    "lowercase 40-hex catalog revision.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(pack.Url))
            {
                error =
                    "A redistribution-approved trusted pack requires a download URL.";
                return false;
            }

            Uri uri;
            string uriError;
            if (!SecureDownload.TryValidatePinnedRawGitHubUrl(
                    pack.Url,
                    "bigfnj",
                    "desktopPet",
                    out uri,
                    out uriError))
            {
                error = uriError;
                return false;
            }

            string[] path = uri.AbsolutePath.Trim('/').Split('/');
            if (path.Length < 4 ||
                !string.Equals(
                    path[2],
                    revision,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    path[path.Length - 1],
                    pack.Id + ".txt",
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Trusted pack URL does not match its id or catalog revision.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates only the shared runtime-loadability bounds. Kept separate from signature and
        /// URL checks so exact boundary and aggregate behavior can be tested deterministically.
        /// </summary>
        internal static bool TryValidateLoadLimits(
            IList<TrustedPack> packs,
            out string error)
        {
            error = null;
            if (packs == null ||
                packs.Count > FortunePackLoadPolicy.MaximumFiles)
            {
                error = "Trusted pack catalog item count exceeds the runtime file limit.";
                return false;
            }

            int approvedFiles = 0;
            long approvedBytes = 0;
            long approvedEntries = 0;
            foreach (TrustedPack pack in packs)
            {
                if (pack == null)
                {
                    error = "Trusted pack catalog contains a null entry.";
                    return false;
                }

                string packError;
                if (!FortunePackLoadPolicy.TryValidatePackMetadata(
                        pack.Bytes, pack.Count, out packError))
                {
                    error = "Trusted pack '" + (pack.Id ?? "") + "' " + packError + ".";
                    return false;
                }

                if (!pack.RedistributionApproved) continue;
                approvedFiles++;
                approvedBytes += pack.Bytes;
                approvedEntries += pack.Count;
            }

            string aggregateError;
            if (!FortunePackLoadPolicy.TryValidateApprovedAggregate(
                    approvedFiles,
                    approvedBytes,
                    approvedEntries,
                    out aggregateError))
            {
                error = "Trusted pack catalog " + aggregateError + ".";
                return false;
            }
            return true;
        }
    }
}
