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
the module and its settings. Published today: **Fortunes**, **AI Brain**, **Pet Studio** and **Reminder**.

A module that fails to load says so, with the reason and a **Reinstall** that keeps its data — rather
than sitting there claiming it needs a restart forever.

Modules also **update in place**. Checking online marks any installed module with a newer published
version, and *Update* keeps your settings, keys and history (unlike uninstalling, which deletes them).
The app checks once a month on its own and tells you rather than installing anything; turn that off
under **Preferences → Modules** if you would rather it never reached the network unprompted.

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

### 🎨 Pet Studio (optional module, for people who make pets)
Check a pet's `animations.xml` before you use it. Three columns: the XML on the left (editable, with a
re-analyze that keeps up as you type and an atomic save), a report plus a colour-coded **reachability
map** in the middle, and the selected animation on the right — its real sprite frames, with playback,
and the transitions it can take. Click a legend colour to filter the map; it stays usable on a sheep
with 268 animations.

The point is the things you cannot see by eye: which animations **can never play** (nothing transitions
into them), and which frames are the sheet's blank tile — so "it shows nothing" stops looking like a
bug. It validates with the host's *own* parser, so its verdict is what the app will actually do, and it
previews the pet on your real desktop without installing or saving it.

It also **imports Shimeji skins**. Point it at a skin folder or a `.zip`, in either the classic desktop
format (an `actions.xml`/`behaviours.xml` config plus PNG sprites) or the newer Android bundle format
(a JSON manifest plus WebP sprites), and it converts the skin to a desktopPet pet, maps its behaviours
onto the app's own action model, keeps the artwork's per-pixel transparency, shows an honest report of
what could not be carried over, then previews and installs it.

### ⏰ Reminder (optional module)
Point the pet at your calendar and it announces each event a few minutes before it starts. Watch **up to
five calendars at once**, each read from a local JSON feed a work process writes, a **Calendar URL**
(Google's secret `.ics`, a published Outlook / Microsoft 365 calendar, or iCloud — recurrence and time
zones handled), or a **running desktop Outlook** over COM. Every calendar has its own name, its own
speech font/size/colour, and its own chime (browse for any WAV/MP3, or turn it off) so a Home event and a
Work event read and sound different.

Beyond the basics: one or several lead times (e.g. `15,5`), quiet hours, and a **hush while you're
presenting or in Do Not Disturb**. A meeting with a **Teams / Zoom / Google Meet / Webex** link gets a
one-click **Join** in the tray. Ask the pet to **read today's agenda** any time, or have it give you a
**morning briefing** at a set time. Optionally **skip meetings you've declined** or all-day events. And
you can add **your own typed reminders** independent of any calendar (`daily 09:00 Standup`, `every 60m
Stretch`, `in 30m Pizza`, `2026-09-01 14:00 Dentist`).

That speech styling is available to any module through a shared helper, and **Preferences → Sound** now
has two independent switches — **pet sounds** (a pet's own effects) and **notification sounds** (module
chimes) — so you can silence one category without the other.

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
  gets sassy, then escapes to a bathtub. Each pet keeps its **own** poke ladder, so poking one does not
  make another sassy, and only the pet you actually clicked answers.
- **Right-click the tray icon** for the menu: add a sheep, **Test Speech**, **Pet Speech**,
  **Enable/Disable AI**, **Options**, and quit.
- **Tray → Pet Speech** picks which module speaks for **each pet**: `Pet Speech ▸ Pearl ▸ Fortunes`,
  `Pet Speech ▸ Rick ▸ AI Brain`, and so on, with a tick on whichever is in effect. There is an *All pets*
  row for the shared default and a *Reset all pets* row to clear per-pet choices. With several pets on
  screen they no longer all say the same line at the same moment — a reaction belongs to one pet.
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

Requires the **.NET 10 SDK** — exactly 10.0.302, pinned in [`global.json`](global.json) with
`rollForward: disable` so a different patch fails fast instead of quietly building something untested.
All twelve projects target `net10.0-windows`. MSI builds also require WiX 5.0.2.

