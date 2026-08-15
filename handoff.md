# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-15**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index (has the fine detail).
> Feature backlog: **[`BACKLOG.md`](BACKLOG.md)**.

---

## THE FREEZE CONTRACT (read this before touching the host)

The host is being frozen: after the release cut from this work, capabilities arrive as modules and the
host itself stops shipping. Two consequences drive everything below — **anything the ABI cannot express
becomes permanently impossible**, and **anything the host gets wrong becomes permanently wrong**.

**The frozen host version is the permanent `MinHostVersion` floor.** `ModuleHost.LoadFrom` now enforces it
(`ModuleHostRequirement.IsSatisfied`), refusing a module that needs a newer host with a legible log line
instead of letting it die at its first missing member. It is permissive by design: only a requirement both
sides can express is enforced, so it refuses for exactly one reason. A module declaring a version above the
frozen host will be refused **forever**, so do not raise `MinHostVersion` in a module unless you truly mean
"this cannot run on the shipped host".

**Every ABI event is raised by the host.** `PetIdle` and `AnimationStarted` were removed at the freeze
precisely because they were declared and never raised — a silent event in a final contract is a trap with no
release left to fix it in. If you ever add one, wire its raise in the same change.

**Previews are invisible to modules.** A transient preview pet (`IPetManager.SpawnPreview`) never reaches
`settings.json`, never survives a restart, never appears in the tray's Remove submenu, and never raises
`PetSpawned` / `PetPoked` / `PetLanded`. That rests on one place: `StartUp.DeriveOnScreenMix` skips transient
registry entries, and both `PersistMix` and the tray read it. Anything that must ignore previews should read
that list rather than walking the pet array.

**Deliberate ABI exclusions, so they are not re-litigated.** No "use this pet" verb: it writes the XML into
settings, closes every pet and resets the mix, and the host's own Pets pane owns it. No per-type size, sound
or voice: those are user preferences the Pets pane owns, and a module writing them would fight it with no
arbitration. Both were in the reverted S6p2 `IPetManager`; leaving them out is a decision, not an oversight.

**An ABI change requires a product version bump in the same commit.** `DesktopPet.Contracts` stamps its
`FileVersion` from `ProductVersion.props`, and a Windows Installer major upgrade skips refreshing a file
whose version did not change — shipping an ABI change without the bump installs a stale `Contracts.dll`
(the failure `9009133` fixed). `AssemblyVersion` stays `1.0.0.0`; that is the binding identity every shipped
module references.

**Gates.** `tests\run-gate.ps1` runs the whole local gate in one command and **fails on a skip** — the module
self-tests skip-pass when their folder is absent, so a build that silently produced no modules used to look
identical to a clean run. `tests\runtime-resource-soak.ps1` is the only check that can catch a leak (OS
handle/GDI/USER/private-byte growth, sampled from outside the process); it is a pre-tag step, not a CI gate.
Freeze baseline: handles +5, GDI −6, USER −6, private bytes +13.6 MB, all well inside their bounds.

## Current state (2026-08-15)

