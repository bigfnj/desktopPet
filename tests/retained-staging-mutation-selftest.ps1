#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'Retained staging-mutation self-test requires Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$pathSafety = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
. $pathSafety

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $tempRoot (
    'DesktopPet-RetainedStaging-' + [Guid]::NewGuid().ToString('N'))
$insideRoot = Join-Path $scratch 'inside'
$outsideRoot = Join-Path $scratch 'outside'
$outsideSentinel = Join-Path $outsideRoot 'must-survive.txt'
$script:raceHookReached = $false
$script:raceMoveBlocked = $false
$script:raceMoveSucceeded = $false
$script:raceStage = $null
$script:raceMoved = $null
$script:raceExpectedOperation = $null
$script:raceAncestorLevels = 0
$script:leafHookReached = $false
$script:leafMoveBlocked = $false
$script:leafDeleteBlocked = $false
$script:leafAliasRejected = $false
$script:leafMoveSucceeded = $false
$script:leafDeleteSucceeded = $false
$script:writeHookReached = $false
$script:writeBlocked = $false
$script:writeSucceeded = $false
$script:writeExpectedOperation = $null

function Remove-TestJunction {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Refusing to remove non-junction test path: $Path"
    }
    [IO.Directory]::Delete($item.FullName)
}

function Set-RetainedStageSwapHook {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [ValidateRange(0, 8)][int]$AncestorLevels = 0
    )

    $script:raceHookReached = $false
    $script:raceMoveBlocked = $false
    $script:raceMoveSucceeded = $false
    $script:raceStage = $null
    $script:raceMoved = $null
    $script:raceExpectedOperation = $Operation
    $script:raceAncestorLevels = $AncestorLevels
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)

        if ($observedOperation -cne $script:raceExpectedOperation) {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:raceHookReached = $true
        $script:raceStage = Split-Path -Parent $observedPath
        for ($level = 0; $level -lt $script:raceAncestorLevels; $level++) {
            $script:raceStage = Split-Path -Parent $script:raceStage
        }
        $script:raceMoved = $script:raceStage + '.attacker-moved'
        try {
            Move-Item `
                -LiteralPath $script:raceStage `
                -Destination $script:raceMoved `
                -ErrorAction Stop
            $script:raceMoveSucceeded = $true
            [void](New-Item `
                -ItemType Junction `
                -Path $script:raceStage `
                -Target $outsideRoot `
                -ErrorAction Stop)
        }
        catch {
            $script:raceMoveBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
    }
}

function Test-SharingViolation {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        $win32Code = $exception.HResult -band 0xffff
        if ($win32Code -in @(5, 32, 33)) {
            return $true
        }
        $exception = $exception.InnerException
    }
    return $false
}

function Set-RetainedLeafSwapHook {
    param([Parameter(Mandatory = $true)][string]$Operation)

    $script:leafHookReached = $false
    $script:leafMoveBlocked = $false
    $script:leafDeleteBlocked = $false
    $script:leafAliasRejected = $false
    $script:leafMoveSucceeded = $false
    $script:leafDeleteSucceeded = $false
    $script:leafExpectedOperation = $Operation
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)

        if ($observedOperation -cne $script:leafExpectedOperation) {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:leafHookReached = $true
        $movedPath = $observedPath + '.attacker-moved'
        try {
            Move-Item `
                -LiteralPath $observedPath `
                -Destination $movedPath `
                -ErrorAction Stop
            $script:leafMoveSucceeded = $true
            Move-Item `
                -LiteralPath $movedPath `
                -Destination $observedPath `
                -ErrorAction Stop
        }
        catch {
            $script:leafMoveBlocked = Test-SharingViolation -ErrorRecord $_
        }
        try {
            Remove-Item -LiteralPath $observedPath -Force -ErrorAction Stop
            $script:leafDeleteSucceeded = $true
        }
        catch {
            $script:leafDeleteBlocked = Test-SharingViolation -ErrorRecord $_
        }
        try {
            New-Item `
                -ItemType HardLink `
                -Path $observedPath `
                -Target $outsideSentinel `
                -ErrorAction Stop | Out-Null
        }
        catch {
            $script:leafAliasRejected = $true
        }
    }
}

function Assert-LeafSwapBlocked {
    param([Parameter(Mandatory = $true)][string]$Name)

    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:leafHookReached -or
        -not $script:leafMoveBlocked -or
        -not $script:leafDeleteBlocked -or
        -not $script:leafAliasRejected -or
        $script:leafMoveSucceeded -or
        $script:leafDeleteSucceeded) {
        throw (
            "Retained leaf barrier failed for '$Name': " +
            "reached=$($script:leafHookReached); " +
            "moveBlocked=$($script:leafMoveBlocked); " +
            "deleteBlocked=$($script:leafDeleteBlocked); " +
            "aliasRejected=$($script:leafAliasRejected); " +
            "moveSucceeded=$($script:leafMoveSucceeded); " +
            "deleteSucceeded=$($script:leafDeleteSucceeded)")
    }
    if ([DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
            $outsideSentinel) -ne 1 -or
        [IO.File]::ReadAllText($outsideSentinel) -cne
            'external-sentinel-must-survive') {
        throw "Retained leaf probe '$Name' modified/aliased the sentinel."
    }
}

function Set-RetainedInPlaceWriteHook {
    param([Parameter(Mandatory = $true)][string]$Operation)

    $script:writeHookReached = $false
    $script:writeBlocked = $false
    $script:writeSucceeded = $false
    $script:writeExpectedOperation = $Operation
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)

        if ($observedOperation -cne
            $script:writeExpectedOperation) {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:writeHookReached = $true
        try {
            [IO.File]::WriteAllText(
                $observedPath,
                'attacker-in-place-write',
                (New-Object Text.UTF8Encoding($false)))
            $script:writeSucceeded = $true
        }
        catch {
            $script:writeBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
    }
}

function Assert-InPlaceWriteBlocked {
    param([Parameter(Mandatory = $true)][string]$Name)

    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:writeHookReached -or
        -not $script:writeBlocked -or
        $script:writeSucceeded) {
        throw (
            "Retained sealed-output barrier failed for '$Name': " +
            "reached=$($script:writeHookReached); " +
            "blocked=$($script:writeBlocked); " +
            "succeeded=$($script:writeSucceeded)")
    }
}

function Restore-RaceFixture {
    if ($null -ne $script:raceStage -and
        (Test-Path -LiteralPath $script:raceStage)) {
        $item =
            Get-Item -LiteralPath $script:raceStage -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-TestJunction -Path $script:raceStage
        }
    }
    if ($null -ne $script:raceMoved -and
        (Test-Path -LiteralPath $script:raceMoved) -and
        -not (Test-Path -LiteralPath $script:raceStage)) {
        Move-Item `
            -LiteralPath $script:raceMoved `
            -Destination $script:raceStage `
            -ErrorAction Stop
    }
}

