using System.Text;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Runs every engine self-test on committed, IP-free fixtures. This is what the CLI `selftest` verb and
    /// run-gate.ps1 invoke; each sub-test is self-contained and never touches an external Shimeji clone.
    /// </summary>
    public static class EngineSelfTest
    {
        public static bool RunAll(out string detail)
        {
            var sb = new StringBuilder();
            bool ok = true;
            string d;

            if (!ClassifierSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            if (!CompositorSelfTest.Run(out d)) ok = false;
            sb.AppendLine(d);

            detail = sb.ToString().TrimEnd();
            return ok;
        }
    }
}
