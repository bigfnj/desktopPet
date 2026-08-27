using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DesktopPet.Tools.ShimejiConvert.Emit;
using DesktopPet.Tools.ShimejiConvert.Shimeji;

namespace DesktopPet.Tools.ShimejiConvert
{
    /// <summary>
    /// Console entry point for the Shimeji -> animations.xml converter (BACKLOG #4).
    ///
    /// The first verb is deliberately not "convert" but "verify": before emitting a single pet, the tool
    /// has to prove it can read, grade and re-emit the 22 pets this repo already ships. Those pets are a
    /// free correctness corpus for the output half of the converter -- if the round-trip cannot survive a
    /// hand-authored pet, it will not survive a generated one, and the failure would otherwise be blamed
    /// on the Shimeji mapping.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();

            switch (args[0])
            {
                case "verify":
                    if (args.Length != 2) return Usage();
                    return Verify(args[1]);
                case "classify":
                    if (args.Length != 2) return Usage();
                    return Classify(args[1]);
                case "selftest":
                    if (args.Length != 1) return Usage();
                    return SelfTest();
                case "composite":
                    if (args.Length != 4) return Usage();
                    return Composite(args[1], args[2], args[3]);
                case "convert":
                    if (args.Length != 5) return Usage();
                    return ConvertVerb(args[1], args[2], args[3], args[4]);
                case "convertbundle":
                    if (args.Length != 3) return Usage();
                    return ConvertBundleVerb(args[1], args[2]);
                case "convertroot":
                    if (args.Length != 2 && args.Length != 3) return Usage();
                    return ConvertRoot(args[1], args.Length == 3 ? args[2] : null);
                case "rebalance":
                    if (args.Length != 2) return Usage();
                    return Rebalance(args[1]);
                case "reweight":
                    if (args.Length != 2) return Usage();
                    return Reweight(args[1]);
                default:
                    return Usage();
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("ShimejiConvert -- Shimeji skin -> desktopPet animations.xml (offline)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  verify <PetsDir>   Grade every pet under <PetsDir> with the app's own validator,");
            Console.Error.WriteLine("                     run the reachability pass, and round-trip the XML.");
            Console.Error.WriteLine("  classify <ConfDir> Parse a Shimeji conf dir (actions.xml + behaviors.xml) and print the");
            Console.Error.WriteLine("                     Group 1/2/3 fidelity census: what converts cleanly, what degrades,");
            Console.Error.WriteLine("                     and what is dropped as residue.");
            Console.Error.WriteLine("  selftest           Run the engine self-tests (classifier + compositor; synthetic fixtures).");
            Console.Error.WriteLine("  composite <ConfDir> <ImgDir> <out.png>");
            Console.Error.WriteLine("                     DEV: composite a real skin's sprites into one magenta-keyed sheet");
            Console.Error.WriteLine("                     and write it, for eyeballing. Point at an external clone.");
            Console.Error.WriteLine("  convert <ConfDir> <ImgDir> <SkinName> <out.xml>");
            Console.Error.WriteLine("                     Convert a Shimeji skin to a desktopPet animations.xml and write it");
            Console.Error.WriteLine("                     plus <out.xml>.residue.txt. Exit 0 only if the pet is accepted");
            Console.Error.WriteLine("                     (valid + round-trips + fully reachable). Pass - as <ConfDir> to");
            Console.Error.WriteLine("                     use the bundled base conf (a sprites-only skin).");
            Console.Error.WriteLine("  convertbundle <BundleDir> <out.xml>");
            Console.Error.WriteLine("                     Convert a modern Android Shimeji JSON+WebP bundle (manifest.json +");
            Console.Error.WriteLine("                     animation.json + sprites/*.webp) to a desktopPet animations.xml and");
            Console.Error.WriteLine("                     write it plus <out.xml>.residue.txt. Prints ACCEPTED and exits 0 only");
            Console.Error.WriteLine("                     if the pet is accepted (valid + round-trips + fully reachable).");
            Console.Error.WriteLine("  rebalance <PetsDir>");
            Console.Error.WriteLine("                     Migration: re-time already-emitted locomotion animations under <PetsDir>");
            Console.Error.WriteLine("                     to the current walk-time budget (so a slow walk no longer glides for");
            Console.Error.WriteLine("                     ~36s in one direction), rewriting only the pets that change through the");
            Console.Error.WriteLine("                     engine's own parser + serializer. Non-converted pets are left untouched.");
            Console.Error.WriteLine("  reweight <PetsDir>");
            Console.Error.WriteLine("                     Migration: re-weight the hub transitions of already-converted pets under");
            Console.Error.WriteLine("                     <PetsDir> through the current damped curve + minimum-share floor, so an");
            Console.Error.WriteLine("                     animation can no longer be technically reachable but practically never");
            Console.Error.WriteLine("                     played. Only touches pets whose header says they came from ShimejiConvert");
            Console.Error.WriteLine("                     AND are still at the pre-damping format version; hand-authored pets and");
            Console.Error.WriteLine("                     already-migrated pets are skipped, so it is safe to re-run.");
            return 2;
        }

