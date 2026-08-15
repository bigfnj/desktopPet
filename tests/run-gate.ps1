#requires -Version 5
<#
.SYNOPSIS
    Run the full local verification gate: build, core tests, every app self-test flag, the source-text
    invariants, and the module publish/version checks.

.DESCRIPTION
    One command so the gate is run the same way every time, and so a self-test cannot quietly report success
    without having run. That second point is not hypothetical: the module self-tests skip-PASS when their
    module folder is absent (correct behavior for a payload with no dev modules), which means a build that
    silently failed to produce modules/ looks identical to a clean run. This script fails on a SKIP.

    Mirrors .github/workflows/build.yml's flag list. The leak soak is deliberately NOT part of this gate --
    see docs/RELEASE-CHECKLIST.md; it is a pre-tag step because OS growth thresholds are too flaky to run
    on every change.

.EXAMPLE
    .\tests\run-gate.ps1
.EXAMPLE
    .\tests\run-gate.ps1 -SkipClean      # faster re-run when nothing structural changed
#>
[CmdletBinding()]
param(
    [switch]$SkipClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Push-Location $repoRoot
try {
    $failures = New-Object 'Collections.Generic.List[string]'

    Write-Host '=== build (Release x64, host + modules)' -ForegroundColor Cyan
    # NOTE: never pipe build.ps1 through Select-Object -First; that short-circuits the upstream pipeline and
    # terminates the build partway, which silently skips the module builds.
    # Hashtable splat, not an array: array elements bind positionally, so '-Release' would arrive as a value
    # rather than a switch.
    $buildParams = @{ Release = $true }
    if (-not $SkipClean) { $buildParams['Clean'] = $true }
    & (Join-Path $repoRoot 'build.ps1') @buildParams
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit $LASTEXITCODE)." }

    $outputRoot = Join-Path $repoRoot 'build\DesktopPetPortable\bin\Release\x64'
    $exe = Join-Path $outputRoot 'DesktopPet.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "The built executable is missing: $exe" }
    foreach ($moduleId in 'testmodule', 'fortunes', 'aibrain') {
        if (-not (Test-Path -LiteralPath (Join-Path $outputRoot "modules\$moduleId"))) {
            throw "Module '$moduleId' is missing from the build output; its self-test would skip-pass."
        }
    }

    Write-Host '=== core regression tests' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'tests\DesktopPet.CoreTests\DesktopPet.CoreTests.csproj') `
        -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'CoreTests build failed.' }
    & (Join-Path $repoRoot 'tests\DesktopPet.CoreTests\bin\Release\DesktopPet.CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { $failures.Add('CoreTests') }

    # Flag -> the marker file it writes, so a SKIP can be detected. Keep in sync with build.yml.
    $flags = [ordered]@{
        '--security-selftest'                = $null
        '--catalog-selftest'                 = $null
        '--fullscreen-selftest'              = $null
        '--pettyperegistry-selftest'         = 'dp-pettyperegistry-selftest.txt'
        '--hardening-selftest'               = $null
        '--module-host-selftest'             = 'dp-module-host-selftest.txt'
        '--fortunes-selftest'                = 'dp-fortunes-selftest.txt'
        '--fortunes-engine-selftest'         = 'dp-fortunes-engine-selftest.txt'
        '--aibrain-selftest'                 = 'dp-aibrain-selftest.txt'
        '--wpf-options-selftest'             = $null
        '--fortunes-smart-progress-selftest' = 'dp-fortunes-smart-progress-selftest.txt'
    }

    Write-Host '=== app self-tests' -ForegroundColor Cyan
    foreach ($flag in $flags.Keys) {
        $marker = $flags[$flag]
        if ($marker) {
            $markerPath = Join-Path $env:TEMP $marker
            if (Test-Path -LiteralPath $markerPath) { Remove-Item -LiteralPath $markerPath -Force }
        }
        # A GUI exe does not block PowerShell, so wait explicitly. Child output is captured rather than
        # inherited: these self-tests print hundreds of PASS lines each, which buries the summary. The log is
        # echoed only when something fails.
        $log = Join-Path $env:TEMP ("dp-gate-" + $flag.Trim('-') + ".log")
        $process = Start-Process -FilePath $exe -ArgumentList $flag -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        if ($process.ExitCode -ne 0) {
            $failures.Add("$flag (exit $($process.ExitCode))")
            Write-Host ("  FAIL  {0}" -f $flag) -ForegroundColor Red
            foreach ($logPath in @($log, "$log.err")) {
                if (Test-Path -LiteralPath $logPath) {
                    Get-Content -LiteralPath $logPath | Select-Object -Last 40 | ForEach-Object { Write-Host "        $_" }
                }
            }
            continue
        }
        if ($marker) {
            $markerPath = Join-Path $env:TEMP $marker
            if (-not (Test-Path -LiteralPath $markerPath)) {
                $failures.Add("$flag (wrote no marker file)")
                Write-Host ("  FAIL  {0} -- no marker" -f $flag) -ForegroundColor Red
                continue
            }
            $skips = @(Select-String -LiteralPath $markerPath -Pattern '^SKIP:')
            if ($skips.Count -gt 0) {
                $failures.Add("$flag (SKIPPED: $($skips[0].Line.Trim()))")
                Write-Host ("  FAIL  {0} -- skipped, did not actually run" -f $flag) -ForegroundColor Red
                continue
            }
        }
        Write-Host ("  ok    {0}" -f $flag) -ForegroundColor DarkGray
    }

    Write-Host '=== source-text invariants' -ForegroundColor Cyan
    & (Join-Path $repoRoot 'tests\runtime-hardening-selftest.ps1')
    if ($LASTEXITCODE -ne 0) { $failures.Add('runtime-hardening-selftest.ps1') }

    Write-Host '=== published module payloads' -ForegroundColor Cyan
    & (Join-Path $repoRoot 'packaging\Test-ModulePublishFreshness.ps1')
    if ($LASTEXITCODE -ne 0) { $failures.Add('Test-ModulePublishFreshness.ps1') }

    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Host "GATE FAILED:" -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'GATE PASSED (build 0 warnings, core tests, 11 self-tests with no skips, invariants, payloads).' -ForegroundColor Green
}
finally {
    Pop-Location
}