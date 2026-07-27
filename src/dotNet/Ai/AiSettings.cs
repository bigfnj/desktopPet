using System;
using System.IO;
using Newtonsoft.Json;

namespace DesktopPet.Ai
{
    /// <summary>
    /// AI-layer configuration, persisted as JSON in <c>%APPDATA%\DesktopPet\ai-settings.json</c>.
    /// Kept separate from the WinForms user.config so the AI layer stays self-contained and the
    /// original engine's settings are never touched. Missing/corrupt file falls back to defaults.
    /// </summary>
    internal sealed class AiSettings
    {
        /// <summary>Ollama base endpoint. No trailing slash needed.</summary>
        public string Endpoint = "http://localhost:11434";

        /// <summary>Fast text-only model used for OCR-based commentary.</summary>
        public string TextModel = "llama3.1:8b";

        /// <summary>Multimodal model used when <see cref="UseVision"/> is on (more expensive).</summary>
        public string VisionModel = "mistral-small3.1:24b";

        /// <summary>When true, send a downscaled screenshot to the vision model instead of OCR text.</summary>
        public bool UseVision = false;

        /// <summary>Per-request HTTP timeout. Cold model loads can be slow, so this is generous.</summary>
        public int TimeoutSeconds = 60;

        /// <summary>Full path to tesseract.exe. Empty means "find <c>tesseract</c> on PATH".</summary>
        public string TesseractPath = "";

        // ---- Phase 5: persona ----------------------------------------------

        /// <summary>The pet's name, injected into its persona. Empty -> a generic "desktop pet".</summary>
        public string PetName = "eSheep";

        /// <summary>Optional name the pet may address you by. Empty -> it won't use one.</summary>
        public string UserName = "";

        /// <summary>One-line personality blurb steering the pet's tone.</summary>
        public string Personality = "friendly, upbeat and a little cheeky";

        // ---- Phase 3: triggers ---------------------------------------------

        /// <summary>Register a global hotkey that fires the reactive "ask about my screen" flow.</summary>
        public bool HotkeyEnabled = true;

        /// <summary>Global hotkey combination, e.g. "Ctrl+Alt+P". Needs at least one modifier.</summary>
        public string Hotkey = "Ctrl+Alt+P";

        /// <summary>Opt-in: the pet occasionally comments on the screen unprompted.</summary>
        public bool IdleCommentaryEnabled = false;

        /// <summary>Lower bound of the random idle-commentary interval, in seconds.</summary>
        public int IdleMinSeconds = 90;

        /// <summary>Upper bound of the random idle-commentary interval, in seconds.</summary>
        public int IdleMaxSeconds = 150;

        /// <summary>Idle loop skips a turn unless the screen changed by at least this % of average luma.</summary>
        public int IdleChangeThresholdPercent = 5;

        // ---- launch preparation --------------------------------------------

        /// <summary>On launch, start the Ollama server (<c>ollama serve</c>) if it isn't already reachable.</summary>
        public bool AutoStartServer = true;

        /// <summary>On launch, preload the active model into memory so the first ask is fast.</summary>
        public bool WarmUpOnLaunch = true;

        /// <summary>Full path to ollama.exe. Empty means autodetect (PATH + default install locations).</summary>
        public string OllamaPath = "";

        [JsonIgnore]
        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DesktopPet", "ai-settings.json");
            }
        }

        /// <summary>Load settings, writing a default file on first run. Never throws.</summary>
        public static AiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    AiSettings loaded = JsonConvert.DeserializeObject<AiSettings>(File.ReadAllText(FilePath));
                    if (loaded != null) return loaded;
                }
                else
                {
                    AiSettings def = new AiSettings();
                    def.Save();
                    return def;
                }
            }
            catch { }
            return new AiSettings();
        }

        /// <summary>Persist settings. Never throws.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }
    }
}
