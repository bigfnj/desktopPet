# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-06**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index (has the fine detail).
> Feature backlog: **[`BACKLOG.md`](BACKLOG.md)**.

---

## Big picture (2026-08-06)

Two things are in flight, both **unreleased** (the last public release is still the v1.0.x line; the box
here runs an installed **v1.1.0 dev** build):

1. **`.NET 4.8 → .NET 10 (LTS) migration` — DONE + on `master`.** The app is `net10.0-windows`, SDK-style,
   framework-dependent (needs the .NET 10 Desktop runtime). Version bumped to **1.1.0**. Behavior parity.
2. **Plugin re-architecture — IN PROGRESS.** Turning the monolith into a lean **plugin host**: the base is
   a pet engine + skin downloader, and every capability (sound, fortunes, AI brain) becomes a **module**
   (a separate DLL loaded in its own collectible `AssemblyLoadContext`) that subscribes to lifecycle events
   and contributes UI. Planned as streams **S1–S7**. Modules are **NOT in the installer yet** (that's S6) —
   they build into the runtime `modules\<id>\` folder for local runs + self-tests only.

### Re-architecture status
- **S1 — plugin host foundation (MERGED, PR #2):** `DesktopPet.Contracts` ABI (`IModule`/`IHost`/`IPet` +
  lifecycle events + host services + declarative options schema + tray contributions); the `ModuleHost`
  loader (per-module collectible ALC, shares the single `DesktopPet.Contracts` from the default context so
  types unify); the live `PetHost` bridge (StartUp raises spawn/poke/land/shutdown at the real hook points).
- **S2 — Sound module (MERGED, PR #3):** NAudio left the base entirely (csproj + payload manifest + lock).
  The base parses `<sound>`, carries the raw MP3 bytes, and raises `AnimationStarted` with them; the
  `modules/Sound` plugin decodes + plays via NAudio **in its own load context**. `--sound-selftest`.
- **S3 part 1 — Fortunes module boundary + welcome starter (MERGED, PR #4):** `modules/Fortunes` (id
  `fortunes`). On the first pet spawn it speaks a **personalized welcome** — a sheep-themed line with the
  **Windows username** (`Environment.UserName`) filled into a `{name}` slot; the 116-line `welcome.json` is
  adapted from ai-platform's DeskPet welcome quips. `--fortunes-selftest`.
- **S3 part 2 — the fortune ENGINE relocation (S3c) — DONE (dumb + smart + native-ONNX-in-ALC), dormant.**
  The whole engine (`FortuneProvider` / `FortuneFileImporter` / `SmartFortunes` / `Embedder`) now lives in
  `modules/Fortunes/engine/`, rebound to the ABI and verified by `--fortunes-engine-selftest` (incl. ONNX
  loading + embedding inside the module's own `AssemblyLoadContext`). It's **dormant** — the base still owns
  fortunes at runtime — so there's zero regression. **S3d (flip the base over) is NEXT.** See below.
- **S4–S7 pending:** S4 extract the AI-brain module; S5 a WPF module-manager shell + schema panes +
  tray-from-contributions (retire FormOptions/FortunesWebView, drop WebView2, Newtonsoft→System.Text.Json);
  S6 strip to a bare host + package first-party modules into the installer + data migration + 2.0.0;
  S7 signed module catalog + Authenticode + consent.

### Locked design decisions
- **Fortunes module ships the ENGINE, not the content.** Both dumb (random) + smart (ONNX/bge-small) live
  in the module; it bundles **no fortune packs**. A fresh module is silent except the personalized welcome
  until the user adds a pack. The ~486KB `fortunes.txt` becomes the importable/downloadable "starter pack"
  (S7 catalog). The bge-small ONNX model is *engine* and travels with the module (like NAudio for Sound).
- **Deployment:** framework-dependent (.NET 10 runtime prompt). **Ecosystem:** open third-party modules,
  gated by code-signing + consent (S7). **Host UI:** native WPF (WebView2 dropped in S5). **Editions:** bare
  host only. **Stream 3 (post-S7)** = a module ecosystem (SDK/template + docs + in-app marketplace).
- Working model: per-phase branch → **local self-test verification** → merge (user authorized *"commit and
  merge as you go"* while GitHub Actions was globally down). **No reinstall/release without explicit go-ahead.**

## S3 engine relocation — expand/contract

The fortune engine was a **tightly-coupled monolith** (`Embedder ← SmartFortunes ← FortuneProvider ←
FortuneFileImporter`; 244 refs across 11 files), so it's landing via **expand/contract**.

- **S3c — expand (DONE).** Copied the four engine files into `modules/Fortunes/engine/` keeping their
  `DesktopPet.Ai` namespace, and rebound the base couplings: `AppPaths`→`FortunePaths` (host-storage-backed),
  `AiSettings` fortune fields→a module `FortuneSettings`, `AtomicFile`+`CrossSessionLock` copied into
  `engine/FileHelpers.cs`, embedded corpus simply not shipped (loader returns empty when absent), classifier
  TSV embedded as a fixture, and the base UI/AI calls stripped from `FilterSelfTest`. ONNX shipped as the
  module's own dependency (bge-small model beside `Fortunes.dll`). The engine stays **dormant** (Init still
  only does the welcome), so the base is untouched. `--fortunes-engine-selftest` proves dumb filter/pick,
  the full `FilterSelfTest`, and the smart layer (Embedder ONNX + SmartFortunes warm/pick) all in-module.
  **The load-bearing detail: native `onnxruntime.dll` in a plugin ALC** — see the gotcha below.
- **S3d — contract (NEXT).** Remove the StartUp fortune glue; wire the module engine live to
  `PetLanded`/`PetPoked`/`RegisterDropResponder`; **stub** the Options fortunes tab + `IPetRuntime`'s
  `SmartFortunesStatus`/`RebuildSmartFortunes` (the real WPF fortunes UI is S5); move the fortune self-tests
  to the module; drop the embedded corpus + ONNX + model from the base csproj + `packaging/runtime-files.txt`;
  split/migrate the fortune settings out of `AiSettings`. Then the whole relocation is user-visible-complete.

The precise rebind detail is in the `project-desktoppet` memory note.

## Build / verify / release

- **Build:** `pwsh build.ps1 -Release [-Zip]` → base + all modules into `build\DesktopPetPortable\bin\
  Release\x64\` (modules under `modules\<id>\`). `installer\build-installer.ps1 -Config Release` → MSI (WiX
  5.0.2). Root `global.json` accepts any installed **.NET 10.x** SDK (`version 10.0.100` + `rollForward
  latestMinor` — relaxed from the old exact 10.0.201 pin after that SDK was uninstalled here, leaving only
  10.0.302; CI still sets up 10.0.201 via setup-dotnet, so it keeps using that).
- **Self-tests:** the app takes `--*-selftest` flags (in-process, no external host). Current set incl.
  `--module-host-selftest`, `--sound-selftest`, `--fortunes-selftest`, `--security-selftest`,
  `--smart-selftest`, `--hardening-selftest`, `--options-selftest`, `--fortunecache-selftest`, … CI
  (`build.yml`) runs the flag loop + `runtime-hardening-selftest.ps1` + MSI.
- **Resource-churn soak** (`--resource-churn-selftest`): **REQUIRES** env `DESKTOPPET_DATA_ROOT` = an
  absolute dir under `%TEMP%\DesktopPet-ResourceSoak-*` (else it exits 2); tune with
  `DESKTOPPET_RESOURCE_CHURN_CYCLES` / `_MIN_DURATION_MS`. Run it via `Start-Process -Wait -PassThru` and
  read `.ExitCode` — **a `| tail` pipe masks the exe's exit code** (this bit me: a stale result file read
  as PASS). Result JSON lands in the data-root dir.
- **Releasing** (when asked): bump `ProductVersion.props`, push a `vX.Y.Z` tag → `release.yml` publishes the
  unsigned portable ZIP + MSI + `SHA256SUMS`. See `docs/RELEASE-CHECKLIST.md`.

## Durable gotchas

- **Installed process = `DesktopPet`** (older dev builds = `eSheep`). Kill with
  `Get-Process -Name eSheep,DesktopPet -ErrorAction SilentlyContinue | Stop-Process -Force` (never
  `-ErrorAction Stop` — it throws on the missing name and leaves the exe locked → MSB3027).
- **Where code lives:** engine `src/dotNet/*`, tray UI `src/Portable/*`, plugin host `src/dotNet/Plugins/*`,
  ABI `src/DesktopPet.Contracts/*`, modules `modules/<Name>/`. New base `.cs` must be added to
  `src/DesktopPet_Portable.csproj` (`<Compile Include>`; `EnableDefaultItems=false`). Modules use SDK
  default globbing.
- **Modules keep the host's contract:** a module references `DesktopPet.Contracts` with `Private="false"`
  so it binds the host's single shared copy (the loader's `Load` returns null for it → default context). A
  module with its own NuGet deps needs `<GenerateDependencyFile>true</GenerateDependencyFile>` +
  `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` so the dep dlls land beside it (a
  *library* project doesn't copy NuGet deps to output by default — that's why Sound needed it and the
  contract-only TestModule didn't).
- **Native deps in a module ALC (onnxruntime):** the loader's `ModuleLoadContext` overrides
  `LoadUnmanagedDll` — it resolves via the module's deps.json (`_resolver.ResolveUnmanagedDllToPath`, with an
  existence check) and then **falls back to probing the module's own folder**. That fallback is essential:
  the onnxruntime NuGet build targets **flatten** the native `onnxruntime.dll` beside the module dll instead
  of under `runtimes\win-x64\native\` (even though deps.json still points there), and it must resolve on an
  installed machine that has no NuGet cache. The Fortunes module pins `win-x64` (framework-dependent) to pull
  the native assets. NAudio was pure-managed and never needed any of this.
- **`AnimationInfo.Pet` is null on the engine-raised sound path** (the shared per-type `Animations` engine
  has no per-pet identity; sound is global). Real per-pet identity is future work S4's AI reactions want.
- **`net10-windows` in-box packages:** ConfigurationManager / ProtectedData (DPAPI) / System.Drawing /
  System.Text.Json are provided by the Windows Desktop framework — do NOT add them as PackageReferences
  (NU1510, and `TreatWarningsAsErrors` makes it fatal). `GenerateAssemblyInfo=false` strips the SDK platform
  attribute → add `[assembly: SupportedOSPlatform("windows7.0")]` to avoid CA1416 spam.
- **The active pet is persisted as its raw `animations.xml`** (not an id); downloaded pets read via
  `UTF8.GetString`, so a leading BOM survives — `PetXmlValidator.TryParse` strips it.
- `TreatWarningsAsErrors=true` — a build failure is often just a newly-orphaned member; the compiler points
  right at it. `src/packages/*` are untracked net48-era NuGet leftovers (the SDK build uses the global
  cache) — ignore them; a future cleanup could delete them.
- **CI note (2026-08-06):** GitHub Actions was globally down; S2, S3.1, and the S3c engine relocation were
  merged on the strength of the full local self-test suite + the resource-churn soak. Re-run CI on `master`
  once Actions is back to confirm green.
