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

Shipped, committed and pushed to `origin/master` (all verified live unless noted). Latest HEAD `243c085`.

- **Phases 1–6** — speech bubble (`FormSpeech`); Ollama brain (`dotNet/Ai/`: OCR + vision → `/api/chat`
  → `{text,emotion}`); triggers (hotkey `Ctrl+Alt+P` + idle loop); emotion→animation (2.8) +
  "thinking" cue (3.6); AI options tab (4) applied live; **context & memory** (5: active-window +
  screen-zone, time-of-day, persona, rolling `chat-history.json`); **vision tested + fixed** (6:
  routed hotkey-only, 896px image, `gemma3:4b` default, 120s timeout).
- **MIT `LICENSE`** for the fork's additions; **per-user WiX MSI installer** in `installer/` (7.1).
- **Fortune Sheep v2 — Phase A** — the current product direction (below).

The plan for what's next is **`FORTUNE-SHEEP-PLAN.md`**; the phase status + remaining work is in
**`BACKLOG.md`**. Build/run is now one command: **`.\build.ps1`** (`-Run`, `-Release`).

## The pivot: "Fortune Sheep" (v2) — current focus

We reframed the product to **cowsay | fortune, as a sheep**: smart contextual fortunes by default
(offline, no LLM), an opt-in AI-insight upgrade, poke-escalation personality. **Phase A is done.**

Code map (Phase A):
- **Corpus** — `src/Fortunes/fortunes-{sfw,spicy}.txt` (13.7k / 26.1k entries, `%`-delimited),
  **embedded** into the exe (csproj `EmbeddedResource`, exe now ~6.7MB). Rebuild/curate with
  `src/Fortunes/build-corpus.sh <clone-of-JKirchartz/fortunes>` (Unlicense/public-domain source).
- **`dotNet/Ai/FortuneProvider.cs`** — loads the embedded corpus (SFW default, Spicy via
  `AiSettings.SpicyFortunes`), random non-repeating `Pick()`.
- **`dotNet/Ai/PokeReactions.cs`** — the sass one-liners (plain list, easy to extend).
- **`StartUp.OnPetPoked`** — timing-based right-click escalation (7s pause resets): 1‑2 fortune /
  3‑4 ignore (turn-away anim, no bubble) / 5‑11 sass / 12 **bathtub escape**. Thresholds are named
  consts. `SayFortune` / `EnsureFortunes` / `landTimer` (land-fortune ~3s after launch) live here too.
- **`FormPet.EscapeToBath()`** — flee via the pet's own `bath*` spawn by re-running the engine's
  **public** `Play(bool, int forceSpawn)` against the spawn whose `.Next` animation name starts
  "bath" (esheep64: spawn id=3 → `batha`). Falls back to a fortune if the pet has no bath spawn.
- **Spicy toggle** — in `src/Portable/FormOptions.cs` (the AI tab). `AiSettings.SpicyFortunes`.

## What's NOT done (pick from here) — the Fortune Sheep build

In order (full detail in `BACKLOG.md`):

1. **Phase B — contextual fortunes (the smart default).** In-process ONNX **bge-small** embedder
   (`Microsoft.ML.OnnxRuntime` + a BERT tokenizer, .NET 4.8), pre-computed corpus vectors, embed the
   screen → **top-k-then-random** fortune match; model via **first-run download**. ⚠️ **Smoke-test
   ONNX-in-single-exe FIRST** — native runtime DLLs vs our embedded-assembly single-exe is the
   biggest risk in the plan. Fail fast before building the matching on top.
2. **Phase C — AI insight tier + One Interface.** Replace `OllamaClient` with an
   `OpenAiCompatBackend` (`/v1/chat/completions`) behind the existing `IPetBrainBackend` seam; provider
   config/detection (Ollama / LM Studio / OpenRouter / OpenAI); DPAPI-encrypt the cloud key; wire
   insight into **poke-1** (Companion default = peek on when a brain is present).
3. **Phases D–E** — presets (Fortune Teller / Companion / Quiet) + idle ambient (semantic gate) +
   options polish; release (installer first-run-download wiring, GitHub Release with the MSI).

**Eyeball TODO:** the 12th-poke **bathtub escape** and the **land fortune** are coded (verified engine
paths) but weren't cleanly auto-screenshotted (a browser modal stole the pokes; the pet sat at
screen-top) — spam-click the sheep ~12× to confirm.

**Older deferred:** 6.4 PII scrubbing; 7.3 AI-state pet art; 7.4 per-pet AI; 7.5 .NET/WPF port; the
`AiSettings.VisionModel` default is now `gemma3:4b` (the old `mistral-small3.1:24b` was an invalid tag).

---

## Gotchas & tooling notes (learned 2026-07-27)

- **WiX v6+/v7 require a PAID "OSMF" EULA** (`WIX7015` on any command). Use **WiX v5**:
  `dotnet tool install --global wix --version 5.0.2`. The UI extension **must match the version** —
  `wix extension add -g WixToolset.UI.wixext/5.0.2` (unversioned pulls v7 and errors as incompatible).
  The installer builds with `installer/build-installer.ps1` (per-user MSI, no admin).
- **PowerShell scripts on this box must be ASCII-only.** PS 5.1 reads script files as ANSI, so a
  UTF-8 em-dash (or any non-ASCII) inside a string breaks parsing (`Unexpected token`). Keep `.ps1`
  content ASCII (build.ps1, build-installer.ps1 already are).
- **Automating a right-click on the pet is flaky.** It's a ~40px always-moving top-level window;
  enumerate the pet process's *smallest* visible window and click its center. Stray clicks that miss
  land on whatever's behind (a browser modal `Save As` opened once and then ate every following
  click). Press `Esc` between clicks; even so, reaching the 12-poke bathtub escape reliably is hard —
  easiest to have a human spam-click to verify. (The hotkey `Ctrl+Alt+P` is still the clean way to
  trigger an AI ask without clicking the sprite.)
- **ONNX-in-single-exe (Phase B) is unproven here** — `Microsoft.ML.OnnxRuntime` ships **native**
  runtime DLLs; our app is a single self-contained exe via the embedded-assembly trick. Smoke-test
  the packaging before building contextual fortunes on top.

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
