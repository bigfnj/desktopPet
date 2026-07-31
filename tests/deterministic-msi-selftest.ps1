#requires -Version 5
[CmdletBinding()]
param(
    # CI/release callers keep the second verified build at the canonical path
    # for downstream parity and lifecycle tests. Local probes restore any
    # pre-existing ignored artifact by default.
    [switch]$KeepVerifiedArtifact,
    # Temp-only regression mode. This exercises preservation/restoration
    # defenses without building, installing, repairing, or uninstalling an MSI.
    [switch]$PreservationSelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Deterministic MSI self-test requires Windows Installer and WiX.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$installerScript = Join-Path $repoRoot 'installer\build-installer.ps1'
$runtimeManifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
$runtimeRoot =
    Join-Path $repoRoot 'build\DesktopPetPortable\bin\Release\x64'
$canonicalMsi = Join-Path $repoRoot 'dist\DesktopPet-AI-Edition.msi'
$canonicalWixPdb =
    Join-Path $repoRoot 'dist\DesktopPet-AI-Edition.wixpdb'
$distributionRoot = Join-Path $repoRoot 'dist'
$pathSafety = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
. $pathSafety

function Save-DesktopPetCanonicalArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$ArtifactRoot,
        [Parameter(Mandatory = $true)][string]$PreservedPath
    )

    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
        throw "Canonical artifact is missing before preservation: $ArtifactPath"
    }
    [void](Copy-DesktopPetValidatedInputFile `
        -Path $ArtifactPath `
        -Root $ArtifactRoot `
        -DestinationPath $PreservedPath)
    return [IO.Path]::GetFullPath($PreservedPath)
}

function Restore-DesktopPetCanonicalArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$ArtifactRoot,
        [Parameter(Mandatory = $true)][string]$PreservedPath,
        [Parameter(Mandatory = $true)][string]$RestoreStagingRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    if (-not (Test-Path -LiteralPath $PreservedPath -PathType Leaf)) {
        throw "Preserved canonical artifact is missing: $PreservedPath"
    }

    # Reject a hard-link/reparse destination before copying any preserved bytes
    # back to the repository. Publish-DesktopPetAtomicFile repeats this check
    # immediately before its retained-handle publication operation.
    $resolvedArtifact = Assert-DesktopPetOutputFileSafe `
        -Path $ArtifactPath `
        -TrustedRoot $ArtifactRoot `
        -ProtectedPaths @($PreservedPath) `
        -ProtectedDirectories @($RestoreStagingRoot)

    $restoreDirectory = Join-Path $RestoreStagingRoot (
        '.DesktopPet-msi-restore-' + [Guid]::NewGuid().ToString('N'))
    Reset-DesktopPetStagingDirectory `
        -Path $restoreDirectory `
        -AllowedRoot $RestoreStagingRoot `
        -TrustedRoot $TrustedRoot
    try {
        $stagedArtifact = Join-Path $restoreDirectory (
            [IO.Path]::GetFileName($resolvedArtifact) + '.restore')
        [void](Copy-DesktopPetValidatedInputFile `
            -Path $PreservedPath `
            -Root (Split-Path -Parent $PreservedPath) `
            -DestinationPath $stagedArtifact)
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $stagedArtifact `
            -DestinationPath $resolvedArtifact `
            -TrustedRoot $TrustedRoot `
            -ProtectedPaths @($PreservedPath))
    }
    finally {
        if (Test-Path -LiteralPath $restoreDirectory) {
            Remove-DesktopPetSafeDirectory `
                -Path $restoreDirectory `
                -AllowedRoot $RestoreStagingRoot `
                -TrustedRoot $TrustedRoot
        }
    }
}

function Remove-DesktopPetGeneratedCanonicalArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$ArtifactRoot
    )

    $resolvedArtifact = Assert-DesktopPetOutputFileSafe `
        -Path $ArtifactPath `
        -TrustedRoot $ArtifactRoot
    if (Test-Path -LiteralPath $resolvedArtifact -PathType Leaf) {
        Remove-DesktopPetTreeNode `
            -Path $resolvedArtifact `
            -AllowedRoot $ArtifactRoot `
            -AllowedFinalRoot (
                Get-DesktopPetFinalPath -Path $ArtifactRoot) `
            -TrustedRoot $ArtifactRoot
    }
}

function Invoke-DesktopPetMsiPreservationSelfTest {
    $fixtureTempRoot =
        Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
    $fixtureRoot = Join-Path $fixtureTempRoot (
        'DesktopPet-MsiPreservationSelfTest-' +
        [Guid]::NewGuid().ToString('N'))
    $fixtureArtifacts = Join-Path $fixtureRoot 'dist'
    $fixturePreserved = Join-Path $fixtureRoot 'preserved'
    $fixtureRestoreRoot = Join-Path $fixtureRoot 'restore'
    $fixtureOutside = Join-Path $fixtureRoot 'outside'
    $fixtureCanonical =
        Join-Path $fixtureArtifacts 'DesktopPet-AI-Edition.msi'
    $fixtureBackup =
        Join-Path $fixturePreserved 'DesktopPet-AI-Edition.msi'
    $outsideSentinel = Join-Path $fixtureOutside 'outside-sentinel.bin'

    try {
        New-Item -ItemType Directory -Path @(
            $fixtureArtifacts,
            $fixturePreserved,
            $fixtureRestoreRoot,
            $fixtureOutside) -Force | Out-Null
        [IO.File]::WriteAllText(
            $outsideSentinel,
            'outside-bytes-must-survive',
            (New-Object Text.UTF8Encoding($false)))
        $outsideHash = (
            Get-FileHash -LiteralPath $outsideSentinel -Algorithm SHA256).Hash
        New-Item `
            -ItemType HardLink `
            -Path $fixtureCanonical `
            -Target $outsideSentinel `
            -ErrorAction Stop | Out-Null

        $preservationRejected = $false
        try {
            [void](Save-DesktopPetCanonicalArtifact `
                -ArtifactPath $fixtureCanonical `
                -ArtifactRoot $fixtureArtifacts `
                -PreservedPath $fixtureBackup)
        }
        catch {
            $preservationRejected =
                $_.Exception.Message -match '(?i)hard-link alias'
        }
        if (-not $preservationRejected -or
            (Test-Path -LiteralPath $fixtureBackup)) {
            throw (
                'MSI preservation accepted a hard-linked canonical artifact.')
        }

        [IO.File]::WriteAllText(
            $fixtureBackup,
            'preserved-bytes-must-not-reach-outside',
            (New-Object Text.UTF8Encoding($false)))
        $restorationRejected = $false
        try {
            Restore-DesktopPetCanonicalArtifact `
                -ArtifactPath $fixtureCanonical `
                -ArtifactRoot $fixtureArtifacts `
                -PreservedPath $fixtureBackup `
                -RestoreStagingRoot $fixtureRestoreRoot `
                -TrustedRoot $fixtureRoot
        }
        catch {
            $restorationRejected =
                $_.Exception.Message -match '(?i)hard-link alias'
        }
        if (-not $restorationRejected -or
            (Get-FileHash `
                -LiteralPath $outsideSentinel `
                -Algorithm SHA256).Hash -cne $outsideHash) {
            throw (
                'MSI restoration did not reject the hard-link alias without ' +
                'modifying its external sentinel.')
        }
        if (@(Get-ChildItem `
                -LiteralPath $fixtureRestoreRoot `
                -Directory `
                -Filter '.DesktopPet-msi-restore-*').Count -ne 0) {
            throw 'Rejected MSI restoration left a private staging directory.'
        }

        Remove-Item -LiteralPath $fixtureCanonical -Force
        Remove-Item -LiteralPath $fixtureBackup -Force
        [IO.File]::WriteAllText(
            $fixtureCanonical,
            'last-good-canonical-bytes',
            (New-Object Text.UTF8Encoding($false)))
        [void](Save-DesktopPetCanonicalArtifact `
            -ArtifactPath $fixtureCanonical `
            -ArtifactRoot $fixtureArtifacts `
            -PreservedPath $fixtureBackup)
        [IO.File]::WriteAllText(
            $fixtureCanonical,
            'generated-probe-bytes',
            (New-Object Text.UTF8Encoding($false)))
        Restore-DesktopPetCanonicalArtifact `
            -ArtifactPath $fixtureCanonical `
            -ArtifactRoot $fixtureArtifacts `
            -PreservedPath $fixtureBackup `
            -RestoreStagingRoot $fixtureRestoreRoot `
            -TrustedRoot $fixtureRoot
        if ([IO.File]::ReadAllText($fixtureCanonical) -cne
            'last-good-canonical-bytes') {
            throw 'Normal MSI artifact restoration did not restore exact bytes.'
        }

        Remove-Item -LiteralPath $fixtureCanonical -Force
        New-Item `
            -ItemType HardLink `
            -Path $fixtureCanonical `
            -Target $outsideSentinel `
            -ErrorAction Stop | Out-Null
        $removalRejected = $false
        try {
            Remove-DesktopPetGeneratedCanonicalArtifact `
                -ArtifactPath $fixtureCanonical `
                -ArtifactRoot $fixtureArtifacts
        }
        catch {
            $removalRejected =
                $_.Exception.Message -match '(?i)hard-link alias'
        }
        if (-not $removalRejected -or
            (Get-FileHash `
                -LiteralPath $outsideSentinel `
                -Algorithm SHA256).Hash -cne $outsideHash) {
            throw (
                'MSI cleanup did not reject a hard-link alias without ' +
                'modifying its external sentinel.')
        }

        Write-Host (
            'PASS: MSI preservation/restoration rejects hard-link aliases, ' +
            'preserves the external sentinel, and restores normal bytes.')
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-DesktopPetSafeDirectory `
                -Path $fixtureRoot `
                -AllowedRoot $fixtureTempRoot `
                -TrustedRoot $fixtureTempRoot
        }
    }
}

if ($PreservationSelfTest) {
    Invoke-DesktopPetMsiPreservationSelfTest
    return
}

foreach ($requiredPath in @(
        $installerScript,
        $runtimeManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Deterministic MSI self-test input is missing: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
    throw "Release runtime is missing: $runtimeRoot"
}

$runtimeFiles = @(
    Get-Content -LiteralPath $runtimeManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) {
    throw 'Runtime payload manifest is empty.'
}
foreach ($name in $runtimeFiles) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name)) {
        throw "Runtime payload entry is not a plain file name: '$name'"
    }
    $runtimePath = Join-Path $runtimeRoot $name
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "Release runtime file is missing: $runtimePath"
    }
}

$tempRoot = Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
$scratchRoot = Join-Path $tempRoot (
    'DesktopPet-DeterministicMsi-' + [Guid]::NewGuid().ToString('N'))
$preservedRoot = Join-Path $scratchRoot 'preserved-artifacts'
$firstMsi = Join-Path $scratchRoot 'first-build.msi'
$secondMsi = Join-Path $scratchRoot 'second-build.msi'
$firstWorkingDirectory =
    Join-Path $scratchRoot 'first invocation path with spaces'
$secondWorkingDirectory =
    Join-Path $scratchRoot 'second-path\different-depth'
$originalTimestamps = @{}
$preservedArtifacts = @{}
$verified = $false

function Release-ComObject {
    param($InputObject)
    if ($null -ne $InputObject -and
        [Runtime.InteropServices.Marshal]::IsComObject($InputObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $InputObject)
    }
}

function Get-MsiIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    $summary = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase(
            [IO.Path]::GetFullPath($Path),
            0)
        $view = $database.OpenView(
            "SELECT ``Value`` FROM ``Property`` " +
            "WHERE ``Property``='ProductCode'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI has no ProductCode: $Path"
        }
        $productCode = [string]$record.StringData(1)
        $summary = $database.SummaryInformation(0)
        $packageCode = [string]$summary.Property(9)
        if ($productCode -notmatch
                '^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$' -or
            $packageCode -notmatch
                '^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$') {
            throw "MSI identity is malformed: $Path"
        }
        return [pscustomobject]@{
            ProductCode = $productCode.ToUpperInvariant()
            PackageCode = $packageCode.ToUpperInvariant()
        }
    }
    finally {
        foreach ($value in @(
                $summary,
                $record,
                $view,
                $database,
                $installer)) {
            Release-ComObject $value
        }
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

function Set-RuntimeProbeTimestamps {
    param([Parameter(Mandatory = $true)][DateTime]$BaseTimestamp)

    for ($index = 0; $index -lt $runtimeFiles.Count; $index++) {
        $runtimePath = Join-Path $runtimeRoot $runtimeFiles[$index]
        # Use four-second increments so values remain distinct even after the
        # two-second DOS timestamp granularity used by cabinet entries.
        (Get-Item -LiteralPath $runtimePath).LastWriteTimeUtc =
            $BaseTimestamp.AddSeconds($index * 4)
    }
}

function Invoke-RealMsiBuild {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][DateTime]$TimestampBase,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$CapturePath
    )

    New-Item -ItemType Directory -Path $WorkingDirectory -Force |
        Out-Null
    $toolTemp = Join-Path $WorkingDirectory 'tool temp'
    New-Item -ItemType Directory -Path $toolTemp -Force | Out-Null
    Set-RuntimeProbeTimestamps -BaseTimestamp $TimestampBase

    $priorTemp = $env:TEMP
    $priorTmp = $env:TMP
    Push-Location $WorkingDirectory
    try {
        # Different current/temp paths exercise path independence while the
        # two source trees retain byte-identical runtime content.
        $env:TEMP = $toolTemp
        $env:TMP = $toolTemp
        & $installerScript
    }
    finally {
        $env:TEMP = $priorTemp
        $env:TMP = $priorTmp
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $canonicalMsi -PathType Leaf)) {
        throw "$Name did not produce the canonical MSI: $canonicalMsi"
    }
    [void](Copy-DesktopPetValidatedInputFile `
        -Path $canonicalMsi `
        -Root $distributionRoot `
        -DestinationPath $CapturePath)
}

