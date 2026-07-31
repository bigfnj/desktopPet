#requires -Version 5
[CmdletBinding()]
param(
    [string]$LockPath,
    [Parameter(Mandatory = $true)][string]$ScanRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [string]$RuntimeManifestPath,
    [Parameter(Mandatory = $true)][string]$ToolRoot,
    [string]$ProvenancePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'The locked Syft release toolchain requires Windows amd64.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $PSScriptRoot 'syft-toolchain-lock.json'
}
if ([string]::IsNullOrWhiteSpace($RuntimeManifestPath)) {
    $RuntimeManifestPath = Join-Path $PSScriptRoot 'runtime-files.txt'
}

$resolvedLock = (Resolve-Path -LiteralPath $LockPath).Path
$resolvedScanRoot = (Resolve-Path -LiteralPath $ScanRoot).Path
$resolvedRuntimeRoot = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$resolvedManifest = (Resolve-Path -LiteralPath $RuntimeManifestPath).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$resolvedToolRoot = [IO.Path]::GetFullPath($ToolRoot)
$resolvedProvenance = $null
if (-not [string]::IsNullOrWhiteSpace($ProvenancePath)) {
    $resolvedProvenance = [IO.Path]::GetFullPath($ProvenancePath)
}

$lock = Get-Content -LiteralPath $resolvedLock -Raw | ConvertFrom-Json
if ([int]$lock.schemaVersion -ne 1 -or
    [string]$lock.syftVersion -cne '1.42.3') {
    throw 'The Syft toolchain lock must use schema 1 and pin version 1.42.3.'
}
$archive = $lock.archive
$expectedFileName = 'syft_1.42.3_windows_amd64.zip'
$expectedSource =
    'https://github.com/anchore/syft/releases/download/v1.42.3/' +
    $expectedFileName
if ([string]$archive.fileName -cne $expectedFileName -or
    [string]$archive.source -cne $expectedSource -or
    [long]$archive.size -ne 28204841 -or
    [string]$archive.sha256 -cne
        'e1b9f4945aa64c2b34970bec617623d7f803d0661b48a50b966768b363322e2d') {
    throw 'The repository Syft lock does not match the reviewed v1.42.3 Windows amd64 archive.'
}

$runtimeFiles = @(
    Get-Content -LiteralPath $resolvedManifest |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) {
    throw 'Runtime manifest is empty.'
}

$pathSafety = Join-Path $PSScriptRoot 'StagingPathSafety.ps1'
. $pathSafety
$transactionPolicy =
    Join-Path $PSScriptRoot 'SyftOutputTransaction.ps1'
if (-not (Test-Path -LiteralPath $transactionPolicy -PathType Leaf)) {
    throw "Syft output transaction policy is missing: $transactionPolicy"
}
. $transactionPolicy
$outputParent = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "Syft output parent does not exist: $outputParent"
}
$protectedOutputPaths = @($resolvedLock, $resolvedManifest)
$protectedOutputDirectories = @(
    $resolvedScanRoot,
    $resolvedRuntimeRoot,
    $resolvedToolRoot)
if ($null -ne $resolvedProvenance) {
    $protectedOutputPaths += $resolvedProvenance
}
$resolvedOutput = Assert-DesktopPetOutputFileSafe `
    -Path $resolvedOutput `
    -TrustedRoot $outputParent `
    -ProtectedPaths $protectedOutputPaths `
    -ProtectedDirectories $protectedOutputDirectories

if ($null -ne $resolvedProvenance) {
    $provenanceParent = Split-Path -Parent $resolvedProvenance
    if (-not (Test-Path -LiteralPath $provenanceParent -PathType Container)) {
        throw "Syft provenance parent does not exist: $provenanceParent"
    }
    # This must complete before runtime hashing, tool download, or Syft scan so
    # a missing/unreadable requested provenance destination cannot follow an
    # SBOM publication.
    $resolvedProvenance = Assert-DesktopPetSyftProvenancePreflight `
        -Path $resolvedProvenance `
        -Root $provenanceParent `
        -ProtectedPaths @(
            $resolvedLock,
            $resolvedManifest,
            $resolvedOutput) `
        -ProtectedDirectories $protectedOutputDirectories
}

$existingSyftJournal = Get-DesktopPetSyftTransactionJournalPath `
    -OutputPath $resolvedOutput `
    -OutputRoot $outputParent
