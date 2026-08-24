using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using DesktopPet.Tools.ShimejiConvert.Emit;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Committed, IP-free end-to-end test of the emitter: a synthetic Shimeji config (a few primitives + a
    /// Fall + a Dragged + a cursor action + a ThrowIE) is parsed, composited from synthetic sprites, and
    /// emitted, then the result must be ACCEPTED -- the app's own validator passes it, it round-trips, and
    /// every animation is reachable. It also checks the residue captured the Group3 drop and Group2 degrade.
    /// Converting a REAL skin is the dev command `ShimejiConvert convert`.
    /// </summary>
    public static class EmitterSelfTest
    {
        public static bool Run(out string detail)
        {
            var failures = new List<string>();

            var owned = new Dictionary<string, Bitmap>(StringComparer.Ordinal)
            {
                { "/s.png", Solid(40, 60, Color.FromArgb(255, 200, 200, 200)) },
                { "/w1.png", Solid(40, 60, Color.FromArgb(255, 180, 180, 180)) },
                { "/w2.png", Solid(40, 60, Color.FromArgb(255, 160, 160, 160)) },
                { "/f.png", Solid(40, 60, Color.FromArgb(255, 120, 120, 255)) },
                { "/p.png", Solid(40, 60, Color.FromArgb(255, 255, 200, 120)) },
                { "/m.png", Solid(40, 60, Color.FromArgb(255, 200, 255, 200)) },
                { "/t.png", Solid(40, 60, Color.FromArgb(255, 255, 120, 120)) },
            };

            try
            {
                ShimejiConfig config = ShimejiParser.ParseActionsXml(SyntheticActionsXml);

                Func<string, Bitmap> load = delegate(string name) { return new Bitmap(owned[name]); };

                SpriteSheet sheet;
                string error;
                if (!SpriteSheetBuilder.Build(config.Poses, load, out sheet, out error))
                {
                    detail = "emitter self-test: compositing failed -- " + error;
                    return false;
                }

                ConversionResult r = PetEmitter.Emit(config, sheet, load, "TestSkin");

                if (!r.Valid) failures.Add("emitted XML failed the validator: " + r.Error);
                if (!r.RoundTrips) failures.Add("emitted XML did not round-trip: " + r.Error);
                if (r.Graph == null || r.Graph.Unreachable.Count != 0)
                    failures.Add("emitted pet has unreachable animations: " + (r.Graph == null ? "(no graph)" : string.Join(",", r.Graph.Unreachable)));
                if (!r.Accepted) failures.Add("result not accepted (valid+roundtrip+reachable)");

                if (!HasAnimationNamed(r, "fall")) failures.Add("no 'fall' magic animation emitted");
                if (!HasAnimationNamed(r, "drag")) failures.Add("no 'drag' magic animation emitted");
                if (!HasAnimationNamed(r, "kill")) failures.Add("no 'kill' magic animation emitted");
                if (!HasAnimationNamed(r, "sync")) failures.Add("no 'sync' magic animation emitted");

                if (!ResidueHas(r.Residue.Dropped, "ThrowIe")) failures.Add("Group3 ThrowIe not recorded as dropped");
                if (!ResidueHas(r.Residue.Degraded, "SitAndLookAtMouse")) failures.Add("Group2 cursor action not recorded as degraded");
            }
            finally
            {
                foreach (Bitmap b in owned.Values) b.Dispose();
            }

            var sb = new StringBuilder();
            sb.AppendLine("emitter self-test: synthetic skin -> valid, reachable, round-tripping pet");
            if (failures.Count == 0) { sb.Append("  accepted; magic names emitted; residue captured drop + degrade"); detail = sb.ToString(); return true; }
            foreach (string f in failures) sb.AppendLine("  FAIL " + f);
            detail = sb.ToString();
            return false;
        }

        private static bool HasAnimationNamed(ConversionResult r, string name)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return false;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool ResidueHas(List<ResidueItem> items, string name)
        {
            foreach (ResidueItem i in items)
                if (string.Equals(i.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Bitmap Solid(int w, int h, Color c)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = CompositingMode.SourceCopy; g.Clear(c); }
            return bmp;
        }

        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
        <Pose Image=""/w2.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" />
      </Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/f.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Pinched"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Dragged"">
      <Animation><Pose Image=""/p.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""5"" /></Animation>
    </Action>
    <Action Name=""SitAndLookAtMouse"" Type=""Stay"" BorderType=""Floor"">
      <Animation Condition=""#{mascot.environment.cursor.y &lt; 100}""><Pose Image=""/m.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""ThrowIe"" Type=""Embedded"" Class=""com.group_finity.mascot.action.ThrowIE"" InitialVX=""32"">
      <Animation><Pose Image=""/t.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""40"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
