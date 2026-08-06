using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DesktopPet
{
    /// <summary>
    /// A hardened WebView2 host for local, offline HTML settings UIs. The renderer-agnostic Options
    /// controllers drive it over a JSON message bridge (JS window.chrome.webview.postMessage -> C#
    /// MessageReceived; C# PostState -> JS 'message' event). Fully offline: no external navigation,
    /// no dev tools, no context menu. The user-data folder lives under the app's writable data root
    /// (never next to the exe, which is read-only when installed).
    /// </summary>
    internal sealed class WebViewHost : UserControl
    {
        private readonly WebView2 _web = new WebView2();
        private bool _ready;
        private string _pendingHtml;

        public event Action<string> MessageReceived;   // raw JSON string from the page
        public event Action ControlReady;

        public bool IsReady { get { return _ready; } }

        public WebViewHost()
        {
            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);
        }

        /// <summary>True when the WebView2 runtime is installed (else the caller falls back to WinForms).</summary>
        public static bool RuntimeAvailable()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; }
        }

        public async Task InitAsync()
        {
            string udf = Path.Combine(AppPaths.DataRoot, "WebView2");
            try { Directory.CreateDirectory(udf); } catch { }
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                null, udf, new CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(env);

            CoreWebView2 core = _web.CoreWebView2;
            CoreWebView2Settings s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            core.WebMessageReceived += (snd, e) =>
            {
                Action<string> h = MessageReceived;
                if (h != null) { try { h(e.TryGetWebMessageAsString()); } catch { } }
            };

            _ready = true;
            Action ready = ControlReady;
            if (ready != null) ready();
            if (_pendingHtml != null) { core.NavigateToString(_pendingHtml); _pendingHtml = null; }
        }

        public void LoadHtml(string html)
        {
            if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.NavigateToString(html);
            else _pendingHtml = html;
        }

        public void PostState(string json)
        {
            if (_ready && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
        }

        public event EventHandler<CoreWebView2NavigationCompletedEventArgs> NavigationCompleted
        {
            add { _web.NavigationCompleted += value; }
            remove { _web.NavigationCompleted -= value; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _web.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Smoke test for the --webview-selftest flag: proves the WebView2 runtime initializes with our
    /// custom user-data folder and loads offline HTML. Skips (pass) when the runtime is absent, since
    /// absence is a supported condition that falls back to the WinForms view. Writes a temp result.
    /// </summary>
    internal static class WebViewSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool result;
            try
            {
                if (!WebViewHost.RuntimeAvailable())
                {
                    sb.AppendLine("SKIP: WebView2 runtime not installed (WinForms fallback path applies).");
                    result = true;
                }
                else
                {
                    result = LoadTrivialPage(sb);
                }
            }
            catch (Exception ex) { sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); result = false; }
            sb.AppendLine(result ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-webview-selftest.txt"), sb.ToString()); } catch { }
            return result;
        }

        private static bool LoadTrivialPage(StringBuilder sb)
        {
            Application.EnableVisualStyles();
            bool ok = false, done = false;
            string err = null;
            using (var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, Opacity = 0d })
            using (var host = new WebViewHost { Dock = DockStyle.Fill })
            {
                form.Controls.Add(host);
                host.NavigationCompleted += (s, e) => { ok = e.IsSuccess; done = true; };
                form.Shown += async (s, e) =>
                {
                    try { await host.InitAsync(); host.LoadHtml("<!doctype html><html><body>webview-selftest</body></html>"); }
                    catch (Exception ex) { err = ex.Message; done = true; }
                };
                form.Show();
                DateTime deadline = DateTime.Now.AddSeconds(30);
                while (!done && DateTime.Now < deadline) { Application.DoEvents(); Thread.Sleep(30); }
                try { form.Close(); } catch { }
            }
            sb.AppendLine("runtime=" + WebViewHost.RuntimeAvailable() + " navigated=" + done + " success=" + ok + (err != null ? " err=" + err : ""));
            return ok;
        }
    }
}
