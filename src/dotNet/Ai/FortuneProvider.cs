using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopPet.Ai
{
    /// <summary>One tagged fortune: its origin collection, content level, profanity flag and text.</summary>
    internal struct FortuneEntry
    {
        public string Source;   // origin collection id (built-in file name, or a user file name)
        public string Category; // coarse topic (tech/wisdom/whimsy/...); "custom" for user files
        public string Level;    // general | edgy | nsfw
        public string Text;
        public bool   Prof;     // contains profanity
        public bool   Custom;   // loaded from the user's fortunes folder
    }

    /// <summary>A source collection as shown in the picker (aggregate over its entries).</summary>
    internal struct SourceStat
    {
        public string Id;
        public string Category;
        public int    Count;
        public bool   Custom;
        public bool   HasSpicy;   // has any edgy/nsfw line
    }

    /// <summary>
    /// The bundled fortunes (cowsay | fortune, but a sheep). Loads a tagged corpus embedded in the
    /// exe — <c>source&lt;TAB&gt;category&lt;TAB&gt;level&lt;TAB&gt;prof&lt;TAB&gt;text</c> — plus any
    /// user-supplied <c>.txt</c> files from <c>%APPDATA%\DesktopPet\fortunes\</c>, and hands out
    /// random lines filtered by the content settings (spicy tier, remove-profanity, per-source
    /// selection). Fully offline, no model, no server. Never throws; degrades to an empty provider.
    /// </summary>
    internal sealed class FortuneProvider
    {
        private readonly List<FortuneEntry> _all  = new List<FortuneEntry>();
        private readonly List<string>       _pool = new List<string>();
        private readonly List<FortuneEntry> _poolE = new List<FortuneEntry>();   // filtered entries (for the smart picker)
        private readonly Random _rng = new Random();
        private int _last = -1;

        public FortuneProvider(AiSettings s)
        {
            LoadEmbedded(_all);
            LoadCustom(_all);
            Rebuild(s ?? new AiSettings());
        }

        public int Count { get { return _pool.Count; } }

        /// <summary>Folder where users drop their own <c>.txt</c> fortune files.</summary>
        public static string CustomDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DesktopPet", "fortunes");
            }
        }

        /// <summary>A random fortune (avoids repeating the immediately previous one). "" if none.</summary>
        public string Pick()
        {
            int n = _pool.Count;
            if (n == 0) return "";
            if (n == 1) return _pool[0];
            int i;
            do { i = _rng.Next(n); } while (i == _last);
            _last = i;
            return _pool[i];
        }

        // ---- pool building --------------------------------------------------

        private void Rebuild(AiSettings s)
        {
            var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool spicyOnly = s.SpicyFortunes && s.SpicyOnly;
            if (!spicyOnly) levels.Add("general");
            if (s.SpicyFortunes)
            {
                levels.Add("nsfw");
                if (!string.Equals(s.SpicyTier, "nsfw", StringComparison.OrdinalIgnoreCase))
                    levels.Add("edgy");
            }

            var disabled = new HashSet<string>(
                s.DisabledSources ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Fallbacks relax only PREFERENCES (disabled sources, spicy-only), NEVER the safety
            // floors: NoProfanity and "spicy off = general only" are hard and must survive every
            // fallback. If the user's constraints can't be met, degrade to clean general content and
            // ultimately to an empty pool (the pet stays silent) rather than leak profanity or
            // adult content the user explicitly excluded.
            Select(levels, s.NoProfanity, disabled);                                          // preferred
            if (_pool.Count == 0) Select(levels, s.NoProfanity, null);                        // relax: disabled sources
            if (_pool.Count == 0 && spicyOnly)                                                // relax: spicy-only -> allow tame
            {
                var withGeneral = new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase) { "general" };
                Select(withGeneral, s.NoProfanity, null);
            }
            if (_pool.Count == 0)                                                             // last resort: clean general only
                Select(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "general" }, s.NoProfanity, null);
            // If still empty, _pool stays empty and Pick() returns "" — we never fall open to
            // profane / above-preference content the user turned off.
        }

        private void Select(HashSet<string> levels, bool noProf, HashSet<string> disabled)
        {
            _pool.Clear();
            _poolE.Clear();
            _last = -1;
            foreach (FortuneEntry e in _all)
            {
                if (!levels.Contains(e.Level)) continue;
                if (noProf && e.Prof) continue;
                if (disabled != null && disabled.Contains(e.Source)) continue;
                _pool.Add(e.Text);
                _poolE.Add(e);
            }
        }

        /// <summary>The active (filtered) pool as entries, for the smart/contextual picker.</summary>
        public List<FortuneEntry> PoolEntries() { return _poolE; }

        /// <summary>
        /// Diagnostic (`--filter-selftest`): prove the content filters never "fail open" — no pooled
        /// entry may violate NoProfanity, and with spicy off nothing above 'general' may appear, even
        /// when the filter combo empties the preferred pool. Writes a report to a temp file and exits.
        /// </summary>
        public static void FilterSelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-filter-selftest.txt");
            var sb = new System.Text.StringBuilder();
            Action<string, AiSettings> check = delegate (string name, AiSettings s)
            {
                try
                {
                    var fp = new FortuneProvider(s);
                    var pool = fp.PoolEntries();
                    int profLeak = 0, spicyLeak = 0;
                    foreach (FortuneEntry e in pool)
                    {
                        if (s.NoProfanity && e.Prof) profLeak++;
                        if (!s.SpicyFortunes && !string.Equals(e.Level, "general", StringComparison.OrdinalIgnoreCase)) spicyLeak++;
                    }
                    sb.AppendLine(name + ": pool=" + pool.Count + " profanity_leaks=" + profLeak +
                        " spicy_leaks=" + spicyLeak + ((profLeak == 0 && spicyLeak == 0) ? "  OK" : "  ***FAIL***"));
                }
                catch (Exception ex) { sb.AppendLine(name + ": EXC " + ex.Message); }
            };

            check("NoProf+SpicyOnly+NSFW (reported blocker)", new AiSettings { SpicyFortunes = true, SpicyTier = "nsfw", SpicyOnly = true, NoProfanity = true });
            check("NoProf only", new AiSettings { NoProfanity = true });
            check("clean default (spicy off)", new AiSettings { SpicyFortunes = false });
            check("NoProf + edgy tier", new AiSettings { SpicyFortunes = true, SpicyTier = "edgy", NoProfanity = true });
            check("NoProf + SpicyOnly + edgy", new AiSettings { SpicyFortunes = true, SpicyTier = "edgy", SpicyOnly = true, NoProfanity = true });

            try { File.WriteAllText(outp, sb.ToString()); } catch { }
        }

        // ---- loading --------------------------------------------------------

        private static List<FortuneEntry> _embeddedCorpus;                       // parsed once; the embedded resource is immutable at runtime
        private static readonly object _embeddedCorpusLock = new object();

        private static void LoadEmbedded(List<FortuneEntry> list)
        {
            list.AddRange(EmbeddedCorpus());                                     // entries are read-only after load, so sharing refs is safe
        }

        /// <summary>
        /// Parse the embedded fortunes.txt once and cache it. Previously re-parsed the ~486KB
        /// resource on every static Sources() call (tab build / add / download).
        /// </summary>
        private static List<FortuneEntry> EmbeddedCorpus()
        {
            if (_embeddedCorpus != null) return _embeddedCorpus;
            lock (_embeddedCorpusLock)
            {
                if (_embeddedCorpus != null) return _embeddedCorpus;
                var parsed = new List<FortuneEntry>();
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    string resName = null;
                    foreach (string n in asm.GetManifestResourceNames())
                        if (n.EndsWith("fortunes.txt", StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                    if (resName != null)
                    {
                        using (Stream st = asm.GetManifestResourceStream(resName))
                        using (StreamReader r = new StreamReader(st, Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                if (line.Length == 0) continue;
                                // source \t category \t level \t prof \t text
                                string[] p = line.Split(new[] { '\t' }, 5);
                                if (p.Length < 5) continue;
                                string text = p[4].Trim();
                                if (text.Length == 0) continue;
                                parsed.Add(new FortuneEntry {
                                    Source = p[0], Category = p[1], Level = p[2], Prof = p[3] == "1",
                                    Text = text, Custom = false });
                            }
                        }
                    }
                }
                catch { }
                _embeddedCorpus = parsed;
                return _embeddedCorpus;
            }
        }

        private static void LoadCustom(List<FortuneEntry> list)
        {
            try
            {
                string dir = CustomDir;
                if (!Directory.Exists(dir)) return;
                foreach (string path in Directory.GetFiles(dir, "*.txt"))
                {
                    if (LoadTaggedPack(path, list)) continue;   // our packs: break out each bundled source
                    // plain user upload: the whole file is one source (the file name)
                    string src = Path.GetFileNameWithoutExtension(path);
                    foreach (string text in ParseFortuneFile(path))
                    {
                        string level; bool prof;
                        FortuneClassifier.Classify(text, src, out level, out prof);
                        list.Add(new FortuneEntry {
                            Source = src, Category = "custom", Level = level, Prof = prof,
                            Text = text, Custom = true });
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Load a downloaded pack in the tagged format (source&lt;TAB&gt;category&lt;TAB&gt;level&lt;TAB&gt;prof&lt;TAB&gt;text)
        /// so every bundled collection (e.g. each TV show) becomes its own toggleable source.
        /// Returns false when the file isn't in that format (a plain user upload).
        /// </summary>
        private static bool LoadTaggedPack(string path, List<FortuneEntry> list)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                string probe = null;
                foreach (string l in lines) if (l.Length > 0) { probe = l; break; }
                if (probe == null) return false;
                string[] pp = probe.Split('\t');
                if (pp.Length < 5 || (pp[3] != "0" && pp[3] != "1")) return false;   // not our tagged format
                foreach (string l in lines)
                {
                    if (l.Length == 0) continue;
                    string[] p = l.Split(new[] { '\t' }, 5);
                    if (p.Length < 5) continue;
                    string text = p[4].Trim();
                    if (text.Length == 0) continue;
                    list.Add(new FortuneEntry {
                        Source = p[0], Category = p[1], Level = p[2], Prof = p[3] == "1",
                        Text = text, Custom = true });
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Parse a user fortune file into bubble-sized lines. Accepts the BSD <c>fortune</c> format
        /// (blocks separated by a line containing only <c>%</c>) or plain one-fortune-per-line.
        /// </summary>
        public static IEnumerable<string> ParseFortuneFile(string path)
        {
            string raw;
            try { raw = File.ReadAllText(path); }
            catch { yield break; }

            IEnumerable<string> chunks;
            if (Regex.IsMatch(raw, @"(?m)^\s*%\s*$"))
                chunks = Regex.Split(raw, @"(?m)^\s*%\s*$");
            else
                chunks = raw.Split('\n');

            foreach (string c in chunks)
            {
                string t = Regex.Replace(c, @"\s+", " ").Trim();
                if (t.Length >= 8 && t.Length <= 280) yield return t;
            }
        }

        // ---- source enumeration for the picker ------------------------------

        /// <summary>All available source collections (built-in + custom) with entry counts.</summary>
        public static List<SourceStat> Sources()
        {
            var all = new List<FortuneEntry>();
            LoadEmbedded(all);
            LoadCustom(all);

            var map = new Dictionary<string, SourceStat>(StringComparer.OrdinalIgnoreCase);
            foreach (FortuneEntry e in all)
            {
                SourceStat st;
                if (!map.TryGetValue(e.Source, out st))
                    st = new SourceStat { Id = e.Source, Category = e.Category, Custom = e.Custom };
                st.Count++;
                if (!string.Equals(e.Level, "general", StringComparison.OrdinalIgnoreCase)) st.HasSpicy = true;
                map[e.Source] = st;
            }

            var result = new List<SourceStat>(map.Values);
            result.Sort((a, b) =>
            {
                if (a.Custom != b.Custom) return a.Custom ? 1 : -1;               // custom last
                int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;                                             // then grouped by theme
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }
    }

    /// <summary>
    /// Deterministic content classifier for user-supplied fortunes (mirrors classify-corpus.py so
    /// custom files get the same level/profanity tagging as the bundled corpus).
    /// </summary>
    internal static class FortuneClassifier
    {
        private static readonly Regex Nsfw = new Regex(
            @"\b(pussy|cocks|dicks|penis|penises|vagina\w*|cums|cumming|jizz|blow ?jobs?|hand ?jobs?|" +
            @"rim ?jobs?|masturbat\w*|porn\w*|rape|raping|rapist|dildo\w*|orgasms?|semen|ejaculat\w*|" +
            @"horny|clit\w*|nipples?|titties|titty|slut\w*|whore\w*|cunt\w*|nsfw|hentai|dominatrix|" +
            @"fetish\w*|genital\w*|scrotum|testicles?|foreskin|blowie|creampie|cumshot|deepthroat|" +
            @"felch\w*|fisting|gangbang|bukkake|blow job|handjob)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Edgy = new Regex(
            @"\b(fuck\w*|shit\w*|bitch\w*|asshole\w*|ass|arse\w*|damn|goddamn\w*|bastard\w*|piss\w*|" +
            @"nigg\w*|fag|faggot\w*|retard\w*|douche\w*|pricks?|wank\w*|bollocks|twat\w*|jackass|" +
            @"dumbass|motherfuck\w*|bullshit|dick|cock|boob\w*|tits)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> EdgySources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "yo-mama", "carlin" };

        public static void Classify(string text, string source, out string level, out bool prof)
        {
            bool nsfw = Nsfw.IsMatch(text);
            bool edgy = Edgy.IsMatch(text);
            prof = nsfw || edgy;
            level = nsfw ? "nsfw"
                  : (edgy || EdgySources.Contains(source)) ? "edgy"
                  : "general";
        }
    }
}