        private static int Verify(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory))
            {
                Console.Error.WriteLine("No such directory: " + petsDirectory);
                return 2;
            }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml")))
                    pets.Add(candidate);

            pets.Sort(StringComparer.OrdinalIgnoreCase);

            if (pets.Count == 0)
            {
                Console.Error.WriteLine("Found no <dir>\animations.xml under " + petsDirectory);
                return 2;
            }

            Console.WriteLine(
                "pet".PadRight(20) + "KiB".PadLeft(7) + "anim".PadLeft(6) + "edge".PadLeft(6) +
                "term".PadLeft(6) + "unreach".PadLeft(9) + "  round-trip  valid");
            Console.WriteLine(new string('-', 78));

            int invalid = 0;
            int roundTripFailures = 0;
            int disconnected = 0;

            foreach (string petDirectory in pets)
            {
                string name = Path.GetFileName(petDirectory);
                string path = Path.Combine(petDirectory, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);
                double kib = new FileInfo(path).Length / 1024.0;

                XmlData.RootNode root;
                string error;
                bool valid = ShimejiEngine.TryValidate(xml, out root, out error);

                string graphCells = "".PadLeft(6) + "".PadLeft(6) + "".PadLeft(6) + "".PadLeft(9);
                string roundTrip = "-";

                if (valid)
                {
                    GraphReport report = ShimejiEngine.Analyze(root);
                    if (!report.IsConnected) disconnected++;

                    graphCells =
                        report.AnimationCount.ToString().PadLeft(6) +
                        report.EdgeCount.ToString().PadLeft(6) +
                        report.Terminal.Count.ToString().PadLeft(6) +
                        report.Unreachable.Count.ToString().PadLeft(9);

                    string roundTripError;
                    roundTrip = ShimejiEngine.RoundTrips(root, out roundTripError) ? "ok" : "FAIL";
                    if (roundTrip == "FAIL")
                    {
                        roundTripFailures++;
                        Console.WriteLine(
                            name.PadRight(20) + kib.ToString("F0").PadLeft(7) + graphCells +
                            "  " + roundTrip.PadRight(11) + "ok");
                        Console.WriteLine("      round-trip: " + roundTripError);
                        continue;
                    }
                }
                else
                {
                    invalid++;
                }

                Console.WriteLine(
                    name.PadRight(20) + kib.ToString("F0").PadLeft(7) + graphCells +
                    "  " + roundTrip.PadRight(11) + (valid ? "ok" : "INVALID"));

                if (!valid) Console.WriteLine("      validator: " + error);
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count +
                              "   invalid " + invalid +
                              "   round-trip failures " + roundTripFailures +
                              "   with unreachable animations " + disconnected);

            // A shipped pet failing the app's own validator, or failing to survive its own DTOs, means the
            // harness is wrong -- not the pet. Unreachable animations are reported, not failed: whether
            // hand-authored pets are fully connected is exactly the open question this pass answers.
            return invalid == 0 && roundTripFailures == 0 ? 0 : 1;
        }

        private static int Classify(string confDirectory)
        {
            if (!Directory.Exists(confDirectory))
            {
                Console.Error.WriteLine("No such directory: " + confDirectory);
                return 2;
            }

            ShimejiConfig config;
            try
            {
                config = ShimejiParser.ParseConfDirectory(confDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Parse failed: " + ex.Message);
                return 2;
            }

            foreach (FidelityGroup g in new[] { FidelityGroup.Group1, FidelityGroup.Group2, FidelityGroup.Group3 })
            {
                var inGroup = config.Actions.Where(a => a.Group == g).ToList();
                Console.WriteLine();
                Console.WriteLine(string.Format("----- {0} ({1} actions) -----", g, inGroup.Count));
                foreach (ShimejiAction a in inGroup)
                {
                    string cls = a.Class != null ? " Class=" + a.Class : "";
                    string border = a.BorderType != null ? " Border=" + a.BorderType : "";
                    Console.WriteLine(string.Format("  {0,-30} Type={1}{2}{3}", a.Name, a.Type, cls, border));
                    Console.WriteLine("       -> " + a.Reason);
                }
            }

            int g1 = config.Actions.Count(a => a.Group == FidelityGroup.Group1);
            int g2 = config.Actions.Count(a => a.Group == FidelityGroup.Group2);
            int g3 = config.Actions.Count(a => a.Group == FidelityGroup.Group3);
            int cg1 = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group1);
            int cg2 = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group2);

            Console.WriteLine();
            Console.WriteLine(string.Format("actions {0}   Group1 {1}   Group2 {2}   Group3 {3}",
                config.Actions.Count, g1, g2, g3));
            Console.WriteLine(string.Format("behavior conditions {0}   maps-cleanly(G1) {1}   needs-state(G2) {2}",
                config.BehaviorConditions.Count, cg1, cg2));
            return 0;
        }

        private static int SelfTest()
        {
            string detail;
            bool ok = EngineSelfTest.RunAll(out detail);
            Console.WriteLine(detail);
            Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            return ok ? 0 : 1;
        }

        private static int Composite(string confDirectory, string imgDirectory, string outPng)
        {
            if (!Directory.Exists(confDirectory)) { Console.Error.WriteLine("No such conf directory: " + confDirectory); return 2; }
            if (!Directory.Exists(imgDirectory)) { Console.Error.WriteLine("No such img directory: " + imgDirectory); return 2; }

            ShimejiConfig config;
            try { config = ShimejiParser.ParseConfDirectory(confDirectory); }
            catch (Exception ex) { Console.Error.WriteLine("Parse failed: " + ex.Message); return 2; }

            SpriteSheet sheet;
            string error;
            if (!SpriteSheetBuilder.Build(PetEmitter.PosesToComposite(config), SpriteSheetBuilder.FileLoader(imgDirectory), false, out sheet, out error))
            {
                Console.Error.WriteLine("Composite failed: " + error);
                return 1;
            }

            File.WriteAllBytes(outPng, sheet.PngBytes);
            Console.WriteLine(string.Format(
                "sheet: {0}x{1} tiles, cell {2}x{3}, scale {4:0.###}, frames {5}, png {6:N0} bytes, projected XML {7:N0} bytes",
                sheet.TilesX, sheet.TilesY, sheet.CellWidth, sheet.CellHeight, sheet.Scale,
                sheet.FrameIndexByKey.Count, sheet.PngBytes.Length, sheet.ProjectedXmlBytes));
            Console.WriteLine("wrote " + outPng);
            return 0;
        }

        // DEV/batch: detect the skin(s) under a root dir (as Pet Studio's import does) and convert the first,
        // reporting a one-line tab-separated verdict. For measuring convert yield over a harvested collection;
        // no output file is written. Exit 0 = accepted.
        private static int ConvertRoot(string root, string outXml)
        {
            if (!Directory.Exists(root)) { Console.WriteLine("NO-DIR\t" + root); return 2; }
            string note;
            System.Collections.Generic.List<DetectedSkin> skins = SkinLayout.Detect(root, out note);
            if (skins == null || skins.Count == 0) { Console.WriteLine("DETECT-FAIL\t" + (note ?? "")); return 3; }
            DetectedSkin skin = skins[0];
            string error;
            ConversionResult r = ShimejiEngine.ConvertSkin(skin.ConfDir, skin.ImgDir, skin.Name, out error);
            if (r == null) { Console.WriteLine("CONVERT-FAIL\t" + error); return 1; }
            if (outXml != null && r.Accepted)
                File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            int anims = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            Console.WriteLine((r.Accepted ? "ACCEPTED" : "NOT-ACCEPTED") +
                "\tskins=" + skins.Count + "\tanims=" + anims + "\tvalid=" + r.Valid + "\tbundled=" + skin.UsesBundledConf);
            return r.Accepted ? 0 : 1;
        }

        private static int ConvertBundleVerb(string bundleDir, string outXml)
        {
            if (!Directory.Exists(bundleDir)) { Console.Error.WriteLine("No such bundle directory: " + bundleDir); return 2; }
            if (!BundleConverter.IsBundle(bundleDir))
            {
                Console.Error.WriteLine("Not an Android Shimeji bundle (need manifest.json + animation.json): " + bundleDir);
                return 2;
            }

            // Take the display name from the manifest, so the header and residue label read nicely.
            BundleInfo info;
            try { BundleParser.Parse(bundleDir, out info); }
            catch (Exception ex) { Console.Error.WriteLine("Parse failed: " + ex.Message); return 1; }
            string skinName = !string.IsNullOrWhiteSpace(info.Name) ? info.Name.Trim() : Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar));

            string error;
            ConversionResult r = BundleConverter.ConvertBundle(bundleDir, skinName, out error);
            if (r == null) { Console.Error.WriteLine("Convert failed: " + error); return 1; }

            File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            string residuePath = outXml + ".residue.txt";
            File.WriteAllText(residuePath, r.Residue.ToText(skinName), new UTF8Encoding(false));

            int animCount = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            int unreachable = r.Graph != null ? r.Graph.Unreachable.Count : -1;
            Console.WriteLine(string.Format(
                "pet: {0} animations, valid={1}, roundtrip={2}, unreachable={3}, accepted={4}",
                animCount, r.Valid, r.RoundTrips, unreachable, r.Accepted));
            Console.WriteLine(string.Format("residue: {0} dropped, {1} degraded", r.Residue.Dropped.Count, r.Residue.Degraded.Count));
            Console.WriteLine("wrote " + outXml);
            Console.WriteLine("wrote " + residuePath);
            Console.WriteLine(r.Accepted ? "ACCEPTED" : "NOT-ACCEPTED");
            if (!r.Valid) Console.Error.WriteLine("validator: " + r.Error);
            return r.Accepted ? 0 : 1;
        }

        private static int ConvertVerb(string confDirectory, string imgDirectory, string skinName, string outXml)
        {
            bool bundled = confDirectory == "-";
            if (!bundled && !Directory.Exists(confDirectory)) { Console.Error.WriteLine("No such conf directory: " + confDirectory + " (pass - to use the bundled base conf)"); return 2; }
            if (!Directory.Exists(imgDirectory)) { Console.Error.WriteLine("No such img directory: " + imgDirectory); return 2; }

            string error;
            ConversionResult r = ShimejiEngine.ConvertSkin(bundled ? "" : confDirectory, imgDirectory, skinName, out error);
            if (r == null) { Console.Error.WriteLine("Convert failed: " + error); return 1; }

            File.WriteAllText(outXml, r.EmittedXml, new UTF8Encoding(false));
            string residuePath = outXml + ".residue.txt";
            File.WriteAllText(residuePath, r.Residue.ToText(skinName), new UTF8Encoding(false));

            int animCount = r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                ? r.Root.Animations.Animation.Length : 0;
            int unreachable = r.Graph != null ? r.Graph.Unreachable.Count : -1;
            Console.WriteLine(string.Format(
                "pet: {0} animations, valid={1}, roundtrip={2}, unreachable={3}, accepted={4}",
                animCount, r.Valid, r.RoundTrips, unreachable, r.Accepted));
            Console.WriteLine(string.Format("residue: {0} dropped, {1} degraded", r.Residue.Dropped.Count, r.Residue.Degraded.Count));
            Console.WriteLine("wrote " + outXml);
            Console.WriteLine("wrote " + residuePath);
            if (!r.Valid) Console.Error.WriteLine("validator: " + r.Error);
            return r.Accepted ? 0 : 1;
        }

        // Migration: re-time already-emitted locomotion animations to the current walk-time budget without
        // re-converting from source. The emitter marks a locomotion spoke uniquely as repeat="6" with a
        // border that turns; recompute its repeat from the shipped interval x frame count via the SAME policy
        // the emitter now uses (PetEmitter.LocoRepeatCount), and rewrite only the pets that change -- through
        // the engine's own parser + serializer, so a file's encoding/shape is untouched apart from the repeat
        // attribute. Pets that aren't converter output (no matching loco spoke) are left exactly as they are.
        /// <summary>
        /// Re-weight the hub transitions of already-converted pets through the current curve.
        ///
        /// Why a migration rather than a re-conversion: the source Shimeji skins are deliberately NOT in this
        /// repo (IP), so the shipped animations.xml is the only artefact available. That turns out to be
        /// enough, because the old emitter wrote weight = HubBaseWeight + frequency, so the source frequency
        /// is exactly (probability - HubBaseWeight) and can be pushed back through the new curve.
        ///
        /// That recovery is ALSO why this must not run twice on the same pet: a second pass would treat an
        /// already-damped weight as a raw frequency. Hence the format-version gate rather than a "looks
        /// about right" heuristic.
        /// </summary>
        private static int Reweight(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, skipped = 0, failures = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(xml, out root, out error))
                {
                    Console.WriteLine(name.PadRight(36) + " SKIP (invalid: " + error + ")");
                    skipped++;
                    continue;
                }

                // Two gates, both required. The author gate keeps this off hand-authored pets entirely -- the
                // shipped sheep have real author names and must never be re-weighted. The version gate makes
                // the run idempotent.
                if (root.Header == null ||
                    !string.Equals(root.Header.Author, PetEmitter.ConvertedAuthor, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (not converter output)");
                    skipped++;
                    continue;
                }
                if (!string.Equals(root.Header.Version, PetEmitter.ConvertedFormatVersionFlatWeights, StringComparison.Ordinal))
                {
                    Console.WriteLine(name.PadRight(36) + " skip (already at format " + (root.Header.Version ?? "?") + ")");
                    skipped++;
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no animations)");
                    skipped++;
                    continue;
                }

                // The hub is the animation fanning out to the most others; in every converted skin that is the
                // idle state the behaviour tree returns to.
                XmlData.AnimationNode hub = null;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                    if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
                }
                if (hub == null || hub.Sequence.Next.Length < 2)
                {
                    Console.WriteLine(name.PadRight(36) + " skip (no hub to re-weight)");
                    skipped++;
                    continue;
                }

                XmlData.NextNode[] edges = hub.Sequence.Next;
                var weights = new List<int>(edges.Length);
                int hubSelfIndex = -1;
                for (int i = 0; i < edges.Length; i++)
                {
                    int frequency = Math.Max(0, edges[i].Probability - PetEmitter.HubBaseWeight);
                    weights.Add(PetEmitter.HubWeightFromFrequency(frequency));
                    // The hub's own re-selection edge keeps the bare baseline; see ApplyMinimumShare.
                    if (edges[i].Value == hub.Id) { hubSelfIndex = i; weights[i] = PetEmitter.HubBaseWeight; }
                }
                PetEmitter.ApplyMinimumShare(weights, hubSelfIndex, PetEmitter.HubMinimumSharePercent);

                int changedEdges = 0;
                for (int i = 0; i < edges.Length; i++)
                    if (edges[i].Probability != weights[i]) { edges[i].Probability = weights[i]; changedEdges++; }

                root.Header.Version = PetEmitter.ConvertedFormatVersion;

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(36) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++;

                // Report the rarest REAL animation, excluding the hub's own re-selection edge. That edge is
                // deliberately pinned to the baseline, so including it would report ~0.6% on a pet whose
                // animations are all comfortably above the floor -- a misleading number that looks like a bug.
                int total = weights.Sum();
                int rarest = int.MaxValue;
                for (int i = 0; i < weights.Count; i++)
                    if (i != hubSelfIndex && weights[i] < rarest) rarest = weights[i];
                double worst = (total > 0 && rarest != int.MaxValue) ? rarest * 100.0 / total : 0;
                Console.WriteLine(name.PadRight(36) + " reweighted " + changedEdges + "/" + edges.Length +
                    " edges, rarest animation now " +
                    worst.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%");
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   reweighted " + petsChanged +
                "   skipped " + skipped + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static int Rebalance(string petsDirectory)
        {
            if (!Directory.Exists(petsDirectory)) { Console.Error.WriteLine("No such directory: " + petsDirectory); return 2; }

            var pets = new List<string>();
            foreach (string candidate in Directory.GetDirectories(petsDirectory))
                if (File.Exists(Path.Combine(candidate, "animations.xml"))) pets.Add(candidate);
            pets.Sort(StringComparer.OrdinalIgnoreCase);
            if (pets.Count == 0) { Console.Error.WriteLine("Found no <dir>\\animations.xml under " + petsDirectory); return 2; }

            int petsChanged = 0, animsChanged = 0, failures = 0;
            foreach (string petDir in pets)
            {
                string name = Path.GetFileName(petDir);
                string path = Path.Combine(petDir, "animations.xml");
                string xml = File.ReadAllText(path, Encoding.UTF8);

                XmlData.RootNode root;
                string error;
                if (!ShimejiEngine.TryValidate(xml, out root, out error))
                {
                    Console.WriteLine(name.PadRight(28) + " SKIP (invalid: " + error + ")");
                    continue;
                }
                if (root.Animations == null || root.Animations.Animation == null) { Console.WriteLine(name.PadRight(28) + " unchanged"); continue; }

                // id -> name, so a border's target can be recognised by name ("turn" marks a loco spoke).
                var nameById = new Dictionary<int, string>();
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                    if (a != null) nameById[a.Id] = a.Name;

                int changedHere = 0;
                foreach (XmlData.AnimationNode a in root.Animations.Animation)
                {
                    if (a == null || a.Sequence == null) continue;
                    if (!string.Equals(a.Sequence.RepeatCount, "6", StringComparison.Ordinal)) continue;   // only the emitter's loco spokes
                    if (!TurnsAtBorder(a, nameById)) continue;                                              // and only a walk-and-turn

                    int frames = a.Sequence.Frame != null ? a.Sequence.Frame.Length : 0;
                    int passMs = frames * IntervalMs(a);
                    string updated = PetEmitter.LocoRepeatCount(passMs)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(updated, a.Sequence.RepeatCount, StringComparison.Ordinal))
                    {
                        a.Sequence.RepeatCount = updated;
                        changedHere++;
                    }
                }

                if (changedHere == 0) { Console.WriteLine(name.PadRight(28) + " unchanged"); continue; }

                string outXml = ShimejiEngine.Serialize(root);
                XmlData.RootNode reparsed;
                string reError;
                if (!ShimejiEngine.TryValidate(outXml, out reparsed, out reError))
                {
                    Console.Error.WriteLine(name.PadRight(28) + " FAIL (re-validate: " + reError + ")");
                    failures++;
                    continue;
                }
                File.WriteAllText(path, outXml, new UTF8Encoding(false));
                petsChanged++; animsChanged += changedHere;
                Console.WriteLine(name.PadRight(28) + " rebalanced " + changedHere + " loco animation(s)");
            }

            Console.WriteLine();
            Console.WriteLine("pets " + pets.Count + "   changed " + petsChanged +
                "   loco animations retimed " + animsChanged + "   failures " + failures);
            return failures == 0 ? 0 : 1;
        }

        // A locomotion spoke turns at the screen edge: it has a border whose next targets an animation
        // named "turn". This keeps the migration from touching a hand-authored pet that merely uses repeat="6".
        private static bool TurnsAtBorder(XmlData.AnimationNode a, Dictionary<int, string> nameById)
        {
            if (a.Border == null || a.Border.Next == null) return false;
            foreach (XmlData.NextNode n in a.Border.Next)
            {
                string target;
                if (n != null && nameById.TryGetValue(n.Value, out target) &&
                    string.Equals(target, "turn", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // One frame's on-screen interval in ms: the mean of the animation's start and end interval (a
        // locomotion animation is near-constant, so the mean is its per-frame time). Falls back to whichever
        // end is present. 0 when neither parses, which keeps LocoRepeatCount at its ceiling (a no-op here).
        private static int IntervalMs(XmlData.AnimationNode a)
        {
            int s = ParseMs(a.Start != null ? a.Start.Interval : null);
            int e = ParseMs(a.End != null ? a.End.Interval : null);
            if (s > 0 && e > 0) return (s + e) / 2;
            return s > 0 ? s : e;
        }

        private static int ParseMs(string value)
        {
            int ms;
            return int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0 ? ms : 0;
        }
    }
}
