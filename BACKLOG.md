# AI Desktop Pet — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

---

## ▶ Current major work — .NET 10 + plugin re-architecture (2026-08-06)

The active effort is **not** in the feature list below. Two things, both now **released** — the public line
reached **v1.2.3 (2026-08-12)**, and modules ship separately through the in-app catalog at **1.1.1**:

1. **`.NET 4.8 → .NET 10 (LTS)` migration — DONE, on `master`** (v1.1.0, framework-dependent, behavior parity).
2. **Plugin re-architecture (streams S1–S7) — IN PROGRESS** — turn the monolith into a **plugin host**; each
   capability becomes a module (own `AssemblyLoadContext`). **Done:** S1 host foundation (`DesktopPet.Contracts`
   ABI + loader + `PetHost`), S2 **Sound** module (NAudio out of the base), S3 part 1 **Fortunes** module
   boundary + a personalized Windows-username **welcome starter**, and **S3c** — the fortune **engine
   relocation**. **S3 is DONE + MERGED** (PRs #4/#5/#6): the Fortunes module is the live fortune source and
   the base is ONNX-free. **S4 (AI-brain module) — MERGED (PR #7):** the
   optional screen-commentary LLM now lives entirely in `modules/AiBrain` and owns the ask/hotkey/idle/drop
   flow; the base is runtime-disconnected (it never runs the brain). Off by default. The base's now-dead AI
   files + Options AI tab are removed in S5 (entangled with the AiSettings split), mirroring how S3d left the
   fortune UI/engine for S5. **S5 (WPF shell) + Pets features + the "B" audio arc — DONE + MERGED
   (PRs #8-25):** the WPF module-manager shell (Preferences/Pets/module panes; tray from contributions); Pets
   enrichment/bundle/check-for-new + per-pet **size** + per-pet **sound**; window 1050×820, OS-following theme,
   scroll + dark-scrollbar fixes; and the base now OWNS audio playback (host `AudioOutput`, **DirectSound**
   device picker + Test-sound button, **NAudio 3** back in the base — **WASAPI rejected** for a ~25 MB
   SDK-projection payload cost), which let the **S2 Sound module be retired**. **Next:** S5b-2(d) Fortunes
   pane → S5b-3 (FormOptions/FortunesWebView + WebView2 retired; About/Help now themed WPF windows) → S5c/d/e (AiSettings split + delete the
   residual base fortune/AI code + Options tabs + Newtonsoft→System.Text.Json) — **all DONE + MERGED.**
   **S6 phase 1 (bare host + in-app Modules catalog) — DONE + MERGED (PR #68, 2026-08-11), detail below.**
   **Next: S6 phase 2** (Pets becomes a module too, pre-installed by default) — full plan in
   [`S6P2-PETS-MODULE-PLAN.md`](S6P2-PETS-MODULE-PLAN.md), which folds in the old #16 (per-pet
   personality/voice). **S7 (third-party module code-signing + consent) is DROPPED (2026-08-13):** real
   signing isn't coming any time soon, and S6 phase 1's hash-pinning + permissions-consent already covers
   the in-catalog case — revisit if/when third-party signing is actually on the table. **TTS was DROPPED as
   a feature (2026-08-13):** not ready to build.

Full status, the expand/contract plan, and gotchas live in **[`handoff.md`](handoff.md)** and the
`project-desktoppet` memory note. **Feature item #9 below (Fortunes tab overhaul) is subsumed by this work**
— the fortunes UI is rebuilt in S5 (WPF, driven by the module's schema), not tweaked in place.

**S6 phase 1 — bare host + in-app Modules catalog — DONE + MERGED (PR #68, 2026-08-11).** Root problem:
neither the MSI nor the portable ZIP had ever shipped Fortunes/AiBrain — both ship the base pet engine only,
with modules only ever existing in raw dev/CI build output. Everyone who's downloaded a release got the base
engine, none of the actual product story. Original plan (`enchanted-sniffing-swing.md`) was to statically
bundle modules into the installer; discussed with the user and pivoted to something better — an in-app
**Modules** pane that fetches modules the same way pets/fortune packs already do (HTTPS + SHA-256-pinned
catalog fetch, user picks, downloaded on demand), which also quietly absorbs what was going to be a separate
later S7 stream ("signed catalog + consent") since a catalog that downloads and runs code needs hash-pinning
and a permissions-consent step regardless of when it's built.
- **`RemoteCatalog`** gains a third parallel list, `CatalogModule` (mirrors `CatalogPet`/`CatalogPack`
  exactly), carrying each module's declared `ModulePermissions` so the install prompt shows what a module
  *will* be able to do before its code is ever downloaded or run.
- **New Modules pane** (`ModulesPaneControl.cs`), fixed second in nav after Preferences. `OptionsShell.
  CollectPanes()` changed from load-order to: Preferences fixed first, Modules fixed second, then
  **everything else alphabetized** (Pets today, plus any module-contributed pane) — so install order never
  affects where a pane lands.
- **Modules only load at startup** (no hot-load — that was explicitly scoped OUT after discussion: a
  same-process reload would need to wire tray items/options panes/lifecycle events into an already-running
  app, real extra complexity for marginal UX gain over "restart and reopen where you were"), so install/
  uninstall restarts the app. This reuses `Program.cs`'s `RequestRestart`/`TryRequestRestartAfterSave`/
  `CompleteInstanceLifecycle`/`LaunchReplacement` chain — which existed, fully self-tested, with **zero real
  callers** until this PR. Threaded an optional `--reopen-options=<pane>` argument through the whole chain so
  the relaunch reopens Settings back on the Modules pane (not literally the same window instance — that
  would be hot-load — but a fast enough bounce + auto-reopen that it reads as continuous).
- **Real bug caught in live testing, not by any self-test:** the first live Uninstall attempt failed with
  "Access to the path 'Fortunes.dll' is denied" — a module's DLL is locked by the OS for as long as its
  `AssemblyLoadContext` is loaded in the current process, so deleting it immediately can never work. Fixed
  with `PendingModuleRemovals`: Uninstall marks the id (a small file under `AppPaths.DataRoot`, deliberately
  NOT inside `modules/` itself so it's never mistaken for a module by the loader's directory scan) and
  restarts; the *next* process deletes both the install folder and the module's data folder before
  `ModuleHost.LoadFrom` ever gets a chance to re-lock them.
- **Packaging:** `New-ModuleDistZip.ps1` (new) zips a module's build output — excluding `.pdb`/`.lib`,
  matching the base's own lean-manifest convention — into `modules-dist/<id>.zip`, the exact shape the
  install flow extracts straight into `modules/<id>/`. `modules-dist/modules.json` carries the catalog
  metadata (name/desc/version/permissions) a bare zip can't self-describe. `New-ContentCatalog.ps1` extended
  to emit a `modules` array in `catalog.json` alongside pets/packs, hashing each zip as the actually-committed
  git blob (sequencing matters here: the zip must be committed *before* regenerating the catalog, or the
  generator's CRLF-normalizing text fallback corrupts a binary hash — `*.zip -text` in `.gitattributes`
  already guarantees the committed bytes are exact, but only once they exist as a commit to hash).
- **Fully verified live, not just gated:** built and published Fortunes + AiBrain as the first two real
  catalog modules; the user ran the complete loop for real — Uninstall Fortunes (hit the DLL-lock bug, fixed
  it) → restart → confirmed gone → **Check for modules online** against the real published `catalog.json` →
  Fortunes surfaced as available → Install → restart → confirmed restored. User: "it works."
- **Not done (S6 phase 2, separate stream):** Pets becoming a module too (pre-installed by default, unlike
  Fortunes/AiBrain) — needs new `IHost` ABI verbs for spawn/remove/mix (today's multi-pet orchestration in
  `StartUp.cs` reaches `FormPet`/`PetTypeRegistry` directly, which a real module can't do), scoped at lower
  detail in the plan file since it's genuinely bigger than a file move.

**DROPPED (2026-08-13) — TTS / speech module.** Not ready to build; parked, revisit later if TTS becomes
worth it. *(Original note kept for reference:)* the "B" audio arc made the base own a shared
audio output (host-owned `AudioOutput`, DirectSound, device-selectable) and retired the S2 Sound module. A
future **text-to-speech module** can then speak calendar events / appointments through the same mixer,
ducking pet SFX. Needs its own plan: which TTS engine (local `System.Speech` / `Windows.Media.SpeechSynthesis`
vs a cloud/LLM TTS), what triggers it, and an ABI `Speak`/`PlaySound` host service so the module produces
audio through the shared output. Deferred per the user 2026-08-07 ("another module entirely").

---

## Status (2026-07-27)

- ✅ **Phase 1** — speech bubble (`FormSpeech`) shipped.
- ✅ **Phase 2** — Ollama brain (`dotNet/Ai/`): capture → OCR/vision → `/api/chat` → `{text,emotion}`.
- ✅ **Phase 3** — triggers: global hotkey (`Ctrl+Alt+P`), idle-commentary loop + gate.
- ✅ **2.8** — emotion → animation mapping (`FormPet.TryPlayAnimation` + `StartUp.EmoteAll`).
- ✅ **3.6** — "thinking" animation cue while the model responds.
- ✅ **Phase 4** — AI settings tab in the tray Options dialog (`src/Portable/FormOptions.cs`), applied live via `StartUp.ReloadAiSettings()`.
- ✅ Launch warmup + Ollama server auto-start.
- ✅ **Phase-1 Speech tab** ported into the compiled `src/Portable/FormOptions.cs` (`3c3393e`) + AI-aware greeting.
- ✅ **Phase 5** — context & memory (active-window + screen-zone, time-of-day, persona, rolling `chat-history.json`).
- ✅ **Phase 6** — vision path tested + fixed (routed hotkey-only, 896px image, timeout 120, sane defaults).
- ✅ **MIT license** for the fork's additions + **Phase 7.1 per-user WiX MSI** installer (`installer/`).

---

## Post-v1 backlog (added 2026-07-29)

Fortune Sheep is feature-complete — Phases **A–C** below all shipped (bundled corpus + poke-escalation,
offline bge-small **smart fortunes**, and the OpenAI-compatible multi-provider **AI brain** behind a
default-off master switch + tray Load/Unload + DPAPI keys). A pre-release **cleanup pass** landed
2026-07-29 (dead-code trim, correctness fixes incl. the sound self-mute, .NET 4.8 retarget, CI/release
workflows — see [`handoff.md`](handoff.md)). **v1.0.1 shipped 2026-08-04** via a lean hobby-grade CI
(the never-green enterprise gate/SBOM/signing/rights pipeline was stripped, ~50 scripts deleted);
releasing is now `git tag vX.Y.Z` (see [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md)).

- ✅ **DONE (1.4.6) — `modules/PetStudio` is PUBLISHED** (Pet Studio 1.1.0). Grew from a validator into a
  three-column authoring window: an editable XML pane (debounced re-analyze + atomic save) feeding
  preview/install, a colour-coded reachability map with clickable legend filters, and a detail panel rendering
  the selected animation's real sprite frames with playback plus its outgoing transitions. Its Open dialog
  defaults to the pet library (the additive `IPetManager.PetsDirectory` that moved the host version) and
  remembers the last folder browsed to. Blank transparent frames and orphaned-but-complete animations now
  explain themselves instead of looking broken. It still **source-links** the host's parser, validator and
  `AnimationReachability`; `--petstudio-selftest` pins that the module's verdict and `PetXmlValidator`'s agree
  on every fixture, and now also that the map's dead set equals `AnimationReachability.FindUnreachable`.

### Known ABI gaps (add when the module that needs them is written — see handoff.md's host contract)

- ✅ **DONE (v1.6.0) — a module can play audio, and can speak instead of showing a bubble.**
  `IHost.PlaySound(moduleId, audio, volume)` routes a WAV or MP3 container through the existing shared mixer
  and device, gated on the new `ModulePermissions.Audio`; `StopSound` cuts that module's own audio for
  barge-in (by ramping out over ~10 ms and returning short, so NAudio drops the input — muting would leave a
  silent input occupying the mixer for the rest of the utterance). `RegisterSpeechResponder` +
  `ModulePermissions.Voice` offer every line before its bubble is drawn, with **claiming and suppressing as
  two separate knobs** so bubble-only, bubble+voice and voice-instead-of-bubble are all expressible.
  A **container, not raw PCM**: a `float[]` would alias the mixer thread's read, and would freeze
  interleaving order and channel semantics into the ABI forever. `ModuleKit.WavAudio.FromPcm` covers engines
  that emit bare samples. Module audio never enters the decode cache (reference-keyed and never evicted, so
  caching speech would retain every line the pet ever spoke) — pinned by a source invariant that was itself
  negative-tested after the first version silently passed with the misuse injected.
  **Ducking is still not implemented**, but the per-owner input tracking this added is the groundwork; it
  changes how the app sounds, so it wants its own decision and a setting.
  *(Original gap below, for the reasoning.)*
- ~~📌 A module cannot play audio.~~ `IHost` exposes `Volume` read-only and no playback verb at all, while the
  base owns a full mixer (`src/dotNet/AudioOutput.cs`, DirectSound, per-sound volume + overlap, device picker).
  The engine's `<sound>` path reaches `AudioOutput` directly, which is what left `SoundData`/`SoundLoop`
  unreachable when the Sound module was retired. **A TTS/voice module — already a planned future module — is
  therefore impossible today.** Shape when it is needed: something like
  `bool PlaySound(string moduleId, byte[] audio, double volume)` routed to the existing mixer, gated on a new
  `ModulePermissions.Audio` so it shows up in the pre-install consent list. Small and additive; add it *with*
  that module rather than speculatively, and remember rule 3 (product bump in the same commit).
- 📌 **A module cannot draw on or near the pet.** No ABI for overlay/decoration. Nothing planned needs it yet;
  noted so it is not mistaken for an oversight if something does.

### Module SDK follow-ups

- ✅ **DONE (petstudio 1.1.1) — Pet Studio's window theme comes from `IHost.IsDarkTheme`, not the OS registry.**
  It used to read `AppsUseLightTheme` directly, which is right only while the host sits on its default "system"
  setting and wrong the moment a user pins the opposite — the host's actual preference was invisible to modules
  until `IHost.IsDarkTheme` landed in 1.4.7. `PetStudioTheme.Current()` now takes the `IHost` and asks it;
  a null host (or a host that throws) falls back to light, the same direction the host's own resolver fails in.
  The `DESKTOPPET_FORCE_THEME` env override went with it, because the settable `RecordingHost.IsDarkTheme` is a
  better version of what it was for: `--petstudio-selftest` now drives the theme **both ways** plus the no-host
  case, where before it asserted nothing about theming at all and its fake host hardcoded `IsDarkTheme => false`.
  **The one non-obvious edit:** `PetStudioWindow` built the theme in a *field initializer*, which runs before the
  constructor body assigns `_host` — so it had to move into the constructor. `MinHostVersion` 1.4.6 → **1.4.7**.

- ✅ **DONE (2026-08-18, fortunes + aibrain 1.1.2) — both modules now build on `DesktopPet.ModuleKit`.**
  Deleted 752 lines: the two byte-identical `CrossSessionLock`/`AtomicFile` copies, AiBrain's own
  `UnicodeTextProgress`, and three hand-rolled embedded-resource loaders. Net −820/+37.
  **Deliberately kept, so nobody "finishes" it later:** `FortuneProvider` still reads its corpus itself,
  because it decodes with *strict* UTF-8 (`throwOnInvalidBytes`) and distinguishes "resource missing" from
  "failed to parse", while `EmbeddedResources.LoadText` is deliberately lenient and returns `""` for both.
  The thin wrappers also stayed where the contract differs (`ReadEmbeddedText` returns **null**, not `""`,
  because its callers branch on null). Held back before only because a republish reaches existing users — and
  the repo has 0 stars, so that audience was hypothetical.
- ✅ **DONE — a committed leak soak for a module-owned window** (`tests\DesktopPet.WindowSoak` +
  `tests\module-window-soak.ps1`). `runtime-resource-soak.ps1` samples the shipped app from outside and its
  churn loop (`Program.RuntimeResourceChurn`) only drives pets/speech and the tray, so a module's window was
  covered by nothing; the soak that found the sprite re-decode bug existed only as prose in `handoff.md`.
  A separate `UseWPF` console exe (CoreTests is `UseWindowsForms`, so it could not live there) **loads the
  module DLL at runtime and reflects** — `PetStudioWindow` is `internal sealed`, so a compile-time reference
  would buy nothing, and leaving it out keeps the project free of build-order coupling. It reuses ModuleKit's
  `RecordingHost` rather than hand-rolling a fake that rots on every ABI addition, and a missing reflected
  member is a hard FAIL, never a skip. **Not in the blocking gate** (`run-gate.ps1:12-15` excludes leak soaks
  as too flaky for CI); it is a pre-tag step in `docs/RELEASE-CHECKLIST.md`.
  Current numbers for Pet Studio, 2 × 20 cycles: segment 2 handles +0, GDI +0, USER +0, private **−7.8 MB**.
  - **⚠ The trap that cost the most time here, worth knowing before writing any WeakReference leak test:**
    exactly one window per segment looked rooted, always the last one (cycle 7 of 8, cycle 19 of 20). It was
    not a leak and not `Application.MainWindow` — it was the strong reference *escaping the cycle method* and
    sitting in the caller's stack slot until overwritten. Fixed by having the cycle return a `WeakReference`
    rather than the window, and marking it `NoInlining`, so the only strong reference lives in a frame that is
    guaranteed to be torn down. A displacer window was tried first and did nothing; it was removed rather than
    left in place looking meaningful.
  - **Negative-tested, not assumed:** deliberately rooting each window in a static list makes it fail on two
    independent signals — all cycles rooted instead of none, and segment-2 private bytes **+31.4 MB** instead
    of −9 MB. Reporting *which* cycles are rooted is what separates a real leak (all of them) from the
    framework artifact above (only the last).
- 📌 **Third-party module ecosystem (Phase B).** Signing + per-publisher consent, a signed third-party index
  (or a curated links page first), and NuGet-publishing Contracts/ModuleKit/the template so a module can live
  outside this repo. Designed but deliberately unbuilt — see `docs/module-ecosystem-roadmap.md`, which also
  records the open questions and argues the cheap steps first.

### Bugs & maintenance

- ✅ **DONE (v1.5.0) — every pet on screen no longer speaks the same line at the same moment.** A reaction now
  belongs to ONE pet: the poked pet, the pet that landed, or the pet a drop was routed to (round-robin, because
  uniform random repeats the same pet often enough to read as still-broken). `Say(pet, …)` is the fix;
  per-pet *routing* (Tray → Pet Speech) is the feature built on top of it. Fortunes and AI Brain 1.2.0 carry
  the module half. Three things fell out of it that were invisible while everything broadcast:
  **`FormPet` knew which pet you clicked and threw it away** (the host recovered "a" pet, so poking pet #5 was
  reported as pet #1 to every module); **poke escalation was per-app** (poke Pearl three times then Rick, and
  Rick got the sass tier); and **`SayAll` spoke through authoring previews**, contradicting the documented
  previews-are-invisible invariant. The repeat guard moved into `FormPet.Say` because `IHost.Say` bypasses
  `SayAll` entirely — leaving it where it was would have silently killed the suppress-repeats preference.
  *(Original report below.)*
- ~~📌 OPEN (reported 2026-08-19) — every pet on screen speaks the SAME line at the SAME moment.~~ Reported as
  "when the same pet is chosen, it speaks at the same time, and the same saying", with the reporter's own
  hunch that it is probably *all* pets rather than only duplicates of one type. **That hunch is correct, and
  the cause is not subtle:** `StartUp.SayAll` (`src/dotNet/StartUp.cs:1152-1171`) takes one string and fans it
  out to every live pet in a single loop (`sheeps[i].Say(text)`), and essentially everything speaks through it
  — the base's poke sass (`:1357`), the tray's Test Speech (`ContextMenus.cs:224`), Fortunes
  (`FortunesModule.cs:166`) and the AI brain (`AiBrainModule.cs:701`). Nothing picks a pet, and nothing
  staggers. So four pets means four identical bubbles appearing simultaneously. Pet *type* is irrelevant.
  - Worth scoping deliberately rather than patching, because "what should happen instead" is a product
    question with at least three defensible answers: **one pet speaks** (chosen at random, or the poked one —
    already available via `IHost.Say(pet, text)`, which exists and bypasses `SayAll` entirely); **all speak but
    staggered** by a short jitter so it reads as chatter rather than a chorus; or **all speak but with
    different lines**, which is much bigger because the fortune/AI callers produce one string, not N.
  - Note `PetHost.RaisePokeReaction` already resolves *which* module answers a poke, and `PokeInfo.Pet`
    already carries the specific pet, so the plumbing to speak to just one pet is present and unused on this
    path — the fix is likely a caller change, not new ABI.
  - **Relates to #16 (per-pet personality/voice)** and to the Voice module: a voice engine must speak a
    broadcast line **once**, not once per pet, so whatever lands here should not assume one utterance equals
    one pet.
- ✅ **DONE — every tagged release was published by TWO racing workflows.** `release.yml` and
  `publish-release.yml` both triggered on `push: tags: v*`, both ran the whole build, and both did
  `gh release upload --clobber` against the same GitHub release. Whichever finished last won, so
  **`SHA256SUMS.txt` listed the author nupkgs or not depending on who lost the race** — every release was
  non-deterministic, and `publish-release.yml`'s own header comment asserted release.yml was
  "manual-dispatch only", which was factually wrong about its own sibling.
  Consolidated into `release.yml` (which already packed the nupkgs) and **deleted `publish-release.yml`**,
  folding in its two correctness properties: it checked out the **tag** rather than the default branch, and it
  **verified the tag against `ProductVersion.props`**. `release.yml` had neither — so a `workflow_dispatch`
  re-run built the default branch and published it under the tag's release, and a tag that disagreed with the
  product version was published without complaint. Added a `concurrency` group too, since two concurrent runs
  of the surviving workflow would reproduce the same clobbering.
- 📌 **`release.yml` still runs `microsoft/setup-msbuild`, which is vestigial.** `build.ps1:48-54` states it no
  longer probes MSBuild/VS, and the MSI is built by the `wix` dotnet tool, so nothing consumes it. Left in
  deliberately rather than removed in the same change: it costs seconds, and the release path is the wrong
  place to find out you were wrong about an implicit dependency. Drop it the next time the release workflow is
  touched for another reason.

- ✅ **DONE (2026-08-18, 1.4.8) — a module that fails to load is no longer invisible.** It used to count as
  installed (the pane enumerates folders), report no live version so no update was ever offered, and show
  "installed — restart to activate" forever, leaving Uninstall — which deletes the module's settings and API
  keys — as the only escape from a state the user did not cause. `ModuleHost.LoadFrom` already caught every
  failure and only logged it; all four early-return paths (no module DLL, no `IModule` type, `MinHostVersion`
  refusal, any exception) now record a `ModuleLoadFailure` surfaced through `StartUp.ModuleFailures`. The pane
  renders "failed to load — &lt;reason&gt;" with a **Reinstall** routed to the existing install flow, which
  replaces only the install folder and leaves module data alone. A `MinHostVersion` refusal is distinguished
  as "needs a newer app" with no Reinstall, because the module is fine and only updating the app helps.

- ✅ **DONE (2026-08-14, aibrain 1.1.1) — the AI brain read the screen through the ANSI codepage, so the pet
  quoted mojibake back at the user** (reported as a bubble sneering at `asÂ®`). `AiBrain.RunOcrAsync` set
  `RedirectStandardOutput` without `StandardOutputEncoding`; unset, .NET takes the encoding from
  `GetConsoleOutputCP()`, which is **0** in a GUI process with no console, and decodes codepage 0 as CP_ACP —
  the system ANSI codepage. Tesseract emits UTF-8, so `as®` (`61 73 C2 AE`) arrived as `asÂ®`, and with it
  `—`→`â€"`, `’`→`â€™`, `©`→`Â©`, `™`→`â„¢`, `é`→`Ã©`. Curly apostrophes are on nearly every page, so the brain
  had been fed corrupted context routinely, wasting tokens and occasionally derailing on the garbage; bytes
  with no CP1252 mapping landed on C1 control codepoints that `CleanOcr` then stripped, quietly losing
  characters too. Fixed by pinning lenient UTF-8 (a replacement char costs one glyph; strict would throw and
  blind the pet to the whole screen) on stdout AND stderr. **Windows' built-in OCR was never affected** (WinRT
  strings), so only Tesseract users ever saw it. Guarded three ways: a `®` in the `Test OCR` probe image with a
  red status on mis-decode, a `--aibrain-selftest` assertion on the psi factory (CI has no OCR engine, so the
  assertion is on the psi rather than a real run), and a repo-wide source guard in
  `tests\runtime-hardening-selftest.ps1` pairing every `RedirectStandardOutput` with an explicit encoding.
- ✅ **DONE (2026-08-14, 1.4.2) — installed modules could never be updated, only uninstalled.**
  `ModulesPaneControl.DiffNew()` diffed the catalog **by id**, so an installed module disappeared from the
  available list permanently: no version was compared, nothing checked at startup, and a module bugfix could
  reach only people who had not installed it yet. The lone workaround was Uninstall + reinstall, and uninstall
  deletes the module's data (settings, API keys, chat history). Now an installed row whose live `Info.Version`
  is older than the catalog's offers **"Update to vX.Y.Z"**, and `PendingModuleUpdates` applies it across a
  restart the way `PendingModuleRemovals` handles deletes (a loaded module's DLL is locked, so it cannot be
  overwritten in place): verified download → unpack into `<baseDir>\module-staging\<id>.staged` → marker in the
  data root → next launch swaps it in *before* `ModuleHost.LoadFrom`, then leaves the module's data directory
  untouched. Staging deliberately sits outside `modules\` (the loader loads every subdirectory it finds) and
  under `BaseDirectory` (same volume as the install, so the swap is a `Directory.Move`), and the swap moves the
  old copy aside first so a failed move rolls back instead of leaving no module at all. Unparseable versions
  offer nothing rather than guessing, and removals are processed first so an uninstall that raced an update
  wins. Covered by four new `--module-host-selftest` assertions on throwaway directories.
- ✅ **DONE (2026-08-14, 1.4.2) — the update check runs itself, once a month.** Requested right after the
  update path landed: an Update button nobody clicks "Check for modules online" to reveal is not much better
  than no update path. **A literal 1st-of-the-month alarm was deliberately NOT built** — a desktop pet is not
  always running on a given date, so that design silently skips any month the app happened to be off that day.
  Instead `ModuleUpdateSchedule` stores the month a check last SUCCEEDED (`yyyy-MM`, a marker file in the data
  root, not a settings field — machine state with no user meaning should not drag the settings schema and its
  migrations along) and a check becomes due as soon as the calendar month moves on: a pet started on the 5th
  having missed the 1st still checks, one left running a year checks twelve times. The month is stamped only
  after a **successful** fetch, so being offline costs nothing but a retry. A fresh install is seeded without
  checking (first check lands next month), and with no modules installed it stamps and skips the network
  entirely. `StartUp` evaluates two minutes after launch, then every six hours — that cadence is how it notices
  a month rolling over, NOT how often it hits the network. A hit shows a **tray notification** that opens
  Settings → Modules when clicked; nothing downloads or installs itself, so consent stays exactly where S6 put
  it. The version rule moved into a shared `ModuleUpdateScan` used by both the pane and the check, because a
  badge and a notification that disagree about what an update is would be worse than either alone. This is the
  only unprompted network request in the app, so it is a documented Preferences toggle (default on, absent in
  an older doc reads as on) and a new PRIVACY.md paragraph. 13 new self-test assertions cover the due-ness
  rule (same month, missed 1st, year boundary, clock moved backwards, unparseable stamp) and the version rule;
  a new CoreTests group pins the nullable-bool contract and the cross-process merge.
- ✅ **DONE (2026-08-13, v1.4.0) — release tags no longer collide with upstream's.** Upstream (Adrianotiger)
  tagged v1.2.3–v1.3.2 (2019–2021), so the fork's 1.2.x series ran into that range. Resolved by jumping the
  fork clear of it: the next release was cut as **v1.4.0** (not 1.2.4), so no tag collides. Releases continue
  from 1.4.x.
- ✅ **DONE (2026-08-13) — scrubbed a personal work email from the first 10 fork commits.** The fork's
  day-one commits (2026-06-24) were authored/committed under a work address rather than the project's
  `bigfnj` (personal identity) identity. Rewrote history with `git filter-repo --mailmap` to map those 10
  commits onto the `bigfnj` identity (0 commits now carry the old address; the HEAD tree stayed byte-identical,
  so no file content changed), then force-pushed master + the re-pointed release tags. Tracked file content was
  already clean. **Residual:** GitHub keeps the original commits in immutable `refs/pull/*/head` refs that a
  force-push can't remove — fully purging them needs a GitHub Support "remove sensitive data" request (or
  delete+recreate the repo). Left as a known, low-exposure residual.

- ✅ **DONE (2026-08-14) — S6p2 (Pets-as-a-module) built then fully REVERTED per user.** The whole stream
  (an `IPetManager` ABI + PetHost bridge, a `modules/Pets` plugin owning the Options→Pets pane + tray, per-row
  action buttons, per-type settings scoping, and a per-pet "voice" picker) shipped gated + pushed, but on the
  live eyeball the user disliked the module UI (lost tray icons, then the pane itself), so it was reverted to
  the pre-S6p2 state (`890f76d`). The original host Pets gallery + icon'd tray are restored. Design + code are
  preserved in git history (`feat(s6p2)` commits `53912a6`..`520aada`) if the direction is ever revisited.
- ✅ **DONE (2026-08-13, 1.4.1) — `DesktopPet.Contracts` FileVersion tracks the product** (`9009133`). A fixed
  `FileVersion=1.0.0.0` made a Windows Installer major upgrade SKIP refreshing the ABI dll whenever its content
  changed but the version didn't — shipping a stale Contracts.dll that couldn't resolve new ABI types (hit live
  during the S6p2 eyeball install). Now FileVersion follows the product; `AssemblyVersion` stays `1.0.0.0` (the
  ABI binding version modules reference). Latent fix — matters whenever the plugin ABI changes.
- ✅ **DONE (2026-08-13, v1.4.0) — the pet read its OWN window as screen context ("sheep jokes on a loop").**
  The primary pet form is titled "Sheep" and — unlike child sheep — carries no `WS_EX_NOACTIVATE`, so a poke
  (right-click) or drag ACTIVATES it, making "Sheep" the foreground window. The context-aware fortune picker
  then embedded "Sheep" as the on-screen context, which (verified against the live 37,857-line index) puts
  **24 of the top-32 candidates in the sheep/wool cluster** — so the same memorable sheep/knitting jokes
  recurred several times a day whenever the user touched the pet. Fixed in `ActiveWindow`
  (`src/dotNet/Ai/ActiveWindow.cs`): screen-context capture now ignores foreground windows owned by the pet's
  own process (blanks the title + drops its bounds), so the picker falls back to a plain random fortune. Fails
  open. Base change, no module republish. *(How it was found: parsed the live 62 MB vector cache + re-ran the
  bge-small query path in Python to prove the joke was NOT a generic hub — rank ~15,800/37,857 for normal
  titles — but jumped to top-32 only for a "Sheep"-dominated title.)*
- ✅ **DONE (2026-08-13, v1.4.0) — the Genres filter was a no-op for downloaded packs.** Every plain
  (untagged) pack line was hardcoded `Topic="life"/Genre="quip"`, so disabling "tv-quote" or "fact" removed
  nothing — the ~150 downloaded tv-*/fact packs were all "quip". New `FortuneClassifier.ClassifyGenre(source)`
  derives a taxonomy genre from the pack id (tv-* → tv-quote, *fact* → fact, limerick/songs-poems → verse,
  dadjokes/yo-mama/riddles → joke, else quip), used by `TryParsePlainContent`. Coarse but honest (a plain pack
  is homogeneous in delivery style). **Behavior note:** a user who had tv-quote/fact disabled now really loses
  those packs from the pool on update — the intended effect. Module change → fortunes.zip republished + catalog
  regenerated.
- ✅ **DONE (2026-08-12) — the Fortunes module never had its own corpus** (PR #72). The S3 move dropped
  the `fortunes.txt` EmbeddedResource from the base csproj with a comment saying it had moved to
  `modules/Fortunes` — but `Fortunes.csproj` never picked it up, so `EmbeddedCorpus()` failed into
  `_embeddedError` (a diagnostics string nothing reads) on every build since, and a lean install had
  nothing to say. 10,310 lines restored; 7 of its 26 sources exist in no pack file, so it was never
  duplicate content. The two self-tests that would have caught it — `SmartFortunes.SelfTest` and
  `ProgressiveSelfTest`, both of which fail on an empty pool — had been orphaned by the same move and
  are now wired (the latter to `--fortunes-smart-progress-selftest`, run in CI).
- ✅ **DONE (2026-08-12) — "Rebuild smart index" always claimed the pool was empty** (PR #72). `Warm()`
  starts a background task and leaves `ready=false`; `WarmProgress` gates `total` on `ready`, so `total`
  is 0 for exactly the moment after a rebuild — which is when the button asked. The status now takes
  pool size from the provider (known synchronously) and lets the index's counters answer only "how far
  along". An empty pool with packs installed now blames the filters instead of saying "add a pack".
- ✅ **DONE (2026-08-12) — a checkbox tick cost a full engine rebuild** (PR #72). Each pack/genre toggle
  wrote settings AND re-read every pack file and re-warmed the ONNX index, so the new group toggle meant
  19 of those back to back. New `ListCard.DeferChanges`: the box moves at once, the pane goes dirty, and
  `SetChecked` replays once per CHANGED item at Apply, so any number of ticks costs one write and one
  rebuild.
- ✅ **DONE (2026-08-12) — published module payloads silently rotted.** `modules-dist\<id>.zip` is a
  committed artifact the catalog serves and nothing rebuilds it automatically. Both modules had drifted
  (see the corpus bug above; aibrain.zip was a release behind PR #71's Windows OCR, so catalog installs
  had no screen reading without Tesseract). `packaging\Test-ModulePublishFreshness.ps1` now fails CI when
  `modules/<Id>/` has commits newer than the newest touching its zip. Git-based rather than
  rebuild-and-compare-hashes, because hash equality would require byte-identical module builds across
  SDK versions and checkout paths — a stronger promise than this repo makes. Markdown is excluded
  (`modules/Fortunes/BACKLOG.md` would otherwise demand a 31 MB republish for a note).
- ✅ **DONE (2026-08-05) — Oversized speech bubble under Remote Desktop.** `FormSpeech` measured its text
  box at `GetDpiForMonitor(anchor)` but painted with the window's own DC; under RDP those diverge (the
  session virtualizes DPI), so the box was reserved for a higher DPI than the text was drawn at. Now
  measures at the window's own DPI (`GetDpiForWindow`, == the paint DPI), re-checked each follow tick so a
  reconnect self-heals. Fixed at the physical console previously; this closes the RDP case.
- ✅ **DONE (2026-08-04) — Dark theme colorization.** Every white-on-dark offender fixed: `NumericUpDown`
  edit fills (`DarkNumericUpDown` answering `WM_CTLCOLOREDIT` with a dark brush), the owner-drawn left
  `TabControl` strip + gutter (`DarkTabControl` filling the client on `WM_ERASEBKGND`), combos/trees/lists/
  edits/scrollbars (`SetWindowTheme` dark styles), pet-card thumbnails on solid-dark, the restored eSheep
  icon, and muted-hint contrast. Follows the Windows light/dark setting. (En route, fixed a crash from an
  earlier `SetWindowTheme(" "," ")` theme-strip attempt.)
- ✅ **DONE (2026-08-04) — Codebase optimization after the security cleanup.** ~4,870 lines removed across
  a build-warning-clean, full-suite-green audit: dead methods/overloads, 2 orphaned source files, 4 unused
  framework references, 2 dead packaging scripts, two write-only `StartUp` clusters, and — the big one — the
  ~2,530-line embedded C# `FinalPathResolver` in `StagingPathSafety.ps1` collapsed to plain PowerShell
  (4235→645 lines) with all function contracts preserved, plus the dead MutationTestHook (~42 sites) + unused
  build params. Release pipeline re-validated end-to-end (deterministic ZIP + MSI + ICE + all self-tests).
  Deliberately kept: `#if !PORTABLE` dual-build branches, `src/legacy/` quarantine, the `OllamaClient` 8-arg
  test-seam ctor, `AppPaths.CatalogCacheDirectory`.
- ✅ **DONE (2026-08-04, v1.0.6) — Mimiko pet cannot be downloaded / applied — UTF-8 BOM.** `Pets/mimiko/animations.xml`
  begins with a UTF-8 BOM (`EF BB BF`) before `<?xml`; the other pets in its shared cat/fox set
  (neko / fox / pink_neko) don't. The download itself verifies (the catalog SHA-256 is taken over the
  BOM'd git blob, so it matches), but applying it throws **"There is an error in XML document (1, 1)"**
  because `PetXmlValidator.TryParse` passes the decoded string — leading `U+FEFF` intact — straight into
  `XmlSerializer.Deserialize(new StringReader(xml))` (`src/dotNet/PetXmlValidator.cs:399`). **Fix (robust,
  recommended):** strip a leading `﻿` (and any leading whitespace) before the `StringReader`, so any
  BOM'd pet or user-dropped `.xml` parses — protects every pet, not just Mimiko. **Alt (data-only):** re-save
  `Pets/mimiko/animations.xml` without a BOM and regenerate the catalog hash.

- ✅ **DONE (2026-08-10) — Post-conversion cleanup audit** (PRs #39/#40/#41/#42, ~9,400 lines net removed,
  build-warning-clean + full-suite-green, four gated buckets). After the plugin re-architecture, a
  dead-code/leak sweep: **#39** deleted the FormOptions-only `DarkTabControl`/`DarkNumericUpDown` (0 consumers)
  + `src/legacy/` (old net48/UWP tree, in no build) and fixed two `CancellationTokenSource` leaks
  (`AiBrainModule._lifetime`, `PetsPaneControl._netCts` — cancelled but never disposed); **#40** stripped the
  base's dead dumb-fortune engine (`FortuneProvider`/`FortuneFileImporter`, extracting the one live type
  `FortunePackLoadPolicy`), the `OptionsController` façade + Preferences/Fortunes/self-test seams (kept
  `PetsController`), and dead StartUp AI members; **#41** collapsed `ContextMenus.cs` to PORTABLE-only (removed
  the dead `#if !PORTABLE` UWP branches + `OpenOptionWindow` shim + first-boot no-op) and added a **FormHelp
  re-entry guard** (was modeless + undisposed); **#42** shipped the **ONNX Runtime license + notices** with the
  Fortunes module (it redistributes `onnxruntime.dll` but carried no license). **Deliberately NOT done — surfaced
  for a decision:** the base `src/dotNet/Ai/` AI cluster (~4,900 LOC) is dead-but-anchored and fully duplicated
  by the live `modules/AiBrain/engine/` copies; removing it is the planned **S5c/d/e "AiSettings split"** (it
  rips ~57 SecuritySelfTest references + needs a user-settings migration), and the full Newtonsoft→System.Text.Json
  drop is broader than the cluster (11 base files use Newtonsoft, only 5 in the cluster). Do the cluster removal
  as its own deliberate stream, not a "safe delete."
- ✅ **DONE (2026-08-11) — S5c: base AI-brain cluster removed ("AiSettings split")** (PRs #44/#45/#46,
  ~6.8k lines net removed, expand→contract, each PR gated green on CI). The dead base AI code
  (`src/dotNet/Ai/*` — a full duplicate of the live `modules/AiBrain` plugin) is gone. **#44** relocated
  the ~50 base-only AI **security** assertions into the module's `--aibrain-selftest` (`AiEngineProbe`) so
  they exercise the shipping engine — **zero coverage loss** (SSRF/endpoint reject, no-plaintext-key +
  DPAPI-failure ciphertext preservation, credential scoping, executable allow-list, response
  sanitize/bounds, deadlines, HTTP-retry, session-lifecycle races). **#45** rehomed the one non-AI setting,
  the random-drop cadence, into `settings.json` (`AppSettingsDocument` + `LocalData` + a self-contained
  one-time migration from the legacy `ai-settings.json`; new CoreTests group → 24). **#46** deleted the 12
  brain files + trimmed `StartUp` (retire machinery, `ApplyAiBrainState`, the uncalled `ClearAiHistory`;
  `InitAiTriggers`→`InitDropTriggers`) and `SecuritySelfTest` (AI methods + AI-only doubles), keeping
  `ActiveWindow`/`HotkeyListener` (host services), `PokeReactions`, `FortunePackLoadPolicy`, and every
  non-AI test. **Newtonsoft stays** — 6 non-AI base files still use it; the System.Text.Json migration is a
  separate, later pass (`AppSettingsStore` is still 100% Newtonsoft). Verified every phase: clean `-Release`
  (0 warnings) + all self-tests + CoreTests (24) + hardening ps1 + resource-churn soak, locally and on CI.
- ✅ **DONE (2026-08-11) — Newtonsoft.Json dropped product-wide + About/Help moved to WPF** (PRs #48/#49/#50/#51,
  each gated green on CI). Two cleanups to lean the base before feature work. **(1) Newtonsoft → in-box
  System.Text.Json, everywhere.** #48 migrated the 5 straightforward base files (Program marker, PetHost dict,
  LocalData legacy read, PackCollections + RemoteCatalog DOM) behind a new lenient `src/Portable/JsonRead.cs`
  (`Str`/`IntOrNull`/`BoolOrNull`, mirroring Newtonsoft's null-tolerant `JToken` casts). #49 did the gnarly
  `AppSettingsStore` (public **fields** need `IncludeFields=true`; `[JsonExtensionData]` field→`Dictionary<string,
  JsonElement>` property; `JToken.DeepClone`→`JsonElement.Clone`; kept default null handling so the nullable
  absent-vs-null settings survive; `UnsafeRelaxedJsonEscaping`+`WriteIndented`) and dropped the base + CoreTests
  Newtonsoft PackageReferences + packaging manifests. #50 migrated the **AiBrain module** engine (its stale-writer
  merge ported to `SerializeToNode` + `JsonNode.DeepEquals`/.NET 9 + `DeepClone`-before-reparent), preserving the
  DPAPI/credential-scope/no-plaintext-key invariants (`--aibrain-selftest` 80/0). **Result: zero `Newtonsoft.Json.dll`
  anywhere in the Release tree.** (The Fortunes module never used Newtonsoft.) **(2) About + Help → WPF.** #51
  rebuilt both as themed WPF windows on the existing shell (`OptionsShell.OpenAbout`/`OpenHelp` → `AboutWindow`/
  `HelpWindow`, `WpfTheme`), added a shared security-reviewed `src/Portable/WebLinks.cs` (relocated the About-link
  HTTPS validator + a github.com/bigfnj/desktopPet doc allowlist), rewired the tray, and **deleted the WinForms
  `AboutBox` + `FormHelp`**. So the only WinForms left is the pet engine (`FormPet`/`FormSpeech`) + the dev-only
  `FormDebug` console (kept). *(WebView2 + the old `FormOptions` were already retired earlier in S5b-3 — the
  cleanup only had to correct stale docs.)* **✅ Eyeballed 2026-08-19 — the window renders correctly.** Captured
  by rendering `AboutWindow` to a PNG from a throwaway harness (reflection over the `(author, title, version,
  info)` constructor → `RenderTargetBitmap` → `PngBitmapEncoder`). Confirmed: dark theme applied to background,
  text and chrome; all **six** allowlisted documentation links present and legible in the dark link colour
  (`#6CB6FF`); the modernization blurb, "Using DesktopPet" bullets and Original/Legacy credits all lay out
  correctly; Close anchored bottom-right. Content measured 524×581 inside the 560×640 window, so the pet-info
  card sits below the fold and scrolls, which is by design. *(Text looks slightly dim in a `RenderTargetBitmap`
  capture — grayscale antialiasing — and is crisp on screen; do not chase that as a bug.)*
  **Still worth a glance on the next reinstall:** the live tray → About / Help path on the installed MSI, and
  the light-theme variant (the capture followed this box's dark OS setting). The harness was **not committed**:
  a permanent render harness for one static window is not worth the machinery, and nothing else needs it.
  *(Correction, since the earlier wording sent readers hunting: there is no separate `HelpWindow`. Help was
  folded INTO `AboutWindow` and the tray entry is a single "About / Help". Nothing asserts this window —
  `--wpf-options-selftest` covers `CollectPanes` and `PaneView` only.)*

### Feature ideas (queued, not yet scoped)

1. ✅ **DONE (2026-08) — Fortunes-selection UX.** The flat source list is now a grouped `TreeView`
   (collection → sources, tri-state) with a filter box and a live "N of M sources · L lines" total; the
   fortune-packs section is a matching grouped download tree.
2. **AI-voice bundle** (one cohesive change — all touch `AiSettings` + `AiBrain.BuildSystemPrompt` + the AI tab):
   - **Personality presets** (~9 + Custom): a dropdown that writes a canned string into
     `AiSettings.Personality` (already injected into the system prompt in `AiBrain.BuildSystemPrompt`).
   - **Speech patterns** (pirate / l33t / rhyme / puns…): a *separate* "Speaking style" line appended to
     the system prompt (a structural constraint vs. personality = tone). Dropdown + Custom.
   - **Model-capability validation**: for Ollama, query `/api/show` capabilities to filter the Vision
     dropdown + assert on Test-connection; for generic `/v1` (no metadata) fall back to a name heuristic +
     a probe on Test. Never hard-block (let power users override).
3. **UI modernization** (Options looks dated). **Tier 1 SHIPPED + polished (2026-08)** — system-following
   dark title bar + fully dark controls (`WindowTheme.cs` + `DarkNumericUpDown` + `DarkTabControl`) on
   Options/About/Help, and the **Pets → Get more pets** gallery is now a 4-across grid of preview tiles
   (bundled icons, `PetThumbnails`) with aligned local-pet cards. Tier 2: Krypton Toolkit. Tier 3
   (superseded) — Options/About/Help are now native **WPF** windows (`src\Portable\Wpf\`), so the old
   WebView2/`FormOptions` HTML-settings-page idea is moot.
4. **Shimeji → animations.xml converter** (unlocks the huge Shimeji skin library). Best-effort, offline-
   first (convert → hand-check → commit to our `Pets/` mirror); ship the *converter*, not copies (IP). Hard
   part is behavior-tree → `<next>`-graph mapping; images + core states convert cleanly (~80% fidelity).
5. ✅ **DONE (2026-08) — Secure online pet + pack downloads, plus offline bundling.** Shipped a
   runtime-fetched, HTTPS-trusted `catalog.json` (per-asset SHA-256; `SecureDownload.TryValidateBranchRawGitHubUrl`;
   `src/dotNet/RemoteCatalog.cs` loader; `packaging/New-ContentCatalog.ps1` generator that hashes the committed
   git blob so hashes match what raw serves). The Options **Pets** tab is now a live gallery — built-in +
   bundled + downloaded pets, each validated via `PetXmlValidator` and installed to `<DataRoot>\pets`; the
   **Fortunes** packs section gained "Check online for new packs" (verify → import to CustomDir). New content
   pushed to the repo + a regenerated catalog appears live, no rebuild. Also shipped **offline bundling**: the
   portable ZIP carries all 22 pets + 12 fortune packs beside the exe (`AppPaths.Bundled*Directory`; deterministic
   ZIP `-ContentDirectories`; shared `packaging/Stage-BundledContent.ps1`), while the MSI stays lean and pulls
   on demand. Verified end-to-end including a live GitHub fetch (all 34 assets hash-match raw; app
   `--online-selftest` PASS). Diagnostics: `--catalog-selftest`, `--catalog-parse-file=<path>`, `--online-selftest`.
6. ✅ **DONE (2026-08) — Granular per-source fortune packs (grouped tree).** The 12 monolithic packs
   were split by their column-1 source tag into **152 per-source packs** (`packs/<source>.txt`), grouped
   by a new embedded `packs/collections.json` (12 named collections). Content is byte-identical to the
   originals (50,860 lines) and all 152 display names are curated. The embedded pack catalog was fully
   retired (`packs.json`, `TrustedPackCatalog`, and the per-pack rights gate) in favor of the runtime
   `catalog.json`; the Fortunes tab gained a grouped tri-state **download tree** (mirror of the Sources
   tree) so a user can expand a collection and check individual shows/authors, with per-file SHA-256
   verification on download.
7. ✅ **DONE (2026-08-05) — Multiple _different_ pets on screen at once** (phases ①+② + tray). A
   `PetTypeRegistry` (`src/dotNet/PetTypeRegistry.cs`) holds each loaded type's `(Xml, Animations)`
   with a reference count, disposed when its last pet closes; `StartUp` keeps the active/default type
   as before and spawns extra types via `AddSheep(string id)` (loaded on demand through the new shared
   `PetCatalog`). The on-screen mix persists in settings **schema v2** (`pets: [{id,count}]`, migrated
   from the single count) and is restored on launch; the tray gained **Add a pet ▸** / **Remove a pet ▸**
   submenus. Verified live (a seeded 2 + 2 mix restores 4 pets incl. an extra type) + a `PetTypeRegistry`
   self-test (CI) + 3 CoreTests groups (migration/validation/merge). Original notes below.

   **Multiple _different_ pets on screen at once** (queued 2026-08-04) — e.g. Pearl **and** Rick together,
   not just N copies of one pet. Today every instance shares one global animation set
   (`StartUp.animations`/`xml`); "Use this pet" swaps that set and reloads all sheep, and the active pet is
   persisted as a single `animations.xml` blob (`LocalData.GetXml`). **Key enabler:** `FormPet` already
   takes its `Animations`/`Xml` _per instance_ (`new FormPet(animations, xml)`) and children inherit their
   parent's set, so rendering, physics, window-climbing, child-spawning, fortunes, the AI brain, speech
   bubbles, and fullscreen handling are all pet-type-agnostic already. This is wiring, not a rewrite. Work:
   (a) a small **registry of loaded pet types** (each its own `Animations`+`Xml`, lazy-loaded, disposed when
   its last instance closes) replacing the single global pair; (b) an **"add alongside"** spawn path that
   leaves existing pets untouched (`AddSheep` already takes the set as a parameter); (c) **UI** — an
   "＋ Add" button beside "Use this pet" (Use = replace all, Add = add one), optionally an on-screen roster
   ("Pearl ×1, Rick ×2"); (d) **persistence** — save a _list_ of pet ids + counts instead of one XML, with a
   migration from the single-pet format; (e) decide **tray / auto-start** semantics ("Add new sheep" and the
   auto-start-N-pets setting both assume one type). Cost: each type loads its own sprite sheet (a few MB) —
   trivial for 2–3 types. **Phased plan:** ① runtime-only "Add" (the mix resets to one pet on restart) to
   prove the engine handles it — small, low risk; ② persist the mix across restarts (list format +
   migration); ③ polish — per-type add/remove, roster, counts. Relates to historical **7.4** (which framed
   multi-pet as per-pet AI personalities — a natural follow-on once types can coexist).
8. ✅ **DONE (2026-08-05) — Responsive local pet grid + Options default width.** The "your pets" list now
   flows into fixed-width cards, **2 columns by default, scaling to 3–4** as the window widens
   (`ApplyLocalPetColumns` in `src/Portable/FormOptions.cs`), and the Options window sizes itself so the
   widest tab (Pets) shows its default 2-column layout with **no horizontal scrollbar**
   (`FitLocalGridToTwoColumns`, measured at runtime). Original notes below.

   **Two-column local pet list** (queued 2026-08-04) — the top "your pets" list in **Options → Pets** is a
   single vertical column (`BuildPetGallery` adds each `BuildPetCard` straight into the TopDown
   `flowLayoutPanel1`), so with several pets downloaded it gets tall and scrolly. Show it as **2 columns**
   once there are enough pets. The "Get more pets" catalog grid already wraps multi-column via a
   `LeftToRight` + `WrapContents` panel, so reuse that pattern for the local cards. Watch: keep the
   "Use this pet" / "✓ Active" buttons aligned inside the narrower cards, and decide whether the built-in
   default spans full width or joins the grid.
9. ✅ **DONE (2026-08-12) — Fortunes tab clarity** (PR #72). Rescoped the same day it was built: the
   original entry was written against the old WinForms tab and asked for a "complete overhaul", but PRs
   #69/#70 had already delivered most of it (grouped collapsible collections + filter, browse → tick →
   download, curated names for all 152 packs, an import path for your own files). What actually shipped
   was the three specific remaining items, plus two things found while building:
   - **(a) Live pool count.** `SettingKind.Info` joined the ABI as a display-only field, backing an
     "N fortunes from M packs" line that turns into a warning at zero. Content + source filters are hard
     constraints by design, so an impossible combination used to stop the pet talking with nothing on
     screen explaining why (a live box was found sitting on `spicyOnly=true`, exactly the setting that
     does it).
   - **(b) One ordered Content level** — Clean / Clean + edgy / Everything / Spicy only — replacing
     `SpicyFortunes` + `SpicyTier` + `SpicyOnly` (16 combinations, several contradictory). The old names
     lied: "Edgy + NSFW" admitted general + edgy + nsfw, i.e. everything, and "True NSFW only" kept tame
     lines while dropping the edgy tier. **Filter profanity** stayed separate, being a word filter rather
     than a tier. Migration is a pure function (`FortunesModule.MigrateContentLevel`) so all five readings
     are asserted in `--fortunes-engine-selftest` rather than checked by hand.
   - **(c) "Show me 5 examples"** prints five lines the current selection would actually produce.
   - **Group-level toggles** on collapsible list-card headers. Switching off a section (19 NSFW packs) was
     19 clicks. The header checkbox drives the child checkboxes through their own toggles, so the card's
     `SetChecked` still fires per changed item and the module persists identically — covered by new
     `--wpf-options-selftest` assertions (tri-state, per-item `SetChecked`, other groups untouched).
   - **Pack ceiling 128 → 512.** 152 packs ship; the old cap silently dropped 24 of them, which is why a
     search for "Simpsons" came back empty.
10. ❌ **REMOVED (2026-08-13) — "About" tab showing the README.** Dropped per the user. Superseded anyway:
    About + Help are now themed WPF windows (`AboutWindow`/`HelpWindow`, PR #51), so a README-rendering
    Options tab is moot.
11. ✅ **DONE (2026-08-05) — Version stamp in the Options window (bottom-left).** A muted
    `v<Application.ProductVersion>` label anchored bottom-left in `FormOptions` (`BuildVersionStamp`),
    sourced from `ProductVersion.props` at build time. Original notes below.

    **Version stamp in the Options window (bottom-left)** (queued 2026-08-04) — show the running build's
    version (`Application.ProductVersion`, sourced from `ProductVersion.props`) in the bottom-left corner of
    Options so "which version am I running?" is answerable at a glance. **Directly prevents the stale-build
    confusion that cost real time this session** (the box was on v1.0.1 while fixes shipped in v1.0.2+).
    Cheap — one `Label`; pairs naturally with the About tab (#10).
12. ✅ **DONE (2026-08-11) — "Triumph" insult-comic personality.** Added a **"Triumph"** preset to
    `AiBrainModule.PersonalityPresets` (modeled on Triumph the Insult Comic Dog: mock-compliment then savage
    put-down of whatever's on screen + the user, with the "for me to POOP on!" catchphrase). It's a
    *personality* (tone), so it stacks with the existing *speech* styles — notably **Triumph + "Samuel"
    speech = a relentlessly profane roast**, exactly the requested combination. Opt-in (default persona
    unchanged); the system prompt already backs it ("commit fully… never merely polite"). One-line data add,
    gated green.
    - ✅ **DONE (2026-08-11) — "Jeff Ross" personality + strengthened the "Samuel" speech instruction.** User
      reported Samuel wasn't swearing enough even on an uncensored model (`dolphin3:latest`, already configured
      locally). Root cause wasn't a code filter — `SanitizeResponseText` only strips control chars/length, no
      lexical filtering anywhere in the AI-remark path (`NoProfanity` is a *fortunes*-corpus filter, unrelated).
      It was that `Personas.cs`'s Samuel instruction was an abstract style adjective ("swear hard with real,
      unfiltered profanity") — small local models under-follow abstract style asks, especially while also
      juggling the 15-word cap, in-character constraint, and strict JSON output in the same system prompt.
      Fixed by making it a concrete, checkable requirement: "work at least one real, strong curse word... into
      every single remark. A remark with zero profanity in it has failed." Also added a **"Jeff Ross"** preset
      to `PersonalityPresets` (the Roastmaster General: filthy, below-the-belt roast jokes mixing affection with
      savage personal put-downs, funny first/mean second), paired the same way Triumph pairs with Samuel speech.
      `--aibrain-selftest` still 93/93 (no assertion pins the exact wording). **⚠ Still needs a live smoke test**
      with dolphin3 to confirm the concrete-requirement phrasing actually lands more profanity than the old
      abstract phrasing did — not yet observed running.
    - **Correction (2026-08-11): the "gate hygiene bug" noted earlier this session was a false alarm, not a
      product bug.** I'd run `--wpf-options`/`--module-host`/`--hardening`/`--security`/`--fortunes` — every one
      missing the required `-selftest` suffix (`Program.cs` only recognizes `--wpf-options-selftest`,
      `--module-host-selftest`, `--hardening-selftest`, `--security-selftest`, `--fortunes-selftest`). An
      unrecognized flag falls straight through to normal app startup: the first bad call grabbed the app's free
      second-instance slot and launched a real second pet instance (the "orphaned process"); every call after
      that just hit the built-in "Application is already running! Only 2 instances are allowed." MessageBox and
      returned — none of the five self-test suites actually ran. Re-ran with the correct flag names: all five
      pass clean, exit 0, no dialogs, no lingering process. **No fix needed; the flags work correctly as-is.**
    - ✅ **DONE (2026-08-11) — raised the remark length cap: 1-2 sentences, ~20 words each (40 max), room for
      a roast's setup + knockdown.** The old cap ("under 15 words," one sentence) sat at the bottom of English
      readability's 15-20-word "sweet spot" (per readability research: 14 words ≈ 90% comprehension; 15-20 is
      the general-purpose recommended range; NIH plain-language guidance caps at 20) and left too little room
      for a full sentence, let alone a curse word + a specific on-screen detail + in-character voice all at
      once — a likely contributor to Samuel under-swearing (word-budget competition, see the entry above).
      `AiBrain.BuildSystemPrompt` (`AiBrain.cs:99`) now reads: "Keep it to one or two sentences, about 20 words
      each (40 words at most) — for a roast or insult-comic personality, a short setup followed by the
      knockdown lands well; otherwise one sentence is often enough." Verified no downstream truncation risk:
      the speech bubble (`FormSpeech.cs`) measures + auto-sizes on the actual text (no fixed height cap), and
      `MaximumResponseCharacters` (512) comfortably exceeds a 40-word remark (~250 chars). All 6 gates green
      (this time with the CORRECT `-selftest` flag names).
    - ✅ **DONE (2026-08-11) — "Samuel" speech pattern re-targeted at Jules Winnfield specifically.** User's
      live smoke test (Jeff Ross + Samuel speech) roasted hard but still leaned light on profanity; asked to
      push it further WITHOUT making profanity mandatory again (a walk-back from the "hard requirement" wording
      two entries up), and floated switching the reference from generic "Samuel L. Jackson" to his specific
      Pulp Fiction character, Jules Winnfield. Agreed — a specific, heavily-documented character is a much
      sharper style-transfer target for an LLM than a vague "sassy and swears" actor descriptor, and Jules's
      actual delivery (curses landing as rhetorical emphasis inside a half-sermon, half-threat cadence, not
      uniform density) is exactly the "lean hard, not mandatory" shape asked for. `Personas.SpeechPatterns`'s
      `Id = "samuel"` entry **kept its Id unchanged** (so this box's already-saved `SpeechPattern: "samuel"`
      keeps resolving with zero migration) but its `Name` → **"Jules Winnfield"** and `Instruction` rewritten:
      profanity framed as "your default reflex, not a checkbox to tick," reach for it whenever it lands harder
      than a clean word (most of the time, with this voice), but it doesn't have to land in literally every
      remark. Confirmed no code/self-test pins the old "Samuel" display string. `--aibrain-selftest` still
      93/93. Dev install refreshed. **⚠ Not yet observed live** — same open gap as the two entries above,
      reasoned + gated offline only; this is the one to actually eyeball next.
    - ✅ **DONE (2026-08-11) — merged Personality + Speech style into one curated "Disposition" dropdown**
      (schema v2→v3). User's insight: the two axes stacking freely let incoherent pairings through (e.g. "Shy
      and sweet" + "Jules Winnfield"), and a single well-known character per entry is a sharper style-transfer
      target than an abstract adjective blurb (already proven true by the Jules Winnfield rename above) — so
      curate ONE list where tone + delivery are baked into a single instruction per entry, never mixed
      incoherently. User supplied an initial 24-character list; researched several via web search to verify
      accuracy (Brittany Broski's actual mechanism is tangential rambling, not "sassy" — removed her; Jeff
      Dunham's Walter, Anthony Jeselnik's cold arrogant one-liners vs. Jeff Ross's warm roasts, DC's Etrigan's
      menacing rhyme confirmed via Ollama-docs-style source verification, not guessed). Iterated with the user
      to drop Triumph (redundant with Jeff Ross/Jules Winnfield) and Shakespeare/Monty Python (neither cleared
      the "is this actually funny/distinct" bar), and to add 4 more "VERY CLEAR" archetypes at the same bar as
      Beavis & Butthead: **The Dude**, **Drill Sergeant**, **Foghorn Leghorn** (verified his actual "I say, I
      say"/"boy" tics via search), and **A Proper Butler**. Final roster: **26 dispositions**
      (`modules/AiBrain/engine/Dispositions.cs`, new file, replaces the old `Personas.cs` — the dead
      `Presets`/`Preset` struct, confirmed zero references, was deleted rather than carried forward). Kept 7
      ids identical to the old speech-pattern ids they absorbed (samuel/pirate/leet/rhyme/pun/yoda/valley) so
      those specific migrate cleanly; the other 19 are fresh slugs. Default is **Ted Lasso** (closest in spirit
      to the old default blurb). `AiSettings.Disposition` (single field) replaces `Personality` (free-text
      blurb) + `SpeechPattern` (id) entirely — `MigrateDispositionFromV2` reads the retired `SpeechPattern` key
      out of `ExtensionData` (STJ routes an unmatched JSON key there once the field is gone from the class),
      carries it over verbatim when it names one of the 7 absorbed ids, else falls back to the default (the
      free-text Personality blurb can't be reliably reversed onto a curated id, so it's discarded, not
      consulted). `AiBrainModule`'s two pane fields ("personality"+"speechStyle") collapsed into one
      "disposition" Enum field; `AiBrain.BuildSystemPrompt` now emits one "Disposition:" clause instead of
      stacking "Your personality:"+"Speech style:". **Real tradeoff worth remembering: merging removes the
      ability to STACK a tone preset with a speech style** (e.g. the old "Triumph personality + Samuel speech"
      combo) — every disposition instruction had to become self-sufficient (Jeff Ross's now bakes in its own
      profanity-forward delivery directly, no longer leaning on a separately-selected Jules Winnfield speech).
      Migrated THIS box live: old doc had `Personality:"sassy, brash..."` + `SpeechPattern:"samuel"` → landed
      on `Disposition:"samuel"` (Jules Winnfield) post-migration, confirmed by reading the actual file after a
      refresh — Jeff Ross is no longer active and needs re-picking from the new dropdown if wanted (its own
      instruction now carries the profanity itself, no pairing needed). `--aibrain-selftest` 93→96 (+3: catalog
      knows/rejects an id + instruction non-empty, replacing the old persona/speech-pattern trio; +2 new
      migration assertions: a carried-over id migrates cleanly, a retired id — e.g. old "uwu"/"shakespeare" —
      falls back to default). All 6 gates green. **⚠ Not yet observed live** — same open gap as everything
      else in this AI-voice stream; the whole merged pane + the new dispositions (Drill Sergeant,
      Foghorn Leghorn, The Dude, Butler) are **smoke-tested live and working — user-confirmed 2026-08-13.**
    - ✅ **DONE (2026-08-11, post-v1.2.1) — removed the chat-memory feature entirely** (PR #65). User: "this
      caused issues, remove it," referring to `MemoryEnabled` ("Remember recent remarks") — the SAME setting
      whose self-reinforcing replay (feeding the model its own last remark back into its own prompt) caused
      the repetition-loop bug worked around earlier this project by turning it off (dolphin3 + memory OFF),
      never actually fixed. Rather than re-litigate that fix, removed the feature outright: deleted
      `ChatHistory.cs` (945 lines) + every setting/pane/self-test touchpoint; `AiSettings.MemoryEnabled` is
      gone (no migration needed — a stale key on an old doc is inert). Follow-up user catch: with the feature
      gone, the "Clear chat history" pane action had nothing left to clear — removed that too. Two self-tests
      had used `MemoryEnabled`/`ChatHistory` purely as convenient test fixtures (not testing memory itself);
      swapped to `UseVision` for the stale-writer-merge test and dropped the now-undefined `partitionA` clause
      from the credential-scope test (its still-valid plaintext-key assertions kept, renamed). Net -1,110
      lines. All 6 gates green.
    - ✅ **DONE (2026-08-11, post-v1.2.1) — the pet no longer forces the user's name into every remark**
      (PR #64). User: "I dont mind that it says my name but are you forcing it for every dialogue? ... just
      when it makes sense." `AiBrain.BuildSystemPrompt` literally said "Always address them as &lt;name&gt;" —
      a hard per-remark requirement. Softened to "use their name only when it actually fits the remark, not
      in every single one," keeping the existing guard against inventing a name or reading one off the screen
      (window titles/paths were previously mistaken for the user's name — that protection is untouched).
    - ✅ **DONE (2026-08-11, post-v1.2.1) — fixed two missing tray-menu icons** (PR #66). User screenshot
      showed "Add a pet", "Remove a pet", "Test Speech", "Disable AI", and "Ask about my screen" all missing
      icons next to Options/About/Close, which had them. Turned out to be two different things: **"Remove a
      pet" and "Test Speech" simply never had an `.Image` assignment** in `ContextMenus.cs` (confirmed via
      code read, then confirmed with the user that "Add a pet" DOES show its icon — only these two were
      genuinely blank) — fixed by reusing the same app icon "Add a pet" defaults to (`Resources.icon`) for
      Remove, and the pet glyph (`Resources.esheep`) for Test Speech. **"Disable AI" and "Ask about my
      screen" can't show an icon at all — not a bug, a real ABI gap:** they're module-contributed via
      `DesktopPet.Contracts.TrayItem` (`PluginApi.cs:73`), which has **no icon property whatsoever**. Fixing
      that means extending the plugin ABI (every module gets the capability, not just AiBrain) + picking real
      icon assets for AiBrain's tray items — queued as #15 below, not built.
    - ✅ **DONE (2026-08-11, post-v1.2.1) — #15 built same-day: every tray item now has its OWN distinct
      icon** (PR #67). User: "I dont want the 'same' icon repeated" — drew two new purpose-made icons (red
      prohibition sign for Remove a pet, speech bubble for Test Speech) instead of reusing Add-a-pet's/the
      pet glyph. For the module-contributed pair, actually built what #15 above queued: extended
      `TrayItem` (`PluginApi.cs`) with `byte[] IconPng` — raw PNG bytes, not a concrete image type, so the
      ABI stays framework-agnostic (no `System.Drawing`) per its own stated design goal — decoded host-side
      in `ContextMenus.BuildModuleMenuItem`. AiBrain ships a red X + tiny monitor as plain embedded resources
      (same pattern as Fortunes' `welcome.json`). **Real GDI+ gotcha hit and fixed:** decoding straight from
      a `MemoryStream` and disposing it immediately can throw "A generic error occurred in GDI+" later,
      since GDI+ can lazily reference the source stream — fixed by cloning into an independent `Bitmap`
      before the stream disposes. Also disposes the decoded module-icon Bitmap on every tray-menu rebuild
      (it rebuilds on each open) so repeat opens don't leak. Confirmed the icons actually embed
      (`GetManifestResourceNames()` on the built DLL) and confirmed live in the running dev install —
      user: "its perfect!"
    *(Original idea below.)* The AI-voice
    work this session shipped a **Personality** dropdown (12 canned presets incl. a profane **"Samuel"** =
    Samuel L. Jackson persona) and firm **Speech-style** patterns — both fed to `AiBrain.BuildSystemPrompt`
    (personality = tone, speech = delivery), so they **stack into emergent characters**. **Idea:** add
    insult-comic dispositions, starting with a **"Triumph"** personality modeled on *Triumph the Insult Comic
    Dog* — every remark a setup for a put-down ("…for me to poop on!"), roasting whatever's on screen and the
    user. Because personality and speech combine, e.g. **Triumph personality + "Samuel" speech = a relentlessly
    profane insult act that only roasts you.** Scope: one or two roast-oriented personality blurbs (and maybe a
    dedicated "insult everything" speech instruction); keep it opt-in (default persona stays friendly); note a
    small local model tends to soften the roast (per the dolphin-mistral-7B → dolphin3-8B testing this session).
    Build sites: `modules/AiBrain/AiBrainModule.cs` (`PersonalityPresets`) + `modules/AiBrain/engine/Personas.cs`
    (`SpeechPatterns`). Validates the persona blurb + speech-instruction prompt design.

13. ✅ **DONE (2026-08-11) — AI provider redesign: Local + Cloud coexist, cloud-primary with local fallback**
    (PRs #55/#56). `AiSettings` schema v1→v2: `Provider` is now the cloud selector `{""|openai|openrouter|
    custom}` and the local slot is the fixed `Endpoint`/`TextModel`/`VisionModel` (Ollama); new
    `CloudTextModel`/`CloudVisionModel`/`UseLocalFallback`. One-time migration preserves an old cloud user's
    scoped DPAPI key (scope hash unchanged). The AI Brain pane split into **Local provider** / **Local server
    (Ollama)** / **Cloud provider** (dropdown + endpoint + key + cloud models + consent) / **Fallback**. New
    `FallbackBackend` composite: a retryable cloud failure fails over once to the local Ollama model (mapped
    text/vision); a deterministic 4xx surfaces without falling over (shared `AiEndpointPolicy.IsRetryable`
    classifier). `--aibrain-selftest` 86 PASS/0 FAIL (migration + cloud-slot + 4 fallback assertions added;
    all credential-security assertions still green).
    - ✅ **DONE (2026-08-11) — fix: the local slot had been hardcoded to Ollama** (PR #58, user-caught during
      smoke-test). Before the redesign, `lmstudio`/`llamacpp` were valid local ids served by the generic
      OpenAI-compatible backend (llama.cpp/LM Studio speak the same `/v1` protocol); that was lost when the
      local slot became fixed-Ollama, and Ollama isn't bundled (confirmed — no `ollama.exe` in packaging),
      so a user without it installed had no local option at all. New `AiSettings.LocalBackendKind`
      (`"ollama"`|`"openai-compat"`, no schema bump — safe default for an absent key) +
      `AiBrainModule.BuildLocalBackend` picks the backend accordingly; a "Local backend" pane dropdown.
      `--aibrain-selftest` → 88 PASS/0 FAIL.
    - ✅ **DONE (2026-08-11) — capability-aware model dropdowns + uncensored tagging** (PR #60). The four
      model fields (local/cloud text+vision) are real dropdowns now, populated by two new "Refresh local/
      cloud models" pane actions. `OllamaClient.ListModelsAsync` reads `GET /api/tags`'s `"capabilities"`
      array (confirmed via Ollama's own docs — a genuine per-model signal on current servers, e.g.
      `["completion","vision"]`) for REAL vision detection; falls back to the (previously dead-code)
      `AiModelPolicy.LooksVisionCapable` heuristic when absent or for any generic `/v1` endpoint
      (`OpenAiCompatBackend.ListModelsAsync`, no capability metadata available). The vision dropdown only
      ever offers vision-flagged models. New `AiModelPolicy.LooksUncensored` (dolphin/uncensored/
      abliterated/unfiltered markers) tags and sorts matching models to the top of both dropdowns —
      **tagged, never hidden**, so other personas still see the full list; only Samuel/Triumph benefit from
      easy discovery. **Safety invariant (the design's load-bearing point):** the pane's Enum dropdown is a
      strictly closed, non-editable ComboBox — the currently-saved model is unconditionally unioned into
      every dropdown's options so opening+saving the pane can never silently blank a configured model, even
      before a refresh or with the model server unreachable. `--aibrain-selftest` 88→92 (0 FAIL). **✅
      Smoke-tested live and working — user-confirmed 2026-08-13:** the vision filter, the uncensored
      tag/sort, and the safety invariant all behave against a real Ollama instance.
    - ✅ **DONE (2026-08-11) — VRAM-size hint in the model label** (PR #62). User also asked for a "Browse"
      button to point a model field at an arbitrary local file. On the size ask: Ollama's `/api/tags` already
      carries a real `"size"` field (bytes on disk, a solid proxy for VRAM/weight footprint) that
      `ListModelsAsync` wasn't reading; added `ModelListing.SizeBytes` (long?, null for backends with no such
      metadata) + `JsonRead.Int64OrNull` + parsing in `OllamaClient.ListModelsAsync`. Labels now read
      `"4.9GB · dolphin3:8b · uncensored"` (size first — most scannable for "will it fit"). On Browse: **declined
      by the user after reconsidering** ("i think i mis-understood the 'browse' question, if ollama doesnt support
      custom pathing, then why are we adding it?") — Ollama can't be pointed at an un-imported file via chat
      requests at all (needs a `Modelfile` + `ollama create`, a real registration step, confirmed via Ollama's own
      docs, not a pointer), and a bare llama.cpp server's model is fixed at launch (`--model <path>`), not
      swappable per-request — so an "informational" file picker would only add a cosmetic, functionally-inert
      control. **No Browse button was built; don't re-propose one without this context** — `ollama pull` +
      the existing "Refresh models" action already cover real usage. Forced one design fix: a variable-length
      size PREFIX broke the old fixed-suffix label↔id strip trick, replaced with a proper `_modelIdByLabel`
      dictionary (`FormatModelLabel` registers label→id as a side effect; `ResolveModelId` looks it up on save;
      relies on the pane's Load-always-before-Save lifecycle). `--aibrain-selftest` 92→93. **The combined PR #60
      + #62 model-dropdown feature is smoke-tested live and working — user-confirmed 2026-08-13.**
      *(Original idea below.)* The AI Brain pane
    currently exposes one provider block. Rework into two: rename the existing block **"Local provider"**
    (Ollama/LM Studio on `localhost`), add a **"Cloud provider"** section (an OpenAI-compatible endpoint +
    DPAPI-encrypted key — the `OpenAiCompatBackend` already exists), and a **"use local provider as fallback"**
    toggle so a cloud failure/timeout falls back to the local model. Pairs with the existing "use cloud model"
    checkbox that swaps the model dropdown for a free-text field. Build site: `modules/AiBrain` (AiSettings +
    the pane schema in `AiBrainModule` + backend selection in `AiSessionManager`/`AiBrain`).
14. ✅ **DONE (2026-08-12) — screen reading works on a fresh box, via Windows' built-in OCR** (PR #71).
    The goal was "a fresh box has no OCR, so screen reading silently degrades." This entry proposed bundling
    a portable Tesseract; that was **not** what shipped, deliberately. Bundling, hosting, or CI-compiling
    Tesseract all make us the redistributor of a third-party binary — license notices, CVE patching duty,
    ~30 MB of download, and (for the compile route) a second heavy pipeline for Leptonica's dependency
    chain. Research also found there is **no official upstream Windows binary**: the de-facto one is a
    community NSIS *installer*, not a portable payload, so "just download it" would mean running an
    installer we can't hash-pin.
    **`Windows.Media.Ocr` ships with the OS.** A throwaway spike settled the three risks: it reads a probe
    image with no install step, it resolves inside the module's own collectible ALC, and the HOST does not
    need the projection (it travels with the module). Cost is **~6 MB compressed, in this module only** —
    the ~24 MB projection DLL is metadata-heavy and compresses ~4×, which is the detail the earlier WASAPI
    rejection (a ~25 MB *uncompressed* figure, against the base payload) obscured.
    Tesseract stays preferred (sharper on dense text): resolution is configured path → usual install
    locations → PATH → Windows built-in. **Test OCR names whichever engine answered** — a silent fallback
    would otherwise never tell the user the better engine exists — and a **"Get Tesseract…"** button opens
    the official guide; the standard installer lands where auto-detect already looks, so afterwards Test OCR
    just goes green with nothing to configure. Also added the **"Choose OCR engine…"** picker and an OCR
    path field (this entry claimed the picker had shipped; it had not).
    New ABI: **`IHost.OpenLink`**, gated on the calling module declaring `ModulePermissions.Network` and
    validated by the existing security-reviewed `WebLinks` HTTPS policy.
    **Caveat, verified against a no-WinRT control:** a WinRT-using module pins its collectible ALC, so it
    never unloads. Harmless — `Unload()` only runs at app shutdown or on load-failure paths, and module
    uninstall already forces a restart (`PendingModuleRemovals`). Noted in AiBrain.csproj beside the TFM.
    A self-test asserts Windows OCR reads a probe image **inside the module's load context** (skip-passing
    where the OS has no recognizer, e.g. a CI runner with no language pack), so the projection resolving
    under the real plugin loader is pinned, not just spike-verified.
15. ✅ **DONE (2026-08-11, same day) — extended the plugin ABI's `TrayItem` with an optional icon** (PR #67).
    `TrayItem` gained `byte[] IconPng` (raw bytes, not a concrete image type, keeping the ABI
    framework-agnostic); `ContextMenus.BuildModuleMenuItem` decodes it defensively. Any module can now ship
    a tray icon this way, not just AiBrain. Detail above in the AI-voice section (post-v1.2.1 tray-icon entry).
16. **Per-pet speech personality/preference** (queued 2026-08-11, unscoped, user's own caveat: "this may be
    complicated"). Today every on-screen pet shares the SAME global voice config — one `AiSettings.
    Disposition`, one (still-being-designed, not yet built) "Trigger Speech" source preference. The idea:
    let each pet TYPE carry its own — e.g. one sheep is AI Brain running the "Wednesday Addams" disposition,
    another is Fortunes tuned toward dad-joke-leaning packs, a third is AI Brain again but on "Jules
    Winnfield." Multi-pet-type coexistence already exists (`PetTypeRegistry`, backlog #7, DONE), so the
    on-screen mechanics for "more than one distinct pet at once" are already solved — what's NOT solved is
    that voice/personality config is a single global `AiSettings`/`FortuneSettings` blob, not keyed per pet
    type. Real complexity to scope later: (a) the AI brain's settings (disposition, model, provider) would
    need to become per-pet-type rather than one shared `AiSettings` document; (b) which pet a given
    poke/drop/AI-ask event is "for" already resolves through `IPet`/`PetHandle` in the ABI, so the plumbing
    to know WHICH pet triggered a reaction may already be there — needs verifying, not assuming; (c) whatever
    "Trigger Speech" setting design lands (still an open discussion as of this note) should be built with
    this in mind from the start — a global-only setting now that has to be retrofitted to per-pet later is
    much more painful than designing the storage key as pet-type-aware from day one, even if the UI stays
    global-only for its first cut.

17. **Tray audio recorder — one-click record everything in + out to MP3** (queued 2026-08-20, not yet
    scoped; came out of an audio-capture research pass). The want: click a tray item, it records the mic
    **and** system/loopback audio to a single MP3 (a meeting, a call), click again to stop. Filed here
    because desktopPet is already the .NET 10 tray app with the pieces to reuse — a tray-contribution ABI
    (`TrayItem`), a module loader with its own `AssemblyLoadContext`, an `Audio` permission, and **NAudio 3
    already in the base** for `AudioOutput`. Could ship as a module (`modules/Recorder`) or, honestly, as
    its own standalone tray app — a meeting recorder isn't "pet" behaviour, so decide that before building;
    the reuse argument is the tray/module/audio scaffolding, not a conceptual fit with a desktop pet.
    Real things to scope, not assume:
    - **Capture is two streams.** Mic = `WaveInEvent`/`WasapiCapture`; system output = `WasapiLoopbackCapture`
      (WASAPI loopback, no "Stereo Mix" needed). Mix to one file via a `MixingSampleProvider`, or record two
      tracks and mix on stop. **Watch the format mismatch** — loopback runs at the render device's rate/channels
      and the mic at its own; resample both to a common `WaveFormat` before mixing.
    - **The WASAPI payload question is already on file.** The base **rejected WASAPI for _playback_** over a
      ~25 MB SDK-projection payload cost (see the S5 note up top; DirectSound won, NAudio 3 stayed). Capture is
      the other direction — confirm whether NAudio 3's `WasapiLoopbackCapture`/`WasapiCapture` pull in that same
      projection cost before committing, since that was the deciding factor last time.
    - **Silence stalls loopback.** `WasapiLoopbackCapture.DataAvailable` doesn't fire while nothing is playing;
      the standard fix is to play silence through the device for the recording's duration.
    - **MP3 encoding.** NAudio can go WAV → MP3 via `MediaFoundationEncoder`, or shell out to the **ffmpeg
      already in the DevToolbox** (`WAV → -codec:a libmp3lame`). Record WAV, encode on stop.
    - **A new, more sensitive permission.** The existing `ModulePermissions.Audio` is for _playback_. Recording
      the user's mic + everything they hear is categorically different — a distinct capture/record permission
      with a **visible recording-in-progress indicator** (tray state), not a silent grant.
    - **Legal constraint, not a nicety.** The owner is in **California (all-party consent, Penal Code §632)** and
      records IEP meetings (FERPA) and work calls (client-confidential), so this must be **local-only** (no cloud
      upload path, ever) and should make "you are recording" obvious. This rules out the cloud note-taker design
      entirely and is a first-class requirement, not a later polish. Off-the-shelf alternatives evaluated in the
      same research: Meetily (local, OSS, pairs with the box's Ollama) and Bandicam (paid) — this item is the
      build-it-ourselves option.

    **Fuller vision (2026-08-20 discussion) — the pet as a record → transcribe → summarize orchestrator.**
    The real pitch isn't "a recorder that happens to live near a pet"; it's that the pet is the always-on
    interface and trigger, and on stop it runs a pipeline: capture → auto-transcribe to a file → optionally an
    AI-brain summary file. The pet framing is genuinely supported by the ABI, and it also gives a status surface
    a plain tray app doesn't — but two things in the code make "just use its AI brain" more than a wiring job:
    - **The pet-as-trigger part is real and already expressible.** `IHost.RegisterPokeResponder` /
      `RegisterPetPokeResponder` (poke the sheep to start/stop), `RegisterHotkey` (a global "record now" combo,
      Hotkey permission), and `AddTrayItems` (a tray entry) all exist today. And the pet earns its keep beyond a
      launcher: it already has **speech bubbles** (Speech/Voice) + **animations**, so it can show "🔴 recording",
      "transcribing", "summary ready" ambiently — that's the actual argument for doing this in the pet.
    - **FRICTION 1 (the important one): modules are isolated and there is NO summarize/LLM verb on `IHost`**
      (grep-verified across `PluginApi.cs` — the only "brain" mentions are comments; the AI brain is itself a
      MODULE that consumes host events, not a service other modules can call, and each module runs in its own
      `AssemblyLoadContext`). So a recorder module cannot hand a transcript to "the brain." Two clean paths:
      **(a)** the recorder carries its **own Ollama call to `localhost:11434`** (the box already runs it; AiBrain's
      `OllamaClient.cs` is the pattern) — self-contained, zero cross-module coupling, the right v1; or **(b)** add a
      host-level text-generation service to the ABI so one brain config serves every module — cleaner long-term but
      a deliberate ABI extension and a new "modules share a service" pattern. **Do not design assuming
      module→module calls; they don't exist.**
    - **FRICTION 2: there is NO speech-to-text anywhere in the repo** (grep-verified — the only "whisper" hits are
      fortune-pack text). Transcription is the biggest new dependency, bigger than capture or summary. Windows'
      built-in speech (what #14 used for OCR) is dictation-grade and weak on multi-speaker meeting audio, so
      realistically a Whisper-class engine (whisper.cpp / faster-whisper — also Meetily's choice), shipped as a
      model-beside-the-exe like the bundled bge-small ONNX.
      - *Browser Web Speech API — considered + REJECTED (2026-08-20).* Clever but wrong for this on three
        independent counts: (1) it transcribes a **live mic only**, not a file or the system-loopback stream, so
        it can't ingest the recorded mix or hear the far-end participants — disqualifying alone for a meeting
        recorder; (2) classic mode is **cloud (Google)** and `webkitSpeechRecognition` works only in
        Google-branded Chrome — Electron/WebView2 throw a `network` error because Google restricts the endpoint,
        so an embedded browser can't use it; (3) it would **re-add the WebView2 engine S5b-3 deliberately
        removed**. Chrome 139's on-device mode (`processLocally` + `install()` language packs, Aug 2025) fixes the
        cloud/privacy count but not the mic-only or browser-dependency counts, and was flaky at release. Native
        `Windows.Media.SpeechRecognition` (the OS STT twin of the #14 OCR pattern) is local + browser-free but
        dictation-grade and live/stream-oriented — weak on a long multi-speaker call. **Whisper-class on the
        recorded file stays the pick.**
    - **Phase it — four subsystems (trigger/UI, capture, STT, summarize), built in independently-useful slices:**
      **P1** poke/tray/hotkey → capture mic+system → MP3 + recording indicator (the item above);
      **P2** on stop → local Whisper → transcript file beside the MP3;
      **P3** → local Ollama → summary file. Ship P1 first; it proves the capture stack and is useful alone.
    - Everything stays **local-only** (CA all-party consent + FERPA) — no cloud STT or summary path, ever.

18. **Consolidate standalone tray utilities into pet modules — candidate evaluation** (2026-08-20, not
    scoped). The pet is an always-on tray host with a plugin ABI, so it's a natural home for the small
    single-purpose tray apps in this account. Three were assessed against the module model (in-proc .NET 10
    C# `IModule` in its own ALC, talking only to `IHost`; user surface = tray items + declarative pane, no
    self-shipped WinForms/WPF):
    - **LightHost (`bigfnj/LightHost`) — NOT a fit for the Microphone module.** It's a C++/JUCE realtime
      VST/VST3 *effects host* (routes device-in → plugin graph → device-out live); grep-confirmed it has
      **zero capture/record/encode code** — no `AudioFormatWriter`, no WAV/MP3, and it doesn't do
      system/loopback at all. Can't be an in-proc C# module (C++ app, no DLL/C ABI), and as a separate
      process it emits nothing to record. Also GPLv3 via bundled JUCE + VST SDK (would infect the MIT pet).
      Mic capture → **NAudio (already in the base)** does mic + WASAPI-loopback natively. Only revisit
      LightHost if realtime VST mic-cleanup (noise-suppression/EQ before transcription) ever becomes a hard
      requirement, and then as a separate GPL-isolated process, never in-proc. (Relates to #17.)
    - **blinkingLED (`bigfnj/blinkingLED`) — port-with-work.** Same stack. Blink `Forms.Timer` loop stays in
      the module; rate presets + on/off ms → declarative pane; enable/pause → a tray item. Its Win32
      (`SendInput` VK_SCROLL, `IsKeyLocked`) is plain P/Invoke, ALC-safe. Work = flip EXE→Library, drop
      `Program.Main`/single-instance/DPI (host owns those), strip self-shipped UI (icon-picker
      `OpenFileDialog`, uninstall `MessageBox`, balloons, Start-with-Windows reg key), and re-base
      "quit when Caps ON" → "pause when Caps ON" (a module can't quit the host). **No LICENSE file — add MIT
      before bundling.** Pet framing: the Scroll-Lock LED as a heartbeat tell ("I'm awake and watching").
    - **IdleLauncherTray (`bigfnj/IdleLauncherTray`) — port-with-work, but licensing gates it.** The idle
      engine (`PhysicalIdle`: global `WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks reading the `LLKHF_INJECTED` flag to
      tell physical input from `SendKeys`/automation, `GetTickCount64` monotonic timing, XInput gamepad poll,
      `GetLastInputInfo` fail-safe) is dependency-free P/Invoke and drops straight into a module timer; config
      → pane, target-file chooser → `IHost.PickFilesToOpen` (host owns the dialog). **Biggest technical care:
      the low-level hook is global but injection-free, so an ALC-loaded lib CAN install it on the host UI
      thread — but it MUST be `UnhookWindowsHookEx`'d in `Shutdown()` or an ALC unload leaks a dangling hook.**
      **Biggest blocker is licensing: it's GPLv2, the pet is MIT — relicense (bigfnj-owned) before any
      engineering.** Pet framing: the sheep sleeps after N genuine-idle minutes (not fooled by anti-idle
      jiggles), launches your target on wake/poke, and locks the PC when it closes.

    **Two cross-cutting findings (these matter more than any single port):**
    - **The permission enum needs new capability flags — and this gates #17 too.** `ModulePermissions`
      (Speech/Animation/ScreenContext/Network/Hotkey/Storage/Pets/Audio/Voice) has NO flag for what these
      modules actually do: audio **capture** (today's `Audio` is playback-only), **synthetic keyboard input**
      (blinkingLED), **global input monitoring** + **arbitrary process launch** (IdleLauncherTray). Modules run
      in-process at full privilege with no sandbox, so they'd all *work* — but the consent screen would
      silently under-disclose. If the pet becomes a utility suite, add flags along the lines of
      `AudioCapture` / `InputSynthesis` / `InputMonitoring` / `LaunchProcess` so consent stays honest. Likely
      its own work item; additive to the enum (safe).
    - **Licensing is a recurring gate.** Pet is MIT. LightHost = GPLv3 (from JUCE — *not* ours to relicense →
      don't bundle). IdleLauncherTray = GPLv2, blinkingLED = unlicensed — both bigfnj-owned, so both need a
      deliberate MIT relicense before shipping as modules.

    **Reusable port recipe (for any same-stack tray app):** keep the dependency-free engine, discard the
    WinForms shell (`Program`/`Main`/single-instance/`NotifyIcon`/`OpenFileDialog`/`MessageBox`/custom Forms),
    rebuild the surface as tray items + a declarative pane, and be disciplined about tearing down OS-global
    state on ALC unload (hooks, Scroll-Lock state, audio devices).

### Smart-fortune topic routing — ✅ DONE (2026-08-05)

The original note here was stale (it predated the taxonomy rework). Current, verified state:

- **Taxonomy already built out** (`FortuneTaxonomy`, schema v2, `TaxonomyVersion 2026-07-31`): 12 topics
  (tech/science/work-money/love/family/faith/society/food/nature/arts/health-body/life) × 12 genres
  (tv-quote/observation/joke/pun/quip/aphorism/wisdom/fact/insult/verse/dark/uplifting) × 3 levels. Every
  fortune is tagged on all three axes, enforced at parse time.
- **Corpus coverage measured** (embedded baseline = 10,310 entries): all 12 topics populated, skewed
  toward `life` (49%) with `family`/`health-body`/`love`/`faith` thin, but nothing empty — so **no
  re-tagging was needed**.
- **Router replaced with prototype-embedding routing** (`SmartFortunes.Router` + `RouteByContext`): the
  old hardcoded process-name→topic table (5 app families, only 6 of 12 topics) is gone. Now one short
  prototype sentence per topic is embedded at warm and the on-screen context routes to the nearest
  topic(s) by centered cosine similarity — reuses bge-small, covers all 12 topics, routes on the actual
  context not the exe name, no app list. Still a soft score bonus. Verified by `--smart-selftest`
  behavioral asserts (a code context → `tech`, a recipe context → `food`).

Possible future polish (not queued): rebalance the corpus toward the thin topics; tune the prototype
sentences; make the route bonus context-strength-weighted.

### Deferred audit items (low priority)

- ✅ **#17** — resolved: `src/app.config` carries no manual binding redirects (`AutoGenerateBindingRedirects` covers it).
- ✅ **#12 DONE (2026-08-05)** — `VectorCache.Save` now prunes proactively: with an active pool set it no
  longer backfills non-active on-disk keys, so the file stays near the active-pool size instead of drifting
  toward the 100k cap (active keys from other processes are still merged; the no-active-pool diagnostics
  path is unchanged). Verified by `--smart-selftest` (incl. the saturated disjoint active-pool case).
- ✅ **#15 DONE (2026-08-05)** — `AiBrain.ComputeSignature` now reads its 16×16 frame via a single `LockBits`
  + `Marshal.Copy` pass instead of 256 `GetPixel` calls (identical luma output; the `ComputeSignature`/
  `CaptureScreen(...,1280)` source invariants the hardening harness greps for are preserved).
- ✅ **Land greeting timing** — resolved: `StartUp.LandTimer_Tick` polls the pet's fall (250 ms) and speaks only once it settles (~0.5 s of no descent, ~10 s safety cap), not a fixed 3 s.
- ✅ **Sass lines** — expanded from a 12-line seed to ~35 in `Ai/PokeReactions.cs`.

---

## Historical planning archive (obsolete)

> Everything from this heading through the end of the file is retained as an implementation-history
> snapshot. Its unchecked boxes, paths, packaging assumptions, and "locked" decisions do **not**
> describe the current product or backlog. Use the post-v1 backlog above for feature ideas and
> [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md) for current release gates.

### Former "Fortune Sheep" v2 plan

The original full plan is in **[`FORTUNE-SHEEP-PLAN.md`](FORTUNE-SHEEP-PLAN.md)**. Its status snapshot
at the time was:

**✅ Phase A — DONE** (`243c085`, 2026-07-27): bundled corpus (`src/Fortunes/`, 13.7k SFW + 26.1k
Spicy, public domain), `Ai/FortuneProvider.cs`, right-click **poke-escalation** (1‑2 fortune → 3‑4
ignore/turn‑away → 5‑11 sass → 12 **bathtub escape**), land-fortune, Spicy toggle. The sheep is a
working, offline, zero-setup fortune machine.

**⬜ Phase B — contextual fortunes (the smart default).** In-process ONNX **bge‑small** embedder
(`Microsoft.ML.OnnxRuntime` + a BERT tokenizer, .NET 4.8), pre-computed corpus vectors (build-time),
embed the screen (active window + OCR) → **top‑k‑then‑random** match. Model delivered via **first-run
onboarding** (download‑now vs use‑random). ⚠️ **Validate the ONNX‑in‑single‑exe packaging FIRST** (native
runtime DLLs vs the embedded‑assembly trick) — the biggest risk in the plan.

**⬜ Phase C — AI insight tier + One Interface.** Replace `OllamaClient` with an `OpenAiCompatBackend`
(`/v1/chat/completions`) behind `IPetBrainBackend`; provider config/detection (none / Ollama / LM Studio
/ **OpenRouter** / **OpenAI**); **DPAPI‑encrypt** the cloud key; wire **insight into poke‑1** when a brain
is configured + "peek" on (Companion is the default preset). Vision routing (Phase 6) carries over.

**⬜ Phase D — presets + polish.** Fortune Teller / Companion / Quiet presets; idle ambient via the
embedder's semantic gate (replaces the luma gate); options‑tab pass (corpus, preset, idle freq, provider,
peek, model‑download button).

**⬜ Phase E — release.** Installer wiring for the first‑run model download; version bump; GitHub Release
with the MSI; README/grimoire updates.

**Open verification (eyeball):** the 12th‑poke **bathtub escape** and the **land fortune** are coded
(they reuse verified engine paths) but weren't cleanly screenshotted under automation — confirm by
spam‑clicking the sheep ~12×.

**Small TODOs discovered:** sass lines in `Ai/PokeReactions.cs` are a seed set (extend freely, or move to
a bundled `sass.txt`); the corpus is a first‑pass curation — refine SFW/Spicy anytime via
`src/Fortunes/build-corpus.sh`; land‑fortune fires ~3s post‑launch regardless of the actual landing moment.

**Deferred / backlog:** 6.4 PII scrubbing; 7.3 AI‑state pet art; 7.4 per‑pet AI; 7.5 .NET/WPF port.

---

## Historical Phase 1 — Speech Layer (no AI dependency)

Goal: get a speech bubble rendering on screen that tracks the pet. No LLM involved yet. Proves the rendering approach before wiring in the brain.

| # | Item | Notes |
|---|------|-------|
| 1.1 | **`FormSpeech.cs`** — borderless WinForms follow-window | Tracks `FormPet.Left/Top`; renders above the pet; transparent background |
| 1.2 | Speech bubble shape | Custom-painted rounded rect + tail pointer; no WPF, pure GDI+ `Graphics.FillPath` |
| 1.3 | Typewriter text effect | `Timer`-driven character reveal at ~30ms/char |
| 1.4 | Auto-dismiss | Bubble fades/closes after configurable N seconds (default 6s) |
| 1.5 | `FormPet` integration | `FormPet.Say(string text)` public method; wires up `FormSpeech` instance |
| 1.6 | "Test Speech" context menu item | Fires a hardcoded line to verify rendering |
| 1.7 | Multi-monitor positioning | Speech bubble stays on same screen as pet; clamp to working area |

---

## Historical Phase 2 — Ollama AI Brain

Goal: connect to a locally running Ollama instance and generate responses from screen context.

| # | Item | Notes |
|---|------|-------|
| 2.1 | **`OllamaClient.cs`** | `HttpClient` wrapper for `POST /api/chat`; streaming response via `ReadLineAsync`; configurable endpoint + model |
| 2.2 | **`AiBrain.cs`** — orchestrator | Owns the capture → OCR → prompt → response pipeline |
| 2.3 | Screen capture | `Graphics.CopyFromScreen` for full desktop; downscale to 1280×720 before sending |
| 2.4 | OCR text extraction | Shell out to `tesseract` exe; parse stdout; strip non-printable chars |
| 2.5 | Change detection gate | Frame diff (sum of pixel delta); skip LLM call if screen unchanged by > threshold |
| 2.6 | Prompt design | System prompt establishes pet persona + emotion vocabulary; user prompt = OCR text; optional base64 image for vision model |
| 2.7 | Response parsing | Expect JSON `{ "text": "...", "emotion": "happy" }`; fall back to plain text with neutral emotion |
| 2.8 | Emotion → animation mapping | Table: `happy→walk/jump`, `sad→fall`, `thinking→scratch`, `excited→run`, `confused→look-around`; map to animation IDs from `animations.xml` |
| 2.9 | Error handling | Ollama not running → pet stays silent (no crash); timeout 8s; retry once |

---

## Historical Phase 3 — Triggers

Goal: give the user explicit ways to invoke the AI, plus opt-in proactive behavior.

| # | Item | Notes |
|---|------|-------|
| 3.1 | **Global hotkey** — `RegisterHotKey` P/Invoke | Default `Ctrl+Alt+P`; configurable in settings; triggers reactive ask |
| 3.2 | "Ask [pet name]" context menu item | Same as hotkey but via right-click menu |
| 3.3 | Reactive ask flow | Capture screen → OCR → send to Ollama with "what do I see?" prompt → pet speaks + emotes |
| 3.4 | **Idle commentary loop** (opt-in) | Every 90–150s if screen changed meaningfully, pet makes an unprompted short remark |
| 3.5 | Idle gate | Skip idle commentary if `FormPet.State != Passive` or if last interaction < 30s ago |
| 3.6 | "Listening" animation | Trigger a named animation (e.g. `look`) while waiting for Ollama response; cancel on response |

---

## Historical Phase 4 — Configuration

Goal: make the AI layer configurable without recompiling.

| # | Item | Notes |
|---|------|-------|
| 4.1 | **`AiSettings.cs`** | JSON settings file in `%APPDATA%\DesktopPet\ai-settings.json`; persist on change |
| 4.2 | Ollama endpoint | Default `http://localhost:11434`; editable in options dialog |
| 4.3 | Model selector | Separate text model and vision model; populate from `GET /api/tags` response |
| 4.4 | Hotkey configuration | UI to remap the global hotkey |
| 4.5 | Idle commentary toggle | On/off + frequency slider (30s–300s) |
| 4.6 | Speech bubble style | Font size, display duration, max character width |
| 4.7 | Extend `FormOptions` | Add "AI" tab to the existing options dialog |

---

## Historical Phase 5 — Context & Memory

Goal: make the pet smarter about its surroundings and consistent across sessions.

| # | Item | Notes |
|---|------|-------|
| 5.1 | Active window title tracking | `GetForegroundWindow` + `GetWindowText`; include in prompt ("user is in VS Code") |
| 5.2 | Time-of-day persona | Morning/afternoon/evening tweaks to system prompt tone |
| 5.3 | Rolling conversation history | Last N exchanges kept in memory and included in Ollama context window |
| 5.4 | Persist history | Save/load from `%APPDATA%\DesktopPet\chat-history.json`; rolling 20-message window |
| 5.5 | Pet name personalization | `GhostConfig`-style JSON: pet name, user name, personality blurb → injected into system prompt |
| 5.6 | Screen zone awareness | Detect which app is under the pet (title bar overlap) and comment on it |

---

## Historical Phase 6 — Vision (optional upgrade path)

Goal: use a local vision-language model for richer screen understanding when the user wants it.

| # | Item | Notes |
|---|------|-------|
| 6.1 | Vision model toggle | Option to send a downscaled screenshot (base64) alongside the OCR text |
| 6.2 | Model routing | Text-only call for idle commentary; vision call only on hotkey ask (more expensive) |
| 6.3 | Recommended models | `llava`, `qwen2.5vl`, `moondream` — document in README |
| 6.4 | PII scrubbing | Blur/redact sensitive regions before sending (password fields, etc.) — P/Invoke `FindWindow` to identify input fields |

---

## Historical Phase 7 — Polish & Distribution

| # | Item | Notes |
|---|------|-------|
| 7.1 | Installer (NSIS or WiX) | Bundles the EXE; optionally bundles Ollama installer check |
| 7.2 | First-run onboarding | Detect if Ollama is not running; show setup dialog with model pull instructions |
| 7.3 | Custom pet XML for AI states | Add new animation IDs to `animations.xml` for AI-specific emotions (thinking, excited, confused) |
| 7.4 | Multiple pet support | AI brain per-pet; each pet has its own personality JSON |
| 7.5 | Upgrade path to .NET 10 WPF | Long-term option once physics engine is fully understood; not for v1 |

---

## Historical reference implementations

| Phase | Primary reference | Specific files |
|-------|------------------|---------------|
| 1 (speech) | bigfnj/Ghostpet-Prototype | `Controls/SpeakPanel.xaml`, `Controls/SpeakPanel.xaml.cs` |
| 2 (Ollama client) | — | `OllamaClient.cs` is greenfield; Ollama REST docs at `http://localhost:11434` |
| 2 (screen + OCR) | mediar-ai/screenpipe | `crates/screenpipe-vision` capture loop, change detection gate |
| 3 (reactions→animations) | alvinunreal/openpets | `src/reaction-animation-mapping.ts`, `src/local-ipc-protocol.ts` |
| 4 (settings) | bigfnj/Ghostpet-Prototype | `AppSettings.cs` |
| 5 (window tracking) | alvinunreal/openpets | `src/window-tracker.ts`, `src/terminal-focus.ts` |
| 6 (vision routing) | alvinunreal/openpets | `src/plugin-ai-gateway.ts` |

---

## Superseded decisions recorded by the original plan

- **Keep .NET Framework 4.8 WinForms** — the physics engine is deeply WinForms-native (Win32 P/Invoke, WinForms.Timer, ImageList). Porting to WPF would break the product without improving anything visible.
- **Ollama only (no cloud APIs)** — all inference runs locally. No API keys, no data leaves the machine.
- **AI layer is additive** — `FormPet.cs` and `Animations.cs` are not modified. The speech bubble and brain are separate classes that observe and call into the existing API.
- **Emotion hint is a string, not an enum** — keeps the prompt contract loose so new emotions can be added without recompiling.
