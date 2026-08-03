# DesktopPet AI Edition release checklist

A public release is prohibited while any item marked **BLOCKER** is unresolved. Record the reviewer,
date, and evidence link for every approval.

## Repository and environment setup (one time, then audit periodically)

- Create a GitHub Environment named `desktopPet-release-signing`. Store
  `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` only as
  environment secrets, not repository or organization secrets. Set the non-secret repository
  variable `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT` to the approved certificate's exact 40-digit
  SHA-1 thumbprint so both the protected signing job and the separate no-secret verifier pin the
  same trusted signer. Require at least two independent maintainers to approve deployments,
  prevent self-review, and allow deployment only from protected `v*` tag refs. The workflow itself
  proves that the tag commit is in protected default-branch history.
- Set non-secret repository variable `WINDOWS_PREVIOUS_SIGNING_CERTIFICATE_THUMBPRINTS` to a
  comma-separated allowlist of exact 40-digit SHA-1 thumbprints for approved historical signing
  certificates. Before changing `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT`, add the outgoing
  certificate to this allowlist so the next release can authenticate its N-1 MSI. Retain an old
  thumbprint only while that certificate can sign the highest lower stable public MSI; remove it
  after a newer approved release makes it obsolete. Never populate the allowlist from an
  unauthenticated release asset.
- Create a separate GitHub Environment named `desktopPet-release-publication`. Require at least one
  independent maintainer to approve the final publication job after signing and attestation pass.
  Do not place signing material in this environment, and allow deployment only from protected
  `v*` tag refs.
- Set the repository's default `GITHUB_TOKEN` permission to read-only and disable permission for
  Actions to create or approve pull requests. The workflow grants `contents: write` only to its
  final publication job and grants `id-token: write` only to its attestation job.
- Protect `.github/workflows/**`, `packaging/**`, `installer/**`, `ProductVersion.props`, dependency
  locks, notices, and the runtime manifest with CODEOWNERS review and required status checks.
- Add a tag ruleset for `v*` that restricts tag creation, update, and deletion to release
  maintainers. A release tag is immutable after publication.
- Retain GitHub Actions logs and artifact-attestation records for the required audit period.

## 1. Rights and policy gates

- [ ] **BLOCKER:** written permission or a compatible license covers the upstream
  `Adrianotiger/desktopPet` engine code in this distribution.
- [ ] **BLOCKER:** every bundled sprite, icon, sound, and other artwork has documented redistribution
  rights, or has been replaced with cleared material.
- [ ] **BLOCKER:** the embedded fortune corpus was rebuilt only from source entries approved for
  redistribution; retain its source/license manifest and review evidence.
- [ ] **BLOCKER:** downloadable fortune packs carry **no automated redistribution-rights gate**. The
  old per-pack gate (schema-2 `packaging/pack-rights-evidence.json` + strict `docs/rights/<pack-id>.json`
  documents + `Test-PackRightsEvidence.ps1`) was retired when packs moved to the runtime `catalog.json`;
  the catalog verifies per-file SHA-256 **integrity** only, not rights. Review every pack's
  redistribution rights by hand and clear, replace, or remove uncleared sources before publishing
  `catalog.json`. The packs are fan-compiled from mixed community sources (see `packs/README.md` and
  `FORTUNE-SOURCES-ASSESSMENT.md`); presence in the repository is not a redistribution grant.
- [ ] **BLOCKER:** the exact bge-small model and vocabulary revision, hashes, conversion steps, and
  license evidence are recorded.
- [ ] **BLOCKER:** `packaging/Test-SourceRightsEvidence.ps1` passes with release approval required
  for all six exact scopes in `packaging/source-rights-evidence.json`: the corpus, model,
  vocabulary, supported engine-source closure, bundled executable art/resources, and downloadable
  pet animation/icon/catalog payloads. For each virtual set, approval `memberPaths` must form an
  exact, non-overlapping partition of every fingerprinted member; a blanket, partial, duplicate, or
  extra-path approval is invalid. Strict release validation must also see the exact LF-only engine
  bytes required by `.gitattributes`; development-only CRLF canonicalization is not release
  approval.
- [ ] After every rights gate is approved, set repository variable
  `RELEASE_RIGHTS_APPROVED_COMMIT` to the exact release-tag commit SHA. Never use a branch name,
  wildcard, or reusable approval value.
- [ ] Privacy text and in-product consent language match actual data flows.
- [ ] `THIRD_PARTY_NOTICES.md` matches the resolved dependency lock and release SBOM.

