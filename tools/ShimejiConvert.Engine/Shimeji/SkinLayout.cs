using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopPet.Tools.ShimejiConvert.Shimeji
{
    /// <summary>One convertible skin found on disk: where its conf lives, where its sprites live, its name.</summary>
    public sealed class DetectedSkin
    {
        public string Name;
        public string ConfDir;           // holds actions.xml (+ optionally behaviors.xml); null when bundled
        public string ImgDir;            // holds the shimeN.png sprites
        public bool UsesBundledConf;     // true when the skin ships no conf and the bundled base config is used
    }

    /// <summary>
    /// Works out the Shimeji conf + sprite folders inside whatever a user points us at. Shimeji-EE lays a
    /// skin out as a shared conf/ plus one or more img/&lt;Skin&gt;/ sprite folders, but downloads vary, so
    /// this is heuristic and tolerant:
    ///   * conf = the nearest folder (root, root/conf, or a shallow search) containing actions.xml;
    ///   * each img folder = a folder containing shimeN-style PNGs (root/img/*, or the root itself).
    /// A skin with NO actions.xml cannot be converted here -- a sprites-only skin relies on the base
    /// Shimeji conf, which is copyrighted and this repo does not ship. That case returns no skins with a
    /// clear reason, rather than guessing.
    /// </summary>
    public static class SkinLayout
    {
        public static List<DetectedSkin> Detect(string rootDir, out string note)
        {
            note = null;
            var skins = new List<DetectedSkin>();
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            {
                note = "no such folder: " + rootDir;
                return skins;
            }

            string confDir = FindConfDir(rootDir);
            bool bundled = confDir == null;

            foreach (string imgDir in FindImgDirs(rootDir))
                skins.Add(new DetectedSkin
                {
                    Name = SkinName(rootDir, imgDir),
                    ConfDir = confDir,
                    ImgDir = imgDir,
                    UsesBundledConf = bundled,
                });

            if (skins.Count == 0)
                note = bundled
                    ? "found no sprites (looked for *.png) and no actions.xml. Point at a Shimeji skin folder."
                    : "found a conf but no sprite folder (looked for *.png). Point at the skin's img folder.";
            else if (bundled)
                note = "this skin has no behaviour config of its own; the bundled Shimeji base behaviour will be used.";
            return skins;
        }

        private static string FindConfDir(string root)
        {
            if (File.Exists(Path.Combine(root, "actions.xml"))) return root;
            string conf = Path.Combine(root, "conf");
            if (File.Exists(Path.Combine(conf, "actions.xml"))) return conf;
            // shallow search (root's descendants, capped depth) for any actions.xml
            foreach (string dir in EnumerateDirs(root, 3))
                if (File.Exists(Path.Combine(dir, "actions.xml"))) return dir;
            return null;
        }

        private static IEnumerable<string> FindImgDirs(string root)
        {
            var found = new List<string>();
            string img = Path.Combine(root, "img");
            if (Directory.Exists(img))
            {
                foreach (string sub in Directory.GetDirectories(img))
                    if (HasSprites(sub)) found.Add(sub);
                if (found.Count > 0) return found;
                if (HasSprites(img)) { found.Add(img); return found; }
            }
            if (HasSprites(root)) { found.Add(root); return found; }
            // last resort: any descendant folder with sprites
            foreach (string dir in EnumerateDirs(root, 3))
                if (HasSprites(dir)) found.Add(dir);
            return found;
        }

        private static bool HasSprites(string dir)
        {
            try { return Directory.GetFiles(dir, "*.png").Length > 0; }
            catch { return false; }
        }

        private static string SkinName(string root, string imgDir)
        {
            string leaf = new DirectoryInfo(imgDir).Name;
            if (!string.Equals(leaf, "img", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(imgDir, root, StringComparison.OrdinalIgnoreCase))
                return leaf;
            return new DirectoryInfo(root).Name;
        }

        private static IEnumerable<string> EnumerateDirs(string root, int maxDepth)
        {
            var queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            while (queue.Count > 0)
            {
                Tuple<string, int> cur = queue.Dequeue();
                string[] subs;
                try { subs = Directory.GetDirectories(cur.Item1); }
                catch { continue; }
                foreach (string sub in subs)
                {
                    yield return sub;
                    if (cur.Item2 + 1 < maxDepth) queue.Enqueue(Tuple.Create(sub, cur.Item2 + 1));
                }
            }
        }
    }
}
