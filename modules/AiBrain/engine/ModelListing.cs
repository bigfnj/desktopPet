namespace DesktopPet.Ai
{
    /// <summary>
    /// One entry from a backend's installed/available model list (see
    /// <see cref="OllamaClient.ListModelsAsync"/>, <see cref="OpenAiCompatBackend.ListModelsAsync"/>).
    /// <see cref="Vision"/> is the backend's OWN reported capability when it has one (Ollama's
    /// <c>/api/tags</c> "capabilities" array is a real signal); it is null when the backend has no such
    /// signal (an older Ollama server, or any generic OpenAI-compatible <c>/v1/models</c> response), in
    /// which case the caller should fall back to <see cref="AiModelPolicy.LooksVisionCapable"/>.
    /// </summary>
    internal sealed class ModelListing
    {
        public ModelListing(string id, bool? vision)
        {
            Id = id;
            Vision = vision;
        }

        public string Id { get; private set; }
        public bool? Vision { get; private set; }
    }
}
