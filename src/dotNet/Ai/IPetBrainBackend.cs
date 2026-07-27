using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.Ai
{
    /// <summary>
    /// A local LLM backend the pet's brain talks to. Ollama is the only implementation today,
    /// but keeping this seam means a llama.cpp / llama-server (or any OpenAI-compatible) backend
    /// is a drop-in later without touching <see cref="AiBrain"/>.
    /// </summary>
    internal interface IPetBrainBackend : IDisposable
    {
        /// <summary>
        /// Send a chat completion and return the raw assistant text. When <paramref name="jsonFormat"/>
        /// is true the backend is asked to constrain output to JSON.
        /// </summary>
        Task<string> ChatAsync(string model, IList<ChatMessage> messages, bool jsonFormat, CancellationToken ct);

        /// <summary>True when the backend server is reachable. Lets the pet stay silent when it isn't.</summary>
        Task<bool> IsAvailableAsync(CancellationToken ct);

        /// <summary>
        /// Ensure the backend server is running, starting it if necessary, and return true once it
        /// responds. Best-effort: returns false (never throws) if it can't be started.
        /// </summary>
        Task<bool> EnsureServerAsync(CancellationToken ct);

        /// <summary>Preload a model into memory so the first real request is fast. Never throws.</summary>
        Task WarmUpAsync(string model, CancellationToken ct);
    }
}
