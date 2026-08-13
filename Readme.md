# 🐑 desktopPet — AI Edition

> A physics-driven desktop **sheep** with a lean core and **optional modules** — offline fortunes that
> are *smart* about what's on your screen, and a multi-provider AI brain you can toggle from the tray.
> Fork of [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet); the original
> animation architecture remains, with compatibility, correctness, and security fixes alongside
> the added fortune and AI features.

> **Releases** are unsigned Windows x64 builds (ZIP + MSI) published from a `vX.Y.Z` git tag; verify
> downloads against `SHA256SUMS.txt`. The engine, artwork, fortune corpus, packs, and model are
> fan-compiled from mixed community/upstream sources — provenance is documented (see
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)), not a blanket redistribution clearance.

The sheep walks, falls, climbs your windows and naps on the taskbar. When it lands, and when you
poke it, it speaks a **fortune** in a little bubble. Poke it too much and it gets sassy and rockets
off to a bathtub. That's the whole toy — and it works with **no internet, no account, no GPU**.

---

## What it does

### 🧩 Modules (how you get everything below)
The installer and the portable ZIP ship **lean** — a pet engine and nothing else. Optional features
arrive from **Options → Modules**, which lists what's installed and what the online catalog offers,
shows each module's declared permissions *before* it downloads anything, and installs it after a
SHA-256 check against the published `catalog.json`. Modules load at startup, so installing or
removing one restarts the app (it reopens straight back on the Modules pane). Uninstalling removes
the module and its settings. Two are published today: **Fortunes** and **AI Brain**.

### 🔮 Fortunes (optional module, 100% offline)
One-liners — quotes, jokes, philosophy, Simpsons chalkboard gags, the abridged Bible, and more. The
module carries a built-in corpus of ~10,000 lines, so it has something to say the moment it installs,
before you download a single pack. Install it from **Options → Modules**, then from
**Options → Fortunes** you can:
- Dial the tone with one ordered **Content level** — *Clean only* / *Clean + edgy* / *Everything* /
  *Spicy only* — plus a separate **Filter profanity** switch for recognized profanity and explicit
  sexual content. A live count under the controls says how many fortunes the current selection
  actually leaves (and warns when that is none), and **Show me 5 examples** prints what it would say.
- **Pick sources** — 150+ per-source packs, grouped into collapsible collections with a filter box,
  so you can run only Simpsons + Futurama if you want.
- **Download packs** — *Check online for packs*, tick the ones you want, then *Download selected*;
  each download is SHA-256-verified against the published `catalog.json`.
- **Add your own** — *Import your own…* runs your `.txt` files (BSD `fortune` `%`-format or
  one-per-line) through a bounded, validating importer; or drop them straight into the folder and
  hit *Rescan*.

The schema-v2 target format is six tab-separated fields:
`source / topic / genre / level / profanity / text`. The conservative `prof` flag covers recognized
profanity and explicit sexual content. The embedded corpus and bundled packs are now this v2 format
(the classification pass is complete): the runtime reads their per-fortune topic and genre directly.
External five-field v1 packs are still accepted through the explicit compatibility path.
Source and content filters are hard constraints; an impossible selection produces an empty pool
rather than falling back to disallowed content.

### 🧠 Smart fortunes (part of the Fortunes module, offline, CPU-only, no keys)
A tiny sentence-embedding model (**bge-small**, ONNX, int8 — shipped inside the Fortunes module
package, which is why that module is ~30 MB) reads your foreground window and
picks a fortune that *fits what's on screen* — a C# file nudges it toward programming quips, a breakup
post toward heartbreak lines. It warms once in the background (cached after), avoids repeating the lines
it just showed, and falls back to the full library whenever it isn't sure. Toggle it in
**Options → Fortunes**.

### 🤖 AI brain (optional module, OFF by default — no provider requests until enabled)
A screen-commentary LLM: the pet glances at your screen (OCR or a vision model) and speaks an original
remark. It's **off out of the box**, so DesktopPet does not contact the configured provider. When you
want it:
- Right-click the tray → **Enable AI**. **Disable AI** cancels DesktopPet's provider requests. With
  Ollama, configured warm-up and unload operations also control that server's keep-alive model
  memory. Generic OpenAI-compatible providers expose no remote-memory control, so disabling
  DesktopPet does not promise to free memory owned by those servers.
- Works with **any OpenAI-compatible provider** — Ollama (local, with keep-alive VRAM control),
  LM Studio, llama.cpp, OpenRouter, OpenAI, or a custom `/v1` endpoint. Pick one in **Options → AI**;
  cloud keys are stored **DPAPI-encrypted**.
- Ask on demand with the global hotkey (`Ctrl+Alt+P`) or the tray, or opt into occasional idle
  commentary.
- **Reads the screen with no extra install.** Windows' own OCR does the work out of the box; the
  module only falls back to **Tesseract** if you have it, and **Options → AI → Choose OCR engine…**
  lets you pick. **Test OCR** confirms which one answered.

