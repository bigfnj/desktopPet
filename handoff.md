# desktopPet AI Edition — Session Handoff

> Working notes for picking this up in a future session. Last updated: 2026-07-11.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet). Branch `master`, HEAD `47fb326`.
> There is also a persistent memory note: `project-desktoppet` in the auto-memory index — read it too.

---

## TL;DR of where we are

The original engine is a .NET Framework 4.8 (targets v4.7.2) WinForms desktop pet: XML-driven sprite
sheets, a probability-weighted animation state machine, gravity/border/taskbar physics via Win32
P/Invoke. **We do not modify the engine's behavior** — the AI work is an additive layer.

Done this session (all working, all verified live, **all uncommitted**):

- **Phase 1 (speech bubble)** was already in the fork; we fixed two things and added one:
  - Fixed the "first bubble always top-left" bug (`FormSpeech` now sets `StartPosition = Manual`).
  - Added **flip-below**: when there's no room above the pet, the bubble renders below it with the
    tail pointing up (`_tailOnTop`).
- **Phase 2 (Ollama brain)** — full spine, verified end-to-end (pet read the screen via OCR and spoke
  a context-accurate line). Backlog items 2.1–2.7 + 2.9.
- **Phase 3 (triggers)** — global hotkey (3.1), idle commentary loop + gate (3.4/3.5); 3.2/3.3 were
  covered by Phase 2. Verified: Ctrl+Alt+P fired an ask; idle loop spoke unprompted.
- **Launch warmup + server auto-start** (extra, user-requested) — on pet launch, a background task
  starts `ollama serve` if needed and preloads the active model. Verified: killed all Ollama, launched
  only the pet, server came up + model went resident.

Default build platform was changed **x86 → x64**.

## What's NOT done (pick from here)

1. **2.8 emotion→animation mapping** — the brain already parses `emotion` from the JSON and there's a
   `// TODO (backlog 2.8)` at the call site in `StartUp.AskAboutScreen`. Not wired to any animation.
2. **3.6 listening animation** — currently the "…" speech placeholder is the "thinking" cue. The
   animation-based version is deferred.
   - **2.8 and 3.6 share one blocker:** `FormPet.SetNewAnimation(int)` is **private**. The clean fix is
     a small additive public method on `FormPet`, e.g. `public bool TryPlayAnimation(string name)` that
     resolves a name→ID over `Animations.SheepAnimations` (a `Dictionary<int,TAnimation>`; each
     `TAnimation` has `.ID` and `.Name`) and calls `SetNewAnimation`. Then map emotions
     (happy/sad/thinking/excited/confused) to whatever animation names the esheep XML actually has.
     ⚠️ Verify the esheep `animations.xml` animation names before assuming "look"/"jump"/etc. exist.
3. **Phase 4 (config UI)** — expose the AI settings in the options dialog instead of hand-editing JSON.
   Existing pattern: the speech toggle/duration live in `dotNet/Portable/FormOptions.cs`
   (Phase 1). Add an "AI" tab there. `ContextMenus.RefreshSpeechMenuItem()` shows the live-toggle
   pattern for the tray items.
4. **Phases 5–7** — context/memory, vision routing, installer/onboarding. Not started.
5. **Vision path is built but untested** — `AiSettings.UseVision=true` sends a downscaled screenshot to
   `mistral-small3.1:24b`. Only the text+OCR path has been exercised. Worth a real test.

---

## Build & run

Toolchain is already on the machine — nothing to install.

- **MSBuild**: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
  (VS 2026 / v18). .NET Framework 4.8 targeting pack present.
