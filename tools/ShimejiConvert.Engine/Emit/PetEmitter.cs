using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DesktopPet.Tools.ShimejiConvert.Shimeji;
using XmlData;

namespace DesktopPet.Tools.ShimejiConvert.Emit
{
    /// <summary>The output of a conversion: the pet, its serialized XML, the honest loss report, and the
    /// reachability verdict the acceptance check reads.</summary>
    public sealed class ConversionResult
    {
        public RootNode Root;
        public string EmittedXml;
        public ResidueReport Residue;
        public GraphReport Graph;
        public bool Valid;         // the app's own validator accepted the emitted XML
        public bool RoundTrips;    // survives serialize -> re-validate
        public string Error;       // validator/emit error, if any

        /// <summary>The machine-checkable acceptance bar: validates, round-trips, and every animation is
        /// reachable (terminals are allowed -- they respawn by design).</summary>
        public bool Accepted { get { return Valid && RoundTrips && Graph != null && Graph.Unreachable.Count == 0; } }
    }

    /// <summary>
    /// Builds a valid, reachable desktopPet pet from a parsed Shimeji skin + its composited sheet.
    ///
    /// v1 is a HUB-AND-SPOKE state machine: each Group1 primitive with sprites becomes one animation, a
    /// standing pose is the hub, and the hub can reach every spoke (which returns to it). This uses every
    /// sprite and produces a lively pet that walks / sits / falls / is draggable, but it does NOT reproduce
    /// Shimeji's conditional Markov behaviour selection -- that simplification is stated in the residue, not
    /// hidden. Group2 actions are recorded as degraded, Group3 as dropped, and the four magic names
    /// (fall / drag / kill / sync) are emitted (kill/sync synthesised, as Shimeji has no equivalent).
    /// </summary>
    public static class PetEmitter
    {
        private const int TickMs = 40;
        private const int MinInterval = 20;
        private const int MaxInterval = 4000;   // cap idle holds at a restful-but-not-frozen 4s