> **Privacy:** fortunes and smart-fortunes are entirely local. The optional AI brain can send window,
> OCR, screenshot, persona, and recent-conversation context to the provider you configure after it
> is enabled. Remote providers require explicit cloud-data consent. See [`PRIVACY.md`](PRIVACY.md).

### 🐾 The classic pet
The upstream engine's core experience remains: sprite-sheet animations, gravity, window-edge climbing,
taskbar sitting, child pets, NAudio sound, and the drop-in `animations.xml` pet format (swap the sheep
for any community pet). Compatibility, validation, lifecycle, and multi-monitor fixes modify that
engine where required. From **Options → Pets** you can pick a different look or **download more pets**
from the in-app catalog — shown as a grid of thumbnail previews, each SHA-256-verified before it is
added. The tray dialogs also follow your **Windows light/dark theme**.

---

## Install

Each GitHub release provides two Windows x64 artifacts:

- **`DesktopPet-AI-Edition.msi`** — a per-user installer (no admin).
- **`DesktopPet-Portable.zip`** — unzip anywhere and run `DesktopPet.exe`.

Either way you get the whole thing (sheep + fortunes + smart model + AI runtime) with **no downloads
required** to run. The builds are **unsigned** — verify them against `SHA256SUMS.txt` on the release.

---

## Using it

- **Left-click-drag** the sheep to move it; it falls and roams on its own.
- **Right-click the sheep** to poke it — first pokes give fortunes, then it starts ignoring you, then
  gets sassy, then escapes to a bathtub.
- **Right-click the tray icon** for the menu: add a sheep, **Fortunes** (test), **Enable/Disable AI**,
  **Options**, and quit.
- **Options** has panes for **Preferences**, **Modules**, and then one per installed module,
  alphabetically: **AI** (provider / model / key / OCR / triggers), **Fortunes** (content level /
  sources / packs / smart toggle), **Pets**.

