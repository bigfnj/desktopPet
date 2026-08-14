# S6 Phase 2 — Pets becomes a module (with per-pet personality/voice)

> Plan doc. Status: **proposed, not started.** Companion to `BACKLOG.md` (backlog item S6p2 + #16)
> and `handoff.md`. Written 2026-08-13.

## Goal

Finish the plugin re-architecture: turn **Pets** — the last big capability still baked into the host —
into a module, exactly like Fortunes and AiBrain, with one difference: **Pets is pre-installed by default**
(the app is pointless with zero pets, so it ships enabled rather than being opt-in from the catalog).

While doing it, build in what backlog **#16 (per-pet personality/voice)** needs from day one, because
retrofitting per-pet config onto a global-only design later is the painful path the #16 note calls out.
The end state: each on-screen pet **type** can run its own voice — one sheep is AiBrain on "Wednesday
Addams", another is Fortunes leaning on dad-joke packs, a third is AiBrain on "Jules Winnfield".

Non-goal: rewriting the physics/animation engine. `FormPet`/`Animations`/`FormSpeech` stay host-owned
WinForms; the module orchestrates *which* pets exist and *what voice* each has, through the ABI.

## Why this is bigger than the Fortunes/AiBrain extractions

Fortunes and AiBrain were **observers**: they subscribe to `PetSpawned`/`PetPoked`/`PetLanded`, capture
screen context, and speak. Every verb they needed already existed on `IHost` (`SayAll`,
`CaptureScreenContext`, `RegisterDropResponder`, …). Pets is different — it is an **orchestrator** that
today reaches straight into host internals a module cannot see:

- `StartUp` owns `FormPet[] sheeps` (cap `MAX_SHEEPS = 16`), a `PetTypeRegistry registry`, and a
  `Dictionary<FormPet, PetTypeRegistry.Entry> petEntries`.
- Spawning: `AddSheep()` / `AddSheep(string id)` → `AddSheepCore(xml, animations, entry)`; type loading
  via `ResolveExtraType(id)` → `PetCatalog` → `registry.Add(...)`.
- Removing: `RemoveOnePet(id)`; refcount released on `FormClosed` (`ExtraPet_FormClosed`).
- The persisted mix: settings **schema v2** `pets: [{id,count}]`, restored by `BuildStartupSpawnPlan()`,
  written by `OnScreenMix()` / `PersistMix()`; `GetAutoStartPets()`, `IsAtMaxPets`.
- UI/tray drive these **in-process today**: `src/Portable/Wpf/PetsPaneControl.cs` (the WPF Pets pane) and
  the tray "Add a pet ▸ / Remove a pet ▸" submenus in `ContextMenus.cs` both call `StartUp`/`PetCatalog`
  directly.

A real module lives in its own `AssemblyLoadContext` and references only `DesktopPet.Contracts` — it can
touch none of `FormPet`, `PetTypeRegistry`, `PetCatalog`, or `StartUp`. So S6p2 is mostly **ABI design**:
define the pet-management verbs, keep the engine (StartUp) as their implementation, and move the
policy/UI/catalog-glue into the module.

## New ABI surface

Add a pet-management facet to `IHost` (or a sub-interface `IPetManager` returned by
`IHost.GetPetManager()` — preferred, so the pet verbs don't bloat the core surface every module sees).
All verbs run on the UI thread, like the rest of the host services.

```
interface IPetManager
{
    // enumerate installed pet TYPES (id + display name + built-in flag) — replaces PetCatalog for the module
    IReadOnlyList<PetTypeInfo> InstalledTypes();
    // the on-screen mix right now (id -> count), and the persisted autostart mix
    IReadOnlyList<PetCount> OnScreenMix();
    IReadOnlyList<PetCount> AutostartMix();

    bool SpawnOne(string typeId);        // add one pet of a type ("" = active/default); false if at MAX
    bool RemoveOne(string typeId);       // remove one on-screen pet of a type
    bool SetActiveType(string typeId);   // "Use this pet" = replace-all with this type
    void SetAutostartMix(IReadOnlyList<PetCount> mix);   // persist the launch mix

    int MaxPets { get; }
    bool IsAtMax { get; }

    // install/remove a downloaded pet type (validated XML + sprite sheet) into the pet store
    bool InstallType(string typeId, byte[] petZipOrXml, out string error);
    bool UninstallType(string typeId);
}
```

Enrich `IPet` so a voice module can key config on the pet's **type**, and so poke/land/idle events can be
attributed to a specific pet:

```
interface IPet
{
    int Id { get; }
    bool IsBusy { get; }
    string TypeId { get; }        // NEW: which pet type this instance is (e.g. "pink_sheep")
    string DisplayName { get; }   // NEW: character name for UI ("Pearl")
}
```

`CatalogKinds` gains `Pet` (today it only has `Pack`), so the module fetches pet types through the same
`FetchCatalogItemsAsync` / `DownloadCatalogItemAsync` path Fortunes uses for packs — the host keeps
owning the HTTPS + SHA-256 verification.

`StartUp` already exposes most of the implementation (`AddSheep`, `RemoveOnePet`, `OnScreenMix`,
`PersistMix`, `BuildStartupSpawnPlan`); the work is wrapping them behind `IPetManager` on `PetHost` and
making `PetHandle` carry `TypeId`/`DisplayName` (it already wraps `FormPet` via `ConditionalWeakTable`,
and `petEntries` already knows each pet's type).

## Per-pet personality/voice (folding in #16)

This is the reason to do #16 *now*: the storage key and the event-routing must be pet-type-aware from the
first cut, even if the initial UI is still global.

1. **Config keyed by pet type, not global.** Today `AiSettings` (one `Disposition`) and `FortuneSettings`
   are single documents. Introduce a per-type override layer: `GetSettings(moduleId)` gains a scoped
   variant `GetSettings(moduleId, petTypeId)` (host-owned store, falls back to the global doc when a type
   has no override). AiBrain reads the disposition/model/provider for the type of the pet a remark is
   *for*; Fortunes reads the pack/genre selection for that type. No override set ⇒ identical to today.
2. **Attribute each event to its pet.** `PetPoked`/`PetLanded`/`PetIdle` already carry an `IPet`; with
   `IPet.TypeId` added, a voice module resolves "which config" with no new plumbing. Verify the drop
   responder path passes the specific pet (today `SpeakFortune` uses `_lastPet` — good enough, but the
   drop tick should name the pet it fired for so multi-pet drops attribute correctly).
3. **"Trigger Speech" must be pet-aware in its storage from the start.** The existing poke-responder
   arbitration (`RegisterPokeResponder(moduleId, …)` + the user's "Trigger Speech" pick) is global today.
   Store the preference as `perType[typeId] -> moduleId` with a global default, even if the first UI only
   edits the default — so a later per-pet UI is a view change, not a migration.
4. **UI, phased.** First cut: the Pets pane lists on-screen types and, per type, a "Voice" dropdown
   (which speech module) + a link into that module's per-type settings. This is additive to the module's
   own global pane.

## Phasing (each sub-stream gated: build -Release 0-warn + CoreTests + all self-tests + churn soak)

- **P2a — ABI + host bridge.** Add `IPetManager`, `IPet.TypeId/DisplayName`, `CatalogKinds.Pet`. Implement
  on `PetHost`/`PetHandle` over the existing `StartUp` verbs. No behavior change; a recording self-test
  (`--petmanager-selftest`) drives spawn/remove/mix/active through the ABI against a fake engine.
- **P2b — Pets module (pre-installed).** New `modules/Pets` (id `pets`) that contributes the Pets options
  pane + the tray Add/Remove submenus **through the ABI**, replacing `PetsPaneControl`/the direct tray
  calls. Ships in the base install (a pre-seeded module dir + a catalog entry marked default) rather than
  catalog-optional. The host still spawns the persisted mix at launch; the module owns the UI/policy.
- **P2c — retire the host-side Pets UI.** Delete `PetsPaneControl` + the in-host tray pet items once the
  module covers them (mirrors how S3d left the fortune UI/engine for S5). `OptionsShell.CollectPanes`
  already alphabetizes non-fixed panes, so the module's pane lands where Pets is today.
- **P2d — per-pet voice (the #16 payload).** Add `GetSettings(moduleId, petTypeId)` scoping; make AiBrain
  + Fortunes read per-type config; add the per-type "Voice" UI. Settings migrate forward (global doc
  becomes the default; no per-type overrides until the user sets one).

## Migration & packaging

- **Pre-installed default:** unlike Fortunes/AiBrain (catalog-optional), Pets ships in the MSI/ZIP as a
  seeded `modules/pets/` + a catalog `modules.json` entry flagged pre-installed, so a fresh box has pets
  with no catalog round-trip. Reuse `New-ModuleDistZip.ps1` + `New-ContentCatalog.ps1`; remember the
  publish-freshness gate (`Test-ModulePublishFreshness.ps1`) — **committing `modules/pets/` means a
  republish commit** (rebuild → zip → commit zip → regenerate catalog), same as every module change.
- **Settings:** the `pets: [{id,count}]` schema v2 doc stays as-is (the host still persists the mix); the
  per-type voice overrides are a NEW additive store, so no schema bump to the existing pet mix.
- **`MAX_SHEEPS = 16`** stays a host constant surfaced via `IPetManager.MaxPets`.

## Risks / open decisions

- **`IPetManager` on `IHost` vs a sub-interface.** Prefer the sub-interface (`GetPetManager()`), so the 8
  pet verbs don't appear on the surface every trivial module sees. Decide before P2a.
- **Pre-installed-but-a-module** is a new install shape (not catalog-optional, not host-baked). Confirm the
  bare-host story still reads cleanly ("the host is a pet engine; Pets is the one module that ships on").
- **Per-type config explosion.** Keep overrides sparse (only stored when set); never fan a global doc out
  into N per-type copies. The scoped `GetSettings` must fall through, not duplicate.
- **Uninstalling the Pets module** would leave a pet engine with no pet UI. Either forbid uninstalling a
  pre-installed module, or degrade to "reinstall from catalog" — decide in P2b.
- **Drop/idle attribution.** Confirm the drop-responder tick can name the specific pet it fired for before
  P2d relies on it for per-pet voice; today Fortunes leans on `_lastPet`.
