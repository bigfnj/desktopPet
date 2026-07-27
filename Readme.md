# desktopPet — AI Edition (WIP)

> **Fork of [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet)**  
> Active development branch: `master` | Status: **Phases 1–4 shipped** (speech, Ollama brain, triggers, emotion→animation, AI options tab)

This fork keeps the original physics-driven WinForms animation engine intact and layers a local-LLM AI brain on top of it — screen awareness, speech bubbles, and reactive behavior driven by [Ollama](https://ollama.ai/) running locally on your machine.

**What works today:** point the pet at your screen with the global hotkey (`Ctrl+Alt+P`) or the tray's "Ask about my screen"; it OCRs the screen (or sends a screenshot in vision mode), asks Ollama, speaks a short remark in a bubble, and plays an animation matching the emotion. An opt-in idle loop makes occasional unprompted remarks. Everything is configurable from the **AI** tab in the tray Options dialog. See [`BACKLOG.md`](BACKLOG.md) for the full phase status and what's next.

---

## What the original engine gives us (don't break this)

The upstream project is a complete XML-driven desktop pet runtime:

- **Sprite sheet rendering** via a Magenta-keyed WS_EX_LAYERED WinForms window — no compositing, no WPF, pure GDI
- **State machine animations** — every animation is a node with probability-weighted next-states, gravity detection, and border collision
- **Physics** — the pet walks, falls, climbs window title bars, and sits on the taskbar via `EnumWindows` P/Invoke
- **Multiple pets** — each defined by a self-contained `animations.xml` (sprite sheet + audio embedded as base64)
- **Child pets** — an animation can spawn a second pet as a child
- **NAudio sound** — MP3 playback keyed to animation IDs

**None of this will be touched.** The AI layer is purely additive.

---

## What we're building on top

```
┌─────────────────────────────────────────────┐
│           FormPet (original, untouched)      │
│   Physics · Gravity · Border · Sprites       │
└──────────────────┬──────────────────────────┘
                   │  SetNewAnimation(id)
                   │  FormPet.Left / .Top
                   ▼
┌─────────────────────────────────────────────┐
│              FormSpeech  (new)               │
│   Borderless follow-window · Speech bubble   │
│   Typewriter text · Auto-dismiss             │
└──────────────────┬──────────────────────────┘
                   │  Say(text, emotionHint)
                   ▼
┌─────────────────────────────────────────────┐
│              AiBrain  (new)                  │
│   Screen capture (BitBlt)                    │
│   OCR (Tesseract)                            │
│   Ollama API  →  text + emotion              │
│   Change detection (frame diff)              │
└─────────────────────────────────────────────┘
```

The AI brain emits two things per call:
- A **text response** → rendered in the speech bubble
- An **emotion hint** (`happy`, `sad`, `thinking`, `excited`, `confused`) → mapped to a named animation ID

---

## Tech stack

| Layer | Technology | Notes |
|-------|-----------|-------|
| Pet engine | .NET Framework 4.8 WinForms | Unchanged from upstream |
| Speech bubble | WinForms borderless form | Tracks `FormPet` position |
| Screen capture | `Graphics.CopyFromScreen` | Built-in, no extra deps |
| OCR | Tesseract 5 (exe via process) | Already in devtoolbox |
| LLM inference | Ollama local API (`/api/chat`) | `http://localhost:11434` |
| Vision model | `llava` / `qwen2.5vl` | For hotkey "what do I see?" |
| Text model | `llama3.2` / `qwen2.5` | For idle commentary (faster) |
| JSON | `Newtonsoft.Json` | Already a dependency |
| HTTP | `System.Net.Http.HttpClient` | Built-in |

---

## Reference projects

These were studied before a line was written here:

| Project | What we took from it |
|---------|---------------------|
| [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet) | The animation engine itself — this fork |
| [alvinunreal/openpets](https://github.com/alvinunreal/openpets) | Reaction→animation mapping pattern, IPC protocol design, agent integration model, speech bubble arbiter |
| [bigfnj/Ghostpet-Prototype](https://github.com/bigfnj/Ghostpet-Prototype) | Speech panel WPF approach, behavior JSON state machine, idle loop design |
| [mediar-ai/screenpipe](https://github.com/mediar-ai/screenpipe) | Screen capture + OCR + LLM loop architecture, change detection gate |

---

## Backlog

See [`BACKLOG.md`](BACKLOG.md) for the full feature backlog with phases and priorities.

---

## Building

Requires Visual Studio (2022+) with the .NET Framework 4.8 targeting pack.

Build the **portable** project directly — **not** the `.sln` (the solution drags in the UWP
`OptionsWindow` project, which needs a UWP workload):

```powershell
MSBuild src\DesktopPet_Portable.csproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir="<repo>\src\"
MSBuild src\DesktopPet_Portable.csproj -t:build   -p:Configuration=Debug -p:SolutionDir="<repo>\src\"
```

- Default platform is **x64** (AnyCPU is not a valid combo — it errors "OutputPath not set").
- Output: `build\DesktopPetPortable\bin\Debug\DesktopPet.exe`.
- The running process is named **`eSheep`** and it **locks the exe** — kill it before rebuilding:
  `Get-Process eSheep,DesktopPet -EA SilentlyContinue | Stop-Process -Force`.

> ⚠️ The portable csproj compiles the engine from `src/dotNet/*` but the tray dialogs
> (FormOptions, AboutBox, FormHelp, Install) from **`src/Portable/*`**. Edit the options UI in
> `src/Portable/FormOptions.cs`.

The AI layer requires, at runtime:
- [Ollama](https://ollama.ai/) reachable at `http://localhost:11434` (the pet can auto-start `ollama serve`).
- A pulled text model (default `llama3.1:8b`) and, for vision mode, a multimodal model (default `gemma3:4b`).
- Tesseract on `PATH` (or set `TesseractPath` in the AI options tab / `ai-settings.json`).

All AI behavior is configurable from the **AI** tab of the tray Options dialog, or by editing
`%APPDATA%\DesktopPet\ai-settings.json`.

### Vision mode (optional)

By default the pet reads the screen via **OCR + a fast text model** — a glance costs a few seconds.
Turning on **Use vision model** sends a downscaled screenshot to a multimodal model instead: richer
understanding, but much heavier. To keep the pet responsive, vision is used **only for explicit asks**
(the hotkey / tray item); the **idle‑commentary loop always stays on the fast text path**.

Recommended vision models (`ollama pull <name>`), fastest first:

| Model | Notes |
|-------|-------|
| `gemma3:4b` | Small, quick — the default. Good enough for "what am I looking at?". |
| `gemma3:12b` | Sharper, still reasonable. |
| `mistral-small3.2:24b` / `gemma3:27b` | Best reading of on‑screen text; can take ~a minute per glance on a cold model. |

Vision inference scales with image size, so the screenshot is downscaled to 896px wide before sending
and `TimeoutSeconds` defaults to 120 to give a cold model room. Pick a smaller model if reactions feel slow.

---

## Original credits

Original project by [Adrianotiger](https://github.com/Adrianotiger).  
NAudio by [naudio](https://github.com/naudio/NAudio).  
See original [Readme](https://github.com/Adrianotiger/desktopPet/blob/master/Readme.md) for full credits.