function Assert-RaceBlocked {
    param([Parameter(Mandatory = $true)][string]$Name)

    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:raceHookReached -or
        -not $script:raceMoveBlocked -or
        $script:raceMoveSucceeded) {
        Restore-RaceFixture
        throw (
            "Retained staging barrier failed for '$Name': " +
            "reached=$($script:raceHookReached); " +
            "blocked=$($script:raceMoveBlocked); " +
            "succeeded=$($script:raceMoveSucceeded)")
    }
    if (-not (Test-Path -LiteralPath $outsideSentinel -PathType Leaf) -or
        [IO.File]::ReadAllText($outsideSentinel) -cne
            'external-sentinel-must-survive') {
        throw "Retained staging probe '$Name' modified the external sentinel."
    }
    $unexpectedOutsideEntries = @(
        Get-ChildItem -LiteralPath $outsideRoot -Force |
            Where-Object Name -CNE 'must-survive.txt'
    )
    if ($unexpectedOutsideEntries.Count -ne 0) {
        throw "Retained staging probe '$Name' redirected a write outside."
    }
}

function Assert-RetainedWindow {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$AcquireMarker,
        [Parameter(Mandatory = $true)][string]$MutationMarker,
        [Parameter(Mandatory = $true)][string]$DisposeMarker
    )

    $acquireIndex = $Source.IndexOf(
        $AcquireMarker,
        [StringComparison]::Ordinal)
    $mutationIndex = $Source.IndexOf(
        $MutationMarker,
        [StringComparison]::Ordinal)
    $disposeIndex = $Source.IndexOf(
        $DisposeMarker,
        [StringComparison]::Ordinal)
    if ($acquireIndex -lt 0 -or
        $mutationIndex -le $acquireIndex -or
        $disposeIndex -le $mutationIndex) {
        throw (
            "Staging entrypoint '$Name' does not retain its scratch lease " +
            'across the mutation hook.')
    }
}

