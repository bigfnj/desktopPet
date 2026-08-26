# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-08-26**.
> Fork of Adrianotiger/desktopPet. Clone it wherever you like -- nothing here depends on the
> checkout path, and this file is public, so no machine paths go in it.
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> Also read the persistent memory note `project-desktoppet` in the auto-memory index (has the fine detail).
> Feature backlog: **[`BACKLOG.md`](BACKLOG.md)**.

---

## START HERE (session closed 2026-08-26)

**Shipped v1.8.0's feature payload: a fourth catalog module (Reminder), a module-owned styled-speech
platform, and two global audio toggles — on `feat/reminder-and-fixes`, pushed to `master`, Reminder in the
catalog.** ProductVersion is `1.8.0`; host ABI grew (additively) to `1.8.0`. Full detail in BACKLOG.md
("v1.8.0 — shipped") and the `project_desktoppet` memory note. In one breath:

- **Reminder module** (`modules/Reminder`, v1.2.0, `MinHostVersion 1.8.0`, perms `Speech|Storage|Network|
  Audio`): the pet announces upcoming calendar events. Three sources — a local JSON feed, a **Calendar URL /
  ICS** (iCal.Net 5.2.3 + NodaTime; Google / published Outlook / M365 / iCloud, with recurrence + time
  zones), and a **running desktop Outlook over late-bound COM** (attaches only to an already-running
  OUTLOOK.EXE, never launches, never quits). Multiple **lead times** (fires e.g. 15 + 5 min before via
  `DueNowMulti`/fired-key `eventId@lead`), **quiet hours** (overnight-aware), an optional **chime** (embedded
  MP3 via `IHost.PlaySound`), the **event location** in the announcement, and per-module speech styling.
  `CachingCalendarSource` keeps last-good on failure + throttled STA background refresh.
- **Module-owned styled speech** (the reusable platform behind it): `SpeechStyle` on the ABI +
  `IHost.Say/SayAll(text, style)`; the bubble (`FormSpeech`) is now a dumb renderer honoring family/size/
  bold/italic/underline/color; `DesktopPet.ModuleKit.SpeechStyleSettings` gives any module the setting fields
  + load/save + `ToStyle` in ~2 lines. Other modules can adopt it later.
- **Two global Sound master switches** (Preferences → Sound): **pet sounds** (embedded `<sound>` SFX) and
  **notification sounds** (module `PlaySound`, e.g. the chime), independent, both default-on
  (`AppSettingsStore` nullable-bool pattern; `StartUp` gates SoundSink + PlayModuleSound).
- Also landed earlier in the session on `master` (commit `abdd594`): the shimeji converter's
  frequency-weighted behaviour + WAV→MP3 sound capture (all 27 shipped pets re-converted, pets.json = 49),
  Pet Studio 1.4.0's **"Analyze installed pet" dropdown** (host `IPetManager.TryReadTypeXml`), and the
  Fortunes smart-picker repeat fix.

### What is NOT done -- read this before picking anything up

- **No release is cut, deliberately.** `master` has the source + the Reminder module in the catalog
  (raw.githubusercontent serves `master`, so downloads are live). But **no `v1.8.0` tag was pushed.** See the
  next bullet for why — this was a conscious hold at session close, awaiting the maintainer's informed
  decision, NOT a forgotten step.
- **A `v*` tag is not a harmless marker here — it auto-publishes binaries.** `release.yml` triggers on
  `push: tags: v*` and builds+publishes the ZIP + MSI to a public GitHub release. Those binaries bundle
  exactly what `THIRD_PARTY_NOTICES.md` lists (top of file) as **unresolved redistribution blockers**:
  the unlicensed upstream WinForms engine (Adrianotiger, no license grant), sprites without a complete
  redistribution grant, and the mixed/copyrighted fortune corpus. Do NOT tag a release without the
  maintainer's informed decision on those blockers. To cut it once cleared:
  `git tag v1.8.0 && git push origin v1.8.0`.