An installed copy stores mutable data under `%LOCALAPPDATA%\DesktopPet`. A portable copy stores it
under `data\` beside the executable. Supported files from the legacy `%APPDATA%\DesktopPet` location
are migrated when needed.

---

## Meet the pets

The default is the classic **eSheep**, but **Options → Pets** offers a catalog of ~20 drop-in pets
(each a self-contained `animations.xml`, SHA-256-verified on download). Choosing one replaces the
current pet instantly. They vary a lot: some are plain walkers, and a few are packed with rare
"easter-egg" behaviours that only surface once in a while.

The **colored sheep** are the deepest, and the gallery shows them by their character names rather than
their colour (the thumbnail already shows that): **Ben** (blue), **Gus** (green), **Omar** (orange),
**Pearl** (pink), **Patsu** (purple), **Rick** (red), **Yogurt** (yellow) — all by Oliver B. They
share one 268-animation parkour set.

### Easter-egg behaviours

| Pet | Rare / special behaviours |
|-----|---------------------------|
| **Colored sheep** (Ben, Gus, Omar, Pearl, Patsu, Rick, Yogurt) | Rocket **blastoff** (ignites underneath, launches diagonally, tumbles on impact); **UFO abduction** with a tractor beam; arrival or exit by **spaceship**; a **black-sheep** romance & chase; a crown-wearing **king mode** with its own full moveset; a **flower-bloom** death; **handstand** walking; parkour (rolls, wall-slides, wall-jumps); a sneeze that flings it into a wall-jump; bathtub dives |
| **Ssj Goku** (RedSparr0w) | **Super Saiyan** transformation, **Instant Transmission** teleport, flight, ki blasts, wall smacks |
| **Pingus** (Adriano) | A *Lemmings* tribute: **digger / miner / basher / stopper**, bridge-building, belly slide, skate, a "superman" flight, reading a book, and spawning a baby penguin |
| **Negima** (Adriano) | Character / costume swaps — **Asuna** and **Akira**, three outfits each |
| **Neko · Fox · Mimiko · Pink Fox · Pink Neko · Yellow Neko** | A run-across-the-screen **chase & runaway**, plus scratching and napping |
| **Blue Ham Ham** (Michelle!) | Emotive idles: **Sparkle, Shy, Tired, Cheer** |
| **Mareep · Pikachu · Shiny Sylveon · Bbunny** | Simple directional walkers — nice sprites, no gags |

**How to catch them.** Many sheep gags are *entrances*: on spawn the pet rolls for how it arrives
(walking in, falling, diving, rolling, a handstand, on a window edge, with a black sheep, or in a
spaceship), so removing and re-adding a pet re-rolls it. Mid-life, a rare deep-idle branch summons a
UFO / spaceship / black-sheep visitor, and the rocket blastoff is a roughly 1-in-5 roll from a deep
idle state. Poking (right-click) runs its own ladder: fortunes, then ignoring you, then sass, then a
bathtub escape. Every pet's exact moves and odds live in its `animations.xml`.

---

## Building

Requires Visual Studio 2022+ (or Build Tools) with the **.NET Framework 4.8** targeting pack. MSI
builds also require WiX 5.0.2.

```powershell
.\build.ps1 -Release -Zip                                   # -> dist\DesktopPet-Portable.zip
msbuild .\tests\DesktopPet.CoreTests\DesktopPet.CoreTests.csproj -restore -p:Configuration=Release -p:Platform=x64
.\tests\DesktopPet.CoreTests\bin\Release\DesktopPet.CoreTests.exe
$wix = Join-Path $env:TEMP 'DesktopPet-WiX-5.0.2'
.\packaging\Install-LockedWixToolchain.ps1 -PackageRoot $wix -GlobalExtension
.\installer\build-installer.ps1 -Config Release             # -> dist\DesktopPet-AI-Edition.msi
```

- `build.ps1` never terminates a running app; if `DesktopPet.exe` is locked, close it and retry. It
  builds only the supported x64 project (`src/DesktopPet_Portable.csproj`).
- ZIP and MSI share the runtime list in [`packaging/runtime-files.txt`](packaging/runtime-files.txt).
  The ZIP also adds `DesktopPet.portable`, which forces portable data-root behavior even when it is
  extracted into an install-shaped directory.
- The smart-model runtime (`onnxruntime.dll`, the bge-small model, managed deps) ships inside the
  **Fortunes module package**, not beside the exe — the installer and the ZIP are lean and carry no
  modules, so it arrives when the user installs Fortunes from the in-app catalog (~30 MB, which is
  almost entirely this model + runtime). The corpus + packs pipeline lives in
  [`src/Fortunes/`](src/Fortunes/) (`build-corpus.sh` → `strip-authors.py` → `classify-corpus.py`).

> ⚠️ The portable csproj compiles the engine from `src/dotNet/*` but the tray dialogs (FormOptions,
> AboutBox, FormHelp) from **`src/Portable/*`** — edit the options UI there.

See [`grimoire/`](grimoire/) for a deep architecture reference and
[`FORTUNE-SOURCES-ASSESSMENT.md`](FORTUNE-SOURCES-ASSESSMENT.md) for the corpus inventory.

### Continuous integration & releases

- **[`.github/workflows/build.yml`](.github/workflows/build.yml)** builds Release x64 + the ZIP, runs
  CoreTests and the app self-tests, builds the MSI, and uploads both as run artifacts.
- **[`.github/workflows/release.yml`](.github/workflows/release.yml)** — push a `vX.Y.Z` tag and it
  builds, packages the ZIP/MSI + `SHA256SUMS.txt`, and publishes them on a GitHub release (unsigned).

---

## How it fits together

```
   FormPet (upstream-compatible engine)         SmartFortunes            AI brain (optional)
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

The repository-root [`LICENSE`](LICENSE) covers the original contributions owned by `bigfnj`.
The bundled third-party works below remain the property of their respective creators and are
included here with gratitude and attribution.

### Sources & thanks

**Engine & pet** — forked from [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet),
the eSheep desktop-pet lineage.

**Embedder model** — [`bge-small-en-v1.5`](https://huggingface.co/BAAI/bge-small-en-v1.5) by BAAI.

**Fortune corpus** — aggregated largely from
[JKirchartz/fortunes](https://github.com/JKirchartz/fortunes) and the classic BSD `fortune` files,
with grateful thanks to the sources behind them, including: the Quotable quote collection; clean
jokes; collected authors, artists, and activists; Seth Godin; Larry Wall and the hacker koans;
*Epigrams on Programming* (Alan Perlis); RFC 1925; *Oblique Strategies* (Brian Eno & Peter Schmidt);
William Blake; Jenny Holzer; Ogden Nash; Robert Louis Stevenson; *The Dictionary of Obscure Sorrows*
(John Koenig); *The Simpsons* chalkboard gags; and r/Showerthoughts.

**Fortune packs** (optional downloads) — with thanks to r/DadJokes and r/Showerthoughts; the Bastard
Operator From Hell (Simon Travaglia); programming epigrams, RFC 1925, and Larry Wall; the *Tao Te
Ching* and classic philosophy; *Oblique Strategies* and assorted authors and poets; Groucho Marx,
Jack Handey, and Red Green; fortune-cookie and Chuck Norris trivia; pop-culture television (*The
Simpsons*, *Futurama*, *MST3K*, *Star Trek*, *Firefly*, *South Park*, *The Sopranos*, *It's Always
Sunny in Philadelphia*, and more); George Carlin, the Church of the SubGenius, and Robert Anton
Wilson; and the classic `fortune -o` collection. Heartfelt thanks to every author and community above.

See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for the dependency inventory,
[`SUPPORT.md`](SUPPORT.md) for support, and [`SECURITY.md`](SECURITY.md) for private
security-reporting guidance.
