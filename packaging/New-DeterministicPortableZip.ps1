#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [string]$ManifestPath,
    [string]$MarkerPath,
    # Optional caller policy runs against the completed private archive before
    # mandatory readback verification and atomic publication.
    [scriptblock]$AdditionalStagedArchiveValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $scriptDirectory 'runtime-files.txt'
}
if ([string]::IsNullOrWhiteSpace($MarkerPath)) {
    $MarkerPath = Join-Path $scriptDirectory 'DesktopPet.portable'
}
$pathSafety = Join-Path $scriptDirectory 'StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $pathSafety -PathType Leaf)) {
    throw "Packaging path-safety policy is missing: $pathSafety"
}
. $pathSafety
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-DesktopPetStreamSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][IO.Stream]$Stream)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Stream)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-DesktopPetStagedPortableZip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$SealedArchiveFile,
        [Parameter(Mandatory = $true)][string[]]$ExpectedEntryNames,
        [Parameter(Mandatory = $true)][hashtable]$EntrySources,
        [Parameter(Mandatory = $true)][DateTimeOffset]$NormalizedTimestamp
    )

    $archiveStream = $null
    $archive = $null
    try {
        # Validate a memory copy read from the exact retained object. Reopening
        # the lexical path would create a rename/substitution handoff.
        $archiveStream = New-Object IO.MemoryStream
        $SealedArchiveFile.CopyTo($archiveStream)
        $archiveStream.Position = 0
        $archive = New-Object IO.Compression.ZipArchive(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Read,
            $false)

        if ($archive.Entries.Count -ne $ExpectedEntryNames.Count) {
            throw (
                "entry count $($archive.Entries.Count) does not match " +
                "expected count $($ExpectedEntryNames.Count)")
        }
        for ($index = 0; $index -lt $ExpectedEntryNames.Count; $index++) {
            $expectedName = $ExpectedEntryNames[$index]
            $entry = $archive.Entries[$index]
            if ([string]$entry.FullName -cne $expectedName) {
                throw (
                    "entry $index is '$($entry.FullName)', expected " +
                    "'$expectedName'")
            }
            if ($entry.LastWriteTime.DateTime -ne
                $NormalizedTimestamp.DateTime) {
                throw "entry '$expectedName' has a non-normalized timestamp"
            }
            if ([int]$entry.ExternalAttributes -ne 0) {
                throw "entry '$expectedName' has nonzero external attributes"
            }

            $sourceInput = $EntrySources[$expectedName]
            if ($null -eq $sourceInput) {
                throw "entry '$expectedName' has no retained source"
            }
            if ([long]$entry.Length -ne [long]$sourceInput.Length) {
                throw (
                    "entry '$expectedName' length $($entry.Length) does not " +
                    "match source length $($sourceInput.Length)")
            }

            $entryStream = $entry.Open()
            try {
                $entryHash = Get-DesktopPetStreamSha256 -Stream $entryStream
            }
            finally {
                $entryStream.Dispose()
            }
            $sourceHash = $sourceInput.ComputeHash('SHA256')
            if ($entryHash -cne $sourceHash) {
                throw "entry '$expectedName' differs from its retained source"
            }
        }
    }
    catch {
        throw (
            'Staged portable ZIP verification failed: ' +
            $_.Exception.Message)
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        elseif ($null -ne $archiveStream) {
            $archiveStream.Dispose()
        }
    }
}

foreach ($path in @($RuntimeRoot, $ManifestPath, $MarkerPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Portable-package input not found: $path"
    }
}
if (-not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw "Runtime root is not a directory: $RuntimeRoot"
}

$runtimeRootFull = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$destinationFull = [IO.Path]::GetFullPath($DestinationPath)
$manifestFull = [IO.Path]::GetFullPath($ManifestPath)
$markerFull = [IO.Path]::GetFullPath($MarkerPath)

$validatedInputs = New-Object 'Collections.Generic.List[IDisposable]'
$maximumManifestBytes = 1MB
try {
$manifestInput = Open-DesktopPetValidatedInputFile `
    -Path $manifestFull `
    -Root (Split-Path -Parent $manifestFull)
$validatedInputs.Add($manifestInput)
$markerInput = Open-DesktopPetValidatedInputFile `
    -Path $markerFull `
    -Root (Split-Path -Parent $markerFull)
$validatedInputs.Add($markerInput)

$runtimeFiles = @(
    $manifestInput.ReadAllTextUtf8($maximumManifestBytes) -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) {
    throw 'Runtime payload manifest is empty.'
}
if (@($runtimeFiles | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Runtime payload manifest contains duplicate entries.'
}

$entrySources = @{}
foreach ($name in $runtimeFiles) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name) -or
        [string]::Equals(
            $name,
            'DesktopPet.portable',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime payload entry is unsafe or reserved: '$name'"
    }
    $source = Join-Path $runtimeRootFull $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Runtime payload file not found: $source"
    }
    $sourceInput = Open-DesktopPetValidatedInputFile `
        -Path $source `
        -Root $runtimeRootFull
    $validatedInputs.Add($sourceInput)
    $entrySources.Add($name, $sourceInput)
}
$entrySources.Add('DesktopPet.portable', $markerInput)

$entryNames = [string[]]$entrySources.Keys
[Array]::Sort($entryNames, [StringComparer]::Ordinal)