- **The 12 MiB pets require the new app build.** `RemoteCatalog.Parse` throws out the WHOLE catalog if any
  pet exceeds the app's `MaximumXmlBytes`; some shimeji exceed the old 4 MiB, so any app still on 4 MiB
  breaks on the new catalog (loses all "Check for new pets"). The maintainer chose this (keep quality,
  require app update) over reverting the budget.
- **Content-rating pass** on the catalog before it is genuinely public (shimeji.org content is unrated).
- **Reminder module manual eyeball still light.** Outlook-COM path was tested live this session (0 events
  was a genuinely all-past/expired-recurrence calendar; a past-window `Restrict` returned 46, proving the
  filter). The ICS path and the double-lead/quiet-hours/chime timing were unit-self-tested, not watched
  end-to-end against a real feed with an event a few minutes out. Worth one live eyeball.

### Four things worth knowing before you touch this

1. **`grimoire/03-pet-xml-format.md` is the authority on the pet XML format.** Read §6 (the `only` enum, and
   **the respawn rule** -- no eligible `<next>` means the pet respawns, so dead ends are intentional) and §7
   (the four magic names `fall`/`drag`/`kill`/`sync`) BEFORE concluding you have discovered anything. I
   wrote both up as findings this session and had to correct it.
2. **Do not add a shared source file under `src/` and register it in three csprojs.** `EnableDefaultItems`
   is false everywhere, so a new file must be added to the app, `modules/PetStudio` and any tool that
   compiles it -- and touching `modules/PetStudio/PetStudio.csproj` marks `petstudio.zip` stale, forcing a
   version bump and an in-app update prompt for nothing. Put shared helpers in a file the consumers already
   compile. That is exactly why `Mp3Format` lives inside `PetXmlValidator.cs` rather than its own file.
3. **The Shimeji format reference is not in this repo and must not be.** Clone `gil/shimeji-ee` (tracks
   Kilkakon v1.0.13) OUTSIDE the tree. On Windows the checkout fails on a macOS `Icon` file -- the clone
   still succeeds, so `git restore --source=HEAD conf/ img/` gets what you need: `conf/actions.xml`,
   `conf/behaviors.xml`, `conf/Mascot.xsd`, and two sample skins.
4. **`run-gate.ps1` is the verification.** One command, fails on a SKIP. It caught every mistake below.

### Two bugs fixed that were not mine, both latent for a reason

- **`New-ModulePublish.ps1`** passed git two pathspecs, not one: in PowerShell
  `@('status','--porcelain','--','modules/' + $x)` builds a FIVE-element array, so git saw `modules/` and
  `AiBrain` separately. The guard therefore tested "is anything under `modules/` dirty" and then blamed the
  module being published. It refused to publish aibrain because `PetStudio.csproj` was dirty.
- **`run-gate.ps1`** deleted self-test markers with `Remove-Item -LiteralPath`, which still performs `~`
  home-directory expansion. Windows uses the 8.3 short form for `TEMP` when the account name exceeds 8
  characters, and that contains a `~`. **Latent because run one has no marker to delete** -- a fresh box
  passes first and fails second, and CI never sees it because the runner's profile is short.

### Three mistakes I made and corrected

- Extracted `Mp3Format` into its own file. The gate caught the PetStudio build break; then I realised the
  csproj edit would force a pointless `petstudio` republish and folded it into `PetXmlValidator.cs` instead.
- Claimed the four magic names and the `only` semantics as findings. `grimoire/03` §6-§7 already had both.
  `MAPPING.md` now separates "already documented" from "what this pass added" so it cannot happen again.
- Treated terminal animations as needing graph closure. §6's respawn rule makes them deliberate;
  `PetGraph.Terminal` is now labelled informational, and only *unreachable* animations are a signal.

