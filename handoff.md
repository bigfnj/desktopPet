# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-11**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index (has the fine detail).
> Feature backlog: **[`BACKLOG.md`](BACKLOG.md)**.

---

## Big picture (2026-08-12)

**Released as `v1.2.2`.** `v1.2.1` bundled the whole net10 migration + plugin re-architecture below
through **S5c/d/e** (base AI-cluster removal, Newtonsoft dropped product-wide, About/Help → themed WPF),
plus the AI provider redesign (local+cloud+fallback), capability-aware model dropdowns with a VRAM-size
hint, and the Personality+Speech-style merge into one curated 26-entry **Disposition** catalog
(`AiSettings` schema v3).

**`v1.2.2` is the S6 release: the app now ships LEAN and features arrive as installable modules.** An
in-app **Options → Modules** catalog (HTTPS + SHA-256-pinned, permissions shown before download, restart
to activate) replaces the original "bundle modules into the installer" plan and absorbs what would have
been S7's signed-catalog/consent stream. On top of it: arbitrated poke reactions with a **Trigger Speech**
picker, fortune-pack browse/download/import, and a grouped+filterable pack picker with curated names for
all 152 packs. **Next up: S6 phase 2** (Pets itself becomes a pre-installed module — needs new `IHost`
spawn/remove verbs) — see BACKLOG.md for the full queue.

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
  **(Superseded: the S2 Sound module was RETIRED in B4 — the base owns audio playback now; see the "B" audio arc below.)**
- **S3 part 1 — Fortunes module boundary + welcome starter (MERGED, PR #4):** `modules/Fortunes` (id
  `fortunes`). On the first pet spawn it speaks a **personalized welcome** — a sheep-themed line with the
  **Windows username** (`Environment.UserName`) filled into a `{name}` slot; the 116-line `welcome.json` is
  adapted from ai-platform's DeskPet welcome quips. `--fortunes-selftest`.
- **S3 — Fortunes fully extracted (MERGED, PRs #4/#5/#6, `db0d6dd`).** The engine (`FortuneProvider` /
  `FortuneFileImporter` / `SmartFortunes` / `Embedder`) lives in `modules/Fortunes/engine/`; the module is
  the live fortune source and the base is **ONNX-free**. Residual in the base: the *dumb* `FortuneProvider`
  + corpus + the disconnected fortunes Options tab (retired in S5).
