using System;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Canned one-liners for the poke-escalation "sass" tier (pokes 5-11). Deliberately a plain
    /// list so more can be slotted in later (or swapped for a bundled text resource) without
    /// touching the escalation logic. Never throws.
    /// </summary>
    internal static class PokeReactions
    {
        private static readonly Random _rng = new Random();
        private static int _last = -1;

        // --- Add / edit freely: one string per reaction. -------------------------
        private static readonly string[] Sass =
        {
            "Stop tickling me!",
            "Are you finished?",
            "I'm not a slot machine.",
            "Do you mind?",
            "Okay, okay, I'm awake!",
            "Personal space, please!",
            "That tickles!",
            "Hey! Quit poking.",
            "You again?",
            "I have feelings, you know.",
            "Enough already!",
            "Is this fun for you?",
        };
        // -------------------------------------------------------------------------

        /// <summary>A random sass line (avoids repeating the previous one). "" if the list is empty.</summary>
        public static string RandomSass()
        {
            int n = Sass.Length;
            if (n == 0) return "";
            if (n == 1) return Sass[0];
            int i;
            do { i = _rng.Next(n); } while (i == _last);
            _last = i;
            return Sass[i];
        }
    }
}
