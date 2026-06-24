# AI Desktop Pet — Backlog

> Fork of Adrianotiger/desktopPet. AI layer is additive — the original physics engine is never modified.

---

## Phase 1 — Speech Layer (no AI dependency)

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

## Phase 2 — Ollama AI Brain

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

## Phase 3 — Triggers

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

## Phase 4 — Configuration

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

## Phase 5 — Context & Memory

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

## Phase 6 — Vision (optional upgrade path)

Goal: use a local vision-language model for richer screen understanding when the user wants it.

| # | Item | Notes |
|---|------|-------|
| 6.1 | Vision model toggle | Option to send a downscaled screenshot (base64) alongside the OCR text |
| 6.2 | Model routing | Text-only call for idle commentary; vision call only on hotkey ask (more expensive) |
| 6.3 | Recommended models | `llava`, `qwen2.5vl`, `moondream` — document in README |
| 6.4 | PII scrubbing | Blur/redact sensitive regions before sending (password fields, etc.) — P/Invoke `FindWindow` to identify input fields |

---

## Phase 7 — Polish & Distribution

| # | Item | Notes |
|---|------|-------|
| 7.1 | Installer (NSIS or WiX) | Bundles the EXE; optionally bundles Ollama installer check |
| 7.2 | First-run onboarding | Detect if Ollama is not running; show setup dialog with model pull instructions |
| 7.3 | Custom pet XML for AI states | Add new animation IDs to `animations.xml` for AI-specific emotions (thinking, excited, confused) |
| 7.4 | Multiple pet support | AI brain per-pet; each pet has its own personality JSON |
| 7.5 | Upgrade path to .NET 10 WPF | Long-term option once physics engine is fully understood; not for v1 |

---

## Reference implementations to study before building each phase

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

## Decisions locked in

- **Keep .NET Framework 4.8 WinForms** — the physics engine is deeply WinForms-native (Win32 P/Invoke, WinForms.Timer, ImageList). Porting to WPF would break the product without improving anything visible.
- **Ollama only (no cloud APIs)** — all inference runs locally. No API keys, no data leaves the machine.
- **AI layer is additive** — `FormPet.cs` and `Animations.cs` are not modified. The speech bubble and brain are separate classes that observe and call into the existing API.
- **Emotion hint is a string, not an enum** — keeps the prompt contract loose so new emotions can be added without recompiling.
