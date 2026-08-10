using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopPet.Ai;

namespace DesktopPet.Options
{
    // =====================================================================================
    // Renderer-agnostic controller layer ("the seam") for the whole Options panel. NOTHING here
    // references System.Windows.Forms: a WinForms/Krypton view binds controls to the State DTOs and
    // calls the command methods; a WebView2 view JSON-serializes the same DTOs to an HTML page and
    // posts commands back. All validation/clamping lives here so every renderer behaves identically.
    // The whole layer is `internal` because the domain services it wraps (AiSettings, FortuneProvider,
    // RemoteCatalog, ...) are internal; it compiles into the DesktopPet exe. DTO *members* stay public
    // so Newtonsoft can serialize them for the WebView2 bridge.
    // =====================================================================================

    // ---- shared result (mirrors the existing Set*(...) -> bool + rollback pattern) ----
    internal class OpResult
    {
        public bool Ok;
        public string Message;
        public static OpResult Success(string m = null) { return new OpResult { Ok = true, Message = m }; }
        public static OpResult Fail(string m) { return new OpResult { Ok = false, Message = m }; }
    }
    internal sealed class OpResult<T> : OpResult
    {
        public T Value;                       // the PERSISTED (possibly clamped) value; the view re-syncs to this
        public static OpResult<T> Ok2(T v, string m = null) { return new OpResult<T> { Ok = true, Value = v, Message = m }; }
        public static new OpResult<T> Fail(string m) { return new OpResult<T> { Ok = false, Message = m }; }
    }

    // Seam over StartUp/Program.Mainthread so controllers don't bind the WinForms singleton and are
    // fakeable in tests. StartUp implements this (its methods already exist).
    internal interface IPetRuntime
    {
        string ActivePetXml { get; }
        bool IsAtMaxPets { get; }
        bool LoadNewXMLFromString(string xml);              // replace-all ("Use this pet")
        bool AddPetFromTray(string id);                     // add-alongside
        bool RemoveOnePet(string id);
        string SmartFortunesStatus();
        void RebuildSmartFortunes();
        void ReloadAiSettings();
    }

    // Seam over RemoteCatalogClient + download/install. Async results arrive via callbacks so any
    // renderer can surface progress. (Phase-1 skeleton; wired against RemoteCatalogClient in Phase 3.)
    internal interface ICatalogService
    {
        void FetchAsync(Action<OpResult> onDone);
        void DownloadPacksAsync(IEnumerable<string> packIds, Action<OpResult> onDone);
        void DownloadPetAsync(string petId, Action<OpResult> onDone);
    }

    // =============================== FAÇADE ===============================
    internal sealed class OptionsController
    {
        public PreferencesController Preferences { get; private set; }
        public PetsController        Pets        { get; private set; }
        public FortunesController    Fortunes    { get; private set; }

        private readonly AiSettings _ai;
        internal const int SaveTimeoutMs = 250;   // matches FormOptions.UiSettingsSaveTimeoutMilliseconds

        public OptionsController(LocalData data, IPetRuntime runtime, ICatalogService catalog)
        {
            // AiSettings is still the shared backing store for the fortune tone/source fields + the
            // random-drop preferences; the AI-brain controller moved out with the AI-brain module (S4b).
            _ai = AiSettings.Load();
            Preferences = new PreferencesController(data, _ai);
            Pets        = new PetsController(runtime, catalog);
            Fortunes    = new FortunesController(_ai, runtime, catalog);
        }

        public void Load() { Preferences.Load(); Pets.Load(); Fortunes.Load(); }

        // Persist AiSettings-backed changes (Fortunes/RandomDrop). Preferences persist per-set via LocalData.
        public OpResult Commit() { return _ai.SaveWithin(SaveTimeoutMs) ? OpResult.Success() : OpResult.Fail("Settings could not be saved."); }
    }

    // =============================== PREFERENCES ===============================
    internal sealed class PreferencesState
    {
        public int  VolumeLevel;            // 0..10 (0 == muted)
        public bool WindowForeground;
        public bool StealTaskbarFocus;
        public int  AutoStartPets;          // 1..16
        public int  ScaleLevel;
        public bool MultiScreen;
        public bool SpeechEnabled;
        public int  SpeechDurationSeconds;  // 2..30
        public bool RandomDropEnabled;
        public int  RandomDropMinutes;
        public int  RandomDropJitterMinutes;
        public bool RunAtStartup;           // HKCU Run registration (OS-level)
    }

    internal sealed class PreferencesController
    {
        private readonly LocalData _data;
        private readonly AiSettings _ai;
        public PreferencesState State { get; private set; }
        public PreferencesController(LocalData data, AiSettings ai) { _data = data; _ai = ai; }

