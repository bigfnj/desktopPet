#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$CurrentReleaseTag,
    [Parameter(Mandatory = $true)][string]$CurrentMsiPath,
    [Parameter(Mandatory = $true)][string]$CurrentRuntimeRoot,
    [Parameter(Mandatory = $true)][string]$RuntimeManifestPath,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [Parameter(Mandatory = $true)][string]$DownloadRoot,
    [Parameter(Mandatory = $true)][string]$GitHubToken,
    [string]$GitHubCliPath,
    [string[]]$AllowedPreviousSignerThumbprints = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'MsiNMinusOneUpgradeGate.Policy.ps1')

$gateContext = Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
    -Repository $Repository `
    -CurrentReleaseTag $CurrentReleaseTag `
    -CurrentMsiPath $CurrentMsiPath `
    -CurrentRuntimeRoot $CurrentRuntimeRoot `
    -RuntimeManifestPath $RuntimeManifestPath `
    -EvidencePath $EvidencePath `
    -DownloadRoot $DownloadRoot `
    -GitHubToken $GitHubToken
if ($gateContext.IsComplete) {
    Write-Host (
        'No lower stable public release with an MSI exists; wrote explicit ' +
        'machine-readable not-applicable evidence.'
    ) -ForegroundColor Yellow
    return
}

$downloadRootLease = $gateContext.DownloadRootLease
if ($null -eq $downloadRootLease) {
    throw 'N-1 policy did not transfer its retained download-root lease.'
}
$resolvedDownloadRoot = [string]$gateContext.ResolvedDownloadRoot
$downloadParent = [string]$gateContext.DownloadParent
$currentMsiInput = $gateContext.CurrentMsiInput
if ($null -eq $currentMsiInput) {
    throw 'N-1 policy did not transfer its retained current-MSI identity.'
}
$previousMsiInput = $null
$checksumInput = $null
try {
$currentMsi = [string]$gateContext.CurrentMsi
$currentRuntime = [string]$gateContext.CurrentRuntime
$manifest = [string]$gateContext.Manifest
$evidence = [string]$gateContext.Evidence
$evidenceParent = [string]$gateContext.EvidenceParent
$evidenceProtectedPaths = [string[]]$gateContext.EvidenceProtectedPaths
$evidenceProtectedDirectories =
    [string[]]$gateContext.EvidenceProtectedDirectories
$currentHash = [string]$gateContext.CurrentHash
$headers = [hashtable]$gateContext.Headers
$prior = $gateContext.Prior
$priorMsiName = [string]$prior.MsiAsset.name
if ([string]::IsNullOrWhiteSpace($priorMsiName) -or
    $priorMsiName -cne [IO.Path]::GetFileName($priorMsiName)) {
    throw "Prior public release has an unsafe MSI asset name: '$priorMsiName'."
}

function Resolve-GitHubReleaseTagCommit {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag
    )

    $reference = Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec 60 `
        -Headers $headers `
        -Uri "https://api.github.com/repos/$Repository/git/ref/tags/$Tag"
    $target = $reference.object
    for ($depth = 0; $depth -lt 8; $depth++) {
        $type = [string]$target.type
        $sha = ([string]$target.sha).ToLowerInvariant()
        if ($sha -cnotmatch '^[0-9a-f]{40}$') {
            throw "Release tag '$Tag' resolved to an invalid Git object SHA."
        }
        if ($type -ceq 'commit') {
            return $sha
        }
        if ($type -cne 'tag') {
            throw "Release tag '$Tag' resolved to unsupported Git object type '$type'."
        }
        $annotatedTag = Invoke-RestMethod `
            -UseBasicParsing `
            -TimeoutSec 60 `
            -Headers $headers `
            -Uri "https://api.github.com/repos/$Repository/git/tags/$sha"
        $target = $annotatedTag.object
    }
    throw "Release tag '$Tag' exceeded the bounded annotated-tag peel depth."
}

$allowedSigners =
    New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
foreach ($value in @($AllowedPreviousSignerThumbprints)) {
    foreach ($candidate in @(
            ([string]$value).Split(
                [char[]]@(',', ';', ' ', [char]9),
                [StringSplitOptions]::RemoveEmptyEntries))) {
        $normalized = $candidate.Replace(' ', '').ToUpperInvariant()
        if ($normalized -cnotmatch '^[0-9A-F]{40}$') {
            throw "Allowed prior-MSI signer thumbprint is invalid: '$candidate'."
        }
        [void]$allowedSigners.Add($normalized)
    }
}
if ($allowedSigners.Count -eq 0) {
    throw (
        'A prior public MSI exists, but no trusted Authenticode signer ' +
        'thumbprint was supplied.')
}
if ([string]::IsNullOrWhiteSpace($GitHubCliPath) -or
    -not (Test-Path -LiteralPath $GitHubCliPath -PathType Leaf)) {
    throw (
        'A prior public MSI exists, but the GitHub CLI path required for ' +
        'artifact-attestation verification is unavailable.')
}
$resolvedGitHubCli = (Resolve-Path -LiteralPath $GitHubCliPath).Path

$checksumAssets = @(
    $prior.Release.assets |
        Where-Object { [string]$_.name -ceq 'SHA256SUMS.txt' })
if ($checksumAssets.Count -ne 1) {
    throw (
        "Prior public MSI '$($prior.Tag)' exists, but its release does not " +
        'contain exactly one SHA256SUMS.txt.')
}

function Receive-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)]$Asset,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $assetUri = [uri][string]$Asset.browser_download_url
    if ($assetUri.Scheme -cne 'https') {
        throw "Prior-release assets require HTTPS: $assetUri"
    }
    $expectedLength = [long]$Asset.size
    if ($expectedLength -le 0) {
        throw "Prior-release asset has an invalid size: $($Asset.name)"
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $client = New-Object Net.Http.HttpClient($handler)
    $request = New-Object Net.Http.HttpRequestMessage(
        [Net.Http.HttpMethod]::Get,
        $assetUri)
    $response = $null
    $input = $null
    $output = $null
    $cancellation = New-Object Threading.CancellationTokenSource
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(180)
        $cancellation.CancelAfter([TimeSpan]::FromSeconds(180))
        foreach ($headerName in $headers.Keys) {
            [void]$request.Headers.TryAddWithoutValidation(
                [string]$headerName,
                [string]$headers[$headerName])
        }
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
                "Prior-release asset download failed with HTTP " +
                "$([int]$response.StatusCode): $($Asset.name)")
        }
        if ($null -ne $response.Content.Headers.ContentLength -and
            [long]$response.Content.Headers.ContentLength -ne
                $expectedLength) {
            throw (
                "Prior-release Content-Length mismatch: $($Asset.name)")
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
            if ($total -gt $expectedLength) {
                throw "Prior-release asset exceeded its API size: $($Asset.name)"
            }
            $output.Write($buffer, 0, $read)
        }
        if ($total -ne $expectedLength) {
            throw "Downloaded prior-release asset length mismatch: $($Asset.name)"
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

$previousMsi = Join-Path $resolvedDownloadRoot (
    $priorMsiName)
$evidenceProtectedPaths += $previousMsi
$checksumPath = Join-Path $resolvedDownloadRoot 'SHA256SUMS.txt'
Receive-ReleaseAsset -Asset $prior.MsiAsset -Destination $previousMsi
Receive-ReleaseAsset `
    -Asset $checksumAssets[0] `
    -Destination $checksumPath
$previousMsiInput = Open-DesktopPetValidatedInputFile `
    -Path $previousMsi `
    -Root $resolvedDownloadRoot
$checksumInput = Open-DesktopPetValidatedInputFile `
    -Path $checksumPath `
    -Root $resolvedDownloadRoot
foreach ($assetBinding in @(
        [pscustomobject]@{
            Asset = $prior.MsiAsset
            Input = $previousMsiInput
        },
        [pscustomobject]@{
            Asset = $checksumAssets[0]
            Input = $checksumInput
        })) {
    if ([long]$assetBinding.Input.Length -ne
        [long]$assetBinding.Asset.size) {
        throw (
            "Retained prior-release asset length mismatch: " +
            $assetBinding.Asset.name)
    }
    $assetDigest = [string]$assetBinding.Asset.digest
    if (-not [string]::IsNullOrWhiteSpace($assetDigest)) {
        if ($assetDigest -cnotmatch '^sha256:[0-9a-f]{64}$') {
            throw (
                "Prior-release asset has malformed API digest: " +
                $assetBinding.Asset.name)
        }
        $observedAssetHash =
            $assetBinding.Input.ComputeHash('SHA256').ToLowerInvariant()
        if ("sha256:$observedAssetHash" -cne $assetDigest) {
            throw (
                "Prior-release asset failed its GitHub API digest: " +
                $assetBinding.Asset.name)
        }
    }
}

$declared = @{}
foreach ($line in (
        $checksumInput.ReadAllTextUtf8(16MB) -split '\r?\n' |
            Where-Object { $_ -cne '' })) {
    if ($line -notmatch
        '^(?<hash>[0-9a-f]{64}) [ *](?<name>[^\\/]+)$') {
        throw "Prior release checksum manifest has a malformed line: '$line'."
    }
    if ($declared.ContainsKey($Matches.name)) {
        throw "Prior release checksum manifest has a duplicate: '$($Matches.name)'."
    }
    $declared[$Matches.name] = $Matches.hash
}
$previousName = [IO.Path]::GetFileName($previousMsi)
if (-not $declared.ContainsKey($previousName)) {
    throw "Prior release checksum manifest does not pin '$previousName'."
}
$expectedPreviousHash = [string]$declared[$previousName]
$observedPreviousHash =
    $previousMsiInput.ComputeHash('SHA256').ToLowerInvariant()
if ($observedPreviousHash -cne $expectedPreviousHash) {
    throw "Prior public MSI '$previousName' failed SHA256SUMS.txt verification."
}

$signature = Get-AuthenticodeSignature -LiteralPath $previousMsi
$signerThumbprint = if ($null -ne $signature.SignerCertificate) {
    (([string]$signature.SignerCertificate.Thumbprint).Replace(
        ' ', '')).ToUpperInvariant()
}
else { '' }
if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
    -not $allowedSigners.Contains($signerThumbprint) -or
    $null -eq $signature.TimeStamperCertificate) {
    throw (
        "Prior public MSI '$previousName' lacks a valid timestamped " +
        "signature from an allowed signer (observed '$signerThumbprint').")
}

$previousSourceCommit = Resolve-GitHubReleaseTagCommit -Tag $prior.Tag
$previousSourceRef = "refs/tags/$($prior.Tag)"
$previousSignerWorkflow =
    "$Repository/.github/workflows/release.yml"
$previousPredicateType = 'https://slsa.dev/provenance/v1'
$attestationPolicy = @(
    '--repo', $Repository,
    '--signer-workflow', $previousSignerWorkflow,
    '--source-ref', $previousSourceRef,
    '--source-digest', $previousSourceCommit,
    '--predicate-type', $previousPredicateType,
    '--deny-self-hosted-runners'
)
$previousGhToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $GitHubToken
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $attestationOutput = @(
            & $resolvedGitHubCli attestation verify $previousMsi `
                @attestationPolicy 2>&1)
        $attestationExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}