## 2. Version and source freeze

- [ ] All intended changes are reviewed and committed; generated labeling work is not accidentally
  included.
- [ ] `ProductVersion.props` contains the approved product name, publisher, and three-part version.
- [ ] `src/packages.lock.json` is committed and locked restore succeeds from a clean clone.
- [ ] The release tag is exactly `vX.Y.Z`, where `X.Y.Z` equals `DesktopPetVersion`.
- [ ] Branch protections and required CI checks are green for the tag commit.

## 3. Automated quality gates

- [ ] Release x64 build succeeds with warnings treated as errors.
- [ ] The core regression suite plus PetTester hardening, embedder, smart-fortune, fortune-filter,
  security, runtime hardening, deterministic portable packaging, offline Help/documentation
  boundaries, SBOM refresh, and bounded runtime-resource-soak tests return exit code zero.
- [ ] NuGet direct and transitive vulnerability audit is clean; every manually vendored binary is
  separately inventoried, scanned, and covered by retained provenance/license evidence.
- [ ] Fortune labeling pipeline self-test passes on Linux.
- [ ] PowerShell scripts parse and shell scripts pass `bash -n`.
- [ ] XML, JSON, YAML, project, and WiX sources parse.
- [ ] Two clean Release rebuilds use independently restored, exact
  `Microsoft.Net.Compilers.Toolset` 4.14.0 and
  `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3 package trees from the locked NuGet
  graph; every runtime-manifest SHA-256 matches across builds.
- [ ] MSI builds with the digest-locked WiX 5.0.2 tool and UI extension, and all standard ICE
  validation passes except intentionally suppressed ICE91, which is inapplicable to this fixed
  per-user package. Two real builds with different source mtimes and working/temp paths are
  byte-identical and report identical ProductCode and PackageCode values.
- [ ] MSI contains exactly `packaging/runtime-files.txt`. ZIP contains that same byte-for-byte
  runtime plus only the canonical `DesktopPet.portable` mode marker, with sorted entries and
  normalized timestamps; two equivalent ZIP builds are byte-identical.
- [ ] Syft runs directly from the repository-locked v1.42.3 Windows archive after exact
  28,204,841-byte length and SHA-256 verification. Every canonical runtime hash is unchanged
  before/after SBOM execution. The SPDX contains every exact locked NuGet identity and exactly the
  runtime-manifest files with matching post-signing SHA-256 values; the shared runtime view from
  the final ZIP and MSI validates against that same artifact-specific SBOM. The portable mode
  marker is validated separately as package metadata. `packages.lock.json` is metadata input, not
  a shipped file. Official SPDX 2.3 schema validation uses CPython 3.13.5 selected by a
  commit-pinned setup action and installs only the repository's SHA-256-locked binary wheels.
- [ ] A disposable Windows account passes silent install, installed self-tests, explicit full-file
  repair of both the EXE and a non-EXE payload, and uninstall. Add/Remove Programs must not expose
  its unreliable per-user component repair action.
- [ ] The isolated `verify_n_minus_one` job resolves the highest lower stable public release
  containing an MSI. Before execution, it verifies the download/API digest and published
  `SHA256SUMS.txt`, requires a valid timestamped Authenticode signature from the current or approved
  historical signer allowlist, and verifies the MSI's GitHub artifact attestation against the
  official release workflow, exact prior tag ref and peeled commit, SLSA v1 predicate, and
  GitHub-hosted runner policy. Its disposable Windows runner then performs a real install, major
  upgrade, exact
  runtime replacement, obsolete-file removal, settings preservation, downgrade rejection, and
  uninstall. The job uploads only one machine-readable evidence document, never current release
  assets. When no prior public MSI exists, that evidence must explicitly report
  `no_prior_public_msi`.
- [ ] The fresh `seal_release` job downloads pristine signed assets directly from `sign_msi` and
  downloads the isolated N-1 evidence separately. It validates both exact input sets and the
  evidence-to-current-MSI hash binding, creates the final `SHA256SUMS.txt`, and uploads the only
  artifact that attestation and publication may consume. No job that executes an MSI may seal,
  attest, or publish release bytes.

## 4. Manual QA

- [ ] Fresh MSI and portable launches work from directories containing spaces and non-ASCII text.
- [ ] Installed and portable data roots are correct and independent of the current working
  directory.
- [ ] Multi-monitor, mixed-DPI, taskbar-edge, drag/drop, animation, audio, speech, and shutdown paths
  pass.
- [ ] AI-off mode makes no provider request.
- [ ] Local Ollama and one OpenAI-compatible endpoint pass consent, endpoint, timeout, cancellation,
  load/unload, OCR, and vision checks.
- [ ] Fortune source, profanity, general/edgy/NSFW, spicy-only, and all-disabled combinations behave
  fail-closed.
- [ ] Malformed pet XML, unsafe expressions, traversal ids, oversized downloads, hash mismatches,
  redirects, and non-loopback HTTP endpoints are rejected without corrupting saved state.

## 5. Signing and publication

- [ ] The `desktopPet-release-signing` environment secrets
  `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` hold the approved
  code-signing PFX; the repository variable `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT` matches it;
  `WINDOWS_PREVIOUS_SIGNING_CERTIFICATE_THUMBPRINTS` contains only still-needed approved historical
  signers; and the required-reviewer/tag-ref rules above are active.
- [ ] The certificate subject, expiration, key custody, timestamp service, and revocation plan are
  documented.
- [ ] Create an empty **draft** GitHub Release for the existing protected `vX.Y.Z` tag. Dispatch the
  workflow from that exact tag ref, not from a branch, with the same tag as input:
  `gh workflow run release.yml --repo bigfnj/desktopPet --ref $Tag -f "tag=$Tag"`. Record the exact
  40-character tag commit in the reviewed release record for public `--source-digest`
  verification. The workflow must prove that the tag commit is in protected default-branch history
  and that both named exact-SHA build checks are green. It must never create a tag, start from a
  published release, use `--clobber`, or overwrite/mix an asset.
- [ ] Confirm the unprivileged build ran every repository-built executable and the full isolated MSI
  lifecycle before signing. Confirm `sign_exe` and `sign_msi` each used a fresh protected runner,
  had no checkout, repository script, tool installation, or archive extraction, and signed exactly
  one validated file. `assemble_release` must contain all release-commit WiX/SBOM/ZIP code and no
  signing secret. Neither key-only job may execute `DesktopPet.exe`.
- [ ] Confirm `verify_n_minus_one` uploaded evidence only, `seal_release` re-downloaded pristine
  signed assets and produced the final exact set, and the signed-asset attestation job attested that
  sealed set before approving `desktopPet-release-publication`.
- [ ] Confirm the separate no-secret signed-artifact job downloaded the final artifact, verified
  Authenticode status, the pinned signer thumbprint, an RFC 3161 timestamp, and SBOM evidence, then
  passed install, installed self-tests, repair, and uninstall.
- [ ] On a clean Windows machine, run the fail-closed public procedure in `PROVENANCE.md`: first
  authenticate the build-provenance asset with `gh attestation verify` pinned to
  `bigfnj/desktopPet/.github/workflows/release.yml`, `refs/tags/vX.Y.Z`, and the reviewed source
  commit; only then extract `signing_certificate_thumbprint`. Require both the MSI and packaged EXE
  to have `Valid` Authenticode status, that exact signer thumbprint, and a timestamp.
- [ ] Under that same repository, `--signer-workflow`, `--source-ref`, and `--source-digest` policy,
  verify the GitHub provenance attestation for all six release files. Then verify the exact
  `SHA256SUMS.txt` set, the versioned SPDX JSON SBOM, packaged payloads, build-provenance record, and
  N-1 evidence.
- [ ] Release notes disclose privacy behavior, known limitations, upgrade notes, and cleared content
  scope.

## 6. Post-release and rollback

- [ ] Install both official artifacts from GitHub, not from the runner workspace, and repeat the
  workflow/tag/commit-pinned attestation, exact-signer/timestamp, checksum, and payload smoke test.
- [ ] Confirm Add/Remove Programs identity, version, publisher, install path, absent Repair action,
  upgrade, and uninstall; separately confirm explicit full-file repair from the original MSI.
- [ ] If verification fails, unpublish the release, preserve evidence, revoke or rotate signing
  material when appropriate, and issue a corrected version. Never replace assets under an existing
  tag.

If a run fails after uploading assets but before publishing, the release remains a draft. Preserve
the failed-run evidence and rerun after correcting the failure. The publication job resumes only
when every existing asset is an expected, checksum-identical subset of the newly sealed set; it
uploads only missing assets and never overwrites. Remove a draft asset only when investigation
proves it is unexpected or mismatched. Never reuse or modify a tag after any version of that
release became public.
