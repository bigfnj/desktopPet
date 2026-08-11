# AI Desktop Pet — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

---

## ▶ Current major work — .NET 10 + plugin re-architecture (2026-08-06)

The active effort is **not** in the feature list below. Two things, both **unreleased** (last public release
is the v1.0.x line; the box runs a **v1.1.0 dev** build):

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
   residual base fortune/AI code + Options tabs + Newtonsoft→System.Text.Json); then S6 bare-host + package
   modules into the installer + MSI-bundles-pets (2.0.0), S7 signed catalog + consent. **TTS = its own future
   module** (backlog entry below).

Full status, the expand/contract plan, and gotchas live in **[`handoff.md`](handoff.md)** and the
`project-desktoppet` memory note. **Feature item #9 below (Fortunes tab overhaul) is subsumed by this work**
— the fortunes UI is rebuilt in S5 (WPF, driven by the module's schema), not tweaked in place.

**Backlogged — TTS / speech module (its own module, post-B):** the "B" audio arc made the base own a shared
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

### Bugs & maintenance

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
  cleanup only had to correct stale docs.)* **⚠ Open eyeball:** the WPF About/Help windows' visual rendering
  wasn't verified headlessly — confirm on the next reinstall (tray → About / Help: content, links, dark theme).

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
9. **Fortunes tab — complete overhaul** (queued 2026-08-04) — the whole **Options → Fortunes** screen and
   its settings want a redesign, not incremental tweaks. Current state to rework: the tone/level controls
   (Spicy tier + NSFW/edgy, No-profanity, Spicy-only), the grouped tri-state **Sources** `TreeView`
   (collections → sources) with its filter box and "N of M sources · L lines" total, the smart-fortunes
   toggle, and the matching grouped **download tree** for the 152 per-source packs. Open questions to scope
   first: layout / information architecture, clearer and less-jargony tone controls, pack discoverability,
   and a way to **preview what a selection actually sounds like** before committing. Needs a design pass
   (and a note from the user on what specifically feels off today) before it's built.
10. ⏸ **DEFERRED (2026-08-05) — the user is reconsidering the rendering approach** (bundled README →
    Markdown-to-RTF vs a curated panel vs WebView2). Requeued for a later session; specs below.

    **"About" tab in Options showing the README** (queued 2026-08-04) — add an **About** tab to the Options
    dialog (alongside Preferences / Pets / Fortunes / AI) that renders an easy-to-read, formatted version of
    the repo `Readme.md` (product blurb, what it does, links, credits/license). A standalone `AboutBox`
    already exists (`src/Portable/AboutBox.cs`, already reads `Application.ProductVersion`) — fold its
    content in or relocate it into the tab. Rendering choice to scope: the README is Markdown and WinForms
    doesn't render MD natively, so either (a) a `RichTextBox` fed a bundled RTF / light MD→RTF conversion,
    (b) a hand-laid-out panel of the key sections, or (c) WebView2 (ties to the Tier-3 UI idea) rendering
    the MD/HTML. Keep it fully offline — bundle the content, never fetch GitHub at runtime.
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
    gated green. *(Original idea below.)* The AI-voice
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
    - **Deferred — capability-aware model dropdowns** (queued 2026-08-11, unbuilt). Model fields (local text/
      vision, cloud text/vision) are still free-text. `AiModelPolicy.LooksVisionCapable(model)` already
      exists in `AiSettings.cs` as a name-based heuristic but is currently dead code — only exercised by
      `AiEngineProbe`, never wired into the pane. Real fix: for Ollama, fetch the installed model list (+
      capability metadata) via `/api/tags`/`/api/show` and populate/filter the dropdown for real; a generic
      `/v1` endpoint (cloud, or a local llama.cpp/LM Studio server) exposes no reliable capability metadata,
      so it falls back to the existing name heuristic. Real scope: a live model-list fetch per slot + two
      different capability-detection paths + pane wiring — bigger than a one-line add, sequence as its own
      pass. *(Original idea below.)* The AI Brain pane
    currently exposes one provider block. Rework into two: rename the existing block **"Local provider"**
    (Ollama/LM Studio on `localhost`), add a **"Cloud provider"** section (an OpenAI-compatible endpoint +
    DPAPI-encrypted key — the `OpenAiCompatBackend` already exists), and a **"use local provider as fallback"**
    toggle so a cloud failure/timeout falls back to the local model. Pairs with the existing "use cloud model"
    checkbox that swaps the model dropdown for a free-text field. Build site: `modules/AiBrain` (AiSettings +
    the pane schema in `AiBrainModule` + backend selection in `AiSessionManager`/`AiBrain`).
14. **Bundle a portable OCR engine in the AiBrain module + an engine picker** (queued 2026-08-10, unbuilt).
    OCR works today only when `TesseractPath` resolves (configured path → `%ProgramFiles%`/`%LOCALAPPDATA%`
    Tesseract-OCR → PATH); a fresh box has none, so screen-reading silently degrades. The **"Test OCR"** button
    (green/red) and a file-browser **"Choose OCR engine…"** picker shipped this session; the remaining work is
    to **bundle a portable Tesseract inside the AiBrain module package** (like the module already bundles ONNX)
    so it works out-of-the-box — no runtime auto-download. Ties into S6 packaging / the S7 module catalog.

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