        public void Load()
        {
            State = new PreferencesState
            {
                VolumeLevel           = (int)Math.Round(_data.GetVolume() * 10.0),
                WindowForeground      = _data.GetWindowForeground(),
                StealTaskbarFocus     = _data.GetStealTaskbarFocus(),
                AutoStartPets         = _data.GetAutoStartPets(),
                ScaleLevel            = _data.GetScale(),
                MultiScreen           = _data.GetMultiscreen(),
                SpeechEnabled         = _data.GetSpeechEnabled(),
                SpeechDurationSeconds = _data.GetSpeechDuration(),
                RandomDropEnabled     = _ai.RandomDropEnabled,
                RandomDropMinutes     = _ai.RandomDropMinutes,
                RandomDropJitterMinutes = _ai.RandomDropJitterMinutes,
                RunAtStartup          = StartupRegistration.IsEnabled(),
            };
        }

        // OS-level per-user startup registration (HKCU Run). Re-reads to reflect the effective state.
        public OpResult SetRunAtStartup(bool on)
        {
            StartupRegistration.Set(on);
            State.RunAtStartup = StartupRegistration.IsEnabled();
            return State.RunAtStartup == on ? OpResult.Success()
                                            : OpResult.Fail("Couldn't update the startup setting.");
        }

        public OpResult<int> SetVolumeLevel(int level)
        {
            int clamped = Math.Max(0, Math.Min(10, level));
            if (!_data.SetVolume(clamped / 10.0)) return OpResult<int>.Fail("Couldn't save volume.");
            State.VolumeLevel = (int)Math.Round(_data.GetVolume() * 10.0);
            return OpResult<int>.Ok2(State.VolumeLevel);
        }
        public OpResult SetWindowForeground(bool v)  { return Persist(_data.SetWindowForeground(v), () => State.WindowForeground = _data.GetWindowForeground()); }
        public OpResult SetStealTaskbarFocus(bool v) { return Persist(_data.SetStealTaskbarFocus(v), () => State.StealTaskbarFocus = _data.GetStealTaskbarFocus()); }
        public OpResult SetAutoStartPets(int n)      { return Persist(_data.SetAutoStartPets(n), () => State.AutoStartPets = _data.GetAutoStartPets()); }
        public OpResult SetScaleLevel(int lvl)       { return Persist(_data.SetScale(lvl), () => State.ScaleLevel = _data.GetScale()); }
        public OpResult SetMultiScreen(bool v)       { return Persist(_data.SetMultiscreen(v), () => State.MultiScreen = _data.GetMultiscreen()); }
        public OpResult SetSpeechEnabled(bool v)     { return Persist(_data.SetSpeechEnabled(v), () => State.SpeechEnabled = _data.GetSpeechEnabled()); }
        public OpResult SetSpeechDuration(int s)     { return Persist(_data.SetSpeechDuration(s), () => State.SpeechDurationSeconds = _data.GetSpeechDuration()); }

        // RandomDrop is AiSettings-backed; persisted by OptionsController.Commit().
        public void SetRandomDrop(bool on, int minutes, int jitter)
        {
            _ai.RandomDropEnabled = on; _ai.RandomDropMinutes = minutes; _ai.RandomDropJitterMinutes = jitter;
            State.RandomDropEnabled = on; State.RandomDropMinutes = minutes; State.RandomDropJitterMinutes = jitter;
        }

        private static OpResult Persist(bool ok, Action resync)
        { if (ok) { resync(); return OpResult.Success(); } return OpResult.Fail("Setting could not be saved; reverted."); }
    }

    // =============================== PETS ===============================
    internal sealed class PetRow { public string Id; public string DisplayName; public bool IsBuiltIn; public bool IsActive; }
    internal sealed class PetsState { public List<PetRow> Installed = new List<PetRow>(); }

    internal sealed class PetsController
    {
        private readonly IPetRuntime _runtime;
        private readonly ICatalogService _catalog;
        public PetsState State { get; private set; }
        public event Action PetsChanged;

        public PetsController(IPetRuntime runtime, ICatalogService catalog) { _runtime = runtime; _catalog = catalog; }

        public void Load()
        {
            State = new PetsState();
            string activeXml = _runtime != null ? _runtime.ActivePetXml : null;
            foreach (PetCatalog.PetInfo p in PetCatalog.EnumerateLocal())
                State.Installed.Add(new PetRow { Id = p.Id, DisplayName = p.DisplayName, IsBuiltIn = p.IsBuiltIn, IsActive = IsActive(p, activeXml) });
        }

