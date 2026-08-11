using System;
using System.Collections.Generic;

namespace DesktopPet.Options
{
    // =====================================================================================
    // Renderer-agnostic controller layer ("the seam") for the Pets pane. NOTHING here references
    // System.Windows.Forms: a WPF view binds to the State DTOs and calls the command methods. All
    // validation/clamping lives here so every renderer behaves identically. The layer is `internal`
    // because the domain services it wraps are internal; it compiles into the DesktopPet exe.
    //
    // The Preferences/Fortunes/AI controllers + the OptionsController façade + OptionsSelfTest were
    // removed with the residual base fortune/AI-brain engines; only the live PetsController remains
    // (used by the WPF Pets pane). Its shared result/runtime/catalog seam types stay alongside it.
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
}
