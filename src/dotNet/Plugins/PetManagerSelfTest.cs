using System;
using System.IO;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// <c>--petmanager-selftest</c> (S6p2): exercises the <see cref="IPetManager"/> bridge without a live
    /// pet. Verifies the contract is reachable through <c>IHost.GetPetManager()</c>, the read/enumerate
    /// verbs, the graceful no-op behavior of the live verbs when there is no runtime, and a real
    /// install → enumerate → uninstall round-trip through the on-disk pet library (validated + path
    /// contained). Best-effort isolates the data root under the temp folder and always removes its probe.
    /// </summary>
    internal static class PetManagerSelfTest
    {
        private const string ProbeId = "petmanager-selftest-probe";

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;

            // Best-effort isolation: point the data root at a throwaway temp dir. Harmless if AppPaths has
            // already resolved (the probe is uninstalled below either way), so the real library is untouched.
            try
            {
                string isolated = Path.Combine(Path.GetTempPath(), "dp-petmgr-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(isolated);
                Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideEnvironmentVariable, isolated);
            }
            catch { }

            try
            {
                var host = new PetHost(null);   // null StartUp: the live verbs degrade, the disk verbs still work
                IPetManager pm = host.GetPetManager();
                ok &= Check(sb, "GetPetManager returns a manager", pm != null);
                if (pm == null) { Finish(sb, false); return false; }

                ok &= Check(sb, "MaxPets == MAX_SHEEPS (16)", pm.MaxPets == StartUp.MAX_SHEEPS);
                ok &= Check(sb, "IsAtMax is false with no runtime", !pm.IsAtMax);
                ok &= Check(sb, "OnScreenMix is empty with no runtime", pm.OnScreenMix().Count == 0);
                ok &= Check(sb, "SpawnOne is a safe no-op with no runtime", !pm.SpawnOne("eSheep"));
                ok &= Check(sb, "RemoveOne is a safe no-op with no runtime", !pm.RemoveOne("eSheep"));
                ok &= Check(sb, "SetActiveType is a safe no-op with no runtime", !pm.SetActiveType("eSheep"));

                bool hasBuiltIn = false;
                foreach (PetTypeInfo t in pm.InstalledTypes())
                    if (t != null && t.IsBuiltIn) { hasBuiltIn = true; break; }
                ok &= Check(sb, "InstalledTypes includes the built-in default", hasBuiltIn);

                // Failure paths must write nothing.
                string err;
                ok &= Check(sb, "InstallType rejects an unsafe id",
                    !pm.InstallType("../evil", new byte[] { 1 }, out err) && !string.IsNullOrEmpty(err));
                ok &= Check(sb, "InstallType rejects empty bytes",
                    !pm.InstallType(ProbeId, new byte[0], out err));
                ok &= Check(sb, "InstallType rejects invalid xml",
                    !pm.InstallType(ProbeId, Encoding.UTF8.GetBytes("<not-a-pet/>"), out err));

                // Round-trip using the built-in pet xml as a known-valid payload.
                string builtinXml, readErr;
                bool haveXml = PetCatalog.TryReadPetXml(PetCatalog.BuiltInPetId, out builtinXml, out readErr)
                    && !string.IsNullOrEmpty(builtinXml);
                ok &= Check(sb, "built-in pet xml available for the round-trip", haveXml);
                if (haveXml)
                {
                    byte[] xmlBytes = new UTF8Encoding(false).GetBytes(builtinXml);
                    ok &= Check(sb, "InstallType writes a validated pet",
                        pm.InstallType(ProbeId, xmlBytes, out err));

                    ok &= Check(sb, "the installed pet enumerates in InstalledTypes",
                        EnumeratesProbe(pm));

                    ok &= Check(sb, "UninstallType removes it", pm.UninstallType(ProbeId, out err));
                    ok &= Check(sb, "the pet no longer enumerates after uninstall", !EnumeratesProbe(pm));
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // Remove the probe even if an assert threw mid-round-trip.
                try { string e; new PetHost(null).GetPetManager().UninstallType(ProbeId, out e); } catch { }
            }

            Finish(sb, ok);
            return ok;
        }

        private static bool EnumeratesProbe(IPetManager pm)
        {
            foreach (PetTypeInfo t in pm.InstalledTypes())
                if (t != null && string.Equals(t.TypeId, ProbeId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool Check(StringBuilder sb, string label, bool pass)
        {
            sb.AppendLine((pass ? "PASS: " : "FAIL: ") + label);
            return pass;
        }

        private static void Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { Console.Out.Write(sb.ToString()); } catch { }
        }
    }
}
