using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace DesktopPet.Ai
{
    /// <summary>
    /// The bundled fortunes (cowsay | fortune, but a sheep). Loads a %-delimited corpus embedded
    /// in the exe — SFW by default, Spicy opt-in — and hands out random lines. Fully offline, no
    /// model, no server. Never throws; degrades to an empty provider if the resource is missing.
    /// </summary>
    internal sealed class FortuneProvider
    {
        private readonly List<string> _fortunes = new List<string>();
        private readonly Random _rng = new Random();
        private int _last = -1;

        public FortuneProvider(bool spicy)
        {
            Load(spicy ? "fortunes-spicy.txt" : "fortunes-sfw.txt");
            if (_fortunes.Count == 0) Load("fortunes-sfw.txt");   // fallback if the requested set is absent
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

        private void Load(string fileSuffix)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                if (resName == null) return;

                using (Stream s = asm.GetManifestResourceStream(resName))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    StringBuilder cur = new StringBuilder();
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line == "%")
                        {
                            string e = cur.ToString().Trim();
                            if (e.Length > 0) _fortunes.Add(e);
                            cur.Length = 0;
                        }
                        else
                        {
                            if (cur.Length > 0) cur.Append(' ');
                            cur.Append(line);
                        }
                    }
                    string tail = cur.ToString().Trim();
                    if (tail.Length > 0) _fortunes.Add(tail);
                }
            }
            catch { }
        }
    }
}
