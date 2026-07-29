# 🐑 desktopPet — AI Edition

> A physics-driven desktop **sheep that tells fortunes** — fully **offline** by default, *smart* about
> what's on your screen, with an **optional** multi-provider AI brain you can toggle from the tray.
> Fork of [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet); the original
> animation engine is kept intact and everything below is layered additively on top.

The sheep walks, falls, climbs your windows and naps on the taskbar. When it lands, and when you
poke it, it speaks a **fortune** in a little bubble. Poke it too much and it gets sassy and rockets
off to a bathtub. That's the whole toy — and it works with **no internet, no account, no GPU**.

---

## What it does

### 🔮 Fortunes (always on, 100% offline)
A large bundled corpus of one-liners — quotes, jokes, philosophy, Simpsons chalkboard gags, the
abridged Bible, and more. From **Options → Fortunes** you can:
- Dial the tone: **Enable spicy** (Edgy / True-NSFW), **Remove all profanity**, or **Spicy only**.
- **Pick sources** — check exactly the collections you want (e.g. only Simpsons + Futurama).
- **Download packs** — optional themed packs pulled from GitHub into your fortunes folder
  (Dad Jokes, BOFH excuses, Tech, Philosophy, Pop-culture TV, and more). Each pack breaks out into
  its own toggleable sources.
- **Add your own** — drop any `.txt` (BSD `fortune` `%`-format or one-per-line) into the folder.

### 🧠 Smart fortunes (on by default, still offline, CPU-only, no keys)
A tiny bundled sentence-embedding model (**bge-small**, ONNX, int8) reads your foreground window and
picks a fortune that *fits what's on screen* — a C# file nudges it toward programming quips, a breakup
post toward heartbreak lines. It warms once in the background (cached after) and falls back to random
whenever it isn't sure. Toggle it in **Options → Fortunes**.

### 🤖 AI brain (optional, OFF by default — zero VRAM until you ask)
A screen-commentary LLM: the pet glances at your screen (OCR or a vision model) and speaks an original
remark. It's **off out of the box**, so it never touches your GPU while you game. When you want it:
- Right-click the tray → **Load AI** (warms the model). The item flips to **Unload AI (free VRAM)** —
  one click frees the GPU again.
- Works with **any OpenAI-compatible provider** — Ollama (local, with keep-alive VRAM control),
  LM Studio, llama.cpp, OpenRouter, OpenAI, or a custom `/v1` endpoint. Pick one in **Options → AI**;
  cloud keys are stored **DPAPI-encrypted**.
- Ask on demand with the global hotkey (`Ctrl+Alt+P`) or the tray, or opt into occasional idle
  commentary.

> **Privacy:** fortunes and smart-fortunes are entirely local — nothing leaves your machine. The AI
> brain only contacts the provider you configure, and only when it's turned on.

### 🐾 The classic pet
All of the upstream engine is untouched: sprite-sheet animations, gravity, window-edge climbing,
taskbar sitting, child pets, NAudio sound, and the drop-in `animations.xml` pet format (swap the sheep
for any community pet).

---

## Install

Two self-contained, offline options (built from `dist/`):

- **`DesktopPet-AI-Edition.msi`** — a per-user installer (no admin). Start-menu + Desktop shortcuts,
  clean uninstall via Add/Remove Programs.
- **`DesktopPet-Portable.zip`** — unzip anywhere and run `DesktopPet.exe`. No install, fully portable.

Either way you get the whole thing (sheep + fortunes + smart model + AI runtime) with **no downloads
required** to run.

---

## Using it

- **Left-click-drag** the sheep to move it; it falls and roams on its own.
- **Right-click the sheep** to poke it — first pokes give fortunes, then it starts ignoring you, then
  gets sassy, then escapes to a bathtub.
- **Right-click the tray icon** for the menu: add a sheep, **Fortunes** (test), **Load/Unload AI**,
  **Options**, and quit.
