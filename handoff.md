# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-04**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index.
> Backlog lives in **[`BACKLOG.md`](BACKLOG.md)**.

---

## Where things stand (2026-08-04)

- **v1.0.2 is the current published GitHub release** (tag `v1.0.2`, commit `bc70418`, 2026-08-04 —
  portable ZIP + MSI + `SHA256SUMS`; all three release/build/publish workflows green). `master` is at
  the release plus post-release doc tweaks; everything in the next section shipped in v1.0.2.
- Releasing is lean: bump `ProductVersion.props`, push a `vX.Y.Z` tag → `release.yml` builds + publishes
  the unsigned portable ZIP + MSI + `SHA256SUMS`. The old enterprise gate (SBOM/signing/rights/
  reproducible-build/TOCTOU-staging, ~50 never-green scripts) was deleted; the app's own defensive
  self-tests were kept. See `docs/RELEASE-CHECKLIST.md`.
- Build: `build.ps1 -Release -Zip` → `dist/DesktopPet-Portable.zip`; `installer/build-installer.ps1
  -Config Release` → MSI (WiX 5.0.2). CI (`build.yml`) = build + CoreTests + five app `--*-selftest`s +
  runtime-hardening + MSI.

## What shipped in v1.0.2

One commit per item; all verified against a warning-clean build + the full self-test/CoreTests/
runtime-hardening suite.

- **Fortune repetition fix** — `SmartFortunes.Pick` now uses a 24-wide candidate set with
  recent-avoidance (no repeating the last 16), and `SayFortune` draws from the whole ~10k-line library
  ~1/3 of the time. Regression asserted in `--smart-selftest` (24 distinct picks / 40 on a stable window).
- **Dark theme finished** — `DarkNumericUpDown` (answers `WM_CTLCOLOREDIT` with a dark brush) and
  `DarkTabControl` (fills the strip on `WM_ERASEBKGND`) kill the last white-on-dark areas; combos/trees/
  lists/edits/scrollbars use `SetWindowTheme`. Follows the Windows light/dark setting. (Fixed a crash
  from an earlier `SetWindowTheme(" "," ")` theme-strip attempt — never do that.)
- **Pets gallery** — **Options → Pets → Get more pets** is a 4-across grid of thumbnail tiles. Icons for
  not-yet-downloaded pets ship embedded as `src/Resources/pet-thumbnails.zip` (loaded by id via
  `PetThumbnails`); downloaded pets without an `icon.png` fall back to the same bundled preview. Local
  cards use a fixed name-column so the buttons align.
- **Codebase optimization audit (~4,870 lines removed)** — dead methods/overloads, 2 orphaned source
  files, 4 unused framework refs, 2 dead packaging scripts, two write-only `StartUp` clusters, and the
  big one: the ~2,530-line embedded C# `FinalPathResolver` in `packaging/StagingPathSafety.ps1` collapsed
  to plain PowerShell (**4235 → 645 lines**, all function contracts preserved via thin `IDisposable`
  handle classes), plus the dead MutationTestHook (~42 sites) and unused build params. Release pipeline
  re-validated end-to-end: deterministic ZIP (byte-identical across two builds) + MSI + ICE + self-tests.
  Deliberately **kept**: `#if !PORTABLE` dual-build branches, `src/legacy/` quarantine, the `OllamaClient`
  8-arg test-seam ctor, `AppPaths.CatalogCacheDirectory`.

## Next up (from `BACKLOG.md`, unscoped)

- **AI-voice bundle** — persona-preset + speech-style dropdowns already exist in the AI tab; what's left
  is **model-capability validation** (query Ollama `/api/show` to filter the Vision dropdown).
- **Shimeji → animations.xml converter** (big; unlocks the Shimeji skin library).
- **UI modernization Tier 2/3** (Krypton Toolkit / WebView2) — optional.

## Durable gotchas

- **Installed process = `DesktopPet`** (dev build = `eSheep`). Kill with
  `Get-Process -Name eSheep,DesktopPet -ErrorAction SilentlyContinue | Stop-Process -Force` (never
  `-ErrorAction Stop` — it throws on the missing name, skips the kill, leaves the exe locked → MSB3027).
- **Tray UI compiles from `src/Portable/*`** (FormOptions/AboutBox/FormHelp); the engine from
  `src/dotNet/*`. New `.cs` files must be added to `src/DesktopPet_Portable.csproj` (`<Compile Include>`).
- **WiX is provisioned by `packaging/Install-LockedWixToolchain.ps1`** (locked 5.0.2); don't replace it
  with an ad hoc `dotnet tool install`.
- Root `global.json` pins .NET SDK **10.0.201** (10.0.302 misreports the net48 TFM in `dotnet list package`).
- `TreatWarningsAsErrors=true` — a build failure often just means a newly-orphaned member; the compiler
  points right at it.
