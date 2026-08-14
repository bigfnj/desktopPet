using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// <c>--pets-selftest</c> (S6p2): loads the real bundled Pets.dll through the module loader against a
    /// recording host whose <c>GetPetManager()</c> returns a fake manager, then asserts the module contributes
    /// a "Pets" options pane with a button-driven roster (per-row RowActions) + a downloads card, and that the
    /// Use / Add row actions call through to the pet manager. Skips-pass when the module isn't present.
    /// </summary>
    internal static class PetsModuleSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
                if (!Directory.Exists(Path.Combine(modulesRoot, "pets")))
                {
                    sb.AppendLine("SKIP: no bundled pets module at " + Path.Combine(modulesRoot, "pets"));
                    return Finish(sb, true);
                }

                var fake = new FakePetManager();
                var host = new RecordingHost(fake);
                using (var loader = new ModuleHost())
                {
                    int loaded = loader.LoadFrom(modulesRoot, host, s => sb.AppendLine("  " + s));
                    ok &= Check(sb, "at least one module loaded", loaded >= 1);

                    OptionsPane pane = null;
                    foreach (OptionsPane p in host.OptionsPanes)
                        if (p != null && string.Equals(p.Title, "Pets", StringComparison.OrdinalIgnoreCase)) { pane = p; break; }
                    ok &= Check(sb, "Pets module contributes a 'Pets' options pane", pane != null);
                    if (pane == null) return Finish(sb, false);

                    ok &= Check(sb, "the pane has two list cards (roster + downloads)",
                        pane.Lists != null && pane.Lists.Count == 2);

                    ListCard roster = (pane.Lists != null && pane.Lists.Count > 0) ? pane.Lists[0] : null;
                    ok &= Check(sb, "the roster card hides checkboxes (button-driven)", roster != null && roster.HideCheckbox);

                    IReadOnlyList<ListItem> rows = (roster != null && roster.LoadItems != null) ? roster.LoadItems() : null;
                    ok &= Check(sb, "the roster lists the fake installed types", rows != null && rows.Count == fake.Types.Count);

                    ListItem builtinRow = null;
                    if (rows != null)
                        foreach (ListItem it in rows)
                            if (it != null && string.Equals(it.Id, "eSheep", StringComparison.OrdinalIgnoreCase)) { builtinRow = it; break; }
                    ok &= Check(sb, "the built-in pet has a row with actions",
                        builtinRow != null && builtinRow.RowActions != null && builtinRow.RowActions.Count >= 3);

                    if (builtinRow != null && builtinRow.RowActions != null)
                    {
                        RowAction use = FindByLabel(builtinRow, "Use");
                        RowAction add = FindByLabel(builtinRow, "Add");
                        ok &= Check(sb, "the row offers a Use action", use != null);
                        ok &= Check(sb, "the row offers an Add action", add != null);
                        if (use != null)
                        {
                            use.InvokeAsync().GetAwaiter().GetResult();
                            ok &= Check(sb, "Use called SetActiveType(eSheep)", fake.LastSetActive == "eSheep");
                        }
                        if (add != null)
                        {
                            add.InvokeAsync().GetAwaiter().GetResult();
                            ok &= Check(sb, "Add called SpawnOne(eSheep)", fake.LastSpawn == "eSheep");
                        }
                    }

                    ListCard online = (pane.Lists != null && pane.Lists.Count > 1) ? pane.Lists[1] : null;
                    ok &= Check(sb, "the downloads card offers check + download actions",
                        online != null && online.Actions != null && online.Actions.Count == 2);
                }
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            return Finish(sb, ok);
        }

        private static RowAction FindByLabel(ListItem row, string contains)
        {
            foreach (RowAction ra in row.RowActions)
                if (ra != null && !string.IsNullOrEmpty(ra.Label) &&
                    ra.Label.IndexOf(contains, StringComparison.Ordinal) >= 0)
                    return ra;
            return null;
        }

        private static bool Check(StringBuilder sb, string label, bool pass) { sb.AppendLine((pass ? "PASS: " : "FAIL: ") + label); return pass; }
        private static bool Finish(StringBuilder sb, bool ok) { sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL"); try { Console.Out.Write(sb.ToString()); } catch { } return ok; }

        private sealed class FakePetManager : IPetManager
        {
            public readonly List<PetTypeInfo> Types = new List<PetTypeInfo>
            {
                new PetTypeInfo { TypeId = "eSheep", DisplayName = "eSheep", IsBuiltIn = true },
                new PetTypeInfo { TypeId = "pink_sheep", DisplayName = "Pearl", IsBuiltIn = false },
            };
            public string LastSpawn, LastRemove, LastSetActive;
            public IReadOnlyList<PetTypeInfo> InstalledTypes() { return Types; }
            public IReadOnlyList<PetCount> OnScreenMix() { return new List<PetCount> { new PetCount { TypeId = "eSheep", Count = 1 } }; }
            public int MaxPets { get { return 16; } }
            public bool IsAtMax { get { return false; } }
            public bool SpawnOne(string typeId) { LastSpawn = typeId; return true; }
            public bool RemoveOne(string typeId) { LastRemove = typeId; return true; }
            public bool SetActiveType(string typeId) { LastSetActive = typeId; return true; }
            public bool InstallType(string typeId, byte[] animationsXml, out string error) { error = null; return true; }
            public bool UninstallType(string typeId, out string error) { error = null; return true; }
            public int GetSizeLevel(string typeId) { return 0; }
            public bool SetSizeLevel(string typeId, int level) { return true; }
            public bool GetSoundEnabled(string typeId) { return true; }
            public bool SetSoundEnabled(string typeId, bool enabled) { return true; }
        }

        private sealed class RecordingHost : IHost
        {
            private readonly IPetManager _pm;
            public RecordingHost(IPetManager pm) { _pm = pm; }
            public readonly List<OptionsPane> OptionsPanes = new List<OptionsPane>();
            public readonly List<TrayItem> TrayItems = new List<TrayItem>();

            public string HostVersion { get { return "selftest"; } }
            public bool SpeechEnabled { get { return true; } }
            public double Volume { get { return 0.5; } }
            public string OwnerName { get { return ""; } }
            public void SetOwnerName(string name) { }
            public IPetManager GetPetManager() { return _pm; }

            public event Action<IPet> PetSpawned;
            public event Action<PokeInfo> PetPoked;
            public event Action<IPet> PetLanded;
            public event Action<IdleContext> PetIdle;
            public event Action<AnimationInfo> AnimationStarted;
            public event Action HostShutdown;
            internal void TouchEvents() { PetSpawned?.Invoke(null); PetPoked?.Invoke(null); PetLanded?.Invoke(null); PetIdle?.Invoke(null); AnimationStarted?.Invoke(null); HostShutdown?.Invoke(); }

            public void Say(IPet pet, string text) { }
            public void SayAll(string text) { }
            public bool TryPlayAnimation(IPet pet, string animationName) { return true; }
            public void PlayAnimationAll(IReadOnlyList<string> animationCandidates) { }
            public ScreenContext CaptureScreenContext(IPet pet) { return new ScreenContext { WindowTitle = "", ProcessName = "", MonitorBounds = new PixelRect(0, 0, 1920, 1080) }; }
            public IDisposable RegisterHotkey(string combo, Action onPressed) { return new NoopDisposable(); }
            public IModuleStorage GetStorage(string moduleId) { return new MemStorage(); }
            public IModuleSettings GetSettings(string moduleId) { return new MemSettings(); }
            public IModuleSettings GetSettings(string moduleId, string petTypeId) { return GetSettings(moduleId); }
            public IDisposable RegisterDropResponder(int priority, Func<bool> onDrop) { return new NoopDisposable(); }
            public IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke) { return new NoopDisposable(); }
            public System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind) { return System.Threading.Tasks.Task.FromResult((IReadOnlyList<CatalogItem>)new List<CatalogItem>()); }
            public System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id) { return System.Threading.Tasks.Task.FromResult(new byte[0]); }
            public IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions) { return new List<string>(); }
            public bool OpenLink(string moduleId, string httpsUrl) { return true; }
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