- **Options** has tabs for **Speech**, **Fortunes** (tone / sources / packs / smart toggle), and
  **AI** (provider / model / key / triggers).

Settings live in `%APPDATA%\DesktopPet\` (`ai-settings.json`, custom `fortunes\`); the smart-model
vector cache lives in `%LOCALAPPDATA%\DesktopPet\`.

---

## Building

Requires Visual Studio 2022+ (or Build Tools) with the **.NET Framework 4.8** targeting pack, and the
WiX tool for the installer.

```powershell
.\build.ps1 -Release           # build the app  (add -Zip for the portable zip)
.\installer\build-installer.ps1  # -> dist\DesktopPet-AI-Edition.msi   (needs: dotnet tool install --global wix --version 5.0.2)
```

- `build.ps1` kills the running pet (process **`DesktopPet`** / `eSheep`), restores, and builds the
  **portable x64 project** (`src/DesktopPet_Portable.csproj`) — not the `.sln`.
- The smart-model runtime (`onnxruntime.dll`, the bge-small model, managed deps) ships as plain files
  beside the exe; the installer and the zip bundle them. The corpus + packs pipeline lives in
  [`src/Fortunes/`](src/Fortunes/) (`build-corpus.sh` → `strip-authors.py` → `classify-corpus.py`).

> ⚠️ The portable csproj compiles the engine from `src/dotNet/*` but the tray dialogs (FormOptions,
> AboutBox, FormHelp, Install) from **`src/Portable/*`** — edit the options UI there.

See [`grimoire/`](grimoire/) for a deep architecture reference and
[`FORTUNE-SOURCES-ASSESSMENT.md`](FORTUNE-SOURCES-ASSESSMENT.md) for the corpus inventory.

### Continuous integration & releases

- **[`.github/workflows/build.yml`](.github/workflows/build.yml)** builds the portable app and runs the
  bundled-embedder self-test on every push / PR (Windows runner).
- **[`.github/workflows/release.yml`](.github/workflows/release.yml)** builds the portable zip **and** the
  MSI and attaches both to a **published GitHub Release** (tag `vX.Y.Z`). So shipping is: draft a release,
  publish it, and CI attaches the installers.

---

## How it fits together

```
   FormPet (upstream engine, untouched)         SmartFortunes            AI brain (optional)
   physics · sprites · poke · bathtub            bge-small ONNX          IPetBrainBackend
        │                                        (offline, CPU)          ├─ OllamaClient (native, keep-alive VRAM)
        │ Say(text)                                   │ Pick(context)    └─ OpenAiCompatBackend (/v1: LMStudio,
        ▼                                              │                     llama.cpp, OpenRouter, OpenAI, custom)
   FormSpeech (follows the pet)  ◄── SayFortune() ◄────┴── FortuneProvider (corpus + packs + filters)
```

- **FortuneProvider** loads the embedded corpus + downloaded/custom packs and filters by tone + source.
- **SmartFortunes** embeds the active pool once (cached) and ranks by centered cosine + app→category
  routing, degrading to random when nothing fits.
- **AI brain** is gated behind a master switch (off by default) and picks its backend from the provider
  setting; only Ollama gets native keep-alive VRAM load/unload.

---

## License & credits

This fork's own additions (the `src/dotNet/Ai/` layer, the fortunes corpus/pipeline, the options tabs,
the build/installer tooling, and the [`grimoire/`](grimoire/)) are **MIT** ([`LICENSE`](LICENSE)).

The WinForms **engine** is from [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet);
the default **artwork** is the classic eSheep / *Stray Sheep* screen-mate sprite set the upstream
project distributes freely. Fortune packs are fan-compiled from public/community sources for personal
use — provenance and licensing in [`packs/README.md`](packs/README.md). Embedding model:
**bge-small-en-v1.5** (MIT). Runtime: **ONNX Runtime** (MIT). Sound: **NAudio**.
