using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet.Ai
{
    /// <summary>
    /// <see cref="IPetBrainBackend"/> over Ollama's native REST API (<c>POST /api/chat</c>).
    /// Non-streaming; the request JSON is built by hand so we control the vision "images" array.
    /// A single <see cref="HttpClient"/> is reused for the client's lifetime.
    /// </summary>
    internal sealed class OllamaClient : IPetBrainBackend
    {
        private readonly string _endpoint;
        private readonly string _exePath;
        private readonly HttpClient _http;

        public OllamaClient(string endpoint, TimeSpan timeout, string exePath)
        {
            _endpoint = (string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint).TrimEnd('/');
            _exePath = exePath;
            _http = new HttpClient { Timeout = timeout };
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                using (HttpResponseMessage resp = await _http.GetAsync(_endpoint + "/api/tags", ct).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            if (await IsAvailableAsync(ct).ConfigureAwait(false)) return true;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ResolveOllamaExe(),
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);   // long-lived server; intentionally not awaited or redirected
            }
            catch
            {
                return false;   // exe not found / can't launch
            }

            // Poll until the server answers (up to ~20s).
            for (int i = 0; i < 40; i++)
            {
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { }
                if (await IsAvailableAsync(ct).ConfigureAwait(false)) return true;
            }
            return false;
        }

        public async Task WarmUpAsync(string model, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            try
            {
                // No "prompt" -> Ollama just loads the model into memory (done_reason: "load").
                JObject payload = new JObject
                {
                    ["model"] = model,
                    ["stream"] = false,
                    ["keep_alive"] = "10m"
                };
                using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (HttpResponseMessage resp = await _http.PostAsync(_endpoint + "/api/generate", content, ct).ConfigureAwait(false))
                {
                    // Body ignored; a successful response means the model is now resident.
                }
            }
            catch { }
        }

        /// <summary>Evict a model from memory/VRAM immediately (keep_alive: 0). Best-effort; never throws.</summary>
        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            try
            {
                JObject payload = new JObject { ["model"] = model, ["keep_alive"] = 0 };
                using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (HttpResponseMessage resp = await _http.PostAsync(_endpoint + "/api/generate", content, ct).ConfigureAwait(false))
                { }
            }
            catch { }
        }

        private string ResolveOllamaExe()
        {
            if (!string.IsNullOrWhiteSpace(_exePath) && File.Exists(_exePath)) return _exePath;

            string[] candidates =
            {
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Ollama\ollama.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Ollama\ollama.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\Ollama\ollama.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;

            return "ollama";   // last resort: rely on PATH
        }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            JArray msgArray = new JArray();
            foreach (ChatMessage m in messages)
            {
                JObject jm = new JObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content ?? ""
                };
                if (m.ImagesBase64 != null && m.ImagesBase64.Length > 0)
                    jm["images"] = new JArray(m.ImagesBase64);
                msgArray.Add(jm);
            }

            JObject payload = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = msgArray
            };
            if (jsonFormat) payload["format"] = "json";

            using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage resp = await _http.PostAsync(_endpoint + "/api/chat", content, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject obj = JObject.Parse(json);
                JToken msg = obj["message"];
                if (msg != null && msg["content"] != null)
                    return (string)msg["content"];
                return "";
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