$destinationParent = Split-Path -Parent $destinationFull
if ([string]::IsNullOrWhiteSpace($destinationParent) -or
    -not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    throw "Destination parent must already exist: $destinationParent"
}
$destinationFull = Assert-DesktopPetOutputFileSafe `
    -Path $destinationFull `
    -TrustedRoot $destinationParent `
    -ProtectedPaths @($manifestFull, $markerFull) `
    -ProtectedDirectories @($runtimeRootFull)
$zipDestinationExists = $false
$zipDestinationSha256 = $null
if (Test-Path -LiteralPath $destinationFull -PathType Leaf) {
    $zipDestinationInput = Open-DesktopPetValidatedInputFile `
        -Path $destinationFull `
        -Root $destinationParent
    try {
        $zipDestinationSha256 =
            $zipDestinationInput.ComputeHash('SHA256')
        $zipDestinationExists = $true
    }
    finally {
        $zipDestinationInput.Dispose()
    }
}
elseif (Test-Path -LiteralPath $destinationFull) {
    throw "Portable ZIP destination is not a regular file: $destinationFull"
}

$temporaryDirectory = Join-Path $destinationParent (
    '.DesktopPet-zip-' + [Guid]::NewGuid().ToString('N'))
$temporaryDirectoryLease = $null
$sealedTemporaryFile = $null
$temporarySha256 = $null
$zipPrimaryError = $null
try {
    $temporaryDirectoryLease = Open-DesktopPetNewScratchDirectory `
        -Path $temporaryDirectory `
        -AllowedRoot $destinationParent `
        -TrustedRoot $destinationParent `
        -ProtectedPaths @($manifestFull, $markerFull, $destinationFull) `
        -ProtectedDirectories @($runtimeRootFull)
    $temporaryPath = Join-Path $temporaryDirectory (
        [IO.Path]::GetFileName($destinationFull) + '.tmp')
    $temporaryPath = Assert-DesktopPetOutputFileSafe `
        -Path $temporaryPath `
        -TrustedRoot $temporaryDirectory `
        -ProtectedPaths @($manifestFull, $markerFull, $destinationFull) `
        -ProtectedDirectories @($runtimeRootFull)
    $temporaryCreated = $false
    $normalizedTimestamp =
        New-Object DateTimeOffset 1980, 1, 1, 0, 0, 0, ([TimeSpan]::Zero)

    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'portable-zip-stage-write' `
        -Path $temporaryPath
    $output = New-Object IO.FileStream(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    $temporaryCreated = $true
    try {
        $archive = New-Object IO.Compression.ZipArchive(
            $output,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($name in $entryNames) {
                $entry = $archive.CreateEntry(
                    $name,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $normalizedTimestamp
                $entry.ExternalAttributes = 0

                $entryStream = $entry.Open()
                try {
                    $entrySources[$name].CopyTo($entryStream)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
        $output.Flush($true)
    }
    finally {
        $output.Dispose()
    }

    if ($null -ne $AdditionalStagedArchiveValidation) {
        $null = & $AdditionalStagedArchiveValidation $temporaryPath
    }
    $sealedTemporaryFile = Open-DesktopPetSealedStagedFile `
        -Path $temporaryPath `
        -Root $temporaryDirectory
    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'portable-zip-sealed-validate' `
        -Path $temporaryPath
    Assert-DesktopPetStagedPortableZip `
        -SealedArchiveFile $sealedTemporaryFile `
        -ExpectedEntryNames $entryNames `
        -EntrySources $entrySources `
        -NormalizedTimestamp $normalizedTimestamp

    $temporarySha256 =
        $sealedTemporaryFile.ComputeHash('SHA256')
    $publishZipParameters = @{
        TemporaryPath = $temporaryPath
        DestinationPath = $destinationFull
        TrustedRoot = $destinationParent
        ProtectedPaths = @($manifestFull, $markerFull)
        ProtectedDirectories = @($runtimeRootFull)
        SealedTemporaryFile = $sealedTemporaryFile
        ExpectedTemporarySha256 = $temporarySha256
    }
    if ($zipDestinationExists) {
        $publishZipParameters.ExpectedDestinationSha256 =
            $zipDestinationSha256
    }
    else {
        $publishZipParameters.DestinationMustBeAbsent = $true
    }
    $destinationFull =
        Publish-DesktopPetAtomicFile @publishZipParameters
    $temporaryCreated = $false
}
catch {
    $zipPrimaryError = $_
    throw
}
finally {
    if ($null -ne $sealedTemporaryFile) {
        $sealedTemporaryFile.Dispose()
        $sealedTemporaryFile = $null
    }
    if ($null -ne $temporaryDirectoryLease) {
        $temporaryDirectoryLease.Dispose()
        $temporaryDirectoryLease = $null
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        try {
            Remove-DesktopPetSafeDirectory `
                -Path $temporaryDirectory `
                -AllowedRoot $destinationParent `
                -TrustedRoot $destinationParent
        }
        catch {
            if ($null -eq $zipPrimaryError) {
                throw
            }
            Write-Warning (
                'Portable ZIP scratch cleanup also failed; preserving the ' +
                "primary error. Cleanup error: $($_.Exception.Message)")
        }
    }
}

Write-Host (
    "Deterministic portable ZIP created: {0} ({1} entries)." -f
    $destinationFull,
    $entryNames.Count) -ForegroundColor Green
}
finally {
    foreach ($validatedInput in $validatedInputs) {
        $validatedInput.Dispose()
    }
}