if (Test-Path -LiteralPath $existingSyftJournal) {
    Throw-DesktopPetSyftExistingJournal `
        -JournalPath $existingSyftJournal `
        -OutputPath $resolvedOutput `
        -OutputRoot $outputParent
}

function Get-RuntimeHashMap {
    $hashes = New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::Ordinal)
    foreach ($name in $runtimeFiles) {
        if (-not (Test-DesktopPetWindowsLeafName -Name $name) -or
            $hashes.ContainsKey($name)) {
            throw "Runtime manifest contains an unsafe or duplicate name: '$name'."
        }
        $path = Join-Path $resolvedRuntimeRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Runtime file is missing before or after Syft execution: $path"
        }
        $hashes.Add(
            $name,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
    }
    return ,$hashes
}

function Assert-HashMapsEqual {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )
    if ($Before.Count -ne $After.Count) {
        throw 'Runtime file count changed while executing SBOM tooling.'
    }
    foreach ($name in $Before.Keys) {
        if (-not $After.ContainsKey($name) -or
            [string]$Before[$name] -cne [string]$After[$name]) {
            throw "Runtime content changed while executing SBOM tooling: $name"
        }
    }
}

function Receive-LockedHttpsFileCreateNew {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][uri]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$ExpectedLength
    )

    if ($Uri.Scheme -cne 'https') {
        throw "Locked tool downloads require HTTPS: $Uri"
    }
    if ($ExpectedLength -le 0) {
        throw 'Locked tool download length must be positive.'
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $client = New-Object Net.Http.HttpClient($handler)
    $request = New-Object Net.Http.HttpRequestMessage(
        [Net.Http.HttpMethod]::Get,
        $Uri)
    $response = $null
    $input = $null
    $output = $null
    $cancellation = New-Object Threading.CancellationTokenSource
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(180)
        $cancellation.CancelAfter([TimeSpan]::FromSeconds(180))
        $request.Headers.UserAgent.ParseAdd(
            'DesktopPet-Locked-Syft-Downloader/1.0')
        $output = New-Object IO.FileStream(
            $Destination,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::WriteThrough)
        $response = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $cancellation.Token
        ).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw (
                "Locked tool download failed with HTTP " +
                "$([int]$response.StatusCode).")
        }
        if ($null -ne $response.Content.Headers.ContentLength -and
            [long]$response.Content.Headers.ContentLength -ne
                $ExpectedLength) {
            throw (
                'Locked tool download Content-Length differs from the lock: ' +
                $response.Content.Headers.ContentLength)
        }

        $input =
            $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = New-Object byte[] 65536
        $total = 0L
        while (($read = $input.ReadAsync(
                    $buffer,
                    0,
                    $buffer.Length,
                    $cancellation.Token
                ).GetAwaiter().GetResult()) -gt 0) {
            $total += [long]$read
            if ($total -gt $ExpectedLength) {
                throw (
                    'Locked tool download exceeded its exact locked length.')
            }
            $output.Write($buffer, 0, $read)
        }
        if ($total -ne $ExpectedLength) {
            throw (
                "Locked tool download length is $total; expected " +
                "$ExpectedLength.")
        }
        $output.Flush($true)
    }
    finally {
        foreach ($resource in @(
                $output,
                $input,
                $response,
                $request,
                $cancellation,
                $client,
                $handler)) {
            if ($null -ne $resource) {
                $resource.Dispose()
            }
        }
    }
}

$toolParent = Split-Path -Parent $resolvedToolRoot
if ([string]::IsNullOrWhiteSpace($toolParent) -or
    -not (Test-Path -LiteralPath $toolParent -PathType Container)) {
    throw "Locked Syft tool parent must already exist: $toolParent"
}

$toolRootProtectedPaths = @(
    $resolvedLock,
    $resolvedManifest,
    $resolvedOutput)
if ($null -ne $resolvedProvenance) {
    $toolRootProtectedPaths += $resolvedProvenance
}
$toolRootLease = Open-DesktopPetNewScratchDirectory `
    -Path $resolvedToolRoot `
    -AllowedRoot $toolParent `
    -TrustedRoot $toolParent `
    -ProtectedPaths $toolRootProtectedPaths `
    -ProtectedDirectories @(
        $repoRoot,
        $resolvedScanRoot,
        $resolvedRuntimeRoot)

$toolPrimaryError = $null
$archiveInput = $null
$syftInput = $null
try {
$expandedRootLease = $null
$beforeHashes = Get-RuntimeHashMap
$archivePath = Join-Path $resolvedToolRoot $expectedFileName
$expandedRoot = Join-Path $resolvedToolRoot 'expanded'
$expandedRootLease = Open-DesktopPetNewScratchDirectory `
    -Path $expandedRoot `
    -AllowedRoot $resolvedToolRoot `
    -TrustedRoot $toolParent `
    -ProtectedPaths $toolRootProtectedPaths `
    -ProtectedDirectories @(
        $repoRoot,
        $resolvedScanRoot,
        $resolvedRuntimeRoot)

Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'before-locked-syft-archive-create' `
    -Path $archivePath
Receive-LockedHttpsFileCreateNew `
    -Uri $expectedSource `
    -Destination $archivePath `
    -ExpectedLength ([long]$archive.size)
$archiveInput = Open-DesktopPetValidatedInputFile `
    -Path $archivePath `
    -Root $resolvedToolRoot
if ([long]$archiveInput.Length -ne [long]$archive.size) {
    throw (
        "Downloaded Syft archive length is $($archiveInput.Length); " +
        "expected $($archive.size).")
}
$observedHash =
    $archiveInput.ComputeHash('SHA256').ToLowerInvariant()
if ($observedHash -cne [string]$archive.sha256) {
    throw "Downloaded Syft archive failed its locked SHA-256 check: $observedHash"
}

Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'locked-syft-archive-validated-before-extract' `
    -Path $archivePath
Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedRoot
$syftCandidates = @(
    Get-ChildItem -LiteralPath $expandedRoot -Filter 'syft.exe' -File -Recurse)
if ($syftCandidates.Count -ne 1) {
    throw "Locked Syft archive must contain exactly one syft.exe; found $($syftCandidates.Count)."
}
$syft = $syftCandidates[0].FullName
$syftInput = Open-DesktopPetValidatedInputFile `
    -Path $syft `
    -Root $expandedRoot
$syftSha256 = $syftInput.ComputeHash('SHA256')
$versionOutput = ((& $syft version) | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    $versionOutput -notmatch '(?m)^Version:\s+1\.42\.3\s*$') {
    throw "Extracted Syft executable is not version 1.42.3: $versionOutput"
}
if ($syftInput.ComputeHash('SHA256') -cne $syftSha256) {
    throw 'Extracted Syft executable changed during version validation.'
}

$resolvedOutput = Assert-DesktopPetOutputFileSafe `
    -Path $resolvedOutput `
    -TrustedRoot $outputParent `
    -ProtectedPaths $protectedOutputPaths `
    -ProtectedDirectories $protectedOutputDirectories

$outputStagingDirectory = Join-Path $outputParent (
    '.DesktopPet-syft-output-' + [Guid]::NewGuid().ToString('N'))
$provenanceStaging = $null
$provenanceStagingLease = $null
$retainRecoveryStaging = $false
$stagedOutputLease = $null
$sealedStagedOutput = $null
$temporaryProvenanceLease = $null
$sealedTemporaryProvenance = $null
$syftPrimaryError = $null
$outputStagingLease = Open-DesktopPetNewScratchDirectory `
    -Path $outputStagingDirectory `
    -AllowedRoot $outputParent `
    -TrustedRoot $outputParent `
    -ProtectedPaths @($protectedOutputPaths + $resolvedOutput) `
    -ProtectedDirectories $protectedOutputDirectories
try {
    $stagedOutput = Join-Path $outputStagingDirectory (
        [IO.Path]::GetFileName($resolvedOutput) + '.tmp')
    $stagedOutput = Assert-DesktopPetOutputFileSafe `
        -Path $stagedOutput `
        -TrustedRoot $outputParent `
        -ProtectedPaths @($protectedOutputPaths + $resolvedOutput) `
        -ProtectedDirectories $protectedOutputDirectories
    [void](Write-DesktopPetNewFileBytes `
        -Path $stagedOutput `
        -Root $outputParent `
        -Bytes ([byte[]]@()) `
        -ProtectedPaths @($protectedOutputPaths + $resolvedOutput) `
        -ProtectedDirectories $protectedOutputDirectories `
        -MutationOperation 'before-syft-output-create')
    $stagedOutputLease = Open-DesktopPetValidatedMutableFile `
        -Path $stagedOutput `
        -Root $outputStagingDirectory
    $stagedOutputSha256 = $null

    $previousUpdateCheck = $env:SYFT_CHECK_FOR_APP_UPDATE
    $scanPrimaryError = $null
    try {
        $env:SYFT_CHECK_FOR_APP_UPDATE = 'false'
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-syft-scan-write' `
            -Path $stagedOutput
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'locked-syft-version-validated-before-scan' `
            -Path $syft
        if ($syftInput.ComputeHash('SHA256') -cne $syftSha256) {
            throw 'Extracted Syft executable changed before scan execution.'
        }
        & $syft scan "dir:$resolvedScanRoot" `
            -o "spdx-json=$stagedOutput"
        if ($LASTEXITCODE -ne 0) {
            throw "Locked Syft scan failed (exit $LASTEXITCODE)."
        }
        if ($syftInput.ComputeHash('SHA256') -cne $syftSha256) {
            throw 'Extracted Syft executable changed during scan execution.'
        }
        $sealedStagedOutput = $stagedOutputLease.Seal()
        $stagedOutputLease = $null
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'syft-output-sealed-validate' `
            -Path $stagedOutput
        $stagedOutputSha256 =
            $sealedStagedOutput.ComputeHash('SHA256')
        $spdx =
            $sealedStagedOutput.ReadAllTextUtf8(128MB) |
                ConvertFrom-Json
        if ([string]$spdx.spdxVersion -cne 'SPDX-2.3') {
            throw (
                'Locked Syft produced an unexpected SPDX version: ' +
                "'$($spdx.spdxVersion)'.")
        }
    }
    catch {
        $scanPrimaryError = $_
        throw
    }
    finally {
        $scanCleanupErrors =
            New-Object 'Collections.Generic.List[Exception]'
        try {
            if ($null -eq $previousUpdateCheck) {
                Remove-Item Env:SYFT_CHECK_FOR_APP_UPDATE `
                    -ErrorAction Stop
            }
            else {
                $env:SYFT_CHECK_FOR_APP_UPDATE = $previousUpdateCheck
            }
        }
        catch {
            $scanCleanupErrors.Add($_.Exception)
        }
        if ($null -ne $stagedOutputLease) {
            try {
                $stagedOutputLease.Dispose()
                $stagedOutputLease = $null
            }
            catch {
                $scanCleanupErrors.Add($_.Exception)
            }
        }
        if ($scanCleanupErrors.Count -gt 0) {
            if ($null -eq $scanPrimaryError) {
                throw $scanCleanupErrors[0]
            }
            Write-Warning (
                'Locked Syft scan cleanup also failed; preserving the primary ' +
                "error. Cleanup error: $($scanCleanupErrors[0].Message)")
        }
    }

    if (-not (Test-Path -LiteralPath $stagedOutput -PathType Leaf)) {
        throw (
            'Locked Syft scan did not produce staged SPDX JSON: ' +
            $stagedOutput)
    }
    $afterHashes = Get-RuntimeHashMap
    Assert-HashMapsEqual -Before $beforeHashes -After $afterHashes

    $temporaryProvenance = $null
    $temporaryProvenanceSha256 = $null
    $expectedProvenanceHash = $null
    if ($null -ne $resolvedProvenance) {
        $resolvedProvenance = Assert-DesktopPetSyftProvenancePreflight `
            -Path $resolvedProvenance `
            -Root $provenanceParent `
            -ProtectedPaths @(
                $resolvedLock,
                $resolvedManifest,
                $resolvedOutput) `
            -ProtectedDirectories $protectedOutputDirectories
        $lines = @(
            'syft_version=1.42.3'
            "syft_archive=$expectedFileName"
            "syft_archive_size=$([long]$archive.size)"
            "syft_archive_sha256=$([string]$archive.sha256)"
            "syft_archive_source=$expectedSource"
            "syft_executable_sha256=$syftSha256"
            'syft_runtime_hashes_unchanged=true'
        )
        $provenanceInput = Open-DesktopPetValidatedInputFile `
            -Path $resolvedProvenance `
            -Root $provenanceParent
        $provenanceReadPrimaryError = $null
        try {
            $expectedProvenanceHash =
                $provenanceInput.ComputeHash('SHA256')
            $existingProvenance =
                $provenanceInput.ReadAllTextUtf8(16MB)
        }
        catch {
            $provenanceReadPrimaryError = $_
            throw
        }
        finally {
            Close-DesktopPetSyftResources `
                -Resources @($provenanceInput) `
                -PrimaryError $provenanceReadPrimaryError `
                -Context 'Syft provenance read'
        }

        $provenanceStaging = Join-Path $provenanceParent (
            '.DesktopPet-syft-provenance-' +
            [Guid]::NewGuid().ToString('N'))
        $provenanceStagingLease = Open-DesktopPetNewScratchDirectory `
            -Path $provenanceStaging `
            -AllowedRoot $provenanceParent `
            -TrustedRoot $provenanceParent `
            -ProtectedPaths @(
                $resolvedLock,
                $resolvedManifest,
                $resolvedOutput,
                $resolvedProvenance) `
            -ProtectedDirectories @(
                $protectedOutputDirectories +
                $outputStagingDirectory)
        $temporaryProvenance = Join-Path $provenanceStaging (
            [IO.Path]::GetFileName($resolvedProvenance) + '.tmp')
        $temporaryProvenance = Assert-DesktopPetOutputFileSafe `
            -Path $temporaryProvenance `
            -TrustedRoot $provenanceParent `
            -ProtectedPaths @(
                $resolvedLock,
                $resolvedManifest,
                $resolvedOutput,
                $resolvedProvenance) `
            -ProtectedDirectories $protectedOutputDirectories
        $provenanceDocument =
            New-DesktopPetSyftBoundProvenanceDocument `
                -ExistingContent $existingProvenance `
                -EvidenceLines $lines `
                -StagedOutputSha256 $stagedOutputSha256
        $provenanceEvidence = $provenanceDocument.Evidence
        $temporaryProvenanceContent = $provenanceDocument.Content
        $temporaryProvenanceBytes =
            (New-Object Text.UTF8Encoding($false)).GetBytes(
                $temporaryProvenanceContent)
        $provenanceHasher = [Security.Cryptography.SHA256]::Create()
        $provenanceHashPrimaryError = $null
        try {
            $expectedTemporaryProvenanceSha256 =
                ([BitConverter]::ToString(
                    $provenanceHasher.ComputeHash(
                        $temporaryProvenanceBytes))).Replace('-', '')
        }
        catch {
            $provenanceHashPrimaryError = $_
            throw
        }
        finally {
            Close-DesktopPetSyftResources `
                -Resources @($provenanceHasher) `
                -PrimaryError $provenanceHashPrimaryError `
                -Context 'Syft provenance hashing'
        }
        [void](Write-DesktopPetNewUtf8File `
            -Path $temporaryProvenance `
            -Root $provenanceParent `
            -Content $temporaryProvenanceContent `
            -ProtectedPaths @(
                $resolvedLock,
                $resolvedManifest,
                $resolvedOutput,
                $resolvedProvenance) `
            -ProtectedDirectories $protectedOutputDirectories `
            -MutationOperation 'before-syft-provenance-write')
        $temporaryProvenanceLease =
            Open-DesktopPetValidatedMutableFile `
                -Path $temporaryProvenance `
                -Root $provenanceStaging
        $sealedTemporaryProvenance =
            $temporaryProvenanceLease.Seal()
        $temporaryProvenanceLease = $null
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'syft-provenance-sealed-validate' `
            -Path $temporaryProvenance
        $temporaryProvenanceSha256 =
            $sealedTemporaryProvenance.ComputeHash('SHA256')
        if ($temporaryProvenanceSha256 -cne
                $expectedTemporaryProvenanceSha256) {
            throw (
                'Generated Syft provenance differs from the exact authored ' +
                'UTF-8 bytes.')
        }
        $observedTemporaryProvenance =
            $sealedTemporaryProvenance.ReadAllTextUtf8(16MB)
        if (-not $observedTemporaryProvenance.EndsWith(
                $provenanceEvidence,
                [StringComparison]::Ordinal) -or
            $observedTemporaryProvenance -notmatch
                ('(?m)^syft_sbom_spdx_sha256=' +
                 [regex]::Escape($stagedOutputSha256) + '\r?$')) {
            throw (
                'Generated Syft provenance is not bound to the exact staged ' +
                'SBOM SHA-256.')
        }
    }

    try {
        $transactionParameters = @{
            StagedOutputPath = $stagedOutput
            OutputPath = $resolvedOutput
            OutputRoot = $outputParent
            SealedStagedOutputFile = $sealedStagedOutput
            ExpectedStagedOutputSha256 = $stagedOutputSha256
            ProtectedPaths = @($resolvedLock, $resolvedManifest)
            ProtectedDirectories = $protectedOutputDirectories
        }
        if ($null -ne $resolvedProvenance) {
            $transactionParameters.StagedProvenancePath =
                $temporaryProvenance
            $transactionParameters.ProvenancePath =
                $resolvedProvenance
            $transactionParameters.ProvenanceRoot =
                $provenanceParent
            $transactionParameters.ExpectedProvenanceSha256 =
                $expectedProvenanceHash
            $transactionParameters.SealedStagedProvenanceFile =
                $sealedTemporaryProvenance
            $transactionParameters.ExpectedStagedProvenanceSha256 =
                $temporaryProvenanceSha256
        }
        Publish-DesktopPetSyftOutputTransaction @transactionParameters
    }
    catch {
        if (Test-DesktopPetSyftTransactionRequiresRecovery `
                -Exception $_.Exception) {
            $retainRecoveryStaging = $true
        }
        throw
    }
}
catch {
    $syftPrimaryError = $_
    throw
}
finally {
    $stagingCleanupErrors =
        New-Object 'Collections.Generic.List[Exception]'
    foreach ($stagingHandle in @(
            $sealedTemporaryProvenance,
            $temporaryProvenanceLease,
            $sealedStagedOutput,
            $stagedOutputLease,
            $provenanceStagingLease,
            $outputStagingLease)) {
        if ($null -eq $stagingHandle) {
            continue
        }
        try {
            $stagingHandle.Dispose()
        }
        catch {
            $stagingCleanupErrors.Add($_.Exception)
        }
    }
    if ($retainRecoveryStaging) {
        Write-Warning (
            "Preserving Syft recovery staging after incomplete rollback: " +
            "$outputStagingDirectory" +
            $(if ($null -ne $provenanceStaging) {
                "; $provenanceStaging"
            }))
    }
    else {
        if ($null -ne $provenanceStaging -and
            (Test-Path -LiteralPath $provenanceStaging)) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $provenanceStaging `
                    -AllowedRoot $provenanceParent `
                    -TrustedRoot $provenanceParent
            }
            catch {
                $stagingCleanupErrors.Add($_.Exception)
            }
        }
        if (Test-Path -LiteralPath $outputStagingDirectory) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $outputStagingDirectory `
                    -AllowedRoot $outputParent `
                    -TrustedRoot $outputParent
            }
            catch {
                $stagingCleanupErrors.Add($_.Exception)
            }
        }
    }
    if ($stagingCleanupErrors.Count -gt 0) {
        if ($null -eq $syftPrimaryError) {
            throw $stagingCleanupErrors[0]
        }
        Write-Warning (
            'Syft output scratch cleanup also failed; preserving the primary ' +
            "error. Cleanup error: $($stagingCleanupErrors[0].Message)")
    }
}

