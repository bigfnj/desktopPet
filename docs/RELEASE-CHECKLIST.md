# Releasing

DesktopPet AI Edition ships **unsigned** Windows x64 builds. To cut a release:

1. Bump `DesktopPetVersion` (and `DesktopPetAssemblyVersion`) in
   [`ProductVersion.props`](../ProductVersion.props).
2. Commit and push to `master`; confirm [`build.yml`](../.github/workflows/build.yml) is green.
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

[`release.yml`](../.github/workflows/release.yml) then builds the portable ZIP + MSI, writes
`SHA256SUMS.txt`, and publishes them on the GitHub release for that tag.

> The former enterprise release process — reproducible double-builds, SBOM/SPDX, code signing, and
> source-rights / pack-rights evidence gates — was retired in favor of this lean hobby-grade flow.
> Provenance for bundled third-party content is documented in
> [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md), not gated.