        public static ConversionResult Emit(ShimejiConfig config, SpriteSheet sheet, Func<string, Bitmap> load, string skinName)
        {
            var result = new ConversionResult { Residue = new ResidueReport() };
            skinName = string.IsNullOrWhiteSpace(skinName) ? "Shimeji" : skinName.Trim();

            // --- gather sprite-bearing primitives and the magic sources ---
            ShimejiAction fallAction = FirstWithClass(config, "Fall");
            ShimejiAction dragAction = FirstWithClass(config, "Dragged");

            var spokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (!IsFloorAction(a)) continue;               // coherent floor behaviour only (no wall/ceiling/embedded)
                if (a == fallAction) continue;                 // becomes the 'fall' magic animation
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;               // no sprites (e.g. Look/Offset) -> not an animation
                spokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames });
            }

            // A hub every spoke can return to. Prefer a standing pose.
            Emitted hub = spokes.FirstOrDefault(e => e.Source != null && string.Equals(e.Source.Name, "Stand", StringComparison.OrdinalIgnoreCase))
                          ?? spokes.FirstOrDefault();
            if (hub == null)
            {
                // Degenerate skin with no usable primitive: synthesise a one-frame stand on tile 0.
                hub = new Emitted { Name = "stand", Source = null, Frames = new List<int> { 0 } };
                spokes.Add(hub);
            }

            // --- assemble the ordered animation list and assign ids ---
            var all = new List<Emitted>();
            all.AddRange(spokes);

            Emitted fall = null, drag = null;
            if (fallAction != null)
            {
                List<int> f = FramesOf(fallAction, sheet);
                if (f.Count > 0) fall = new Emitted { Name = "fall", Source = fallAction, Frames = f };
            }
            if (fall == null) fall = new Emitted { Name = "fall", Source = null, Frames = hub.Frames };
            all.Add(fall);

            if (dragAction != null)
            {
                List<int> d = FramesOf(dragAction, sheet);
                if (d.Count > 0) drag = new Emitted { Name = "drag", Source = dragAction, Frames = d };
            }
            if (drag == null) drag = new Emitted { Name = "drag", Source = null, Frames = hub.Frames };
            all.Add(drag);

            var kill = new Emitted { Name = "kill", Source = null, Frames = hub.Frames };
            var sync = new Emitted { Name = "sync", Source = null, Frames = hub.Frames };
            // A one-frame "turn" that flips facing, so a walker reaching a screen edge turns and heads back
            // instead of standing against the wall doing idles forever.
            var turn = new Emitted { Name = "turn", Source = null, Frames = hub.Frames };
            all.Add(kill);
            all.Add(sync);
            all.Add(turn);

            for (int i = 0; i < all.Count; i++) all[i].Id = i + 1;

            // --- build each animation node ---
            HubSpokes = spokes;   // so the hub's <next> set can reach every spoke
            var nodes = new List<AnimationNode>();
            foreach (Emitted e in spokes)
                nodes.Add(BuildSpoke(e, hub, fall, turn));
            nodes.Add(BuildFall(fall, hub));
            nodes.Add(BuildDrag(drag, fall));
            nodes.Add(BuildKill(kill));
            nodes.Add(BuildSync(sync, hub));
            nodes.Add(BuildTurn(turn, hub));

            // --- header (with a generated icon from the hub's first sprite) ---
            var header = BuildHeader(skinName, config, hub, load);

            var root = new RootNode
            {
                Header = header,
                Image = new ImageNode
                {
                    TilesX = sheet.TilesX,
                    TilesY = sheet.TilesY,
                    Png = sheet.Base64Png,
                    Transparency = "Magenta",
                },
                Spawns = new SpawnsNode
                {
                    Spawn = new[]
                    {
                        // Both spawns land ON-SCREEN (a spawn's <next> takes probability but NOT only): one
                        // drops in from the top, one appears standing on the floor. An off-screen "walk in
                        // from the edge" needs a locomotion next -- routing it to the stationary hub left the
                        // pet standing off-screen and invisible.
                        new SpawnNode { Id = 1, Probability = 50, X = "random*(screenW-imageW-50)/100+25", Y = "-imageH-20", Next = Next(fall.Id, 100, null) },
                        new SpawnNode { Id = 2, Probability = 50, X = "random*(screenW-imageW-50)/100+25", Y = "areaH-imageH", Next = Next(hub.Id, 100, null) },
                    },
                },
                Animations = new AnimationsNode { Animation = nodes.ToArray() },
                Childs = new ChildsNode(),   // Breed is Group3 -> no children
            };

            BuildResidue(config, result.Residue);

            // --- validate + round-trip + reachability ---
            result.Root = root;
            result.EmittedXml = ShimejiEngine.Serialize(root);
            RootNode reparsed;
            string error;
            result.Valid = ShimejiEngine.TryValidate(result.EmittedXml, out reparsed, out error);
            result.Error = error;
            if (result.Valid)
            {
                result.Graph = ShimejiEngine.Analyze(reparsed);
                string rtError;
                result.RoundTrips = ShimejiEngine.RoundTrips(reparsed, out rtError);
                if (!result.RoundTrips && string.IsNullOrEmpty(result.Error)) result.Error = rtError;
            }
            else
            {
                result.Graph = ShimejiEngine.Analyze(root);
            }
            return result;
        }

        private sealed class Emitted
        {
            public int Id;
            public string Name;
            public ShimejiAction Source; // null for synthesised animations
            public List<int> Frames;
        }

        private static ShimejiAction FirstWithClass(ShimejiConfig config, string shortClass)
        {
            foreach (ShimejiAction a in config.Actions)
                if (string.Equals(a.Class, shortClass, StringComparison.Ordinal)) return a;
            return null;
        }

        // A floor-appropriate primitive: Group1, has sprites, not Embedded (Look/Offset/Fall/Dragged/Jump/
        // Regist), and Floor (or unset) border context. Wall/ceiling/climb primitives are excluded from the
        // emitted behaviour -- they would play nonsensically on the floor -- and recorded in the residue.
        internal static bool IsFloorAction(ShimejiAction a)
        {
            if (a == null || a.Group != FidelityGroup.Group1) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;   // Embedded actions are handled as magic or excluded
            if (a.BorderType != null && !string.Equals(a.BorderType, "Floor", StringComparison.Ordinal)) return false;
            // Reject anything that moves UPWARD: those are climbs / jumps / flings (e.g. PullUpShimeji2's
            // 20,-20), which on the floor would launch the pet off the top of the screen.
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p.VelY < 0) return false;
            return true;
        }

        // The poses the converter will actually use (floor animations + the fall/drag sources), so the
        // compositor sizes the sheet to exactly those frames. That keeps the cell tight with the sprite's
        // feet at the bottom -- otherwise a tall ceiling-pose anchor pads the cell, the pet floats, and
        // ground detection breaks.
        public static List<ShimejiPose> PosesToComposite(ShimejiConfig config)
        {
            var poses = new List<ShimejiPose>();
            ShimejiAction fall = FirstWithClass(config, "Fall");
            ShimejiAction drag = FirstWithClass(config, "Dragged");
            foreach (ShimejiAction a in config.Actions)
            {
                if (!(IsFloorAction(a) || a == fall || a == drag)) continue;
                if (a.Animations.Count > 0)
                    foreach (ShimejiPose p in a.Animations[0].Poses)
                        poses.Add(p);
            }
            return poses;
        }

        private static List<int> FramesOf(ShimejiAction a, SpriteSheet sheet)
        {
            var frames = new List<int>();
            if (a.Animations.Count == 0) return frames;
            // First animation only: multiple <Animation> blocks are conditional alternatives, not a sequence.
            foreach (ShimejiPose p in a.Animations[0].Poses)
            {
                int idx;
                if (p != null && !string.IsNullOrEmpty(p.Image) && sheet.FrameIndexByKey.TryGetValue(p.FrameKey, out idx))
                    frames.Add(idx);
            }
            return frames;
        }

        private static bool IsLocomotion(Emitted e)
        {
            if (e.Source == null || e.Source.Animations.Count == 0) return false;
            foreach (ShimejiPose p in e.Source.Animations[0].Poses)
                if (p.VelX != 0 || p.VelY != 0) return true;
            return false;
        }

        private static AnimationNode BuildSpoke(Emitted e, Emitted hub, Emitted fall, Emitted turn)
        {
            List<ShimejiPose> poses = e.Source != null && e.Source.Animations.Count > 0
                ? e.Source.Animations[0].Poses : new List<ShimejiPose>();
            int vx0 = poses.Count > 0 ? poses[0].VelX : 0;
            int vy0 = poses.Count > 0 ? poses[0].VelY : 0;
            int vxN = poses.Count > 0 ? poses[poses.Count - 1].VelX : 0;
            int vyN = poses.Count > 0 ? poses[poses.Count - 1].VelY : 0;
            int iv0 = poses.Count > 0 ? Interval(poses[0].Duration) : 200;
            int ivN = poses.Count > 0 ? Interval(poses[poses.Count - 1].Duration) : 200;
            bool loco = IsLocomotion(e);

            NextNode[] next;
            if (e == hub) next = HubChoices(hub);
            else if (loco) next = new[] { Next(e.Id, 50, "none"), Next(hub.Id, 50, "none") }; // keep walking, or rest
            else next = new[] { Next(hub.Id, 100, "none") };

            var node = new AnimationNode
            {
                Id = e.Id,
                Name = e.Name,
                Start = Moving(vx0, vy0, iv0, 1.0),
                End = Moving(vxN, vyN, ivN, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = loco ? "6" : "0",
                    Frame = e.Frames.ToArray(),
                    Next = next,
                },
            };
            if (fall != null && e != fall)
                node.Gravity = new HitNode { Next = new[] { Next(fall.Id, 100, "none") } };
            if (loco)
                // Reach a screen edge -> turn (flip) and head back, rather than standing against the wall.
                node.Border = new HitNode { Next = new[] { Next(turn.Id, 100, "none") } };
            return node;
        }

        // Flip facing, then return to the hub (which will pick a walk that now heads the other way).
        private static AnimationNode BuildTurn(Emitted turn, Emitted hub)
        {
            return new AnimationNode
            {
                Id = turn.Id,
                Name = "turn",
                Start = Moving(0, 0, 120, 1.0),
                End = Moving(0, 0, 120, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "0",
                    Frame = turn.Frames.ToArray(),
                    Action = "flip",
                    Next = new[] { Next(hub.Id, 100, "none") },
                },
            };
        }

        // The hub can reach every spoke (so nothing is orphaned) and can stay put.
        private static NextNode[] HubChoices(Emitted hub)
        {
            var list = new List<NextNode>();
            // handled by caller passing the full spoke list via a closure-free field:
            foreach (Emitted s in HubSpokes)
                list.Add(Next(s.Id, s == hub ? 20 : 10, "none"));
            if (list.Count == 0) list.Add(Next(hub.Id, 100, "none"));
            return list.ToArray();
        }

        // Set just before building the hub node so HubChoices can see every spoke without threading it
        // through every helper. Single-threaded emit, so a static is safe and keeps the signatures clean.
        private static List<Emitted> HubSpokes = new List<Emitted>();

        private static AnimationNode BuildFall(Emitted fall, Emitted hub)
        {
            return new AnimationNode
            {
                Id = fall.Id,
                Name = "fall",
                Start = Moving(0, 4, 90, 1.0),
                End = Moving(0, 14, 40, 1.0),   // accelerate downward as it drops
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "20",          // keep falling (loop the frame) while airborne ...
                    Frame = fall.Frames.ToArray(),
                    Next = new[] { Next(hub.Id, 100, "none") },
                },
                // ... and land the instant it reaches the floor / a border, rather than handing straight back
                // to the hub and oscillating with gravity (the "goes nuts on release" bug).
                Border = new HitNode { Next = new[] { Next(hub.Id, 100, "none") } },
            };
        }

        private static AnimationNode BuildDrag(Emitted drag, Emitted fall)
        {
            return new AnimationNode
            {
                Id = drag.Id,
                Name = "drag",
                Start = Moving(0, 0, 100, 1.0),
                End = Moving(0, 0, 100, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "0",
                    Frame = drag.Frames.ToArray(),
                    Next = new[] { Next(fall.Id, 100, "none") },   // released -> fall
                },
            };
        }

        private static AnimationNode BuildKill(Emitted kill)
        {
            // Fade out, then the engine closes the pet (a terminal animation with no next).
            return new AnimationNode
            {
                Id = kill.Id,
                Name = "kill",
                Start = Moving(0, 0, 60, 1.0),
                End = Moving(0, 0, 60, 0.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "3",
                    Frame = kill.Frames.ToArray(),
                    Next = new NextNode[0],
                },
            };
        }

        private static AnimationNode BuildSync(Emitted sync, Emitted hub)
        {
            return new AnimationNode
            {
                Id = sync.Id,
                Name = "sync",
                Start = Moving(0, 0, 100, 1.0),
                End = Moving(0, 0, 100, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "0",
                    Frame = sync.Frames.ToArray(),
                    Next = new[] { Next(hub.Id, 100, "none") },
                },
            };
        }

        private static HeaderNode BuildHeader(string skinName, ShimejiConfig config, Emitted hub, Func<string, Bitmap> load)
        {
            string icon = null;
            try
            {
                string iconImage = HubImage(hub);
                if (load != null && iconImage != null)
                    using (Bitmap src = load(iconImage))
                        icon = Convert.ToBase64String(IconBuilder.BuildIco(src));
            }
            catch { /* icon is best-effort; a null becomes a placeholder below */ }
            if (icon == null)
                using (var blank = new Bitmap(IconBuilder.Size, IconBuilder.Size))
                    icon = Convert.ToBase64String(IconBuilder.BuildIco(blank));

            string petname = skinName.Length > 16 ? skinName.Substring(0, 16) : skinName;
            return new HeaderNode
            {
                Author = "Converted from a Shimeji skin",
                Title = skinName + " (converted)",
                Petname = petname,
                Version = "1.0",
                Info = "Converted from a Shimeji skin by ShimejiConvert. Behaviour is approximated; see the import report for what was simplified or dropped.",
                Application = "1",
                Icon = icon,
            };
        }

        private static string HubImage(Emitted hub)
        {
            if (hub.Source != null && hub.Source.Animations.Count > 0 && hub.Source.Animations[0].Poses.Count > 0)
                return hub.Source.Animations[0].Poses[0].Image;
            return null;
        }

        private static void BuildResidue(ShimejiConfig config, ResidueReport residue)
        {
            ShimejiAction fallAction = FirstWithClass(config, "Fall");
            ShimejiAction dragAction = FirstWithClass(config, "Dragged");
            var notOnFloor = new List<string>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (a.Group == FidelityGroup.Group3)
                    residue.Dropped.Add(new ResidueItem { Name = a.Name, Kind = "dropped", Detail = a.Reason });
                else if (a.Group == FidelityGroup.Group2)
                    residue.Degraded.Add(new ResidueItem { Name = a.Name, Kind = "degraded", Detail = a.Reason });
                else if (a.Group == FidelityGroup.Group1 && a.Animations.Count > 0 && a.Animations[0].Poses.Count > 0
                         && !IsFloorAction(a) && a != fallAction && a != dragAction)
                    notOnFloor.Add(a.Name);   // wall / ceiling / climb / jump primitives
            }

            int condNeedsState = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group2);
            residue.Notes.Add("Sprite edges are hard, not anti-aliased: the app renders every pet with a 1-bit magenta transparency key (a pixel is either shown or invisible, no partial transparency), so soft/smooth edges cannot be preserved -- mild for hard-outlined art, more visible on glows or shadows.");
            residue.Notes.Add("The pet gets a coherent FLOOR behaviour (idle / walk-and-turn / fall / drag). Shimeji's full conditional behaviour selection (its Markov chain and " + condNeedsState + " state-dependent conditions) is not reproduced; it wanders and rests rather than following the original's exact routine.");
            if (notOnFloor.Count > 0)
                residue.Notes.Add("Wall, ceiling and jump animations are not represented -- the converted pet stays on the floor: " + string.Join(", ", notOnFloor) + ".");
            residue.Notes.Add("Per-pose velocity is reduced to one start/end pair per animation, and 'walk to a target x' becomes a fixed-length walk.");
        }

        // ---- small builders ----

        private static MovingNode Moving(int x, int y, int intervalMs, double opacity)
        {
            return new MovingNode
            {
                X = x.ToString(),
                Y = y.ToString(),
                OffsetY = 0,          // anchors are already baked into the sheet
                Opacity = opacity,
                Interval = intervalMs.ToString(),
            };
        }

        private static NextNode Next(int target, int probability, string only)
        {
            return new NextNode { Value = target, Probability = probability, OnlyFlag = only };
        }

        private static int Interval(int durationTicks)
        {
            int ms = durationTicks * TickMs;
            if (ms < MinInterval) ms = MinInterval;
            if (ms > MaxInterval) ms = MaxInterval;
            return ms;
        }

        private static readonly string[] MagicNames = { "fall", "drag", "kill", "sync" };

        private static string SanitizeName(string name)
        {
            string n = string.IsNullOrWhiteSpace(name) ? "anim" : name.Trim();
            foreach (string m in MagicNames)
                if (string.Equals(n, m, StringComparison.OrdinalIgnoreCase)) return n + "_";
            return n;
        }
    }
}
