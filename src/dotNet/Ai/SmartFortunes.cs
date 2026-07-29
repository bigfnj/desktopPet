using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Smart/contextual fortune picker. Embeds the active fortune pool with the local bge-small
    /// <see cref="Embedder"/> (cached to disk so it's a one-time cost), then for a given on-screen
    /// context picks a fortune by centered cosine similarity, nudged by app→category routing, with a
    /// confidence-adaptive fall-through to random when nothing fits. Fully offline; additive — returns
    /// null whenever it can't help so the caller just speaks a random fortune.
    /// </summary>
    internal sealed class SmartFortunes : IDisposable
    {
        private readonly Embedder _embed = new Embedder();
        private readonly VectorCache _cache = new VectorCache();
        private readonly Random _rng = new Random();

        private List<FortuneEntry> _pool;
        private float[][] _vecs;      // centered + L2-normalized, parallel to _pool
        private float[] _mean;
        private volatile bool _ready;
        private int _warmToken;

        private const int TopK = 8;
        private const float RouteBonus = 0.06f;
        private const float MinConfidence = 0.10f;   // below this the best match is too weak -> random
        // bge-small-en-v1.5 is asymmetric: the query gets this instruction, passages stay plain.
        private const string QueryPrefix = "Represent this sentence for searching relevant passages: ";

        /// <summary>The bundled model is present and loadable.</summary>
        public bool Available { get { return Embedder.ModelPresent; } }
        /// <summary>Pool vectors are computed and the picker can serve contextual picks.</summary>
        public bool Ready { get { return _ready; } }

        /// <summary>Number of fortunes in the warmed pool (0 until ready).</summary>
        public int PoolCount { get { var p = _pool; return p == null ? 0 : p.Count; } }

        /// <summary>Embed the given pool in the background (idempotent; supersedes any prior warm).</summary>
        public void Warm(List<FortuneEntry> pool)
        {
            if (pool == null || !Available) { _ready = false; return; }
            int token = ++_warmToken;
            _ready = false;
            Task.Run(() => WarmCore(pool, token));
        }

        private void WarmCore(List<FortuneEntry> pool, int token)
        {
            try
            {
                if (!_embed.IsReady) return;
                int n = pool.Count;
                var raw = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    if (token != _warmToken) return;          // superseded by a newer warm
                    raw[i] = _cache.GetOrEmbed(pool[i].Text, _embed);
                    if ((i & 2047) == 2047) _cache.Save();     // checkpoint so a big warm survives a close
                }
                int dim = _embed.Dim > 0 ? _embed.Dim : 384;

                var mean = new float[dim];
                int cnt = 0;
                for (int i = 0; i < n; i++) { var v = raw[i]; if (v == null) continue; for (int k = 0; k < dim; k++) mean[k] += v[k]; cnt++; }
                if (cnt > 0) for (int k = 0; k < dim; k++) mean[k] /= cnt;

                var vecs = new float[n][];
                for (int i = 0; i < n; i++) vecs[i] = CenterNormalize(raw[i], mean);

                if (token != _warmToken) return;
                _pool = pool; _vecs = vecs; _mean = mean; _ready = true;
                _cache.Save();
            }
            catch { }
        }

        /// <summary>
        /// A fortune that fits <paramref name="context"/> (the screen/window text), or null to signal
        /// "no good match — use a random fortune". <paramref name="app"/> is the foreground process name.
        /// </summary>
        public string Pick(string context, string app)
        {
            if (!_ready) return null;
            List<FortuneEntry> pool = _pool; float[][] vecs = _vecs; float[] mean = _mean;
            if (pool == null || vecs == null || string.IsNullOrWhiteSpace(context)) return null;
            try
            {
                float[] q = _embed.Embed(QueryPrefix + context);
                if (q == null) return null;
                float[] qc = CenterNormalize(q, mean);
                if (qc == null) return null;

                HashSet<string> routed = Router.Categories(app);

                // keep the top-K scoring fortunes
                var idx = new int[TopK]; var sc = new float[TopK];
                for (int t = 0; t < TopK; t++) { idx[t] = -1; sc[t] = float.NegativeInfinity; }
                int n = Math.Min(pool.Count, vecs.Length);
                for (int i = 0; i < n; i++)
                {
                    float[] v = vecs[i]; if (v == null) continue;
                    float s = Dot(qc, v);
                    if (routed != null && routed.Contains(pool[i].Category)) s += RouteBonus;
                    // insert into the small top-K if it beats the current minimum
                    int min = 0; for (int t = 1; t < TopK; t++) if (sc[t] < sc[min]) min = t;
                    if (s > sc[min]) { sc[min] = s; idx[min] = i; }
                }

                float best = float.NegativeInfinity;
                for (int t = 0; t < TopK; t++) if (sc[t] > best) best = sc[t];
                if (best < MinConfidence) return null;               // weak fit -> let the caller go random

                // random among the (valid) top-K for variety
                var pick = new List<int>();
                for (int t = 0; t < TopK; t++) if (idx[t] >= 0) pick.Add(idx[t]);
                if (pick.Count == 0) return null;
                return pool[pick[_rng.Next(pick.Count)]].Text;
            }
            catch { return null; }
        }

        private static float[] CenterNormalize(float[] v, float[] mean)
        {
            if (v == null || mean == null || v.Length != mean.Length) return v;
            int d = v.Length;
            var o = new float[d];
            double s = 0;
            for (int k = 0; k < d; k++) { float c = v[k] - mean[k]; o[k] = c; s += (double)c * c; }
            float inv = (float)(1.0 / Math.Sqrt(Math.Max(s, 1e-12)));
            for (int k = 0; k < d; k++) o[k] *= inv;
            return o;
        }
        private static float Dot(float[] a, float[] b)
        {
            int d = Math.Min(a.Length, b.Length); float s = 0;
            for (int k = 0; k < d; k++) s += a[k] * b[k];
            return s;
        }

        public void Dispose() { try { _embed.Dispose(); } catch { } }

        // ---- diagnostic ---------------------------------------------------------
        public static void SelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-smart-selftest.txt");
            var sb = new System.Text.StringBuilder();
            try
            {
                var fp = new FortuneProvider(new AiSettings());
                var pool = fp.PoolEntries();
                sb.AppendLine("pool=" + pool.Count + " modelPresent=" + Embedder.ModelPresent);
                using (var sm = new SmartFortunes())
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    sm.Warm(pool);
                    while (!sm.Ready && sw.ElapsedMilliseconds < 120000) System.Threading.Thread.Sleep(200);
                    sw.Stop();
                    sb.AppendLine("warmed=" + sm.Ready + " in " + sw.ElapsedMilliseconds + "ms");
                    foreach (var ctx in new[] {
                        "Program.cs - Visual Studio - writing C# code",
                        "Reddit - r/relationships - my girlfriend and I broke up",
                        "Spreadsheet - quarterly budget report in Excel" })
                    {
                        string f = sm.Pick(ctx, "");
                        sb.AppendLine("[" + ctx.Substring(0, Math.Min(40, ctx.Length)) + "...] -> " + (f ?? "(random)"));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            try { File.WriteAllText(outp, sb.ToString()); } catch { }
        }
    }

    /// <summary>Foreground-app → preferred fortune categories. A soft nudge, never a hard filter.</summary>
    internal static class Router
    {
        public static HashSet<string> Categories(string app)
        {
            if (string.IsNullOrEmpty(app)) return null;
            string a = app.ToLowerInvariant();
            if (Has(a, "code", "devenv", "studio", "vim", "nvim", "sublime", "idea", "pycharm", "rider", "cursor", "term", "cmd", "powershell", "conemu", "wsl", "git"))
                return Set("tech", "wisdom");
            if (Has(a, "chrome", "firefox", "msedge", "edge", "opera", "brave", "vivaldi"))
                return Set("observations", "whimsy", "facts");
            if (Has(a, "winword", "excel", "powerpnt", "outlook", "onenote", "teams", "slack", "notion", "obsidian"))
                return Set("work", "wisdom");
            if (Has(a, "spotify", "vlc", "music", "itunes", "foobar"))
                return Set("creative", "whimsy");
            if (Has(a, "steam", "game", "epicgames", "battle.net"))
                return Set("whimsy");
            return null;
        }
        private static bool Has(string a, params string[] keys) { foreach (var k in keys) if (a.IndexOf(k, StringComparison.Ordinal) >= 0) return true; return false; }
        private static HashSet<string> Set(params string[] c) { return new HashSet<string>(c, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Persistent text→embedding cache so the pool is embedded only once ever (survives restarts and
    /// pack changes). Stored as a flat binary in %LOCALAPPDATA%\DesktopPet\vectors. Never throws.
    /// </summary>
    internal sealed class VectorCache
    {
        private const int Magic = 0x42474531; // "BGE1"
        private readonly Dictionary<string, float[]> _map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private bool _dirty;

        private static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet", "vectors"); } }
        private static string File_ { get { return Path.Combine(Dir, "cache.bin"); } }

        public VectorCache() { Load(); }

        public float[] GetOrEmbed(string text, Embedder e)
        {
            lock (_lock) { float[] v; if (_map.TryGetValue(text, out v)) return v; }
            float[] emb = e.Embed(text);
            if (emb != null) lock (_lock) { _map[text] = emb; _dirty = true; }
            return emb;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(File_)) return;
                using (var br = new BinaryReader(File.OpenRead(File_)))
                {
                    if (br.ReadInt32() != Magic) return;
                    int count = br.ReadInt32();
                    int dim = br.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        string key = br.ReadString();
                        var v = new float[dim];
                        for (int k = 0; k < dim; k++) v[k] = br.ReadSingle();
                        _map[key] = v;
                    }
                }
            }
            catch { _map.Clear(); }
        }

        public void Save()
        {
            try
            {
                lock (_lock)
                {
                    if (!_dirty || _map.Count == 0) return;
                    Directory.CreateDirectory(Dir);
                    int dim = 0; foreach (var v in _map.Values) { dim = v.Length; break; }
                    string tmp = File_ + ".tmp";
                    using (var bw = new BinaryWriter(File.Create(tmp)))
                    {
                        bw.Write(Magic); bw.Write(_map.Count); bw.Write(dim);
                        foreach (var kv in _map)
                        {
                            bw.Write(kv.Key);
                            var v = kv.Value;
                            for (int k = 0; k < dim; k++) bw.Write(k < v.Length ? v[k] : 0f);
                        }
                    }
                    if (File.Exists(File_)) File.Delete(File_);
                    File.Move(tmp, File_);
                    _dirty = false;
                }
            }
            catch { }
        }
    }
}
