using System;
using System.Linq;
using System.Text;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Verifies the bundled Shimeji base conf embeds and parses, so a sprites-only skin (no conf of its own)
    /// can convert against it. The bundled conf IS the gil/shimeji-ee reference config, so this also pins
    /// that it is intact: 91 actions grouping 53 Group1 / 32 Group2 / 6 Group3.
    /// </summary>
    public static class BundledConfSelfTest
    {
        public static bool Run(out string detail)
        {
            ShimejiConfig cfg;
            try { cfg = ShimejiParser.ParseBundledConf(); }
            catch (Exception ex) { detail = "bundled-conf self-test: ParseBundledConf threw -- " + ex.Message; return false; }

            int total = cfg.Actions.Count;
            int g1 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group1);
            int g2 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group2);
            int g3 = cfg.Actions.Count(a => a.Group == FidelityGroup.Group3);

            var sb = new StringBuilder();
            sb.AppendLine("bundled-conf self-test: base actions.xml + behaviors.xml embed and parse");
            if (total == 91 && g1 == 53 && g2 == 32 && g3 == 6)
            {
                sb.Append("  91 actions (53/32/6) -- the reference census, intact");
                detail = sb.ToString();
                return true;
            }
            sb.AppendLine(string.Format("  FAIL expected 91 actions (53/32/6), got {0} ({1}/{2}/{3})", total, g1, g2, g3));
            detail = sb.ToString();
            return false;
        }
    }
}