**Latest public release: `v1.4.4`** (2026-08-15) — the release candidate for the host code freeze. It bundles
the whole pre-freeze sweep (PR #74) and the Pet Studio module (PR #75), both merged to `master`. The box runs
a hash-verified install of the **published** 1.4.4 MSI; the user is doing a manual validation pass before we
progress. **Read the FREEZE CONTRACT block above before touching the host** — the ABI is meant to stop
changing after this.

What landed in 1.4.3→1.4.4 (full detail in the PR bodies + the freeze contract; don't re-derive here):
- **ABI closed out:** removed the never-raised `PetIdle`/`AnimationStarted`; added `IPetManager` (inspect /
  place / author, incl. a transient `SpawnPreview` from an XML string), `IPet.TypeId`, `ModulePermissions.Pets`,
  the catalog `"pet"` kind; `MinHostVersion` is now enforced at load time, before `Init`.
- **Bugs:** `PetTypeRegistry` re-stage eviction; module payloads unpacked on the UI thread; a cold `dotnet
  build` failed; `global.json` didn't actually pin the SDK.
- **Gates:** the leak soak is restored (rot-proof counter list); `tests/run-gate.ps1` runs the whole local
  gate and FAILS on a skipped self-test; module version parity (source = modules.json = catalog.json) is
  enforced; two salvaged reachability invariants live in `--security-selftest`.
- **Removed:** `Tools/` (PetEditor + PetTester, the last net48 island) and ~600 lines of verified-dead code.
- **`modules/PetStudio` (Pet Studio 1.0.0): BUILT + CI-gated, NOT published.** It declares `MinHostVersion
  1.4.3`, so it can't be listed in the catalog until that host ships. It source-links the host's own parser
  (safe only because the host is frozen) and previews via `IPetManager`. On the box it was copied in by hand
  from the build output — it is NOT in the MSI. Publish steps are in BACKLOG.md.

**Historical — the OCR + module-update work now shipped as `v1.4.2`:** the pet quoted `asÂ®` off the screen.
Root cause was not the
model: `AiBrain.RunOcrAsync` redirected tesseract's stdout without setting `StandardOutputEncoding`, and an
unset encoding is taken from `GetConsoleOutputCP()`, which returns **0** in a GUI process with no console —
.NET then decodes codepage 0 as **CP_ACP**, the system ANSI codepage (1252 here). Tesseract writes UTF-8, so
every non-ASCII glyph on screen entered the prompt as mojibake (`as®`→`asÂ®`, `—`→`â€"`, `’`→`â€™`, `é`→`Ã©`)
and the model quoted the garbage back. Reproduced and fixed at the byte level, then verified through the real
module: `Test OCR` returns ✓ on the live engine. **Windows built-in OCR was never affected** (WinRT strings),
so this only ever hit users who have Tesseract — the reporter's box has it configured.

Three guards now hold it: the probe image in `SelfTestOcrAsync` carries a `®` and the status goes RED on a
mis-decode (a MISSED `®` is not a failure — only a mis-decoded one); `--aibrain-selftest` asserts the psi
factory pins UTF-8 on stdout AND stderr (runs on CI, where no OCR engine exists); and
`tests\runtime-hardening-selftest.ps1` fails repo-wide if any `RedirectStandardOutput` lacks a paired
`StandardOutputEncoding`. That last one was negative-tested against the pre-fix file.

**Why a host release came with it:** the module republish alone could never have reached anyone who already
had AI Brain. `ModulesPaneControl.DiffNew()` diffed the catalog **by id only**, so an installed module vanished
from the list forever, no version was ever compared, and the only route left was Uninstall — which deletes the
module's settings, keys and history. So the pane now offers **"Update to vX.Y.Z"** on an installed row whose
live version is older than the catalog's, and `PendingModuleUpdates` applies it: verified download → unpack to
`<baseDir>\module-staging\<id>.staged` → marker → next launch swaps it in before `ModuleHost.LoadFrom` can lock
anything, **leaving the module's data directory alone**. Staging sits OUTSIDE `modules\` on purpose (the loader
loads every subdirectory it finds, and would have loaded a half-written `aibrain.new` as a module) and under
`BaseDirectory` rather than the data root so the swap is a same-volume `Directory.Move`. The swap moves the old
copy aside and rolls back on failure: deleting first and then failing would leave the user with no module at
all, which is worse than the stale one they were replacing.

**The check also runs itself now, monthly.** `ModuleUpdateSchedule` stores the month a check last *succeeded*
and becomes due when the calendar month moves on, rather than firing on the 1st — a pet that was switched off
that day would otherwise skip the month entirely. Stamped only after a successful fetch (offline costs a retry,
not a month), seeded without checking on a fresh install, skipped with no modules installed, and evaluated two
minutes after launch then six-hourly (a cadence for noticing the month flip, not a polling rate). A hit raises a
tray notification that opens Settings → Modules; nothing self-installs. It is the app's only unprompted network
request, hence a Preferences toggle (default on, absent-in-older-doc reads as on) and a PRIVACY.md paragraph.
The version rule lives in one shared `ModuleUpdateScan` so the pane's button and the notification can't disagree.

**Earlier releases (historical):** `v1.4.1` (2026-08-14, a packaging fix); `v1.4.0` (2026-08-13) fixed the pet
reading its OWN "Sheep"-titled window as screen context (a sheep-joke loop; fixed in `ActiveWindow`) and the
Genres filter being a no-op for downloaded packs. `v1.4.2` (2026-08-14) shipped the OCR mojibake fix + the
module-update path + the monthly auto-check above.

**History was scrubbed (2026-08-13):** a personal work email on the 10 fork-day commits was removed via
`git filter-repo --mailmap` (→ `bigfnj <peshinator@gmail.com>`); master + the v1.2.1/1.2.2/1.2.3 tags were
force-pushed. **Residual:** GitHub's immutable `refs/pull/*/head` refs still hold the old commits — a
force-push can't remove them; fully purging needs a GitHub Support "remove sensitive data" request (in BACKLOG).

**S6p2 (Pets-as-a-module) was built, then FULLY REVERTED (2026-08-14).** The whole stream — an `IPetManager`
ABI + PetHost bridge, a `modules/Pets` plugin owning the Options→Pets pane + tray, per-row action buttons,
per-type settings, and a per-pet "voice" picker — shipped gated + pushed, but on the live eyeball the user
disliked the module UI (lost tray icons, then the pane itself), so it was reverted to the pre-S6p2 state
(`890f76d`). Design + code are preserved in git history (`feat(s6p2)` commits `53912a6`..`520aada`).
**Lesson: eyeball a UI-heavy direction EARLY, before building four phases of it.**

**Kept from that cycle (genuine, module-independent):** the `DesktopPet.Contracts` **FileVersion now tracks
the product** (`9009133`). It had a fixed `FileVersion=1.0.0.0`, so a Windows Installer major upgrade SKIPPED
refreshing the ABI dll when its content changed but the version didn't — shipping a stale Contracts.dll that
couldn't resolve new ABI types (hit live during the eyeball install). `AssemblyVersion` stays `1.0.0.0` (the
ABI binding version modules reference). **Any future ABI change now refreshes on upgrade.**

