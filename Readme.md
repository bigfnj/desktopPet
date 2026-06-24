# desktopPet — AI Edition (WIP)

> **Fork of [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet)**  
> Active development branch: `master` | Status: **AI brain layer in progress**

This fork keeps the original physics-driven WinForms animation engine intact and layers a local-LLM AI brain on top of it — screen awareness, speech bubbles, and reactive behavior driven by [Ollama](https://ollama.ai/) running locally on your machine.

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

## Building (original)

Requires Visual Studio + .NET Framework 4.8.

```
src/DesktopPet.csproj        — main portable build
src/DesktopPet_Portable.csproj — portable standalone
```

Open either `.csproj` in Visual Studio, build, run.

No new build requirements have been added yet. When the AI layer lands it will require:
- Ollama running locally (`ollama serve`)
- A pulled model (`ollama pull llama3.2` for text, `ollama pull llava` for vision)
- Tesseract in PATH (already present on devtoolbox machines)

---

## Original credits

Original project by [Adrianotiger](https://github.com/Adrianotiger).  
NAudio by [naudio](https://github.com/naudio/NAudio).  
See original [Readme](https://github.com/Adrianotiger/desktopPet/blob/master/Readme.md) for full credits.
