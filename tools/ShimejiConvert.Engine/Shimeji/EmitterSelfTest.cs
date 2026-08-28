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
                { "/c1.png", Solid(40, 60, Color.FromArgb(255, 120, 255, 255)) },
                { "/c2.png", Solid(40, 60, Color.FromArgb(255, 100, 235, 235)) },
                { "/k1.png", Solid(40, 60, Color.FromArgb(255, 250, 240, 60)) },
                { "/k2.png", Solid(40, 60, Color.FromArgb(255, 230, 220, 40)) },
                { "/k3.png", Solid(40, 60, Color.FromArgb(255, 190, 120, 240)) },
                { "/k4.png", Solid(40, 60, Color.FromArgb(255, 170, 100, 220)) },
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

                // ---- the wall region ----
                // Four properties, each of which was a real bug or is the mechanism the feature rests on.
                XmlData.AnimationNode wall = FindAnimationNamed(r, "ClimbWall");
                if (wall == null)
                {
                    // Was a live failure: a Group1-only wall filter dropped the reference conf's ClimbWall
                    // (Group2 because its CONDITION reads mascot.anchor), leaving a pet that clings motionless.
                    failures.Add("no wall animation emitted (a Group2 wall action must still convert)");
                }
                else
                {
                    // The cling. Presence of <gravity> is what makes the engine drop an unsupported pet, so a
                    // wall animation must NOT have one. This is how the hand-authored sheep stay on walls.
                    if (wall.Gravity != null)
                        failures.Add("wall animation has a <gravity> node, so the pet would fall off the wall instead of clinging");

                    // The climb: negative Y is upward.
                    int wallEndY = ParseIntOrZero(wall.End != null ? wall.End.Y : null);
                    if (wallEndY >= 0)
                        failures.Add("wall animation does not move upward (end y=" + wallEndY + ")");

                    // It must be unreachable from the floor hub's own choice list, or a wall-cling would play
                    // in the middle of the screen -- the reason wall actions were excluded outright before.
                    if (HubSequenceTargets(r).Contains(wall.Id))
                        failures.Add("the floor hub can select the wall animation directly; it must only be entered from a vertical border");

                    // And it must be reachable, via a vertical-border edge on a locomotion animation.
                    if (!HasBorderEdgeTo(r, wall.Id, "vertical"))
                        failures.Add("no only=\"vertical\" border edge enters the wall region");
                }

                // ---- the ceiling region ----
                // The ceiling exists to be entered by CLIMBING and no other way, so most of what is asserted
                // here is about what must NOT reach it.
                XmlData.AnimationNode ceiling = FindAnimationNamed(r, "ClimbCeiling");
                if (ceiling == null)
                {
                    failures.Add("no ceiling animation emitted");
                }
                else
                {
                    // Same cling mechanism as the wall: <gravity> is what makes the engine drop an
                    // unsupported pet, so a hanging animation must not carry one.
                    if (ceiling.Gravity != null)
                        failures.Add("ceiling animation has a <gravity> node, so the pet would drop instead of hanging");

                    // It travels ALONG the ceiling, not through it. A non-zero Y here would either fight the
                    // engine's PositionY pin at the top border or walk the pet off the ceiling.
                    int ceilEndY = ParseIntOrZero(ceiling.End != null ? ceiling.End.Y : null);
                    if (ceilEndY != 0)
                        failures.Add("ceiling animation has vertical velocity (end y=" + ceilEndY + "); it must move horizontally only");
                    if (ParseIntOrZero(ceiling.End != null ? ceiling.End.X : null) == 0)
                        failures.Add("ceiling animation does not move horizontally, so the pet would hang motionless");

                    // Never selectable mid-screen.
                    if (HubSequenceTargets(r).Contains(ceiling.Id))
                        failures.Add("the floor hub can select the ceiling animation directly; it must only be entered from the top border");

                    // Reachable, and reachable ONLY from the wall. This is the assertion that keeps the
                    // top-border ambiguity harmless: if a FLOOR animation ever gained an only="horizontal"
                    // edge, the pet could snap to the ceiling from ground level.
                    if (!HasBorderEdgeTo(r, ceiling.Id, "horizontal"))
                        failures.Add("no only=\"horizontal\" border edge enters the ceiling region");
                    foreach (XmlData.AnimationNode src in BorderSourcesOf(r, ceiling.Id, "horizontal"))
                        if (FindAnimationNamed(r, "ClimbWall") == null || src.Id != FindAnimationNamed(r, "ClimbWall").Id)
                            failures.Add("ceiling is entered from '" + src.Name + "', which is not the wall climb; it must be reachable only by climbing");

                    // And it must lead back out, or a pet that reaches the ceiling stays there for good.
                    if (ceiling.Border == null || ceiling.Border.Next == null || ceiling.Border.Next.Length == 0)
                        failures.Add("ceiling animation has no border edge, so the pet could never leave the ceiling");
                }

                // The geometry the old exclusion existed to protect: admitting a ceiling pose whose anchor is
                // ABOVE the floor anchor must not pad the cell, because a padded cell lifts every floor pet
                // off the ground. The floor poses anchor at 60, so an unscaled cell taller than that means
                // the ceiling anchor leaked into the cell height.
                if (sheet.CellHeight > 60)
                    failures.Add("cell height grew to " + sheet.CellHeight + " (>60): a ceiling anchor padded the cell, which floats every floor animation");

                // ...and the mechanism itself. Cell height alone cannot catch a ceiling pose composited under
                // the FLOOR convention: the cell stays 60 either way, but the sprite lands at the cell BOTTOM,
                // so the pet hangs a full cell below the ceiling it is meant to be gripping.
                //
                // The fixture makes the two conventions exact opposites, which is what gives this teeth. The
                // ceiling sprite is 60 tall anchored at 24, so top-anchored it occupies rows 0..35 and leaves
                // the bottom empty, while bottom-anchored it occupies rows 36..59 and leaves the TOP empty.
                // Asserting both ends distinguishes them; asserting only the top would also pass on a sprite
                // that happened to fill the cell.
                // THE guard, and the one that actually matters: no animation may reference a blank tile.
                // Anchor arithmetic that skips too much of the source produces a fully transparent tile, the
                // pet vanishes mid-animation, and nothing else notices -- the XML validates, the graph is
                // reachable, the round-trip passes. That shipped in 1.9.4 for every Android-bundle pet
                // because bundles anchor bottom-centre and the ceiling path skipped AnchorY rows.
                var blank = new List<string>();
                if (r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null)
                {
                    foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                    {
                        if (a == null || a.Sequence == null || a.Sequence.Frame == null) continue;
                        foreach (int tile in a.Sequence.Frame)
                            if (!TileIsPainted(sheet, tile))
                                blank.Add(a.Name + " -> tile " + tile);
                    }
                }
                if (blank.Count > 0)
                    failures.Add("animations reference blank (fully transparent) tiles, so the pet vanishes: "
                        + string.Join(", ", blank.ToArray()));

                string ceilKey = FirstPoseKey(config, "ClimbCeiling");
                if (ceilKey != null)
                {
                    if (!TileRowIsPainted(sheet, ceilKey, 0))
                        failures.Add("the ceiling frame is not drawn at the top of its tile, so the pet would hang a whole cell below the ceiling");
                    if (TileRowIsPainted(sheet, ceilKey, sheet.CellHeight - 1))
                        failures.Add("the ceiling frame reaches the bottom of its tile, so it was composited under the floor anchor convention");
                }

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

        private static XmlData.AnimationNode FindAnimationNamed(ConversionResult r, string name)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
            return null;
        }

        private static int ParseIntOrZero(string value)
        {
            int parsed;
            return int.TryParse((value ?? "").Trim(), out parsed) ? parsed : 0;
        }

        /// <summary>Ids the FLOOR hub can select directly: the floor animation whose sequence fans out to the
        /// most others.
        ///
        /// "Floor" is decided by the presence of a &lt;gravity&gt; node, not by fan-out alone. Fan-out on its
        /// own used to be enough, but it silently stops identifying the floor once the wall region has more
        /// than one spoke: in a small fixture a wall animation (which lists its sibling wall poses plus fall)
        /// can out-fan the hub, and the test then reports the hub selecting a wall animation when what it
        /// actually found WAS the wall. Gravity is the right discriminator because omitting it is precisely
        /// what defines a wall or ceiling animation.</summary>
        private static List<int> HubSequenceTargets(ConversionResult r)
        {
            var targets = new List<int>();
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return targets;
            XmlData.AnimationNode hub = null;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Sequence == null || a.Sequence.Next == null) continue;
                if (a.Gravity == null) continue;   // wall / ceiling / fall, not the floor
                if (hub == null || a.Sequence.Next.Length > hub.Sequence.Next.Length) hub = a;
            }
            if (hub != null)
                foreach (XmlData.NextNode n in hub.Sequence.Next) targets.Add(n.Value);
            return targets;
        }

        /// <summary>True when some animation has a &lt;border&gt; edge with the given only-flag pointing at the
        /// target id.</summary>
        private static bool HasBorderEdgeTo(ConversionResult r, int targetId, string onlyFlag)
        {
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return false;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Border == null || a.Border.Next == null) continue;
                foreach (XmlData.NextNode n in a.Border.Next)
                    if (n.Value == targetId && string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Every animation carrying a border edge of this only= flag INTO the target. The ceiling test needs
        // the sources, not just "does an edge exist": the property that matters is that nothing except the
        // wall climb can reach it.
        private static List<XmlData.AnimationNode> BorderSourcesOf(ConversionResult r, int targetId, string onlyFlag)
        {
            var sources = new List<XmlData.AnimationNode>();
            if (r.Root == null || r.Root.Animations == null || r.Root.Animations.Animation == null) return sources;
            foreach (XmlData.AnimationNode a in r.Root.Animations.Animation)
            {
                if (a == null || a.Border == null || a.Border.Next == null) continue;
                foreach (XmlData.NextNode n in a.Border.Next)
                    if (n.Value == targetId && string.Equals(n.OnlyFlag, onlyFlag, StringComparison.Ordinal))
                    {
                        sources.Add(a);
                        break;
                    }
            }
            return sources;
        }

        /// <summary>The sheet FrameKey of an action's first pose, or null when the fixture has no such action.
        /// Read AFTER PosesToComposite has run, so the AnchorToTop part of the key is already set.</summary>
        private static string FirstPoseKey(ShimejiConfig config, string actionName)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Name, actionName, StringComparison.Ordinal)
                    && a.Animations.Count > 0 && a.Animations[0].Poses.Count > 0)
                    return a.Animations[0].Poses[0].FrameKey;
            return null;
        }

        /// <summary>True when a tile has ANY sprite pixel. A tile that is entirely the transparency key
        /// renders as an invisible pet, which no other check in the pipeline can see.</summary>
        private static bool TileIsPainted(SpriteSheet sheet, int index)
        {
            if (sheet == null || index < 0) return false;
            int col = index % sheet.TilesX;
            int row = index / sheet.TilesX;
            using (var ms = new System.IO.MemoryStream(sheet.PngBytes, false))
            using (var bmp = new Bitmap(ms))
            {
                int x0 = col * sheet.CellWidth;
                int y0 = row * sheet.CellHeight;
                // Every 2nd pixel: enough to catch a fully blank tile without scanning the whole sheet once
                // per frame reference.
                for (int y = y0; y < y0 + sheet.CellHeight && y < bmp.Height; y += 2)
                    for (int x = x0; x < x0 + sheet.CellWidth && x < bmp.Width; x += 2)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (c.A != 0 && !(c.R == 255 && c.G == 0 && c.B == 255)) return true;
                    }
                return false;
            }
        }

        /// <summary>True when the given row WITHIN this frame's tile has sprite pixels on it, i.e. anything
        /// other than the magenta key the compositor clears the background to.</summary>
        private static bool TileRowIsPainted(SpriteSheet sheet, string frameKey, int rowInCell)
        {
            int index;
            if (sheet == null || !sheet.FrameIndexByKey.TryGetValue(frameKey, out index)) return false;
            if (rowInCell < 0 || rowInCell >= sheet.CellHeight) return false;
            int col = index % sheet.TilesX;
            int row = index / sheet.TilesX;
            using (var ms = new System.IO.MemoryStream(sheet.PngBytes, false))
            using (var bmp = new Bitmap(ms))
            {
                int y = row * sheet.CellHeight + rowInCell;
                if (y >= bmp.Height) return false;
                int x0 = col * sheet.CellWidth;
                for (int x = x0; x < x0 + sheet.CellWidth && x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (!(c.R == 255 && c.G == 0 && c.B == 255)) return true;
                }
                return false;
            }
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
    <!-- Wall region. The Condition makes this Group2 ON PURPOSE: the reference conf's ClimbWall is Group2 for
         exactly this reason, and a Group1-only wall filter silently produced a pet that grabs a wall and hangs
         there motionless. Negative Velocity y is the climb, and the anchor matches the floor poses. -->
    <Action Name=""ClimbWall"" Type=""Move"" BorderType=""Wall"">
      <Animation Condition=""#{mascot.anchor.y &gt; 100}"">
        <Pose Image=""/c1.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
        <Pose Image=""/c2.png"" ImageAnchor=""20,60"" Velocity=""0,-2"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A DESCENDING wall pose, so the ceiling has somewhere to hand back to. Without one the ceiling exit
         would fall back to the climb and send the pet straight back into the border it just left. -->
    <Action Name=""DescendWall"" Type=""Move"" BorderType=""Wall"">
      <Animation>
        <Pose Image=""/c2.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
        <Pose Image=""/c1.png"" ImageAnchor=""20,60"" Velocity=""0,2"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- Ceiling region. The anchor is deliberately 20,24 rather than the floor's 20,60, mirroring the
         reference conf's 64,48-vs-64,128: for a hanging mascot the contact point is near the TOP of the
         sprite. That difference is the whole reason ceiling poses need AnchorToTop compositing. -->
    <Action Name=""ClimbCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/k1.png"" ImageAnchor=""20,24"" Velocity=""-2,0"" Duration=""4"" />
        <Pose Image=""/k2.png"" ImageAnchor=""20,24"" Velocity=""-2,0"" Duration=""4"" />
      </Animation>
    </Action>
    <!-- A BOTTOM-anchored ceiling pose, which is what every Android bundle produces: the bundle format
         anchors every pose bottom-centre, so the anchor carries no ceiling meaning. Skipping AnchorY source
         rows here skipped the entire sprite and emitted a blank tile. That shipped in 1.9.4 and was only
         caught by eye on Kopo, because the fixture had only top-anchored ceiling poses. -->
    <Action Name=""HangCeiling"" Type=""Move"" BorderType=""Ceiling"">
      <Animation>
        <Pose Image=""/k3.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
        <Pose Image=""/k4.png"" ImageAnchor=""20,60"" Velocity=""2,0"" Duration=""4"" />
      </Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
