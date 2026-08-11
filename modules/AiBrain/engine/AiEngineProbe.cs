using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Ai;

namespace DesktopPet.AiBrainModule
{
    /// <summary>
    /// Self-test hook (NOT part of the plugin ABI) for --aibrain-selftest's engine leg. Proves the relocated
    /// AI-brain engine actually RUNS inside the module's own load context, without needing a live LLM:
    /// the DPAPI-scoped settings store (encrypt -> atomic write -> cross-session lock -> reload -> decrypt),
    /// chat-history persistence, endpoint/persona/model policy, and backend construction. This is the S4a
    /// expand-phase gate (mirrors the Fortunes FortuneEngineProbe). Invoked reflectively by the host so the
    /// base keeps no reference to the module engine.
    /// </summary>
    public static partial class AiEngineProbe
    {
        public static bool Run(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            string root = null;
            try
            {
                // Isolate the settings/history files in a throwaway root so the probe never touches real data.
                root = Path.Combine(Path.GetTempPath(), "dp-aibrain-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                AiPaths.SetRoot(root);

                // --- endpoint policy (in-module) ---
                string normLocal, normCloud, err;
                bool okLocal = AiEndpointPolicy.TryNormalize("http://localhost:11434", out normLocal, out err);
                ok &= Check(sb, "endpoint policy normalizes a loopback endpoint", okLocal);
                ok &= Check(sb, "loopback endpoint is recognized as local", okLocal && AiEndpointPolicy.IsLoopbackEndpoint(normLocal));
                bool okCloud = AiEndpointPolicy.TryNormalize("https://api.openai.com/v1", out normCloud, out err);
                ok &= Check(sb, "endpoint policy normalizes a cloud endpoint as non-loopback", okCloud && !AiEndpointPolicy.IsLoopbackEndpoint(normCloud));

                // --- persona + speech-pattern layer (in-module) ---
                ok &= Check(sb, "persona knows the 'pirate' speech pattern", Personas.IsKnownSpeech("pirate"));
                ok &= Check(sb, "persona rejects an unknown speech pattern", !Personas.IsKnownSpeech("definitely-not-a-pattern"));
                ok &= Check(sb, "speech instruction for a known pattern is non-empty", !string.IsNullOrEmpty(Personas.SpeechInstruction("pirate")));

                // --- model-capability policy (in-module) ---
                ok &= Check(sb, "model policy flags a vision model", AiModelPolicy.LooksVisionCapable("llava"));
                ok &= Check(sb, "model policy treats a text model as text-only", !AiModelPolicy.LooksVisionCapable("llama3.1:8b"));
                ok &= Check(sb, "model policy flags an uncensored model", AiModelPolicy.LooksUncensored("dolphin3:8b"));
                ok &= Check(sb, "model policy treats an unmarked model as untagged", !AiModelPolicy.LooksUncensored("llama3.1:8b"));
                string normModel;
                ok &= Check(sb, "model policy normalizes a valid id", AiModelPolicy.TryNormalize("gemma3:4b", out normModel) && normModel == "gemma3:4b");

                // --- backend construction (types + HttpClient load in the module ALC; no network) ---
                try
                {
                    using (IPetBrainBackend ollama = new OllamaClient(normLocal, TimeSpan.FromSeconds(30), ""))
                    using (IPetBrainBackend compat = new OpenAiCompatBackend(normCloud, "", TimeSpan.FromSeconds(30)))
                        ok &= Check(sb, "Ollama + OpenAI-compat backends construct in-module", ollama != null && compat != null);
                }
                catch (Exception ex) { ok = false; sb.AppendLine("FAIL: backend construction threw: " + ex.GetType().Name + ": " + ex.Message); }

                // --- the crown jewel: the DPAPI-scoped settings store, end to end, in the module ALC ---
                // Proves AtomicFile.TryWriteAllText + CrossSessionLock + ProtectedData all rebound cleanly.
                AiSettings s = AiSettings.Load();
                s.PetName = "ProbePet";
                s.Provider = "openai";
                s.OpenAiBaseUrl = "https://api.openai.com/v1";
                string setError;
                bool keyStored = s.TrySetApiKey("sk-probe-secret-1234567890", out setError);
                bool saved = s.Save();
                ok &= Check(sb, "settings save (atomic write + cross-session lock) succeeds", saved);

                AiSettings reloaded = AiSettings.Load();
                ok &= Check(sb, "settings scalar round-trips (PetName)", string.Equals(reloaded.PetName, "ProbePet", StringComparison.Ordinal));
                if (keyStored)
                {
                    // DPAPI encrypted the key on Save; reload must decrypt it back to plaintext.
                    ok &= Check(sb, "DPAPI API key round-trips (encrypt->save->reload->decrypt) in-module",
                        string.Equals(reloaded.ApiKey, "sk-probe-secret-1234567890", StringComparison.Ordinal));
                }
                else
                {
                    // DPAPI can be unavailable in a headless/service context; that is not an engine defect.
                    sb.AppendLine("SKIP: DPAPI key store unavailable here (" + setError + ") - round-trip not asserted");
                }

                // --- chat history persistence (in-module) ---
                var mem = new AiSettings { MemoryEnabled = true };
                ChatHistory history = ChatHistory.Load(mem);
                int before = history.RecentMessages().Count;
                history.Add("VS Code editing Program.cs", "Nice C# work, keep it tidy!");
                ok &= Check(sb, "chat history records an exchange in-module", history.RecentMessages().Count > before);

                // --- relocated AI SECURITY assertions (ported ~verbatim from the base SecuritySelfTest;
                // see AiEngineProbe.Security.cs). They exercise the SHIPPING module engine so no coverage
                // is lost when the base's dead Ai/* copy is deleted in a later phase. ---
                ok &= RunSecurity(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { if (root != null) Directory.Delete(root, true); } catch { } }
            detail = sb.ToString();
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