        public OpResult UsePet(string petId)
        {
            string xml, err;
            if (!PetCatalog.TryReadPetXml(petId, out xml, out err)) return OpResult.Fail(err);
            // Record which pet is now active so per-pet size/sound key by its real id (normalize handles ""/built-in).
            if (Program.MyData != null) Program.MyData.SetActivePetId(petId);
            bool ok = _runtime.LoadNewXMLFromString(xml);
            if (ok) { Load(); Raise(); }
            return ok ? OpResult.Success("Pet applied.") : OpResult.Fail("Couldn't apply pet.");
        }
        public OpResult AddPet(string petId)
        {
            bool ok = _runtime.AddPetFromTray(string.IsNullOrEmpty(petId) ? PetCatalog.BuiltInPetId : petId);
            if (ok) Raise();
            return ok ? OpResult.Success("Added.") : OpResult.Fail("Max pets reached or load failed.");
        }
        // Replace the active pet with the built-in default ("Restore default pet").
        public OpResult RestoreDefaultPet()
        {
            string xml, err;
            if (!PetCatalog.TryReadPetXml(PetCatalog.BuiltInPetId, out xml, out err)) return OpResult.Fail(err);
            if (Program.MyData != null) Program.MyData.SetActivePetId(PetCatalog.BuiltInPetId);
            bool ok = _runtime != null && _runtime.LoadNewXMLFromString(xml);
            if (ok) { Load(); Raise(); }
            return ok ? OpResult.Success("Default pet restored.") : OpResult.Fail("Couldn't restore the default pet.");
        }
        public void DownloadPet(string petId, Action<OpResult> onDone) { _catalog.DownloadPetAsync(petId, r => { if (r.Ok) { Load(); Raise(); } if (onDone != null) onDone(r); }); }

        private void Raise() { var h = PetsChanged; if (h != null) h(); }
        private static bool IsActive(PetCatalog.PetInfo p, string activeXml)
        {
            if (string.IsNullOrEmpty(activeXml)) return false;
            string xml, err;
            if (!PetCatalog.TryReadPetXml(p.IsBuiltIn ? PetCatalog.BuiltInPetId : p.Id, out xml, out err)) return false;
            return string.Equals(xml, activeXml, StringComparison.Ordinal);
        }
    }

    // =============================== FORTUNES (driver) ===============================
    internal enum SourceStatus { Active, Inactive }
    internal sealed class SourceRow { public string Id; public string Topic; public int Lines; public bool Custom; public bool HasSpicy; public bool Active; public SourceStatus Status; }
    internal sealed class GenreRow  { public string Id; public bool Enabled; }
    internal sealed class FortunesState
    {
        public bool SmartEnabled;
        public string SmartStatus;
        public bool SpicyEnabled;
        public string SpicyTier;                 // "edgy" | "nsfw"
        public bool SpicyOnly;
        public bool NoProfanity;
        public List<SourceRow> Sources = new List<SourceRow>();
        public List<GenreRow>  Genres  = new List<GenreRow>();
        public int  ActiveSources;
        public int  TotalSources;
        public int  ActiveLines;                 // matchable lines after all filters (FortuneProvider.Count)
    }

    internal sealed class FortunesController
    {
        private readonly AiSettings _ai;
        private readonly IPetRuntime _runtime;
        private readonly ICatalogService _catalog;
        public FortunesState State { get; private set; }
        public event Action<string> SmartStatusChanged;
        public event Action SourcesChanged;

        public FortunesController(AiSettings ai, IPetRuntime runtime, ICatalogService catalog) { _ai = ai; _runtime = runtime; _catalog = catalog; }

        public void Load()
        {
            State = new FortunesState
            {
                SmartEnabled = _ai.SmartFortunes,
                SmartStatus  = _runtime != null ? _runtime.SmartFortunesStatus() : "",
                SpicyEnabled = _ai.SpicyFortunes,
                SpicyTier    = string.IsNullOrEmpty(_ai.SpicyTier) ? "edgy" : _ai.SpicyTier,
                SpicyOnly    = _ai.SpicyOnly,
                NoProfanity  = _ai.NoProfanity,
            };
            RebuildRows();
        }

