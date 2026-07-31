#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$CoreTestsExecutable,
    [string]$RuntimeHardeningScript,
    [string]$SbomRefreshScript,
    [string]$SbomInventoryScript,
    [string]$PetTesterExecutable,
    [string]$PetTesterHardeningScript,
    [string[]]$RepositorySmokeScripts,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($CoreTestsExecutable)) {
    $CoreTestsExecutable = Join-Path $scriptDirectory (
        '..\tests\DesktopPet.CoreTests\bin\Release\DesktopPet.CoreTests.exe')
}
if ([string]::IsNullOrWhiteSpace($RuntimeHardeningScript)) {
    $RuntimeHardeningScript =
        Join-Path $scriptDirectory '..\tests\runtime-hardening-selftest.ps1'
}
if ([string]::IsNullOrWhiteSpace($SbomRefreshScript)) {
    $SbomRefreshScript =
        Join-Path $scriptDirectory '..\tests\sbom-runtime-refresh-selftest.ps1'
}
if ([string]::IsNullOrWhiteSpace($SbomInventoryScript)) {
    $SbomInventoryScript =
        Join-Path $scriptDirectory '..\tests\sbom-inventory-negative-selftest.ps1'
}
if ([string]::IsNullOrWhiteSpace($PetTesterExecutable)) {
    $PetTesterExecutable = Join-Path $scriptDirectory (
        '..\build\PetTester\bin\Release\x64\PetTester.exe')
}
if ([string]::IsNullOrWhiteSpace($PetTesterHardeningScript)) {
    $PetTesterHardeningScript =
        Join-Path $scriptDirectory '..\tests\pettester-hardening-selftest.ps1'
}
if ($null -eq $RepositorySmokeScripts -or
    $RepositorySmokeScripts.Count -eq 0) {
    $RepositorySmokeScripts = @(
        (Join-Path $scriptDirectory '..\tests\deterministic-portable-zip-selftest.ps1'),
        (Join-Path $scriptDirectory '..\tests\offline-help-selftest.ps1'),
        (Join-Path $scriptDirectory '..\tests\documentation-boundary-selftest.ps1'),
        (Join-Path $scriptDirectory '..\tests\corpus-provenance-hardening-selftest.ps1')
    )
}

