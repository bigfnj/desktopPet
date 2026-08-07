using System.Collections.Generic;

namespace DesktopPet.Ai
{
    /// <summary>
    /// The fortune-relevant settings the engine reads, split out of the base <c>AiSettings</c> when the
    /// engine moved into the Fortunes module (S3). Field names/types mirror the old <c>AiSettings</c>
    /// fortune block exactly, so the relocated <see cref="FortuneProvider"/> is unchanged apart from the
    /// type name. Populated by the module from <c>host.GetSettings</c> when the engine goes live (S3d); the
    /// self-tests construct it directly.
    /// </summary>
    public sealed class FortuneSettings
    {
        public bool SpicyFortunes = false;
        public string SpicyTier = "edgy";      // "edgy" | "nsfw"
        public bool SpicyOnly = false;
        public bool NoProfanity = false;
        public bool SmartFortunes = true;
        public List<string> DisabledSources = new List<string>();
        public List<string> DisabledGenres = new List<string>();
    }
}
