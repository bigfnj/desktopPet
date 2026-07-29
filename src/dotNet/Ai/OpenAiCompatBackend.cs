using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet.Ai
{
    /// <summary>
    /// One backend for any OpenAI-compatible <c>/v1</c> endpoint: LM Studio, llama.cpp
    /// (<c>llama-server</c>), OpenRouter, OpenAI, or a custom base URL. The base URL should include
    /// <c>/v1</c> (e.g. <c>https://openrouter.ai/api/v1</c>, <c>http://localhost:1234/v1</c>). Optional
    /// Bearer key. Chat via <c>/chat/completions</c>, vision via <c>image_url</c> content parts. These
    /// providers manage their own model lifetime, so start/warm/unload are no-ops (cloud has no local
    /// VRAM; local servers load on first request). Ollama keeps its native client for keep-alive VRAM
    /// control; everything else routes here.
    /// </summary>
    internal sealed class OpenAiCompatBackend : IPetBrainBackend
    {
        private readonly HttpClient _http;
        private readonly string _base;   // ".../v1"
        private readonly string _key;

        public OpenAiCompatBackend(string baseUrl, string apiKey, TimeSpan timeout)
        {
            _base = (baseUrl ?? "").TrimEnd('/');
            _key  = apiKey ?? "";
            _http = new HttpClient { Timeout = timeout };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopPet");
            if (!string.IsNullOrWhiteSpace(_key))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            // OpenRouter attribution headers (harmless for other providers).
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktopPet");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopPet");
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try { using (var r = await _http.GetAsync(_base + "/models", ct).ConfigureAwait(false)) return r.IsSuccessStatusCode; }
            catch { return false; }
        }

        // We don't own these servers, and cloud has nothing to warm/unload.
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return IsAvailableAsync(ct); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            JArray msgs = new JArray();
            foreach (ChatMessage m in messages)
            {
                JObject jm = new JObject { ["role"] = m.Role };
                if (m.ImagesBase64 != null && m.ImagesBase64.Length > 0)
                {
                    JArray parts = new JArray();
                    if (!string.IsNullOrEmpty(m.Content))
                        parts.Add(new JObject { ["type"] = "text", ["text"] = m.Content });
                    foreach (string b64 in m.ImagesBase64)
                        parts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64," + b64 } });
                    jm["content"] = parts;
                }
                else jm["content"] = m.Content ?? "";
                msgs.Add(jm);
            }

            JObject payload = new JObject { ["model"] = model, ["messages"] = msgs, ["stream"] = false };
            if (jsonFormat) payload["response_format"] = new JObject { ["type"] = "json_object" };

            using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
            using (var resp = await _http.PostAsync(_base + "/chat/completions", content, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject obj = JObject.Parse(json);
                JArray choices = obj["choices"] as JArray;
                if (choices != null && choices.Count > 0)
                {
                    JToken msg = choices[0]["message"];
                    if (msg != null && msg["content"] != null) return (string)msg["content"];
                }
                return "";
            }
        }

        /// <summary>List model ids from <c>/v1/models</c> for the settings dropdown. Best-effort.</summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct)
        {
            var names = new List<string>();
            try
            {
                using (var r = await _http.GetAsync(_base + "/models", ct).ConfigureAwait(false))
                {
                    if (!r.IsSuccessStatusCode) return names;
                    string json = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    JArray data = JObject.Parse(json)["data"] as JArray;
                    if (data != null)
                        foreach (var m in data) { string id = (string)m["id"]; if (!string.IsNullOrWhiteSpace(id)) names.Add(id); }
                }
            }
            catch { }
            return names;
        }

        public void Dispose() { _http.Dispose(); }
    }

    /// <summary>Provider presets for the "One Interface" — base URL + whether a key/host is needed.</summary>
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