- **NuGet**: there is no `nuget.exe`. The project uses `packages.config`. Restore via MSBuild:
  ```
  MSBuild DesktopPet_Portable.csproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir='D:\.claude\projects\desktopPet\src\'
  ```
  (Both flags required; plain `-t:restore` errors "No solution found".) Packages restore to `src\packages\`.
- **Build** (from `src/`):
  ```
  MSBuild DesktopPet_Portable.csproj -t:build -p:Configuration=Debug -p:SolutionDir='D:\.claude\projects\desktopPet\src\'
  ```
  - Default platform is now **x64**. AnyCPU is NOT a valid combo (errors "OutputPath not set").
  - Build the **.csproj directly, NOT the .sln** — the .sln drags in the UWP `OptionsWindow` project,
    which needs a UWP workload.
- **Output**: `build\DesktopPetPortable\bin\Debug\DesktopPet.exe` (AssemblyName is `DesktopPet`; the
  running process shows as `DesktopPet` and also `eSheep` in places).
- **Gotcha**: a running pet **locks the exe** → build fails on the copy step with MSB3027. Kill it first:
  `Get-Process DesktopPet,eSheep | Stop-Process -Force`.

Newtonsoft.Json is embedded and resolved at runtime via `Program.cs` `AssemblyResolve` — it works (the
AI JSON round-trips), don't be alarmed by the embed indirection.

---

## AI layer architecture (all additive)

New files, all under `src/dotNet/Ai/` (untracked — `git add` when committing):

| File | Role |
|------|------|
| `IPetBrainBackend.cs` | Provider seam: `ChatAsync`, `IsAvailableAsync`, `EnsureServerAsync`, `WarmUpAsync`. Lets a llama.cpp/llama-server backend drop in later without touching `AiBrain`. |
| `OllamaClient.cs` | The backend. `HttpClient` → Ollama native `POST /api/chat` (non-streaming, `format:json`, vision via `images` array). Also starts `ollama serve` and preloads models. |
| `AiBrain.cs` | Orchestrator: `AskAboutScreenAsync` (capture→OCR/image→chat→parse), `ScreenChanged` (idle gate), `PrepareAsync` (launch warmup). Returns null on any failure = pet stays silent. |
| `BrainResponse.cs` | DTOs: `BrainResponse{Text,Emotion}`, `ChatMessage`. |
| `AiSettings.cs` | JSON config (see below). |
| `HotkeyListener.cs` | `NativeWindow` + user32 `RegisterHotKey`; parses "Ctrl+Alt+P" → mods+vk. |

Wiring (modified engine/shell files — these ARE tracked, shown as `M` in git):

- `dotNet/StartUp.cs` — owns the AI layer. `InitAiTriggers()` (called at end of ctor, on the UI thread)
  loads settings, fires `PrepareAsync` in the background (`Task.Run`), registers the hotkey, arms the
  idle timer. `EnsureBrain()` lazily builds the brain. `AskAboutScreen()` is the single entry the
  hotkey/tray/idle all call; it shows "…", awaits the brain, marshals the answer back to the UI via
  `SayAll`. `IdleTimer_Tick`/`ScheduleIdle`/`AnyPetBusy` are the idle loop + gate. Disposes hotkey+timer+brain.
- `dotNet/ContextMenus.cs` — tray item "Ask about my screen" → `Program.Mainthread.AskAboutScreen()`.
- `dotNet/FormPet.cs` — added read-only `public bool IsBusy` (=IsDragging) for the idle gate; the
  flip-below `Say()` change (passes mouth top+bottom to `ShowSpeech`).
- `dotNet/FormSpeech.cs` — `StartPosition=Manual` fix + flip-below rendering.
- `DesktopPet_Portable.csproj` — x64 default + `<Compile Include>` entries for the 6 Ai files.

Data flow (text path): hotkey/tray/idle → `StartUp.AskAboutScreen` → `AiBrain.AskAboutScreenAsync` →
capture primary screen (downscale 1280w) → `tesseract` OCR → `OllamaClient.ChatAsync` (`/api/chat`,
`llama3.1:8b`, `format:json`) → parse `{text,emotion}` → `SayAll(text)`.

**Language level: C# 7.3** (old-style csproj default). No target-typed `new`, switch expressions,
records, init-only, or nullable refs.

---

## Config: `%APPDATA%\DesktopPet\ai-settings.json`

Auto-created with defaults on first run (`AiSettings.Load`). Fields + shipping defaults:

```json
{
  "Endpoint": "http://localhost:11434",
  "TextModel": "llama3.1:8b",
  "VisionModel": "mistral-small3.1:24b",
  "UseVision": false,
  "TimeoutSeconds": 60,
  "TesseractPath": "C:\\Users\\Admin\\AppData\\Local\\DevToolbox\\native\\tesseract\\tesseract.exe",
  "HotkeyEnabled": true,
  "Hotkey": "Ctrl+Alt+P",
  "IdleCommentaryEnabled": false,
  "IdleMinSeconds": 90,
  "IdleMaxSeconds": 150,
  "IdleChangeThresholdPercent": 5,
  "AutoStartServer": true,
  "WarmUpOnLaunch": true,
  "OllamaPath": ""
}
```

`TesseractPath` empty → uses `tesseract` on PATH. `OllamaPath` empty → autodetects
(`%LOCALAPPDATA%\Programs\Ollama\ollama.exe`, ProgramFiles, then PATH).

---

## Environment notes (this machine)

- **Ollama**: Windows-native at `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`, listens `localhost:11434`.
  The tray "ollama app" process alone did **not** bind the port — you need `ollama serve` running (the
  pet now does this automatically). Cold `ollama serve` takes **~20s** to bind on this box, so warmup
  finishes ~25-30s after launch; an ask in the first ~25s still hits cold start.
  - Models present: `llama3.1:8b` (text, fast), `mistral-small3.1:24b` (vision), `qwen3-coder:30b`,
    `qwen2.5:32b-instruct-q3_K_M`.
  - There is also a WSL Ollama (see the `reference_ollama_windows` memory) — both can't own :11434.
- **Tesseract**: 5.5.0 at `C:\Users\Admin\AppData\Local\DevToolbox\native\tesseract\tesseract.exe`
  (from the DevToolbox; tessdata beside it — code sets `TESSDATA_PREFIX` if a `tessdata` subdir exists).

## How to test (automation recipes that worked)

The pet is a small (~40x40) always-moving, layered, title-less top-level window — hard to click.
Patterns used (all via PowerShell + P/Invoke, screenshots saved to the scratchpad):

- **Trigger the ask without clicking the sprite**: use the **global hotkey** — send Ctrl+Alt+P via
  `keybd_event` (VK 0x11 Ctrl, 0x12 Alt, 0x50 P). Easiest deterministic trigger.
- **Clicking the pet**: enumerate the process's small visible window (`EnumWindows`+`GetWindowRect`,
  10<w<300), gate to on-screen (`top>=5`, not off the top during spawn-fall), then `SetCursorPos` +
  `mouse_event` right-click. Off-screen clicks during the spawn-fall hit other windows' system menus.
- **Verify server/warmup**: kill all `ollama`/`ollama app`, launch only the pet, then check
  `GET /api/tags` (up), `Get-NetTCPConnection -LocalPort 11434` (owner), `GET /api/ps` (resident model).
- **⚠️ Tool-timeout trap**: the Bash/PowerShell tools kill the whole process tree on timeout (2 min
  default), which **kills the launched pet**. Keep any single launch+poll command well under the
  timeout, or split "launch" and "check" into separate short commands so the pet survives between them.
- Always kill the pet before rebuilding (it locks the exe).

---

## Git / uncommitted state

Nothing from this session is committed. On `master` (HEAD `47fb326`):

- Modified (tracked): `DesktopPet_Portable.csproj`, `dotNet/ContextMenus.cs`, `dotNet/FormPet.cs`,
  `dotNet/FormSpeech.cs`, `dotNet/StartUp.cs`.
- Untracked: `src/dotNet/Ai/` (the 6 new files) — **must `git add`** when committing.
- Untracked runtime artifact: `src/DesktopPet.config` — the portable app's live settings store, written
  when it runs un-installed. Not source; consider adding to `.gitignore` (also `src/packages/` and the
  `build/` output if not already ignored).

Suggested first commit when resuming: stage the Ai folder + modified files, message something like
`feat(ai): phases 2-3 — Ollama brain, triggers, launch warmup`.
