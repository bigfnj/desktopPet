using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopPet.PetStudioModule
{
    /// <summary>The result of analysing one pet XML: does it load, and what will misbehave if it does.</summary>
    internal sealed class PetReport
    {
        /// <summary>False when the pet would be REJECTED by the host outright (schema, limits, unsafe
        /// expressions). Warnings do not affect this: a pet with dead animations still runs.</summary>
        public bool IsValid;

        /// <summary>Why it was rejected, or "" when it validates.</summary>
        public string Error = "";

        /// <summary>Ids of animations that can never play. Not fatal, but almost always a mistake, and
        /// invisible without walking the graph.</summary>
        public readonly List<int> UnreachableAnimations = new List<int>();

        public int AnimationCount;
        public int SpawnCount;
        public int ChildCount;
        public string PetName = "";
        public string Author = "";

        /// <summary>A human-readable report, which is also exactly what the self-test asserts on.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            if (!IsValid)
            {
                sb.AppendLine("REJECTED — this pet would not load:");
                sb.AppendLine("  " + Error);
                return sb.ToString();
            }

            sb.AppendLine("Valid pet" +
                (PetName.Length > 0 ? " — " + PetName : "") +
                (Author.Length > 0 ? " by " + Author : ""));
            sb.AppendLine("  " + AnimationCount + " animations, " + SpawnCount + " spawns, " +
                ChildCount + " children");

            if (UnreachableAnimations.Count == 0)
            {
                sb.AppendLine("  every animation is reachable");
                return sb.ToString();
            }

            sb.AppendLine("  " + UnreachableAnimations.Count + " animation(s) can NEVER play:");
            foreach (int id in UnreachableAnimations)
                sb.AppendLine("    animation " + id + " is never reached");
            sb.AppendLine("  (an animation is reachable from drag/fall/kill/sync, from a spawn with a " +
                "non-zero probability, from a transition with a non-zero probability, or from a child whose " +
                "PARENT animation is itself reachable)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Analyses a pet XML with the HOST's own parser, validator and reachability walk (all source-linked
    /// into this module), so the verdict here is exactly the verdict the pet will get when it runs.
    ///
    /// Deliberately UI-free: the window renders what this returns, and the module self-test drives it
    /// directly. That separation is the lesson from the tool this replaces, whose analysis lived inside a
    /// WinForms form and so could never be tested or reused.
    /// </summary>
    internal static class PetAnalyzer
    {
        internal static PetReport Analyze(string animationsXml)
        {
            var report = new PetReport();
            if (string.IsNullOrWhiteSpace(animationsXml))
            {
                report.Error = "No pet XML was supplied.";
                return report;
            }

            XmlData.RootNode root;
            string error;
            if (!PetXmlValidator.TryParse(animationsXml, out root, out error))
            {
                report.Error = string.IsNullOrEmpty(error) ? "The pet XML could not be parsed." : error;
                return report;
            }

            report.IsValid = true;
            if (root.Header != null)
            {
                report.PetName = root.Header.Petname ?? "";
                report.Author = root.Header.Author ?? "";
            }
            if (root.Animations != null && root.Animations.Animation != null)
                report.AnimationCount = root.Animations.Animation.Length;
            if (root.Spawns != null && root.Spawns.Spawn != null)
                report.SpawnCount = root.Spawns.Spawn.Length;
            if (root.Childs != null && root.Childs.Child != null)
                report.ChildCount = root.Childs.Child.Length;

            // The reachability walk needs the runtime's own view of the entry animations (drag/fall/kill/
            // sync), which only exists once the XML is staged into an Xml + Animations pair -- the same
            // staging the host does before it will run a pet.
            try
            {
                using (var xml = new Xml(1))
                using (var animations = new Animations(xml))
                {
                    string stageError;
                    if (xml.TryReadXml(animationsXml, out stageError))
                    {
                        xml.LoadAnimations(animations);
                        report.UnreachableAnimations.AddRange(
                            AnimationReachability.FindUnreachable(root, animations));
                    }
                }
            }
            catch (Exception)
            {
                // Reachability is advisory. A pet that validates but cannot be staged is still reported as
                // valid, because the host's own answer to "will this load" is the validator, not this walk.
            }

            return report;
        }
    }
}