try {
    New-Item -ItemType Directory `
        -Path $scratchRoot, $preservedRoot `
        -Force | Out-Null

    foreach ($artifactPath in @($canonicalMsi, $canonicalWixPdb)) {
        if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
            $preservedPath =
                Join-Path $preservedRoot ([IO.Path]::GetFileName($artifactPath))
            $preservedArtifacts[$artifactPath] =
                Save-DesktopPetCanonicalArtifact `
                    -ArtifactPath $artifactPath `
                    -ArtifactRoot $distributionRoot `
                    -PreservedPath $preservedPath
        }
        else {
            $preservedArtifacts[$artifactPath] = $null
        }
    }
    foreach ($name in $runtimeFiles) {
        $runtimePath = Join-Path $runtimeRoot $name
        $originalTimestamps[$runtimePath] =
            (Get-Item -LiteralPath $runtimePath).LastWriteTimeUtc
    }

    Invoke-RealMsiBuild `
        -Name 'First real MSI build' `
        -TimestampBase ([DateTime]::SpecifyKind(
            [DateTime]'2005-02-03T04:05:06',
            [DateTimeKind]::Utc)) `
        -WorkingDirectory $firstWorkingDirectory `
        -CapturePath $firstMsi
    Invoke-RealMsiBuild `
        -Name 'Second real MSI build' `
        -TimestampBase ([DateTime]::SpecifyKind(
            [DateTime]'2035-11-12T13:14:16',
            [DateTimeKind]::Utc)) `
        -WorkingDirectory $secondWorkingDirectory `
        -CapturePath $secondMsi

    $firstHash =
        (Get-FileHash -LiteralPath $firstMsi -Algorithm SHA256).Hash
    $secondHash =
        (Get-FileHash -LiteralPath $secondMsi -Algorithm SHA256).Hash
    $firstIdentity = Get-MsiIdentity -Path $firstMsi
    $secondIdentity = Get-MsiIdentity -Path $secondMsi
    if ($firstHash -cne $secondHash) {
        throw (
            "Equivalent real MSI builds are not byte-identical. First " +
            "SHA-256=$firstHash; second SHA-256=$secondHash.")
    }
    if ($firstIdentity.ProductCode -cne $secondIdentity.ProductCode) {
        throw (
            "Equivalent real MSI builds have different ProductCodes: " +
            "$($firstIdentity.ProductCode) vs $($secondIdentity.ProductCode).")
    }
    if ($firstIdentity.PackageCode -cne $secondIdentity.PackageCode) {
        throw (
            "Equivalent real MSI builds have different PackageCodes: " +
            "$($firstIdentity.PackageCode) vs $($secondIdentity.PackageCode).")
    }

    $verified = $true
    Write-Host (
        (
            "PASS: two real ICE-validated MSI builds are byte-identical despite " +
            "different source mtimes and working/temp paths. SHA-256={0}; " +
            "ProductCode={1}; PackageCode={2}"
        ) -f
        $firstHash,
        $firstIdentity.ProductCode,
        $firstIdentity.PackageCode
    ) -ForegroundColor Green
}
finally {
    foreach ($entry in $originalTimestamps.GetEnumerator()) {
        if (Test-Path -LiteralPath $entry.Key -PathType Leaf) {
            (Get-Item -LiteralPath $entry.Key).LastWriteTimeUtc =
                [DateTime]$entry.Value
        }
    }

    if (-not ($KeepVerifiedArtifact -and $verified)) {
        foreach ($artifactPath in @($canonicalMsi, $canonicalWixPdb)) {
            if (-not $preservedArtifacts.ContainsKey($artifactPath)) {
                continue
            }
            $preservedPath = $preservedArtifacts[$artifactPath]
            if ($null -ne $preservedPath -and
                (Test-Path -LiteralPath $preservedPath -PathType Leaf)) {
                Restore-DesktopPetCanonicalArtifact `
                    -ArtifactPath $artifactPath `
                    -ArtifactRoot $distributionRoot `
                    -PreservedPath $preservedPath `
                    -RestoreStagingRoot (Join-Path $repoRoot 'build') `
                    -TrustedRoot $repoRoot
            }
            elseif (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
                Remove-DesktopPetGeneratedCanonicalArtifact `
                    -ArtifactPath $artifactPath `
                    -ArtifactRoot $distributionRoot
            }
        }
    }

    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratchRoot `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
