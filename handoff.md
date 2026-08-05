# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-04**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index.
> Backlog lives in **[`BACKLOG.md`](BACKLOG.md)**.

---

## Where things stand (2026-08-04)

- **v1.0.6 is the current release** (tag `v1.0.6`). A `vX.Y.Z` tag push runs `release.yml` → publishes the
  unsigned portable ZIP + MSI + `SHA256SUMS`. `master` is at the release. The box here runs the installed
  v1.0.6 (per-user MSI at `%LOCALAPPDATA%\Programs\DesktopPet AI Edition\`).
- **v1.0.2 → v1.0.6 all landed 2026-08-04 in one session.** v1.0.3 was tagged/pushed mid-session; v1.0.4–
  v1.0.6 were bundled and pushed at session end. Everything in the next section shipped across v1.0.3–v1.0.6.
- Releasing is lean: bump `ProductVersion.props`, push a `vX.Y.Z` tag → `release.yml` builds + publishes.
  The old enterprise gate (SBOM/signing/rights/reproducible-build/TOCTOU-staging, ~50 never-green scripts)
  was deleted; the app's own defensive self-tests were kept. See `docs/RELEASE-CHECKLIST.md`.
- Build: `build.ps1 -Release -Zip` → `dist/DesktopPet-Portable.zip`; `installer/build-installer.ps1
  -Config Release` → MSI (WiX 5.0.2). CI (`build.yml`) = build + CoreTests + five app `--*-selftest`s +
  runtime-hardening + MSI.

## What shipped, v1.0.3 → v1.0.6 (all 2026-08-04)

- **v1.0.3 — fortune variety + speech-bubble hardening.** The recurring "same few fortunes" turned out to
  be a **stale install**: the box was running v1.0.1, which predates the `af90b82` fortune fix (check the
  *running binary's* FileVersion before believing "we already fixed this"). Widened `SmartFortunes`
  further (`TopK` 24→32, recent-avoidance 16→24; `--smart-selftest` stable-context distinct now 30/40).
  Hardened `FormSpeech`: measure text at the **target monitor's DPI** (was a fixed 96-DPI bitmap rendered
  on a PerMonitorV2 window — wrong above 100% scaling), **shrink-to-fit width** (was a fixed 220px column),
  `AutoScaleMode.None`, and trim display text. (Committed `43d2e00`, tagged `v1.0.3`, pushed.)
- **v1.0.4 — pet character names.** The Pets gallery shows each pet's `<petname>` (Ben/Gus/Omar/Pearl/
  Patsu/Rick/Yogurt) instead of a colour-derived folder title. One `DisplayPetName(folder, catalogName)`
  helper + a small `PetCharacterNames` map drive **both** the local "your pets" list and the online
  download grid; falls back to the catalog name, then a title-cased folder id.
- **v1.0.5 — active-pet badge.** The running pet's card shows a disabled **"✓ Active"** badge instead of
  its apply button. `IsActivePet` matches the item's XML to `Program.MyData.GetXml()` (the persisted active
  pet); the gallery rebuilds after `ApplyPet` so the badge follows a switch.
- **v1.0.6 — Mimiko download fix.** `Pets/mimiko/animations.xml` carries a UTF-8 BOM. The download path
  decodes bytes with `UTF8.GetString`, which **keeps** the leading `U+FEFF` (unlike `File.ReadAllText`),
  so `XmlSerializer` threw *"There is an error in XML document (1, 1)"*. Fix: strip a leading `\uFEFF` in
  `PetXmlValidator.TryParse` before the `StringReader` — hardens **any** BOM'd pet/user file. Verified
  before/after on the real file.
- **Docs.** README gained a **"Meet the pets"** section (character-name lineup + a per-pet easter-egg
  table; the colored sheep's `blastoff` rocket, alien abduction, king mode, etc.). BACKLOG gained #7–#9.

## Next up (from `BACKLOG.md`, unscoped)

- **#7 — Multiple _different_ pets at once** (Pearl + Rick together, not N copies of one). Phased plan is in
  the backlog; **Phase 1** (runtime "Add", no persistence) is small because `FormPet` already takes its
  `Animations`/`Xml` **per instance** — the engine is pet-type-agnostic; only `AddSheep` + the single global
  `animations`/`xml` + single-XML persistence assume one type.
- **#8 — Two-column local pet list** — the "your pets" list is a single TopDown column; reuse the catalog
  grid's `LeftToRight`+`WrapContents` pattern.
- **#9 — Fortunes tab: complete overhaul** — user-flagged; needs a scoping pass (specifics TBD) before build.
- Older: AI-voice **model-capability validation** (query Ollama `/api/show` to filter the Vision dropdown);
  **Shimeji → animations.xml converter** (big; unlocks the Shimeji skin library); UI Tier 2/3.

## Durable gotchas

- **Installed process = `DesktopPet`** (dev build = `eSheep`). Kill with
  `Get-Process -Name eSheep,DesktopPet -ErrorAction SilentlyContinue | Stop-Process -Force` (never
  `-ErrorAction Stop` — it throws on the missing name, skips the kill, leaves the exe locked → MSB3027).
- **Tray UI compiles from `src/Portable/*`** (FormOptions/AboutBox/FormHelp); the engine from
  `src/dotNet/*`. New `.cs` files must be added to `src/DesktopPet_Portable.csproj` (`<Compile Include>`).
- **The active pet is persisted as its raw `animations.xml`** (`LocalData.GetXml`/`TrySetPetAssets`), not an
  id — that's how `IsActivePet` identifies the running pet. Downloaded pets read via `UTF8.GetString`, so a
  BOM survives (see the v1.0.6 fix).
- **WiX is provisioned by `packaging/Install-LockedWixToolchain.ps1`** (locked 5.0.2); don't replace it
  with an ad hoc `dotnet tool install`.
- Root `global.json` pins .NET SDK **10.0.201** (10.0.302 misreports the net48 TFM in `dotnet list package`).
- `TreatWarningsAsErrors=true` — a build failure often just means a newly-orphaned member; the compiler
  points right at it.
