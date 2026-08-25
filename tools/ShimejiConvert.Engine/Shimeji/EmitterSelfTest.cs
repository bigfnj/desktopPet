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
                if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(config), load, false, out sheet, out error))
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

                // Guard the invisible-pet bug: a spawn that places the pet fully off-screen horizontally and
                // routes to a stationary animation leaves it invisible. Evaluate each spawn's X against a
                // fake 1920-wide screen and require the pet to land within the horizontal bounds. (Y may be
                // above the top on purpose -- that spawn falls in.)
                if (r.Root != null && r.Root.Spawns != null && r.Root.Spawns.Spawn != null)
                {
                    foreach (XmlData.SpawnNode sp in r.Root.Spawns.Spawn)
                    {
                        int x = EvalOnFakeScreen(sp.X, sheet.CellWidth, sheet.CellHeight);
                        if (x < 0 || x > 1920 - sheet.CellWidth)
                            failures.Add("spawn " + sp.Id + " lands the pet off-screen horizontally (x=" + x + " of 1920)");
                    }
                }

                if (!HasAnimationNamed(r, "fall")) failures.Add("no 'fall' magic animation emitted");
                if (!HasAnimationNamed(r, "drag")) failures.Add("no 'drag' magic animation emitted");
                if (!HasAnimationNamed(r, "kill")) failures.Add("no 'kill' magic animation emitted");
                if (!HasAnimationNamed(r, "sync")) failures.Add("no 'sync' magic animation emitted");

                if (!ResidueHas(r.Residue.Dropped, "ThrowIe")) failures.Add("Group3 ThrowIe not recorded as dropped");
                if (!ResidueHas(r.Residue.Degraded, "SitAndLookAtMouse")) failures.Add("Group2 cursor action not recorded as degraded");
                if (!r.Residue.Notes.Exists(s => s.IndexOf("sound", StringComparison.OrdinalIgnoreCase) >= 0))
                    failures.Add("residue did not note the dropped pose sound");
                if (!r.Residue.Notes.Exists(s => s.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0))
                    failures.Add("residue did not note script-computed values");

                // Colour-key path keeps writing the magenta key.
                if (r.Root == null || r.Root.Image == null || r.Root.Image.Transparency != "Magenta")
                    failures.Add("colour-key pet did not declare <transparency>Magenta</transparency>");

                // Alpha path: same skin composited with real alpha must (a) declare the reserved
                // "Alpha" keyword the host renders per-pixel, and (b) leave genuinely-transparent
                // pixels in the sheet (empty cell area) instead of flattening onto magenta.
                SpriteSheet alphaSheet;
                if (!SpriteSheetBuilder.Build(Emit.PetEmitter.PosesToComposite(config), load, true, out alphaSheet, out error))
                {
                    failures.Add("alpha-mode compositing failed -- " + error);
                }
                else
                {
                    if (!alphaSheet.IsAlpha) failures.Add("alpha sheet did not carry IsAlpha");
                    if (!HasFullyTransparentPixel(alphaSheet.PngBytes))
                        failures.Add("alpha sheet has no fully-transparent pixel (background was flattened, not kept)");

                    ConversionResult ra = PetEmitter.Emit(config, alphaSheet, load, "TestSkinAlpha");
                    if (ra.Root == null || ra.Root.Image == null || ra.Root.Image.Transparency != "Alpha")
                        failures.Add("alpha pet did not declare <transparency>Alpha</transparency>");
                    if (!ra.Valid) failures.Add("alpha-mode emitted XML failed the validator: " + ra.Error);
                    if (!ra.Accepted) failures.Add("alpha-mode result not accepted (valid+roundtrip+reachable)");
                }
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

        // True if the decoded sheet has at least one fully-transparent pixel -- the signature of the
        // alpha path (empty cell area kept transparent) versus the magenta path (everything opaque).
        private static bool HasFullyTransparentPixel(byte[] png)
        {
            if (png == null || png.Length == 0) return false;
            using (var ms = new System.IO.MemoryStream(png, false))
            using (var bmp = new Bitmap(ms))
            {
                int stepY = Math.Max(1, bmp.Height / 32);
                int stepX = Math.Max(1, bmp.Width / 32);
                for (int y = 0; y < bmp.Height; y += stepY)
                    for (int x = 0; x < bmp.Width; x += stepX)
                        if (bmp.GetPixel(x, y).A == 0) return true;
                return false;
            }
        }

        private static int EvalOnFakeScreen(string expr, int imageW, int imageH)
        {
            return DesktopPet.SafeExpression.Evaluate(expr, delegate(string name)
            {
                switch (name)
                {
                    case "screenW": return 1920;
                    case "screenH": return 1080;
                    case "areaW": return 1920;
                    case "areaH": return 1040;
                    case "imageW": return imageW;
                    case "imageH": return imageH;
                    case "imageX": return -1;
                    case "imageY": return -1;
                    case "random": return 50;
                    case "randS": return 50;
                    case "scale": return 1;
                    default: throw new System.FormatException("unexpected variable in a spawn expression: " + name);
                }
            });
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
      <Animation><Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" Sound=""/beep.wav"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation>
        <Pose Image=""/w1.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""${5+Math.random()*5}"" />
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