- **S4 — AI-brain module (MERGED, PR #7).** The optional
  screen-commentary LLM now lives entirely in `modules/AiBrain` and OWNS the ask/hotkey/idle/drop/emote flow
  through host services; the base is runtime-disconnected (drop → arbitrated tick; `ApplyAiBrainState`
  neutered; AI tray items removed). OFF by default; reachable via its own setting/hotkey until the S5 UI
  rebuild (accept-the-gap). Two additive ABI additions: `IHost.PlayAnimationAll` +
  `ScreenContext.WindowUnderPet`; the real global-hotkey registrar now lives in `PetHost`. A non-destructive
  migrator copies the base `ai-settings.json` (incl. DPAPI keys) into the module store on first run.
  **Deferred to S5 (like S3d deferred the fortune UI/engine):** deleting the 8 base AI-brain files, removing
  the FormOptions AI tab, and trimming the SecuritySelfTest AI tests — they're entangled with `AiSettings`'
  DPAPI credential machinery, so they're cut with the AiSettings split + WPF Options rebuild. `--aibrain-selftest`.
- **S5 — WPF shell + Pets features (MERGED, PRs #8-21).** The WPF module-manager window
  (`src/Portable/Wpf/OptionsWindow.cs` + `OptionsShell.cs`, shown from the WinForms UI thread) with host-built
  **Preferences** + **Pets** panes and each module's schema pane; the tray merges module contributions. Pets
  features: enriched cards (unique quips + "N animations · M sounds"), all 22 pets bundled (dev + ZIP; MSI
  bundling deferred to S6), a **"Check for new pets"** online button, per-pet **size** (inline clickable
  1/2/3), and per-pet **sound** on/off. Window default **1050×820** (Pets 3-across), OS-following **theme**
  (light/dark/system, no visible toggle), mouse-wheel scroll fix, dark scrollbar.
- **"B" audio arc — Option B host-owned audio (MERGED, PRs #22-25); user-confirmed audible.** The base OWNS
  playback now via `src/dotNet/AudioOutput.cs` — one shared mixer + **DirectSound** output; pet MP3s decoded
  once (ACM/OS codec, no shipped native) + cached; per-sound volume + overlap; graceful no-device. The engine
  `<sound>` path (`Animations.SoundSink`, now `(petTypeId,animId,data,loop)`) plays directly. A **device
  picker + Test-sound button** in Preferences route to any playback device (`DirectSoundOut.Devices`; setting
  `audioDeviceId`). **NAudio is back in the base** (3.0.0-preview.6: Core/WinMM/Dmo + transitive Midi +
  System.Numerics.Tensors) — **WASAPI was rejected** (its pkg needs a `net10.0-windows10.0.19041` TFM that
  drags `Microsoft.Windows.SDK.NET.dll` ~25 MB into the payload). The **S2 Sound module was RETIRED** in B4
  (inert once the base owned playback). **TTS is a backlogged future module** on this shared output.
- **NEXT — entangled, plan before building:** **S5b-2(d) Fortunes pane**, then **S5c/d/e** — the
  **AiSettings split**, delete the residual base fortune/AI code +
  Options tabs, Newtonsoft→System.Text.Json. The Fortunes module contributes **no** pane yet and the base's
  `FortuneProvider` is residual/disconnected, so these overlap. Then **S6** (bare host + package first-party
  modules into the installer, MSI-bundles-pets, migration, **2.0.0**) and **S7** (signed catalog + consent).
  (Already done: **FormOptions / FortunesWebView + WebView2 are retired**, and **About/Help are now themed
  WPF windows** — the pet engine (FormPet/FormSpeech) + the dev-only FormDebug console are the only WinForms left.)
- **Open follow-ups:** (a) per-pet size + sound key the ACTIVE/default pet as `""`, so a pet's card toggle
  doesn't bite while it's the *active* one — key the active pet by its real id (shared fix for both). (b) The
  schema panes (Preferences / AI Brain) aren't columnized for the wide window — awaiting the user's read on
  whether they feel too empty. (c) Optional theme polish: live re-theme on Apply (currently applies on reopen)
  + a dark ComboBox dropdown template.

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

## Extraction pattern — expand/contract (S3 + S4)

Both feature extractions use **expand/contract**: copy the engine into the module + rebind to the ABI
(dormant, base untouched) → flip the module live + disconnect the base → delete the dead base code. Rebind
template (reused for both): `AppPaths`→a module path provider (host-storage-backed), settings→a module
settings class, `AtomicFile`+`CrossSessionLock`+`UnicodeTextProgress` copied into module helper files,
logging→no-op, screen context→`host.CaptureScreenContext`.

- **S3 (fortunes) — DONE + MERGED.** Engine relocated (dumb + smart + native-`onnxruntime.dll`-in-ALC), flipped
  live, base ONNX-free. The load-bearing detail: native `onnxruntime.dll` in a plugin ALC (see the gotcha below).
- **S4 (AI brain) — functional flip DONE (branch `stream2/s4-aibrain`).** Engine relocated to
  `modules/AiBrain/engine/` (AiBrain/AiSessionManager/backends/ChatHistory/Personas/AiEndpointPolicy/settings),
  rebound to the ABI (`AiPaths`, `ScreenContext`, module Newtonsoft dep). The module owns AI live; the base is
  runtime-disconnected. The "contract" step (delete the 8 base AI files, remove the FormOptions AI tab, trim
  the SecuritySelfTest AI tests) is **deferred to S5** because those consumers are entangled with `AiSettings`'
  DPAPI credential machinery (which needs `Personas`/`AiEndpointPolicy`/`AiProviders` until the AiSettings
  split). So the base still compiles + its AI defensive tests still pass — it just never runs the brain.

The precise rebind detail is in the `project-desktoppet` memory note.

## Build / verify / release

- **Build:** `pwsh build.ps1 -Release [-Zip]` → base + all modules into `build\DesktopPetPortable\bin\
  Release\x64\` (modules under `modules\<id>\`). `installer\build-installer.ps1 -Config Release` → MSI (WiX
  5.0.2). Root `global.json` accepts any installed **.NET 10.x** SDK (`version 10.0.100` + `rollForward
  latestMinor` — relaxed from the old exact 10.0.201 pin after that SDK was uninstalled here, leaving only
  10.0.302; CI still sets up 10.0.201 via setup-dotnet, so it keeps using that).
- **Self-tests:** the app takes `--*-selftest` flags (in-process, no external host), e.g.
  `--module-host-selftest`, `--fortunes-selftest`, `--fortunes-engine-selftest`, `--wpf-options-selftest`,
  `--security-selftest`, `--hardening-selftest`, `--fortunecache-selftest`, … (`--sound-selftest` was removed
  when the Sound module was retired in B4; `--smart-selftest`/`--embed`/`--smart-progress` went when the smart
  engine moved to the Fortunes module). **`build.yml` is the source of truth for the current set**; CI runs the
  flag loop + `runtime-hardening-selftest.ps1` + MSI.
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