### Decisions taken (review if you disagree)

- Converter is a **console tool under `tools/`**, not a module -- BACKLOG #4's own workflow is a dev
  workflow, and a CLI iterates far faster. The engine stays separable so a module can wrap it later.
- Acceptance bar is **machine-checkable only**: validates, reachable, frames index real tiles, under 4 MiB.
  Anything about whether it *looks* right is reported for a human, never enforced.
- `aibrain` got a **version bump rather than an in-place republish**, so existing installs are actually
  offered the fix and `1.2.0` keeps meaning one payload.
- **Commit identity is set repo-locally** (`git config --local user.name` / `user.email`) to match the
  author on every existing commit. Worth doing because this repo is PUBLIC and a machine's default git
  identity may be a work account -- git will happily derive one from the hostname and publish it. Check
  `git log -1 --format=%an` after cloning: repo-local config does NOT travel with the clone.

---

## START HERE (session closed 2026-08-20) -- superseded by the run above

**Two releases shipped: `v1.5.0` and `v1.6.0`.** Everything is merged to `master`, CI-green, tagged, published
and installed on this box. Tree clean, nothing half-finished.

### What is NOT done — read this before picking anything up

The session was planned as A→F. **A, AA and B shipped. C, D, E and F were never started** — there is no code
for any of them, only the design in the plan. Do not go looking for a half-built Voice module; there isn't one.

| Part | State |
|---|---|
| C — Voice module (Windows WinRT engine, speech modes) | **Not started.** Design is solid; start with the spike below |
| D — reminders (JSON/XML/line formats, scheduler) | Not started |
| E — Kokoro engine | Not started, and may never be — see the licence risk |
| F — Personality module (quotes, timers) | Not started |

**Start Part C with the spike, not with code.** Nobody has proven that WinRT
`Windows.Media.SpeechSynthesis` works from an **unpackaged Win32 process**; Microsoft's docs only describe UWP
use. AiBrain proved `Windows.Media.Ocr` works there, which is encouraging but is not the same API. Documented
fallback if the spike fails: `System.Speech` (SAPI 5), which definitely works unpackaged but cannot reach
Windows 11's natural voices. This box has David/Mark (male) and **Zira (female)** as OneCore voices, so the
"prefer a female voice" default is satisfiable here.

**Kokoro may be undeliverable, and that is an acceptable outcome.** It needs eSpeak-NG for phonemes, eSpeak-NG
is GPLv3, and we neither bundle nor mirror it. If arms-length use (a child process, never linked) does not work
cleanly, drop it, keep the Windows engine, and record why — the same call this repo already made twice, for
Tesseract bundling and for TTS itself. Do not let sunk design cost force a licence decision.

The **host ABI it all needs already exists and is released**, so Part C needs no further host work:
`PlaySound` / `StopSound` / `RegisterSpeechResponder` / `Audio` + `Voice` permissions, all in 1.6.0. A Voice
module declares `MinHostVersion 1.6.0`.

### What shipped

| | |
|---|---|
| **v1.5.0** | per-pet speech routing: a reaction belongs to ONE pet, plus the Pet Speech tray cascade |
| **v1.6.0** | the audio + speech-interception ABI a voice module needs (`PlaySound`, `StopSound`, `RegisterSpeechResponder`, `Audio`/`Voice` permissions) |
| **fortunes 1.2.0, aibrain 1.2.0** | live in the catalog; both require host 1.5.0 |
| **petstudio 1.1.1** | themes from `IHost.IsDarkTheme` instead of the OS registry |
| PRs | #85 backlog, #86 CI fix, #87 host ABI + tray, #88 modules, #89 audio ABI |

Both releases hash-verified and installed here; `Contracts.dll` refreshed to match each time, which is
release-checklist row 10 and the failure that silently breaks every module.