```powershell
.\tests\run-gate.ps1                                        # the one that matters: build + CoreTests +
                                                            # every self-test + source-text invariants +
                                                            # module payload freshness. Fails on a SKIP.
.\build.ps1 -Release -Zip                                   # -> dist\DesktopPet-Portable.zip
dotnet build .\tests\DesktopPet.CoreTests\DesktopPet.CoreTests.csproj -c Release
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

- The Shimeji conversion engine lives in [`tools/ShimejiConvert.Engine`](tools/ShimejiConvert.Engine/)
  and is **shared**: Pet Studio source-links it for in-app import (above), and the
  [`tools/ShimejiConvert`](tools/ShimejiConvert/) CLI drives it for batch/dev use (`verify`, `convert`,
  `convertroot`, `convertbundle`). It recompiles `PetXmlValidator.cs` rather than reimplementing the
  rules, so converted pets are graded by exactly what the app enforces. It bundles libwebp's `dwebp`
  (BSD) to decode Android-bundle WebP sprites with alpha, since the Windows WebP codec drops it.

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

## Writing your own module

Everything above the pet engine is a module, and you can write one **without cloning this repository**.
There is no signing gate, no allowlist and no catalog requirement: build a DLL, drop the folder in
`modules\`, restart.

```powershell
# DesktopPet.Contracts.nupkg + DesktopPet.ModuleKit.nupkg are attached to every release;
# download them and point a package source at that folder.
dotnet new install <path>\templates\desktoppet-module
dotnet new desktoppet-module -n MyThing --moduleId mything --displayName "My Thing" --standalone true
dotnet build -c Release
# copy bin\Release\ to %LOCALAPPDATA%\Programs\DesktopPet AI Edition\modules\mything\ and restart
DesktopPet.exe --module-selftest=mything      # runs your module through the real loader
```

What you get scaffolded is a module that already works — a tray item, a settings pane whose values
round-trip, a reaction to the pet being poked, and a self-test.

- **[`docs/module-authoring.md`](docs/module-authoring.md)** — the guide: the `IHost` surface,
  permissions, what the host guarantees, and the publishing rules.
- **`DesktopPet.Contracts`** is the whole contract: implement `IModule`, talk to the app through
  `IHost`. Its `AssemblyVersion` is pinned at `1.0.0.0` forever, so a module you build today keeps
  loading. Simplest possible start: the portable ZIP ships `DesktopPet.Contracts.dll` beside the exe,
  and a plain `<Reference>` to it is enough.
- **`DesktopPet.ModuleKit`** is optional convenience — durable file writes, per-module paths,
  embedded-resource loading, `WavAudio` for wrapping raw samples, and a headless `RecordingHost` so you
  can unit-test a module with no app running.

Two capabilities worth knowing about if you are writing something that talks:

- **Speak for one pet, not all of them.** Register with `RegisterPetPokeResponder` /
  `RegisterPetDropResponder` and the host tells you *which* pet the reaction belongs to, so you can call
  `Say(pet, …)`. `SayAll` still exists but is for announcements to the user, not pet reactions. Check
  `IsPetAlive` before acting on a handle you captured before a slow `await` — there is no removal event,
  so the pet may be gone, and speaking to a dead pet is dropped rather than redirected.
- **Audio and voice.** `PlaySound(moduleId, wavOrMp3, volume)` plays through the app's shared mixer and
  device (declare `ModulePermissions.Audio`); `StopSound` cuts your own audio for barge-in.
  `RegisterSpeechResponder` (declare `ModulePermissions.Voice`) offers you every line *before* its bubble
  is drawn, so a voice module can speak it and optionally suppress the bubble. Returning `false` from
  `PlaySound` means nothing will be heard — fall back to showing the bubble.

The ABI is **stable, not frozen**: it only ever gains members, never loses or redefines them. If you
need something it cannot express, that is a gap worth filing rather than a wall.

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
