using System;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Buckets each Shimeji action (and each behaviour-selection condition) into a <see cref="FidelityGroup"/>.
    ///
    /// The rules are a direct port of the census that produced the 91-action baseline (53 Group1 / 32 Group2
    /// / 6 Group3) against the reference gil/shimeji-ee config. They are ordered most-limiting first, and the
    /// GROUP a rule assigns -- not its reason text -- is what the counts depend on, so the ordering only ever
    /// matters where it moves an action between groups (the Group3 embedded classes are checked before the
    /// Group2 state references).
    /// </summary>
    public static class ActionClassifier
    {
        private static bool Has(string blob, string token)
        {
            return blob != null && blob.IndexOf(token, StringComparison.Ordinal) >= 0;
        }

        private static bool ClassIs(ShimejiAction a, string shortClass)
        {
            return string.Equals(a.Class, shortClass, StringComparison.Ordinal);
        }

        public static void Classify(ShimejiAction a)
        {
            // Group 3 -- structurally impossible / out of scope. Checked first.
            if (ClassIs(a, "ThrowIE") || ClassIs(a, "WalkWithIE") || ClassIs(a, "FallWithIE"))
            {
                Set(a, FidelityGroup.Group3, "carries or throws a window (IE " + a.Class + "); desktopPet cannot and should not move the user's windows");
                return;
            }
            if (ClassIs(a, "Breed"))
            {
                Set(a, FidelityGroup.Group3, "spawns an autonomous bred mascot; desktopPet <child> auto-closes and can't be dragged, so an independent sibling pet is not reproducible");
                return;
            }

            // Group 2 -- needs host state a converted pet cannot express without the format additions.
            if (Has(a.SubtreeBlob, "cursor"))
            {
                Set(a, FidelityGroup.Group2, "branches on cursor position (needs cursorX/cursorY + selfX/selfY, added in Stage 5)");
                return;
            }
            if (Has(a.SubtreeBlob, "activeIE"))
            {
                Set(a, FidelityGroup.Group2, "navigates relative to a specific window's geometry (activeIE.*), which desktopPet does not expose; degrades to the generic 'on a window' situation");
                return;
            }
            if (Has(a.SubtreeBlob, "totalCount"))
            {
                Set(a, FidelityGroup.Group2, "gated on mascot.totalCount (breed-count state, not exposed)");
                return;
            }
            if (Has(a.SubtreeBlob, "mascot.anchor"))
            {
                Set(a, FidelityGroup.Group2, "branches on the pet's own screen position (mascot.anchor.*); needs selfX/selfY (added in Stage 5)");
                return;
            }

            // Group 1 -- preservable with converter-only work.
            if (ClassIs(a, "Fall")) { Set(a, FidelityGroup.Group1, "maps to the magic 'fall' animation name"); return; }
            if (ClassIs(a, "Dragged")) { Set(a, FidelityGroup.Group1, "maps to the magic 'drag' animation name"); return; }
            if (ClassIs(a, "Jump")) { Set(a, FidelityGroup.Group1, "jump arc approximated by start/end velocity"); return; }
            if (ClassIs(a, "Look")) { Set(a, FidelityGroup.Group1, "facing change -> the 'flip' sequence action"); return; }
            if (ClassIs(a, "Offset")) { Set(a, FidelityGroup.Group1, "positional nudge -> baked into the sheet or <offsety>"); return; }
            if (ClassIs(a, "Regist")) { Set(a, FidelityGroup.Group1, "drag-resist animation; plays as ordinary frames"); return; }

            if (string.Equals(a.BorderType, "Ceiling", StringComparison.Ordinal))
            {
                Set(a, FidelityGroup.Group1, "maps to only=horizontal (top border); note: horizontal also fires at the bottom, so top/bottom is ambiguous");
                return;
            }

            Set(a, FidelityGroup.Group1, "deterministic map (frames / velocity / interval / next)");
        }

        public static void ClassifyBehaviorCondition(ShimejiBehaviorCondition c)
        {
            string cond = c.Condition ?? "";
            if (Has(cond, "activeIE")) { SetC(c, FidelityGroup.Group2, "needs window geometry (activeIE)"); return; }
            if (Has(cond, "cursor")) { SetC(c, FidelityGroup.Group2, "needs cursor state"); return; }
            if (Has(cond, "totalCount")) { SetC(c, FidelityGroup.Group2, "needs breed-count state"); return; }
            if (Has(cond, "mascot.anchor.x") || Has(cond, "mascot.anchor.y"))
            { SetC(c, FidelityGroup.Group2, "self-position comparison (mascot.anchor.x/y vs coordinates)"); return; }
            if (Has(cond, "isOn(mascot.anchor)") && !Has(cond, "activeIE"))
            { SetC(c, FidelityGroup.Group1, "border/floor/ceiling situation -> maps to only="); return; }
            if (Has(cond, "Math.random")) { SetC(c, FidelityGroup.Group1, "random gate -> maps to a probability weight"); return; }
            SetC(c, FidelityGroup.Group2, "other stateful condition");
        }

        private static void Set(ShimejiAction a, FidelityGroup g, string reason) { a.Group = g; a.Reason = reason; }
        private static void SetC(ShimejiBehaviorCondition c, FidelityGroup g, string reason) { c.Group = g; c.Reason = reason; }
    }
}
