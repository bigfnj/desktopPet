# Releasing

DesktopPet AI Edition ships **unsigned** Windows x64 builds. To cut a release:

1. Bump `DesktopPetVersion` (and `DesktopPetAssemblyVersion`) in
   [`ProductVersion.props`](../ProductVersion.props).
2. Commit and push to `master`; confirm [`build.yml`](../.github/workflows/build.yml) is green.
3. **Run the leak soak locally** and check the growth numbers:
   `.\tests\runtime-resource-soak.ps1` → expect `"Result": "PASS"` with `Handles`/`GdiObjects`/`UserObjects`
   growth inside their bounds (16 each) and `PrivateBytes` under 64 MB. This is the only gate that catches an
   undisposed HWND, Bitmap, Font or Icon: it drives the app from outside and watches the OS counters, so no
   in-process self-test substitutes for it. It is deliberately not in the blocking CI path (it needs a real
   window station, and growth thresholds flake on a headless runner) — run it here, or trigger the
   **resource-soak** job via workflow dispatch. Record the numbers in the release notes so the next release
   has something to compare against.
4. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

[`release.yml`](../.github/workflows/release.yml) then builds the portable ZIP + MSI, writes
`SHA256SUMS.txt`, and publishes them on the GitHub release for that tag.

> The former enterprise release process — reproducible double-builds, SBOM/SPDX, code signing, and
> source-rights / pack-rights evidence gates — was retired in favor of this lean hobby-grade flow.
> Provenance for bundled third-party content is documented in
> [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md), not gated.