. (Join-Path $scriptDirectory 'ReleaseGate.PathPolicy.ps1')

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    try {
        if ($Process.HasExited) { return }
    }
    catch {
        return
    }

    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
    }
    else {
        try { $Process.Kill() } catch { }
    }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogBaseName
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    Set-ReleaseGateProcessArguments `
        -StartInfo $startInfo `
        -ArgumentList $ArgumentList
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "$Name could not be started."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree -Process $process
            throw "$Name timed out after $TimeoutSeconds seconds."
        }
        # A parameterless wait flushes redirected asynchronous output after exit.
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $rawExitCode = $process.ExitCode
        if ($null -eq $rawExitCode) {
            throw "$Name completed without an observable exit code."
        }
        $exitCode = [int]$rawExitCode
        if ($exitCode -ne 0) {
            throw "$Name exited with code $exitCode.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
        }
        return [pscustomobject]@{
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$resolvedCoreTests = (Resolve-Path -LiteralPath $CoreTestsExecutable).Path
$resolvedRuntimeScript = (Resolve-Path -LiteralPath $RuntimeHardeningScript).Path
$resolvedSbomRefreshScript = (Resolve-Path -LiteralPath $SbomRefreshScript).Path
$resolvedSbomInventoryScript =
    (Resolve-Path -LiteralPath $SbomInventoryScript).Path
$resolvedPetTester = (Resolve-Path -LiteralPath $PetTesterExecutable).Path
$resolvedPetTesterScript =
    (Resolve-Path -LiteralPath $PetTesterHardeningScript).Path
$resolvedRepositorySmokeScripts = @(
    $RepositorySmokeScripts |
        ForEach-Object { (Resolve-Path -LiteralPath $_).Path }
)
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\')
$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-ProductTests-' + [Guid]::NewGuid().ToString('N'))
$originalTemp = $env:TEMP
$originalTmp = $env:TMP

try {
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    # DesktopPet self-tests use Path.GetTempPath() for result files. Giving every
    # invocation a private root prevents stale results and concurrent-job collisions.
    $env:TEMP = $scratchRoot
    $env:TMP = $scratchRoot

    $releasePdb = [IO.Path]::ChangeExtension($resolvedExecutable, '.pdb')
    if (Test-Path -LiteralPath $releasePdb -PathType Leaf) {
        throw "Release executable has an undistributed symbol sidecar: $releasePdb"
    }
    $binaryBytes = [IO.File]::ReadAllBytes($resolvedExecutable)
    $binaryAscii = [Text.Encoding]::ASCII.GetString($binaryBytes)
    $binaryUnicode = [Text.Encoding]::Unicode.GetString($binaryBytes)
    if ($binaryAscii.IndexOf(
            $repoRoot,
            [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $binaryUnicode.IndexOf(
            $repoRoot,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Release executable embeds the absolute repository checkout path.'
    }
    Write-Host 'Release executable contains no PDB sidecar or checkout-root path.'

    $core = Invoke-BoundedProcess `
        -Name 'core regression tests' `
        -FilePath $resolvedCoreTests `
        -WorkingDirectory (Split-Path -Parent $resolvedCoreTests) `
        -LogBaseName 'core'
    Write-Host '----- core regression tests -----'
    if (-not [string]::IsNullOrWhiteSpace($core.StdOut)) { Write-Host $core.StdOut }
    if (-not [string]::IsNullOrWhiteSpace($core.StdErr)) { Write-Host $core.StdErr }
    if ($core.StdOut -notmatch 'PASS: 20 DesktopPet core regression groups\.') {
        throw 'Core regression tests did not report the expected 20-group PASS summary.'
    }

    $tests = @(
        @{
            Name = 'embedder'
            Argument = '--embed-selftest'
            Log = 'dp-embed-selftest.txt'
            Required = @('IsReady=True', 'dim=384')
        },
        @{
            Name = 'smart fortunes'
            Argument = '--smart-selftest'
            Log = 'dp-smart-selftest.txt'
            Required = @('warmed=True', 'contextual_picks=3/3', 'RESULT=PASS')
        },
        @{
            Name = 'fortune filters'
            Argument = '--filter-selftest'
            Log = 'dp-filter-selftest.txt'
            Required = @('RESULT=PASS')
        },
        @{
            Name = 'security'
            Argument = '--security-selftest'
            Log = $null
            Required = @()
        }
    )

    $testIndex = 0
    foreach ($test in $tests) {
        $testIndex++
        $resultPath = if ($test.Log) {
            Join-Path $scratchRoot $test.Log
        }
        else {
            $null
        }
        try {
            $run = Invoke-BoundedProcess `
                -Name "$($test.Name) self-test" `
                -FilePath $resolvedExecutable `
                -ArgumentList @($test.Argument) `
                -WorkingDirectory (Split-Path -Parent $resolvedExecutable) `
                -LogBaseName ("product-{0:D2}" -f $testIndex)
        }
        catch {
            if ($resultPath -and
                (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
                $failureResult =
                    Get-Content -LiteralPath $resultPath -Raw
                throw (
                    $_.Exception.Message +
                    [Environment]::NewLine +
                    'RESULT LOG:' +
                    [Environment]::NewLine +
                    $failureResult)
            }
            throw
        }

        Write-Host "----- $($test.Name) -----"
        if (-not [string]::IsNullOrWhiteSpace($run.StdOut)) { Write-Host $run.StdOut }
        if (-not [string]::IsNullOrWhiteSpace($run.StdErr)) { Write-Host $run.StdErr }
        if (-not $test.Log) {
            Write-Host "$($test.Name) self-test exited successfully."
            continue
        }

        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw "$($test.Name) self-test produced no result log in its isolated TEMP directory."
        }
        $result = Get-Content -LiteralPath $resultPath -Raw
        Write-Host $result
        if ($result -match '\*\*\*FAIL\*\*\*' -or $result -match '(^|\r?\n)EXC[: ]') {
            throw "$($test.Name) self-test reported a failure."
        }
        foreach ($requiredPattern in $test.Required) {
            if ($result -notmatch [regex]::Escape($requiredPattern)) {
                throw "$($test.Name) self-test did not report '$requiredPattern'."
            }
        }
    }

    $powerShell = (Get-Process -Id $PID).Path
    $runtimeArguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $resolvedRuntimeScript,
        '-ExecutablePath', $resolvedExecutable
    )
    $runtime = Invoke-BoundedProcess `
        -Name 'runtime hardening regression harness' `
        -FilePath $powerShell `
        -ArgumentList $runtimeArguments `
        -WorkingDirectory (Split-Path -Parent $resolvedRuntimeScript) `
        -LogBaseName 'runtime-hardening'
    Write-Host '----- runtime hardening -----'
    if (-not [string]::IsNullOrWhiteSpace($runtime.StdOut)) { Write-Host $runtime.StdOut }
    if (-not [string]::IsNullOrWhiteSpace($runtime.StdErr)) { Write-Host $runtime.StdErr }
    if ($runtime.StdOut -notmatch 'PASS: focused runtime hardening regression harness\.') {
        throw 'Runtime hardening harness did not report its PASS summary.'
    }

    $petTesterArguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $resolvedPetTesterScript,
        '-PetTesterExecutable', $resolvedPetTester
    )
    $petTester = Invoke-BoundedProcess `
        -Name 'PetTester hardening regression harness' `
        -FilePath $powerShell `
        -ArgumentList $petTesterArguments `
        -WorkingDirectory (Split-Path -Parent $resolvedPetTesterScript) `
        -LogBaseName 'pettester-hardening'
    Write-Host '----- PetTester hardening -----'
    if (-not [string]::IsNullOrWhiteSpace($petTester.StdOut)) {
        Write-Host $petTester.StdOut
    }
    if (-not [string]::IsNullOrWhiteSpace($petTester.StdErr)) {
        Write-Host $petTester.StdErr
    }
    if ($petTester.StdOut -notmatch
        'PASS: focused PetTester hardening regression harness\.') {
        throw 'PetTester hardening harness did not report its PASS summary.'
    }

    foreach ($smokeScript in $resolvedRepositorySmokeScripts) {
        $smokeName = [IO.Path]::GetFileNameWithoutExtension($smokeScript)
        $smokeArguments = @(
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy', 'Bypass',
            '-File', $smokeScript
        )
        $smoke = Invoke-BoundedProcess `
            -Name $smokeName `
            -FilePath $powerShell `
            -ArgumentList $smokeArguments `
            -WorkingDirectory (Split-Path -Parent $smokeScript) `
            -LogBaseName $smokeName
        Write-Host "----- $smokeName -----"
        if (-not [string]::IsNullOrWhiteSpace($smoke.StdOut)) {
            Write-Host $smoke.StdOut
        }
        if (-not [string]::IsNullOrWhiteSpace($smoke.StdErr)) {
            Write-Host $smoke.StdErr
        }
        if ($smoke.StdOut -notmatch '(?m)^PASS:') {
            throw "$smokeName did not report a PASS summary."
        }
    }

    $sbomArguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $resolvedSbomRefreshScript
    )
    $sbomRefresh = Invoke-BoundedProcess `
        -Name 'post-sign SBOM refresh regression harness' `
        -FilePath $powerShell `
        -ArgumentList $sbomArguments `
        -WorkingDirectory (Split-Path -Parent $resolvedSbomRefreshScript) `
        -LogBaseName 'sbom-refresh'
    Write-Host '----- post-sign SBOM refresh -----'
    if (-not [string]::IsNullOrWhiteSpace($sbomRefresh.StdOut)) {
        Write-Host $sbomRefresh.StdOut
    }
    if (-not [string]::IsNullOrWhiteSpace($sbomRefresh.StdErr)) {
        Write-Host $sbomRefresh.StdErr
    }
    if ($sbomRefresh.StdOut -notmatch
        'PASS: post-sign SBOM runtime evidence refresh self-test\.') {
        throw 'Post-sign SBOM refresh harness did not report its PASS summary.'
    }

    $sbomInventoryArguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $resolvedSbomInventoryScript
    )
    $sbomInventory = Invoke-BoundedProcess `
        -Name 'SBOM inventory negative-control harness' `
        -FilePath $powerShell `
        -ArgumentList $sbomInventoryArguments `
        -WorkingDirectory (Split-Path -Parent $resolvedSbomInventoryScript) `
        -LogBaseName 'sbom-inventory'
    Write-Host '----- SBOM inventory negative controls -----'
    if (-not [string]::IsNullOrWhiteSpace($sbomInventory.StdOut)) {
        Write-Host $sbomInventory.StdOut
    }
    if (-not [string]::IsNullOrWhiteSpace($sbomInventory.StdErr)) {
        Write-Host $sbomInventory.StdErr
    }
    if ($sbomInventory.StdOut -notmatch
        'PASS: SBOM inventory baseline and \d+ fail-closed negative controls\.') {
        throw 'SBOM inventory negative-control harness did not report its PASS summary.'
    }

    Write-Host 'All isolated, bounded product self-tests passed.' -ForegroundColor Green
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTempRoot + '\DesktopPet-ProductTests-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