**A second latent bug fixed in 1.6.0, worth knowing:** an unrecognised permission name made the catalog parser
throw for the **entire catalog**, not the entry. Since every catalog feature shares one fetch, the first
release to add a flag would have taken the Modules pane, the monthly update check, pack browsing *and* the
Pets gallery away from every older host. It had already fired unnoticed — `Pets` shipped in 1.4.4, so a v1.4.2
host cannot parse today's catalog at all. Publishing the Voice module would have done it again, at scale.

### The bug that started it

*"When the same pet is chosen, it speaks at the same time, and the same saying."* Correct, and it was **all**
pets, not just duplicates: `StartUp.SayAll` fanned one string to every pet and everything spoke through it.
Fixed by making a reaction belong to one pet. **Routing is the feature; `Say(pet, …)` is the fix** — per-type
routing alone would not have fixed it, because two Pearls share a routing key.

### Four things worth knowing before you touch this

1. **`triggerSpeech` uses `""` for GLOBAL; the pet mix uses `""` for the ACTIVE pet.** Keying a real pet by its
   raw mix id rewrites the all-pets preference *and looks like it worked*, because the lookup falls back to
   global — every other pet type would test fine. `SpeechRoutingKey` exists for this and an invariant pins it.
2. **The pet-aware responders are new NAMES, not overloads.** A parameterless `delegate { }` converts to both
   `Func<bool>` and `Func<IPet,bool>`, so overloading would be CS0121 for anyone recompiling.
3. **`IsPetAlive` is on `IHost`, not `IPet`** — `IPet` has seven implementations and ModuleKit ships
   `FakePet : IPet`, so adding there breaks modules on recompile.
4. **Both leak soaks and `--wpf-options-selftest` need a real window station.** Keep the machine logged in.

### Decisions taken unattended (review these)

| # | Decision | Why | Reversible? |
|---|---|---|---|
| 1 | **Per-INSTANCE pet identity deferred; shipped per-TYPE** | Reverses an explicit choice. Pricing it found schema v3, replacing `DeriveOnScreenMix` (which the whole preview-safety invariant rests on), three rewritten CoreTests groups, two permanent removal models, and a nickname feature that does not exist — and two Pearls would *still* share one AI disposition. Types already have curated names (Pearl, Rick, Ben), so the menu reads as pictured | Yes, own release |
| 2 | **Consolidated the two release workflows, deleted `publish-release.yml`** | Both fired on `v*` and clobbered the same release, so SHA256SUMS listed the nupkgs or not depending on who lost the race. Verified fixed: the v1.5.0 tag fired exactly one workflow | Yes |
| 3 | **Poke escalation made per-pet in the same release** | Not in the plan, but shipping routed sass on shared `pokeCount` means poking Pearl three times then Rick gives Rick the sass tier. Same class of bug | Yes |
| 4 | **Repeat guard moved into `FormPet.Say`** | It was in `SayAll`, which `IHost.Say` bypasses, so routing would have silently killed the user's suppress-repeats preference | Yes |
| 5 | **Drop subject is round-robin, not random** | Uniform random repeats the same pet often enough to read as "still broken" | Yes |
| 6 | **Bathtub escape stays global** | Every pet fleeing *is* the joke, unlike sass which answers "you poked me". Now commented as a decision | Yes |
| 7 | **PetStudio left declaring `Speech` it does not have** | It calls `SayAll` for a user-visible error without declaring `Speech`. Changing to `Log` would hide a real error; declaring `Speech` is a permission widening needing the update-row consent delta, which is Part B | Yes, in BACKLOG |
| 8 | **`setup-msbuild` left in `release.yml`** | Vestigial, but the release path is the wrong place to discover an implicit dependency | Yes, in BACKLOG |

### Two mistakes I made and corrected

- **I corrupted `AiBrainModule.cs`** with a PowerShell `Get-Content -Raw` / `Set-Content -Encoding UTF8`
  round-trip: it read UTF-8 as ANSI and re-encoded, producing 25 mojibake sequences. Caught it, reverted the
  file, redid the edits with the editor. **Never round-trip a `.cs` file through PowerShell here.**
