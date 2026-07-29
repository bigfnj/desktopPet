using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Local sentence embedder (bge-small-en-v1.5, ONNX int8) that powers smart/contextual fortunes.
    /// Fully offline, no API, no keys. Purely additive: if the model or the native onnxruntime is
    /// absent it stays "not ready" and <see cref="Embed"/> returns null, so the pet falls back to
    /// random fortunes and behaves exactly as before. CLS-pooled + L2-normalized (bge's recipe).
    /// </summary>
    internal sealed class Embedder : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private readonly object _lock = new object();
        private InferenceSession _session;
        private Dictionary<string, int> _vocab;
        private bool _tried;
        private const string Unk = "[UNK]", Cls = "[CLS]", Sep = "[SEP]";

        private static string Base
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopPet"); }
        }
        /// <summary>Where the downloaded model + vocab live.</summary>
        public static string ModelDir  { get { return Path.Combine(Base, "models", "bge-small"); } }
        /// <summary>Optional folder holding a downloaded native onnxruntime.dll (keeps the base install lean).</summary>
        public static string RuntimeDir { get { return Path.Combine(Base, "runtime"); } }
        public static string ModelPath { get { return Path.Combine(ModelDir, "model.onnx"); } }
        public static string VocabPath { get { return Path.Combine(ModelDir, "vocab.txt"); } }

        /// <summary>Model files present on disk (doesn't force a load).</summary>
        public static bool ModelPresent { get { return File.Exists(ModelPath) && File.Exists(VocabPath); } }

        /// <summary>Vector dimension (bge-small = 384).</summary>
        public int Dim { get; private set; }

        /// <summary>True once the model is loaded and ready to embed.</summary>
        public bool IsReady { get { EnsureLoaded(); return _session != null && _vocab != null; } }

        private void EnsureLoaded()
        {
            if (_tried) return;
            lock (_lock)
            {
                if (_tried) return;
                _tried = true;
                try
                {
                    if (!ModelPresent) return;
                    PointAtNativeRuntime();

                    var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
                    int i = 0;
                    foreach (string line in File.ReadAllLines(VocabPath)) { string t = line.Trim(); if (t.Length > 0 || i == 0) vocab[t] = i; i++; }

                    var session = new InferenceSession(ModelPath);
                    _vocab = vocab; _session = session;
                }
                catch { _session = null; _vocab = null; }
            }
        }

        // Help the P/Invoke locate native onnxruntime.dll: a downloaded runtime dir first (lean
        // install path), else the folder this exe lives in (side-by-side build/install).
        private static void PointAtNativeRuntime()
        {
            try
            {
                if (File.Exists(Path.Combine(RuntimeDir, "onnxruntime.dll"))) { SetDllDirectory(RuntimeDir); return; }
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(asmDir)) SetDllDirectory(asmDir);
            }
            catch { }
        }

        /// <summary>Embed text to a unit-length vector, or null when the embedder isn't ready.</summary>
        public float[] Embed(string text)
        {
            EnsureLoaded();
            if (_session == null || _vocab == null || string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                lock (_lock)
                {
                    long[] ids = Encode(text);
                    int n = ids.Length;
                    var inputIds = new DenseTensor<long>(ids, new[] { 1, n });
                    var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });
                    var types = new DenseTensor<long>(new long[n], new[] { 1, n });

                    var feeds = new List<NamedOnnxValue>();
                    foreach (string key in _session.InputMetadata.Keys)
                    {
                        if (key.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0) feeds.Add(NamedOnnxValue.CreateFromTensor(key, mask));
                        else if (key.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0) feeds.Add(NamedOnnxValue.CreateFromTensor(key, types));
                        else feeds.Add(NamedOnnxValue.CreateFromTensor(key, inputIds));
                    }
                    using (var results = _session.Run(feeds))
                    {
                        var t = results.First().AsTensor<float>();
                        int dims = t.Dimensions.Length;
                        int hidden = t.Dimensions[dims - 1];
                        var v = new float[hidden];
                        if (dims == 3) for (int k = 0; k < hidden; k++) v[k] = t[0, 0, k];   // CLS token
                        else           for (int k = 0; k < hidden; k++) v[k] = t[0, k];
                        Normalize(v);
                        Dim = hidden;
                        return v;
                    }
                }
            }
            catch { return null; }
        }

        // ---- minimal BERT (uncased) WordPiece ----------------------------------
        private long[] Encode(string text)
        {
            var ids = new List<long> { _vocab[Cls] };
            foreach (string word in BasicTokenize(text))
                foreach (string piece in WordPiece(word))
                {
                    int id;
                    ids.Add(_vocab.TryGetValue(piece, out id) ? id : _vocab[Unk]);
                }
            ids.Add(_vocab[Sep]);
            if (ids.Count > 256) ids = ids.Take(255).Concat(new long[] { _vocab[Sep] }).ToList();  // guard
            return ids.ToArray();
        }

        private static IEnumerable<string> BasicTokenize(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } }
                else if (char.IsPunctuation(c) || char.IsSymbol(c)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } yield return c.ToString(); }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private IEnumerable<string> WordPiece(string word)
        {
            if (word.Length > 100) { yield return Unk; yield break; }
            int start = 0;
            var pieces = new List<string>();
            while (start < word.Length)
            {
                int end = word.Length;
                string cur = null;
                while (start < end)
                {
                    string sub = (start > 0 ? "##" : "") + word.Substring(start, end - start);
                    if (_vocab.ContainsKey(sub)) { cur = sub; break; }
                    end--;
                }
                if (cur == null) { yield return Unk; yield break; }
                pieces.Add(cur);
                start = end;
            }
            foreach (string p in pieces) yield return p;
        }

        private static void Normalize(float[] v)
        {
            double s = 0; foreach (float x in v) s += x * x;
            float inv = (float)(1.0 / Math.Sqrt(Math.Max(s, 1e-12)));
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }

        public void Dispose() { try { if (_session != null) _session.Dispose(); } catch { } }

        /// <summary>Diagnostic: embed a few strings and write load status + cosines to a temp file.</summary>
        public static void SelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-embed-selftest.txt");
            var sb = new System.Text.StringBuilder();
            try
            {
                using (var e = new Embedder())
                {
                    sb.AppendLine("ModelPresent=" + ModelPresent);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ready = e.IsReady;
                    sw.Stop();
                    sb.AppendLine("IsReady=" + ready + " loadMs=" + sw.ElapsedMilliseconds);
                    if (ready)
                    {
                        float[] a = e.Embed("I love programming in C sharp");
                        float[] b = e.Embed("Writing code in dot net is really fun");
                        float[] c = e.Embed("The weather outside is freezing cold today");
                        sb.AppendLine("dim=" + (a == null ? 0 : a.Length));
                        sb.AppendLine("cos(code,code)=" + Dot(a, b).ToString("F4") + " (expect HIGH)");
                        sb.AppendLine("cos(code,weather)=" + Dot(a, c).ToString("F4") + " (expect LOW)");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            try { File.WriteAllText(outp, sb.ToString()); } catch { }
        }

        private static float Dot(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0f;
            float d = 0; for (int i = 0; i < a.Length; i++) d += a[i] * b[i]; return d;
        }
    }
}
