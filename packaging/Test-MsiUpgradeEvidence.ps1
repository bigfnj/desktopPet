#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$ExpectedCurrentReleaseTag,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedCurrentMsiSha256,
    [ValidatePattern('^$|^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$ExpectedAttestationRepository = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw "Required machine-readable N-1 upgrade evidence is absent: $EvidencePath"
}
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne 1 -or
    [string]$evidence.currentReleaseTag -cne
        $ExpectedCurrentReleaseTag -or
    [string]$evidence.currentMsiSha256 -cne
        $ExpectedCurrentMsiSha256.ToLowerInvariant()) {
    throw 'N-1 upgrade evidence does not match the current release artifact.'
}

$status = [string]$evidence.status
if ($status -ceq 'not_applicable') {
    if ([string]$evidence.reason -cne 'no_prior_public_msi' -or
        $null -ne $evidence.previousReleaseTag) {
        throw 'Not-applicable upgrade evidence is malformed.'
    }
}
elseif ($status -ceq 'passed') {
    foreach ($field in @(
            'exactCurrentRuntimeInstalled',
            'obsoleteFileRemoved',
            'settingsPreservedThroughUpgradeAndUninstall',
            'downgradeRejected',
            'uninstallCompleted',
            'inputMsiHashesPreserved',
            'previousMsiGitHubAttestationVerified',
            'previousMsiAttestationDeniedSelfHostedRunners',
            'previousMsiTimestampPresent')) {
        $value = $evidence.$field
        if ($value -isnot [bool] -or -not $value) {
            throw "N-1 upgrade evidence did not pass required check '$field'."
        }
    }
    foreach ($field in @(
            'previousReleaseTag',
            'previousProductVersion',
            'previousProductCode',
            'previousMsiSha256',
            'upgradeCode',
            'settingsSha256',
            'previousMsiAttestationRepository',
            'previousMsiAttestationWorkflow',
            'previousMsiAttestationSourceRef',
            'previousMsiAttestationSourceDigest',
            'previousMsiAttestationPredicateType',
            'previousMsiSignerThumbprint')) {
        if ([string]::IsNullOrWhiteSpace([string]$evidence.$field)) {
            throw "N-1 upgrade evidence is missing '$field'."
        }
    }
    if ([string]$evidence.previousReleaseTag -notmatch
            '^v\d+\.\d+\.\d+$' -or
        [string]$evidence.currentProductVersion -notmatch
            '^\d+\.\d+\.\d+$' -or
        [string]$evidence.previousProductVersion -notmatch
            '^\d+\.\d+\.\d+$' -or
        [string]$evidence.currentProductCode -notmatch
            '^\{[0-9A-F-]{36}\}$' -or
        [string]$evidence.previousProductCode -notmatch
            '^\{[0-9A-F-]{36}\}$' -or
        [string]$evidence.upgradeCode -notmatch
            '^\{[0-9A-F-]{36}\}$' -or
        [string]$evidence.previousMsiSha256 -notmatch
            '^[0-9a-f]{64}$' -or
        [string]$evidence.settingsSha256 -notmatch
            '^[0-9a-f]{64}$' -or
        [string]$evidence.obsoleteFileProbe -cne
            'DesktopPet.obsolete-upgrade-probe' -or
        [string]$evidence.previousMsiAttestationRepository -notmatch
            '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        [string]$evidence.previousMsiAttestationSourceDigest -cnotmatch
            '^[0-9a-f]{40}$' -or
        [string]$evidence.previousMsiSignerThumbprint -cnotmatch
            '^[0-9A-F]{40}$' -or
        [int]$evidence.runtimeFileCount -lt 1 -or
        [int]$evidence.downgradeExitCode -notin @(1603, 1638)) {
        throw 'Passed N-1 upgrade evidence contains invalid typed values.'
    }
    if (-not [string]::IsNullOrWhiteSpace(
            $ExpectedAttestationRepository) -and
        [string]$evidence.previousMsiAttestationRepository -cne
            $ExpectedAttestationRepository) {
        throw (
            'Passed N-1 upgrade evidence was not authenticated for the ' +
            'expected repository.')
    }
    $attestationRepository =
        [string]$evidence.previousMsiAttestationRepository
    if ([string]$evidence.previousMsiAttestationWorkflow -cne
            "$attestationRepository/.github/workflows/release.yml" -or
        [string]$evidence.previousMsiAttestationSourceRef -cne
            "refs/tags/$([string]$evidence.previousReleaseTag)" -or
        [string]$evidence.previousMsiAttestationPredicateType -cne
            'https://slsa.dev/provenance/v1') {
        throw 'Passed N-1 evidence has an invalid release-attestation policy.'
    }
    if ([string]$evidence.currentReleaseTag -cne
            "v$($evidence.currentProductVersion)" -or
        [string]$evidence.previousReleaseTag -cne
            "v$($evidence.previousProductVersion)" -or
        [version]$evidence.previousProductVersion -ge
            [version]$evidence.currentProductVersion) {
        throw 'Passed N-1 upgrade evidence has inconsistent release versions.'
    }
}
else {
    throw "Unsupported N-1 upgrade evidence status: '$status'."
}

Write-Host (
    "N-1 upgrade evidence is valid for ${ExpectedCurrentReleaseTag}: $status"
) -ForegroundColor Green
