# AI Desktop Pet — Backlog

> Fork of Adrianotiger/desktopPet. The original physics experience is preserved, while compatibility,
> correctness, validation, and security fixes do modify engine files where required.

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
workflows — see [`handoff.md`](handoff.md)). **Release is held** pending the items below.
The authoritative public-release gates are in
[`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md), including unresolved redistribution rights.

### Feature ideas (queued, not yet scoped)

1. **Fortunes-selection UX.** The flat source `CheckedListBox` buckles as packs break into per-show
   sources. Replace with a grouped `TreeView` (pack → sources, tri-state checkboxes) + a filter box + a
   live "N sources / M lines" total; optionally merge the Sources + Packs boxes into one tree.
2. **AI-voice bundle** (one cohesive change — all touch `AiSettings` + `AiBrain.BuildSystemPrompt` + the AI tab):
   - **Personality presets** (~9 + Custom): a dropdown that writes a canned string into
     `AiSettings.Personality` (already injected into the system prompt in `AiBrain.BuildSystemPrompt`).
   - **Speech patterns** (pirate / l33t / rhyme / puns…): a *separate* "Speaking style" line appended to
     the system prompt (a structural constraint vs. personality = tone). Dropdown + Custom.
   - **Model-capability validation**: for Ollama, query `/api/show` capabilities to filter the Vision
     dropdown + assert on Test-connection; for generic `/v1` (no metadata) fall back to a name heuristic +
     a probe on Test. Never hard-block (let power users override).
3. **UI modernization** (Options looks dated). Tier 1 (pure WinForms, best ROI): system-theme detection +
   immersive dark title bar (`DwmSetWindowAttribute`) + flat controls + spacing. Tier 2: Krypton Toolkit.
   Tier 3: WebView2 HTML settings page (the commented `LoadWebViewPage` in `FormOptions` is a starting point).
4. **Shimeji → animations.xml converter** (unlocks the huge Shimeji skin library). Best-effort, offline-
   first (convert → hand-check → commit to our `Pets/` mirror); ship the *converter*, not copies (IP). Hard
   part is behavior-tree → `<next>`-graph mapping; images + core states convert cleanly (~80% fidelity).

### Expanded classifications — the routing fix + brainstorm

**Finding:** the bundled corpus is classified into only **`whimsy` + `wisdom`**, but the smart-fortune
**Router** (`SmartFortunes.Router`) routes foreground apps to 7 categories
(tech/wisdom/observations/whimsy/facts/work/creative). So 5 never match out-of-box until a matching pack
(e.g. Tech) is installed — routing is weak with no packs. This is **deferred Phase 3** of the cleanup, held
so the taxonomy can be decided first.

- **Two axes.** *Topic* (what it's about — drives app→category routing) and *tone/humor-type* (dad-joke,
  pun, one-liner, insult, wholesome, dark — orthogonal; partly captured by level/prof already). Expand
  topics to ~14–18: tech, work, wisdom/philosophy, science/facts, relationships/love, money/finance, food,
  gaming, music, movies/TV, health, nature/animals, history, art/creative, sports, education…
- **Prototype-embedding routing (elegant).** Instead of a hardcoded app→category table, embed one
  representative sentence per category once and route the screen context to its nearest prototype. Scales
  to any taxonomy for free, reuses the bundled bge-small, no app-name list to maintain.
- **How to (re)classify the corpus** at build time: (a) heuristic keyword lexicon (fast, noisy),
  (b) zero-shot via the bundled embedder — assign each fortune to its nearest category prototype (offline,
  consistent with the picker), or (c) local-LLM batch label via Ollama (best quality, one-time offline run
  over ~33k lines). Recommend **(b)/(c)** then switch the Router to prototype-embedding. The window *title*
  (already captured as context) is a richer routing signal than the process name.

### Deferred audit items (low priority)

- **#17** stale manual binding redirects in `src/app.config` (work today via `AutoGenerateBindingRedirects`).
- **#12** `VectorCache` grows unbounded (~60 MB worst case; add prune-to-active-pool).
- **#15** `AiBrain.ComputeSignature` uses `GetPixel` (negligible — only 16×16 px; real cost is the capture).

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
