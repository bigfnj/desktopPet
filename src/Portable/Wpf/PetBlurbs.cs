using System;
using System.Collections.Generic;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// One short, unique, tongue-in-cheek blurb per bundled pet, keyed by catalog id (S5b-2c3). The seven
    /// colored sheep share one 268-move animation set, so each gets its own colour-based quip to keep the
    /// descriptions distinct. Purely cosmetic copy for the Pets gallery; an unknown id falls back to a
    /// generic line.
    /// </summary>
    internal static class PetBlurbs
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "eSheep",        "The original parkour sheep by Adriano. Accept no substitutes." },
            { "blue_sheep",    "Ben — blue as a Monday and twice as athletic; parkours to avoid his feelings." },
            { "green_sheep",   "Gus — green and serene; never once jealous, since he's already the best climber." },
            { "orange_sheep",  "Omar — orange you glad he's here? 268 moves, every one a showoff." },
            { "pink_sheep",    "Pearl — pretty in pink and extra bouncy; quietly judging your desktop clutter." },
            { "purple_sheep",  "Patsu — purple and faintly regal; convinced he is desktop royalty." },
            { "red_sheep",     "Rick — red-hot and always rolling; never gonna give your cursor up." },
            { "yellow_sheep",  "Yogurt — mellow yellow with a rocket habit; may spontaneously blast off." },
            { "esheep64",      "The 64-move remix — fewer tricks, same swagger." },
            { "bbunny",        "A bunny. It hops, it judges, it hops again." },
            { "blue_ham_ham",  "A tiny blue hamster with enormous cheek energy." },
            { "fox",           "Sly, quick, and about 27 flavors of mischief." },
            { "mareep",        "An electric sheep that dreams of androids." },
            { "mimiko",        "A small black cat with a large list of opinions." },
            { "negima",        "Pure anime energy, rendered in pixels." },
            { "neko",          "The internet's favorite cat, now loitering on your taskbar." },
            { "pikachu",       "You knew this one was coming. Pika." },
            { "pingus",        "A penguin with the most moves outside the sheep club (28!); waddles with purpose." },
            { "pink_fox",      "Fox, but make it pink — same cunning, more flair." },
            { "pink_neko",     "Neko in its full Barbie era." },
            { "shiny_sylveon", "Sparkly, elegant, and well aware of it — the shiny-variant flex." },
            { "ssj-goku",      "Powering up on your desktop. It's over 9000 pixels." },
            { "yellow_neko",   "A sunny cat that pairs with any wallpaper." },
        };

        public static string For(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                string blurb;
                if (Map.TryGetValue(id, out blurb)) return blurb;
            }
            return "A delightful desktop companion.";
        }
    }
}
