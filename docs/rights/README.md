# `docs/rights/` — retained redistribution-rights evidence

This directory holds the **human-authored, retained rights-review evidence** that the release
gate binds by hash. It is the single place where redistribution rights for every bundled or
downloadable asset are documented before a public release. Nothing here grants rights by itself;
the approval flags live in the machine-checked evidence files under `packaging/` and are set by a
maintainer **only after** real license evidence is retained here.

## What lives here

| File | Covers | Referenced / checked by |
|------|--------|-------------------------|
| `source-review.md` | The source-rights scopes (corpus, model, vocabulary, engine-source closure, bundled art, downloadable pet art) — the retained review narrative + license evidence. | `packaging/Test-SourceRightsEvidence.ps1` (expects evidence under `docs/rights/`) |
| `<pack-id>.json` | One strict UTF-8 JSON rights document **per downloadable pack**, one file each. | `packaging/Test-PackRightsEvidence.ps1` (path must match `^docs/rights/…\.json$`) |

The **exact schema** each file must satisfy is defined authoritatively by those two validators and
by `docs/RELEASE-CHECKLIST.md` §1 (Rights and policy gates). Do not restate the schema from memory —
run the validators. A pack rights document must bind the pack id, catalog revision, pack SHA-256,
record count, and catalog license; identify every source by canonical HTTPS repository and immutable
lowercase 40-char revision; record a concrete SPDX-style (or `LicenseRef-`) license, a substantive
redistribution grant, and non-placeholder obligations per source; and partition every one-based pack
record exactly once. The catalog license must equal the deterministic `AND` of the distinct source
licenses.

## The two approval-bearing files (never edited by tooling)

- `packaging/source-rights-evidence.json` — six scopes, each with `releaseApproved` and
  `sourceApprovals[]`.
- `packaging/pack-rights-evidence.json` — schema-2 `approvals[]`, each with `redistributionApproved`.

A maintainer flips a flag to `true` only after the corresponding `docs/rights/` evidence is written,
reviewed, and retained. Automated steps (including any assistant) must never set these to `true`.

## Current status (as of taxonomy 2026-07-31)

Every scope is **unapproved** — this is the deliberate pre-release baseline, not an oversight:

| Scope | `path` in source-rights-evidence.json | Approved? |
|-------|----------------------------------------|-----------|
| Embedded corpus | `src/Fortunes/fortunes.txt` | ❌ false |
| Embedder model | `src/Models/bge-small.onnx` | ❌ false |
| Embedder vocabulary | `src/Models/bge-small.vocab.txt` | ❌ false |
| Engine source closure | `@engine-source` | ❌ false |
| Bundled art/resources | `@bundled-art` | ❌ false |
| Downloadable pet art | `@downloadable-pet-art` | ❌ false |
| Downloadable fortune packs | `packaging/pack-rights-evidence.json` `approvals[]` | ❌ empty |

Corpus provenance is a known blocker: the checked-in `fortunes.txt` has no retained
`fortunes.sources.tsv` and no reviewed pinned `FORTUNE_SOURCE_COMMIT` (see `TAXONOMY.md` →
"Current-corpus provenance status").

## Relationship to the v1 → v2 taxonomy flip

The shipped corpus is **v1** (`fortunes.txt`, 5-column). The v2 work (12 topics incl. `health-body`,
per-fortune genre labels) lives only in the **gitignored** `src/Fortunes/labels-store.tsv` working
artifact and is **not redistributed** — the v2 flip re-expresses the *same* corpus bytes with richer
metadata, so it adds **no new redistributed content** and no new rights scope beyond the existing
corpus scope above. Flipping to v2 therefore gates on the same corpus-rights approval as any release,
plus the genre-filtering UI (done). Resolving the corpus/pack rights here is the remaining gate.

## User action checklist to reach "rights ready"

1. Establish redistribution rights (or clear/replace) for each of the six source scopes; write the
   evidence into `source-review.md` and flip the matching `releaseApproved` flags.
2. For each downloadable pack, author `docs/rights/<pack-id>.json`, add its matching
   `pack-rights-evidence.json` approval, and set `redistributionApproved: true`.
3. Rebuild `fortunes.txt` from redistribution-approved sources and retain its
   `fortunes.sources.tsv`; pin a reviewed `FORTUNE_SOURCE_COMMIT`.
4. Run `Test-SourceRightsEvidence.ps1` and `Test-PackRightsEvidence.ps1` in release mode until green.
