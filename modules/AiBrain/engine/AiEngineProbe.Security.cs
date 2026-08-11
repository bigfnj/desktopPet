using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Ai;

namespace DesktopPet.AiBrainModule
{
    /// <summary>
    /// Relocated AI SECURITY assertions. These were ported ~verbatim from the base
    /// <c>SecuritySelfTest.cs</c> so they exercise the SHIPPING module engine (DesktopPet.Ai.* — the
    /// module's own copies) instead of the base's about-to-be-deleted duplicate. Every reject/failure
    /// invariant still drives its reject/failure path; nothing was weakened. Runs headless: no live LLM,
    /// no network, and all file/DPAPI writes are isolated under throwaway temp roots (AiPaths.SetRoot).
    /// </summary>
    public static partial class AiEngineProbe
    {
        // Mirrors the base's Newtonsoft Formatting.Indented for the throwaway ai-settings.json the DPAPI-
        // failure assertions inject: WriteIndented + the relaxed encoder keep the base64 ciphertext literal.
        // IncludeFields serializes the settings' public fields exactly as the store persists them.
        private static readonly JsonSerializerOptions ProbeJson = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        internal static bool RunSecurity(StringBuilder sb)
        {
            bool ok = true;

            // --- endpoint policy rejects (loopback + cloud ACCEPTS are asserted in AiEngineProbe.Run) ---
            // The two IP-loopback ACCEPT call sites from the base (127.0.0.1 / [::1]) exercise the
            // IPAddress.IsLoopback path (distinct from localhost's Uri.IsLoopback), so they are ported too.
            ok &= CheckEndpoint(sb, "http://127.0.0.1:8080/v1", true);
            ok &= CheckEndpoint(sb, "http://[::1]:8080/v1", true);
            ok &= CheckEndpoint(sb, "http://example.com/v1", false);
            ok &= CheckEndpoint(sb, "http://192.168.1.20/v1", false);
            ok &= CheckEndpoint(sb, "ftp://localhost/model", false);
            ok &= CheckEndpoint(sb, "https://user:password@example.com/v1", false);
            ok &= CheckEndpoint(sb, "https://example.com/v1?token=secret", false);

            ok &= CheckAiSettingsPersistence(sb);
            ok &= CheckAiCredentialScoping(sb);
            ok &= CheckAiNormalization(sb);
            ok &= CheckAiResponseBounds(sb);
            ok &= CheckAiResponseDeadline(sb);
            ok &= CheckOllamaStartupDeadline(sb);
            ok &= CheckAiHttpStatusPolicy(sb);
            ok &= CheckAiRetirementBound(sb);
            ok &= CheckAiReconfigureDisposeRace(sb);
            ok &= CheckAiAfterRetireDurability(sb);

            return ok;
        }

        private static bool CheckEndpoint(StringBuilder sb, string value, bool expected)
        {
            string normalized;
            string error;
            bool actual = AiEndpointPolicy.TryNormalize(value, out normalized, out error);
            return Check(sb, "endpoint policy: " + value, actual == expected);
        }

        private static bool CheckAiSettingsPersistence(StringBuilder sb)
        {
            bool ok = true;
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-ai-settings-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                // The module resolves ai-settings.json / chat-history.json under AiPaths.Root; point it at
                // this throwaway directory (the base used the DESKTOPPET_DATA_ROOT override instead).
                AiPaths.SetRoot(directory);
                string path = AiSettings.FilePath;
                File.WriteAllText(
                    path,
                    "{\n" +
                    "  \"SchemaVersion\": 1,\n" +
                    "  \"TimeoutSeconds\": 120,\n" +
                    "  \"MemoryEnabled\": false,\n" +
                    "  \"futureSameSchema\": { \"keep\": true }\n" +
                    "}",
                    new UTF8Encoding(false));

                AiSettings first = AiSettings.Load();
                AiSettings second = AiSettings.Load();
                first.TimeoutSeconds = 77;
                bool firstSaved = first.Save();
                second.MemoryEnabled = true;
                bool secondSaved = second.Save();
                JsonObject merged = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings stale writers merge and preserve unknown fields",
                    firstSaved &&
                    secondSaved &&
                    (int)merged["TimeoutSeconds"] == 77 &&
                    (bool)merged["MemoryEnabled"] &&
                    (bool)merged["futureSameSchema"]["keep"]);

