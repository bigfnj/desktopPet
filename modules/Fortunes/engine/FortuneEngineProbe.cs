using System;
using System.Collections.Generic;
using System.Text;
using DesktopPet.Ai;

namespace DesktopPet.FortunesModule
{
    /// <summary>
    /// Self-test hook (NOT part of the plugin ABI) for --fortunes-engine-selftest. Proves the relocated
    /// fortune engine works inside the module's own load context: a deterministic filter/pick over injected
    /// entries, the engine's own comprehensive <see cref="FortuneProvider.FilterSelfTest"/> (dedup /
    /// classifier-parity / parser / custom ingestion / importer), and the SMART layer (Embedder loading
    /// native ONNX + SmartFortunes warming/picking over the injected pool). Invoked reflectively by the host
    /// so the base needs no reference to the module engine.
    /// </summary>
    public static class FortuneEngineProbe
    {
        public static bool Run(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                var entries = new List<FortuneEntry>
                {
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Prof = false, Text = "A calm general line.",  Custom = false },
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Prof = false, Text = "Another general line.", Custom = false },
                    new FortuneEntry { Source = "probe", Topic = "life", Genre = "dark", Level = "edgy",    Prof = false, Text = "An edgy line.",        Custom = false },
                };

                // SpicyFortunes=false => general-only, so the edgy entry is filtered out.
                var tame = new FortuneProvider(entries, new FortuneSettings());
                ok &= Check(sb, "tame pool keeps only general entries (edgy excluded)", tame.Count == 2);
                ok &= Check(sb, "tame Pick returns a non-empty line", !string.IsNullOrEmpty(tame.Pick()));

                // Edgy tier pulls in the edgy entry alongside general.
                var spicy = new FortuneProvider(entries, new FortuneSettings { SpicyFortunes = true, SpicyTier = "edgy" });
                ok &= Check(sb, "edgy tier includes the edgy entry", spicy.Count == 3);

                // The engine's full self-test suite, running in the module's context.
                bool filter = FortuneProvider.FilterSelfTest();
                ok &= Check(sb, "engine FilterSelfTest (dedup/classifier/parser/ingestion/importer)", filter);

                // --- smart layer: proves ONNX loads + runs inside the module's own load context ---
                ok &= Check(sb, "bge-small model present beside the module", Embedder.ModelPresent);
                if (Embedder.ModelPresent)
                {
                    // Embedder.SelfTest loads the ONNX model + embeds hardcoded strings and checks
                    // cos(code,code) > cos(code,weather) - the definitive proof that native onnxruntime.dll
                    // resolved and ran in the module's AssemblyLoadContext.
                    ok &= Check(sb, "Embedder loads ONNX + embeds in the module ALC", Embedder.SelfTest());

                    // SmartFortunes warm/pick over the injected pool exercises the rebinds: VectorCache
                    // (AtomicFile + FortunePaths.VectorCacheDir) + CrossSessionLock, all in-module. The
                    // public parameterless ctor uses the default cache dir = FortunePaths.VectorCacheDir.
                    using (var sm = new SmartFortunes())
                    {
                        sm.Warm(entries);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool ready = false, complete = false; int idx = 0, total = 0;
                        while (!complete && sw.ElapsedMilliseconds < 60000)
                        {
                            sm.WarmProgress(out ready, out complete, out idx, out total);
                            if (!complete) System.Threading.Thread.Sleep(100);
                        }
                        ok &= Check(sb, "SmartFortunes warms the injected pool in-module (VectorCache/lock rebinds)",
                            sm.Ready && sm.PoolCount == entries.Count);
                        string pick = sm.Pick("Visual Studio Code editing a C# file", "devenv");
                        sb.AppendLine("    smart pick -> " + (pick ?? "(random fallback)"));
                    }
                }
                else
                {
                    sb.AppendLine("    (bge-small model absent - smart checks skipped)");
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            detail = sb.ToString();
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
