# `docs/rights/` — retained redistribution-rights evidence

This directory holds the **human-authored, retained rights-review evidence** that the release gate
binds by hash. It is the single place where redistribution rights for the bundled source-scope assets
are documented before a public release. Nothing here grants rights by itself; the approval flags live
in the machine-checked evidence file under `packaging/` and are set by a maintainer **only after**
real license evidence is retained here.

## What lives here

| File | Covers | Referenced / checked by |
|------|--------|-------------------------|
| `source-review.md` | The source-rights scopes (corpus, model, vocabulary, engine-source closure, bundled art, downloadable pet art) — the retained review narrative + license evidence. | `packaging/Test-SourceRightsEvidence.ps1` (expects evidence under `docs/rights/`) |

The **exact schema** the evidence must satisfy is defined authoritatively by that validator and by
`docs/RELEASE-CHECKLIST.md` §1 (Rights and policy gates). Do not restate the schema from memory — run
the validator.

## The approval-bearing file (never edited by tooling)

- `packaging/source-rights-evidence.json` — six scopes, each with `releaseApproved` and
  `sourceApprovals[]`.

A maintainer flips a flag to `true` only after the corresponding `docs/rights/` evidence is written,
reviewed, and retained. Automated steps (including any assistant) must never set these to `true`.

## Retired: the per-pack rights gate

Downloadable **fortune packs** used to carry their own fail-closed rights gate: a schema-2
`packaging/pack-rights-evidence.json` with one `redistributionApproved` record per pack, each bound
by hash to a strict per-pack `docs/rights/<pack-id>.json` document and revalidated at release by
`Test-PackRightsEvidence.ps1`. That machinery was **removed** when fortune packs moved to the runtime
`catalog.json` (one file per source, served branch-pinned with a per-file SHA-256 integrity check).

The consequence is explicit: downloadable-pack **integrity** is still verified, but downloadable-pack
**redistribution rights are no longer machine-checked**. Before any public release, pack rights must
be reviewed by hand (see `packs/README.md` and `FORTUNE-SOURCES-ASSESSMENT.md`). The six source
scopes below remain gated exactly as before.

## Current status (source scopes)

Every scope is **unapproved** — this is the deliberate pre-release baseline, not an oversight:

| Scope | `path` in source-rights-evidence.json | Approved? |
|-------|----------------------------------------|-----------|
| Embedded corpus | `src/Fortunes/fortunes.txt` | ❌ false |
| Embedder model | `src/Models/bge-small.onnx` | ❌ false |
| Embedder vocabulary | `src/Models/bge-small.vocab.txt` | ❌ false |
| Engine source closure | `@engine-source` | ❌ false |
| Bundled art/resources | `@bundled-art` | ❌ false |
| Downloadable pet art | `@downloadable-pet-art` | ❌ false |

Corpus provenance is a known blocker: the checked-in `fortunes.txt` has no retained
`fortunes.sources.tsv` and no reviewed pinned `FORTUNE_SOURCE_COMMIT` (see `TAXONOMY.md` →
"Current-corpus provenance status").

## Relationship to the v1 → v2 taxonomy flip

The shipped corpus is **v1** (`fortunes.txt`). The v2 work (12 topics incl. `health-body`,
per-fortune genre labels) lives only in the **gitignored** `src/Fortunes/labels-store.tsv` working
artifact and is **not redistributed** — the v2 flip re-expresses the *same* corpus bytes with richer
metadata, so it adds **no new redistributed content** and no new rights scope beyond the existing
corpus scope above. Flipping to v2 therefore gates on the same corpus-rights approval as any release,
plus the genre-filtering UI (done). Resolving the corpus rights here is the remaining gate.

## User action checklist to reach "rights ready"

1. Establish redistribution rights (or clear/replace) for each of the six source scopes; write the
   evidence into `source-review.md` and flip the matching `releaseApproved` flags.
2. Review downloadable-pack rights by hand — there is no longer an automated per-pack gate. Clear,
   replace, or remove any source that lacks a redistribution grant before publishing `catalog.json`.
3. Rebuild `fortunes.txt` from redistribution-approved sources and retain its
   `fortunes.sources.tsv`; pin a reviewed `FORTUNE_SOURCE_COMMIT`.
4. Run `Test-SourceRightsEvidence.ps1` in release mode until green.
