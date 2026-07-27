using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace DesktopPet.Ai
{
    /// <summary>One remembered exchange: a compact screen context and the pet's reply.</summary>
    internal sealed class ChatTurn
    {
        public string Context;   // compact label (e.g. active-window title), NOT the full OCR
        public string Reply;     // what the pet said
    }

    /// <summary>
    /// Rolling conversation memory (backlog 5.3/5.4). Keeps the last few exchanges so the pet has
    /// continuity — it can avoid repeating itself and reference what it saw earlier. Persisted to
    /// <c>%APPDATA%\DesktopPet\chat-history.json</c> as a rolling window. Thread-safe; never throws.
    /// </summary>
    internal sealed class ChatHistory
    {
        private const int MaxTurns = 10;   // 10 exchanges = up to 20 messages fed back to the model

        private readonly object _lock = new object();
        private readonly List<ChatTurn> _turns;

        private ChatHistory(List<ChatTurn> turns)
        {
            _turns = turns ?? new List<ChatTurn>();
            Trim();
        }

        [JsonIgnore]
        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DesktopPet", "chat-history.json");
            }
        }

        /// <summary>Load the rolling history, or an empty one on first run / any error.</summary>
        public static ChatHistory Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    List<ChatTurn> turns = JsonConvert.DeserializeObject<List<ChatTurn>>(File.ReadAllText(FilePath));
                    return new ChatHistory(turns);
                }
            }
            catch { }
            return new ChatHistory(null);
        }

        /// <summary>Recent turns as alternating user(context) / assistant(reply) messages.</summary>
        public IList<ChatMessage> RecentMessages()
        {
            List<ChatMessage> msgs = new List<ChatMessage>();
            lock (_lock)
            {
                foreach (ChatTurn t in _turns)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.Reply)) continue;
                    string ctx = string.IsNullOrWhiteSpace(t.Context) ? "(the screen)" : t.Context;
                    msgs.Add(ChatMessage.User("Earlier context: " + ctx, null));
                    msgs.Add(ChatMessage.Assistant(t.Reply));
                }
            }
            return msgs;
        }

        /// <summary>Append an exchange, trim to the window, and persist. Never throws.</summary>
        public void Add(string context, string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return;
            lock (_lock)
            {
                _turns.Add(new ChatTurn { Context = context ?? "", Reply = reply.Trim() });
                Trim();
                SaveNoLock();
            }
        }

        private void Trim()
        {
            while (_turns.Count > MaxTurns) _turns.RemoveAt(0);
        }

        private void SaveNoLock()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_turns, Formatting.Indented));
            }
            catch { }
        }
    }
}