**The box** runs the **published `v1.4.4` MSI** (hash-verified against `SHA256SUMS.txt`), with fortunes 1.1.1
+ aibrain 1.1.1 carried across the upgrade and **Pet Studio copied in by hand** from the build output (it is
deliberately not in the catalog or the MSI). `DesktopPet.Contracts.dll` refreshed to 1.4.4.0 on the upgrade,
confirming the FileVersion-tracks-product fix works with a real ABI change riding on it.

---

## Big picture (2026-08-12) — historical

**Released as `v1.2.3` (2026-08-12).** Backlog #9 (Fortunes clarity) plus three real bugs it turned up.
Read the two OPEN items at the top of BACKLOG.md's "Bugs & maintenance" before the next release — both
are decisions waiting on the user, not work waiting on a keyboard.

**The one thing to internalise from this session:** `modules-dist/<id>.zip` is a **committed artifact
that the live catalog serves from `master`**, and nothing rebuilds it for you. Merging to master *is*
the module publish — no tag, no release, no upload step. Both modules had silently drifted from their
source, and the drift was invisible because the failure paths are quiet:

- Fortunes shipped with **no built-in corpus at all** (the S3 move dropped the EmbeddedResource from
  the base and the module never picked it up), so a lean install had nothing to say. The lookup failure
  went into `_embeddedError`, which only ever appends to a diagnostics string nothing reads.
- AI Brain shipped a release behind PR #71, so catalog installs had no Windows OCR and therefore no
  screen reading unless the user happened to have Tesseract.

`packaging\Test-ModulePublishFreshness.ps1` now fails CI on that drift. **Practical consequence: any PR
touching `modules/<Id>/` needs a republish commit before CI passes** — rebuild, `New-ModuleDistZip.ps1`,
**commit the zip**, then `New-ContentCatalog.ps1`, in that order, because the catalog hashes the
*committed* blob. Markdown is excluded so a BACKLOG note doesn't demand a 31 MB republish.

Also worth knowing: two self-tests (`SmartFortunes.SelfTest`, `ProgressiveSelfTest`) had sat with **zero
callers** since the same S3d move, and both fail on an empty pool — they would have caught the corpus bug
on day one. If you relocate code between the base and a module, check what stopped being invoked.

**Previously released as `v1.2.2`.** `v1.2.1` bundled the whole net10 migration + plugin re-architecture below
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
  when the Sound module was retired in B4). The smart-engine flags went with the S3d move to the Fortunes
  module and **left their tests with no callers at all** — `SmartFortunes.SelfTest` now runs inside
  `--fortunes-engine-selftest`, and the slow cold-cache one came back as
  **`--fortunes-smart-progress-selftest`** (~18s; CI runs it, the local default loop does not).
  **`build.yml` is the source of truth for the current set**; CI runs the flag loop +
  `runtime-hardening-selftest.ps1` + `packaging\Test-ModulePublishFreshness.ps1` + MSI.
- **Resource-churn soak** (`--resource-churn-selftest`): **REQUIRES** env `DESKTOPPET_DATA_ROOT` = an
  absolute dir under `%TEMP%\DesktopPet-ResourceSoak-*` (else it exits 2); tune with
  `DESKTOPPET_RESOURCE_CHURN_CYCLES` / `_MIN_DURATION_MS`. Run it via `Start-Process -Wait -PassThru` and
  read `.ExitCode` — **a `| tail` pipe masks the exe's exit code** (this bit me: a stale result file read
  as PASS). Result JSON lands in the data-root dir.
- **Releasing** (when asked): bump `ProductVersion.props` (**both** `DesktopPetVersion` and
  `DesktopPetAssemblyVersion`; `publish-release.yml` verifies the tag matches), push a `vX.Y.Z` tag →
  `release.yml` publishes the unsigned portable ZIP + MSI + `SHA256SUMS`. Fully automated: nothing is
  built or uploaded by hand. See `docs/RELEASE-CHECKLIST.md`.
- **Tagging will fight you**: upstream tagged **v1.2.3–v1.3.2** in 2019-21 and those refs are in any clone
  with `upstream` as a remote, so `git tag v1.2.3` fails as "already exists". `origin` has none of them.
  Delete the stale local ref and re-tag (reversible via `git fetch upstream --tags`). See the OPEN backlog
  item — the durable fix is to move our series past v1.3.2.
- **Modules do NOT ship with releases.** They are served from `master` via
  `raw.githubusercontent.com/bigfnj/desktopPet/master/modules-dist/` + `catalog.json`, so **merging to
  master publishes them to every existing user immediately**, independent of any tag. Same for pets and
  packs. Treat a merge that touches `modules-dist/` as a publish.

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