                AiSettings keyWriterA = AiSettings.Load();
                AiSettings keyWriterB = AiSettings.Load();
                keyWriterA.Provider = "openai";
                keyWriterA.OpenAiBaseUrl = "https://api.openai.com/v1";
                keyWriterA.ApiKey = "stale-writer-openai-key";
                keyWriterB.Provider = "openrouter";
                keyWriterB.OpenAiBaseUrl = "https://openrouter.ai/api/v1";
                keyWriterB.ApiKey = "stale-writer-router-key";
                bool keyWriterASaved = keyWriterA.Save();
                bool keyWriterBSaved = keyWriterB.Save();
                AiSettings mergedKeys = AiSettings.Load();
                bool routerKeyPreserved =
                    mergedKeys.ApiKey == "stale-writer-router-key";
                mergedKeys.Provider = "openai";
                mergedKeys.OpenAiBaseUrl = "https://api.openai.com/v1";
                ok &= Check(
                    sb,
                    "AI settings stale writers merge provider-scoped keys",
                    keyWriterASaved &&
                    keyWriterBSaved &&
                    routerKeyPreserved &&
                    mergedKeys.ApiKey == "stale-writer-openai-key");

                const string customEndpoint =
                    "https://gateway.example/TenantA/v1";
                const string customKey =
                    "custom-endpoint-key-do-not-persist";
                AiSettings customSettings = AiSettings.Load();
                customSettings.SelectProviderEndpoint("custom", true);
                customSettings.UpdateSelectedProviderEndpoint(customEndpoint);
                customSettings.ApiKey = customKey;
                string openAiEndpoint =
                    customSettings.SelectProviderEndpoint("openai", true);
                string restoredCustomEndpoint =
                    customSettings.SelectProviderEndpoint("custom", true);
                bool customSaved = customSettings.Save();
                AiSettings customReloaded = AiSettings.Load();
                ok &= Check(
                    sb,
                    "Custom provider endpoint and scoped key survive switching and reload",
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
                    customReloaded.ApiKey == customKey);

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
                ok &= Check(
                    sb,
                    "UI-budgeted AI settings save rejects a held lock promptly",
                    boundedSaveRejected &&
                    lockWait.Elapsed < TimeSpan.FromSeconds(2));

                string undecryptable =
                    Convert.ToBase64String(new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 });
                string openAiScope = AiSettings.BuildCredentialScope(
                    "openai",
                    "https://api.openai.com/v1");
                byte[] normalizedPrimary = File.ReadAllBytes(path);
                byte[] normalizedBackup = File.ReadAllBytes(path + ".bak");

