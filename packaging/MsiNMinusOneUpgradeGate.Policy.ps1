#requires -Version 5

Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

function Select-DesktopPetPriorPublicMsiRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Releases,
        [Parameter(Mandatory = $true)][version]$CurrentVersion
    )

    $eligible = @(
        foreach ($release in $Releases) {
            $tag = [string]$release.tag_name
            if ([bool]$release.draft -or
                [bool]$release.prerelease -or
                $tag -notmatch '^v\d+\.\d+\.\d+$') {
                continue
            }
            $version = [version]$tag.Substring(1)
            if ($version -ge $CurrentVersion) {
                continue
            }
            $msiAssets = @(
                $release.assets |
                    Where-Object { [string]$_.name -match '(?i)\.msi$' })
            if ($msiAssets.Count -eq 0) {
                continue
            }
            if ($msiAssets.Count -ne 1) {
                throw "Prior public release '$tag' has multiple MSI assets."
            }
            [pscustomobject]@{
                Release = $release
                Tag = $tag
                Version = $version
                MsiAsset = $msiAssets[0]
            }
        }
    )
    return @(
        $eligible |
            Sort-Object Version -Descending |
            Select-Object -First 1
    )
}

function Publish-DesktopPetMsiNMinusOneEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Document,
        [Parameter(Mandatory = $true)][string]$EvidencePath,
        [Parameter(Mandatory = $true)][string]$EvidenceParent,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $evidenceDestinationExists = $false
    $evidenceDestinationSha256 = $null
    if (Test-Path -LiteralPath $EvidencePath -PathType Leaf) {
        $evidenceDestinationInput = Open-DesktopPetValidatedInputFile `
            -Path $EvidencePath `
            -Root $EvidenceParent
        try {
            $evidenceDestinationSha256 =
                $evidenceDestinationInput.ComputeHash('SHA256')
            $evidenceDestinationExists = $true
        }
        finally {
            $evidenceDestinationInput.Dispose()
        }
    }
    elseif (Test-Path -LiteralPath $EvidencePath) {
        throw "Upgrade evidence destination is not a regular file: $EvidencePath"
    }

    $stagingDirectory = Join-Path $EvidenceParent (
        '.DesktopPet-upgrade-gate-evidence-' +
        [Guid]::NewGuid().ToString('N'))
    $stagingLease = $null
    $temporaryEvidenceLease = $null
    $sealedTemporaryEvidence = $null
    $evidencePrimaryError = $null
    $stagingLease = Open-DesktopPetNewScratchDirectory `
        -Path $stagingDirectory `
        -AllowedRoot $EvidenceParent `
        -TrustedRoot $EvidenceParent `
        -ProtectedPaths @($ProtectedPaths + $EvidencePath) `
        -ProtectedDirectories $ProtectedDirectories
    try {
        $temporaryEvidence = Join-Path $stagingDirectory (
            [IO.Path]::GetFileName($EvidencePath) + '.tmp')
        $temporaryEvidence = Assert-DesktopPetOutputFileSafe `
            -Path $temporaryEvidence `
            -TrustedRoot $EvidenceParent `
            -ProtectedPaths @($ProtectedPaths + $EvidencePath) `
            -ProtectedDirectories $ProtectedDirectories
        $evidenceText =
            ($Document | ConvertTo-Json -Depth 8) +
            [Environment]::NewLine
        [void](Write-DesktopPetNewUtf8File `
            -Path $temporaryEvidence `
            -Root $EvidenceParent `
            -Content $evidenceText `
            -ProtectedPaths @($ProtectedPaths + $EvidencePath) `
            -ProtectedDirectories $ProtectedDirectories `
            -MutationOperation 'before-nminusone-evidence-write')
        $evidenceHasher = [Security.Cryptography.SHA256]::Create()
        try {
            $expectedEvidenceSha256 = ([BitConverter]::ToString(
                $evidenceHasher.ComputeHash(
                    (New-Object Text.UTF8Encoding($false)).
                        GetBytes($evidenceText)))).Replace('-', '')
        }
        finally {
            $evidenceHasher.Dispose()
        }
        $temporaryEvidenceLease = Open-DesktopPetValidatedMutableFile `
            -Path $temporaryEvidence `
            -Root $stagingDirectory
        $sealedTemporaryEvidence = $temporaryEvidenceLease.Seal()
        $temporaryEvidenceLease = $null
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'nminusone-evidence-sealed-validate' `
            -Path $temporaryEvidence
        $temporarySha256 =
            $sealedTemporaryEvidence.ComputeHash('SHA256')
        if ($temporarySha256 -cne $expectedEvidenceSha256 -or
            $sealedTemporaryEvidence.ReadAllTextUtf8(16MB) -cne
                $evidenceText) {
            throw (
                'Generated upgrade evidence differs from its exact ' +
                'in-memory authoring bytes.')
        }
        $validated =
            $sealedTemporaryEvidence.ReadAllTextUtf8(16MB) |
                ConvertFrom-Json
        if ([int]$validated.schemaVersion -ne 1 -or
            [string]::IsNullOrWhiteSpace([string]$validated.status)) {
            throw (
                'Generated upgrade evidence failed its schema sanity ' +
                'check.')
        }
        $publishEvidenceParameters = @{
            TemporaryPath = $temporaryEvidence
            DestinationPath = $EvidencePath
            TrustedRoot = $EvidenceParent
            ProtectedPaths = $ProtectedPaths
            ProtectedDirectories = $ProtectedDirectories
            SealedTemporaryFile = $sealedTemporaryEvidence
            ExpectedTemporarySha256 = $temporarySha256
        }
        if ($evidenceDestinationExists) {
            $publishEvidenceParameters.ExpectedDestinationSha256 =
                $evidenceDestinationSha256
        }
        else {
            $publishEvidenceParameters.DestinationMustBeAbsent = $true
        }
        [void](Publish-DesktopPetAtomicFile @publishEvidenceParameters)
    }
    catch {
        $evidencePrimaryError = $_
        throw
    }
    finally {
        if ($null -ne $sealedTemporaryEvidence) {
            $sealedTemporaryEvidence.Dispose()
            $sealedTemporaryEvidence = $null
        }
        if ($null -ne $temporaryEvidenceLease) {
            $temporaryEvidenceLease.Dispose()
            $temporaryEvidenceLease = $null
        }
        if ($null -ne $stagingLease) {
            $stagingLease.Dispose()
            $stagingLease = $null
        }
        if (Test-Path -LiteralPath $stagingDirectory) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $stagingDirectory `
                    -AllowedRoot $EvidenceParent `
                    -TrustedRoot $EvidenceParent
            }
            catch {
                if ($null -eq $evidencePrimaryError) {
                    throw
                }
                Write-Warning (
                    'Upgrade evidence scratch cleanup also failed; ' +
                    "preserving the primary error. Cleanup error: " +
                    $_.Exception.Message)
            }
        }
    }
}

