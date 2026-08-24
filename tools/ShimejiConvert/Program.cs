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
            if (!SpriteSheetBuilder.Build(config.Poses, SpriteSheetBuilder.FileLoader(imgDirectory), out sheet, out error))
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
    }
}
