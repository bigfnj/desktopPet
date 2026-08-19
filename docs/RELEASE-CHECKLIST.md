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
   Then, if any module owns a window, **run the module-window soak too**:
   `.\tests\module-window-soak.ps1` → expect `RESULT=PASS`. The soak above cannot reach a module window at
   all — it drives the shipped app from outside and the app's churn loop never opens one — so this is the only
   check covering a module's own HWNDs, Bitmaps and decoded sprites. It compares the LAST segment against the
   previous one rather than against a cold start, because the first pass legitimately sets a high private-byte
   watermark while a sprite sheet decodes. Record these numbers too.
4. **Walk the live smoke script** below. Everything above is a self-test: it proves invariants, not that the
   app still works. This is the class of check that caught the S6p2 UI, a stale install being debugged as if
   it were current, and the OCR mojibake — none of which any automated gate noticed.
5. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.

## Live smoke script

Install the built MSI over the previous version (never onto a clean machine only — the upgrade path is the
one users take), then:

| # | Check | Watch for |
|---|---|---|
| 1 | Pets spawn at startup | the persisted mix restores, not a default single pet |
| 2 | Walk / fall / animate | no stuck sprite, no flicker at screen edges |
| 3 | Right-click a pet | a fortune speaks, bubble sized to the text |
| 4 | Let it idle for the drop interval | an unprompted fortune arrives |
| 5 | Tray → Add a pet / Remove a pet | the mix changes, and **survives a restart** |
| 6 | Options → every pane opens, Apply, reopen | values persisted; no pane throws |
| 7 | Options → Modules → Check online | installed rows show versions; updates offered where newer |
| 8 | Install → Update → Uninstall a module | each restarts cleanly; an update KEEPS module settings |
| 9 | AI Brain (if configured) → Ask | one answer, correctly sized bubble, no mojibake in quoted text |
| 10 | After an ABI change only | installed `DesktopPet.Contracts.dll` FileVersion matches the new product version |

Row 10 is the one that silently breaks: Windows Installer skips refreshing a file whose version did not
change, so an ABI change shipped without a `ProductVersion.props` bump installs a stale `Contracts.dll` and
every module fails to resolve the new types.

[`release.yml`](../.github/workflows/release.yml) then builds the portable ZIP + MSI, writes
`SHA256SUMS.txt`, and publishes them on the GitHub release for that tag.

## Modules are a separate publish

Modules do **not** ship with a release. `modules-dist/` is served off `master` via raw.githubusercontent, so
**merging to master is the module publish** — it reaches every existing user with no tag involved. Use:

```powershell
.\packaging\New-ModulePublish.ps1 -ModuleId <id> -Commit
```

It builds, zips, updates `modules-dist/modules.json`, commits, regenerates `catalog.json` and verifies, in the
one order that works: the catalog records the SHA-256 of the **committed** git blob, so the zip must be
committed before the catalog is generated. It refuses to continue otherwise.

Sequencing that matters when a module needs a new host: publish the module only **after** the host release it
declares in `MinHostVersion` has shipped, or the catalog offers users a module their host correctly refuses.
That is why Pet Studio 1.1.0 was published after `v1.4.6`, not with it.

> The former enterprise release process — reproducible double-builds, SBOM/SPDX, code signing, and
> source-rights / pack-rights evidence gates — was retired in favor of this lean hobby-grade flow.
> Provenance for bundled third-party content is documented in
> [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md), not gated.
