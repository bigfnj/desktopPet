# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-07-27**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> Branch `master`, pushed to `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> There is also a persistent memory note `project-desktoppet` in the auto-memory index — read it too.

---

## Where we are

The original engine is a .NET Framework 4.8 (targets 4.7.2) WinForms desktop pet: XML-driven sprite
sheets, a probability-weighted animation state machine, gravity/border/taskbar physics via Win32
P/Invoke. **We never modify the engine's behavior** — the AI work is an additive layer.

Shipped and committed (all verified live):

- **Phase 1** — speech bubble (`FormSpeech`), follow-window, flip-below when no room above.
- **Phase 2** — Ollama brain (`dotNet/Ai/`): capture → OCR (or screenshot for vision) → `/api/chat`
  (`format:json`) → parse `{text, emotion}`.
- **Phase 3** — triggers: global hotkey `Ctrl+Alt+P`, opt-in idle-commentary loop + gate.
- **2.8** — emotion → animation mapping.
- **3.6** — "thinking" animation cue while the model responds.
- **Phase 4** — AI settings tab in the tray Options dialog, applied live on close.
- Launch warmup + Ollama server auto-start.

Commit series on `master` (2026-07-27): `cb400f7` phases 2-3 → `1a8ee91` 2.8+3.6 → `771abac` phase 4
→ (this session's cleanup/docs commit). All pushed to `origin/master`.

## What's NOT done (pick from here)

1. **Port the Phase-1 Speech tab into the compiled options file.** It currently does not exist in the
   running app (see the repo-layout gotcha below). Add a `BuildSpeechTab()` to
   `src/Portable/FormOptions.cs` mirroring the AI tab's `BuildAiTab()`; wire
   `Properties.Settings.Default.SpeechEnabled/SpeechDuration` + `ContextMenus.RefreshSpeechMenuItem()`.
2. **Vision path is built but untested.** `AiSettings.UseVision=true` sends a downscaled screenshot to
   `mistral-small3.1:24b`. Only the text+OCR path has been exercised live.
3. **Phases 5–7** — context/memory (window title, rolling history, persona JSON), vision routing,
   installer/onboarding. Not started.

---

## ⚠️ Repo layout gotcha (this cost a full debugging loop — read it)

The portable csproj **`src/DesktopPet_Portable.csproj`** compiles:
- the **engine** from `src/dotNet/*` (StartUp, FormPet, ContextMenus, FormSpeech, Animations, Program,
  Xml, ProcessIcon, EmbeddedAssembly, and `dotNet/Ai/*`), but
- the **tray dialogs** from **`src/Portable/*`** (FormOptions, AboutBox, FormHelp, Install, LocalData).

There **used to be** a stray, non-compiled duplicate tree at `src/dotNet/Portable/` (FormOptions,
AboutBox, FormHelp, Install). It was referenced by **no** project and has been **deleted**. Phase 1's
Speech tab and the first cut of Phase 4's AI tab were both mistakenly written into that dead copy,
which is why they never appeared. **Edit tray UI in `src/Portable/*`.**

`src/DesktopPet.csproj` (non-portable) compiles the `dotNet/*` engine but **none** of the option forms
— it's a different/secondary config; we build and ship the **portable** project.

---

## Build & run

Toolchain is already on the machine — nothing to install.

- **MSBuild**: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe` (VS 2026 / v18).
- **Restore** (packages.config, no `nuget.exe`) — both flags required:
  ```
  MSBuild DesktopPet_Portable.csproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir='D:\.claude\projects\desktopPet\src\'
  ```
- **Build** (from `src/`):
  ```
  MSBuild DesktopPet_Portable.csproj -t:build -p:Configuration=Debug -p:SolutionDir='D:\.claude\projects\desktopPet\src\'
  ```
  - Build the **.csproj directly, NOT the .sln** (the sln pulls in the UWP `OptionsWindow`, needs a UWP workload).
  - Default platform is **x64**. AnyCPU errors "OutputPath not set".
- **Output**: `build\DesktopPetPortable\bin\Debug\DesktopPet.exe`.
- **The running process is named `eSheep`** (AssemblyName is `DesktopPet`). It **locks the exe**, so kill
  it before rebuilding — and note the name:
  ```
  Get-Process -Name eSheep,DesktopPet -ErrorAction SilentlyContinue | Stop-Process -Force
  ```
  ⚠️ Do **not** use `-ErrorAction Stop` here: "DesktopPet" usually has no match, which throws and skips
  the kill, leaving the exe locked (build then fails with MSB3027).

Newtonsoft.Json is embedded and resolved at runtime via `Program.cs` `AssemblyResolve`. It works; the
`Newtonsoft.Json.Linq` types used by the AI code and the options model-dropdowns round-trip fine.

**Language level: C# 7.3** (old-style csproj default). No target-typed `new`, switch expressions,
records, init-only, or nullable refs.

---

## AI layer architecture (all additive)

`src/dotNet/Ai/`:

| File | Role |
|------|------|
| `IPetBrainBackend.cs` | Provider seam: `ChatAsync`, `IsAvailableAsync`, `EnsureServerAsync`, `WarmUpAsync`. |
| `OllamaClient.cs` | Backend. `HttpClient` → Ollama `POST /api/chat` (non-streaming, `format:json`, vision via `images`). Starts `ollama serve`, preloads models. |
| `AiBrain.cs` | Orchestrator: `AskAboutScreenAsync` (capture→OCR/image→chat→parse), `ScreenChanged` (idle gate), `PrepareAsync` (warmup). Returns null on any failure = pet stays silent. |
| `BrainResponse.cs` | DTOs: `BrainResponse{Text,Emotion}`, `ChatMessage`. |
| `AiSettings.cs` | JSON config (`%APPDATA%\DesktopPet\ai-settings.json`). `internal sealed`. |
| `HotkeyListener.cs` | `NativeWindow` + user32 `RegisterHotKey`; `TryParse("Ctrl+Alt+P")`. |

Wiring in the engine/shell:

- `dotNet/StartUp.cs` — owns the AI layer. `InitAiTriggers()` (ctor, UI thread) loads settings, fires
  `PrepareAsync` on a background task, then `ApplyAiTriggers()`. `AskAboutScreen()` is the single entry
  the hotkey/tray/idle call: shows "…", awaits the brain, marshals the answer back via `SayAll`, emotes
  via `EmoteAll`. **`ReloadAiSettings()` (public)** re-applies settings live (called by the options
  dialog on close): reload JSON, dispose+null the cached brain, `ApplyAiTriggers()`. `EmoteAll` /
  `EmotionAnimations` map the emotion hint to animations.
- `dotNet/FormPet.cs` — `public bool IsBusy` (idle gate) and **`public bool TryPlayAnimation(string name)`**
  (2.8/3.6): case-insensitive name→ID lookup over `Animations.SheepAnimations`, calls the private
  `SetNewAnimation`. No-op/false if the pet's XML lacks that animation. **UI thread only.**
- `dotNet/ContextMenus.cs` — tray "Ask about my screen" → `Program.Mainthread.AskAboutScreen()`.
- `src/Portable/FormOptions.cs` — the **AI** tab (`BuildAiTab`), model dropdowns from `GET /api/tags`.

Text-path data flow: hotkey/tray/idle → `StartUp.AskAboutScreen` → `AiBrain.AskAboutScreenAsync` →
capture primary screen (downscale 1280w) → `tesseract` OCR → `OllamaClient.ChatAsync`
(`llama3.1:8b`, `format:json`) → parse `{text,emotion}` → `EmoteAll(emotion)` + `SayAll(text)`.

### Emotion → animation map (default eSheep names)

`StartUp.EmotionAnimations` returns a prioritized candidate list per emotion; `TryPlayAnimation` plays
the first the pet's XML defines (else nothing). Verified against `Pets/esheep64/animations.xml`:

- happy → `flower`, `jump`, `boing`
- excited → `run`, `jump`, `boing`
- sad → `sleep1a`, `sleep2a`
- thinking → `sleep1a` (also the 3.6 "waiting" cue)
- confused → `rotate1a`, `boing`
- neutral / unknown → nothing (pet keeps roaming)

⚠️ Animation names differ per pet. Non-eSheep pets simply fall through — that's intentional.

---

## Config: `%APPDATA%\DesktopPet\ai-settings.json`

Auto-created with defaults on first run (`AiSettings.Load`). Editable in-app via the **AI** tab (saved +
applied live on dialog close). Fields + shipping defaults:

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

- **Ollama**: Windows-native at `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`, `localhost:11434`, runs as a
  service (models incl. `llama3.1:8b`, `mistral-small3.1:24b`). Cold `ollama serve` takes ~20s to bind;
  warmup finishes ~25-30s after launch, so an ask in the first ~25s can hit cold start.
- **Tesseract**: 5.5.0 at `C:\Users\Admin\AppData\Local\DevToolbox\native\tesseract\tesseract.exe`.

## Testing recipes that worked

The pet is a small (~40x40), always-moving, title-less layered window — hard to click.

- **Trigger an ask without clicking the sprite**: send the global hotkey `Ctrl+Alt+P` via `keybd_event`
  (VK 0x11 Ctrl, 0x12 Alt, 0x50 P). Most deterministic trigger.
- **Detect the speech bubble**: enumerate the `eSheep` process's visible top-level windows; a ~220x58
  window appearing above the ~40x40 pet window == a bubble is showing.
- **Options dialog is tray-only** — the pet's own right-click just shows a greeting. Opening Options
  from automation means driving the Win11 tray (unreliable); easiest to have a human open
  tray → Options → **AI** tab.
- **⚠️ Tool-timeout trap**: the Bash/PowerShell tools kill the whole process tree on timeout, which
  kills the launched pet. Keep launch+poll well under the timeout, or split "launch" and "check" into
  separate short commands so the pet survives between them.