        // ---- settings (persisted at Apply/Commit; toggles re-derive rows so totals stay live) ----
        public void SetSmartEnabled(bool v) { _ai.SmartFortunes = v; State.SmartEnabled = v; }
        public void SetContentLevel(bool spicyEnabled, string tier, bool spicyOnly)
        {
            _ai.SpicyFortunes = spicyEnabled;
            _ai.SpicyTier = string.Equals(tier, "nsfw", StringComparison.OrdinalIgnoreCase) ? "nsfw" : "edgy";
            _ai.SpicyOnly = spicyOnly;
            State.SpicyEnabled = _ai.SpicyFortunes; State.SpicyTier = _ai.SpicyTier; State.SpicyOnly = _ai.SpicyOnly;
            RebuildRows(); RaiseSources();
        }
        public void SetNoProfanity(bool v) { _ai.NoProfanity = v; State.NoProfanity = v; RebuildRows(); RaiseSources(); }
        public void SetSourceActive(string id, bool active) { Toggle(_ai.DisabledSources, id, !active); RebuildRows(); RaiseSources(); }
        public void SetGenreEnabled(string id, bool enabled) { Toggle(_ai.DisabledGenres, id, !enabled); RebuildRows(); }
        public void SetAllSources(bool active)
        {
            if (_ai.DisabledSources == null) _ai.DisabledSources = new List<string>();
            _ai.DisabledSources.Clear();
            if (!active) foreach (var s in FortuneProvider.Sources()) _ai.DisabledSources.Add(s.Id);
            RebuildRows(); RaiseSources();
        }

        // ---- commands ----
        public OpResult Apply()
        {
            bool saved = _ai.SaveWithin(OptionsController.SaveTimeoutMs);
            if (_runtime != null) _runtime.ReloadAiSettings();
            RebuildRows(); RaiseSources();
            return saved ? OpResult.Success("Fortune mix updated (" + State.ActiveLines + " lines).") : OpResult.Fail("Couldn't save fortune settings.");
        }
        public void RebuildSmartWeights()
        {
            _ai.SaveWithin(OptionsController.SaveTimeoutMs);
            if (_runtime != null) _runtime.RebuildSmartFortunes();
            PollSmartStatus();
        }
        public void PollSmartStatus()
        {
            if (_runtime == null) return;
            string s = _runtime.SmartFortunesStatus();
            if (!string.Equals(s, State.SmartStatus, StringComparison.Ordinal)) { State.SmartStatus = s; var h = SmartStatusChanged; if (h != null) h(s); }
        }
        public void DownloadChecked(IEnumerable<string> packIds) { _catalog.DownloadPacksAsync(packIds, r => { Load(); RaiseSources(); }); }
        public void RefreshPackCatalog(Action<OpResult> onDone) { _catalog.FetchAsync(r => { if (onDone != null) onDone(r); }); }
        public string CustomFortunesFolder() { return FortuneProvider.CustomDir; }

        private void RebuildRows()
        {
            var disabledSources = new HashSet<string>(_ai.DisabledSources ?? new List<string>(), StringComparer.Ordinal);
            var disabledGenres  = new HashSet<string>(_ai.DisabledGenres  ?? new List<string>(), StringComparer.Ordinal);
            State.Sources.Clear();
            State.TotalSources = 0; State.ActiveSources = 0;
            foreach (SourceStat s in FortuneProvider.Sources())
            {
                bool active = !disabledSources.Contains(s.Id);
                State.Sources.Add(new SourceRow { Id = s.Id, Topic = s.Topic, Lines = s.Count, Custom = s.Custom, HasSpicy = s.HasSpicy, Active = active, Status = active ? SourceStatus.Active : SourceStatus.Inactive });
                State.TotalSources++; if (active) State.ActiveSources++;
            }
            State.Genres.Clear();
            foreach (GenreStat g in FortuneProvider.Genres())
                State.Genres.Add(new GenreRow { Id = g.Id, Enabled = !disabledGenres.Contains(g.Id) });
            // Authoritative matchable count after ALL filters (sources + genres + level + profanity).
            try { State.ActiveLines = new FortuneProvider(_ai).Count; } catch { State.ActiveLines = 0; }
        }
        private void RaiseSources() { var h = SourcesChanged; if (h != null) h(); }
        private static void Toggle(List<string> set, string id, bool present)
        { if (set == null) return; if (present) { if (!set.Contains(id)) set.Add(id); } else set.Remove(id); }
    }

    // The AI-brain controller (AiState / AiController) moved out with the AI-brain module (S4b): the module
    // owns its own DPAPI-scoped settings + provider/endpoint/key handling. Its config UI is rebuilt from the
    // module's contributions in the WPF shell (S5).

