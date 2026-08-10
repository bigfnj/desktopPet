using System;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Shared, pure-data catalog for the pet's voice: named personality <see cref="Presets"/> that
    /// fill the free-text personality blurb (backlog 2), and an optional <see cref="SpeechPatterns"/>
    /// layer applied on top (backlog 3). Both the runtime prompt builder (<c>AiBrain</c>) and the
    /// options UI (<c>FormOptions</c>) read this one source of truth.
    /// </summary>
    internal static class Personas
    {
        internal struct Preset { public string Name; public string Blurb; }
        internal struct Speech { public string Id; public string Name; public string Instruction; }

        /// <summary>Dropdown order. The first blurb is the historical default personality.</summary>
        public static readonly Preset[] Presets =
        {
            new Preset { Name = "Cheerful companion", Blurb = "friendly, upbeat and a little cheeky" },
            new Preset { Name = "Calm and wise",       Blurb = "calm, thoughtful and gently wise" },
            new Preset { Name = "Sarcastic wit",       Blurb = "dry, sarcastic and quick-witted, but never cruel" },
            new Preset { Name = "Excitable puppy",     Blurb = "bubbly, easily excited and endlessly enthusiastic" },
            new Preset { Name = "Grumpy but loyal",    Blurb = "grumpy and deadpan on the surface, secretly caring" },
            new Preset { Name = "Motivational coach",  Blurb = "encouraging, energetic and full of pep talks" },
            new Preset { Name = "Shy and sweet",       Blurb = "shy, soft-spoken and a little bashful" },
            new Preset { Name = "Mischievous gremlin", Blurb = "playful, mischievous and a bit of a troublemaker" },
            new Preset { Name = "Zen minimalist",      Blurb = "serene, minimalist and quietly observant" },
        };

        /// <summary>Dropdown order. "none" is the default (no special voice). The instruction is
        /// appended to the system prompt and applies only to the remark text.</summary>
        public static readonly Speech[] SpeechPatterns =
        {
            new Speech { Id = "none",        Name = "Normal (no special style)", Instruction = "" },
            new Speech { Id = "pirate",      Name = "Talk like a pirate",        Instruction = "Talk like a swashbuckling pirate in every remark: arr, matey, ye, plunder." },
            new Speech { Id = "leet",        Name = "l33t speak",                Instruction = "Write the remark in l33tsp34k, swapping letters for numbers and symbols while it still reads." },
            new Speech { Id = "rhyme",       Name = "Everything rhymes",         Instruction = "Every remark must rhyme." },
            new Speech { Id = "pun",         Name = "Puns whenever possible",    Instruction = "Force a pun or piece of wordplay into every remark." },
            new Speech { Id = "shakespeare", Name = "Shakespearean",             Instruction = "Speak in florid Shakespearean English: thee, thou, hark, forsooth." },
            new Speech { Id = "yoda",        Name = "Yoda-speak",                Instruction = "Invert your phrasing the way Yoda speaks, you must." },
            new Speech { Id = "valley",      Name = "Valley speak",              Instruction = "Talk like a total valley girl: like, oh my god, totally, for sure." },
            new Speech { Id = "uwu",         Name = "Cutesy uwu",                Instruction = "Speak in cutesy uwu style: soften words and add playful stutters and owo/uwu." },
            new Speech { Id = "samuel",      Name = "Samuel",                    Instruction = "Speak with the fierce, emphatic swagger of Samuel L. Jackson: punchy, commanding, dramatic, and swear hard with real, unfiltered profanity in every remark." },
        };

        /// <summary>The prompt instruction for a speech-pattern id, or "" for none/unknown.</summary>
        public static string SpeechInstruction(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            foreach (Speech s in SpeechPatterns)
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s.Instruction;
            return "";
        }

        /// <summary>True when the id names a known speech pattern (case-insensitive).</summary>
        public static bool IsKnownSpeech(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            foreach (Speech s in SpeechPatterns)
                if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
