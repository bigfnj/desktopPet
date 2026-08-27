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

        // A locomotion sequence's repeat count is chosen so the whole walk lasts about this long before the
        // pet re-decides (keep walking / rest / turn), regardless of how slow the source frames are. The old
        // fixed count ran a slow animation (a multi-second Creep) for 6 repeats = 7 passes = ~36s of gliding
        // in one direction -- the "walks weird" report. Total time is (repeat+1) * one-pass duration (see
        // AnimationRuntimeLimits.CalculateTotalSteps: frameCount + frameCount*repeat with repeatfrom 0), so
        // repeat = round(target / passMs) - 1, bounded below by a single pass and above by the old ceiling.
        private const int TargetLocoMs = 2500;
        private const int MinLocoRepeats = 0;   // 0 = play once (AnimationXML: a value of 0 or 1 is no-repeat)
        private const int MaxLocoRepeats = 6;   // the previous fixed value; a fast walk never runs longer than before

        public static ConversionResult Emit(ShimejiConfig config, SpriteSheet sheet, Func<string, Bitmap> load, string skinName, Func<string, byte[]> loadSound = null)
        {
            var result = new ConversionResult { Residue = new ResidueReport() };
            skinName = string.IsNullOrWhiteSpace(skinName) ? "Shimeji" : skinName.Trim();

            // --- gather sprite-bearing primitives and the magic sources ---
            ShimejiAction fallAction = FirstWithClass(config, "Fall");
            ShimejiAction dragAction = FirstWithClass(config, "Dragged");

            var spokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (!IsFloorAction(a)) continue;               // the floor region (no ceiling/embedded)
                if (a == fallAction) continue;                 // becomes the 'fall' magic animation
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;               // no sprites (e.g. Look/Offset) -> not an animation
                spokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames });
            }

            // The WALL region: a separate set, deliberately NOT reachable from the floor hub. Entry is only
            // ever a vertical-border edge on a locomotion animation, which is what stops a wall-cling from
            // playing in the middle of the screen -- the reason wall actions were excluded outright before.
            var wallSpokes = new List<Emitted>();
            foreach (ShimejiAction a in config.Actions)
            {
                if (!IsWallAction(a)) continue;
                List<int> frames = FramesOf(a, sheet);
                if (frames.Count == 0) continue;
                wallSpokes.Add(new Emitted { Name = SanitizeName(a.Name), Source = a, Frames = frames });
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
            all.AddRange(wallSpokes);

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
            Dictionary<string, int> spokeWeights = BuildSpokeWeights(config);
            foreach (Emitted s in spokes) s.Weight = HubWeightFor(s, hub, spokeWeights);
            // Then guarantee a minimum share, which needs the WHOLE set and so cannot live in HubWeightFor.
            // Without it the damped weights still leave a long tail that is technically reachable and
            // practically invisible: the shipped corpus had 392 of 609 hub options below 1%, the worst at
            // 0.03% (~54 minutes of idling to appear once).
            ApplyHubMinimumShare(spokes, hub);

            // Where a floor walker enters the wall. Prefer a climbing (Move) primitive over a static grab, so
            // hitting an edge actually goes somewhere; null when the skin has no wall sprites, in which case
            // everything below degrades to the previous floor-only behaviour.
            Emitted wallEntry = wallSpokes.FirstOrDefault(e => e.Source != null && ClimbsUpward(e.Source))
                                ?? wallSpokes.FirstOrDefault();

            var nodes = new List<AnimationNode>();
            foreach (Emitted e in spokes)
                nodes.Add(BuildSpoke(e, hub, fall, turn, wallEntry));
            foreach (Emitted e in wallSpokes)
                nodes.Add(BuildWallSpoke(e, fall, wallSpokes));
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
                    // "Alpha" is the host's reserved keyword (Xml.AlphaTransparencyKeyword) selecting the
                    // per-pixel render path; any real colour name keeps the magenta colour-key path.
                    Transparency = sheet.IsAlpha ? "Alpha" : "Magenta",
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

            // --- sounds: transcode each sounded action's clip to MP3 and attach it to that animation. The
            // desktopPet format ties a sound to an animation (played at its start), so a per-pose clip is
            // mapped to the whole animation via its FIRST sounded pose; per-pose timing is not reproduced. All
            // best-effort and budgeted by the loader -- a missing clip / no transcoder just leaves it silent. ---
            int soundWanted = 0, soundCaptured = 0;
            var soundNodes = new List<SoundNode>();
            foreach (Emitted e in all)
            {
                if (e.Source == null) continue;
                string clip = FirstSoundClip(e.Source);
                if (clip == null) continue;
                soundWanted++;
                if (loadSound == null) continue;
                byte[] mp3;
                try { mp3 = loadSound(clip); } catch { mp3 = null; }
                if (mp3 == null || mp3.Length == 0) continue;
                soundNodes.Add(new SoundNode
                {
                    Id = e.Id,
                    Probability = 100,
                    Loop = 0,
                    Base64 = Convert.ToBase64String(mp3),
                });
                soundCaptured++;
            }
            if (soundNodes.Count > 0)
                root.Sounds = new SoundsNode { Sound = soundNodes.ToArray() };

            BuildResidue(config, result.Residue, sheet.IsAlpha);
            AppendSoundResidue(result.Residue, soundWanted, soundCaptured, loadSound != null);

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
            public int Weight = HubBaseWeight; // how often the hub selects this spoke (source behaviour frequency + baseline)
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
        /// <summary>
        /// A wall primitive: Group1, has sprites, not Embedded, and its border context is Wall.
        ///
        /// Deliberately does NOT inherit IsFloorAction's rejection of upward velocity. On the floor a negative
        /// VelY launches the pet off the top of the screen, which is why that guard exists; on a wall climbing
        /// up IS the behaviour. The separation is what makes it safe to allow one and not the other.
        ///
        /// Wall poses need no anchor rework: the reference conf anchors ClimbWall and GrabWall at the same
        /// 64,128 as Stand and Walk, so admitting them to the sheet cannot change cell geometry. (Ceiling poses
        /// anchor at 64,48 and DO need normalising, which is why they are a separate step.)
        /// </summary>
        internal static bool IsWallAction(ShimejiAction a)
        {
            if (a == null) return false;
            // Group1 OR Group2, unlike the floor region. Group2 means "the SELECTION CONDITION needs host
            // state we do not have" (ShimejiModel: a condition referencing cursor/anchor/activeIE), not "the
            // animation is unconvertible" -- and the wall region replaces Shimeji's conditional selection with
            // its own border-driven graph, so that condition was never going to be honoured either way. The
            // emitter already discards conditions everywhere by using Animations[0] only.
            // Without this, the reference conf contributes GrabWall (Group1) but NOT ClimbWall (Group2), so
            // the pet grabs the wall and hangs there motionless -- half a feature.
            // Group3 stays out: those are Embedded classes (window throwing, breeding), which is code.
            if (a.Group != FidelityGroup.Group1 && a.Group != FidelityGroup.Group2) return false;
            if (a.Animations.Count == 0 || a.Animations[0].Poses.Count == 0) return false;
            if (a.Class != null) return false;
            return string.Equals(a.BorderType, "Wall", StringComparison.Ordinal);
        }

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
                // Wall poses are included now. Safe for the cell geometry this comment warns about, because
                // they carry the SAME anchor as the floor poses (64,128 in the reference conf); it is the
                // CEILING anchor (64,48) that would pad the cell, and ceiling is still excluded here.
                if (!(IsFloorAction(a) || IsWallAction(a) || a == fall || a == drag)) continue;
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

        /// <summary>Does this wall action actually move UP the wall? Used to prefer a climb over a static grab
        /// as the wall entry point.</summary>
        private static bool ClimbsUpward(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return false;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && p.VelY < 0) return true;
            return false;
        }

        // Relative weights on a locomotion animation's border edge. The turn is only="none" so it is eligible
        // at EVERY border; the climb is only="vertical" so it competes only at a left/right screen edge, where
        // these weights make it win 1 in 3. Everywhere else the pet still just turns around.
        private const int BorderTurnWeight = 2;
        private const int BorderClimbWeight = 1;

        // How much of a wall animation's own sequence is spent climbing before it re-decides. Kept short so a
        // pet does not commit to one long unbroken climb.
        private const int WallRepeatCount = 3;

        /// <summary>
        /// One wall animation. Two things make this different from a floor spoke, and both are load-bearing:
        ///
        ///   * NO &lt;gravity&gt; element. Presence of that element is what tells the engine to fall when
        ///     nothing is underneath, so OMITTING it is precisely what keeps the pet clinging to the wall.
        ///     This is how the hand-authored sheep do it (wall_slide has no gravity node).
        ///   * The border edge is only="none" -> fall, so reaching the top, the taskbar or a window edge means
        ///     letting go. Ceiling behaviour will later refine the only="horizontal" case specifically.
        /// </summary>
        private static AnimationNode BuildWallSpoke(Emitted e, Emitted fall, IList<Emitted> wallSpokes)
        {
            List<ShimejiPose> poses = e.Source != null && e.Source.Animations.Count > 0
                ? e.Source.Animations[0].Poses : new List<ShimejiPose>();
            // Only the VERTICAL component is kept: horizontal motion on a wall would walk the pet off it, so
            // the source's VelX is read and discarded on purpose rather than never read.
            int vy0 = poses.Count > 0 ? poses[0].VelY : 0;
            int vyN = poses.Count > 0 ? poses[poses.Count - 1].VelY : 0;
            int iv0 = poses.Count > 0 ? Interval(poses[0].Duration) : 200;
            int ivN = poses.Count > 0 ? Interval(poses[poses.Count - 1].Duration) : 200;

            var next = new List<NextNode>();
            foreach (Emitted other in wallSpokes)
                next.Add(Next(other.Id, other == e ? 60 : 20, "none"));   // keep climbing, or switch wall pose
            if (fall != null) next.Add(Next(fall.Id, 25, "none"));        // or let go

            var node = new AnimationNode
            {
                Id = e.Id,
                Name = e.Name,
                Start = Moving(0, vy0, iv0, 1.0),
                End = Moving(0, vyN, ivN, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = WallRepeatCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Frame = e.Frames.ToArray(),
                    Next = next.ToArray(),
                },
            };
            // Deliberately no Gravity: that is the cling. Border = let go and fall.
            if (fall != null)
                node.Border = new HitNode { Next = new[] { Next(fall.Id, 100, "none") } };
            return node;
        }

        private static AnimationNode BuildSpoke(Emitted e, Emitted hub, Emitted fall, Emitted turn, Emitted wallEntry)
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
            else if (loco) next = new[] { Next(e.Id, 65, "none"), Next(hub.Id, 35, "none") }; // keep walking (same heading), or return to the hub to re-decide
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
                    RepeatCount = loco
                        ? LocoRepeatCount(SequencePassMs(poses)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "0",
                    Frame = e.Frames.ToArray(),
                    Next = next,
                },
            };
            if (fall != null && e != fall)
                node.Gravity = new HitNode { Next = new[] { Next(fall.Id, 100, "none") } };
            if (loco)
            {
                // Reach an edge -> turn (flip) and head back. At a LEFT/RIGHT screen edge specifically, the
                // pet may instead grab the wall and climb: both entries are eligible there, so the weights
                // decide (climb wins 1 in 3). At any other border only the only="none" turn matches, so
                // behaviour away from the walls is exactly what it was.
                var borderNext = new List<NextNode> { Next(turn.Id, BorderTurnWeight, "none") };
                if (wallEntry != null) borderNext.Add(Next(wallEntry.Id, BorderClimbWeight, "vertical"));
                node.Border = new HitNode { Next = borderNext.ToArray() };
            }
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

        // The hub can reach every spoke (so nothing is orphaned), weighted by each spoke's source behaviour
        // frequency: a character that walks and runs a lot in Shimeji does so here too, instead of a flat pick
        // that gave its many idle poses the same odds as its few movement ones (the "shuffles animations but
        // never goes anywhere" report).
        private static NextNode[] HubChoices(Emitted hub)
        {
            var list = new List<NextNode>();
            // handled by caller passing the full spoke list via a closure-free field:
            foreach (Emitted s in HubSpokes)
                list.Add(Next(s.Id, Math.Max(1, s.Weight), "none"));
            if (list.Count == 0) list.Add(Next(hub.Id, 100, "none"));
            return list.ToArray();
        }

        // The hub picks its next action weighted by the source's behaviour frequency (ShimejiConfig
        // .BehaviorFrequency; a behaviour's name is the action it runs), plus a small baseline so every spoke
        // stays reachable -- the acceptance check requires it -- even one the source only ever reaches as a
        // transition (Frequency 0 at root). The hub's OWN re-selection stays at the baseline: it is also every
        // spoke's return target, and folding the stand behaviour's often-high frequency back in here would just
        // make the pet loiter on the hub between actions rather than getting on with the next one.
        /// <summary>The header author every converted pet carries. The reweight migration matches on this to
        /// be certain it only ever touches converter output, never a hand-authored pet.</summary>
        public const string ConvertedAuthor = "Converted from a Shimeji skin";

        /// <summary>Header version stamped by the CURRENT emitter. See BuildHeader for why it matters.</summary>
        public const string ConvertedFormatVersion = "1.1";

        /// <summary>The version emitted before the hub weighting was damped and floored; what the reweight
        /// migration looks for.</summary>
        public const string ConvertedFormatVersionFlatWeights = "1.0";

        public const int HubBaseWeight = 4;
        private const int MaxResolveDepth = 8;   // guard the reference walk against deep or cyclic composites

        /// <summary>Smallest share of the hub's pool any single spoke may have, as a percentage.</summary>
        public const double HubMinimumSharePercent = 1.5;

        /// <summary>Damping applied to a source frequency before it becomes a weight.</summary>
        private const double HubFrequencyScale = 3.0;

        /// <summary>
        /// Turn an accumulated source behaviour frequency into a hub selection weight.
        ///
        /// The square root is the point. BuildSpokeWeights SUMS a frequency every time a behaviour references
        /// an action, so locomotion (referenced by many composites) accumulated to ~1100 while a one-off pose
        /// stayed at the baseline of 4 -- a 326x spread nobody chose, which fell out of the summing. Taking the
        /// root keeps the ORDERING (a character that walks a lot still walks a lot) while collapsing the range
        /// to roughly 10-25x, and the minimum-share pass then bounds the tail.
        ///
        /// Public because the reweight migration must apply the identical curve to already-emitted pets; two
        /// implementations of this would drift, exactly as two copies of the walk-time budget would.
        /// </summary>
        public static int HubWeightFromFrequency(int frequency)
        {
            if (frequency <= 0) return HubBaseWeight;
            return HubBaseWeight + (int)Math.Round(HubFrequencyScale * Math.Sqrt(frequency),
                MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Raise the smallest weights until none holds less than <see cref="HubMinimumSharePercent"/> of the
        /// pool, so every animation actually plays. Converges because lifting a floored entry raises its own
        /// share faster than it inflates the total; the iteration cap is belt-and-braces.
        ///
        /// <paramref name="excludeIndex"/> is the hub's own re-selection edge and is deliberately left at the
        /// baseline: the hub is also every spoke's RETURN target, so weighting it up makes the pet loiter on
        /// the hub between actions instead of getting on with the next one.
        /// </summary>
        public static void ApplyMinimumShare(IList<int> weights, int excludeIndex, double minimumPercent)
        {
            if (weights == null || weights.Count == 0 || minimumPercent <= 0) return;
            for (int pass = 0; pass < 64; pass++)
            {
                long total = 0;
                for (int i = 0; i < weights.Count; i++) total += weights[i];
                if (total <= 0) return;

                int need = (int)Math.Ceiling(total * minimumPercent / 100.0);
                bool changed = false;
                for (int i = 0; i < weights.Count; i++)
                {
                    if (i == excludeIndex) continue;
                    if (weights[i] < need) { weights[i] = need; changed = true; }
                }
                if (!changed) return;
            }
        }

        private static void ApplyHubMinimumShare(List<Emitted> spokes, Emitted hub)
        {
            if (spokes == null || spokes.Count == 0) return;
            var weights = new List<int>(spokes.Count);
            int hubIndex = -1;
            for (int i = 0; i < spokes.Count; i++)
            {
                weights.Add(spokes[i].Weight);
                if (spokes[i] == hub) hubIndex = i;
            }
            ApplyMinimumShare(weights, hubIndex, HubMinimumSharePercent);
            for (int i = 0; i < spokes.Count; i++) spokes[i].Weight = weights[i];
        }

        private static int HubWeightFor(Emitted e, Emitted hub, Dictionary<string, int> spokeWeights)
        {
            if (e == hub) return HubBaseWeight;
            int freq = 0;
            if (spokeWeights != null && e.Source != null && e.Source.Name != null)
                spokeWeights.TryGetValue(e.Source.Name, out freq);
            return HubWeightFromFrequency(freq);
        }

        // Turn root behaviour frequencies into per-spoke selection weights. A <Behavior Name="X" Frequency="N">
        // names action X, which is usually a Sequence/Select composite whose sprites live on the low-level
        // posed actions it references; walk those references down to the actual floor spokes and credit each
        // with N. A behaviour that already names a posed floor action credits it directly. Frequencies
        // accumulate, so an action many behaviours play (Walk is reached by walk-and-sit, walk-and-jump, ...)
        // ends up correctly dominant -- which is what makes the pet actually move.
        private static Dictionary<string, int> BuildSpokeWeights(ShimejiConfig config)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (config == null || config.BehaviorFrequency.Count == 0) return result;

            var byName = new Dictionary<string, ShimejiAction>(StringComparer.Ordinal);
            foreach (ShimejiAction a in config.Actions)
                if (a != null && a.Name != null && !byName.ContainsKey(a.Name)) byName[a.Name] = a;

            foreach (KeyValuePair<string, int> kv in config.BehaviorFrequency)
            {
                var spokes = new HashSet<string>(StringComparer.Ordinal);
                ResolveSpokes(kv.Key, byName, spokes, new HashSet<string>(StringComparer.Ordinal), 0);
                foreach (string spoke in spokes)
                {
                    int current;
                    result.TryGetValue(spoke, out current);
                    result[spoke] = current + kv.Value;
                }
            }
            return result;
        }

        // Resolve an action name to the set of floor spokes it (transitively) plays. A directly-posed floor
        // action IS a spoke; a composite recurses into the actions it references. Bounded depth + a visited set
        // guard malformed self-referential configs.
        private static void ResolveSpokes(string name, Dictionary<string, ShimejiAction> byName,
            HashSet<string> outSpokes, HashSet<string> visited, int depth)
        {
            if (name == null || depth > MaxResolveDepth || !visited.Add(name)) return;
            ShimejiAction a;
            if (!byName.TryGetValue(name, out a)) return;
            if (IsFloorAction(a)) { outSpokes.Add(name); return; }
            foreach (string reference in a.ReferencedActions)
                ResolveSpokes(reference, byName, outSpokes, visited, depth + 1);
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
                // Constant terminal velocity, not an accelerating start->end ramp: the sequence self-loops
                // (below), and a ramp would snap back to the slow start speed on every loop and visibly pulse.
                Start = Moving(0, 10, 40, 1.0),
                End = Moving(0, 10, 40, 1.0),
                Sequence = new SequenceNode
                {
                    RepeatFromFrame = 0,
                    RepeatCount = "20",
                    Frame = fall.Frames.ToArray(),
                    // Keep falling: when the repeats run out mid-air, restart THIS animation (a seamless 1-tick
                    // restart), never hand back to a standing hub. A hub carries gravity->fall, so that round
                    // trip is the ~2s "thinks it hit the floor then keeps falling" stutter. This is esheep64's
                    // canonical self-looping fall.
                    Next = new[] { Next(fall.Id, 100, "none") },
                },
                // The one exit: the instant the pet reaches the floor / a border, land at the hub.
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

            // Keep the character's real name (the validator allows up to 128); only guard against something
            // absurdly long. The Pets gallery reads this <petname> as the display label.
            string petname = skinName.Length > 60 ? skinName.Substring(0, 60) : skinName;
            return new HeaderNode
            {
                Author = ConvertedAuthor,
                Title = skinName + " (converted)",
                Petname = petname,
                // Not decoration: the reweight migration recovers a source frequency as (weight -
                // HubBaseWeight), which only holds for a pet emitted BEFORE the damped curve. It therefore
                // rewrites 1.0 -> 1.1 and skips anything already at 1.1, and new conversions start at 1.1 so
                // they are never re-curved. Bump this if the hub weighting ever changes shape again.
                Version = ConvertedFormatVersion,
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

        private static void BuildResidue(ShimejiConfig config, ResidueReport residue, bool alpha)
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
                         && !IsFloorAction(a) && !IsWallAction(a) && a != fallAction && a != dragAction)
                    notOnFloor.Add(a.Name);   // ceiling / jump primitives (wall is converted now)
            }

            // Wall actions that DID convert, so the report can say what the pet gained rather than only what
            // it lost. Reported separately from notOnFloor because they are no longer residue at all.
            var wallConverted = new List<string>();
            foreach (ShimejiAction a in config.Actions)
                if (IsWallAction(a) && a.Animations[0].Poses.Count > 0) wallConverted.Add(a.Name);

            int condNeedsState = config.BehaviorConditions.Count(c => c.Group == FidelityGroup.Group2);
            if (alpha)
                residue.Notes.Add("Smooth edges are preserved: this pet keeps its real (anti-aliased) transparency and the app renders it per-pixel. Because that uses a desktopPet-only render mode, this converted pet will not run in web-esheep or other magenta-key players.");
            else
                residue.Notes.Add("Sprite edges are hard, not anti-aliased: the app renders every pet with a 1-bit magenta transparency key (a pixel is either shown or invisible, no partial transparency), so soft/smooth edges cannot be preserved -- mild for hard-outlined art, more visible on glows or shadows.");
            residue.Notes.Add("The pet gets a coherent FLOOR behaviour (idle / walk-and-turn / fall / drag). Shimeji's full conditional behaviour selection (its Markov chain and " + condNeedsState + " state-dependent conditions) is not reproduced; it wanders and rests rather than following the original's exact routine.");
            if (wallConverted.Count > 0)
                residue.Notes.Add("Wall climbing IS converted: on reaching a left/right screen edge the pet may grab the wall and climb it, then let go and fall. Converted for this skin: " + string.Join(", ", wallConverted) + ".");
            // Wording matters here. This used to read "Wall, ceiling and jump animations are not represented",
            // which describes a format limitation that does not exist -- the engine handles walls and ceilings
            // (17 of the 22 hand-authored pets use them). Say what is true: these specific animations were not
            // ATTEMPTED, and why.
            if (notOnFloor.Count > 0)
                residue.Notes.Add("Ceiling and jump animations are not attempted yet, so the pet works the floor and the walls but does not hang from the ceiling: " + string.Join(", ", notOnFloor) + ". This is a converter gap rather than a format limit -- the pet format expresses both (only=\"horizontal\" plus an upward climb), and the hand-authored pets use them.");
            residue.Notes.Add("Per-pose velocity is reduced to one start/end pair per animation, and 'walk to a target x' becomes a fixed-length walk that turns at the screen edge.");

            // (Sound residue is appended by AppendSoundResidue after emit, which knows how many clips were
            // actually captured vs dropped -- BuildResidue can only see that a pose named one.)

            int scriptPoses = config.Poses.Count(p => p.ScriptFlattened);
            if (scriptPoses > 0)
                residue.Notes.Add(scriptPoses + " pose(s) used script-computed durations or velocities; these are flattened to fixed values, so their timing or motion is approximate.");

            int scriptActions = config.Actions.Count(a =>
                a.SubtreeBlob != null && (a.SubtreeBlob.Contains("${") || a.SubtreeBlob.Contains("#{")));
            if (scriptActions > 0)
                residue.Notes.Add(scriptActions + " action(s) use script-computed values (${...} / #{...}) for timing, targets or conditions. desktopPet can't evaluate scripts, so those are approximated by fixed timing and a bounded wander, or dropped.");
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

        // One full pass of a sequence, in ms: the sum of every pose's clamped frame interval -- the artist's
        // intended pace for one play-through, which is what the walk-time budget is measured against.
        private static int SequencePassMs(List<ShimejiPose> poses)
        {
            if (poses == null || poses.Count == 0) return 0;
            long total = 0;
            foreach (ShimejiPose p in poses) total += Interval(p != null ? p.Duration : 0);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        // How many times to REPEAT a locomotion sequence so the whole walk lasts ~TargetLocoMs. Total passes
        // is repeat+1, so repeat = round(target / passMs) - 1, clamped to [MinLocoRepeats, MaxLocoRepeats].
        // Shared by the emitter and the `rebalance` migration so shipped and freshly-converted pets use one
        // policy. passMs <= 0 (unknown timing) keeps the old ceiling.
        public static int LocoRepeatCount(int passMs)
        {
            if (passMs <= 0) return MaxLocoRepeats;
            int passes = (int)Math.Round((double)TargetLocoMs / passMs, MidpointRounding.AwayFromZero);
            int repeat = passes - 1;
            if (repeat < MinLocoRepeats) repeat = MinLocoRepeats;
            if (repeat > MaxLocoRepeats) repeat = MaxLocoRepeats;
            return repeat;
        }

        // The first pose in an action's first animation that carries a Sound clip, or null. The pet format
        // attaches a sound to an animation (played at its start), so one representative clip per animation is
        // the honest mapping of Shimeji's per-pose sounds.
        private static string FirstSoundClip(ShimejiAction a)
        {
            if (a == null || a.Animations.Count == 0) return null;
            foreach (ShimejiPose p in a.Animations[0].Poses)
                if (p != null && !string.IsNullOrWhiteSpace(p.Sound)) return p.Sound;
            return null;
        }

        private static void AppendSoundResidue(ResidueReport residue, int wanted, int captured, bool attempted)
        {
            if (wanted <= 0) return;
            if (captured == wanted)
                residue.Notes.Add(captured + " animation sound(s) captured -- transcoded to MP3 and embedded; " +
                    "they play at each animation's start (Shimeji's per-pose sound timing is not reproduced).");
            else if (captured > 0)
                residue.Notes.Add(captured + " of " + wanted + " animation sound(s) captured (MP3, played at " +
                    "animation start); the rest were dropped -- the per-pet audio budget, or an unreadable/oversize clip.");
            else
                residue.Notes.Add(wanted + " animation(s) carry sound, but none was captured (" +
                    (attempted ? "the clips were missing or over the audio budget" : "no MP3 transcoder was available") +
                    "), so the pet is silent.");
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
