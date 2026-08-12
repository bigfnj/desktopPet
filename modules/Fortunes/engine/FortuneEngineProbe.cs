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

                // The default level is Clean => general-only, so the edgy entry is filtered out.
                var tame = new FortuneProvider(entries, new FortuneSettings());
                ok &= Check(sb, "tame pool keeps only general entries (edgy excluded)", tame.Count == 2);
                ok &= Check(sb, "tame Pick returns a non-empty line", !string.IsNullOrEmpty(tame.Pick()));

                // "Clean + edgy" pulls in the edgy entry alongside general.
                var spicy = new FortuneProvider(entries, new FortuneSettings { ContentLevel = ContentLevels.CleanEdgy });
                ok &= Check(sb, "clean+edgy includes the edgy entry", spicy.Count == 3);

                // Content-level migration: a settings file written before the four tone controls were
                // collapsed must land on the level that preserves the user's evident intent. Getting this
                // wrong silently changes what the pet is allowed to say, in either direction.
                ok &= Check(sb, "migration: spicy off -> clean",
                    FortunesModule.MigrateContentLevel("", false, false) == ContentLevels.Clean);
                ok &= Check(sb, "migration: spicy on -> everything (old 'edgy' tier meant general+edgy+nsfw)",
                    FortunesModule.MigrateContentLevel("", true, false) == ContentLevels.Everything);
                ok &= Check(sb, "migration: spicy on + skip-tame -> spicy only",
                    FortunesModule.MigrateContentLevel("", true, true) == ContentLevels.SpicyOnly);
                ok &= Check(sb, "migration: skip-tame is ignored when spicy was off (never widens)",
                    FortunesModule.MigrateContentLevel("", false, true) == ContentLevels.Clean);
                ok &= Check(sb, "migration: an already-migrated value wins over the legacy keys",
                    FortunesModule.MigrateContentLevel(ContentLevels.CleanEdgy, true, true) == ContentLevels.CleanEdgy);
                ok &= Check(sb, "migration: an unrecognized stored value falls back to the legacy reading",
                    FortunesModule.MigrateContentLevel("bogus", true, false) == ContentLevels.Everything);

                // Pack/genre ticks are staged and folded in at Apply (ListCard.DeferChanges), so this fold
                // decides which packs the engine reads. Dropping or double-adding an id here would quietly
                // change what the pet is allowed to say, so assert it directly rather than by clicking.
                var off = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "b", true } };
                ok &= Check(sb, "merge: disabling an id adds it and keeps the untouched ones",
                    FortunesModule.MergeDisabled("a", off) == "a\nb");
                var on = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "a", false } };
                ok &= Check(sb, "merge: re-enabling an id removes it",
                    FortunesModule.MergeDisabled("a\nb", on) == "b");
                var both = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "a", false }, { "c", true } };
                ok &= Check(sb, "merge: a mixed batch applies in one pass",
                    FortunesModule.MergeDisabled("a\nb", both) == "b\nc");
                var already = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { { "A", true } };
                ok &= Check(sb, "merge: re-disabling an already-disabled id does not duplicate it (case-insensitive)",
                    FortunesModule.MergeDisabled("a", already) == "A");
                ok &= Check(sb, "merge: an empty batch leaves the stored list alone",
                    FortunesModule.MergeDisabled("a\nb", new Dictionary<string, bool>()) == "a\nb");

                // Smart-index status. Warm() runs in the background and leaves ready=false / total=0 until
                // its first batch publishes, so a status read from the index's own counters told everyone
                // "No fortunes yet" every time they pressed Rebuild, however full the pool was.
                string building = FortunesModule.SmartStatusFor(true, 12345, true, false, false, 0, 0);
                ok &= Check(sb, "a just-started warm reports indexing, not an empty pool",
                    building.IndexOf("12,345", StringComparison.Ordinal) >= 0 &&
                    building.IndexOf("No fortunes", StringComparison.Ordinal) < 0);
                ok &= Check(sb, "a finished index reports what it indexed",
                    FortunesModule.SmartStatusFor(true, 900, true, true, true, 900, 900)
                        .IndexOf("ready", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "a partly-warm index says it is usable now",
                    FortunesModule.SmartStatusFor(true, 900, true, true, false, 100, 900)
                        .IndexOf("usable now", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "smart picks off is reported as off, not as an empty pool",
                    FortunesModule.SmartStatusFor(false, 0, false, false, false, 0, 0)
                        .IndexOf("off", StringComparison.Ordinal) >= 0);
                // An empty pool with packs installed is a filter problem; "add a pack" would send a user
                // with 129 of them entirely the wrong way.
                ok &= Check(sb, "empty pool + packs installed blames the filters",
                    FortunesModule.EmptyPoolReason(true).IndexOf("filters", StringComparison.Ordinal) >= 0);
                ok &= Check(sb, "empty pool + nothing installed asks for a pack",
                    FortunesModule.EmptyPoolReason(false).IndexOf("add a pack", StringComparison.Ordinal) >= 0);

                // The fingerprint behind "already built, nothing to rebuild".
                var poolA = new List<FortuneEntry>(entries);
                ok &= Check(sb, "signature: the same pool fingerprints the same",
                    FortunesModule.PoolSignature(poolA) == FortunesModule.PoolSignature(new List<FortuneEntry>(entries)));
                var poolB = new List<FortuneEntry>(entries);
                poolB.RemoveAt(poolB.Count - 1);
                ok &= Check(sb, "signature: dropping an entry changes it",
                    FortunesModule.PoolSignature(poolA) != FortunesModule.PoolSignature(poolB));
                var poolC = new List<FortuneEntry>(entries);
                poolC[0] = new FortuneEntry { Source = "probe", Topic = "life", Genre = "quip", Level = "general", Text = "A different line." };
                ok &= Check(sb, "signature: swapping a line of the same count changes it",
                    FortunesModule.PoolSignature(poolA) != FortunesModule.PoolSignature(poolC));

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