- **The window-soak reported a false leak** (one rooted window per segment, always the last). Not a leak — the
  strong reference escaped the cycle method into the caller's stack slot. See the BACKLOG entry.

---

## START HERE (written 2026-08-18, at the end of a long session) — superseded by the run above

**Nothing is half-finished.** `master` is clean and pushed, `v1.4.8` is released, all three modules are
published and current, and every deferred item from the previous sessions is closed. If you are looking for
"what was I doing", the honest answer is: nothing — pick something from BACKLOG.

Three things to read before you change anything:

1. **THE HOST CONTRACT below — there is no freeze, and do not reinstate one.** It was tried and it failed
   three times in three days. The six rules replace it and are already enforced by gates.
2. **`docs/module-authoring.md`** is the entry point for anything module-shaped, including your own.
   `dotnet new desktoppet-module` scaffolds a module that builds and self-tests as generated.
3. **`tests\run-gate.ps1` is the whole local gate in one command**, and it fails on a *skipped* self-test on
   purpose. Run it before you believe anything.

Two traps that cost real time here, both now guarded but worth knowing:

- **Publishing a module: commit the SOURCE first, the payload second.** The freshness check compares commit
  *recency*, and because the zip is deterministic, re-zipping after a bad order produces identical bytes — so
  there is no new commit available to fix it. `New-ModulePublish.ps1` now refuses to start with uncommitted
  module source.
- **`master` had no upstream tracking**, so a `git checkout master` silently landed on a stale 1.4.4 tree and
  `git pull` errored. It is fixed now, but verify with `git log --oneline origin/master` rather than trusting
  a local branch.

The likeliest next module is **TTS/voice**, and it will immediately hit the audio gap recorded at the top of
BACKLOG: `IHost` has no playback verb at all. Add it *with* that module, per rule 3.

---

## THE HOST CONTRACT: stable, not frozen (read this before touching the ABI)

**There is no freeze. Do not reinstate one.** The host was frozen at 1.4.4 and that rule failed three times in
three days: reopened at 1.4.6 for `IPetManager.PetsDirectory`, then 1.4.7 for `IHost.IsDarkTheme` and
`IHost.Log`, then 1.4.8. Building **one** module plus the SDK surfaced **three** ABI gaps, which is not a
failure of foresight — it is what building reveals. A freeze would have made all three permanently impossible,
and it had already pushed a real UX defect (a failed module being invisible) into BACKLOG as a "post-freeze
fix" while its only escape route deleted the user's settings.

What you actually want from a freeze is *"a module written today keeps working."* That is delivered by the six
rules below, not by refusing to add anything. Adding is cheap; the rules are what make it safe.

**1. `AssemblyVersion` stays `1.0.0.0`, forever.** It is the binding identity every built module references
(`DesktopPet.Contracts, Version=1.0.0.0`). Move it and every existing module fails to load. `FileVersion`, by
contrast, tracks the product deliberately.

**2. Additive only.** Never remove a member, and never change what one means. This is the *real* permanent
commitment, and it holds whether or not anyone calls it a freeze. Adding a member cannot break an existing
module; removing or redefining one breaks all of them silently.

**3. An ABI change bumps the product version in the same commit.** `DesktopPet.Contracts` stamps its
`FileVersion` from `ProductVersion.props`, and a Windows Installer major upgrade skips refreshing a file whose
version did not change — shipping an ABI change without the bump installs a stale `Contracts.dll` that cannot
resolve the new types (the failure `9009133` fixed).

**4. Never declare an event you do not raise.** `PetIdle` and `AnimationStarted` were deleted for exactly
that: a declared-but-silent event is a trap that looks like a feature. Wire the raise in the same change.

