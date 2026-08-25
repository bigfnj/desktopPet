using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Reads a Shimeji conf directory (actions.xml + behaviors.xml) into a <see cref="ShimejiConfig"/>.
    ///
    /// Deliberately TOLERANT and namespace-blind. Two traps this handles (see MAPPING.md):
    ///   * The vendor's own Mascot.xsd restricts Type to six values, but the vendor's shipped actions.xml uses
    ///     nine (Sequence/Floor/Stay/Animate/Wall/Ceiling on top of the schema's set). Validating input
    ///     against Mascot.xsd would reject the reference skin, so we drive off observed values and never
    ///     schema-validate the input.
    ///   * Elements are in the http://www.group-finity.com/Mascot namespace; we match on LocalName so a skin
    ///     that omits or renames the namespace still parses.
    /// </summary>
    public static class ShimejiParser
    {
        /// <summary>
        /// Parse the actions.xml and behaviors.xml found under <paramref name="confDir"/>. actions.xml is
        /// required; behaviors.xml is optional (a skin may ship only actions). Throws if actions.xml is
        /// missing or malformed.
        /// </summary>
        public static ShimejiConfig ParseConfDirectory(string confDir)
        {
            if (string.IsNullOrEmpty(confDir)) throw new ArgumentNullException("confDir");
            if (!Directory.Exists(confDir)) throw new DirectoryNotFoundException("No such conf directory: " + confDir);

            string actionsPath = FindFile(confDir, "actions.xml");
            if (actionsPath == null)
                throw new FileNotFoundException("No actions.xml under " + confDir);
            string behaviorsPath = FindFile(confDir, "behaviors.xml");

            var config = new ShimejiConfig();
            ParseActions(XDocument.Load(actionsPath), config);
            if (behaviorsPath != null)
                ParseBehaviorConditions(XDocument.Load(behaviorsPath), config);
            return config;
        }

        private static string FindFile(string dir, string name)
        {
            foreach (string path in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                    return path;
            return null;
        }

        /// <summary>Parse an in-memory actions.xml string. For self-tests and callers that already hold the
        /// document text rather than a directory on disk.</summary>
        internal static ShimejiConfig ParseActionsXml(string actionsXml)
        {
            var config = new ShimejiConfig();
            ParseActions(XDocument.Parse(actionsXml), config);
            return config;
        }

        /// <summary>Parse the bundled Shimeji-EE base behaviour config (embedded in this assembly), used for a
        /// sprites-only skin that ships no conf of its own. See base-conf/NOTICE.txt for licensing.</summary>
        public static ShimejiConfig ParseBundledConf()
        {
            var config = new ShimejiConfig();
            ParseActions(LoadEmbeddedXml("base-actions.xml", true), config);
            XDocument behaviors = LoadEmbeddedXml("base-behaviors.xml", false);
            if (behaviors != null) ParseBehaviorConditions(behaviors, config);
            return config;
        }

        private static XDocument LoadEmbeddedXml(string logicalName, bool required)
        {
            Assembly asm = typeof(ShimejiParser).Assembly;
            using (Stream s = asm.GetManifestResourceStream(logicalName))
            {
                if (s == null)
                {
                    if (required) throw new InvalidOperationException("Bundled Shimeji conf resource missing: " + logicalName);
                    return null;
                }
                return XDocument.Load(s);
            }
        }

        private static string Local(XElement e) { return e.Name.LocalName; }

        private static string Attr(XElement e, string name)
        {
            foreach (XAttribute a in e.Attributes())
                if (string.Equals(a.Name.LocalName, name, StringComparison.Ordinal))
                    return a.Value;
            return null;
        }

        private static void ParseActions(XDocument doc, ShimejiConfig config)
        {
            // Top-level actions are the DIRECT children of each <ActionList> named Action. Nested <Action>
            // elements (inside a Sequence/Select/Composite) are lower actions and are NOT top-level -- they
            // are folded into their parent's subtree blob instead, exactly as the census does.
            foreach (XElement list in doc.Descendants().Where(e => Local(e) == "ActionList"))
            {
                foreach (XElement el in list.Elements().Where(e => Local(e) == "Action"))
                {
                    var action = new ShimejiAction
                    {
                        Name = Attr(el, "Name"),
                        Type = Attr(el, "Type"),
                        Class = ShortClass(Attr(el, "Class")),
                        BorderType = Attr(el, "BorderType"),
                        SubtreeBlob = SubtreeBlob(el),
                    };
                    // Direct <Animation> children only -- a composite action's nested <Action>s are folded
                    // into the subtree blob for classification, not into this action's animation list.
                    foreach (XElement anim in el.Elements().Where(e => Local(e) == "Animation"))
                    {
                        var animation = new ShimejiAnimation { Condition = Attr(anim, "Condition") };
                        foreach (XElement pose in anim.Elements().Where(e => Local(e) == "Pose"))
                            animation.Poses.Add(ParsePose(pose));
                        action.Animations.Add(animation);
                    }
                    ActionClassifier.Classify(action);
                    config.Actions.Add(action);
                }
            }

            // The complete sprite set, gathered from EVERY <Pose> in the document regardless of how deeply it
            // is nested, so the compositor cannot miss a frame a skin tucked inside a composite action.
            foreach (XElement pose in doc.Descendants().Where(e => Local(e) == "Pose"))
                config.Poses.Add(ParsePose(pose));
        }

        private const int ScriptDurationFallback = 8;  // a gentle hold, not the 1-tick flash a raw parse gives

        private static bool IsScript(string s)
        {
            return s != null && (s.IndexOf("${", StringComparison.Ordinal) >= 0 ||
                                 s.IndexOf("#{", StringComparison.Ordinal) >= 0);
        }

        private static ShimejiPose ParsePose(XElement p)
        {
            string durAttr = Attr(p, "Duration");
            string velAttr = Attr(p, "Velocity");
            string anchorAttr = Attr(p, "ImageAnchor");

            var pose = new ShimejiPose
            {
                Image = Attr(p, "Image"),
                Sound = Attr(p, "Sound"),
                // A ${...}/#{...} duration cannot be evaluated offline; flatten to a gentle hold rather than the
                // 1-tick flash a raw int-parse fallback would produce (a cause of "animations play too fast").
                Duration = ParseInt(durAttr, IsScript(durAttr) ? ScriptDurationFallback : 1),
                ScriptFlattened = IsScript(durAttr) || IsScript(velAttr) || IsScript(anchorAttr),
            };
            ParsePair(anchorAttr, out pose.AnchorX, out pose.AnchorY);
            ParsePair(velAttr, out pose.VelX, out pose.VelY);
            return pose;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static void ParsePair(string s, out int x, out int y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(s)) return;
            string[] parts = s.Split(',');
            if (parts.Length >= 1) x = ParseInt(parts[0].Trim(), 0);
            if (parts.Length >= 2) y = ParseInt(parts[1].Trim(), 0);
        }

        private static void ParseBehaviorConditions(XDocument doc, ShimejiConfig config)
        {
            // Match the census: collect the Condition attribute wherever it appears on a Condition wrapper, a
            // Behavior, or a BehaviorReference, de-duplicated by (owner, condition).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement el in doc.Descendants())
            {
                string local = Local(el);
                // Accept British "Behaviour" spellings and legacy reference tags: real skins use both.
                if (local != "Condition" && local != "Behavior" && local != "Behaviour" &&
                    local != "BehaviorReference" && local != "BehaviourReference") continue;
                string cond = Attr(el, "Condition");
                if (cond == null) continue;
                string owner = Attr(el, "Name") ?? "<wrapper>";
                string key = owner + " " + cond;
                if (!seen.Add(key)) continue;

                var bc = new ShimejiBehaviorCondition { Owner = owner, Condition = cond };
                ActionClassifier.ClassifyBehaviorCondition(bc);
                config.BehaviorConditions.Add(bc);
            }
        }

        internal static string ShortClass(string fullClass)
        {
            if (string.IsNullOrEmpty(fullClass)) return null;
            int dot = fullClass.LastIndexOf('.');
            return dot >= 0 ? fullClass.Substring(dot + 1) : fullClass;
        }

        private static string SubtreeBlob(XElement el)
        {
            var values = new List<string>();
            foreach (XElement e in el.DescendantsAndSelf())
                foreach (XAttribute a in e.Attributes())
                    values.Add(a.Value);
            return string.Join(" || ", values);
        }
    }
}
