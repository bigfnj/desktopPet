using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --petstudio-selftest: proves the Pet Studio module loads through the real AssemblyLoadContext and that
    /// its analysis agrees with the host's own validator.
    ///
    /// The agreement half is the point. Pet Studio source-links the host's parser rather than copying it, and
    /// the whole justification for that is "its verdict cannot drift from what the host will actually run".
    /// That is a claim worth testing rather than asserting: the module's analyzer and the host's
    /// PetXmlValidator are run over the same inputs and required to reach the same conclusion.
    ///
    /// Reflected, because the base keeps no compile-time reference to any module. Skips-pass if absent.
    /// </summary>
    internal static class PetStudioModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            string tempRoot = null;
            try
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "modules", "petstudio");
                if (!Directory.Exists(bundled))
                {
                    sb.AppendLine("SKIP: no bundled petstudio module at " + bundled);
                    return Finish(sb, true);
                }

                // Isolate so the recording host reflects this module's Init alone.
                tempRoot = Path.Combine(Path.GetTempPath(), "dp-petstudio-selftest-" + Guid.NewGuid().ToString("N"));
                string dest = Path.Combine(tempRoot, "petstudio");
                Directory.CreateDirectory(dest);
                foreach (string file in Directory.GetFiles(bundled))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(tempRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "exactly one module loaded (isolated)", loaded == 1);

                    IModule studio = null;
                    foreach (IModule m in loader.Modules)
                        if (m != null && m.Info != null &&
                            string.Equals(m.Info.Id, "petstudio", StringComparison.OrdinalIgnoreCase))
                            studio = m;
                    ok &= Check(sb, "petstudio module reports its id", studio != null);
                    if (studio == null) return Finish(sb, false);

                    ok &= Check(sb, "declares Pets + Storage",
                        studio.Info.Permissions.HasFlag(ModulePermissions.Pets) &&
                        studio.Info.Permissions.HasFlag(ModulePermissions.Storage));
                    // It needs the pet-manager verbs, so it must refuse to run on a host that lacks them.
                    ok &= Check(sb, "requires a host that has IPetManager",
                        !string.IsNullOrEmpty(studio.Info.MinHostVersion) &&
                        studio.Info.MinHostVersion != "1.0.0");
                    ok &= Check(sb, "contributes a tray item and an options pane",
                        host.TrayItems.Count >= 1 && host.OptionsPanes.Count >= 1);
                    ok &= Check(sb, "opening the studio is offered as a pane action",
                        host.OptionsPanes.Count > 0 && host.OptionsPanes[0].Actions != null);

                    ok &= AnalyzerAgreesWithTheHost(sb, studio.GetType().Assembly);

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                try { if (tempRoot != null) Directory.Delete(tempRoot, true); } catch { }
            }
            return Finish(sb, ok);
        }

        /// <summary>
        /// The module's analyzer and the host's validator must agree, because the module compiles its own copy
        /// of that validator from the host's source. A disagreement here means the source-link has rotted --
        /// exactly the failure that source-linking is supposed to make impossible, and the reason PetTester
        /// (which link-compiled a file that later moved) broke silently.
        /// </summary>
        private static bool AnalyzerAgreesWithTheHost(StringBuilder sb, Assembly moduleAssembly)
        {
            Type analyzer = moduleAssembly.GetType("DesktopPet.PetStudioModule.PetAnalyzer");
            if (!Check(sb, "module exposes PetAnalyzer", analyzer != null)) return false;
            MethodInfo analyze = analyzer.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!Check(sb, "PetAnalyzer exposes Analyze", analyze != null)) return false;

            string bundledPet = Properties.Resources.animations;
            var cases = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("the bundled pet", bundledPet),
                new KeyValuePair<string, string>("a pet with a DTD", "<?xml version=\"1.0\"?><!DOCTYPE animations [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><animations xmlns=\"https://esheep.petrucci.ch/\"><header><author>&x;</author></header></animations>"),
                new KeyValuePair<string, string>("junk", "not xml at all"),
                new KeyValuePair<string, string>("empty", ""),
            };

            bool ok = true;
            foreach (KeyValuePair<string, string> testCase in cases)
            {
                XmlData.RootNode parsed;
                string hostError;
                bool hostAccepts = PetXmlValidator.TryParse(testCase.Value, out parsed, out hostError);

                object report = analyze.Invoke(null, new object[] { testCase.Value });
                bool moduleAccepts = (bool)report.GetType().GetField("IsValid").GetValue(report);

                ok &= Check(sb,
                    "verdicts agree on " + testCase.Key + " (host=" + hostAccepts + ", module=" + moduleAccepts + ")",
                    hostAccepts == moduleAccepts);
            }

            // The bundled pet must also come back with a readable report and no dead animations -- the same
            // invariant --security-selftest asserts host-side, checked here through the module's own path.
            object bundledReport = analyze.Invoke(null, new object[] { bundledPet });
            var unreachable = (System.Collections.ICollection)bundledReport.GetType()
                .GetField("UnreachableAnimations").GetValue(bundledReport);
            ok &= Check(sb, "the bundled pet reports no unreachable animations", unreachable.Count == 0);

            string described = (string)bundledReport.GetType().GetMethod("Describe").Invoke(bundledReport, null);
            ok &= Check(sb, "the report describes the pet in prose",
                !string.IsNullOrWhiteSpace(described) && described.IndexOf("Valid pet", StringComparison.Ordinal) >= 0);
            return ok;
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-petstudio-selftest.txt"), sb.ToString()); } catch { }
            Console.Out.Write(sb.ToString());
            return ok;
        }

        private sealed class FakePet : IPet
        {
            public int Id { get { return 1; } }
            public bool IsBusy { get { return false; } }
            public string TypeId { get { return ""; } }
        }

        /// <summary>A headless IHost: Pet Studio only needs it to contribute UI and hand back a pet manager.</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "9999.0.0"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }

            public readonly List<TrayItem> TrayItems = new List<TrayItem>();
            public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action HostShutdown;
            // Never called: it exists so the events count as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { PetSpawned?.Invoke(new FakePet()); PetPoked?.Invoke(null); PetLanded?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { }
            public void SayAll(string text) { }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new Noop(); }
            public IModuleStorage GetStorage(string moduleId) { return null; }
            public IModuleSettings GetSettings(string moduleId) { return null; }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new Noop(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new Noop(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            public IPetManager GetPetManager(string moduleId) { return new DenyingPetManager(); }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return new List<string>(); }
            public bool OpenLink(string moduleId, string httpsUrl) { return false; }
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }

            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }
}
