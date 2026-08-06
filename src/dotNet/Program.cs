using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    /// <summary>
    /// Main for the application. Once the application is started, this class will create all objects.
    /// </summary>
    class Program
    {
        /// <summary>
        /// StartUp is the main program.
        /// </summary>
        public static StartUp Mainthread;
        internal static bool ResourceChurnSelfTestActive { get; private set; }

#if PORTABLE
        public static LocalData MyData;
        public static string InitialXmlOverride = "";
        private static int restartRequested;

        /// <summary>
        /// Open the option dialog, to show some options like reset XML animation or load animation from the webpage.
        /// </summary>
        public static void OpenOptionDialog()
        {
            using (FormOptions formoptions = new FormOptions())
            {
                switch (formoptions.ShowDialog())
                {
                    case DialogResult.Retry:
                        StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.warning, "restoring default XML");

                        if (!TryRequestRestartAfterSave(
                            delegate { return MyData.TrySetPetAssets("", "", ""); },
                            RequestRestart))
                        {
                            MessageBox.Show(
                                "DesktopPet could not save the restored default pet. " +
                                "The running pet was left unchanged.",
                                "Default pet not restored",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            break;
                        }
                        Application.Exit();
                        break;
                }
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // net10 no longer reads DPI awareness from app.config's ApplicationConfigurationSection
            // (defaults to SystemAware otherwise), so set PerMonitorV2 explicitly before any UI.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The smart-fortunes ONNX runtime + its System.* deps + the bge-small model ship as
            // plain files beside the exe (proper MSI / portable zip) and load with standard resolution.

            // Hidden diagnostic: prove the local embedder loads/runs in the real app context
            // (binding redirects + native onnxruntime resolution). Writes to a temp file and exits.
            if (args != null && Array.IndexOf(args, "--embed-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Ai.Embedder.SelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--smart-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Ai.SmartFortunes.SelfTest() ? 0 : 1);
            }
            // Options controller seam: drives all four controllers with fakes against an isolated
            // DESKTOPPET_DATA_ROOT (clamping, source round-trip, no-secret-leak). Writes a temp file.
            if (args != null && Array.IndexOf(args, "--options-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Options.OptionsSelfTest.Run() ? 0 : 1);
            }
            // WebView2 host smoke: init the runtime with our custom user-data folder + load offline
            // HTML. Skips (pass) when the runtime is absent (WinForms fallback path). Writes a temp file.
            if (args != null && Array.IndexOf(args, "--webview-selftest") >= 0)
            {
                Environment.Exit(WebViewSelfTest.Run() ? 0 : 1);
            }
            // End-to-end smoke for the WebView Fortunes control-center: real page + state push + a
            // JS->C# command round-trip. Needs an isolated DESKTOPPET_DATA_ROOT.
            if (args != null && Array.IndexOf(args, "--fortunes-webview-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Options.FortunesWebViewSelfTest.Run() ? 0 : 1);
            }
            // Writable-folder fortune cache: proves add/edit/remove invalidation. Needs isolated root.
            if (args != null && Array.IndexOf(args, "--fortunecache-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Ai.FortuneProvider.CustomCacheSelfTest() ? 0 : 1);
            }
            // Opt-in (slow: real cold-cache embed): prove progressive warming exposes a matchable
            // prefix before the whole pool is done. Writes to a temp file and exits.
            if (args != null && Array.IndexOf(args, "--smart-progress-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Ai.SmartFortunes.ProgressiveSelfTest() ? 0 : 1);
            }
            // Fullscreen-awareness diagnostic: per-monitor scan length + relocation-decision logic.
            if (args != null && Array.IndexOf(args, "--fullscreen-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.FullscreenScan.SelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--filter-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.Ai.FortuneProvider.FilterSelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--catalog-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.RemoteCatalogClient.SelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--online-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.RemoteCatalogClient.OnlineSelfTest() ? 0 : 1);
            }
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg == null ||
                        !arg.StartsWith("--catalog-parse-file=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string catalogPath = arg.Substring("--catalog-parse-file=".Length);
                    string resultPath = Path.Combine(
                        Path.GetTempPath(), "dp-catalog-parse.txt");
                    try
                    {
                        var parsedCatalog = DesktopPet.RemoteCatalogClient.Parse(
                            File.ReadAllText(catalogPath));
                        File.WriteAllText(resultPath,
                            "catalog_parse=PASS pets=" + parsedCatalog.Pets.Count +
                            " packs=" + parsedCatalog.Packs.Count);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(resultPath, "catalog_parse=FAIL " + ex.Message); }
                        catch { }
                        Environment.Exit(1);
                    }
                }
            }
            if (args != null && Array.IndexOf(args, "--security-selftest") >= 0)
            {
                Environment.Exit(DesktopPet.SecuritySelfTest.Run(Console.Out) ? 0 : 1);
            }

            RuntimeResourceChurnConfiguration resourceChurn = null;
            if (args != null &&
                Array.IndexOf(args, "--resource-churn-selftest") >= 0)
            {
                string churnError;
                if (!RuntimeResourceChurnConfiguration.TryCreate(
                        out resourceChurn,
                        out churnError))
                {
                    Debug.WriteLine(
                        "Resource churn self-test rejected: " + churnError);
                    Environment.Exit(2);
                }
                ResourceChurnSelfTestActive = true;
            }

            IDisposable instanceLease = TryAcquireInstanceSlot();
            if (instanceLease == null)
            {
                MessageBox.Show(
                    "Application is already running! Only 2 instances are allowed.",
                    "Information",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Load/migrate mutable settings only after this instance owns a cross-session
                // slot, so a rejected third launch cannot write application data.
                MyData = new LocalData();
                if (!string.IsNullOrWhiteSpace(MyData.SettingsWarning))
                {
                    MessageBox.Show(
                        "DesktopPet could not fully access its settings storage. " +
                        "It will continue with settings held in memory, but changes " +
                        "may not be saved.\r\n\r\n" +
                        MyData.SettingsWarning,
                        "Settings storage unavailable",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                // Check and parse the arguments
                const string SearchStringLocalXml = "localxml=";
                const string SearchStringWebXml = "webxml=";
                const string SearchStringInstall = "install=";
			    foreach (string s in args)
                {
                    if (s.StartsWith(SearchStringLocalXml, StringComparison.OrdinalIgnoreCase))
                    {
                        string localXmlPath = s.Substring(SearchStringLocalXml.Length);
                        try
                        {
                            InitialXmlOverride = ReadBoundedUtf8File(
                                localXmlPath,
                                PetXmlValidator.MaximumXmlBytes);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                "Could not load the requested local pet: " + ex.Message,
                                "Invalid pet",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            return;
                        }
                    }
                    else if (s.StartsWith(SearchStringWebXml, StringComparison.OrdinalIgnoreCase) ||
                             s.StartsWith(SearchStringInstall, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(
                            "Remote and legacy installer pet arguments are disabled. " +
                            "Import a bounded local XML file instead.",
                            "Unsupported pet source",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                }

                // Show the system tray icon.
                using (ProcessIcon pi = new ProcessIcon())
                {
                    pi.Display();

                    RuntimeResourceChurn resourceChurnRunner = null;
                    try
                    {
                        Mainthread = new StartUp(pi);
                        if (resourceChurn != null)
                            resourceChurnRunner =
                                new RuntimeResourceChurn(
                                    Mainthread,
                                    resourceChurn);

                        // Make sure the application runs!
                        Application.Run();
                    }
                    finally
                    {
                        if (resourceChurnRunner != null)
                            resourceChurnRunner.Dispose();
                        if (Mainthread != null) Mainthread.Dispose();
                        Mainthread = null;
                        ResourceChurnSelfTestActive = false;
                    }
                }
            }
            finally
            {
                CompleteInstanceLifecycle(
                    instanceLease,
                    ConsumeRestartRequest,
                    LaunchReplacement);
            }
        }

        internal static void RequestRestart()
        {
            Interlocked.Exchange(ref restartRequested, 1);
        }

        internal static bool TryRequestRestartAfterSave(
            Func<bool> save,
            Action requestRestart)
        {
            if (save == null) throw new ArgumentNullException("save");
            if (requestRestart == null)
                throw new ArgumentNullException("requestRestart");
            if (!save()) return false;
            requestRestart();
            return true;
        }

        internal static void CompleteInstanceLifecycle(
            IDisposable instanceLease,
            Func<bool> consumeRestartRequest,
            Action launchReplacement)
        {
            if (instanceLease == null)
                throw new ArgumentNullException("instanceLease");
            if (consumeRestartRequest == null)
                throw new ArgumentNullException("consumeRestartRequest");
            if (launchReplacement == null)
                throw new ArgumentNullException("launchReplacement");

            instanceLease.Dispose();
            if (consumeRestartRequest())
                launchReplacement();
        }

        private static bool ConsumeRestartRequest()
        {
            return Interlocked.Exchange(ref restartRequested, 0) != 0;
        }

        private static void LaunchReplacement()
        {
            try
            {
                using (Process replacement = Process.Start(Application.ExecutablePath))
                {
                    // Release only our process handle. The replacement keeps running.
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "DesktopPet closed, but its replacement could not be started: " +
                    ex.Message,
                    "Restart failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static IDisposable TryAcquireInstanceSlot()
        {
            string firstPath = Path.Combine(
                AppPaths.DataRoot,
                ".instance-slot-1");
            IDisposable lease = CrossSessionLock.TryAcquire(
                CrossSessionLock.BuildGlobalMutexName(
                    "Instance1",
                    firstPath),
                firstPath,
                1000);
            if (lease != null) return lease;

            string secondPath = Path.Combine(
                AppPaths.DataRoot,
                ".instance-slot-2");
            return CrossSessionLock.TryAcquire(
                CrossSessionLock.BuildGlobalMutexName(
                    "Instance2",
                    secondPath),
                secondPath,
                1000);
        }

#else

        public static LocalData.LocalData MyData = null;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MyData = new LocalData.LocalData(Windows.Storage.ApplicationData.Current.LocalFolder.Path, Application.ExecutablePath);

            // Show the system tray icon.					
            using (ProcessIcon pi = new ProcessIcon())
            {
                pi.Display();

                Mainthread = new StartUp(pi);

                // Make sure the application runs!
                Application.Run();
            }
        }
#endif

        /// <summary>
        /// Check if application is started from the installation path.
        /// </summary>
        /// <returns>true if the executed application is installed.</returns>
        public static bool IsApplicationInstalled()
        {
            return AppPaths.IsInstalled;
        }

        private static string ReadBoundedUtf8File(string path, int maximumBytes)
        {
            PetXmlValidator.RetainedLocalXmlFile retained;
            string pathError;
            if (!PetXmlValidator.TryOpenLocalXmlFile(
                    path,
                    out retained,
                    out pathError))
                throw new InvalidDataException(pathError);

            using (retained)
            using (var stream = retained.OpenRead(4096))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException(
                        "The local pet XML exceeds its size limit.");
                using (var memory = new MemoryStream(
                    (int)Math.Min(stream.Length, maximumBytes)))
                {
                    byte[] buffer = new byte[8192];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "The local pet XML exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }

                    byte[] bytes = memory.ToArray();
                    int offset = bytes.Length >= 3 &&
                                 bytes[0] == 0xEF &&
                                 bytes[1] == 0xBB &&
                                 bytes[2] == 0xBF
                        ? 3
                        : 0;
                    return new System.Text.UTF8Encoding(false, true).GetString(
                        bytes,
                        offset,
                        bytes.Length - offset);
                }
            }
        }
    }

#if PORTABLE
    internal sealed class RuntimeResourceChurnConfiguration
    {
        internal string MarkerPath;
        internal int Cycles;
        internal int IntervalMilliseconds;
        internal int MinimumDurationMilliseconds;
        internal int ExitDelayMilliseconds;

        internal static bool TryCreate(
            out RuntimeResourceChurnConfiguration configuration,
            out string error)
        {
            configuration = null;
            error = null;
            try
            {
                string configuredRoot =
                    Environment.GetEnvironmentVariable(
                        "DESKTOPPET_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(configuredRoot) ||
                    !Path.IsPathRooted(configuredRoot))
                    throw new InvalidOperationException(
                        "DESKTOPPET_DATA_ROOT must name an absolute isolated directory.");

                string root = Path.GetFullPath(configuredRoot)
                    .TrimEnd(Path.DirectorySeparatorChar);
                string temp = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar);
                string requiredPrefix =
                    temp + Path.DirectorySeparatorChar +
                    "DesktopPet-ResourceSoak-";
                if (!root.StartsWith(
                        requiredPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    root.Length <= requiredPrefix.Length)
                    throw new InvalidOperationException(
                        "The resource churn data root must use the temporary " +
                        "DesktopPet-ResourceSoak-* boundary.");

                configuration = new RuntimeResourceChurnConfiguration
                {
                    MarkerPath = Path.Combine(
                        root,
                        "resource-churn-result.json"),
                    Cycles = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_CYCLES",
                        32,
                        4,
                        1000),
                    IntervalMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_INTERVAL_MS",
                        250,
                        50,
                        5000),
                    MinimumDurationMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_MIN_DURATION_MS",
                        10000,
                        1000,
                        900000),
                    ExitDelayMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_EXIT_DELAY_MS",
                        5000,
                        1000,
                        30000)
                };
                return true;
            }
            catch (Exception ex)
            {
                configuration = null;
                error = ex.Message;
                return false;
            }
        }

        private static int ReadBoundedInteger(
            string name,
            int fallback,
            int minimum,
            int maximum)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            int parsed;
            if (!int.TryParse(value, out parsed) ||
                parsed < minimum ||
                parsed > maximum)
                throw new InvalidOperationException(
                    name + " is outside its diagnostic bound.");
            return parsed;
        }
    }

    internal sealed class RuntimeResourceChurn : IDisposable
    {
        private readonly StartUp runtime;
        private readonly RuntimeResourceChurnConfiguration configuration;
        private readonly System.Windows.Forms.Timer cycleTimer;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private System.Windows.Forms.Timer exitTimer;
        private bool finished;
        private int cycles;
        private int speechAndPetCycles;
        private int optionsCycles;
        private int optionsCancellationCycles;
        private int aboutCycles;
        private int helpCycles;
        private int trayAndMenuCycles;

        internal RuntimeResourceChurn(
            StartUp runtime,
            RuntimeResourceChurnConfiguration configuration)
        {
            this.runtime = runtime ??
                throw new ArgumentNullException("runtime");
            this.configuration = configuration ??
                throw new ArgumentNullException("configuration");
            cycleTimer = new System.Windows.Forms.Timer
            {
                Interval = configuration.IntervalMilliseconds
            };
            cycleTimer.Tick += CycleTimer_Tick;
            cycleTimer.Start();
        }

        private void CycleTimer_Tick(object sender, EventArgs e)
        {
            cycleTimer.Stop();
            try
            {
                RunCycle(cycles);
                cycles++;
                if (cycles >= configuration.Cycles &&
                    elapsed.ElapsedMilliseconds >=
                        configuration.MinimumDurationMilliseconds)
                {
                    Finish(true, null);
                    return;
                }
                cycleTimer.Start();
            }
            catch (Exception ex)
            {
                Finish(false, ex);
            }
        }

        private void RunCycle(int cycle)
        {
            string astral = char.ConvertFromUtf32(0x1F642);
            string speech =
                "Resource churn " + cycle + " " + astral +
                " exercises text, bubble paint, and pet image ownership.";
            if (!runtime.RunResourceChurnPetCycle(speech))
                throw new InvalidOperationException(
                    "The speech/pet churn path did not complete.");
            speechAndPetCycles++;

            Task canceledOperation;
            using (var options = new FormOptions())
            {
                PrepareHiddenForm(options);
                options.Show();
                Application.DoEvents();
                options.ExerciseTabsForResourceChurn();
                canceledOperation =
                    options.BeginResourceChurnCloseForDiagnostics();
                options.Close();
                Application.DoEvents();
            }
            optionsCycles++;
            if (canceledOperation == null ||
                !canceledOperation.IsCanceled)
                throw new InvalidOperationException(
                    "The Options close path did not cancel its tracked operation.");
            optionsCancellationCycles++;

            using (var about = new AboutBox())
            {
                PrepareHiddenForm(about);
                about.FillData(
                    "DesktopPet diagnostics",
                    "Resource ownership",
                    Application.ProductVersion,
                    "Unicode " + astral +
                    " [br] [link:https://example.invalid/resource-test]");
                ExerciseForm(about);
            }
            aboutCycles++;

            using (var help = new FormHelp())
            {
                PrepareHiddenForm(help);
                ExerciseForm(help);
            }
            helpCycles++;

            if (!runtime.RefreshTrayIconForResourceChurn())
                throw new InvalidOperationException(
                    "The tray icon refresh path did not complete.");
            ContextMenus.RefreshSpeechMenuItem();
            ContextMenus.RefreshAiBrainMenuItem(runtime.AiBrainEnabled);
            trayAndMenuCycles++;
        }

        private static void PrepareHiddenForm(Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            form.Location = new Point(
                virtualScreen.Right + 64,
                virtualScreen.Bottom + 64);
            form.Opacity = 0d;
        }

        private static void ExerciseForm(Form form)
        {
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            form.Refresh();
            Application.DoEvents();
            form.Close();
            Application.DoEvents();
        }

        private void Finish(bool passed, Exception failure)
        {
            if (finished) return;
            finished = true;
            cycleTimer.Stop();
            elapsed.Stop();
            WriteMarker(passed, failure);
            if (!passed) Environment.ExitCode = 1;

            exitTimer = new System.Windows.Forms.Timer
            {
                Interval = configuration.ExitDelayMilliseconds
            };
            exitTimer.Tick += ExitTimer_Tick;
            exitTimer.Start();
        }

        private void WriteMarker(bool passed, Exception failure)
        {
            string markerDirectory =
                Path.GetDirectoryName(configuration.MarkerPath);
            Directory.CreateDirectory(markerDirectory);
            var result = new JObject
            {
                ["result"] = passed ? "PASS" : "FAIL",
                ["cycles"] = cycles,
                ["targetCycles"] = configuration.Cycles,
                ["elapsedMilliseconds"] = elapsed.ElapsedMilliseconds,
                ["minimumDurationMilliseconds"] =
                    configuration.MinimumDurationMilliseconds,
                ["speechAndPetCycles"] = speechAndPetCycles,
                ["optionsCycles"] = optionsCycles,
                ["optionsCancellationCycles"] =
                    optionsCancellationCycles,
                ["aboutCycles"] = aboutCycles,
                ["helpCycles"] = helpCycles,
                ["trayAndMenuCycles"] = trayAndMenuCycles,
                ["error"] = failure == null
                    ? null
                    : failure.GetType().Name + ": " + failure.Message
            };
            string temporary =
                configuration.MarkerPath + ".tmp-" +
                Process.GetCurrentProcess().Id;
            File.WriteAllText(
                temporary,
                result.ToString(Formatting.Indented),
                new UTF8Encoding(false));
            if (File.Exists(configuration.MarkerPath))
                File.Delete(configuration.MarkerPath);
            File.Move(temporary, configuration.MarkerPath);
        }

        private void ExitTimer_Tick(object sender, EventArgs e)
        {
            exitTimer.Stop();
            Application.Exit();
        }

        public void Dispose()
        {
            cycleTimer.Stop();
            cycleTimer.Tick -= CycleTimer_Tick;
            cycleTimer.Dispose();
            if (exitTimer != null)
            {
                exitTimer.Stop();
                exitTimer.Tick -= ExitTimer_Tick;
                exitTimer.Dispose();
                exitTimer = null;
            }
        }
    }
#endif
}
