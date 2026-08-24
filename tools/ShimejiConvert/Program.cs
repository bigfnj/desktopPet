using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

    }
}
