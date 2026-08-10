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
        private static readonly TimeSpan DefaultStartupDeadline =
            TimeSpan.FromSeconds(20);
        private static readonly TimeSpan DefaultProbeDeadline =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DefaultPollInterval =
            TimeSpan.FromMilliseconds(500);

        private readonly string _endpoint;
        private readonly string _exePath;
        private readonly HttpClient _http;
        private readonly TimeSpan _deadline;
        private readonly TimeSpan _startupDeadline;
        private readonly TimeSpan _probeDeadline;
        private readonly TimeSpan _pollInterval;
        private readonly Func<CancellationToken, bool> _serverStarter;

        public OllamaClient(string endpoint, TimeSpan timeout, string exePath)
        {
            _endpoint = AiEndpointPolicy.NormalizeOrThrow(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint,
                "endpoint");
            _exePath = exePath;
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _startupDeadline = DefaultStartupDeadline;
            _probeDeadline = DefaultProbeDeadline;
            _pollInterval = DefaultPollInterval;
            _http = new HttpClient(AiEndpointPolicy.CreateNoRedirectHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _serverStarter = TryStartServer;
        }

        internal OllamaClient(
            string endpoint,
            TimeSpan timeout,
            string exePath,
            HttpMessageHandler handler,
            TimeSpan startupDeadline,
            TimeSpan probeDeadline,
            TimeSpan pollInterval,
            Func<CancellationToken, bool> serverStarter)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (serverStarter == null)
                throw new ArgumentNullException("serverStarter");

            _endpoint = AiEndpointPolicy.NormalizeOrThrow(
                string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint,
                "endpoint");
            _exePath = exePath;
            _deadline = AiEndpointPolicy.ValidateDeadline(timeout, "timeout");
            _startupDeadline = AiEndpointPolicy.ValidateDeadline(
                startupDeadline,
                "startupDeadline");
            _probeDeadline = AiEndpointPolicy.ValidateDeadline(
                probeDeadline,
                "probeDeadline");
            _pollInterval = AiEndpointPolicy.ValidateDeadline(
                pollInterval,
                "pollInterval");
            _http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _serverStarter = serverStarter;
        }

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            return await IsAvailableAsync(_deadline, ct).ConfigureAwait(false);
        }

        private async Task<bool> IsAvailableAsync(
            TimeSpan deadline,
            CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, _endpoint + "/api/tags"))
                {
                    return await AiEndpointPolicy.SendAndCheckSuccessAsync(
                        _http,
                        request,
                        deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> EnsureServerAsync(CancellationToken ct)
        {
            using (var startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                startupCancellation.CancelAfter(_startupDeadline);
                CancellationToken startupToken = startupCancellation.Token;
                try
                {
                    if (await IsAvailableAsync(
                        _probeDeadline,
                        startupToken).ConfigureAwait(false))
                        return true;

                    startupToken.ThrowIfCancellationRequested();
                    if (!AiEndpointPolicy.IsLoopbackEndpoint(_endpoint))
                        return false;

                    bool started;
                    try
                    {
                        started = await RunServerStarterAsync(
                            _serverStarter,
                            startupToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        startupToken.ThrowIfCancellationRequested();
                        return false;
                    }

                    startupToken.ThrowIfCancellationRequested();
                    if (!started) return false;

                    while (true)
                    {
                        await Task.Delay(
                            _pollInterval,
                            startupToken).ConfigureAwait(false);
                        if (await IsAvailableAsync(
                            _probeDeadline,
                            startupToken).ConfigureAwait(false))
                            return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    if (startupCancellation.IsCancellationRequested)
                        return false;
                    throw;
                }
            }
        }

        private static async Task<bool> RunServerStarterAsync(
            Func<CancellationToken, bool> serverStarter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<bool> starterTask = Task.Run(
                delegate { return serverStarter(cancellationToken); },
                cancellationToken);
            if (starterTask.IsCompleted)
                return await starterTask.ConfigureAwait(false);

            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                delegate { cancellation.TrySetResult(true); }))
            {
                Task completed = await Task.WhenAny(
                    starterTask,
                    cancellation.Task).ConfigureAwait(false);
                if (completed == starterTask)
                    return await starterTask.ConfigureAwait(false);

                ObserveLateStarterFailure(starterTask);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static void ObserveLateStarterFailure(Task starterTask)
        {
            if (starterTask == null) return;
            starterTask.ContinueWith(
                completed =>
                {
                    var ignored = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private bool TryStartServer(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string executable = ResolveOllamaExe();
                if (string.IsNullOrEmpty(executable)) return false;
                cancellationToken.ThrowIfCancellationRequested();
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable)
                };
                cancellationToken.ThrowIfCancellationRequested();
                using (Process started = Process.Start(psi))
                {
                    // Dispose only our process handle. The server itself intentionally remains alive.
                    return started != null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;   // exe not found / can't launch
            }
        }

        public async Task WarmUpAsync(string model, CancellationToken ct)
        {
            string normalizedModel;
            if (!AiModelPolicy.TryNormalize(model, out normalizedModel)) return;
            try
            {
                // No "prompt" -> Ollama just loads the model into memory (done_reason: "load").
                JObject payload = new JObject
                {
                    ["model"] = normalizedModel,
                    ["stream"] = false,
                    ["keep_alive"] = "10m"
                };
                using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/generate"))
                {
                    request.Content = content;
                    await AiEndpointPolicy.SendAndEnsureSuccessAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        /// <summary>Evict a model from memory/VRAM immediately (keep_alive: 0). Best-effort; never throws.</summary>
        public async Task UnloadAsync(string model, CancellationToken ct)
        {
            string normalizedModel;
            if (!AiModelPolicy.TryNormalize(model, out normalizedModel)) return;
            try
            {
                JObject payload = new JObject
                {
                    ["model"] = normalizedModel,
                    ["keep_alive"] = 0
                };
                using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/generate"))
                {
                    request.Content = content;
                    await AiEndpointPolicy.SendAndEnsureSuccessAsync(
                        _http,
                        request,
                        _deadline,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        private string ResolveOllamaExe()
        {
            if (!string.IsNullOrWhiteSpace(_exePath))
                return AiExecutablePolicy.ResolveConfigured(
                    _exePath,
                    "ollama.exe");

            string[] candidates =
            {
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Ollama\ollama.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Ollama\ollama.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\Ollama\ollama.exe")
            };
            foreach (string c in candidates)
            {
                string resolved = AiExecutablePolicy.ResolveConfigured(
                    c,
                    "ollama.exe");
                if (resolved != null) return resolved;
            }

            return AiExecutablePolicy.ResolveFromPath(
                Environment.GetEnvironmentVariable("PATH"),
                "ollama.exe");
        }

        public async Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct)
        {
            string normalizedModel = AiModelPolicy.NormalizeOrThrow(model, "model");
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
                ["model"] = normalizedModel,
                ["stream"] = false,
                ["messages"] = msgArray,
                // A little extra sampling variety so short in-character remarks don't converge on one line.
                ["options"] = new JObject { ["temperature"] = 0.9 }
            };
            if (jsonFormat) payload["format"] = "json";

            using (StringContent content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat"))
            {
                request.Content = content;
                string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                    _http,
                    request,
                    _deadline,
                    ct).ConfigureAwait(false);
                JObject obj = JObject.Parse(json);
                JToken msg = obj["message"];
                if (msg != null && msg["content"] != null)
                {
                    return (string)msg["content"];
                }
                return "";
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