                JsonObject scopedFailure = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                scopedFailure["Provider"] = "openai";
                scopedFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                scopedFailure["ApiKeyEnc"] = "";
                scopedFailure["ApiKeysEnc"] = new JsonObject {
                    [openAiScope] = undecryptable
                };
                File.WriteAllText(
                    path,
                    scopedFailure.ToJsonString(ProbeJson),
                    new UTF8Encoding(false));
                byte[] scopedBeforeLoad = File.ReadAllBytes(path);
                AiSettings scopedLoaded = AiSettings.Load();
                JsonObject scopedAfterLoad = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings preserve provider-scoped ciphertext on DPAPI failure",
                    string.IsNullOrEmpty(scopedLoaded.ApiKey) &&
                    string.Equals(
                        (string)scopedAfterLoad["ApiKeysEnc"][openAiScope],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(scopedBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")));

                JsonObject legacyFailure = JsonNode.Parse(
                    Encoding.UTF8.GetString(normalizedPrimary)).AsObject();
                legacyFailure["Provider"] = "openai";
                legacyFailure["OpenAiBaseUrl"] = "https://api.openai.com/v1";
                legacyFailure["ApiKeysEnc"] = new JsonObject();
                legacyFailure["ApiKeyEnc"] = undecryptable;
                File.WriteAllText(
                    path,
                    legacyFailure.ToJsonString(ProbeJson),
                    new UTF8Encoding(false));
                byte[] legacyBeforeLoad = File.ReadAllBytes(path);
                AiSettings legacyLoaded = AiSettings.Load();
                JsonObject legacyAfterLoad = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings preserve legacy ciphertext on DPAPI failure",
                    string.IsNullOrEmpty(legacyLoaded.ApiKey) &&
                    string.Equals(
                        (string)legacyAfterLoad["ApiKeyEnc"],
                        undecryptable,
                        StringComparison.Ordinal) &&
                    ByteArraysEqual(legacyBeforeLoad, File.ReadAllBytes(path)) &&
                    ByteArraysEqual(normalizedBackup, File.ReadAllBytes(path + ".bak")));
                File.WriteAllBytes(path, normalizedPrimary);

                string backupPath = path + ".bak";
                byte[] validBackup = File.ReadAllBytes(backupPath);
                JsonObject expectedBackup = JsonNode.Parse(
                    File.ReadAllText(backupPath, Encoding.UTF8)).AsObject();
                File.WriteAllText(
                    path,
                    "{ corrupt primary",
                    new UTF8Encoding(false));
                AiSettings recovered = AiSettings.Load();
                JsonObject repairedPrimary = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8)).AsObject();
                ok &= Check(
                    sb,
                    "AI settings corrupt-primary recovery preserves the valid backup",
                    recovered.TimeoutSeconds == (int)expectedBackup["TimeoutSeconds"] &&
                    (int)repairedPrimary["TimeoutSeconds"] ==
                        (int)expectedBackup["TimeoutSeconds"] &&
                    ByteArraysEqual(validBackup, File.ReadAllBytes(backupPath)));

                ChatHistory.DeletePersisted();
                var historySettings = new AiSettings
                {
                    Provider = "ollama",
                    Endpoint = "http://localhost:11434",
                    TextModel = "history-test",
                    VisionModel = "history-vision"
                };
                ChatHistory history = ChatHistory.Load(historySettings);
                history.Add("first context", "first reply");
                history.Add("second context", "second reply");
                string historyPath = ChatHistory.FilePath;
                string historyBackupPath = historyPath + ".bak";
                byte[] historyBackup = File.ReadAllBytes(historyBackupPath);
                File.WriteAllText(
                    historyPath,
                    "corrupt encrypted history",
                    new UTF8Encoding(false));
                ChatHistory recoveredHistory =
                    ChatHistory.Load(historySettings);
                IList<ChatMessage> recoveredMessages =
                    recoveredHistory.RecentMessages();
                bool recoveredFirstTurn =
                    recoveredMessages.Count == 2 &&
                    recoveredMessages[1].Content == "first reply";
                bool historyBackupPreserved =
                    ByteArraysEqual(
                        historyBackup,
                        File.ReadAllBytes(historyBackupPath));
                recoveredHistory.Add("third context", "third reply");
                IList<ChatMessage> persistedMessages =
                    ChatHistory.Load(historySettings).RecentMessages();
                ok &= Check(
                    sb,
                    "chat history recovers from backup and remains writable",
                    recoveredFirstTurn &&
                    historyBackupPreserved &&
                    persistedMessages.Count == 4 &&
                    persistedMessages[3].Content == "third reply");

                string future =
                    "{\n  \"SchemaVersion\": 99,\n" +
                    "  \"TimeoutSeconds\": 42,\n" +
                    "  \"futureOnly\": true\n}";
                File.WriteAllText(path, future, new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                AiSettings futureSettings = AiSettings.Load();
                futureSettings.TimeoutSeconds = 50;
                bool blocked = !futureSettings.Save();
                byte[] after = File.ReadAllBytes(path);
                ok &= Check(
                    sb,
                    "AI settings future schema remains byte-for-byte untouched",
                    blocked && ByteArraysEqual(before, after));
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI settings persistence self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
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
                    ok &= Check(sb, "AI settings persistence self-test cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckAiCredentialScoping(StringBuilder sb)
        {
            bool ok = true;
            const string openAiKey = "selftest-openai-key-do-not-persist";
            const string routerKey = "selftest-router-key-do-not-persist";
            const string customKey = "selftest-custom-key-do-not-persist";
            try
            {
                var settings = new AiSettings
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
                ok &= Check(
                    sb,
                    "API keys are isolated by provider and endpoint",
                    openAiStored &&
                    providerIsolated &&
                    customEndpointIsolated &&
                    openAiRestored &&
                    customRestored &&
                    routerRestored);

                var credentialA = new AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                credentialA.ApiKey = openAiKey;
                var credentialB = new AiSettings
                {
                    Provider = "openai",
                    OpenAiBaseUrl = "https://api.openai.com/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                credentialB.ApiKey = routerKey;
                string partitionA =
                    ChatHistory.PartitionKeyForSelfTest(credentialA);
                string partitionB =
                    ChatHistory.PartitionKeyForSelfTest(credentialB);
                ok &= Check(
                    sb,
                    "chat history is partitioned by credential identity",
                    !string.Equals(
                        partitionA,
                        partitionB,
                        StringComparison.Ordinal));

                var pathCaseA = new AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/TenantA/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                var pathCaseB = new AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/tenanta/v1",
                    TextModel = "Model-A",
                    VisionModel = "Vision-A"
                };
                var modelCaseB = new AiSettings
                {
                    Provider = "custom",
                    OpenAiBaseUrl = "https://gateway.example/TenantA/v1",
                    TextModel = "model-a",
                    VisionModel = "Vision-A"
                };
                string pathPartitionA =
                    ChatHistory.PartitionKeyForSelfTest(pathCaseA);
                ok &= Check(
                    sb,
                    "history identity preserves endpoint-path and model casing",
                    pathPartitionA !=
                        ChatHistory.PartitionKeyForSelfTest(pathCaseB) &&
                    pathPartitionA !=
                        ChatHistory.PartitionKeyForSelfTest(modelCaseB));

                string serialized =
                    JsonSerializer.Serialize(credentialA, ProbeJson);
                string scope = AiSettings.BuildCredentialScope(
                    credentialA.Provider,
                    credentialA.OpenAiBaseUrl);
                ok &= Check(
                    sb,
                    "credential scope, persistence, and history identity omit plaintext keys",
                    serialized.IndexOf(openAiKey, StringComparison.Ordinal) < 0 &&
                    scope.IndexOf(openAiKey, StringComparison.Ordinal) < 0 &&
                    partitionA.IndexOf(openAiKey, StringComparison.Ordinal) < 0);

                var boundedCredentials = new AiSettings
                {
                    Provider = "custom"
                };
                bool admittedAllScopes = true;
                string admissionError = "";
                for (int index = 0;
                    index < AiSettings.MaximumApiKeyScopes;
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
                ok &= Check(
                    sb,
                    "API key scope limit rejects new keys explicitly and permits updates",
                    admittedAllScopes &&
                    overflowRejected &&
                    boundedCredentials.ApiKeysEnc.Count ==
                        AiSettings.MaximumApiKeyScopes &&
                    !string.IsNullOrWhiteSpace(admissionError) &&
                    existingScopeUpdated);

                string emoji = char.ConvertFromUtf32(0x1F642);
                string history256 = ChatHistory.NormalizeFieldForSelfTest(
                    new string('a', 255) + emoji,
                    256);
                string history512 = ChatHistory.NormalizeFieldForSelfTest(
                    new string('a', 511) + emoji,
                    512);
                string identity256 = ChatHistory.LimitIdentityForSelfTest(
                    new string('a', 255) + emoji,
                    256);
                ok &= Check(
                    sb,
                    "chat-history field and identity truncation preserve surrogate pairs",
                    history256.Length == 255 &&
                    history512.Length == 511 &&
                    identity256.Length == 255 &&
                    IsWellFormedUtf16(history256) &&
                    IsWellFormedUtf16(history512) &&
                    IsWellFormedUtf16(identity256));
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI credential scoping self-test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            return ok;
        }

        private static bool CheckAiNormalization(StringBuilder sb)
        {
            bool ok = true;
            var settings = new AiSettings
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

            ok &= Check(sb, "AI settings schema normalized",
                settings.SchemaVersion == AiSettings.CurrentSchemaVersion);
            ok &= Check(sb, "AI settings strings bounded",
                settings.Endpoint.Length <= 2048 &&
                settings.TextModel.Length <= 256 &&
                settings.VisionModel.Length <= 256 &&
                settings.TesseractPath.Length <= 1024 &&
                settings.PetName.Length <= 80 &&
                settings.UserName.Length <= 80 &&
                settings.Personality.Length <= 512);
            ok &= Check(sb, "AI settings values clamped",
                settings.Provider == "ollama" &&
                settings.TimeoutSeconds == 600 &&
                settings.IdleMinSeconds == 15 &&
                settings.IdleMaxSeconds == 3600 &&
                settings.IdleChangeThresholdPercent == 100);
            ok &= Check(sb, "AI disabled-source list bounded",
                settings.DisabledSources.Count == 128);

            string normalizedModel;
            ok &= Check(
                sb,
                "AI model identifiers bounded and sanitized",
                AiModelPolicy.TryNormalize(" owner/model:tag ", out normalizedModel) &&
                normalizedModel == "owner/model:tag" &&
                !AiModelPolicy.TryNormalize("model\r\ninjected", out normalizedModel));
            ok &= Check(
                sb,
                "relative configured AI executable rejected",
                AiExecutablePolicy.ResolveConfigured(
                    "ollama.exe",
                    "ollama.exe") == null);
            ok &= CheckAiExecutablePathPolicy(sb);

            settings.Provider = "openai";
            settings.OpenAiBaseUrl = "https://api.openai.com/v1";
            string apiKeyError;
            bool oversizedApiKeyRejected =
                !settings.TrySetApiKey(
                    new string('k', 9000),
                    out apiKeyError);
            ok &= Check(
                sb,
                "oversized API key rejected",
                oversizedApiKeyRejected &&
                !string.IsNullOrWhiteSpace(apiKeyError) &&
                string.IsNullOrEmpty(settings.ApiKeyEnc) &&
                settings.ApiKeysEnc.Count == 0);
            return ok;
        }

        private static bool CheckAiExecutablePathPolicy(StringBuilder sb)
        {
            bool ok = true;
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

                ok &= Check(
                    sb,
                    "local absolute AI executable paths remain supported",
                    string.Equals(
                        AiExecutablePolicy.ResolveConfigured(
                            executable,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        AiExecutablePolicy.ResolveFromPath(
                            pathWithRemotePrefix,
                            "ollama.exe"),
                        canonical,
                        StringComparison.OrdinalIgnoreCase) &&
                    AiExecutablePolicy.IsReparseFreeLocalFile(
                        executable));

                ok &= Check(
                    sb,
                    "UNC and device AI executable paths rejected before probing",
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\server.invalid\share\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\?\C:\Apps\Ollama\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\\.\C:\Apps\Ollama\ollama.exe") &&
                    !AiExecutablePolicy.IsLocalAbsolutePath(
                        @"\??\C:\Apps\Ollama\ollama.exe") &&
                    AiExecutablePolicy.ResolveConfigured(
                        @"\\server.invalid\share\ollama.exe",
                        "ollama.exe") == null);
                ok &= Check(
                    sb,
                    "AI executable reparse point rejected before descendant probing",
                    AiExecutablePolicy
                        .ReparseScanStopsBeforeTraversalForDiagnostics());
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI executable path policy regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
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
                    ok &= Check(sb, "AI executable path policy cleanup", false);
                }
            }
            return ok;
        }

        private static bool CheckAiResponseBounds(StringBuilder sb)
        {
            bool ok = true;
            string sanitized = AiBrain.SanitizeResponseText(
                " hello\r\n" + new string('x', 700) + "\0tail ");
            ok &= Check(sb, "assistant response text bounded and sanitized",
                sanitized.Length <= 512 &&
                sanitized.IndexOf('\r') < 0 &&
                sanitized.IndexOf('\n') < 0 &&
                sanitized.IndexOf('\0') < 0);

            string astral = char.ConvertFromUtf32(0x1F642);
            string boundary = AiBrain.SanitizeResponseText(
                new string('x', 511) + astral + "tail");
            string exact = AiBrain.SanitizeResponseText(
                new string('x', 510) + astral + "tail");
            ok &= Check(
                sb,
                "assistant response truncation preserves surrogate pairs",
                boundary.Length == 511 &&
                exact.Length == 512 &&
                IsWellFormedUtf16(boundary) &&
                IsWellFormedUtf16(exact));

            byte[] oversized = Encoding.UTF8.GetBytes(new string('a', 2048));
            using (var content = new ByteArrayContent(oversized))
            {
                content.Headers.ContentLength = 1;
                ok &= Check(sb, "chunked/misleading AI response rejected",
                    Throws<InvalidDataException>(delegate
                    {
                        AiEndpointPolicy.ReadResponseStringAsync(
                            content,
                            CancellationToken.None,
                            1024).GetAwaiter().GetResult();
                    }));
            }

            using (var invalidUtf8 = new ByteArrayContent(new byte[] { 0xC3, 0x28 }))
            {
                invalidUtf8.Headers.ContentLength = null;
                ok &= Check(sb, "invalid UTF-8 AI response rejected",
                    Throws<DecoderFallbackException>(delegate
                    {
                        AiEndpointPolicy.ReadResponseStringAsync(
                            invalidUtf8,
                            CancellationToken.None,
                            1024).GetAwaiter().GetResult();
                    }));
            }
            return ok;
        }

        private static bool CheckAiResponseDeadline(StringBuilder sb)
        {
            bool ok = true;
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
                    AiEndpointPolicy.SendAndReadResponseStringAsync(
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
            ok &= Check(
                sb,
                "AI deadline bounds cancellation-ignoring response reads and disposes them",
                bodyTimedOut &&
                bodyHandler.StreamDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5));

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
                    AiEndpointPolicy.SendAndReadResponseStringAsync(
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
            ok &= Check(
                sb,
                "AI deadline bounds cancellation-ignoring response stream acquisition",
                streamAcquisitionTimedOut &&
                streamHandler.ContentDisposed &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            return ok;
        }

        private static bool CheckOllamaStartupDeadline(StringBuilder sb)
        {
            bool ok = true;
            var boundedHandler = new FirstUnavailableThenBlockingHandler();
            bool boundedResult = true;
            bool boundedCompleted = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (var client = new OllamaClient(
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
                ok &= Check(
                    sb,
                    "Ollama startup deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "Ollama startup uses short probes within one overall deadline",
                boundedCompleted &&
                !boundedResult &&
                boundedHandler.RequestCount >= 2 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

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
                using (var client = new OllamaClient(
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
                ok &= Check(
                    sb,
                    "Ollama blocking-starter deadline regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
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
            ok &= Check(
                sb,
                "Ollama overall deadline bounds a synchronous blocking starter",
                blockingStarterCompleted &&
                !blockingStarterResult &&
                blockingStarterEntered &&
                blockingStarterExited &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

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
                using (var client = new OllamaClient(
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
                ok &= Check(
                    sb,
                    "Ollama late-starter cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
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
            ok &= Check(
                sb,
                "Ollama cancellation prevents a queued starter's late launch",
                lateStarterCanceled &&
                lateStarterFinished &&
                Volatile.Read(ref lateStarterObservedCancellation) == 1 &&
                Volatile.Read(ref lateLaunchCount) == 0 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));

            bool callerCancellationObserved = false;
            stopwatch.Restart();
            try
            {
                using (var callerHandler = new BlockingHeadersHandler())
                using (var callerCancellation = new CancellationTokenSource())
                using (var client = new OllamaClient(
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
                ok &= Check(
                    sb,
                    "Ollama caller-cancellation regression: " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            stopwatch.Stop();
            ok &= Check(
                sb,
                "Ollama startup preserves caller cancellation",
                callerCancellationObserved &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(3));
            return ok;
        }

        private static bool CheckAiHttpStatusPolicy(StringBuilder sb)
        {
            bool ok = true;
            using (var badRequest = new HttpResponseMessage(HttpStatusCode.BadRequest))
            {
                bool deterministic = false;
                try { AiEndpointPolicy.EnsureSuccess(badRequest); }
                catch (AiBackendHttpException ex)
                {
                    deterministic = ex.StatusCode == 400 && !ex.IsTransient;
                }
                ok &= Check(sb, "HTTP 400 is not retryable", deterministic);
            }

            using (var throttled = new HttpResponseMessage((HttpStatusCode)429))
            {
                bool transient = false;
                try { AiEndpointPolicy.EnsureSuccess(throttled); }
                catch (AiBackendHttpException ex)
                {
                    transient = ex.StatusCode == 429 && ex.IsTransient;
                }
                ok &= Check(sb, "HTTP 429 is retryable", transient);
            }

            using (var redirect = new HttpResponseMessage(HttpStatusCode.Redirect))
            {
                bool deterministicRedirect = false;
                try { AiEndpointPolicy.EnsureSuccess(redirect); }
                catch (AiBackendHttpException ex)
                {
                    deterministicRedirect = ex.StatusCode == 302 && !ex.IsTransient;
                }
                ok &= Check(
                    sb,
                    "AI redirect rejected as non-retryable before credential forwarding",
                    deterministicRedirect);
            }

            var backend = new DeterministicFailureBackend();
            bool failedWithoutRetry = Throws<AiBackendHttpException>(delegate
            {
                AiBrain.ChatWithRetryForDiagnosticsAsync(
                    backend,
                    "model",
                    new List<ChatMessage>
                    {
                        ChatMessage.User("test", null)
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            });
            ok &= Check(
                sb,
                "deterministic AI failure was not retried",
                failedWithoutRetry && backend.ChatCalls == 1);
            return ok;
        }

        private static bool CheckAiRetirementBound(StringBuilder sb)
        {
            bool ok = true;
            var manager = new AiSessionManager();
            try
            {
                var settings = new AiSettings
                {
                    TextModel = "text-model",
                    VisionModel = "vision-model"
                };
                manager.ReconfigureAsync(
                    delegate
                    {
                        return new AiBrain(
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
                ok &= Check(sb, "cancellation-ignoring AI retirement bounded",
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "AI retirement test threw " + ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiReconfigureDisposeRace(StringBuilder sb)
        {
            bool ok = true;
            var admitted = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var manager = new AiSessionManager();
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
                ok &= Check(
                    sb,
                    "AI reconfiguration returns false when disposal wins before semaphore wait",
                    reachedAdmission &&
                    completed &&
                    reconfigure.Status == TaskStatus.RanToCompletion &&
                    !reconfigure.Result);
            }
            catch (Exception ex)
            {
                ok &= Check(
                    sb,
                    "AI reconfigure/dispose race threw " +
                        ex.GetType().Name + ": " + ex.Message,
                    false);
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
            return ok;
        }

        private static bool CheckAiAfterRetireDurability(StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckAiAfterRetireSupersession(sb);
            ok &= CheckAiAfterRetireMultipleSupersessions(sb);
            ok &= CheckAiAfterRetireNormalDispose(sb);
            ok &= CheckAiAfterRetireDeferredDispose(sb);
            return ok;
        }

        private static bool CheckAiAfterRetireSupersession(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
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

                ok &= Check(
                    sb,
                    "after-retire action survives a superseding generation",
                    supersededWhileHeld &&
                    callbackCount == 1 &&
                    observedRetired &&
                    observedSerialized);
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireMultipleSupersessions(StringBuilder sb)
        {
            bool ok = true;
            var manager = new AiSessionManager();
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

                ok &= Check(
                    sb,
                    "after-retire actions survive multiple superseding generations exactly once",
                    firstCount == 1 &&
                    secondCount == 1 &&
                    firstOrder == 1 &&
                    secondOrder == 2 &&
                    callbacksSerialized);
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire multi-supersession test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireNormalDispose(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
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

                    ok &= Check(
                        sb,
                        "normal dispose drains pending after-retire actions",
                        canceledWhileHeld &&
                        callbackCount == 1 &&
                        observedRetired &&
                        observedSerialized);
                }
            }
            catch (Exception ex)
            {
                ok &= Check(sb,
                    "after-retire normal-dispose test threw " +
                    ex.GetType().Name + ": " + ex.Message,
                    false);
            }
            finally
            {
                if (held) operation.Release();
                manager.Dispose();
            }
            return ok;
        }

        private static bool CheckAiAfterRetireDeferredDispose(StringBuilder sb)
        {
            bool ok = true;
            RetirementTrackingBackend backend;
            AiSessionManager manager = CreateRetirementTestManager(out backend);
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

                        ok &= Check(
                            sb,
                            "deferred dispose drains pending after-retire actions",
                            canceledWhileHeld &&
                            deferredCompleted &&
                            callbackCount == 1 &&
                            observedRetired &&
                            observedSerialized);
                    }
                }
                catch (Exception ex)
                {
                    ok &= Check(sb,
                        "after-retire deferred-dispose test threw " +
                        ex.GetType().Name + ": " + ex.Message,
                        false);
                }
                finally
                {
                    if (held) operation.Release();
                    manager.Dispose();
                }
            }
            return ok;
        }

        private static AiSessionManager CreateRetirementTestManager(
            out RetirementTrackingBackend backend)
        {
            var manager = new AiSessionManager();
            var createdBackend = new RetirementTrackingBackend();
            var settings = new AiSettings
            {
                TextModel = "retirement-model",
                VisionModel = "retirement-model"
            };
            manager.ReconfigureAsync(
                delegate
                {
                    return new AiBrain(createdBackend, settings);
                },
                true,
                false,
                CancellationToken.None).GetAwaiter().GetResult();
            backend = createdBackend;
            return manager;
        }

        private static SemaphoreSlim GetManagerOperation(
            AiSessionManager manager)
        {
            FieldInfo field = typeof(AiSessionManager).GetField(
                "_operation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(
                    typeof(AiSessionManager).FullName,
                    "_operation");
            return (SemaphoreSlim)field.GetValue(manager);
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
    }
}