**5. Raise `MinHostVersion` only when you actually call a newer member.** `ModuleHost.LoadFrom` enforces it
(`ModuleHostRequirement.IsSatisfied`) *before* `Init`, refusing a too-new module with a legible log line
instead of letting it die at its first missing member. A module declaring a version above the *shipped* host
is refused until that host ships — so publish the host first, then the module (Pet Studio 1.1.0 declares
1.4.6 for this reason, and is why it was published after that release rather than with it).

**6. Do not move a source-linked engine file without re-running the parity self-test.** Pet Studio compiles
the host's own parser/validator/reachability rather than copying them, so a reshuffle under `src/dotNet/`
can silently change its verdict. `--petstudio-selftest` asserts the module's verdict equals
`PetXmlValidator`'s on every fixture; that assertion is the guard, not a freeze.

**Two invariants that are about behaviour rather than shape:**

**Previews are invisible to modules.** A transient preview pet (`IPetManager.SpawnPreview`) never reaches
`settings.json`, never survives a restart, never appears in the tray's Remove submenu, and never raises
`PetSpawned` / `PetPoked` / `PetLanded`. That rests on one place: `StartUp.DeriveOnScreenMix` skips transient
registry entries, and both `PersistMix` and the tray read it. Anything that must ignore previews should read
that list rather than walking the pet array.

**Deliberate ABI exclusions, so they are not re-litigated.** No "use this pet" verb: it writes the XML into
settings, closes every pet and resets the mix, and the host's own Pets pane owns it. No per-type size, sound
or voice: those are user preferences the Pets pane owns, and a module writing them would fight it with no
arbitration. These are decisions, not gaps — unlike the audio gap in BACKLOG, which is a real one.

**Gates.** `tests\run-gate.ps1` runs the whole local gate in one command and **fails on a skip** — the module
self-tests skip-pass when their folder is absent, so a build that silently produced no modules used to look
identical to a clean run. `tests\runtime-resource-soak.ps1` is the only committed check that can catch a leak
(OS handle/GDI/USER/private-byte growth, sampled from outside the process); it is a pre-tag step, not a CI gate.
Baseline: handles +5, GDI −6, USER −6, private bytes +13.6 MB, all well inside their bounds. It does **not**
cover the Pet Studio window — see the leak-soak method below.

## Current state (2026-08-18)

**Latest public release: `v1.4.8`.** Three releases landed in one day — 1.4.6, 1.4.7, 1.4.8 — each with MSI +
portable ZIP + SHA256SUMS on its GitHub release. **The live catalog serves 3 modules: fortunes 1.1.2,
aibrain 1.1.2, petstudio 1.1.0.** Both catalog paths a user actually takes are verified end to end on a real
install: **installing** Pet Studio from the catalog, and **updating** fortunes/aibrain 1.1.1 → 1.1.2 with the
module's data directory preserved (fortunes kept 155 files including downloaded packs).

What each release added, newest first:

- **1.4.8** — a module that fails to load is no longer invisible: it reports the reason with a non-destructive
  **Reinstall**, and a `MinHostVersion` refusal says "needs a newer app" instead. This release also **attaches
  `DesktopPet.Contracts.nupkg` and `DesktopPet.ModuleKit.nupkg` as release assets**, which is what makes
  writing a module outside this repo possible (see `docs/module-authoring.md`). They are deliberately NOT on
  nuget.org: the contract's package version tracks the product, so publishing would mean a new public package
  on every release even when the ABI is byte-identical.
- **1.4.7** — `IHost.IsDarkTheme` (a module-owned window can match the app; only the host knows whether the
  user's light/dark/**system** choice resolves to dark) and `IHost.Log` (before it, a module's only way to
  report anything was to make the pet *say* it).
- **1.4.6** — Pet Studio 1.1.0 + `IPetManager.PetsDirectory`, plus the sheep `king_slamB` fix.

1.4.6 in more detail, since it carried the most:

