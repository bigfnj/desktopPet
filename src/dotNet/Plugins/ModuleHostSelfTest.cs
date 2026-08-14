using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// --module-host-selftest: proves the plugin pipeline end-to-end without WinForms. Loads the real
    /// bundled test-module DLL (from &lt;baseDir&gt;\modules) through the AssemblyLoadContext loader against a
    /// recording host, then asserts the module's Init ran (contributed a tray item + an options pane) and
    /// that a raised PetPoked event reached the module (it calls host.SayAll). Skips-pass if the test
    /// module folder is absent (e.g. a payload without dev modules).
    /// </summary>
    internal static class ModuleHostSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "testmodule")))
                {
                    sb.AppendLine("SKIP: no bundled test module at " + Path.Combine(modulesRoot, "testmodule"));
                    return Finish(sb, true);
                }

                var host = new RecordingHost();
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);
                    ok &= Check(sb, "test module reports its id", HasModule(loader, "testmodule"));
                    ok &= Check(sb, "module contributed a tray item", host.TrayItems.Count >= 1);
                    ok &= Check(sb, "module contributed an options pane", host.OptionsPanes.Count >= 1);

                    host.RaisePetPoked(new PokeInfo { Pet = new FakePet(), PokeCount = 1 });
                    ok &= Check(sb, "PetPoked event reached the module (SayAll recorded)", host.LastSayAll == "poked!");

                    loader.ShutdownAll(s => sb.AppendLine("  " + s));
                    // After shutdown the module unsubscribes: a second poke must NOT re-trigger.
                    host.LastSayAll = null;
                    host.RaisePetPoked(new PokeInfo { Pet = new FakePet(), PokeCount = 2 });
                    ok &= Check(sb, "module unsubscribed on Shutdown", host.LastSayAll == null);
                }

                ok &= PendingUpdateSwap(sb);
                ok &= MonthlyCheckSchedule(sb);
                ok &= UpdateScanVersionRule(sb);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            return Finish(sb, ok);
        }

        /// <summary>
        /// The deferred module-update swap (<see cref="PendingModuleUpdates"/>), on throwaway directories. It
        /// is the only place an update can go wrong destructively, so all four outcomes are asserted: a staged
        /// payload replaces the installed one, the module's DATA survives (the reason updates exist at all),
        /// an id whose install folder is gone is discarded rather than resurrected, and an empty staging folder
        /// leaves the installed copy alone. Everything (install root, staging root, marker file) is a throwaway
        /// temp path, so the test never reads or writes the real install or data directories.
        /// </summary>
        private static bool PendingUpdateSwap(StringBuilder sb)
        {
            string root = Path.Combine(Path.GetTempPath(), "dp-module-update-selftest-" + Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(root, "modules");
                string stagingRoot = Path.Combine(root, "module-staging");
                string marker = Path.Combine(root, "pending-module-updates.txt");
                Directory.CreateDirectory(root);

                string installed = Path.Combine(modulesRoot, "demo");
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "Demo.dll"), "old");
                // Mirrors PetHost.ModuleDataDirectory's layout (<data root>\modules\<id>): a module's data lives
                // OUTSIDE its install folder, and an update -- unlike an uninstall -- must leave it alone.
                string moduleData = Path.Combine(root, "data", "modules", "demo");
                Directory.CreateDirectory(moduleData);
                File.WriteAllText(Path.Combine(moduleData, "settings.json"), "keep me");

                string staged = PendingModuleUpdates.PrepareStagingDirectory("demo", stagingRoot);
                File.WriteAllText(Path.Combine(staged, "Demo.dll"), "new");
                PendingModuleUpdates.MarkForUpdate("demo", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));

                ok &= Check(sb, "update: staged payload replaced the installed module",
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");
                ok &= Check(sb, "update: the module's data directory survived",
                    File.Exists(Path.Combine(moduleData, "settings.json")));
                ok &= Check(sb, "update: marker cleared so the swap runs once", !File.Exists(marker));

                // An update for something no longer installed must be discarded, not resurrected.
                string gone = PendingModuleUpdates.PrepareStagingDirectory("removed", stagingRoot);
                File.WriteAllText(Path.Combine(gone, "Removed.dll"), "new");
                PendingModuleUpdates.MarkForUpdate("removed", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an uninstalled module is not resurrected",
                    !Directory.Exists(Path.Combine(modulesRoot, "removed")));

                // An empty staging folder must leave the installed copy intact.
                PendingModuleUpdates.PrepareStagingDirectory("demo", stagingRoot);
                PendingModuleUpdates.MarkForUpdate("demo", marker);
                PendingModuleUpdates.ProcessPending(modulesRoot, stagingRoot, marker, s => sb.AppendLine("  " + s));
                ok &= Check(sb, "update: an empty staged payload keeps the installed module",
                    File.Exists(Path.Combine(installed, "Demo.dll")) &&
                    File.ReadAllText(Path.Combine(installed, "Demo.dll")) == "new");
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC (pending update swap): " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
            return ok;
        }

        /// <summary>
        /// The monthly-check due-ness rule. Asserted directly because every interesting case is a date the test
        /// cannot wait for: the point of the "last successful month" stamp (rather than a literal 1st-of-month
        /// alarm) is that a month is never SKIPPED just because the pet was not running that day, and never
        /// re-checked twice in the same month.
        /// </summary>
        private static bool MonthlyCheckSchedule(StringBuilder sb)
        {
            var march = new DateTime(2026, 3, 14);
            bool ok = Check(sb, "monthly: same month as the last check is not due",
                !ModuleUpdateSchedule.IsDue(march, "2026-03"));
            ok &= Check(sb, "monthly: a new month is due even on the 14th (a missed 1st still checks)",
                ModuleUpdateSchedule.IsDue(march, "2026-02"));
            ok &= Check(sb, "monthly: due across a year boundary",
                ModuleUpdateSchedule.IsDue(new DateTime(2027, 1, 1), "2026-12"));
            ok &= Check(sb, "monthly: a stamp in the future is not due (clock moved back)",
                !ModuleUpdateSchedule.IsDue(march, "2026-04"));
            ok &= Check(sb, "monthly: an absent or unparseable stamp is not due (the caller seeds it)",
                !ModuleUpdateSchedule.IsDue(march, "") && !ModuleUpdateSchedule.IsDue(march, "garbage"));

            string path = Path.Combine(Path.GetTempPath(), "dp-monthly-selftest-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                ok &= Check(sb, "monthly: a missing stamp file reads as empty", ModuleUpdateSchedule.ReadStamp(path) == "");
                ModuleUpdateSchedule.WriteStamp(path, march);
                ok &= Check(sb, "monthly: the stamp round-trips as yyyy-MM", ModuleUpdateSchedule.ReadStamp(path) == "2026-03");
                ok &= Check(sb, "monthly: a just-written stamp is not due again",
                    !ModuleUpdateSchedule.IsDue(march, ModuleUpdateSchedule.ReadStamp(path)));
            }
            finally { try { File.Delete(path); } catch { } }
            return ok;
        }

        /// <summary>
        /// The one version rule shared by the Update button and the monthly check. Newer offers, equal and older
        /// do not, and an unparseable version on either side offers NOTHING — a guess there becomes an update
        /// prompt that survives being accepted.
        /// </summary>
        private static bool UpdateScanVersionRule(StringBuilder sb)
        {
            var catalog = new RemoteCatalog();
            catalog.Modules.Add(new CatalogModule { Id = "demo", Name = "Demo", Version = "1.1.1" });
            catalog.Modules.Add(new CatalogModule { Id = "weird", Name = "Weird", Version = "not-a-version" });

            bool ok = Check(sb, "scan: a newer catalog version is offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.1.0") != null);
            ok &= Check(sb, "scan: an equal version is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.1.1") == null);
            ok &= Check(sb, "scan: an older catalog version is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "1.2.0") == null);
            ok &= Check(sb, "scan: an unknown id is not offered",
                ModuleUpdateScan.FindUpdate(catalog, "absent", "1.0.0") == null);
            ok &= Check(sb, "scan: an unparseable installed version offers nothing",
                ModuleUpdateScan.FindUpdate(catalog, "demo", "dev") == null);
            ok &= Check(sb, "scan: an unparseable catalog version offers nothing",
                ModuleUpdateScan.FindUpdate(catalog, "weird", "1.0.0") == null);
            ok &= Check(sb, "scan: no catalog (never fetched) offers nothing",
                ModuleUpdateScan.FindUpdate(null, "demo", "1.1.0") == null);

            var offers = new List<ModuleUpdateOffer>
            {
                new ModuleUpdateOffer { Offered = catalog.Modules[0], InstalledVersion = "1.1.0" },
            };
            ok &= Check(sb, "scan: one offer describes as 'Demo 1.1.1'", ModuleUpdateScan.Describe(offers) == "Demo 1.1.1");
            offers.Add(new ModuleUpdateOffer { Offered = new CatalogModule { Id = "b", Name = "Bee", Version = "2.0" }, InstalledVersion = "1.0" });
            ok &= Check(sb, "scan: two offers read as a sentence", ModuleUpdateScan.Describe(offers) == "Demo 1.1.1 and Bee 2.0");
            ok &= Check(sb, "scan: no offers describe as empty", ModuleUpdateScan.Describe(new List<ModuleUpdateOffer>()) == "");
            return ok;
        }

        private static bool HasModule(ModuleHost loader, string id)
        {
            foreach (IModule m in loader.Modules)
                if (m.Info != null && string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-module-host-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }

        private sealed class FakePet : IPet { public int Id { get { return 1; } } public bool IsBusy { get { return false; } } }

        /// <summary>A headless IHost that records what modules do, for the self-test.</summary>
        private sealed class RecordingHost : IHost
        {
            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }
            public string LastSayAll;
            public readonly List<TrayItem> TrayItems = new List<TrayItem>();
            public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action HostShutdown;
            public void RaisePetPoked(PokeInfo p) { var h = PetPoked; if (h != null) h(p); }
            // (Other Raise* omitted: the self-test only exercises PetPoked; referencing the events keeps the
            //  compiler from warning them unused.)
            // Never called: it exists so the events count as "used" under TreatWarningsAsErrors (CS0067).
            internal void TouchEvents() { PetSpawned?.Invoke(null); PetLanded?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { LastSayAll = text; }
            public void SayAll(string text) { LastSayAll = text; }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new MemStorage(); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new NoopDisposable(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new NoopDisposable(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return PickedFiles; }
            public string OpenedLink;
            public bool OpenLink(string moduleId, string httpsUrl) { OpenedLink = httpsUrl; return true; }
            public List<string> PickedFiles = new List<string>();
            public void AddTrayItems(IEnumerable<TrayItem> items) { if (items != null) TrayItems.AddRange(items); }
            public void AddOptionsPane(OptionsPane pane) { if (pane != null) OptionsPanes.Add(pane); }

            private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
            private sealed class MemStorage : IModuleStorage { public string DataDirectory { get { return Path.GetTempPath(); } } }
            private sealed class MemSettings : IModuleSettings
            {
                private readonly Dictionary<string, string> _d = new Dictionary<string, string>();
                public string Get(string key, string fallback) { string v; return _d.TryGetValue(key, out v) ? v : fallback; }
                public int GetInt(string key, int fallback) { string v; int n; return (_d.TryGetValue(key, out v) && int.TryParse(v, out n)) ? n : fallback; }
                public bool GetBool(string key, bool fallback) { string v; bool b; return (_d.TryGetValue(key, out v) && bool.TryParse(v, out b)) ? b : fallback; }
                public void Set(string key, string value) { _d[key] = value; }
                public bool Save() { return true; }
            }
        }
    }
}
