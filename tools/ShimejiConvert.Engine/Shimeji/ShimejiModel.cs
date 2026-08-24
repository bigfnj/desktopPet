using System.Collections.Generic;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// How faithfully a Shimeji action survives conversion to the desktopPet animations.xml format.
    /// The taxonomy is fixed and documented in tools/ShimejiConvert/MAPPING.md:
    ///   Group1 = preservable with converter-only work (deterministic map, sheet baking, magic names);
    ///   Group2 = preservable only with new host state the format exposes (cursorX/cursorY/selfX/selfY);
    ///   Group3 = structurally out of scope (IE window manipulation, autonomous breeding) -- residue.
    /// </summary>
    public enum FidelityGroup { Group1 = 1, Group2 = 2, Group3 = 3 }

    /// <summary>
    /// One top-level Shimeji &lt;Action&gt; (a direct child of an &lt;ActionList&gt;), captured with just
    /// enough shape to classify it. Later stages (compositor, emitter) extend this with poses and the
    /// reference tree; Stage 1 only needs the fields the classifier reads plus the raw subtree text.
    /// </summary>
    public sealed class ShimejiAction
    {
        /// <summary>The Name attribute (top-level actions always have one).</summary>
        public string Name;

        /// <summary>The observed Type attribute. Driven off observed values, NOT the vendor XSD: the shipped
        /// actions.xml uses Sequence/Floor/Stay/Animate/Wall/Ceiling, none of which Mascot.xsd permits.</summary>
        public string Type;

        /// <summary>The short embedded-class name (after the last '.'), or null. e.g. Class=
        /// "com.group_finity.mascot.action.ThrowIE" -&gt; "ThrowIE". Only Type="Embedded" carries one.</summary>
        public string Class;

        /// <summary>Floor / Wall / Ceiling, or null.</summary>
        public string BorderType;

        /// <summary>Every attribute value in this action's subtree (itself and all descendants), concatenated.
        /// The classifier scans this for the state references (activeIE, cursor, mascot.anchor, totalCount)
        /// that decide whether an action needs host state a converted pet cannot express today.</summary>
        public string SubtreeBlob;

        /// <summary>Result of classification (see <see cref="ActionClassifier"/>).</summary>
        public FidelityGroup Group;

        /// <summary>Human-readable reason the action landed in its group -- the residue report's text.</summary>
        public string Reason;
    }

    /// <summary>
    /// One &lt;Condition&gt; gate in behaviors.xml (a Condition wrapper, or the Condition attribute on a
    /// Behavior / BehaviorReference). These decide whether Shimeji's behaviour selection can be reproduced by
    /// the desktopPet only= situation enum + probability weights, or whether it needs state the format cannot
    /// see. Reported alongside the action census.
    /// </summary>
    public sealed class ShimejiBehaviorCondition
    {
        public string Owner;      // the Name of the behavior/reference, or "<wrapper>" for a bare Condition
        public string Condition;  // the raw expression text
        public FidelityGroup Group;
        public string Reason;
    }

    /// <summary>A parsed Shimeji configuration: its top-level actions and its behaviour-selection conditions.</summary>
    public sealed class ShimejiConfig
    {
        public readonly List<ShimejiAction> Actions = new List<ShimejiAction>();
        public readonly List<ShimejiBehaviorCondition> BehaviorConditions = new List<ShimejiBehaviorCondition>();
    }
}
