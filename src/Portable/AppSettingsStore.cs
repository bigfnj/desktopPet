using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    /// <summary>Versioned persisted settings owned by <see cref="LocalData"/>.</summary>
    internal sealed class AppSettingsDocument
    {
        public const int CurrentSchemaVersion = 2;
        public const int MaximumAutoStartPets = 16;
        public const int MaximumOnScreenPets = 16;   // total pets across all types (matches MAX_SHEEPS)
        public const int MaximumPetIdLength = 128;
        public const int MaximumPetSizeEntries = 256;   // bounds the per-pet size-override list
        public const int MaximumXmlBytes = 4 * 1024 * 1024;
        public const int MaximumLegacyImageCharacters = 6 * 1024 * 1024;
        public const int MaximumLegacyIconCharacters = 1024 * 1024;

        [JsonProperty("schemaVersion", Order = 1)]
        public int SchemaVersion;

        [JsonProperty("volume", Order = 2)]
        public double Volume;

        [JsonProperty("scaleLevel", Order = 3)]
        public int ScaleLevel;

        [JsonProperty("autoStartPets", Order = 4)]
        public int AutoStartPets;

        [JsonProperty("multiScreen", Order = 5)]
        public bool MultiScreen;

        [JsonProperty("windowForeground", Order = 6)]
        public bool WindowForeground;

        [JsonProperty("stealTaskbarFocus", Order = 7)]
        public bool StealTaskbarFocus;

        [JsonProperty("speechEnabled", Order = 8)]
        public bool SpeechEnabled;

        [JsonProperty("speechDurationSeconds", Order = 9)]
        public int SpeechDurationSeconds;

        [JsonProperty("xml", Order = 10)]
        public string Xml;

        [JsonProperty("images", Order = 11)]
        public string Images;

        [JsonProperty("icon", Order = 12)]
        public string Icon;

        // The on-screen pet mix: how many pets of each type to spawn/restore. id "" = the active/
        // default pet (the one described by Xml above); other ids are pet folder ids. Introduced in
        // schema v2; migrated from the single AutoStartPets count for older docs (see Normalize).
        [JsonProperty("pets", Order = 13)]
        public List<PetCountEntry> Pets;

        // Per-pet size overrides: pet id -> scale level (1/2/3). Absent = follow the global ScaleLevel;
        // id "" is the active/default pet. Optional (older docs carry none). Sits alongside the pet mix.
        [JsonProperty("petSizes", Order = 14)]
        public List<PetSizeEntry> PetSizes;

        // UI theme for the settings window: "system" (follow the OS), "light", or "dark". Optional
        // (older docs default to "system" on load).
        [JsonProperty("themeMode", Order = 15)]
        public string ThemeMode;

        // Audio output device GUID (DirectSound) for host-owned playback; "" = the default device. Optional.
        [JsonProperty("audioDeviceId", Order = 16)]
        public string AudioDeviceId;

        // Pet type ids whose animation sounds are muted (per-pet sound toggle, B3). Absent from this list =
        // sound on (the default). id "" is the active/default pet. Optional (older docs mute nothing).
        [JsonProperty("mutedPets", Order = 17)]
        public List<string> MutedPets;

        // The real id of the active/default pet, so per-pet settings (size/sound) key by the actual pet
        // rather than the "" active-slot placeholder. Default = the built-in eSheep. Set when the user picks
        // a pet ("Use"/restore). Optional (older docs default to the built-in).
        [JsonProperty("activePetId", Order = 18)]
        public string ActivePetId;

        // Keep in sync with PetCatalog.BuiltInPetId (which AppSettingsStore can't reference — it compiles
        // into the SecureDownload-free CoreTests set).
        internal const string DefaultActivePetId = "eSheep";

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData =
            new Dictionary<string, JToken>(StringComparer.Ordinal);

        public static AppSettingsDocument CreateDefault()
        {
            return new AppSettingsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Volume = 0.3,
                ScaleLevel = 1,
                AutoStartPets = 1,
                MultiScreen = false,
                WindowForeground = false,
                StealTaskbarFocus = false,
                SpeechEnabled = true,
                SpeechDurationSeconds = 6,
                Xml = "",
                Images = "",
                Icon = "",
                Pets = new List<PetCountEntry>(),
                PetSizes = new List<PetSizeEntry>(),
                ThemeMode = "system",
                AudioDeviceId = "",
                MutedPets = new List<string>(),
                ActivePetId = DefaultActivePetId
            };
        }

        /// <summary>Upgrade known older schemas and clamp every externally persisted range.</summary>
        public bool Normalize()
        {
            bool changed = false;
            int originalSchema = SchemaVersion;

            if (SchemaVersion < CurrentSchemaVersion)   // covers legacy v0 and v1 docs
            {
                SchemaVersion = CurrentSchemaVersion;
                changed = true;
            }

            if (double.IsNaN(Volume) || double.IsInfinity(Volume))
            {
                Volume = 0.3;
                changed = true;
            }
            double volume = Math.Max(0.0, Math.Min(1.0, Volume));
            if (Math.Abs(volume - Volume) > double.Epsilon)
            {
                Volume = volume;
                changed = true;
            }
            int scale = ScalePolicy.ClampLevel(ScaleLevel);
            if (scale != ScaleLevel) { ScaleLevel = scale; changed = true; }
            int pets = Math.Max(1, Math.Min(MaximumAutoStartPets, AutoStartPets));
            if (pets != AutoStartPets) { AutoStartPets = pets; changed = true; }
            int speech = Math.Max(2, Math.Min(30, SpeechDurationSeconds));
            if (speech != SpeechDurationSeconds)
            {
                SpeechDurationSeconds = speech;
                changed = true;
            }
            changed |= NormalizePayload(ref Xml, MaximumXmlBytes, MaximumXmlBytes);
            changed |= NormalizePayload(
                ref Images,
                MaximumLegacyImageCharacters,
                MaximumLegacyImageCharacters);
            changed |= NormalizePayload(
                ref Icon,
                MaximumLegacyIconCharacters,
                MaximumLegacyIconCharacters);

            // Upgrading a pre-v2 doc: seed the on-screen pet mix from the old single count so the first
            // launch after upgrade restores the same number of pets (id "" = the active/default pet).
            // AutoStartPets is already clamped above.
            if (originalSchema < 2 && (Pets == null || Pets.Count == 0))
            {
                Pets = new List<PetCountEntry> { new PetCountEntry { Id = "", Count = AutoStartPets } };
                changed = true;
            }
            changed |= NormalizePetMix();
            changed |= NormalizePetSizes();
            string theme = NormalizeThemeMode(ThemeMode);
            if (!string.Equals(theme, ThemeMode, StringComparison.Ordinal)) { ThemeMode = theme; changed = true; }
            string device = NormalizeAudioDeviceId(AudioDeviceId);
            if (!string.Equals(device, AudioDeviceId, StringComparison.Ordinal)) { AudioDeviceId = device; changed = true; }
            changed |= NormalizeMutedPets();
            string active = NormalizeActivePetId(ActivePetId);
            if (!string.Equals(active, ActivePetId, StringComparison.Ordinal)) { ActivePetId = active; changed = true; }
            return changed;
        }

        // The active pet id must name a real pet, so an empty/unsafe value falls back to the built-in
        // (unlike the "" that means "the active slot" in the pet mix / muted list).
        internal static string NormalizeActivePetId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return DefaultActivePetId;
            id = id.Trim();
            if (id.Length > MaximumPetIdLength) return DefaultActivePetId;
            foreach (char c in id)
                if (c == '/' || c == '\\' || c == ':' || char.IsControl(c)) return DefaultActivePetId;
            return id;
        }

        // Validate the muted-pets list: drop null/unsafe ids, dedupe (case-insensitive), cap the count.
        private bool NormalizeMutedPets()
        {
            List<string> original = MutedPets;
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (MutedPets != null)
                foreach (string raw in MutedPets)
                {
                    string id = raw ?? "";
                    if (!IsAcceptablePetId(id) || !seen.Add(id)) continue;
                    if (result.Count >= MaximumPetSizeEntries) break;
                    result.Add(id);
                }
            MutedPets = result;
            if (original == null) return true;
            if (original.Count != result.Count) return true;
            for (int i = 0; i < result.Count; i++)
                if (!string.Equals(original[i] ?? "", result[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Normalize the audio device id: null/whitespace/over-long -> "" (the default device).</summary>
        internal static string NormalizeAudioDeviceId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            id = id.Trim();
            return id.Length > 128 ? "" : id;
        }

        /// <summary>Clamp the settings-window theme to one of "system" / "light" / "dark" (default system).</summary>
        internal static string NormalizeThemeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "system";
            switch (mode.Trim().ToLowerInvariant())
            {
                case "light": return "light";
                case "dark": return "dark";
                default: return "system";
            }
        }

        // Validate the persisted pet mix on every load: drop null/unsafe-id entries, clamp each count
        // to [1, MaximumOnScreenPets], dedupe by id (summing counts), then cap the running total across
        // all types to MaximumOnScreenPets. Deliberately does NOT call SecureDownload.IsSafeId (the core
        // regression test project does not compile it); the runtime load path applies the full safe-id
        // check where files are actually opened. id "" (the active/default pet) is allowed.
        private bool NormalizePetMix()
        {
            List<PetCountEntry> original = Pets;
            var merged = new List<PetCountEntry>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (Pets != null)
            {
                foreach (PetCountEntry entry in Pets)
                {
                    if (entry == null) continue;
                    string id = entry.Id ?? "";
                    if (!IsAcceptablePetId(id)) continue;
                    int count = Math.Max(1, Math.Min(MaximumOnScreenPets, entry.Count));
                    int existing;
                    if (indexById.TryGetValue(id, out existing))
                        merged[existing].Count = Math.Min(
                            MaximumOnScreenPets,
                            merged[existing].Count + count);
                    else
                    {
                        indexById[id] = merged.Count;
                        merged.Add(new PetCountEntry { Id = id, Count = count });
                    }
                }
            }

            var result = new List<PetCountEntry>();
            int total = 0;
            foreach (PetCountEntry entry in merged)
            {
                if (total >= MaximumOnScreenPets) break;
                int count = Math.Min(entry.Count, MaximumOnScreenPets - total);
                result.Add(new PetCountEntry { Id = entry.Id, Count = count });
                total += count;
            }

            Pets = result;
            return !PetMixEquals(original, result);
        }

        private static bool IsAcceptablePetId(string id)
        {
            if (id == null) return false;
            if (id.Length == 0) return true;                 // the active/default pet
            if (id.Length > MaximumPetIdLength) return false;
            foreach (char c in id)
                if (c == '/' || c == '\\' || c == ':' || char.IsControl(c)) return false;
            return true;
        }

        internal static bool PetMixEquals(List<PetCountEntry> a, List<PetCountEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                PetCountEntry x = a[i], y = b[i];
                if (x == null || y == null) return false;
                if (!string.Equals(x.Id ?? "", y.Id ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (x.Count != y.Count) return false;
            }
            return true;
        }

        internal static List<PetCountEntry> ClonePetMix(List<PetCountEntry> source)
        {
            if (source == null) return null;
            var copy = new List<PetCountEntry>(source.Count);
            foreach (PetCountEntry entry in source)
                copy.Add(entry == null ? null : new PetCountEntry { Id = entry.Id, Count = entry.Count });
            return copy;
        }

        // Validate the per-pet size overrides on every load: drop null/unsafe-id entries and any whose
        // level is outside the valid range (out of range means "no override" -> follow the global size),
        // dedupe by id (last wins), then cap the list. Mirrors NormalizePetMix; id "" (active pet) allowed.
        private bool NormalizePetSizes()
        {
            List<PetSizeEntry> original = PetSizes;
            var merged = new List<PetSizeEntry>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (PetSizes != null)
            {
                foreach (PetSizeEntry entry in PetSizes)
                {
                    if (entry == null) continue;
                    string id = entry.Id ?? "";
                    if (!IsAcceptablePetId(id)) continue;
                    if (entry.Level < ScalePolicy.MinimumLevel ||
                        entry.Level > ScalePolicy.MaximumLevel) continue;   // absence = follow global
                    int existing;
                    if (indexById.TryGetValue(id, out existing))
                        merged[existing].Level = entry.Level;               // last wins
                    else
                    {
                        indexById[id] = merged.Count;
                        merged.Add(new PetSizeEntry { Id = id, Level = entry.Level });
                    }
                }
            }

            List<PetSizeEntry> result = merged.Count > MaximumPetSizeEntries
                ? merged.GetRange(0, MaximumPetSizeEntries)
                : merged;
            PetSizes = result;
            return !PetSizesEqual(original, result);
        }

        internal static bool PetSizesEqual(List<PetSizeEntry> a, List<PetSizeEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                PetSizeEntry x = a[i], y = b[i];
                if (x == null || y == null) return false;
                if (!string.Equals(x.Id ?? "", y.Id ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (x.Level != y.Level) return false;
            }
            return true;
        }

        internal static List<PetSizeEntry> ClonePetSizes(List<PetSizeEntry> source)
        {
            if (source == null) return null;
            var copy = new List<PetSizeEntry>(source.Count);
            foreach (PetSizeEntry entry in source)
                copy.Add(entry == null ? null : new PetSizeEntry { Id = entry.Id, Level = entry.Level });
            return copy;
        }

        private static bool NormalizePayload(
            ref string value,
            int maximumCharacters,
            int maximumBytes)
        {
            string original = value;
            value = value ?? "";
            try
            {
                if (value.Length > maximumCharacters ||
                    new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes)
                    value = "";
            }
            catch (EncoderFallbackException)
            {
                value = "";
            }
            return !string.Equals(original, value, StringComparison.Ordinal);
        }
    }

    /// <summary>One entry in the on-screen pet mix: a pet type id and how many of it to show.</summary>
    internal sealed class PetCountEntry
    {
        [JsonProperty("id")]
        public string Id;

        [JsonProperty("count")]
        public int Count;
    }

    /// <summary>One per-pet size override: a pet type id and its scale level (1/2/3).</summary>
    internal sealed class PetSizeEntry
    {
        [JsonProperty("id")]
        public string Id;

        [JsonProperty("level")]
        public int Level;
    }

    /// <summary>
    /// Atomic JSON settings persistence with a previous-version backup, corrupt-file preservation,
    /// schema migration, and one-time import from the historical DesktopPet.config formats.
    /// </summary>
    internal sealed class AppSettingsStore
    {
        private const int MaximumSettingsFileBytes = 12 * 1024 * 1024;
        private const int ProcessLockTimeoutMilliseconds = 10000;
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private enum ReadResult
        {
            Missing,
            Loaded,
            Unreadable,
            FutureSchema
        }

        private static readonly object ProcessLock = new object();

        private readonly string _filePath;
        private readonly string _backupPath;
        private readonly string _mutexName;
        private readonly IList<string> _legacyFiles;
        private readonly int _processLockTimeoutMilliseconds;
        private bool _writesBlockedByFutureSchema;
        private bool _writesBlockedByLoadFailure;
        private AppSettingsDocument _baseline;

        public string FilePath { get { return _filePath; } }
        public string BackupPath { get { return _backupPath; } }
        public string LastRecoveryFile { get; private set; }
        public string LastLoadWarning { get; private set; }
        public bool IsReadOnlyFallback { get { return _writesBlockedByLoadFailure; } }

        public AppSettingsStore(string filePath, IEnumerable<string> legacyFiles)
            : this(
                filePath,
                legacyFiles,
                ProcessLockTimeoutMilliseconds)
        {
        }

        internal AppSettingsStore(
            string filePath,
            IEnumerable<string> legacyFiles,
            int processLockTimeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings path is required.", "filePath");
            if (!AppPaths.IsFullyQualifiedPath(filePath))
                throw new ArgumentException("Settings path must be absolute.", "filePath");
            if (processLockTimeoutMilliseconds < 1 ||
                processLockTimeoutMilliseconds > 60 * 60 * 1000)
                throw new ArgumentOutOfRangeException(
                    "processLockTimeoutMilliseconds");

            _filePath = Path.GetFullPath(filePath);
            _backupPath = _filePath + ".bak";
            _mutexName = BuildMutexName(_filePath);
            _processLockTimeoutMilliseconds =
                processLockTimeoutMilliseconds;
            _legacyFiles = new List<string>();
            if (legacyFiles != null)
            {
                foreach (string path in legacyFiles)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { AddUnique(_legacyFiles, Path.GetFullPath(path)); }
                    catch { }
                }
            }
        }

        public AppSettingsDocument Load()
        {
            lock (ProcessLock)
            {
                LastLoadWarning = null;
                try
                {
                    AppSettingsDocument loaded =
                        WithFileLock(LoadCore);
                    _writesBlockedByLoadFailure = false;
                    return loaded;
                }
                catch (Exception ex)
                {
                    _writesBlockedByLoadFailure = true;
                    AppSettingsDocument fallback =
                        Clone(_baseline) ??
                        AppSettingsDocument.CreateDefault();
                    fallback.Normalize();
                    LastLoadWarning =
                        "Settings storage is unavailable. DesktopPet is using " +
                        "an in-memory read-only fallback for this session. " +
                        ex.Message;
                    return fallback;
                }
            }
        }

        public bool Save(AppSettingsDocument settings)
        {
            if (settings == null) return false;
            lock (ProcessLock)
            {
                if (_writesBlockedByLoadFailure) return false;
                try
                {
                    return WithFileLock(delegate { return SaveMerged(settings); });
                }
                catch
                {
                    return false;
                }
            }
        }

        private AppSettingsDocument LoadCore()
        {
            _writesBlockedByFutureSchema = false;
            AppSettingsDocument loaded;
            bool changed;

            ReadResult primaryResult = TryRead(_filePath, out loaded, out changed);
            if (primaryResult == ReadResult.Loaded)
            {
                if (changed)
                    SaveDuringLoad(
                        loaded,
                        "Normalized settings could not be persisted.");
                _baseline = Clone(loaded);
                return loaded;
            }
            if (primaryResult == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                _baseline = Clone(loaded);
                return loaded;
            }

            if (primaryResult == ReadResult.Unreadable)
                PreserveCorruptPrimary();

            ReadResult backupResult = TryRead(_backupPath, out loaded, out changed);
            if (backupResult == ReadResult.Loaded)
            {
                SaveDuringLoad(
                    loaded,
                    "Recovered settings could not be restored to the primary file.");
                _baseline = Clone(loaded);
                return loaded;
            }
            if (backupResult == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                _baseline = Clone(loaded);
                return loaded;
            }

            if (LegacySettingsReader.TryRead(_legacyFiles, out loaded))
            {
                loaded.Normalize();
                SaveDuringLoad(
                    loaded,
                    "Migrated settings could not be persisted.");
                _baseline = Clone(loaded);
                return loaded;
            }

            loaded = AppSettingsDocument.CreateDefault();
            SaveDuringLoad(
                loaded,
                "Default settings could not be persisted.");
            _baseline = Clone(loaded);
            return loaded;
        }

        private void SaveDuringLoad(
            AppSettingsDocument settings,
            string warning)
        {
            if (!SaveCore(settings) &&
                string.IsNullOrEmpty(LastLoadWarning))
                LastLoadWarning = warning;
        }

        private bool SaveMerged(AppSettingsDocument settings)
        {
            if (_writesBlockedByFutureSchema ||
                settings.SchemaVersion > AppSettingsDocument.CurrentSchemaVersion)
                return false;

            AppSettingsDocument existing;
            bool changed;
            ReadResult result = TryRead(_filePath, out existing, out changed);
            if (result == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                return false;
            }

            settings.Normalize();
            AppSettingsDocument target =
                result == ReadResult.Loaded ? existing : Clone(settings);
            if (result == ReadResult.Loaded)
            {
                target.Normalize();
                MergeChangedFields(settings, _baseline, target);
            }

            if (!SaveCore(target)) return false;

            // The baseline follows the values this caller observed/wrote, not unrelated fields
            // merged from another process. A later save therefore changes only newly edited fields.
            _baseline = Clone(settings);
            return true;
        }

        private ReadResult TryRead(
            string path,
            out AppSettingsDocument settings,
            out bool changed)
        {
            settings = null;
            changed = false;
            try
            {
                if (!File.Exists(path)) return ReadResult.Missing;
                string json = ReadBoundedUtf8(path, MaximumSettingsFileBytes);
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text)
                {
                    MaxDepth = 32,
                    DateParseHandling = DateParseHandling.None
                })
                    settings = JsonSerializer.CreateDefault()
                        .Deserialize<AppSettingsDocument>(reader);
                if (settings == null) return ReadResult.Unreadable;
                if (settings.SchemaVersion > AppSettingsDocument.CurrentSchemaVersion)
                {
                    // Use the fields this version understands for the current session, but never
                    // rewrite a document owned by a newer version and discard its unknown data.
                    settings.Normalize();
                    return ReadResult.FutureSchema;
                }
                changed = settings.Normalize();
                return ReadResult.Loaded;
            }
            catch
            {
                settings = null;
                return ReadResult.Unreadable;
            }
        }

        private bool SaveCore(AppSettingsDocument settings)
        {
            try
            {
                string json = JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                if (StrictUtf8.GetByteCount(json) > MaximumSettingsFileBytes)
                    return false;
                return AtomicFile.TryWriteAllText(_filePath, json, _backupPath);
            }
            catch
            {
                return false;
            }
        }

        private static void MergeChangedFields(
            AppSettingsDocument current,
            AppSettingsDocument baseline,
            AppSettingsDocument target)
        {
            bool all = baseline == null;
            if (all || current.SchemaVersion != baseline.SchemaVersion)
                target.SchemaVersion = current.SchemaVersion;
            if (all || current.Volume != baseline.Volume)
                target.Volume = current.Volume;
            if (all || current.ScaleLevel != baseline.ScaleLevel)
                target.ScaleLevel = current.ScaleLevel;
            if (all || current.AutoStartPets != baseline.AutoStartPets)
                target.AutoStartPets = current.AutoStartPets;
            if (all || current.MultiScreen != baseline.MultiScreen)
                target.MultiScreen = current.MultiScreen;
            if (all || current.WindowForeground != baseline.WindowForeground)
                target.WindowForeground = current.WindowForeground;
            if (all || current.StealTaskbarFocus != baseline.StealTaskbarFocus)
                target.StealTaskbarFocus = current.StealTaskbarFocus;
            if (all || current.SpeechEnabled != baseline.SpeechEnabled)
                target.SpeechEnabled = current.SpeechEnabled;
            if (all || current.SpeechDurationSeconds != baseline.SpeechDurationSeconds)
                target.SpeechDurationSeconds = current.SpeechDurationSeconds;
            if (all || !string.Equals(current.Xml, baseline.Xml, StringComparison.Ordinal))
                target.Xml = current.Xml;
            if (all || !string.Equals(current.Images, baseline.Images, StringComparison.Ordinal))
                target.Images = current.Images;
            if (all || !string.Equals(current.Icon, baseline.Icon, StringComparison.Ordinal))
                target.Icon = current.Icon;
            if (all || !AppSettingsDocument.PetMixEquals(current.Pets, baseline.Pets))
                target.Pets = AppSettingsDocument.ClonePetMix(current.Pets);
            if (all || !AppSettingsDocument.PetSizesEqual(current.PetSizes, baseline.PetSizes))
                target.PetSizes = AppSettingsDocument.ClonePetSizes(current.PetSizes);
            if (all || !string.Equals(current.ThemeMode, baseline.ThemeMode, StringComparison.Ordinal))
                target.ThemeMode = current.ThemeMode;
            if (all || !string.Equals(current.AudioDeviceId, baseline.AudioDeviceId, StringComparison.Ordinal))
                target.AudioDeviceId = current.AudioDeviceId;
            if (all || !StringListEquals(current.MutedPets, baseline.MutedPets))
                target.MutedPets = current.MutedPets == null ? null : new List<string>(current.MutedPets);
            if (all || !string.Equals(current.ActivePetId, baseline.ActivePetId, StringComparison.Ordinal))
                target.ActivePetId = current.ActivePetId;
        }

        internal static bool StringListEquals(List<string> a, List<string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i] ?? "", b[i] ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        internal static AppSettingsDocument Clone(AppSettingsDocument source)
        {
            if (source == null) return null;
            IDictionary<string, JToken> extension = null;
            if (source.ExtensionData != null)
            {
                extension = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JToken> item in source.ExtensionData)
                    extension[item.Key] = item.Value == null ? null : item.Value.DeepClone();
            }

            return new AppSettingsDocument
            {
                SchemaVersion = source.SchemaVersion,
                Volume = source.Volume,
                ScaleLevel = source.ScaleLevel,
                AutoStartPets = source.AutoStartPets,
                MultiScreen = source.MultiScreen,
                WindowForeground = source.WindowForeground,
                StealTaskbarFocus = source.StealTaskbarFocus,
                SpeechEnabled = source.SpeechEnabled,
                SpeechDurationSeconds = source.SpeechDurationSeconds,
                Xml = source.Xml,
                Images = source.Images,
                Icon = source.Icon,
                Pets = AppSettingsDocument.ClonePetMix(source.Pets),
                PetSizes = AppSettingsDocument.ClonePetSizes(source.PetSizes),
                ThemeMode = source.ThemeMode,
                AudioDeviceId = source.AudioDeviceId,
                MutedPets = source.MutedPets == null ? null : new List<string>(source.MutedPets),
                ActivePetId = source.ActivePetId,
                ExtensionData = extension
            };
        }

        private T WithFileLock<T>(Func<T> action)
        {
            using (CrossSessionLock.Acquire(
                _mutexName,
                _filePath,
                _processLockTimeoutMilliseconds,
                "settings"))
                return action();
        }

        private static string BuildMutexName(string path)
        {
            return CrossSessionLock.BuildGlobalMutexName("AppSettings", path);
        }

        private static string ReadBoundedUtf8(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                8192,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("Settings file exceeds its size limit.");
                using (var memory = new MemoryStream((int)stream.Length))
                {
                    byte[] buffer = new byte[8192];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "Settings file exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }
                    return StrictUtf8.GetString(memory.ToArray());
                }
            }
        }

        private void PreserveCorruptPrimary()
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                Directory.CreateDirectory(directory);
                string name = Path.GetFileNameWithoutExtension(_filePath) +
                    ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(_filePath);
                string recovery = Path.Combine(directory, name);
                File.Copy(_filePath, recovery, false);
                File.Delete(_filePath);
                LastRecoveryFile = recovery;
            }
            catch
            {
                // Keep the unreadable primary untouched if it cannot be preserved safely.
            }
        }

        private static void AddUnique(ICollection<string> values, string candidate)
        {
            foreach (string value in values)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            values.Add(candidate);
        }
    }

    /// <summary>
    /// Coordinates durable per-user files across console and RDP sessions. A Global named mutex
    /// with a current-user ACL provides the normal fast path, while a same-directory file lease is
    /// always held as the cross-session fail-safe when the Global namespace is restricted.
    /// </summary>
    internal static class CrossSessionLock
    {
        private const int RetryMilliseconds = 25;

        public static string BuildGlobalMutexName(string category, string path)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("A lock category is required.", "category");
            string normalized = Path.GetFullPath(path).ToUpperInvariant();
            string user = CurrentUserSid();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(user + "\n" + normalized));
                var suffix = new StringBuilder(32);
                for (int index = 0; index < 16; index++)
                    suffix.Append(
                        hash[index].ToString("x2", CultureInfo.InvariantCulture));
                return @"Global\DesktopPet." + category + "." + suffix;
            }
        }

        public static IDisposable Acquire(
            string mutexName,
            string dataPath,
            int timeoutMilliseconds,
            string description)
        {
            IDisposable lease = TryAcquire(
                mutexName,
                dataPath,
                timeoutMilliseconds);
            if (lease == null)
                throw new IOException(
                    "Timed out waiting for the " +
                    (string.IsNullOrWhiteSpace(description)
                        ? "application data"
                        : description) +
                    " lock.");
            return lease;
        }

        public static IDisposable TryAcquire(
            string mutexName,
            string dataPath,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(mutexName) ||
                !mutexName.StartsWith(@"Global\", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(dataPath) ||
                timeoutMilliseconds < 0)
                return null;

            var stopwatch = Stopwatch.StartNew();
            bool mutexAvailable;
            IDisposable mutexLease = TryAcquireGlobalMutex(
                mutexName,
                timeoutMilliseconds,
                out mutexAvailable);
            if (mutexAvailable && mutexLease == null)
                return null;

            int remaining = RemainingMilliseconds(
                timeoutMilliseconds,
                stopwatch.ElapsedMilliseconds);
            IDisposable fileLease = TryAcquireFileLease(
                dataPath + ".lock",
                remaining);
            if (fileLease == null)
            {
                if (mutexLease != null) mutexLease.Dispose();
                return null;
            }
            return new CompositeLease(fileLease, mutexLease);
        }

        private static IDisposable TryAcquireGlobalMutex(
            string name,
            int timeoutMilliseconds,
            out bool available)
        {
            available = false;
            Mutex mutex = null;
            try
            {
                bool created;
                try
                {
                    // net10: the net48 `new Mutex(bool,string,out bool,MutexSecurity)` overload moved
                    // to MutexAcl.Create (System.Threading), preserving the current-user ACL intent.
                    mutex = MutexAcl.Create(
                        false,
                        name,
                        out created,
                        CurrentUserMutexSecurity());
                }
                catch (UnauthorizedAccessException)
                {
                    mutex = MutexAcl.OpenExisting(
                        name,
                        MutexRights.Synchronize | MutexRights.Modify);
                }
                available = true;
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(timeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return new MutexLease(mutex);
            }
            catch (Exception ex)
            {
                if (mutex != null) mutex.Dispose();
                if (ex is OutOfMemoryException) throw;
                available = false;
                return null;
            }
        }

        private static IDisposable TryAcquireFileLease(
            string lockPath,
            int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    string directory = Path.GetDirectoryName(
                        Path.GetFullPath(lockPath));
                    if (string.IsNullOrEmpty(directory)) return null;
                    Directory.CreateDirectory(directory);
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.None);
                }
                catch (IOException)
                {
                    if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                        return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
                Thread.Sleep(Math.Min(
                    RetryMilliseconds,
                    Math.Max(
                        1,
                        RemainingMilliseconds(
                            timeoutMilliseconds,
                            stopwatch.ElapsedMilliseconds))));
            }
        }

        private static int RemainingMilliseconds(int timeout, long elapsed)
        {
            if (elapsed >= timeout) return 0;
            return (int)Math.Min(int.MaxValue, timeout - elapsed);
        }

        private static MutexSecurity CurrentUserMutexSecurity()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null)
                throw new UnauthorizedAccessException(
                    "The current Windows user has no security identifier.");
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                user,
                MutexRights.Synchronize | MutexRights.Modify,
                AccessControlType.Allow));
            return security;
        }

        private static string CurrentUserSid()
        {
            try
            {
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                return user == null ? "" : user.Value;
            }
            catch
            {
                return "";
            }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;

            public MutexLease(Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                Mutex mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }

        private sealed class CompositeLease : IDisposable
        {
            private IDisposable _fileLease;
            private IDisposable _mutexLease;

            public CompositeLease(
                IDisposable fileLease,
                IDisposable mutexLease)
            {
                _fileLease = fileLease;
                _mutexLease = mutexLease;
            }

            public void Dispose()
            {
                IDisposable fileLease =
                    Interlocked.Exchange(ref _fileLease, null);
                IDisposable mutexLease =
                    Interlocked.Exchange(ref _mutexLease, null);
                try
                {
                    if (fileLease != null) fileLease.Dispose();
                }
                finally
                {
                    if (mutexLease != null) mutexLease.Dispose();
                }
            }
        }
    }

    /// <summary>Same-directory durable replace helper reusable by the other JSON stores.</summary>
    internal static class AtomicFile
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken)
        {
            ReplaceExisting(
                temporaryPath,
                destinationPath,
                backupPath,
                cancellationToken,
                null);
        }

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken,
            Action<string, string, string, bool> replaceFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (replaceFile == null)
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                else
                    replaceFile(temporaryPath, destinationPath, backupPath, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Portable copies can live on filesystems where File.Replace is unavailable.
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(backupPath))
                File.Copy(destinationPath, backupPath, true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!MoveFileEx(
                    temporaryPath,
                    destinationPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static bool TryWriteAllText(string path, string contents, string backupPath)
        {
            string temp = null;
            try
            {
                if (!AppPaths.IsFullyQualifiedPath(path))
                    return false;

                path = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                temp = Path.Combine(
                    directory,
                    "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents ?? "");
                    writer.Flush();
                    stream.Flush(true);
                }

                if (!File.Exists(path))
                {
                    try
                    {
                        File.Move(temp, path);
                        temp = null;
                        return true;
                    }
                    catch (IOException)
                    {
                        // Another process may have created the destination after our existence check.
                        if (!File.Exists(path)) throw;
                    }
                }

                ReplaceExisting(
                    temp,
                    path,
                    backupPath,
                    CancellationToken.None);
                temp = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (temp != null)
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }
    }

    /// <summary>Strict, read-only importer for historical appSettings and userSettings XML.</summary>
    internal static class LegacySettingsReader
    {
        private const long MaximumLegacyConfigurationBytes = 12L * 1024L * 1024L;

        public static bool TryRead(
            IEnumerable<string> candidates,
            out AppSettingsDocument settings)
        {
            AppSettingsDocument imported = AppSettingsDocument.CreateDefault();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (candidates != null)
            {
                foreach (string path in candidates)
                    ReadCandidate(path, values);
            }

            bool found = false;
            found |= ApplyString(values, "xml", delegate (string value) { imported.Xml = value; });
            found |= ApplyString(values, "Images", delegate (string value) { imported.Images = value; });
            found |= ApplyString(values, "Icon", delegate (string value) { imported.Icon = value; });
            found |= ApplyBool(values, "Multiscreen", delegate (bool value) { imported.MultiScreen = value; });
            found |= ApplyBool(values, "WinForeground", delegate (bool value) { imported.WindowForeground = value; });
            found |= ApplyBool(values, "StealTaskbarFocus", delegate (bool value) { imported.StealTaskbarFocus = value; });
            found |= ApplyBool(values, "SpeechEnabled", delegate (bool value) { imported.SpeechEnabled = value; });
            found |= ApplyInt(values, "Scale", delegate (int value) { imported.ScaleLevel = value; });
            found |= ApplyInt(values, "AutostartPets", delegate (int value) { imported.AutoStartPets = value; });
            found |= ApplyInt(values, "SpeechDuration", delegate (int value) { imported.SpeechDurationSeconds = value; });

            string volumeText;
            if (values.TryGetValue("Volume", out volumeText))
            {
                double volume;
                if (TryParseDouble(volumeText, out volume))
                {
                    // The old default was normalized ("0.3"), while the old UI persisted integer
                    // percentages ("30"). Accept both representations during the one-time import.
                    if (volume > 1.0) volume /= 100.0;
                    imported.Volume = volume;
                    found = true;
                }
            }

            imported.Normalize();
            settings = imported;
            return found;
        }

        private static void ReadCandidate(
            string path,
            IDictionary<string, string> values)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                var file = new FileInfo(path);
                if (file.Length > MaximumLegacyConfigurationBytes) return;

                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 0,
                    MaxCharactersInDocument = MaximumLegacyConfigurationBytes
                };
                var document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, readerSettings))
                    document.Load(reader);

                XmlNodeList appSettings = document.SelectNodes("/configuration/appSettings/add");
                if (appSettings != null)
                {
                    foreach (XmlNode node in appSettings)
                    {
                        string key = Attribute(node, "key");
                        string value = Attribute(node, "value");
                        AddFirst(values, key, value);
                    }
                }

                XmlNodeList userSettings =
                    document.SelectNodes("/configuration/userSettings/*/setting");
                if (userSettings != null)
                {
                    foreach (XmlNode node in userSettings)
                    {
                        string key = Attribute(node, "name");
                        XmlNode valueNode = node.SelectSingleNode("value");
                        AddFirst(values, key, valueNode == null ? null : valueNode.InnerText);
                    }
                }
            }
            catch
            {
                // A malformed candidate does not block later candidates or fresh defaults.
            }
        }

        private static string Attribute(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return null;
            XmlAttribute attribute = node.Attributes[name];
            return attribute == null ? null : attribute.Value;
        }

        private static void AddFirst(
            IDictionary<string, string> values,
            string key,
            string value)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null || values.ContainsKey(key))
                return;
            values[key] = value;
        }

        private static bool ApplyString(
            IDictionary<string, string> values,
            string key,
            Action<string> apply)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return false;
            apply(value ?? "");
            return true;
        }

        private static bool ApplyBool(
            IDictionary<string, string> values,
            string key,
            Action<bool> apply)
        {
            string text;
            bool value;
            if (!values.TryGetValue(key, out text) || !bool.TryParse(text, out value))
                return false;
            apply(value);
            return true;
        }

        private static bool ApplyInt(
            IDictionary<string, string> values,
            string key,
            Action<int> apply)
        {
            string text;
            int value;
            if (!values.TryGetValue(key, out text) ||
                !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return false;
            apply(value);
            return true;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(
                    text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