finally {
    $env:GH_TOKEN = $previousGhToken
}
if ($attestationExitCode -ne 0) {
    throw (
        "Prior public MSI '$previousName' failed GitHub artifact-attestation " +
        "verification for '$Repository': " +
        (($attestationOutput | ForEach-Object { [string]$_ }) -join ' '))
}

$testEvidenceDirectory = Join-Path $evidenceParent (
    '.DesktopPet-upgrade-test-evidence-' +
    [Guid]::NewGuid().ToString('N'))
Reset-DesktopPetStagingDirectory `
    -Path $testEvidenceDirectory `
    -AllowedRoot $evidenceParent `
    -TrustedRoot $evidenceParent
try {
    $testEvidence = Join-Path $testEvidenceDirectory 'upgrade-test.json'
    $testEvidence = Assert-DesktopPetOutputFileSafe `
        -Path $testEvidence `
        -TrustedRoot $evidenceParent `
        -ProtectedPaths @($evidenceProtectedPaths + $evidence) `
        -ProtectedDirectories $evidenceProtectedDirectories

    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'nminusone-authenticated-before-execution-handoff' `
        -Path $previousMsi
    if ($previousMsiInput.ComputeHash('SHA256').ToLowerInvariant() -cne
            $expectedPreviousHash -or
        $currentMsiInput.ComputeHash('SHA256').ToLowerInvariant() -cne
            $currentHash) {
        throw 'An authenticated MSI changed before the N-1 execution handoff.'
    }
    & (Join-Path $PSScriptRoot 'Test-MsiNMinusOneUpgrade.ps1') `
        -PreviousMsiPath $previousMsi `
        -PreviousReleaseTag $prior.Tag `
        -ExpectedPreviousSha256 $expectedPreviousHash `
        -CurrentMsiPath $currentMsi `
        -CurrentReleaseTag $CurrentReleaseTag `
        -ExpectedCurrentSha256 $currentHash `
        -CurrentRuntimeRoot $currentRuntime `
        -RuntimeManifestPath $manifest `
        -EvidencePath $testEvidence
    if (-not (Test-Path -LiteralPath $testEvidence -PathType Leaf)) {
        throw (
            'Successful N-1 upgrade verification produced no staged ' +
            'evidence document.')
    }
    $evidenceDocument =
        Get-Content -LiteralPath $testEvidence -Raw |
            ConvertFrom-Json
    if ([int]$evidenceDocument.schemaVersion -ne 1 -or
        [string]$evidenceDocument.status -cne 'passed') {
        throw 'N-1 upgrade verification produced invalid staged evidence.'
    }
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiGitHubAttestationVerified `
            -NotePropertyValue $true
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiAttestationRepository `
            -NotePropertyValue $Repository
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiAttestationWorkflow `
            -NotePropertyValue $previousSignerWorkflow
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiAttestationSourceRef `
            -NotePropertyValue $previousSourceRef
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiAttestationSourceDigest `
            -NotePropertyValue $previousSourceCommit
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiAttestationPredicateType `
            -NotePropertyValue $previousPredicateType
    $evidenceDocument |
        Add-Member `
            -NotePropertyName previousMsiAttestationDeniedSelfHostedRunners `
            -NotePropertyValue $true
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiSignerThumbprint `
            -NotePropertyValue $signerThumbprint
    $evidenceDocument |
        Add-Member -NotePropertyName previousMsiTimestampPresent `
            -NotePropertyValue $true

    $evidenceProtectedPaths += $testEvidence
    Publish-DesktopPetMsiNMinusOneEvidence `
        -Document $evidenceDocument `
        -EvidencePath $evidence `
        -EvidenceParent $evidenceParent `
        -ProtectedPaths $evidenceProtectedPaths `
        -ProtectedDirectories $evidenceProtectedDirectories
}
finally {
    if (Test-Path -LiteralPath $testEvidenceDirectory) {
        Remove-DesktopPetSafeDirectory `
            -Path $testEvidenceDirectory `
            -AllowedRoot $evidenceParent `
            -TrustedRoot $evidenceParent
    }
}
}
finally {
    foreach ($msiInput in @(
            $checksumInput,
            $previousMsiInput,
            $currentMsiInput)) {
        if ($null -ne $msiInput) {
            $msiInput.Dispose()
        }
    }
    $downloadRootLease.Dispose()
    if (Test-Path -LiteralPath $resolvedDownloadRoot) {
        Remove-DesktopPetSafeDirectory `
            -Path $resolvedDownloadRoot `
            -AllowedRoot $downloadParent `
            -TrustedRoot $downloadParent
    }
}
