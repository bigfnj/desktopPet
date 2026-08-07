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
        private readonly TimeSpan _deadline;

        public OpenAiCompatBackend(string baseUrl, string apiKey, TimeSpan timeout)
        {
            _base = AiEndpointPolicy.NormalizeOrThrow(baseUrl, "baseUrl");
            _key  = apiKey ?? "";
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _http = new HttpClient(AiEndpointPolicy.CreateNoRedirectHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _http.DefaultRequestHeaders.Add("User-Agent", "DesktopPet");
            // OpenRouter attribution headers (harmless for other providers).
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bigfnj/desktopPet");
            _http.DefaultRequestHeaders.Add("X-Title", "DesktopPet");
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                using (var request = CreateRequest(HttpMethod.Get, "/models"))
                {
                    return await AiEndpointPolicy.SendAndCheckSuccessAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        // We don't own these servers, and cloud has nothing to warm/unload.
        public Task<bool> EnsureServerAsync(CancellationToken ct) { return IsAvailableAsync(ct); }
        public Task WarmUpAsync(string model, CancellationToken ct) { return Task.CompletedTask; }
        public Task UnloadAsync(string model, CancellationToken ct) { return Task.CompletedTask; }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            string normalizedModel = AiModelPolicy.NormalizeOrThrow(model, "model");
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

            JObject payload = new JObject
            {
                ["model"] = normalizedModel,
                ["messages"] = msgs,
                ["stream"] = false
            };
            if (jsonFormat) payload["response_format"] = new JObject { ["type"] = "json_object" };

            using (var request = CreateRequest(HttpMethod.Post, "/chat/completions"))
            {
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                    _http,
                    request,
                    _deadline,
                    ct).ConfigureAwait(false);
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

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, _base + relativePath);
            if (!string.IsNullOrWhiteSpace(_key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            return request;
        }

        public void Dispose() { _http.Dispose(); }
    }
}
