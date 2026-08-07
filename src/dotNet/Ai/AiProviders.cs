using System;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Provider presets for the "One Interface" — base URL + whether a key/host is needed. Extracted from
    /// OpenAiCompatBackend.cs in S4b when the AI-brain backends moved to the AiBrain module; the base keeps
    /// this because <see cref="AiSettings"/> still uses it for provider-scoped endpoint/credential handling
    /// (fully retired with the AiSettings split in S5). No behavior change — the presets are unchanged.
    /// </summary>
    internal static class AiProviders
    {
        public struct Preset { public string Id, Name, BaseUrl; public bool NeedsKey, IsLocal; }

        public static readonly Preset[] All =
        {
            new Preset { Id="ollama",    Name="Ollama (local)",         BaseUrl="http://localhost:11434", NeedsKey=false, IsLocal=true  },
            new Preset { Id="lmstudio",  Name="LM Studio (local)",      BaseUrl="http://localhost:1234/v1", NeedsKey=false, IsLocal=true },
            new Preset { Id="llamacpp",  Name="llama.cpp (local)",      BaseUrl="http://localhost:8080/v1", NeedsKey=false, IsLocal=true },
            new Preset { Id="openrouter",Name="OpenRouter (cloud)",     BaseUrl="https://openrouter.ai/api/v1", NeedsKey=true, IsLocal=false },
            new Preset { Id="openai",    Name="OpenAI (cloud)",         BaseUrl="https://api.openai.com/v1", NeedsKey=true, IsLocal=false },
            new Preset { Id="custom",    Name="Custom (OpenAI-compat)", BaseUrl="", NeedsKey=false, IsLocal=false },
        };

        public static Preset Get(string id)
        {
            foreach (var p in All) if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return All[0];
        }
    }
}
