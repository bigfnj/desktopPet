using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopPet.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet.Options
{
    /// <summary>
    /// The WebView2 "control-center" rendering of the Fortunes settings (the Option-3 single pane of
    /// glass). It hosts a <see cref="WebViewHost"/>, binds a <see cref="FortunesController"/> to the
    /// SAME shared <c>AiSettings</c> and pet runtime the rest of the Options dialog uses (so there is
    /// no divergent settings instance), and bridges JSON both ways: page commands drive the controller;
    /// controller state/events are pushed back to the page. Online pack downloads and "add your own"
    /// stay in the proven native WinForms controls beside this view; this pane owns the installed-source
    /// table, smart toggle + rebuild, content level, genres, and Apply.
    /// </summary>
    internal sealed class FortunesWebView : UserControl
    {
        private readonly WebViewHost _host = new WebViewHost();
        private readonly FortunesController _ctl;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private bool _pageReady;

        private sealed class NoopCatalog : ICatalogService
        {
            public void FetchAsync(Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
            public void DownloadPacksAsync(System.Collections.Generic.IEnumerable<string> ids, Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
            public void DownloadPetAsync(string id, Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
        }

        public FortunesWebView(AiSettings ai, IPetRuntime runtime)
        {
            _ctl = new FortunesController(ai, runtime, new NoopCatalog());
            _ctl.Load();
            _ctl.SmartStatusChanged += OnSmartStatusChanged;

            _host.Dock = DockStyle.Fill;
            _host.MessageReceived += OnPageMessage;
            Controls.Add(_host);

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1500 };   // live-update the smart index status while open
            _statusTimer.Tick += delegate { try { _ctl.PollSmartStatus(); } catch { } };
        }

        protected override async void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                await _host.InitAsync();
                _host.LoadHtml(LoadHtmlResource());   // page posts {cmd:"ready"} when its script runs
            }
            catch { /* runtime detection guards the caller; nothing to do if init fails */ }
        }

        /// <summary>Re-read the library (e.g. after a native pack download or file import) and repaint.</summary>
        public void Reload()
        {
            try { _ctl.Load(); if (_pageReady) PushState(); } catch { }
        }

        // ---- test hooks (used by the --fortunes-webview-selftest smoke) ----
        internal bool PageReady { get { return _pageReady; } }
        internal FortunesState ControllerState { get { return _ctl.State; } }
        internal Task<string> EvalAsync(string js) { return _host.ExecuteScriptAsync(js); }

        // ---------------- page -> controller ----------------
        private void OnPageMessage(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            if (InvokeRequired) { try { BeginInvoke((Action)(() => OnPageMessage(json))); } catch { } return; }
            JObject o;
            try { o = JObject.Parse(json); } catch { return; }
            string cmd = (string)o["cmd"];
            try
            {
                switch (cmd)
                {
                    case "ready": _pageReady = true; _statusTimer.Start(); PushState(); break;
                    case "setSmart": _ctl.SetSmartEnabled(B(o, "on")); PushState(); break;
                    case "setSource": _ctl.SetSourceActive((string)o["id"], B(o, "active")); PushState(); break;
                    case "setAllSources": _ctl.SetAllSources(B(o, "active")); PushState(); break;
                    case "setContentLevel": _ctl.SetContentLevel(B(o, "spicy"), (string)o["tier"], B(o, "spicyOnly")); PushState(); break;
                    case "setNoProfanity": _ctl.SetNoProfanity(B(o, "on")); PushState(); break;
                    case "setGenre": _ctl.SetGenreEnabled((string)o["id"], B(o, "enabled")); PushState(); break;
                    case "rebuild": _ctl.RebuildSmartWeights(); PushSmartStatus(_ctl.State.SmartStatus); break;
                    case "apply": OpResult r = _ctl.Apply(); PushState(); PostApplyResult(r); break;
                }
            }
            catch { /* keep the pane alive; a failed command just leaves state unchanged */ }
        }
        private static bool B(JObject o, string k) { JToken t = o[k]; return t != null && t.Type == JTokenType.Boolean && (bool)t; }

        // ---------------- controller -> page ----------------
        private void OnSmartStatusChanged(string status)
        {
            if (InvokeRequired) { try { BeginInvoke((Action)(() => PushSmartStatus(status))); } catch { } return; }
            PushSmartStatus(status);
        }

        private void PushState()
        {
            if (!_pageReady) return;
            FortunesState s = _ctl.State;
            var payload = new
            {
                type = "state",
                state = new
                {
                    smartEnabled = s.SmartEnabled,
                    smartStatus = s.SmartStatus ?? "",
                    spicyEnabled = s.SpicyEnabled,
                    spicyTier = s.SpicyTier,
                    spicyOnly = s.SpicyOnly,
                    noProfanity = s.NoProfanity,
                    activeSources = s.ActiveSources,
                    totalSources = s.TotalSources,
                    activeLines = s.ActiveLines,
                    sources = s.Sources.ConvertAll(r => (object)new { id = r.Id, topic = r.Topic, lines = r.Lines, hasSpicy = r.HasSpicy, active = r.Active }),
                    genres = s.Genres.ConvertAll(g => (object)new { id = g.Id, enabled = g.Enabled }),
                },
            };
            _host.PostState(JsonConvert.SerializeObject(payload));
        }
        private void PushSmartStatus(string status)
        {
            if (!_pageReady) return;
            _host.PostState(JsonConvert.SerializeObject(new { type = "smartStatus", text = status ?? "" }));
        }
        private void PostApplyResult(OpResult r)
        {
            if (!_pageReady || r == null) return;
            _host.PostState(JsonConvert.SerializeObject(new { type = "applyResult", ok = r.Ok, message = r.Message ?? "" }));
        }

        private static string LoadHtmlResource()
        {
            Assembly asm = typeof(FortunesWebView).Assembly;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("fortunes-view.html", StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream st = asm.GetManifestResourceStream(name))
                    using (var rd = new StreamReader(st))
                        return rd.ReadToEnd();
                }
            }
            return "<!doctype html><html><body style='font-family:Segoe UI;background:#141414;color:#eee'>Fortunes view resource missing.</body></html>";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _statusTimer.Stop(); _statusTimer.Dispose(); } catch { }
                try { _ctl.SmartStatusChanged -= OnSmartStatusChanged; } catch { }
                try { _host.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// End-to-end smoke for the WebView Fortunes control-center (--fortunes-webview-selftest): loads the
    /// real embedded page, confirms the host pushed state and the page rendered rows, and round-trips a
    /// JS-&gt;C# command (Disable all / Enable all) to prove the bridge and controller are wired. Skips
    /// (pass) when the WebView2 runtime is absent, since that path falls back to the native tab. Requires
    /// an isolated DESKTOPPET_DATA_ROOT so it never touches real settings.
    /// </summary>
    internal static class FortunesWebViewSelfTest
    {
        private sealed class FakeRuntime : IPetRuntime
        {
            public string ActivePetXml { get { return ""; } }
            public bool IsAtMaxPets { get { return false; } }
            public bool LoadNewXMLFromString(string xml) { return true; }
            public bool AddPetFromTray(string id) { return true; }
            public bool RemoveOnePet(string id) { return true; }
            public string SmartFortunesStatus() { return "selftest"; }
            public void RebuildSmartFortunes() { }
            public void ReloadAiSettings() { }
        }

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok;
            try
            {
                if (!WebViewHost.RuntimeAvailable())
                {
                    sb.AppendLine("SKIP: WebView2 runtime not installed (native Fortunes tab is the fallback).");
                    ok = true;
                }
                else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKTOPPET_DATA_ROOT")))
                {
                    sb.AppendLine("FAIL: DESKTOPPET_DATA_ROOT must be set (isolated root).");
                    ok = false;
                }
                else
                {
                    ok = Drive(sb);
                }
            }
            catch (Exception ex) { sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); ok = false; }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-fortunes-webview-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }

        private static bool Drive(StringBuilder sb)
        {
            Application.EnableVisualStyles();
            bool ok = true;
            var ai = AiSettings.Load();
            using (var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, Opacity = 0d })
            using (var view = new FortunesWebView(ai, new FakeRuntime()) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(view);
                form.Show();
                if (!PumpUntil(() => view.PageReady, 30000)) { sb.AppendLine("FAIL: page never signalled ready"); return false; }

                int total = view.ControllerState.TotalSources;
                ok &= Check(sb, "controller enumerated sources", total > 0);

                int rendered = EvalInt(view, "document.querySelectorAll('#rows .trow').length");
                ok &= Check(sb, "page rendered source rows from pushed state", rendered > 0);

                // JS -> C# round-trip: Disable all, then Enable all.
                view.EvalAsync("document.getElementById('btn-none').click()");
                ok &= Check(sb, "Disable all zeroes active sources", PumpUntil(() => view.ControllerState.ActiveSources == 0, 5000));
                view.EvalAsync("document.getElementById('btn-all').click()");
                ok &= Check(sb, "Enable all restores active sources", PumpUntil(() => view.ControllerState.ActiveSources == total, 5000));

                try { form.Close(); } catch { }
            }
            return ok;
        }

        private static int EvalInt(FortunesWebView view, string js)
        {
            Task<string> t = view.EvalAsync(js);
            DateTime deadline = DateTime.Now.AddSeconds(5);
            while (!t.IsCompleted && DateTime.Now < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            int n;
            return (t.IsCompleted && t.Result != null && int.TryParse(t.Result.Trim('"'), out n)) ? n : -1;
        }
        private static bool PumpUntil(Func<bool> cond, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline) { if (cond()) return true; Application.DoEvents(); Thread.Sleep(25); }
            return cond();
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
    }
}
