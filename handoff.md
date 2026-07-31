# desktopPet AI Edition — Session Handoff

> Working notes for picking this up later. Last updated: **2026-07-29**.
> Repo: `D:\.claude\projects\desktopPet` (fork of Adrianotiger/desktopPet).
> `origin` = **git@github.com:bigfnj/desktopPet.git** (`upstream` = Adrianotiger — never push there).
> There is also a persistent memory note `project_desktoppet` in the auto-memory index — read it too.
> Backlog lives in **[`BACKLOG.md`](BACKLOG.md)**; the older Fortune-Sheep plan is in `FORTUNE-SHEEP-PLAN.md`.
>
> **Superseded release status:** this is a historical implementation handoff, not release authority.
> Use [`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md); public distribution remains blocked
> until every rights, signing, automated, and hands-on gate there has evidence.

---

## Latest: pre-release cleanup pass (2026-07-29)

Work happened on branch **`cleanup/pre-release`**, merged to the default branch and pushed.
**No release was cut** (per request: explore the backlog first). The later full audit found additional
engineering and redistribution gates; a release is not one action away.

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

### Historical release mechanism (do not execute until the current checklist is complete)

1. Complete every gate in `docs/RELEASE-CHECKLIST.md`, then decide version and finalize notes.
2. On GitHub, create a Release with tag `vX.Y.Z` and **publish** it → `release.yml` builds the MSI +
   portable zip on a Windows runner and attaches them. (Or run the `release` workflow manually with the tag.)

---

## Historical product snapshot (verify against current source)

This section records the product shape at the time of the handoff; it is not a claim that every detail
remains current. The base is a .NET Framework 4.8 WinForms desktop pet with XML-driven sprite sheets, a
probability-weighted animation state machine, and gravity/border/taskbar physics via Win32 P/Invoke.
Later compatibility, validation, lifecycle, multi-monitor, persistence, and security work intentionally
modified engine files while preserving the recognizable pet experience.

Recorded as feature-complete in this snapshot:

- **Engine + AI foundation (Phases 1–6)** — speech bubble (`FormSpeech`); brain (`dotNet/Ai/`: OCR/vision
  → chat → `{text,emotion}`); triggers (hotkey `Ctrl+Alt+P` + idle loop); emotion→animation + "thinking"
  cue; AI options tab applied live; context & memory (active-window, screen-zone, time-of-day, persona,
  rolling `chat-history.json`); vision path.
- **Fortune Sheep A–C** — bundled corpus + poke-escalation (fortune → ignore → sass → bathtub); offline
  **smart fortunes** (bge-small ONNX, centered cosine + app→category routing, persistent vector cache);
  the **AI brain** behind a default-off master switch (tray Enable/Disable; Ollama-only
  keep-alive memory control) with an
  OpenAI-compatible multi-provider backend (Ollama / LM Studio / llama.cpp / OpenRouter / OpenAI / custom),
  DPAPI-encrypted keys, Test-connection, downloadable fortune packs, "Rebuild smart weights".
- **MIT `LICENSE`** for the fork's additions; **per-user WiX MSI** in `installer/`; **portable zip** via
  `build.ps1 -Release -Zip`. The smart-model runtime ships as loose files beside the exe.

Current build and validation commands belong in [`Readme.md`](Readme.md) and
[`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md). For an MSI,
`packaging/Install-LockedWixToolchain.ps1` is the authoritative WiX 5.0.2 provisioner;
`installer/build-installer.ps1` consumes that already-verified tool and extension. Do not replace the
locked provisioner with an ad hoc `dotnet tool install` command. The maintained tray dialogs compile
from `src/Portable/*`, the engine from `src/dotNet/*`, and corpus tooling lives in `src/Fortunes/`.

> The running installed process is **DesktopPet**. Current `build.ps1` never terminates it; close the
> application yourself if a build output is locked.
