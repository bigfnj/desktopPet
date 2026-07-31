using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet.Ai
{
    /// <summary>
    /// AI-layer configuration, persisted as JSON under the canonical application data root. Kept
    /// separate from the WinForms user.config so the AI layer stays self-contained and the original
    /// engine's settings are never touched. Missing/corrupt files fall back to recovery/defaults.
    /// </summary>
    internal sealed class AiSettings
    {
        public const int CurrentSchemaVersion = 1;
        private const int MaximumSettingsBytes = 256 * 1024;
        private const int MaximumEndpointCharacters = 2048;
        internal const int MaximumModelCharacters = 256;
        private const int MaximumPathCharacters = 1024;
        private const int MaximumNameCharacters = 80;
        private const int MaximumPersonalityCharacters = 512;
        private const int MaximumApiKeyCharacters = 8192;
        private const int MaximumEncryptedApiKeyCharacters = 16384;
        internal const int MaximumApiKeyScopes = 32;
        private const int MaximumDisabledSources = 128;
        private const int MaximumSourceCharacters = 128;
        private const int ProcessLockTimeoutMilliseconds = 10000;
        private static readonly object ProcessLock = new object();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] PersistedFieldNames = BuildPersistedFieldNames();

        [JsonIgnore]
        private bool _writesBlockedByFutureSchema;

        [JsonIgnore]
        private JObject _baseline;

        /// <summary>Persistence schema for forward migrations.</summary>
        public int SchemaVersion = CurrentSchemaVersion;

        /// <summary>Ollama base endpoint. No trailing slash needed.</summary>
        public string Endpoint = "http://localhost:11434";

        /// <summary>Fast text-only model used for OCR-based commentary.</summary>
        public string TextModel = "llama3.1:8b";

        /// <summary>
        /// Multimodal model used when <see cref="UseVision"/> is on (much slower/heavier than OCR).
        /// Default is a small, fast vision model; bigger ones (e.g. mistral-small3.2:24b) read the
        /// screen better but can take a minute per glance. See the grimoire for recommendations.
        /// </summary>
        public string VisionModel = "gemma3:4b";

        /// <summary>
        /// When true, send a downscaled screenshot to the vision model instead of OCR text.
        /// Only used for explicit asks (hotkey/tray) — idle commentary always stays on the fast
        /// text path, since a vision glance can take tens of seconds.
        /// </summary>
        public bool UseVision = false;

        /// <summary>Per-request HTTP timeout. Generous because a cold vision model on a full-screen
        /// image can take a minute or more.</summary>
        public int TimeoutSeconds = 120;

        /// <summary>Full path to tesseract.exe. Empty means "find <c>tesseract</c> on PATH".</summary>
        public string TesseractPath = "";

        // ---- Phase 5: persona ----------------------------------------------

        /// <summary>The pet's name, injected into its persona. Empty -> a generic "desktop pet".</summary>
        public string PetName = "eSheep";

        /// <summary>Optional name the pet may address you by. Empty -> it won't use one.</summary>
        public string UserName = "";

        /// <summary>One-line personality blurb steering the pet's tone.</summary>
        public string Personality = "friendly, upbeat and a little cheeky";

        /// <summary>
        /// Remember recent remarks (rolling history in chat-history.json) so the pet has continuity
        /// and avoids repeating itself. Turn off to make every reaction stateless.
        /// </summary>
        public bool MemoryEnabled = false;

        /// <summary>
        /// Explicit consent for sending screen/OCR/window context to a non-loopback provider.
        /// Endpoint policy enforces this before any cloud request.
        /// </summary>
        public bool CloudDataConsent = false;

        // ---- fortunes (Phase A) --------------------------------------------

        /// <summary>Include edgier/adult fortunes (see <see cref="SpicyTier"/>) on top of the
        /// family-friendly ones. Off = general content only.</summary>
        public bool SpicyFortunes = false;

        /// <summary>
        /// Which spicy content to include when <see cref="SpicyFortunes"/> is on:
        /// <c>"edgy"</c> = crude/adult humor + explicit (everything), <c>"nsfw"</c> = explicit only.
        /// </summary>
        public string SpicyTier = "edgy";

        /// <summary>
        /// With <see cref="SpicyFortunes"/> on, pull ONLY the spicy tiers and skip the tame
        /// (general) ones. Ignored when SpicyFortunes is off.
        /// </summary>
        public bool SpicyOnly = false;

        /// <summary>
        /// Drop any fortune flagged for recognized profanity or explicit sexual content, at every
        /// level (a conservative hard filter).
        /// </summary>
        public bool NoProfanity = false;

        /// <summary>
        /// Pick fortunes that fit what's on screen (local bge-small embedder). Fully offline, bundled,
        /// no keys. Falls back to random automatically if the model is missing or no good match is found.
        /// </summary>
        public bool SmartFortunes = true;

        /// <summary>
        /// Source collections the user has switched OFF in the picker. Empty = all sources enabled
        /// (so newly-added collections default to on).
        /// </summary>
        public System.Collections.Generic.List<string> DisabledSources =
            new System.Collections.Generic.List<string>();

        /// <summary>
        /// Delivery genres the user has switched OFF in the picker (e.g. "tv-quote", "insult").
        /// Empty = all genres enabled (so newly-added genres default to on). Like
        /// <see cref="DisabledSources"/>, this is a hard preference filter that is never relaxed.
        /// </summary>
        public System.Collections.Generic.List<string> DisabledGenres =
            new System.Collections.Generic.List<string>();

        // ---- AI brain master switch ----------------------------------------

        /// <summary>
        /// Master switch for the optional screen-commentary LLM. OFF by default, so no selected
        /// provider is contacted and only the local CPU smart-fortunes embedder runs. Toggle it from
        /// the tray ("Enable AI" / "Disable AI") or the AI tab. Ollama additionally supports
        /// keep-alive model warm-up and unload; generic OpenAI-compatible providers do not.
        /// </summary>
        public bool AiBrainEnabled = false;

        // ---- provider ("One Interface": Ollama / LM Studio / llama.cpp / OpenRouter / OpenAI) ----

        /// <summary>Provider id: ollama | lmstudio | llamacpp | openrouter | openai | custom.</summary>
        public string Provider = "ollama";

        /// <summary>Base URL (including <c>/v1</c>) for non-Ollama OpenAI-compatible providers.</summary>
        public string OpenAiBaseUrl = "";

        /// <summary>
        /// Last endpoint entered for the Custom provider. Preserving this separately prevents a
        /// preset selection from making an endpoint-scoped Custom credential unreachable.
        /// </summary>
        public string CustomOpenAiBaseUrl = "";

        /// <summary>
        /// Legacy single DPAPI-encrypted key. Normalization migrates it once to the currently
        /// selected provider/endpoint scope, then clears this field so a provider switch cannot
        /// reuse it.
        /// </summary>
        public string ApiKeyEnc = "";

        /// <summary>
        /// DPAPI-encrypted keys keyed by a hash of provider plus normalized endpoint. Neither
        /// plaintext credentials nor endpoint text are stored in the dictionary keys.
        /// </summary>
        public Dictionary<string, string> ApiKeysEnc =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Plaintext API key for the currently selected provider/endpoint scope. Not serialized.
        /// </summary>
        [JsonIgnore]
        public string ApiKey
        {
            get { return GetApiKey(Provider, SelectedCredentialEndpoint()); }
            set
            {
                string error;
                if (!TrySetApiKey(value, out error))
                    throw new InvalidOperationException(error);
            }
        }

        // ---- Phase 3: triggers ---------------------------------------------

        /// <summary>Register a global hotkey that fires the reactive "ask about my screen" flow.</summary>
        public bool HotkeyEnabled = true;

        /// <summary>Global hotkey combination, e.g. "Ctrl+Alt+P". Needs at least one modifier.</summary>
        public string Hotkey = "Ctrl+Alt+P";

        /// <summary>Opt-in: the pet occasionally comments on the screen unprompted.</summary>
        public bool IdleCommentaryEnabled = false;

        /// <summary>Lower bound of the random idle-commentary interval, in seconds.</summary>
        public int IdleMinSeconds = 90;

        /// <summary>Upper bound of the random idle-commentary interval, in seconds.</summary>
        public int IdleMaxSeconds = 150;

        /// <summary>Idle loop skips a turn unless the screen changed by at least this % of average luma.</summary>
        public int IdleChangeThresholdPercent = 5;

        // ---- launch preparation --------------------------------------------

        /// <summary>On launch, start the Ollama server (<c>ollama serve</c>) if it isn't already reachable.</summary>
        public bool AutoStartServer = true;

        /// <summary>On launch, preload the active model into memory so the first ask is fast.</summary>
        public bool WarmUpOnLaunch = true;

        /// <summary>Full path to ollama.exe. Empty means autodetect (PATH + default install locations).</summary>
        public string OllamaPath = "";

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData =
            new Dictionary<string, JToken>(StringComparer.Ordinal);

        [JsonIgnore]
        public static string FilePath
        {
            get { return AppPaths.AiSettingsFile; }
        }

        /// <summary>Load settings, writing a default file on first run. Never throws.</summary>
        public static AiSettings Load()
        {
            lock (ProcessLock)
            {
                try
                {
                    return WithFileLock(LoadCore);
                }
                catch { }
                // A lock/read failure must not turn into a later blind overwrite of settings that
                // this process never observed.
                return new AiSettings { _writesBlockedByFutureSchema = true };
            }
        }

        /// <summary>Persist settings. Returns false when durable storage is unavailable or blocked.</summary>
        public bool Save()
        {
            return SaveWithin(ProcessLockTimeoutMilliseconds);
        }

        /// <summary>
        /// Persist settings within one aggregate lock budget. UI callers use a short budget so a
        /// hung peer cannot freeze the message thread for the full cross-session timeout.
        /// </summary>
        internal bool SaveWithin(int timeoutMilliseconds)
        {
            timeoutMilliseconds = Math.Max(0, timeoutMilliseconds);
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool entered = false;
            try
            {
                entered = Monitor.TryEnter(ProcessLock, timeoutMilliseconds);
                if (!entered) return false;
                int remaining = RemainingMilliseconds(
                    timeoutMilliseconds,
                    stopwatch.ElapsedMilliseconds);
                return WithFileLock(SaveMerged, remaining);
            }
            catch { return false; }
            finally
            {
                if (entered) Monitor.Exit(ProcessLock);
            }
        }

        private static AiSettings LoadCore()
        {
            AiSettings loaded;
            ReadResult result = TryRead(FilePath, out loaded);
            if (result == ReadResult.Loaded || result == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    result == ReadResult.FutureSchema;
                bool changed = loaded.Normalize();
                if (changed && result == ReadResult.Loaded) loaded.SaveCore();
                loaded.CaptureBaseline();
                return loaded;
            }

            ReadResult backupResult = TryRead(FilePath + ".bak", out loaded);
            if (backupResult == ReadResult.Loaded ||
                backupResult == ReadResult.FutureSchema)
            {
                loaded._writesBlockedByFutureSchema =
                    backupResult == ReadResult.FutureSchema;
                loaded.Normalize();
                if (backupResult == ReadResult.Loaded)
                    loaded._writesBlockedByFutureSchema =
                        !loaded.RestorePrimaryWithoutRotatingBackup();
                loaded.CaptureBaseline();
                return loaded;
            }

            if (result == ReadResult.Missing && AppPaths.LegacyMigrationEnabled)
            {
                string legacy = Path.Combine(
                    AppPaths.LegacyRoamingDataRoot,
                    "ai-settings.json");
                if (!string.Equals(
                        Path.GetFullPath(legacy),
                        Path.GetFullPath(FilePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ReadResult legacyResult = TryRead(legacy, out loaded);
                    if (legacyResult == ReadResult.Loaded)
                    {
                        loaded.Normalize();
                        loaded.SaveCore();
                        loaded.CaptureBaseline();
                        return loaded;
                    }
                    if (legacyResult == ReadResult.FutureSchema)
                    {
                        loaded._writesBlockedByFutureSchema = true;
                        loaded.Normalize();
                        loaded.CaptureBaseline();
                        return loaded;
                    }
                }
            }

            AiSettings defaults = new AiSettings();
            defaults.SaveCore();
            defaults.CaptureBaseline();
            return defaults;
        }

        private bool SaveMerged()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;

            AiSettings existing;
            ReadResult result = TryRead(FilePath, out existing);
            if (result == ReadResult.FutureSchema)
            {
                _writesBlockedByFutureSchema = true;
                return false;
            }

            Normalize();
            JObject current = JObject.FromObject(this);
            JObject target;
            bool applyAll = result != ReadResult.Loaded || _baseline == null;
            if (result == ReadResult.Loaded)
            {
                existing.Normalize();
                target = JObject.FromObject(existing);
            }
            else
            {
                target = new JObject();
            }

            foreach (string fieldName in PersistedFieldNames)
            {
                if (string.Equals(
                        fieldName,
                        "ApiKeysEnc",
                        StringComparison.Ordinal))
                {
                    MergeCredentialScopes(
                        current,
                        target,
                        _baseline,
                        applyAll);
                    continue;
                }
                JToken value = current[fieldName];
                JToken baselineValue = _baseline == null ? null : _baseline[fieldName];
                if (!applyAll && JToken.DeepEquals(value, baselineValue)) continue;
                if (value == null)
                    target.Remove(fieldName);
                else
                    target[fieldName] = value.DeepClone();
            }

            if (!SaveDocument(target)) return false;
            _baseline = current;
            return true;
        }

        private static void MergeCredentialScopes(
            JObject current,
            JObject target,
            JObject baseline,
            bool applyAll)
        {
            const string FieldName = "ApiKeysEnc";
            JToken currentValue = current[FieldName];
            if (applyAll)
            {
                if (currentValue == null)
                    target.Remove(FieldName);
                else
                    target[FieldName] = currentValue.DeepClone();
                return;
            }

            var currentScopes = currentValue as JObject ?? new JObject();
            var baselineScopes =
                (baseline == null ? null : baseline[FieldName]) as JObject ??
                new JObject();
            var targetScopes = target[FieldName] as JObject ?? new JObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JProperty property in currentScopes.Properties())
                names.Add(property.Name);
            foreach (JProperty property in baselineScopes.Properties())
                names.Add(property.Name);

            foreach (string name in names)
            {
                JToken value = currentScopes[name];
                JToken baselineValue = baselineScopes[name];
                if (JToken.DeepEquals(value, baselineValue)) continue;
                if (value == null)
                    targetScopes.Remove(name);
                else
                    targetScopes[name] = value.DeepClone();
            }
            target[FieldName] = targetScopes;
        }

        /// <summary>Clamp persisted operational ranges before any caller consumes them.</summary>
        internal bool Normalize()
        {
            bool changed = false;

            if (SchemaVersion <= 0)
            {
                SchemaVersion = CurrentSchemaVersion;
                changed = true;
            }
            changed |= Clamp(ref TimeoutSeconds, 10, 600);
            changed |= Clamp(ref IdleMinSeconds, 15, 3600);
            changed |= Clamp(ref IdleMaxSeconds, IdleMinSeconds, 3600);
            changed |= Clamp(ref IdleChangeThresholdPercent, 0, 100);

            changed |= NormalizeString(
                ref Endpoint, "http://localhost:11434", MaximumEndpointCharacters);
            changed |= NormalizeModel(ref TextModel, "llama3.1:8b");
            changed |= NormalizeModel(ref VisionModel, "gemma3:4b");
            changed |= NormalizeString(ref TesseractPath, "", MaximumPathCharacters);
            changed |= NormalizeString(ref PetName, "eSheep", MaximumNameCharacters);
            changed |= NormalizeString(ref UserName, "", MaximumNameCharacters);
            changed |= NormalizeString(
                ref Personality,
                "friendly, upbeat and a little cheeky",
                MaximumPersonalityCharacters);
            changed |= NormalizeString(ref SpicyTier, "edgy", 16);
            if (!string.Equals(SpicyTier, "edgy", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(SpicyTier, "nsfw", StringComparison.OrdinalIgnoreCase))
            {
                SpicyTier = "edgy";
                changed = true;
            }
            changed |= NormalizeString(ref Provider, "ollama", 32);
            string normalizedProvider = Provider.ToLowerInvariant();
            if (!string.Equals(Provider, normalizedProvider, StringComparison.Ordinal))
            {
                Provider = normalizedProvider;
                changed = true;
            }
            if (!IsKnownProvider(Provider))
            {
                Provider = "ollama";
                changed = true;
            }
            changed |= NormalizeString(
                ref OpenAiBaseUrl, "", MaximumEndpointCharacters);
            changed |= NormalizeString(
                ref CustomOpenAiBaseUrl, "", MaximumEndpointCharacters);
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(CustomOpenAiBaseUrl) &&
                !string.IsNullOrEmpty(OpenAiBaseUrl))
            {
                CustomOpenAiBaseUrl = OpenAiBaseUrl;
                changed = true;
            }
            changed |= NormalizeString(
                ref ApiKeyEnc, "", MaximumEncryptedApiKeyCharacters);
            changed |= NormalizeApiKeyScopes();
            if (!string.IsNullOrEmpty(ApiKeyEnc))
            {
                string scope = BuildCredentialScope(
                    Provider,
                    SelectedCredentialEndpoint());
                if (!IsWellFormedEncryptedApiKey(ApiKeyEnc))
                {
                    ApiKeyEnc = "";
                    changed = true;
                }
                else if (!string.IsNullOrEmpty(scope))
                {
                    if (ApiKeysEnc.ContainsKey(scope))
                    {
                        // A provider-scoped credential supersedes the legacy singleton.
                        ApiKeyEnc = "";
                        changed = true;
                    }
                    else
                    {
                        string migratedKey;
                        if (TryDecryptApiKey(ApiKeyEnc, out migratedKey))
                        {
                            ApiKeysEnc[scope] = ApiKeyEnc;
                            ApiKeyEnc = "";
                            changed = true;
                        }
                        // A well-formed value that DPAPI cannot currently decrypt is preserved.
                        // Profile/DPAPI failures can be transient and must not become credential
                        // deletion during automatic normalization.
                    }
                }
            }
            changed |= NormalizeString(ref Hotkey, "Ctrl+Alt+P", 64);
            changed |= NormalizeString(ref OllamaPath, "", MaximumPathCharacters);
            changed |= NormalizeDisabledSources();
            return changed;
        }

        private bool SaveCore()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;
            return SaveDocument(JObject.FromObject(this));
        }

        private bool RestorePrimaryWithoutRotatingBackup()
        {
            if (_writesBlockedByFutureSchema ||
                SchemaVersion > CurrentSchemaVersion)
                return false;
            return SaveDocument(JObject.FromObject(this), null);
        }

        private static bool SaveDocument(JObject document)
        {
            return SaveDocument(document, FilePath + ".bak");
        }

        private static bool SaveDocument(JObject document, string backupPath)
        {
            if (document == null) return false;
            string json = document.ToString(Formatting.Indented);
            if (StrictUtf8.GetByteCount(json) > MaximumSettingsBytes)
                return false;
            return AtomicFile.TryWriteAllText(FilePath, json, backupPath);
        }

        private void CaptureBaseline()
        {
            _baseline = JObject.FromObject(this);
        }

        private static T WithFileLock<T>(Func<T> action)
        {
            return WithFileLock(action, ProcessLockTimeoutMilliseconds);
        }

        private static T WithFileLock<T>(
            Func<T> action,
            int timeoutMilliseconds)
        {
            using (CrossSessionLock.Acquire(
                BuildMutexName(FilePath),
                FilePath,
                Math.Max(0, timeoutMilliseconds),
                "AI settings"))
                return action();
        }

        private static int RemainingMilliseconds(
            int timeoutMilliseconds,
            long elapsedMilliseconds)
        {
            long remaining = (long)Math.Max(0, timeoutMilliseconds) -
                Math.Max(0L, elapsedMilliseconds);
            if (remaining <= 0) return 0;
            return remaining >= int.MaxValue
                ? int.MaxValue
                : (int)remaining;
        }

        private static string BuildMutexName(string path)
        {
            return CrossSessionLock.BuildGlobalMutexName("AiSettings", path);
        }

        private static string[] BuildPersistedFieldNames()
        {
            var names = new List<string>();
            foreach (FieldInfo field in typeof(AiSettings).GetFields(
                BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsDefined(typeof(JsonIgnoreAttribute), true) ||
                    field.IsDefined(typeof(JsonExtensionDataAttribute), true))
                    continue;
                names.Add(field.Name);
            }
            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        private static ReadResult TryRead(string path, out AiSettings settings)
        {
            settings = null;
            try
            {
                if (!File.Exists(path)) return ReadResult.Missing;
                string json = ReadBoundedUtf8(path, MaximumSettingsBytes);
                settings = JsonConvert.DeserializeObject<AiSettings>(json);
                if (settings == null) return ReadResult.Unreadable;
                return settings.SchemaVersion > CurrentSchemaVersion
                    ? ReadResult.FutureSchema
                    : ReadResult.Loaded;
            }
            catch
            {
                settings = null;
                return ReadResult.Unreadable;
            }
        }

        private static string ReadBoundedUtf8(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("AI settings file exceeds its size limit.");
                using (var memory = new MemoryStream((int)stream.Length))
                {
                    byte[] buffer = new byte[4096];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "AI settings file exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }
                    return StrictUtf8.GetString(memory.ToArray());
                }
            }
        }

        private static bool Clamp(ref int value, int minimum, int maximum)
        {
            int normalized = Math.Max(minimum, Math.Min(maximum, value));
            if (normalized == value) return false;
            value = normalized;
            return true;
        }

        private static bool NormalizeString(
            ref string value,
            string fallback,
            int maximumCharacters)
        {
            string original = value;
            value = value ?? fallback;
            var clean = new StringBuilder(Math.Min(value.Length, maximumCharacters));
            string candidate = value.Trim();
            for (int index = 0; index < candidate.Length;)
            {
                char character = candidate[index++];
                if (char.IsControl(character)) continue;
                if (char.IsHighSurrogate(character))
                {
                    if (index >= candidate.Length ||
                        !char.IsLowSurrogate(candidate[index]))
                        continue;
                    if (clean.Length + 2 > maximumCharacters) break;
                    clean.Append(character);
                    clean.Append(candidate[index++]);
                    continue;
                }
                if (char.IsLowSurrogate(character)) continue;
                if (clean.Length >= maximumCharacters) break;
                clean.Append(character);
            }
            value = clean.ToString();
            return !string.Equals(original, value, StringComparison.Ordinal);
        }

        private static bool NormalizeModel(ref string value, string fallback)
        {
            string original = value;
            string normalized;
            if (!AiModelPolicy.TryNormalize(value, out normalized))
                normalized = fallback;
            value = normalized;
            return !string.Equals(original, value, StringComparison.Ordinal);
        }

        internal string CredentialIdentity()
        {
            string key = ApiKey;
            if (string.IsNullOrEmpty(key)) return "anonymous";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    StrictUtf8.GetBytes(
                        "DesktopPet.CredentialIdentity.v1\n" + key));
                return ToHex(hash);
            }
        }

        internal static string BuildCredentialScope(
            string provider,
            string endpoint)
        {
            string normalizedProvider =
                string.IsNullOrWhiteSpace(provider)
                    ? "ollama"
                    : provider.Trim().ToLowerInvariant();
            if (string.Equals(
                    normalizedProvider,
                    "ollama",
                    StringComparison.Ordinal))
                return "";

            string endpointIdentity = (endpoint ?? "").Trim();
            string normalizedEndpoint;
            string error;
            if (AiEndpointPolicy.TryNormalize(
                    endpointIdentity,
                    out normalizedEndpoint,
                    out error))
                endpointIdentity = normalizedEndpoint;
            if (string.IsNullOrEmpty(endpointIdentity)) return "";

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    StrictUtf8.GetBytes(
                        normalizedProvider + "\n" + endpointIdentity));
                return ToHex(hash);
            }
        }

        private string SelectedCredentialEndpoint()
        {
            return string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase)
                ? Endpoint
                : OpenAiBaseUrl;
        }

        /// <summary>
        /// Select a provider and return the endpoint its UI should display. Preset providers may
        /// replace the shared active endpoint; Custom always restores its remembered endpoint.
        /// </summary>
        internal string SelectProviderEndpoint(
            string provider,
            bool prefillPreset)
        {
            RememberSelectedCustomEndpoint();
            AiProviders.Preset preset = AiProviders.Get(provider);
            Provider = preset.Id;

            if (string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase))
            {
                string endpoint = Endpoint;
                if (prefillPreset || string.IsNullOrWhiteSpace(endpoint))
                    endpoint = preset.BaseUrl;
                Endpoint = (endpoint ?? "").Trim();
                return Endpoint;
            }

            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
            {
                OpenAiBaseUrl = (CustomOpenAiBaseUrl ?? "").Trim();
                return OpenAiBaseUrl;
            }

            string compatibleEndpoint = OpenAiBaseUrl;
            if (prefillPreset || string.IsNullOrWhiteSpace(compatibleEndpoint))
                compatibleEndpoint = preset.BaseUrl;
            OpenAiBaseUrl = (compatibleEndpoint ?? "").Trim();
            return OpenAiBaseUrl;
        }

        internal void UpdateSelectedProviderEndpoint(string endpoint)
        {
            endpoint = (endpoint ?? "").Trim();
            if (string.Equals(
                    Provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase))
            {
                Endpoint = endpoint;
                return;
            }

            OpenAiBaseUrl = endpoint;
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
                CustomOpenAiBaseUrl = endpoint;
        }

        private void RememberSelectedCustomEndpoint()
        {
            if (string.Equals(
                    Provider,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
                CustomOpenAiBaseUrl = (OpenAiBaseUrl ?? "").Trim();
        }

        private string GetApiKey(string provider, string endpoint)
        {
            string scope = BuildCredentialScope(provider, endpoint);
            if (string.IsNullOrEmpty(scope) || ApiKeysEnc == null)
                return "";
            string encrypted;
            string clear;
            return ApiKeysEnc.TryGetValue(scope, out encrypted) &&
                   TryDecryptApiKey(encrypted, out clear)
                ? clear
                : "";
        }

        internal bool TrySetApiKey(string value, out string error)
        {
            error = "";
            value = value ?? "";
            string scope = BuildCredentialScope(
                Provider,
                SelectedCredentialEndpoint());
            if (string.IsNullOrEmpty(scope))
            {
                if (value.Length == 0) return true;
                error =
                    "Select a provider and valid endpoint before entering an API key.";
                return false;
            }
            if (ApiKeysEnc == null)
                ApiKeysEnc = new Dictionary<string, string>(
                    StringComparer.Ordinal);

            if (string.IsNullOrEmpty(value))
            {
                ApiKeysEnc.Remove(scope);
                return true;
            }
            // Invalid input or a transient DPAPI failure must not erase a previously durable key.
            if (value.Length > MaximumApiKeyCharacters)
            {
                error =
                    "The API key is too long. Enter at most " +
                    MaximumApiKeyCharacters.ToString(
                        CultureInfo.InvariantCulture) +
                    " characters.";
                return false;
            }
            if (!ApiKeysEnc.ContainsKey(scope) &&
                ApiKeysEnc.Count >= MaximumApiKeyScopes)
            {
                error =
                    "DesktopPet already stores the maximum of " +
                    MaximumApiKeyScopes.ToString(
                        CultureInfo.InvariantCulture) +
                    " provider/endpoint API keys. Clear an existing key before adding another.";
                return false;
            }
            string encrypted;
            if (!TryEncryptApiKey(value, out encrypted))
            {
                error =
                    "Windows could not encrypt this API key for the current user. " +
                    "The previously saved key was left unchanged.";
                return false;
            }
            ApiKeysEnc[scope] = encrypted;
            return true;
        }

        private bool NormalizeApiKeyScopes()
        {
            bool changed = ApiKeysEnc == null;
            var normalized =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (ApiKeysEnc != null)
            {
                var keys = new List<string>(ApiKeysEnc.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string scope in keys)
                {
                    if (normalized.Count >= MaximumApiKeyScopes)
                    {
                        changed = true;
                        break;
                    }
                    string encrypted = ApiKeysEnc[scope];
                    if (!IsHex(scope, 64) ||
                        !IsWellFormedEncryptedApiKey(encrypted))
                    {
                        changed = true;
                        continue;
                    }
                    normalized.Add(scope, encrypted);
                }
            }
            if (!changed && normalized.Count == ApiKeysEnc.Count)
            {
                foreach (KeyValuePair<string, string> item in normalized)
                {
                    string existing;
                    if (!ApiKeysEnc.TryGetValue(item.Key, out existing) ||
                        !string.Equals(
                            existing,
                            item.Value,
                            StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            ApiKeysEnc = normalized;
            return changed;
        }

        private static bool TryEncryptApiKey(
            string value,
            out string encrypted)
        {
            encrypted = "";
            try
            {
                if (string.IsNullOrEmpty(value) ||
                    value.Length > MaximumApiKeyCharacters)
                    return false;
                byte[] bytes = StrictUtf8.GetBytes(value);
                byte[] protectedBytes = ProtectedData.Protect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser);
                encrypted = Convert.ToBase64String(protectedBytes);
                return encrypted.Length <= MaximumEncryptedApiKeyCharacters;
            }
            catch
            {
                encrypted = "";
                return false;
            }
        }

        private static bool TryDecryptApiKey(
            string encrypted,
            out string value)
        {
            value = "";
            try
            {
                if (string.IsNullOrEmpty(encrypted) ||
                    encrypted.Length > MaximumEncryptedApiKeyCharacters)
                    return false;
                byte[] bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(encrypted),
                    null,
                    DataProtectionScope.CurrentUser);
                if (bytes.Length > MaximumApiKeyCharacters * 4)
                    return false;
                value = StrictUtf8.GetString(bytes);
                return value.Length > 0 &&
                       value.Length <= MaximumApiKeyCharacters;
            }
            catch
            {
                value = "";
                return false;
            }
        }

        private static bool IsWellFormedEncryptedApiKey(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted) ||
                encrypted.Length > MaximumEncryptedApiKeyCharacters)
                return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(encrypted);
                return bytes.Length > 0 &&
                       bytes.Length <= MaximumEncryptedApiKeyCharacters;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHex(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length != length)
                return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                    return false;
            return true;
        }

        private static string ToHex(byte[] value)
        {
            var result = new StringBuilder(value.Length * 2);
            for (int index = 0; index < value.Length; index++)
                result.Append(
                    value[index].ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private bool NormalizeDisabledSources()
        {
            bool changed = DisabledSources == null;
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (DisabledSources != null)
            {
                foreach (string sourceValue in DisabledSources)
                {
                    if (normalized.Count >= MaximumDisabledSources)
                    {
                        changed = true;
                        break;
                    }
                    string source = sourceValue;
                    changed |= NormalizeString(
                        ref source,
                        "",
                        MaximumSourceCharacters);
                    if (string.IsNullOrWhiteSpace(source) || !seen.Add(source))
                    {
                        changed = true;
                        continue;
                    }
                    normalized.Add(source);
                }
            }
            if (!changed && DisabledSources.Count == normalized.Count)
            {
                for (int i = 0; i < normalized.Count; i++)
                    if (!string.Equals(
                            DisabledSources[i],
                            normalized[i],
                            StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
            }
            DisabledSources = normalized;
            return changed;
        }

        private static bool IsKnownProvider(string provider)
        {
            switch (provider)
            {
                case "ollama":
                case "lmstudio":
                case "llamacpp":
                case "openrouter":
                case "openai":
                case "custom":
                    return true;
                default:
                    return false;
            }
        }

        private enum ReadResult
        {
            Missing,
            Loaded,
            Unreadable,
            FutureSchema
        }
    }

    /// <summary>Bounds model identifiers and removes UI/log injection characters.</summary>
    internal static class AiModelPolicy
    {
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string candidate = value.Trim();
            if (candidate.Length < 1 ||
                candidate.Length > AiSettings.MaximumModelCharacters)
                return false;

            for (int index = 0; index < candidate.Length; index++)
            {
                char character = candidate[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= candidate.Length ||
                        !char.IsLowSurrogate(candidate[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
                else
                {
                    UnicodeCategory category = char.GetUnicodeCategory(character);
                    if (category == UnicodeCategory.Format ||
                        category == UnicodeCategory.LineSeparator ||
                        category == UnicodeCategory.ParagraphSeparator)
                        return false;
                }
            }

            normalized = candidate;
            return true;
        }

        public static string NormalizeOrThrow(string value, string parameterName)
        {
            string normalized;
            if (!TryNormalize(value, out normalized))
                throw new ArgumentException(
                    "Enter a model identifier without whitespace or control characters.",
                    parameterName);
            return normalized;
        }
    }
}