Write-Host (
    "Locked Syft 1.42.3 generated SPDX-2.3; {0} runtime hashes were unchanged." -f
    $runtimeFiles.Count
) -ForegroundColor Green
}
catch {
    $toolPrimaryError = $_
    throw
}
finally {
    $toolCleanupErrors =
        New-Object 'Collections.Generic.List[Exception]'
    foreach ($toolInput in @($syftInput, $archiveInput)) {
        if ($null -eq $toolInput) {
            continue
        }
        try {
            $toolInput.Dispose()
        }
        catch {
            $toolCleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $expandedRootLease) {
        try {
            $expandedRootLease.Dispose()
        }
        catch {
            $toolCleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $toolRootLease) {
        try {
            $toolRootLease.Dispose()
        }
        catch {
            $toolCleanupErrors.Add($_.Exception)
        }
    }
    if (Test-Path -LiteralPath $resolvedToolRoot) {
        try {
            Remove-DesktopPetSafeDirectory `
                -Path $resolvedToolRoot `
                -AllowedRoot $toolParent `
                -TrustedRoot $toolParent
        }
        catch {
            $toolCleanupErrors.Add($_.Exception)
        }
    }
    if ($toolCleanupErrors.Count -gt 0) {
        if ($null -eq $toolPrimaryError) {
            throw $toolCleanupErrors[0]
        }
        Write-Warning (
            'Locked Syft tool scratch cleanup also failed; preserving the ' +
            "primary error. Cleanup error: $($toolCleanupErrors[0].Message)")
    }
}