try {
    New-Item -ItemType Directory `
        -Path $insideRoot, $outsideRoot `
        -Force | Out-Null
    [IO.File]::WriteAllText(
        $outsideSentinel,
        'external-sentinel-must-survive',
        (New-Object Text.UTF8Encoding($false)))

    $fragmentScript =
        Join-Path $repoRoot 'installer\New-RuntimeWixFragment.ps1'
    $fragmentFixture = Join-Path $insideRoot 'fragment'
    New-Item -ItemType Directory -Path $fragmentFixture | Out-Null
    $fragmentManifest = Join-Path $fragmentFixture 'runtime-files.txt'
    $fragmentOutput = Join-Path $fragmentFixture 'Runtime.generated.wxs'
    [IO.File]::WriteAllText(
        $fragmentManifest,
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    Set-RetainedStageSwapHook -Operation 'wix-fragment-stage-write'
    & {
        . $fragmentScript `
            -ManifestPath $fragmentManifest `
            -OutputPath $fragmentOutput *> $null
    }
    Assert-RaceBlocked -Name 'WiX fragment staged write'
    if (-not (Test-Path -LiteralPath $fragmentOutput -PathType Leaf)) {
        throw 'WiX fragment retained-stage probe did not publish its output.'
    }
    Set-RetainedStageSwapHook `
        -Operation 'wix-fragment-stage-write' `
        -AncestorLevels 1
    & {
        . $fragmentScript `
            -ManifestPath $fragmentManifest `
            -OutputPath $fragmentOutput *> $null
    }
    Assert-RaceBlocked -Name 'WiX fragment higher-ancestor staged write'
    Set-RetainedInPlaceWriteHook `
        -Operation 'wix-fragment-sealed-validate'
    & {
        . $fragmentScript `
            -ManifestPath $fragmentManifest `
            -OutputPath $fragmentOutput *> $null
    }
    Assert-InPlaceWriteBlocked `
        -Name 'WiX fragment sealed semantic validation'

    $zipScript =
        Join-Path $repoRoot 'packaging\New-DeterministicPortableZip.ps1'
    $zipFixture = Join-Path $insideRoot 'zip'
    $zipRuntime = Join-Path $zipFixture 'runtime'
    New-Item -ItemType Directory -Path $zipRuntime -Force | Out-Null
    $zipManifest = Join-Path $zipFixture 'runtime-files.txt'
    $zipMarker = Join-Path $zipFixture 'DesktopPet.portable'
    $zipOutput = Join-Path $zipFixture 'DesktopPet.zip'
    [IO.File]::WriteAllText(
        (Join-Path $zipRuntime 'DesktopPet.exe'),
        'runtime-bytes',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $zipManifest,
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $zipMarker,
        "portable`n",
        (New-Object Text.UTF8Encoding($false)))
    Set-RetainedStageSwapHook -Operation 'portable-zip-stage-write'
    & {
        . $zipScript `
            -RuntimeRoot $zipRuntime `
            -DestinationPath $zipOutput `
            -ManifestPath $zipManifest `
            -MarkerPath $zipMarker *> $null
    }
    Assert-RaceBlocked -Name 'deterministic ZIP staged write'
    if (-not (Test-Path -LiteralPath $zipOutput -PathType Leaf)) {
        throw 'ZIP retained-stage probe did not publish its output.'
    }
    Set-RetainedInPlaceWriteHook `
        -Operation 'portable-zip-sealed-validate'
    & {
        . $zipScript `
            -RuntimeRoot $zipRuntime `
            -DestinationPath $zipOutput `
            -ManifestPath $zipManifest `
            -MarkerPath $zipMarker *> $null
    }
    Assert-InPlaceWriteBlocked `
        -Name 'deterministic ZIP sealed semantic validation'

    $normalizerScript =
        Join-Path $repoRoot 'packaging\Normalize-MsiDeterminism.ps1'
    $normalizerFixture = Join-Path $insideRoot 'normalizer'
    New-Item -ItemType Directory -Path $normalizerFixture | Out-Null
    $malformedMsi = Join-Path $normalizerFixture 'malformed.msi'
    [IO.File]::WriteAllText(
        $malformedMsi,
        'malformed-msi-must-survive',
        (New-Object Text.UTF8Encoding($false)))
    $malformedHash = (
        Get-FileHash -LiteralPath $malformedMsi -Algorithm SHA256).Hash
    Set-RetainedStageSwapHook -Operation 'msi-normalize-stage-write'
    $normalizerRejected = $false
    try {
        & {
            . $normalizerScript `
                -MsiPath $malformedMsi `
                -IdentityNamespace 'retained-staging-selftest' *> $null
        }
    }
    catch {
        $normalizerRejected = $true
    }
    if (-not $normalizerRejected) {
        throw 'Malformed MSI unexpectedly passed deterministic normalization.'
    }
    Assert-RaceBlocked -Name 'MSI normalization staged write'
    if ((Get-FileHash `
            -LiteralPath $malformedMsi `
            -Algorithm SHA256).Hash -cne $malformedHash) {
        throw 'Failed MSI normalization modified its original input.'
    }

    $leafMalformedMsi = Join-Path $normalizerFixture 'leaf-malformed.msi'
    [IO.File]::WriteAllText(
        $leafMalformedMsi,
        'leaf-malformed-msi-must-survive',
        (New-Object Text.UTF8Encoding($false)))
    $leafMalformedHash = (
        Get-FileHash -LiteralPath $leafMalformedMsi -Algorithm SHA256).Hash
    Set-RetainedLeafSwapHook -Operation 'msi-normalize-stage-mutate'
    $leafNormalizerRejected = $false
    try {
        & {
            . $normalizerScript `
                -MsiPath $leafMalformedMsi `
                -IdentityNamespace 'retained-leaf-selftest' *> $null
        }
    }
    catch {
        $leafNormalizerRejected = $true
    }
    if (-not $leafNormalizerRejected) {
        throw 'Leaf-probe malformed MSI unexpectedly passed normalization.'
    }
    Assert-LeafSwapBlocked -Name 'MSI mutable temporary file'
    if ((Get-FileHash `
            -LiteralPath $leafMalformedMsi `
            -Algorithm SHA256).Hash -cne $leafMalformedHash) {
        throw 'MSI mutable-leaf probe modified its original input.'
    }

    foreach ($operation in @(
            'sbom-stage-write',
            'sbom-stage-mutate',
            'wix-package-stage-write',
            'wix-package-config-write',
            'wix-package-stage-mutate',
            'wix-tool-stage-write',
            'wix-provenance-stage-write')) {
        $probeRoot = Join-Path $insideRoot (
            'primitive-' + $operation)
        $probeLease = $null
        try {
            $probeLease = Open-DesktopPetNewScratchDirectory `
                -Path $probeRoot `
                -AllowedRoot $insideRoot `
                -TrustedRoot $scratch `
                -ProtectedPaths @($outsideSentinel)
            $probeFile = Join-Path $probeRoot 'payload.tmp'
            Set-RetainedStageSwapHook -Operation $operation
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation $operation `
                -Path $probeFile
            [IO.File]::WriteAllText(
                $probeFile,
                'private-staged-bytes',
                (New-Object Text.UTF8Encoding($false)))
            Assert-RaceBlocked -Name $operation
        }
        finally {
            if ($null -ne $probeLease) {
                $probeLease.Dispose()
            }
            Restore-RaceFixture
            if (Test-Path -LiteralPath $probeRoot) {
                Remove-DesktopPetSafeDirectory `
                    -Path $probeRoot `
                    -AllowedRoot $insideRoot `
                    -TrustedRoot $scratch
            }
        }
    }

    $normalizerSource = Get-Content -LiteralPath $normalizerScript -Raw
    Assert-RetainedWindow `
        -Name 'MSI normalization' `
        -Source $normalizerSource `
        -AcquireMarker '$stagingDirectoryLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'msi-normalize-stage-write'" `
        -DisposeMarker '$stagingDirectoryLease.Dispose()'

    $fragmentSource = Get-Content -LiteralPath $fragmentScript -Raw
    Assert-RetainedWindow `
        -Name 'WiX fragment generation' `
        -Source $fragmentSource `
        -AcquireMarker '$temporaryDirectoryLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'wix-fragment-stage-write'" `
        -DisposeMarker '$temporaryDirectoryLease.Dispose()'

    $zipSource = Get-Content -LiteralPath $zipScript -Raw
    Assert-RetainedWindow `
        -Name 'deterministic ZIP generation' `
        -Source $zipSource `
        -AcquireMarker '$temporaryDirectoryLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'portable-zip-stage-write'" `
        -DisposeMarker '$temporaryDirectoryLease.Dispose()'

    $sbomScript =
        Join-Path $repoRoot 'packaging\Add-RuntimeManifestToSpdx.ps1'
    $sbomSource = Get-Content -LiteralPath $sbomScript -Raw
    Assert-RetainedWindow `
        -Name 'SPDX enrichment' `
        -Source $sbomSource `
        -AcquireMarker '$sbomStagingDirectoryLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'sbom-stage-write'" `
        -DisposeMarker '$sbomStagingDirectoryLease.Dispose()'

    $wixToolchainScript =
        Join-Path $repoRoot 'packaging\Install-LockedWixToolchain.ps1'
    $wixToolchainSource =
        Get-Content -LiteralPath $wixToolchainScript -Raw
    Assert-RetainedWindow `
        -Name 'locked WiX package staging' `
        -Source $wixToolchainSource `
        -AcquireMarker '$resolvedPackageRootLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'wix-package-stage-write'" `
        -DisposeMarker '$resolvedPackageRootLease.Dispose()'
    Assert-RetainedWindow `
        -Name 'locked WiX tool staging' `
        -Source $wixToolchainSource `
        -AcquireMarker '$toolPathLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'wix-tool-stage-write'" `
        -DisposeMarker '$toolPathLease.Dispose()'
    Assert-RetainedWindow `
        -Name 'locked WiX provenance staging' `
        -Source $wixToolchainSource `
        -AcquireMarker '$provenanceStagingLease = Open-DesktopPetNewScratchDirectory' `
        -MutationMarker "'wix-provenance-stage-write'" `
        -DisposeMarker '$provenanceStagingLease.Dispose()'
}
finally {
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    Restore-RaceFixture
    if (Test-Path -LiteralPath $scratch) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}

Write-Host (
    'PASS: retained scratch leases block staged-write ancestor swaps; ' +
    'WiX fragment, deterministic ZIP, MSI normalization, SPDX, and locked ' +
    'WiX mutation hooks preserve the external sentinel.'
) -ForegroundColor Green
