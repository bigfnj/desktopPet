# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-07-29**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> There is also a persistent memory note `project_desktoppet` in the auto-memory index — read it too.
> Backlog lives in **[`BACKLOG.md`](BACKLOG.md)**; the older Fortune-Sheep plan is in `FORTUNE-SHEEP-PLAN.md`.

---

## Latest: pre-release cleanup pass (2026-07-29)

Work happened on branch **`cleanup/pre-release`**, merged to the default branch and pushed.
**No release was cut** (per request: explore the backlog first). The tree is release-clean; cutting a
release is now one action away (see below).

### What shipped this session (one commit per phase)

- **Online pets** — repointed the "Online pets" downloader from `Adrianotiger/desktopPet` to our own
  `bigfnj/desktopPet` mirror (our fork already carries all 22 pets), and hardened `LoadPets` so opening
  Options while offline can't crash the app.
- **Phase 1 — dead code** — removed the `_NAudio.dll` orphan, the stale `dotNet/app.config`, unused csproj
  references + `using`s, deduped `System.Security`, and disabled dead ClickOnce manifest generation
  (`GenerateManifests=false`; the Win32 manifest is still embedded via `<ApplicationManifest>`).
- **Phase 2 — correctness** — AI-off no longer contacts Ollama or builds an unused brain; a bad sound clip
  no longer mutes the whole pet (disables only that clip); `LandTimer_Tick` is exception-guarded;
  `AskAboutScreen` UI-thread marshaling is now explicit; `Pet_Click` HttpClient scoped in a `using`.
- **Phase 4 — hygiene/perf** — retargeted to **.NET 4.8** (branding now accurate; 4.8 pack verified);
  idle-interval overflow clamp; removed x86 build configs; cached the embedded-corpus parse; fixed a
  FormOptions timer-leak ordering; refreshed a stale `IPetBrainBackend` doc comment.
- **Phase 6 — CI** — `.github/workflows/build.yml` (build + embedder self-test on push/PR) and
  `release.yml` (build portable zip + MSI, attach to a published GitHub Release).

Every phase verified: `build.ps1 -Release` green, `--embed-selftest` = 0.72/0.44, `--smart-selftest`
picks on-topic. Portable zip (29.4 MB) and MSI (29.8 MB) both build locally (`dist/`, gitignored).

### Deferred on purpose (not forgotten)

- **Phase 3 — classifier enrichment (routing fix).** Skipped tonight because you asked to *brainstorm
  expanded classifications first* (see `BACKLOG.md` → "Expanded classifications"). The bundled corpus is
  classified into only `whimsy`+`wisdom`, so 5 of the Router's 7 categories never match out-of-box. Once
  the taxonomy is decided, the fix = enrich `src/Fortunes/classify-corpus.py`, regenerate
  `src/Fortunes/fortunes.txt`, re-run `--smart-selftest`. The vector cache re-warms on next launch.
- Audit **#17** (binding redirects), **#12** (VectorCache prune), **#15** (ComputeSignature) — all work
  today; low value / not worth touching unattended. Rationale in `BACKLOG.md`.

### Verify hands-on next (GUI — couldn't automate overnight)

- Launch with the **AI brain off** (default) → confirm **no** `localhost:11434` contact (Phase-2 #2 fix).
- Poke a pet repeatedly for fortunes; confirm one failing sound clip no longer silences all audio.
- Open **Options while offline** — should no longer risk a crash on the Online-pets tab.

### To cut the release (HELD per request)

1. Decide version, finalize notes.
2. On GitHub, create a Release with tag `vX.Y.Z` and **publish** it → `release.yml` builds the MSI +
   portable zip on a Windows runner and attaches them. (Or run the `release` workflow manually with the tag.)

---

## Product context (still current)

The original engine is a .NET Framework WinForms desktop pet (now targeting **4.8**): XML-driven sprite
sheets, a probability-weighted animation state machine, gravity/border/taskbar physics via Win32 P/Invoke.
**We never modify the engine's behavior** — the AI/fortunes work is an additive layer.

Shipped and live (Fortune Sheep is feature-complete):

- **Engine + AI foundation (Phases 1–6)** — speech bubble (`FormSpeech`); brain (`dotNet/Ai/`: OCR/vision
  → chat → `{text,emotion}`); triggers (hotkey `Ctrl+Alt+P` + idle loop); emotion→animation + "thinking"
  cue; AI options tab applied live; context & memory (active-window, screen-zone, time-of-day, persona,
  rolling `chat-history.json`); vision path.
- **Fortune Sheep A–C** — bundled corpus + poke-escalation (fortune → ignore → sass → bathtub); offline
  **smart fortunes** (bge-small ONNX, centered cosine + app→category routing, persistent vector cache);
  the **AI brain** behind a default-off master switch (tray Load/Unload for VRAM) with an
  OpenAI-compatible multi-provider backend (Ollama / LM Studio / llama.cpp / OpenRouter / OpenAI / custom),
  DPAPI-encrypted keys, Test-connection, downloadable fortune packs, "Rebuild smart weights".
- **MIT `LICENSE`** for the fork's additions; **per-user WiX MSI** in `installer/`; **portable zip** via
  `build.ps1 -Release -Zip`. The smart-model runtime ships as loose files beside the exe.

Build/run: **`.\build.ps1`** (`-Run`, `-Release`, `-Zip`); MSI: `.\installer\build-installer.ps1`
(needs `dotnet tool install --global wix --version 5.0.2`). Edit the tray dialogs in `src/Portable/*`;
the engine compiles from `src/dotNet/*`. The bundled corpus/packs pipeline lives in `src/Fortunes/`.

> ⚠️ The running installed process is **DesktopPet** (build.ps1 kills `eSheep,DesktopPet` before building).
