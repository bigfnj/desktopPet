using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace DesktopPet.Ai
{
    /// <summary>One remembered exchange: a compact screen context and the pet's reply.</summary>
    internal sealed class ChatTurn
    {
        public string Context;
        public string Reply;
    }

    internal sealed class ChatHistoryDeleteResult
    {
        public bool Succeeded { get; private set; }
        public bool Pending { get; private set; }
        public string Error { get; private set; }

        internal static ChatHistoryDeleteResult Success()
        {
            return new ChatHistoryDeleteResult { Succeeded = true };
        }

        internal static ChatHistoryDeleteResult Deferred(string error)
        {
            return new ChatHistoryDeleteResult {
                Pending = true,
                Error = string.IsNullOrWhiteSpace(error)
                    ? "Deletion is pending."
                    : error
            };
        }

        internal static ChatHistoryDeleteResult Failure(string error)
        {
            return new ChatHistoryDeleteResult {
                Error = string.IsNullOrWhiteSpace(error)
                    ? "Deletion failed."
                    : error
            };
        }
    }

    /// <summary>
    /// Size-bounded, encrypted rolling conversation memory. Histories are partitioned by provider
    /// identity and coordinated across app processes so one instance cannot clobber another.
    /// </summary>
    internal sealed class ChatHistory
    {
        private const int CurrentVersion = 1;
        private const int MaxTurns = 10;
        private const int MaxPartitions = 16;
        private const int MaxContextCharacters = 256;
        private const int MaxReplyCharacters = 512;
        private const int MaxPersistedBytes = 1024 * 1024;
        private const int MaxCleartextBytes = 512 * 1024;
        private const string ProtectedPrefix = "DPH1:";
        private const string DeleteRequestSuffix = ".delete-pending";
        private const int DeleteLockAttempts = 3;
        private const int DeleteFileAttempts = 4;

        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("DesktopPet.ChatHistory.v1");
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private readonly object _lock = new object();
        private HistoryEnvelope _envelope;
        private readonly string _partition;
        private List<ChatTurn> _turns;
        private bool _writesBlockedByFutureSchema;

        private ChatHistory(
            HistoryEnvelope envelope,
            string partition,
            bool writesBlockedByFutureSchema)
        {
            _partition = partition;
            _writesBlockedByFutureSchema = writesBlockedByFutureSchema;
            _envelope = NormalizeEnvelope(envelope, partition);
            if (!_envelope.Partitions.TryGetValue(partition, out _turns))
            {
                _turns = new List<ChatTurn>();
                _envelope.Partitions[partition] = _turns;
            }
        }

        [JsonIgnore]
        public static string FilePath
        {
            get { return AppPaths.ChatHistoryFile; }
        }

        private static string DeleteRequestPath
        {
            get { return FilePath + DeleteRequestSuffix; }
        }

        /// <summary>Load the rolling history, or an empty one on first run / any error.</summary>
        public static ChatHistory Load(AiSettings settings)
        {
            string partition = PartitionKey(settings);
            using (IDisposable lease = TryAcquireFileLock())
            {
                if (lease == null)
                    return Empty(partition);

                try
                {
                    if (File.Exists(DeleteRequestPath))
                    {
                        ChatHistoryDeleteResult deletion =
                            CompletePendingDeletionNoLock();
                        if (!deletion.Succeeded)
                            return Empty(partition);
                    }

                    string path = null;
                    string content;
                    HistoryEnvelope envelope;
                    bool future;
                    bool recoveredFromBackup = false;

                    if (TryReadEnvelopeFile(
                            FilePath,
                            partition,
                            out content,
                            out envelope,
                            out future))
                    {
                        path = FilePath;
                    }
                    else if (TryReadEnvelopeFile(
                            FilePath + ".bak",
                            partition,
                            out content,
                            out envelope,
                            out future))
                    {
                        path = FilePath + ".bak";
                        recoveredFromBackup = true;
                    }
                    else if (!File.Exists(FilePath) &&
                             AppPaths.LegacyMigrationEnabled)
                    {
                        string legacy = LegacyFilePath();
                        if (TryReadEnvelopeFile(
                                legacy,
                                partition,
                                out content,
                                out envelope,
                                out future))
                            path = legacy;
                    }

                    if (path == null) return Empty(partition);

                    bool migrationNeeded =
                        recoveredFromBackup ||
                        !content.StartsWith(ProtectedPrefix, StringComparison.Ordinal) ||
                        !string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase);
                    var history = new ChatHistory(envelope, partition, future);
                    if (migrationNeeded && !future)
                    {
                        bool preserveExistingBackup =
                            recoveredFromBackup ||
                            !content.StartsWith(
                                ProtectedPrefix,
                                StringComparison.Ordinal);
                        bool migrated = history.WriteEnvelopeNoLock(
                            !preserveExistingBackup);
                        if (!migrated && recoveredFromBackup)
                            history._writesBlockedByFutureSchema = true;
                        if (migrated &&
                            !recoveredFromBackup &&
                            !string.Equals(
                                path,
                                FilePath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(path); } catch { }
                        }
                    }
                    return history;
                }
                catch
                {
                    return Empty(partition);
                }
            }
        }

        /// <summary>Recent turns as alternating user(context) / assistant(reply) messages.</summary>
        public IList<ChatMessage> RecentMessages()
        {
            var messages = new List<ChatMessage>();
            lock (_lock)
            {
                foreach (ChatTurn turn in _turns)
                {
                    if (turn == null || string.IsNullOrWhiteSpace(turn.Reply)) continue;
                    string context = string.IsNullOrWhiteSpace(turn.Context)
                        ? "(the screen)"
                        : turn.Context;
                    messages.Add(ChatMessage.User("Earlier context: " + context, null));
                    messages.Add(ChatMessage.Assistant(turn.Reply));
                }
            }
            return messages;
        }

        /// <summary>Append an exchange, trim to the window, and persist. Never throws.</summary>
        public void Add(string context, string reply)
        {
            string normalizedReply =
                NormalizeField(reply, MaxReplyCharacters);
            if (string.IsNullOrWhiteSpace(normalizedReply)) return;
            string normalizedContext =
                NormalizeField(context, MaxContextCharacters);

            lock (_lock)
            using (IDisposable lease = TryAcquireFileLock())
            {
                if (lease == null || _writesBlockedByFutureSchema) return;
                try
                {
                    if (File.Exists(DeleteRequestPath))
                    {
                        CompletePendingDeletionNoLock();
                        return;
                    }
                    bool future;
                    HistoryEnvelope latest;
                    if (!TryReadCurrentEnvelope(_partition, out latest, out future))
                        return;
                    if (future)
                    {
                        _writesBlockedByFutureSchema = true;
                        return;
                    }

                    latest = NormalizeEnvelope(latest, _partition);
                    List<ChatTurn> turns;
                    if (!latest.Partitions.TryGetValue(_partition, out turns))
                    {
                        turns = new List<ChatTurn>();
                        latest.Partitions[_partition] = turns;
                    }
                    turns.Add(new ChatTurn
                    {
                        Context = normalizedContext,
                        Reply = normalizedReply
                    });
                    Trim(turns);
                    _envelope = latest;
                    _turns = turns;
                    WriteEnvelopeNoLock();
                }
                catch { }
            }
        }

        public void Clear()
        {
            lock (_lock)
            using (IDisposable lease = TryAcquireFileLock())
            {
                if (lease == null || _writesBlockedByFutureSchema) return;
                try
                {
                    if (File.Exists(DeleteRequestPath))
                    {
                        CompletePendingDeletionNoLock();
                        return;
                    }
                    bool future;
                    HistoryEnvelope latest;
                    if (!TryReadCurrentEnvelope(_partition, out latest, out future))
                        return;
                    if (future)
                    {
                        _writesBlockedByFutureSchema = true;
                        return;
                    }
                    latest = NormalizeEnvelope(latest, _partition);
                    latest.Partitions.Remove(_partition);
                    _envelope = latest;
                    _turns = new List<ChatTurn>();
                    WriteEnvelopeNoLock();
                }
                catch { }
            }
        }

        public static ChatHistoryDeleteResult DeletePersisted()
        {
            ChatHistoryDeleteResult request = RequestPersistedDeletion();
            if (!request.Pending)
                return request;

            for (int attempt = 0; attempt < DeleteLockAttempts; attempt++)
            {
                using (IDisposable lease = TryAcquireFileLock())
                {
                    if (lease != null)
                        return CompletePendingDeletionNoLock();
                }
                if (attempt + 1 < DeleteLockAttempts)
                    Thread.Sleep(50 * (attempt + 1));
            }
            return ChatHistoryDeleteResult.Deferred(
                "Another DesktopPet instance is using chat history; deletion will retry.");
        }

        public static ChatHistoryDeleteResult RequestPersistedDeletion()
        {
            string requestError;
            return TryPersistDeleteRequest(out requestError)
                ? ChatHistoryDeleteResult.Deferred(
                    "History deletion is pending active-session retirement.")
                : ChatHistoryDeleteResult.Failure(requestError);
        }

        private bool WriteEnvelopeNoLock()
        {
            return WriteEnvelopeNoLock(true);
        }

        private bool WriteEnvelopeNoLock(bool rotateExistingToBackup)
        {
            if (_writesBlockedByFutureSchema ||
                _envelope == null ||
                _envelope.Version > CurrentVersion)
                return false;
            try
            {
                _envelope = NormalizeEnvelope(_envelope, _partition);
                if (!_envelope.Partitions.TryGetValue(_partition, out _turns))
                    _turns = new List<ChatTurn>();

                string json = Serialize(_envelope);
                byte[] clear = StrictUtf8.GetBytes(json);
                if (clear.Length > MaxCleartextBytes) return false;
                byte[] protectedBytes = ProtectedData.Protect(
                    clear,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                string content =
                    ProtectedPrefix + Convert.ToBase64String(protectedBytes);
                if (StrictUtf8.GetByteCount(content) > MaxPersistedBytes)
                    return false;
                return AtomicFile.TryWriteAllText(
                    FilePath,
                    content,
                    rotateExistingToBackup ? FilePath + ".bak" : null);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadCurrentEnvelope(
            string partition,
            out HistoryEnvelope envelope,
            out bool future)
        {
            envelope = new HistoryEnvelope();
            future = false;
            try
            {
                if (!File.Exists(FilePath)) return true;
                string content = ReadBoundedUtf8(FilePath, MaxPersistedBytes);
                envelope = ReadEnvelope(content, partition, out future);
                return true;
            }
            catch
            {
                envelope = null;
                return false;
            }
        }

        private static HistoryEnvelope ReadEnvelope(
            string content,
            string partition,
            out bool future)
        {
            future = false;
            if (content.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                string encoded = content.Substring(ProtectedPrefix.Length);
                if (encoded.Length > MaxPersistedBytes)
                    throw new InvalidDataException("Chat history exceeds its size limit.");
                byte[] protectedBytes = Convert.FromBase64String(encoded);
                if (protectedBytes.Length > MaxPersistedBytes)
                    throw new InvalidDataException("Chat history exceeds its size limit.");
                byte[] clear = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                if (clear.Length > MaxCleartextBytes)
                    throw new InvalidDataException("Chat history exceeds its cleartext limit.");
                HistoryEnvelope envelope =
                    Deserialize<HistoryEnvelope>(StrictUtf8.GetString(clear));
                envelope = envelope ?? new HistoryEnvelope();
                future = envelope.Version > CurrentVersion;
                return envelope;
            }

            // One-time migration from the historical plaintext list.
            List<ChatTurn> legacy = Deserialize<List<ChatTurn>>(content);
            var migrated = new HistoryEnvelope();
            migrated.Partitions[partition] = legacy ?? new List<ChatTurn>();
            return migrated;
        }

        private static bool TryReadEnvelopeFile(
            string path,
            string partition,
            out string content,
            out HistoryEnvelope envelope,
            out bool future)
        {
            content = null;
            envelope = null;
            future = false;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;
                content = ReadBoundedUtf8(path, MaxPersistedBytes);
                envelope = ReadEnvelope(content, partition, out future);
                return true;
            }
            catch
            {
                content = null;
                envelope = null;
                future = false;
                return false;
            }
        }

        private static HistoryEnvelope NormalizeEnvelope(
            HistoryEnvelope envelope,
            string preferredPartition)
        {
            envelope = envelope ?? new HistoryEnvelope();
            if (envelope.Version <= 0) envelope.Version = CurrentVersion;
            Dictionary<string, List<ChatTurn>> source =
                envelope.Partitions ??
                new Dictionary<string, List<ChatTurn>>(StringComparer.Ordinal);
            var normalized =
                new Dictionary<string, List<ChatTurn>>(StringComparer.Ordinal);

            AddNormalizedPartition(
                normalized,
                preferredPartition,
                source.ContainsKey(preferredPartition)
                    ? source[preferredPartition]
                    : null);
            foreach (KeyValuePair<string, List<ChatTurn>> item in source)
            {
                if (normalized.Count >= MaxPartitions) break;
                if (string.Equals(
                        item.Key,
                        preferredPartition,
                        StringComparison.Ordinal))
                    continue;
                AddNormalizedPartition(normalized, item.Key, item.Value);
            }
            envelope.Partitions = normalized;
            return envelope;
        }

        private static void AddNormalizedPartition(
            IDictionary<string, List<ChatTurn>> destination,
            string key,
            IList<ChatTurn> source)
        {
            if (destination.Count >= MaxPartitions ||
                !IsPartitionKey(key) ||
                destination.ContainsKey(key))
                return;

            var turns = new List<ChatTurn>();
            if (source != null)
            {
                int start = Math.Max(0, source.Count - MaxTurns);
                for (int i = start; i < source.Count; i++)
                {
                    ChatTurn item = source[i];
                    if (item == null) continue;
                    string reply = NormalizeField(
                        item.Reply,
                        MaxReplyCharacters);
                    if (string.IsNullOrWhiteSpace(reply)) continue;
                    turns.Add(new ChatTurn
                    {
                        Context = NormalizeField(
                            item.Context,
                            MaxContextCharacters),
                        Reply = reply
                    });
                }
            }
            destination[key] = turns;
        }

        private static void Trim(List<ChatTurn> turns)
        {
            while (turns.Count > MaxTurns) turns.RemoveAt(0);
        }

        private static bool IsPartitionKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f')))
                    return false;
            return true;
        }

        private static string NormalizeField(string value, int maximumCharacters)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var result = new StringBuilder(Math.Min(value.Length, maximumCharacters));
            bool pendingSpace = false;
            for (int index = 0; index < value.Length;)
            {
                char c = value[index++];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }

                int characterLength = 1;
                char lowSurrogate = '\0';
                if (char.IsHighSurrogate(c))
                {
                    if (index >= value.Length ||
                        !char.IsLowSurrogate(value[index]))
                        continue;
                    lowSurrogate = value[index++];
                    characterLength = 2;
                }
                else if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                int spaceLength = pendingSpace && result.Length > 0 ? 1 : 0;
                if (result.Length + spaceLength + characterLength >
                    maximumCharacters)
                    break;
                if (spaceLength != 0) result.Append(' ');
                pendingSpace = false;
                result.Append(c);
                if (characterLength == 2) result.Append(lowSurrogate);
            }
            return result.ToString().Trim();
        }

        internal static string NormalizeFieldForSelfTest(
            string value,
            int maximumCharacters)
        {
            return NormalizeField(value, maximumCharacters);
        }

        private static string ReadBoundedUtf8(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("Chat history exceeds its size limit.");
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
                                "Chat history exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }
                    return StrictUtf8.GetString(memory.ToArray());
                }
            }
        }

        private static T Deserialize<T>(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text)
            {
                MaxDepth = 32,
                DateParseHandling = DateParseHandling.None
            })
            {
                return JsonSerializer.CreateDefault().Deserialize<T>(reader);
            }
        }

        private static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Formatting.None);
        }

        private static string PartitionKey(AiSettings settings)
        {
            settings = settings ?? new AiSettings();
            string provider = LimitIdentity(settings.Provider, 32, "ollama");
            string endpoint = string.Equals(
                    provider,
                    "ollama",
                    StringComparison.OrdinalIgnoreCase)
                ? settings.Endpoint
                : settings.OpenAiBaseUrl;
            string identity =
                provider.ToLowerInvariant() + "\n" +
                NormalizeEndpointIdentity(endpoint) + "\n" +
                LimitIdentity(settings.TextModel, 256, "") + "\n" +
                LimitIdentity(settings.VisionModel, 256, "") + "\n" +
                settings.CredentialIdentity();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(StrictUtf8.GetBytes(identity));
                var key = new StringBuilder(32);
                for (int i = 0; i < 16; i++) key.Append(hash[i].ToString("x2"));
                return key.ToString();
            }
        }

        internal static string PartitionKeyForSelfTest(AiSettings settings)
        {
            return PartitionKey(settings);
        }

        private static string NormalizeEndpointIdentity(string endpoint)
        {
            string limited = LimitIdentity(endpoint, 2048, "").TrimEnd('/');
            string normalized;
            string error;
            return AiEndpointPolicy.TryNormalize(
                    limited,
                    out normalized,
                    out error)
                ? normalized
                : limited;
        }

        private static string LimitIdentity(
            string value,
            int maximumCharacters,
            string fallback)
        {
            value = (value ?? fallback).Trim();
            return value.Length <= maximumCharacters
                ? value
                : UnicodeTextProgress.TruncateAtCodePointBoundary(
                    value,
                    maximumCharacters);
        }

        internal static string LimitIdentityForSelfTest(
            string value,
            int maximumCharacters)
        {
            return LimitIdentity(value, maximumCharacters, "");
        }

        private static ChatHistory Empty(string partition)
        {
            return new ChatHistory(new HistoryEnvelope(), partition, false);
        }

        private static string LegacyFilePath()
        {
            return Path.Combine(
                AppPaths.LegacyRoamingDataRoot,
                "chat-history.json");
        }

        private static bool TryPersistDeleteRequest(out string error)
        {
            error = null;
            try
            {
                string directory = Path.GetDirectoryName(DeleteRequestPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using (var stream = new FileStream(
                    DeleteRequestPath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.SetLength(0);
                    stream.Flush(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not preserve the history deletion request: " +
                    ex.Message;
                return false;
            }
        }

        private static ChatHistoryDeleteResult CompletePendingDeletionNoLock()
        {
            var targets = new List<string> {
                FilePath,
                FilePath + ".bak"
            };
            string legacy = LegacyFilePath();
            if (AppPaths.LegacyMigrationEnabled &&
                !string.Equals(
                    legacy,
                    FilePath,
                    StringComparison.OrdinalIgnoreCase))
                targets.Add(legacy);
            return CompleteDeletionNoLock(
                targets,
                DeleteRequestPath,
                DeleteFileAttempts);
        }

        private static ChatHistoryDeleteResult CompleteDeletionNoLock(
            IList<string> targets,
            string requestPath,
            int attempts)
        {
            attempts = Math.Max(1, attempts);
            string lastError = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                bool remaining = false;
                if (targets != null)
                {
                    foreach (string target in targets)
                    {
                        if (string.IsNullOrEmpty(target)) continue;
                        try
                        {
                            if (File.Exists(target))
                                File.Delete(target);
                            if (File.Exists(target))
                            {
                                remaining = true;
                                lastError = "A history file still exists after deletion.";
                            }
                        }
                        catch (Exception ex)
                        {
                            remaining = true;
                            lastError = ex.Message;
                        }
                    }
                }

                if (!remaining)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(requestPath) &&
                            File.Exists(requestPath))
                            File.Delete(requestPath);
                        if (string.IsNullOrEmpty(requestPath) ||
                            !File.Exists(requestPath))
                            return ChatHistoryDeleteResult.Success();
                        lastError =
                            "The pending history deletion marker could not be removed.";
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }

                if (attempt + 1 < attempts)
                    Thread.Sleep(50 * (attempt + 1));
            }
            return ChatHistoryDeleteResult.Deferred(
                "Saved history could not be fully deleted: " +
                (lastError ?? "unknown file-system error") +
                " The pending request will be retried.");
        }

        internal static bool RunDeletionSelfTest(StringBuilder report)
        {
            report = report ?? new StringBuilder();
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-history-delete-selftest-" +
                Guid.NewGuid().ToString("N"));
            bool ok = true;
            FileStream contention = null;
            try
            {
                Directory.CreateDirectory(root);
                string primary = Path.Combine(root, "chat-history.json");
                string backup = primary + ".bak";
                string request = primary + DeleteRequestSuffix;
                byte[] original = Encoding.UTF8.GetBytes("history-under-contention");
                File.WriteAllBytes(primary, original);
                File.WriteAllText(backup, "backup", Encoding.UTF8);
                File.WriteAllText(request, "", Encoding.UTF8);

                contention = new FileStream(
                    primary,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                ChatHistoryDeleteResult deferred = CompleteDeletionNoLock(
                    new[] { primary, backup },
                    request,
                    2);
                bool preserved = !deferred.Succeeded &&
                    deferred.Pending &&
                    File.Exists(primary) &&
                    File.Exists(request) &&
                    ByteArraysEqual(
                        original,
                        File.ReadAllBytes(primary));
                if (!preserved)
                {
                    ok = false;
                    report.AppendLine(
                        "HISTORY DELETE FAIL contention was reported as success");
                }

                contention.Dispose();
                contention = null;
                ChatHistoryDeleteResult completed = CompleteDeletionNoLock(
                    new[] { primary, backup },
                    request,
                    2);
                if (!completed.Succeeded ||
                    File.Exists(primary) ||
                    File.Exists(backup) ||
                    File.Exists(request))
                {
                    ok = false;
                    report.AppendLine(
                        "HISTORY DELETE FAIL pending deletion did not complete");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine(
                    "HISTORY DELETE EXC: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (contention != null)
                {
                    try { contention.Dispose(); } catch { }
                }
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    ok = false;
                    report.AppendLine(
                        "HISTORY DELETE CLEANUP EXC: " + ex.Message);
                }
            }
            report.AppendLine(
                "chat_history_delete=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static IDisposable TryAcquireFileLock()
        {
            try
            {
                return CrossSessionLock.TryAcquire(
                    MutexName(),
                    FilePath,
                    2000);
            }
            catch
            {
                return null;
            }
        }

        private static string MutexName()
        {
            return CrossSessionLock.BuildGlobalMutexName(
                "ChatHistory",
                FilePath);
        }

        private sealed class HistoryEnvelope
        {
            public int Version = CurrentVersion;
            public Dictionary<string, List<ChatTurn>> Partitions =
                new Dictionary<string, List<ChatTurn>>(StringComparer.Ordinal);
        }
    }
}
