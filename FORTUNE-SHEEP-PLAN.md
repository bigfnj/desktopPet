# Fortune Sheep — build plan

> The v2 direction for this fork. An adorable desktop sheep that speaks **fortunes**
> (think `cowsay | fortune`): smart by default (fortunes that fit your screen, offline),
> charming when poked, and optionally an AI companion that comments on your screen when
> you give it a brain. Builds on the shipped Phases 1–7.1 (speech bubble, `TryPlayAnimation`,
> `AskAboutScreen`, the `IPetBrainBackend` seam, the AI options tab, the WiX MSI).

## Locked decisions (2026-07-27)

- **Embedding model delivery:** *ask at first run* — first-run offers download‑now (~130 MB
  bge‑small) vs. use‑random‑for‑now; installer stays tiny.
- **Default preset:** *Companion* — peeks at the screen for AI insight **when a local LLM is
  detected** (peek ON by default); degrades to fortunes when none.
- **Cloud providers:** *both* OpenRouter (one key → many models) and OpenAI, selectable.
- **Bathtub escape:** *full* variant — force a respawn through the pet's own `spawn id=3`
  (fly in from the screen edge, land in the tub).
- **First implementation pass:** Phases **A → B → C**, then regroup.
- **Name/branding:** TBD (keep "DesktopPet AI Edition" or rename to "Fortune Sheep" at release).

## Architecture — three tiers, one interface

- **Tier 0 — bundled embeddings (default, offline, no LLM):** contextual fortunes + (later)
  semantic idle‑gating and memory. In‑process ONNX (Microsoft.ML.OnnxRuntime, .NET 4.8), no server.
- **Tier 1 — local chat (detected/opt‑in):** Ollama (`:11434`) / LM Studio (`:1234`).
- **Tier 2 — cloud (key):** OpenRouter / OpenAI.
- **One Interface:** a single `OpenAiCompatBackend` (`/v1/chat/completions`, `{baseUrl, apiKey?, model}`)
  behind the existing `IPetBrainBackend` seam drives all of Tier 1–2. Replaces the Ollama‑native client.

## Interaction model

- **On land →** speak a fortune (contextual if the model is present, else random).
- **Right‑click poke escalation** (timing‑based):
  | Poke | Behavior |
  |------|----------|
  | 1 (rested) | main response — **insight** if LLM+peek available, else a fortune |
  | 2 (quick) | another fortune |
  | 3–4 | **ignore** — turn‑away animation, no bubble (`TryPlayAnimation`) |
  | 5–11 | **verbal sass** — canned annoyance lines |
  | 12 (spam) | **full bathtub escape** — respawn via `spawn id=3` (fly in, land in tub) |
- **Idle ambient (Companion default: on, gentle):** occasional contextual fortune on a jittered
  timer, semantically gated; an insight only if LLM + peek + meaningful screen change.
- **SFW / Spicy** corpus toggle.

## Phases

### Phase A — Fortune Sheep core (no new dependencies)
- Vendor a **curated SFW corpus** (+ optional **Spicy**) from JKirchartz/fortunes (Unlicense /
  public domain) as a bundled data resource; `%`‑delimited parser + random pick → speech bubble.
- Fortune on land.
- **Poke‑escalation state machine** (the table above); poke‑1 = fortune for now (insight wired in C).
- **Full bathtub escape:** new small engine entry point to force a respawn via a given `spawn id`
  (drives `spawn id=3` → `batha→bathb→bathc→bathd`, existing frames 134–145/174).
- SFW/Spicy toggle in the options tab.
- *Deliverable:* charming, self‑contained pet, zero setup. Random = the Tier‑0 fallback.

### Phase B — Contextual fortunes (the embedder → the smart default)
- Add **Microsoft.ML.OnnxRuntime** + a BERT tokenizer (FastBertTokenizer), .NET 4.8‑compatible,
  in‑process (no server).
- Ship **bge‑small‑en‑v1.5** (ONNX) + **pre‑computed corpus vectors** (built at compile time).
- `EmbeddingService`: text → vector. Contextual pick = embed screen (active window + OCR) → cosine
  **top‑k → randomize** over corpus vectors → apt but never repetitive; falls back to random.
- **First‑run onboarding** (Phase 7.2, folded in): choose model download‑now vs. random, and detect Ollama.
- *Deliverable:* contextual fortunes are the default; random is the graceful fallback.

### Phase C — AI screen‑insight tier + One Interface
- Replace `OllamaClient` (native `/api/chat`) with **`OpenAiCompatBackend`** (`/v1/chat/completions`)
  behind `IPetBrainBackend`. Vision routing (Phase 6) carries over (OpenAI‑style `image_url`).
- **Provider config + detection:** none / Ollama / LM Studio / cloud (OpenRouter, OpenAI); API key
  **DPAPI‑encrypted** in the settings file. Auto‑detect local servers.
- Wire **insight into poke‑1** when an LLM is configured AND the "let the sheep peek" toggle is on
  (Companion = on by default); otherwise poke‑1 = fortune.
- *Deliverable:* opt‑in, provider‑agnostic AI remarks.

### Phase D — Presets, idle, polish (after the A–C regroup)
- Presets: **Fortune Teller** (never peeks) / **Companion** (default) / **Quiet** (on demand).
- Idle ambient via the embedder's semantic gate (replaces the luma gate).
- Options‑tab pass: corpus, preset, idle frequency, provider, peek toggle, model‑download button.

### Phase E — Release
- Installer updated (first‑run download wiring); version bump; **GitHub Release** with the MSI
  (+ model asset); README / grimoire updates.

## Reuse vs. new
- **Reused:** speech bubble, `TryPlayAnimation` (2.8), `AskAboutScreen`, vision routing (6),
  the AI options tab, `IPetBrainBackend`, the WiX MSI, `ChatHistory`.
- **New:** fortune engine + corpus, poke‑escalation state machine, forced bath‑respawn entry point,
  ONNX embedder + tokenizer + pre‑computed vectors, `OpenAiCompatBackend`, provider config/detection,
  DPAPI key storage, first‑run onboarding.

## Bonus (backlog)
- Embedder also powers **semantic memory** (upgrade Phase 5 "last‑10" → "most‑relevant") and
  **no‑repeat** reply filtering.

## Risks / notes
- Contextual‑fortune quality is good‑but‑imperfect (aphorisms are abstract) — top‑k‑randomize hides misses.
- Corpus curation matters (some upstream files are edgy) — SFW is the default set; Spicy is opt‑in.
- ONNX + tokenizer on .NET Framework 4.8: verified compatible, but adds real dependencies to the
  single‑exe embed trick — validate the packaged exe early in Phase B.