1. **`IPetManager.PetsDirectory`** — one additive ABI member, so a module can open a file dialog in the user's
   pet library instead of guessing the host's folder layout. This is why the version moved.
2. **Pet Studio 1.1.0** — published to the catalog (it declares `MinHostVersion 1.4.6`). A three-column
   authoring window: an editable XML pane (debounced re-analyze, atomic save) feeding preview/install, a
   colour-coded **reachability map** with clickable legend filters, and a detail panel rendering the selected
   animation's real sprite frames with playback plus its outgoing transitions. Its Open dialog defaults to the
   pet library and remembers the last folder browsed to. Blank (fully transparent) frames and orphaned
   animations now explain themselves rather than looking broken.
3. **The module SDK** — see `docs/module-authoring.md`, which is now the entry point for writing a module:
   - **`src/DesktopPet.ModuleKit`** — the helpers each module had hand-copied (`AtomicFile`,
     `CrossSessionLock`, `EmbeddedResources`, `UnicodeTextProgress`, `ModulePaths`, `JsonSettingsStore<T>`,
     `SelfTestProbe`) plus a `Testing` namespace with the `RecordingHost`/fakes every self-test reinvented.
     **It is not the ABI:** Contracts is `Private="false"` and shared from the host; ModuleKit is referenced
     normally and ships *inside* each module's folder, so modules can move versions independently.
   - **`dotnet new desktoppet-module`** (`templates/desktoppet-module`) scaffolds a module that builds and
     passes its own self-test as generated. Guarded against rot by `packaging\Test-ModuleTemplate.ps1`.
   - **`--module-selftest=<id>`** runs any module's own `public static bool SelfTest(out string)` through the
     real loader, so a new module needs **no host edit** to be testable. Absent module = SKIP (which the gate
     treats as failure); no `SelfTest` = FAIL.
   - **`packaging\New-ModulePublish.ps1`** does the whole publish sequence and refuses to regenerate the
     catalog while the zip is uncommitted.

Also in this release: **the seven sheep's orphaned `king_slamB_down`/`king_slamB_up` animations are wired**
(the up/down walks and jumps never slammed onto the opposite surface, unlike base/top). The two
`king_jump_*_flip` animations are left unreachable **on purpose** — base/up jumps already rotate directly, so
those flips were bypassed by design. A sheep therefore still reports 2 unreachable, correctly.

**Leak-soak method for the Pet Studio window** (not committed; `runtime-resource-soak.ps1` cannot reach it).
A throwaway net10 WPF exe referencing the built `PetStudio.dll` + `DesktopPet.Contracts.dll` constructs the
window by reflection with a fake `IHost`, analyzes a pet, selects a node, shows and closes it, and samples
`HandleCount` / `GetGuiResources(GDI,USER)` / `PrivateMemorySize64` from outside. Run **two** segments of 20
cycles: the pass criteria are zero windows still alive as `WeakReference`s after an LOH-compacting GC, flat OS
handles, and **segment 2 barely growing** — the first segment legitimately sets a high private-byte watermark
because the sheep's sprite sheet is large. That last signal is what found the re-decode bug: the debounced
re-analyze was decoding a ~15 MB sheet on every keystroke-settle, now cached on an `<image>` fingerprint.

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
`git filter-repo --mailmap` (→ `bigfnj` (personal identity)); master + the v1.2.1/1.2.2/1.2.3 tags were
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

**The box** runs the **published `v1.4.8` MSI** (hash-verified against `SHA256SUMS.txt`), with all three
modules installed **through the catalog rather than by hand** — Pet Studio via a fresh install, fortunes and
aibrain via the in-app update to 1.1.2. `DesktopPet.Contracts.dll` refreshed with each upgrade (1.4.6.0 →
1.4.7.0 → 1.4.8.0), which is the FileVersion-tracks-product rule proving itself against real ABI changes.

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