    // Drives the whole controller seam with fakes against an isolated data root (the invoker must set
    // DESKTOPPET_DATA_ROOT so it never clobbers real settings). Asserts: Preferences clamping, Fortunes
    // source/row round-trip + totals, and that a set API key never surfaces in the serialized AiState.
    // Invoked by the --options-selftest runtime flag.
    internal static class OptionsSelfTest
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
        private sealed class FakeCatalog : ICatalogService
        {
            public void FetchAsync(Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
            public void DownloadPacksAsync(IEnumerable<string> ids, Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
            public void DownloadPetAsync(string id, Action<OpResult> onDone) { if (onDone != null) onDone(OpResult.Success()); }
        }

        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                // Safety: must run against an isolated data root, not the real user settings.
                string root = Environment.GetEnvironmentVariable("DESKTOPPET_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(root))
                {
                    sb.AppendLine("FAIL: DESKTOPPET_DATA_ROOT must be set (isolated root) to run --options-selftest");
                    return Finish(sb, false);
                }

                var data = new LocalData();
                var ctl = new OptionsController(data, new FakeRuntime(), new FakeCatalog());
                ctl.Load();

                // ---- Preferences clamping ----
                OpResult<int> vol = ctl.Preferences.SetVolumeLevel(99);
                ok &= Check(sb, "volume clamps to 10", vol.Ok && vol.Value == 10);
                ctl.Preferences.SetAutoStartPets(999);
                ok &= Check(sb, "autostart clamps to <=16", ctl.Preferences.State.AutoStartPets <= 16 && ctl.Preferences.State.AutoStartPets >= 1);
                ctl.Preferences.SetSpeechDuration(1);
                ok &= Check(sb, "speech duration floors to >=2", ctl.Preferences.State.SpeechDurationSeconds >= 2);

                // ---- Preferences run-at-startup: redirect to a throwaway key so the real HKCU Run entry is never touched ----
                Environment.SetEnvironmentVariable("DESKTOPPET_STARTUP_TEST_KEY", @"Software\DesktopPetSelfTest\Run");
                try
                {
                    ctl.Preferences.SetRunAtStartup(true);
                    ok &= Check(sb, "run-at-startup enables", ctl.Preferences.State.RunAtStartup);
                    ctl.Preferences.SetRunAtStartup(false);
                    ok &= Check(sb, "run-at-startup disables", !ctl.Preferences.State.RunAtStartup);
                }
                finally
                {
                    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DesktopPetSelfTest", false); } catch { }
                    Environment.SetEnvironmentVariable("DESKTOPPET_STARTUP_TEST_KEY", null);
                }

                // ---- Fortunes: enumeration + toggle round-trip + totals + apply ----
                int total = ctl.Fortunes.State.TotalSources;
                ok &= Check(sb, "sources enumerated", total > 0);
                ok &= Check(sb, "genres enumerated", ctl.Fortunes.State.Genres.Count > 0);
                ok &= Check(sb, "active-lines computed", ctl.Fortunes.State.ActiveLines > 0);
                if (total > 0)
                {
                    string id = ctl.Fortunes.State.Sources[0].Id;
                    ctl.Fortunes.SetSourceActive(id, false);
                    SourceRow row = ctl.Fortunes.State.Sources.Find(r => r.Id == id);
                    ok &= Check(sb, "source deactivates + status Inactive", row != null && !row.Active && row.Status == SourceStatus.Inactive);
                    ok &= Check(sb, "active-source count drops by one", ctl.Fortunes.State.ActiveSources == total - 1);
                    ok &= Check(sb, "disabled list carries the id", (ctl_ai(ctl).DisabledSources ?? new List<string>()).Contains(id));
                    ctl.Fortunes.SetSourceActive(id, true);
                    ok &= Check(sb, "source reactivates", ctl.Fortunes.State.ActiveSources == total);
                }
                ctl.Fortunes.SetContentLevel(true, "nsfw", true);
                ok &= Check(sb, "content level applied", ctl.Fortunes.State.SpicyEnabled && ctl.Fortunes.State.SpicyTier == "nsfw" && ctl.Fortunes.State.SpicyOnly);
                ok &= Check(sb, "fortunes apply persists", ctl.Fortunes.Apply().Ok);

                // ---- Pets: restore default (built-in) via the fake runtime ----
                ok &= Check(sb, "restore default pet", ctl.Pets.RestoreDefaultPet().Ok);
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }
            return Finish(sb, ok);
        }

        // Small reflection-free reach into the shared AiSettings the Fortunes controller mutates, to
        // assert the disabled-source round-trip actually lands in the persisted model.
        private static AiSettings ctl_ai(OptionsController ctl)
        {
            var f = typeof(OptionsController).GetField("_ai", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (AiSettings)f.GetValue(ctl);
        }

        private static bool Check(StringBuilder sb, string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); return cond; }
        private static bool Finish(StringBuilder sb, bool ok)
        {
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-options-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }
    }
}