function Invoke-DesktopPetMsiNMinusOneUpgradePolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
        [string]$Repository,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^v\d+\.\d+\.\d+$')]
        [string]$CurrentReleaseTag,
        [Parameter(Mandatory = $true)][string]$CurrentMsiPath,
        [Parameter(Mandatory = $true)][string]$CurrentRuntimeRoot,
        [Parameter(Mandatory = $true)][string]$RuntimeManifestPath,
        [Parameter(Mandatory = $true)][string]$EvidencePath,
        [Parameter(Mandatory = $true)][string]$DownloadRoot,
        [Parameter(Mandatory = $true)][string]$GitHubToken
    )

    if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
        throw (
            'A GitHub token is required to resolve and download the prior ' +
            'public MSI.')
    }

    $currentMsi = (Resolve-Path -LiteralPath $CurrentMsiPath).Path
    $currentRuntime = (Resolve-Path -LiteralPath $CurrentRuntimeRoot).Path
    $manifest = (Resolve-Path -LiteralPath $RuntimeManifestPath).Path
    $repositoryRoot =
        [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    $evidence = [IO.Path]::GetFullPath($EvidencePath)
    $evidenceParent = Split-Path -Parent $evidence
    if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container)) {
        throw "Upgrade evidence parent does not exist: $evidenceParent"
    }
    $resolvedDownloadRoot = [IO.Path]::GetFullPath($DownloadRoot)
    $downloadParent = Split-Path -Parent $resolvedDownloadRoot
    if (-not (Test-Path -LiteralPath $downloadParent -PathType Container)) {
        throw "Prior-release download parent does not exist: $downloadParent"
    }
    $downloadProtectedPaths = [string[]]@(
        $currentMsi,
        $manifest,
        $evidence)
    $downloadProtectedDirectories = [string[]]@(
        $repositoryRoot,
        $currentRuntime)
    if ((Test-DesktopPetPathWithin `
                -Path $currentRuntime `
                -Root $resolvedDownloadRoot `
                -AllowRoot) -or
        (Test-DesktopPetPathWithin `
                -Path $resolvedDownloadRoot `
                -Root $currentRuntime `
                -AllowRoot)) {
        throw (
            'Prior-release download staging overlaps the current runtime root: ' +
            $resolvedDownloadRoot)
    }
    foreach ($protectedDownloadInput in @(
            $downloadProtectedPaths + $downloadProtectedDirectories)) {
        if ((Test-DesktopPetPathWithin `
                    -Path $protectedDownloadInput `
                    -Root $resolvedDownloadRoot `
                    -AllowRoot) -or
            (Test-DesktopPetPathWithin `
                    -Path $resolvedDownloadRoot `
                    -Root $protectedDownloadInput `
                    -AllowRoot)) {
            throw (
                'Prior-release download staging overlaps a protected path: ' +
                $protectedDownloadInput)
        }
    }

    $evidenceProtectedPaths = [string[]]@($currentMsi, $manifest)
    $evidenceProtectedDirectories = [string[]]@(
        $currentRuntime,
        $resolvedDownloadRoot)
    $evidence = Assert-DesktopPetOutputFileSafe `
        -Path $evidence `
        -TrustedRoot $evidenceParent `
        -ProtectedPaths $evidenceProtectedPaths `
        -ProtectedDirectories $evidenceProtectedDirectories
    $downloadRootLease = $null
    $downloadRootOwned = $false
    $leaseTransferred = $false
    $currentMsiInput = $null
    $currentMsiInputTransferred = $false
    try {
        $downloadRootLease = Open-DesktopPetNewScratchDirectory `
            -Path $resolvedDownloadRoot `
            -AllowedRoot $downloadParent `
            -TrustedRoot $downloadParent `
            -ProtectedPaths $downloadProtectedPaths `
            -ProtectedDirectories $downloadProtectedDirectories
        $downloadRootOwned = $true

        $currentMsiInput = Open-DesktopPetValidatedInputFile `
            -Path $currentMsi `
            -Root (Split-Path -Parent $currentMsi)
        $currentHash =
            $currentMsiInput.ComputeHash('SHA256').ToLowerInvariant()
        $currentVersion = [version]$CurrentReleaseTag.Substring(1)
        $headers = @{
            Accept = 'application/vnd.github+json'
            Authorization = "Bearer $GitHubToken"
            'X-GitHub-Api-Version' = '2022-11-28'
            'User-Agent' = 'DesktopPet-NMinusOne-Release-Gate'
        }

        $releases = @()
        for ($page = 1; $page -le 10; $page++) {
            $uri = (
                "https://api.github.com/repos/$Repository/releases" +
                "?per_page=100&page=$page")
            # Assign first so Windows PowerShell does not retain an empty JSON
            # array as one nested Object[] when the response is wrapped in @(...).
            $pageResponse = Invoke-RestMethod `
                -UseBasicParsing `
                -TimeoutSec 60 `
                -Headers $headers `
                -Uri $uri
            $batch = @($pageResponse)
            $releases += $batch
            if ($batch.Count -lt 100) {
                break
            }
            if ($page -eq 10) {
                throw (
                    'Release history exceeds the bounded 1000-release ' +
                    'N-1 search.')
            }
        }

        $prior = @(
            Select-DesktopPetPriorPublicMsiRelease `
                -Releases $releases `
                -CurrentVersion $currentVersion
        )
        $context = [pscustomobject]@{
            IsComplete = ($prior.Count -eq 0)
            CurrentMsi = $currentMsi
            CurrentRuntime = $currentRuntime
            Manifest = $manifest
            Evidence = $evidence
            EvidenceParent = $evidenceParent
            ResolvedDownloadRoot = $resolvedDownloadRoot
            DownloadParent = $downloadParent
            DownloadRootLease = if ($prior.Count -eq 0) {
                $null
            }
            else {
                $downloadRootLease
            }
            CurrentMsiInput = if ($prior.Count -eq 0) {
                $null
            }
            else {
                $currentMsiInput
            }
            EvidenceProtectedPaths = $evidenceProtectedPaths
            EvidenceProtectedDirectories = $evidenceProtectedDirectories
            CurrentHash = $currentHash
            Headers = $headers
            Prior = if ($prior.Count -eq 0) { $null } else { $prior[0] }
        }
        if ($context.IsComplete) {
            Publish-DesktopPetMsiNMinusOneEvidence `
                -Document ([ordered]@{
                    schemaVersion = 1
                    status = 'not_applicable'
                    reason = 'no_prior_public_msi'
                    currentReleaseTag = $CurrentReleaseTag
                    currentMsiSha256 = $currentHash
                    previousReleaseTag = $null
                }) `
                -EvidencePath $evidence `
                -EvidenceParent $evidenceParent `
                -ProtectedPaths $evidenceProtectedPaths `
                -ProtectedDirectories $evidenceProtectedDirectories
        }
        else {
            $leaseTransferred = $true
            $currentMsiInputTransferred = $true
        }
        return $context
    }
    finally {
        if ($null -ne $currentMsiInput -and
            -not $currentMsiInputTransferred) {
            $currentMsiInput.Dispose()
        }
        if ($downloadRootOwned -and -not $leaseTransferred) {
            if ($null -ne $downloadRootLease) {
                $downloadRootLease.Dispose()
            }
            if (Test-Path -LiteralPath $resolvedDownloadRoot) {
                Remove-DesktopPetSafeDirectory `
                    -Path $resolvedDownloadRoot `
                    -AllowedRoot $downloadParent `
                    -TrustedRoot $downloadParent
            }
        }
    }
}
