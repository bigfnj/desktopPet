using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace DesktopPet.Ai
{
    /// <summary>
    /// The bundled fortunes (cowsay | fortune, but a sheep). Loads a category-tagged corpus embedded
    /// in the exe — SFW by default, Spicy opt-in — and hands out random lines. Fully offline, no
    /// model, no server. Never throws; degrades to an empty provider if the resource is missing.
    /// </summary>
    internal sealed class FortuneProvider
    {
        private readonly List<string> _fortunes = new List<string>();
        private readonly Random _rng = new Random();
        private int _last = -1;

        public FortuneProvider(bool spicy, bool spicyOnly)
        {
            string file = spicy ? "fortunes-spicy.txt" : "fortunes-sfw.txt";
            bool onlySpicy = spicy && spicyOnly;

            Load(file, onlySpicy);
            if (_fortunes.Count == 0 && onlySpicy) Load(file, false);          // spicy-only empty -> full spicy
            if (_fortunes.Count == 0) Load("fortunes-sfw.txt", false);         // ultimate fallback
        }

        public int Count { get { return _fortunes.Count; } }

        /// <summary>A random fortune (avoids repeating the immediately previous one). "" if none loaded.</summary>
        public string Pick()
        {
            int n = _fortunes.Count;
            if (n == 0) return "";
            if (n == 1) return _fortunes[0];
            int i;
            do { i = _rng.Next(n); } while (i == _last);
            _last = i;
            return _fortunes[i];
        }

        private void Load(string fileSuffix, bool onlySpicy)
        {
            try
            {
                _fortunes.Clear();
                _last = -1;
                Assembly asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                if (resName == null) return;

                using (Stream s = asm.GetManifestResourceStream(resName))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        // Format: "category<TAB>rating<TAB>text". Category + rating drive Phase B
                        // routing; rating also powers the "spicy only" filter. Display text is the
                        // last field.
                        string[] parts = line.Split(new[] { '\t' }, 3);
                        string rating = parts.Length >= 3 ? parts[1] : "sfw";
                        string text = parts[parts.Length - 1].Trim();
                        if (onlySpicy && !string.Equals(rating, "spicy", StringComparison.OrdinalIgnoreCase)) continue;
                        if (text.Length > 0) _fortunes.Add(text);
                    }
                }
            }
            catch { }
        }
    }
}
