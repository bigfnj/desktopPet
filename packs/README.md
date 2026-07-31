# Fortune Packs

Optional add-on fortune collections for **DesktopPet AI Edition**. Each pack is a plain UTF-8
text file containing tagged fortunes. Approved packs are downloaded from the in-app **Fortunes**
tab into the application's data-root `fortunes` directory, where they load as new sources.

`packs.json` is a versioned catalog embedded into the application at build time. Each entry records
an id, display metadata, expected count/bytes/schema, SHA-256 digest, and
`redistributionApproved` gate. A held entry (`redistributionApproved: false`) must have `url: null`
(absent or empty is accepted by validators for compatibility), so it exposes no usable download
endpoint. When every entry is held, the catalog-level `revision` is also `null`; a stale or
unpublished commit is not represented as retrievable provenance. A catalog with any approved entry
must instead have a lowercase 40-character commit revision and the approved entry must use its exact
commit-pinned raw URL. The application rejects mutable URLs, mismatched hashes, oversized content,
and unapproved entries.

## Provenance & licensing

These collections were fan-compiled from mixed public/community sources. Labels such as "fair use"
or "personal use" are notes, not redistribution grants:

- **Fair-use / public-domain:** tech, philosophy, literary, facts (fortune-mod datfiles, quotes).
- **Community lists (personal use):** dad jokes & showerthoughts (Reddit), BOFH excuse server.
- **Copyrighted excerpts (personal use):** pop-culture TV dialogue, comedy, spicy.
- **Adults only:** the `nsfw` pack (the classic `fortune -o` set, with hate files removed).

All current entries are held with `redistributionApproved: false` pending source-by-source review.
Do not enable a pack merely because it is present in this repository. Approval requires evidence
for the exact commit-pinned bytes and maintainer/legal sign-off.

Approval is deliberately two-step: first retain and approve the exact rights record while the entry
remains held with no URL or catalog revision; then, in the reviewed release change, set
`redistributionApproved: true`, set the reviewed commit revision, and add the URL pinned to that
same revision and pack path. The release gate fetches and revalidates remote bytes only for approved
entries.

`packaging/pack-rights-evidence.json` is the fail-closed approval manifest. Its protected empty
baseline remains schema 1 for compatibility. Any approval-bearing manifest must use schema 2. A
future `redistributionApproved: true` entry is accepted by the release gate only when that
schema-2 manifest has one matching record bound to the catalog revision, pack SHA-256, and the
SHA-256 of a retained strict UTF-8 JSON review document under `docs/rights/`. The retained document
uses rights-document schema 1 and binds `packId`, `catalogRevision`, `packSha256`, `recordCount`,
and `catalogLicenseExpression` to the catalog and approval.

Every structured review document must then enumerate each source with a unique `sourceId`, a
canonical HTTPS `sourceRepository`, an immutable lowercase 40-character `sourceRevision`, a
concrete SPDX-style or `LicenseRef-` `licenseExpression`, substantive `redistributionGrant` text,
and non-placeholder `obligations`. Its inclusive, one-based `recordRanges` must partition every
record in the pack exactly once, with no overlap or gap. The catalog license is the deterministic
ordinally sorted `AND` expression of the distinct source licenses. Labels such as "fair use",
"personal use", "mixed", "unknown", or "pending" are not licenses or redistribution grants.

Evidence for an unapproved pack is rejected as stale. The current evidence manifest intentionally
has an empty `approvals` array and does not grant any approval.

No warranty of accuracy or attribution. Rights holders can use the process in
[`SUPPORT.md`](../SUPPORT.md).

See `FORTUNE-SOURCES-ASSESSMENT.md` in the repo root for the full source inventory.
