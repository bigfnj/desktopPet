#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workflow = Get-Content -LiteralPath (
    Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$buildWorkflow = Get-Content -LiteralPath (
    Join-Path $repoRoot '.github\workflows\build.yml') -Raw
$releaseGate = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Invoke-ReleaseGate.ps1') -Raw
$syftRunner = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Invoke-LockedSyft.ps1') -Raw
$syftTransaction = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\SyftOutputTransaction.ps1') -Raw
$wixBootstrap = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Install-LockedWixToolchain.ps1') -Raw
$wixToolchainLock = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\wix-toolchain-lock.json') -Raw |
        ConvertFrom-Json
$spdxNormalizer = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Add-RuntimeManifestToSpdx.ps1') -Raw
$runtimeRebuild = Get-Content -LiteralPath (
    Join-Path $repoRoot 'tests\deterministic-runtime-build-selftest.ps1') -Raw
$upgradeGatePath =
    Join-Path $repoRoot 'packaging\Invoke-MsiNMinusOneUpgradeGate.ps1'
$upgradeGate = Get-Content -LiteralPath $upgradeGatePath -Raw
$upgradeGatePolicyPath =
    Join-Path $repoRoot 'packaging\MsiNMinusOneUpgradeGate.Policy.ps1'
$upgradeGatePolicy = Get-Content -LiteralPath $upgradeGatePolicyPath -Raw
. $upgradeGatePolicyPath
$upgradeGateImplementation =
    $upgradeGate + [Environment]::NewLine + $upgradeGatePolicy
$upgradeTest = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Test-MsiNMinusOneUpgrade.ps1') -Raw
$installerBuilder = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\build-installer.ps1') -Raw
$installer = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\DesktopPet.wxs') -Raw
$project = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\DesktopPet_Portable.csproj') -Raw
$sbomRequirements = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\sbom-validation-requirements.txt') -Raw
$releaseWorkflowPolicyPath =
    Join-Path $repoRoot 'packaging\ReleaseWorkflowPolicy.ps1'
. $releaseWorkflowPolicyPath

if ([regex]::Matches(
        $buildWorkflow,
        '(?m)^\s+-AllowDocumentedReleaseBlockers\s*$').Count -ne 1 -or
    $workflow.Contains('-AllowDocumentedReleaseBlockers') -or
    -not $releaseGate.Contains(
        '[switch]$AllowDocumentedReleaseBlockers') -or
    -not [regex]::IsMatch(
        $releaseGate,
        '\$AllowDirtyDevelopment\s+-or\s+\$AllowDocumentedReleaseBlockers') -or
    $buildWorkflow.Contains('actions/upload-artifact@') -or
    $buildWorkflow.Contains('dist\unsigned-ci')) {
    throw (
        'Build CI must admit only the documented corpus/rights blockers, ' +
        'keep the publication workflow strict, and retain protected ' +
        'artifacts only on the ephemeral runner.')
}
$qualityGateIndex = $buildWorkflow.IndexOf(
    '-AllowDocumentedReleaseBlockers',
    [StringComparison]::Ordinal)
$buildStepIndex = $buildWorkflow.IndexOf(
    'Clean build locked Debug x64 payload',
    [StringComparison]::Ordinal)
if ($qualityGateIndex -lt 0 -or
    $buildStepIndex -le $qualityGateIndex) {
    throw (
        'The documented-blocker quality gate must complete before CI build ' +
        'and test steps.')
}

$syftLock = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\syft-toolchain-lock.json') -Raw |
    ConvertFrom-Json
if ([int]$syftLock.schemaVersion -ne 1 -or
    [string]$syftLock.syftVersion -cne '1.42.3' -or
    [string]$syftLock.archive.fileName -cne
        'syft_1.42.3_windows_amd64.zip' -or
    [string]$syftLock.archive.source -cne
        'https://github.com/anchore/syft/releases/download/v1.42.3/syft_1.42.3_windows_amd64.zip' -or
    [long]$syftLock.archive.size -ne 28204841 -or
    [string]$syftLock.archive.sha256 -cne
        'e1b9f4945aa64c2b34970bec617623d7f803d0661b48a50b966768b363322e2d') {
    throw 'Repository Syft lock does not exactly pin the reviewed archive.'
}
if ($workflow.Contains('anchore/sbom-action') -or
    -not $workflow.Contains('.\packaging\Invoke-LockedSyft.ps1') -or
    -not $syftRunner.Contains('Get-RuntimeHashMap') -or
    -not $syftRunner.Contains('Assert-HashMapsEqual') -or
    -not $syftRunner.Contains('syft_runtime_hashes_unchanged=true') -or
    -not $syftRunner.Contains('[IO.FileMode]::CreateNew') -or
    -not $syftRunner.Contains('$archiveInput.ComputeHash(''SHA256'')') -or
    -not $syftRunner.Contains(
        '''locked-syft-archive-validated-before-extract''') -or
    -not $syftRunner.Contains(
        '''locked-syft-version-validated-before-scan''') -or
    -not $syftRunner.Contains('$syftInput.ComputeHash(''SHA256'')')) {
    throw (
        'Release SBOM generation must directly run the size/hash-locked Syft ' +
        'archive and prove runtime hashes unchanged.')
}
foreach ($contract in @(
        '.DesktopPet-syft-output-',
        'Publish-DesktopPetSyftOutputTransaction',
        'ExpectedProvenanceSha256',
        'ConvertFrom-Json',
        'Remove-DesktopPetSafeDirectory')) {
    if (-not $syftRunner.Contains($contract)) {
        throw "Locked Syft output publication is missing: $contract"
    }
}
if ($syftRunner.Contains(
        'Remove-Item -LiteralPath $resolvedOutput -Force')) {
    throw 'Locked Syft still deletes the last valid SBOM before replacement.'
}
$syftPreflightIndex = $syftRunner.IndexOf(
    'Assert-DesktopPetSyftProvenancePreflight',
    [StringComparison]::Ordinal)
$syftWorkIndex = $syftRunner.IndexOf(
    '$beforeHashes = Get-RuntimeHashMap',
    [StringComparison]::Ordinal)
if ($syftPreflightIndex -lt 0 -or
    $syftWorkIndex -le $syftPreflightIndex -or
    -not $releaseGate.Contains(
        'tests/syft-output-transaction-selftest.ps1')) {
    throw (
        'Syft provenance must be preflighted before scan work and its ' +
        'transaction regression must be release-gated.')
}
foreach ($contract in @(
        'New-DesktopPetSyftFileSnapshot',
        'Publish-DesktopPetAtomicFile',
        '.commit-previous-',
        'ExpectedProvenanceSha256',
        'provenanceStateUncertain',
        'publishedProvenanceGuard',
        'DesktopPetRetainRecoveryStaging',
        'RECOVERY_REQUIRED.txt')) {
    if (-not $syftTransaction.Contains($contract)) {
        throw "Syft transactional publication is missing: $contract"
    }
}
$provenanceCommitIndex = $syftTransaction.IndexOf(
    '-DestinationPath $resolvedProvenance',
    [StringComparison]::Ordinal)
$sbomCommitIndex = $syftTransaction.IndexOf(
    '-DestinationPath $resolvedOutput',
    [StringComparison]::Ordinal)
if ($provenanceCommitIndex -lt 0 -or
    $sbomCommitIndex -le $provenanceCommitIndex) {
    throw 'Syft transaction must publish provenance before the canonical SBOM.'
}
foreach ($contract in @(
        'PackageRoot must be an absent, private per-run directory',
        '.DesktopPet-wix-',
        '$lockInput = Open-DesktopPetValidatedInputFile',
        '.DesktopPet-wix-provenance-',
        'Publish-DesktopPetAtomicFile')) {
    if (-not $wixBootstrap.Contains($contract)) {
        throw "Locked WiX bootstrap hardening is missing: $contract"
    }
}
if ($wixBootstrap.Contains('[IO.File]::AppendAllText(')) {
    throw 'Locked WiX provenance is still appended non-atomically.'
}
$lockedUiPackages = @(
    $wixToolchainLock.packages |
        Where-Object {
            [string]$_.id -ceq 'WixToolset.UI.wixext'
        }
)
if ($lockedUiPackages.Count -ne 1 -or
    [string]$lockedUiPackages[0].installedPayload.relativePath -cne
        'wixext5/WixToolset.UI.wixext.dll' -or
    [long]$lockedUiPackages[0].installedPayload.length -ne 773392 -or
    [string]$lockedUiPackages[0].installedPayload.sha256 -cne
        '5ce7f958b6a2b57bcb98c19615c5a3da46976a6e78a6af055fdef99e9e9e8f06') {
    throw 'The WiX UI extension installed payload is not exactly digest-locked.'
}
foreach ($bootstrapContract in @(
        'Get-DesktopPetDotnetGlobalToolRoot',
        'Open-DesktopPetLockedWixExecutable',
        '$installedWixTool.Inputs',
        'Open-DesktopPetLockedWixExtension',
        '$installedWixExtension.Inputs',
        'A non-global WiX extension installation requires a private',
        '$extensionWorkingDirectory = $resolvedToolPath',
        'Join-Path $resolvedToolPath ''.wix\extensions''')) {
    if (-not $wixBootstrap.Contains($bootstrapContract)) {
        throw "Locked WiX bootstrap is missing full handoff: $bootstrapContract"
    }
}
foreach ($lockedWixCaller in @(
        [pscustomobject]@{
            Name = 'installer build'
            Source = $installerBuilder
        },
        [pscustomobject]@{
            Name = 'release workflow'
            Source = $workflow
        })) {
    if (-not $lockedWixCaller.Source.Contains('$wixTool.Inputs') -or
        [regex]::IsMatch(
            $lockedWixCaller.Source,
            '\$wixTool\.Input(?!s)')) {
        throw (
            "$($lockedWixCaller.Name) must retain every validated file in " +
            'the locked WiX tool payload, not only wix.exe.')
    }
}
foreach ($lockedExtensionCaller in @(
        [pscustomobject]@{
            Name = 'installer build'
            Source = $installerBuilder
        },
        [pscustomobject]@{
            Name = 'release workflow'
            Source = $workflow
        })) {
    foreach ($contract in @(
            'Open-DesktopPetLockedWixExtension',
            '$wixExtension.Inputs',
            '''-ext'', $wixExtensionPath')) {
        if (-not $lockedExtensionCaller.Source.Contains($contract)) {
            throw (
                "$($lockedExtensionCaller.Name) is missing digest-locked " +
                "WiX UI extension handoff: $contract")
        }
    }
    if ($lockedExtensionCaller.Source.Contains(
            '''-ext'', ''WixToolset.UI.wixext''')) {
        throw (
            "$($lockedExtensionCaller.Name) resolves the WiX UI extension " +
            'by mutable cache name instead of its retained DLL path.')
    }
}
if (-not $installerBuilder.Contains(
        'foreach ($wixToolInput in @($wixTool.Inputs))') -or
    -not $installerBuilder.Contains(
        '$retainedInputs.Add($wixToolInput)') -or
    -not [regex]::IsMatch(
        $workflow,
        '(?s)\$wixTool\.Inputs.{0,1200}\.Dispose\(\)')) {
    throw (
        'Locked WiX callers do not explicitly retain and dispose the complete ' +
        'validated tool-package payload.')
}
foreach ($sealedPublicationContract in @(
        [pscustomobject]@{
            Name = 'SPDX enrichment'
            Source = $spdxNormalizer
            Markers = @(
                '$temporarySbomLease.Seal()',
                '$expectedSerializedSbomHash',
                '''sbom-final-bytes-written-before-seal''',
                '''sbom-sealed-validate''',
                '-SealedTemporaryFile $sealedTemporarySbom',
                '-ExpectedDestinationSha256 $originalSbomHash')
        },
        [pscustomobject]@{
            Name = 'WiX provenance'
            Source = $wixBootstrap
            Markers = @(
                '$provenanceFileLease.Seal()',
                '''wix-provenance-sealed-validate''',
                'SealedTemporaryFile = $sealedProvenanceFile')
        },
        [pscustomobject]@{
            Name = 'N-1 gate evidence'
            Source = $upgradeGatePolicy
            Markers = @(
                '$temporaryEvidenceLease.Seal()',
                '''nminusone-evidence-sealed-validate''',
                'SealedTemporaryFile = $sealedTemporaryEvidence')
        },
        [pscustomobject]@{
            Name = 'N-1 lifecycle evidence'
            Source = $upgradeTest
            Markers = @(
                '$sealedTemporaryEvidence = Open-DesktopPetSealedStagedFile',
                '''nminusone-operational-evidence-sealed-validate''',
                'SealedTemporaryFile = $sealedTemporaryEvidence')
        },
        [pscustomobject]@{
            Name = 'installer MSI'
            Source = $installerBuilder
            Markers = @(
                '$sealedStagedMsi = Open-DesktopPetSealedStagedFile',
                '''installer-msi-sealed-validate''',
                'SealedTemporaryFile = $sealedStagedMsi')
        })) {
    foreach ($marker in $sealedPublicationContract.Markers) {
        if (-not $sealedPublicationContract.Source.Contains($marker)) {
            throw (
                "$($sealedPublicationContract.Name) is missing sealed " +
                "publication contract: $marker")
        }
    }
}
foreach ($contract in @(
        'SOURCE_DATE_EPOCH',
        'show -s --format=%ct HEAD',
        'Get-DesktopPetDeterministicSpdxCreated',
        '$creationInfoProperty.Value.created =')) {
    if (-not $spdxNormalizer.Contains($contract)) {
        throw "Deterministic SPDX timestamp contract is missing: $contract"
    }
}

$payloadTableRegression =
    '.\tests\packaged-payload-table-selftest.ps1'
if ([regex]::Matches(
        $buildWorkflow,
        [regex]::Escape($payloadTableRegression)).Count -ne 1 -or
    [regex]::Matches(
        $workflow,
        [regex]::Escape($payloadTableRegression)).Count -ne 1) {
    throw (
        'Build and release workflows must each run the real-MSI ' +
        'sibling-directory payload regression exactly once.')
}

$setupPythonAction =
    'actions/setup-python@e797f83bcb11b83ae66e0230d6156d7c80228e7c'
$setupPattern =
    '(?m)^\s+uses:\s+' + [regex]::Escape($setupPythonAction) + '\s*$'
if ([regex]::Matches($buildWorkflow, $setupPattern).Count -ne 1 -or
    [regex]::Matches($workflow, $setupPattern).Count -ne 3 -or
    [regex]::Matches(
        $buildWorkflow,
        "(?m)^\s+python-version:\s+'3\.13\.5'\s*$"
    ).Count -ne 1 -or
    [regex]::Matches(
        $workflow,
        "(?m)^\s+python-version:\s+'3\.13\.5'\s*$"
    ).Count -ne 3 -or
    [regex]::Matches(
        $buildWorkflow,
        '(?m)DESKTOPPET_SBOM_PYTHON=\$python'
    ).Count -ne 1 -or
    [regex]::Matches(
        $workflow,
        '(?m)DESKTOPPET_SBOM_PYTHON=\$python'
    ).Count -ne 3) {
    throw (
        'Every CI/release job that performs official SPDX validation must ' +
        'select the commit-pinned Python action and export the exact interpreter.')
}

$attestBuildProvenanceAction =
    'actions/attest-build-provenance@0f67c3f4856b2e3261c31976d6725780e5e4c373'
$attestBuildProvenancePattern =
    '(?m)^\s+uses:\s+' +
    [regex]::Escape($attestBuildProvenanceAction) +
    '\s*$'
if ([regex]::Matches(
        $workflow,
        $attestBuildProvenancePattern
    ).Count -ne 1) {
    throw 'Release attestation must use the reviewed immutable v4.1.1 action commit.'
}
foreach ($contract in @(
        'ref: ${{ github.sha }}',
        'DISPATCH_SHA: ${{ github.sha }}',
        '$dispatchCommit = ([string]$env:DISPATCH_SHA).Trim().ToLowerInvariant()',
        '$tagCommit -ne $headCommit -or $headCommit -ne $dispatchCommit',
        '$expectedWorkflowRef = "refs/tags/$env:RELEASE_TAG"',
        'if ($env:WORKFLOW_REF -cne $expectedWorkflowRef)',
        'git merge-base --is-ancestor $headCommit $defaultRef')) {
    if (-not $workflow.Contains($contract)) {
        throw "Release tag-ref/default-branch ancestry contract is missing: $contract"
    }
}
if ($workflow.Contains('ref: ${{ inputs.tag }}')) {
    throw 'Release checkout still resolves a mutable tag after workflow dispatch.'
}
if ($workflow.Contains(
        '$env:WORKFLOW_REF -cne "refs/heads/$defaultBranch"')) {
    throw 'Release workflow still emits branch-scoped rather than tag-scoped attestations.'
}

function Get-WorkflowJobText {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$JobName
    )
    $pattern = (
        '(?ms)^  ' + [regex]::Escape($JobName) +
        ':\r?\n.*?(?=^  [A-Za-z0-9_-]+:\r?\n|\z)')
    $match = [regex]::Match($Source, $pattern)
    if (-not $match.Success) {
        throw "Workflow job is missing: $JobName"
    }
    return $match.Value
}
foreach ($job in @(
        [pscustomobject]@{
            Name = 'build-test-package'
            Source = $buildWorkflow
            Requirements = 'packaging\sbom-validation-requirements.txt'
        },
        [pscustomobject]@{
            Name = 'build_test_package'
            Source = $workflow
            Requirements = 'packaging\sbom-validation-requirements.txt'
        },
        [pscustomobject]@{
            Name = 'assemble_release'
            Source = $workflow
            Requirements =
                'validated-source\packaging\sbom-validation-requirements.txt'
        },
        [pscustomobject]@{
            Name = 'verify_signed_release'
            Source = $workflow
            Requirements =
                'validated-source\packaging\sbom-validation-requirements.txt'
        })) {
    $jobText = Get-WorkflowJobText `
        -Source $job.Source `
        -JobName $job.Name
    foreach ($contract in @(
            $setupPythonAction,
            "python-version: '3.13.5'",
            '--only-binary=:all: --require-hashes',
            [string]$job.Requirements,
            'DESKTOPPET_SBOM_PYTHON=$python')) {
        if (-not $jobText.Contains($contract)) {
            throw (
                "Workflow job '$($job.Name)' lacks hermetic SPDX Python " +
                "contract: $contract")
        }
    }
}
foreach ($lockedRequirement in @(
        'attrs==26.1.0',
        'jsonschema==4.26.0',
        'jsonschema-specifications==2025.9.1',
        'referencing==0.37.0',
        'rpds-py==2026.6.3',
        'typing-extensions==4.16.0',
        '--hash=sha256:c647aa4a12dfbad9333ca4e71fe62ddc36f4e63b2d260a37a8b83d2f043ac309',
        '--hash=sha256:d489f15263b8d200f8387e64b4c3a75f06629559fb73deb8fdfb525f2dab50ce',
        '--hash=sha256:98802fee3a11ee76ecaca44429fda8a41bff98b00a0f2838151b113f210cc6fe',
        '--hash=sha256:381329a9f99628c9069361716891d34ad94af76e461dcb0335825aecc7692231',
        '--hash=sha256:9250a9a0a6fd4648b3f868da8d91a4c52b5811a62df58e753d50ae4454a36f80',
        '--hash=sha256:481caa481374e813c1b176ada14e97f1f67a4539ce9cfeb3f350d78d6370c2e8')) {
    if (-not $sbomRequirements.Contains($lockedRequirement)) {
        throw "SBOM validator dependency lock is missing: $lockedRequirement"
    }
}
foreach ($workflowText in @($buildWorkflow, $workflow)) {
    if (-not $workflowText.Contains('--only-binary=:all: --require-hashes') -or
        -not $workflowText.Contains(
            'sbom-validation-requirements.txt')) {
        throw 'SBOM validator requirements are not installed fail-closed.'
    }
}

foreach ($pin in @(
        '<PackageReference Include="Microsoft.Net.Compilers.Toolset"',
        'Version="[4.14.0]"',
        '<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48"',
        'Version="[1.0.3]"',
        'PrivateAssets="all"')) {
    if (-not $project.Contains($pin)) {
        throw "Project is missing immutable compiler/reference pin: $pin"
    }
}
$packageLock = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\packages.lock.json') -Raw | ConvertFrom-Json
$framework = $packageLock.dependencies.'.NETFramework,Version=v4.8'
$expectedLocks = @(
    [pscustomobject]@{
        Id = 'Microsoft.Net.Compilers.Toolset'
        Version = '4.14.0'
        Hash = '/F7wcMX7Al+nXbNII/6F4b33F3WCPL1I5VbY0+9QPiKdzlYPM0+R+nyYPBsxVZ4cwGXeU0yxrL0ITJWejsvN/Q=='
    },
    [pscustomobject]@{
        Id = 'Microsoft.NETFramework.ReferenceAssemblies.net48'
        Version = '1.0.3'
        Hash = 'zMk4D+9zyiEWByyQ7oPImPN/Jhpj166Ky0Nlla4eXlNL8hI/BtSJsgR8Inldd4NNpIAH3oh8yym0W2DrhXdSLQ=='
    })
foreach ($expected in $expectedLocks) {
    $entry = $framework.($expected.Id)
    if ([string]$entry.requested -cne
            "[$($expected.Version), $($expected.Version)]" -or
        [string]$entry.resolved -cne $expected.Version -or
        [string]$entry.contentHash -cne $expected.Hash) {
        throw "Content-addressed build-tool lock mismatch: $($expected.Id)"
    }
}
foreach ($contract in @(
        '.\tests\deterministic-runtime-build-selftest.ps1',
        '-KeepVerifiedRuntime',
        'independent_runtime_rebuild_hashes_match=true')) {
    if (-not ($workflow.Contains($contract) -or
            $runtimeRebuild.Contains($contract))) {
        throw "Independent runtime rebuild contract is missing: $contract"
    }
}
foreach ($contract in @(
        '$env:NUGET_PACKAGES = $packageCache',
        'UseSharedCompilation = ''false''',
        'MSBUILDDISABLENODEREUSE = ''1''',
        '$packageRootNodes',
        '$observedPackageRoot.Equals(',
        '$(NuGetPackageRoot)\microsoft.net.compilers.toolset\',
        '$(NuGetPackageRoot)\microsoft.netframework.referenceassemblies.net48\',
        'Assert-MapsEqual -First $first.Runtime -Second $second.Runtime',
        'Get-ContentTreeHash -Root $compilerRoot',
        'Get-ContentTreeHash -Root $referenceRoot')) {
    if (-not $runtimeRebuild.Contains($contract)) {
        throw "Runtime reproducibility test is missing: $contract"
    }
}

if (-not $installer.Contains('Id="RemoveObsoleteUpgradeProbe"') -or
    -not $installer.Contains('Name="DesktopPet.obsolete-upgrade-probe"') -or
    -not $installer.Contains('On="install"') -or
    -not $workflow.Contains(
        "'RemoveFile', 'RemoveFolder', 'RegistrySearch'")) {
    throw 'Current MSI does not actively remove the N-1 obsolete-file probe.'
}
foreach ($contract in @(
        'SHA256SUMS.txt',
        'Sort-Object Version -Descending',
        'Test-MsiNMinusOneUpgrade.ps1',
        '-ExpectedPreviousSha256 $expectedPreviousHash',
        'Get-AuthenticodeSignature -LiteralPath $previousMsi',
        '$allowedSigners.Contains($signerThumbprint)',
        'attestation verify $previousMsi',
        '--signer-workflow',
        '--source-ref',
        '--source-digest',
        '--predicate-type',
        '--deny-self-hosted-runners',
        'Resolve-GitHubReleaseTagCommit',
        '[IO.FileMode]::CreateNew',
        '$previousMsiInput = Open-DesktopPetValidatedInputFile',
        'CurrentMsiInput = if ($prior.Count -eq 0)',
        '''nminusone-authenticated-before-execution-handoff''')) {
    if (-not $upgradeGateImplementation.Contains($contract)) {
        throw "Prior public MSI discovery/hash-pin contract is missing: $contract"
    }
}
foreach ($contract in @(
        'Assert-DesktopPetOutputFileSafe',
        '.DesktopPet-upgrade-test-evidence-',
        'Publish-DesktopPetAtomicFile',
        'Publish-DesktopPetMsiNMinusOneEvidence')) {
    if (-not $upgradeGateImplementation.Contains($contract)) {
        throw "N-1 gate atomic-evidence contract is missing: $contract"
    }
}
foreach ($contract in @(
        'Assert-DesktopPetOutputFileSafe',
        'Publish-NMinusOneEvidence',
        'Publish-DesktopPetAtomicFile')) {
    if (-not $upgradeTest.Contains($contract)) {
        throw "N-1 lifecycle atomic-evidence contract is missing: $contract"
    }
}
if ($upgradeGateImplementation.Contains(
        '[IO.File]::WriteAllText(' + [Environment]::NewLine +
        '    $evidence,') -or
    $upgradeTest.Contains(
        '[IO.File]::WriteAllText(' + [Environment]::NewLine +
        '        $resolvedEvidence,')) {
    throw 'N-1 evidence is still written directly to its publication path.'
}
if (-not $upgradeGateImplementation.Contains(
        '$pageResponse = Invoke-RestMethod') -or
    -not $upgradeGateImplementation.Contains('$batch = @($pageResponse)') -or
    $upgradeGateImplementation -match
        '(?s)\$batch\s*=\s*@\(\s*Invoke-RestMethod') {
    throw (
        'Prior public MSI discovery must normalize an empty GitHub release ' +
        'page without the Windows PowerShell nested-empty-array defect.')
}
if (-not $upgradeGate.Contains(
        'MsiNMinusOneUpgradeGate.Policy.ps1') -or
    -not $upgradeGate.Contains(
        'Invoke-DesktopPetMsiNMinusOneUpgradePolicy') -or
    $upgradeGatePolicy.Contains('Test-MsiNMinusOneUpgrade.ps1') -or
    $upgradeGatePolicy.Contains('Invoke-MsiNMinusOneUpgradeGate.ps1')) {
    throw (
        'The N-1 entrypoint must delegate discovery/publication to the safe ' +
        'policy, and that policy must not invoke either lifecycle entrypoint.')
}

$releaseSelectionFixture = @(
    [pscustomobject]@{
        tag_name = 'v9.7.0'
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{ name = 'selected.msi' })
    },
    [pscustomobject]@{
        tag_name = 'v9.6.0'
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{ name = 'older.msi' })
    },
    [pscustomobject]@{
        tag_name = 'v9.7.5'
        draft = $true
        prerelease = $false
        assets = @([pscustomobject]@{ name = 'draft.msi' })
    },
    [pscustomobject]@{
        tag_name = 'v9.9.0'
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{ name = 'newer.msi' })
    },
    [pscustomobject]@{
        tag_name = 'v9.5.0'
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{ name = 'notes.txt' })
    }
)
$selectedPrior = @(
    Select-DesktopPetPriorPublicMsiRelease `
        -Releases $releaseSelectionFixture `
        -CurrentVersion ([version]'9.8.7')
)
if ($selectedPrior.Count -ne 1 -or
    [string]$selectedPrior[0].Tag -cne 'v9.7.0' -or
    [string]$selectedPrior[0].MsiAsset.name -cne 'selected.msi') {
    throw 'Safe N-1 policy did not select the newest eligible stable MSI.'
}
$emptyPrior = @(
    Select-DesktopPetPriorPublicMsiRelease `
        -Releases @() `
        -CurrentVersion ([version]'9.8.7')
)
if ($emptyPrior.Count -ne 0) {
    throw 'Safe N-1 policy did not accept an empty release collection.'
}

$fixtureSha = '1' * 40
$fixtureRepository = 'bigfnj/desktopPet'
$fixtureBranch = 'master'
$realBuildRun = [pscustomobject]@{
    id = 101
    run_attempt = 1
    run_number = 10
    head_sha = $fixtureSha
    head_branch = $fixtureBranch
    path = '.github/workflows/build.yml'
    event = 'push'
    status = 'completed'
    conclusion = 'success'
    repository = [pscustomobject]@{ full_name = $fixtureRepository }
    head_repository = [pscustomobject]@{ full_name = $fixtureRepository }
}
$spoofBuildRun = [pscustomobject]@{
    id = 202
    run_attempt = 1
    run_number = 11
    head_sha = $fixtureSha
    head_branch = $fixtureBranch
    path = '.github/workflows/spoof.yml'
    event = 'push'
    status = 'completed'
    conclusion = 'success'
    repository = [pscustomobject]@{ full_name = $fixtureRepository }
    head_repository = [pscustomobject]@{ full_name = $fixtureRepository }
}
$failedRealBuildRun = $realBuildRun.PSObject.Copy()
$failedRealBuildRun.id = 303
$failedRealBuildRun.conclusion = 'failure'
$spoofRejected = $false
try {
    [void](Select-DesktopPetSuccessfulBuildWorkflowRun `
        -WorkflowRuns @($spoofBuildRun, $failedRealBuildRun) `
        -ExpectedRepository $fixtureRepository `
        -ExpectedHeadSha $fixtureSha `
        -ExpectedHeadBranch $fixtureBranch)
}
catch {
    $spoofRejected = $true
}
if (-not $spoofRejected) {
    throw 'A duplicate-name check from a different workflow bypassed the CI gate.'
}
$selectedBuildRun = Select-DesktopPetSuccessfulBuildWorkflowRun `
    -WorkflowRuns @($spoofBuildRun, $realBuildRun) `
    -ExpectedRepository $fixtureRepository `
    -ExpectedHeadSha $fixtureSha `
    -ExpectedHeadBranch $fixtureBranch
if ([long]$selectedBuildRun.id -ne 101) {
    throw 'The authenticated build workflow run selector chose the wrong run.'
}
$fixtureJobs = @(
    [pscustomobject]@{
        run_id = 101
        name = 'Validate fortune labeling pipeline'
        status = 'completed'
        conclusion = 'success'
    },
    [pscustomobject]@{
        run_id = 101
        name = 'Build, test, audit, and package unsigned x64'
        status = 'completed'
        conclusion = 'success'
    }
)
Assert-DesktopPetRequiredBuildWorkflowJobs `
    -Jobs $fixtureJobs `
    -ExpectedRunId 101
$wrongRunJobsRejected = $false
try {
    Assert-DesktopPetRequiredBuildWorkflowJobs `
        -Jobs @(
            $fixtureJobs[0],
            [pscustomobject]@{
                run_id = 202
                name = 'Build, test, audit, and package unsigned x64'
                status = 'completed'
                conclusion = 'success'
            }
        ) `
        -ExpectedRunId 101
}
catch {
    $wrongRunJobsRejected = $true
}
if (-not $wrongRunJobsRejected) {
    throw 'A required job from a different workflow run bypassed the CI gate.'
}

foreach ($contract in @(
        "-Operation '/i'",
        '-Msi $previousMsi',
        '-Msi $currentMsi',
        "-Operation '/x'",
        'taskkill.exe',
        '"/PID $($Process.Id) /T /F"',
        '$stopper.WaitForExit(10000)',
        '$Process.WaitForExit(10000)',
        'Global\_MSIExecute',
        'Wait-WindowsInstallerQuiescence -WaitSeconds 30',
        '$script:installerQuiescenceConfirmed',
        'Skipping MSI cleanup transactions because Windows Installer',
        'Cleanup decisions come from Windows Installer product state',
        'DesktopPet.obsolete-upgrade-probe',
        'settingsPreservedThroughUpgradeAndUninstall',
        'downgradeRejected',
        'exactCurrentRuntimeInstalled',
        'inputMsiHashesPreserved',
        '$previousMsiInput = Open-DesktopPetValidatedInputFile',
        '$currentMsiInput = Open-DesktopPetValidatedInputFile',
        '''nminusone-before-msiexec-start''')) {
    if (-not $upgradeTest.Contains($contract)) {
        throw "Actual N-1 lifecycle contract is missing: $contract"
    }
}
if ($upgradeTest.Contains('$process.Kill()')) {
    throw (
        'N-1 timeout handling must stop the complete msiexec process tree, ' +
        'not only the parent process.')
}
foreach ($contract in @(
        'Invoke-MsiNMinusOneUpgradeGate.ps1',
        'Test-MsiUpgradeEvidence.ps1',
        'DesktopPet-AI-Edition-$env:PRODUCT_VERSION.upgrade-evidence.json',
        '${{ needs.seal_release.outputs.release_artifact_id }}')) {
    if (-not $workflow.Contains($contract)) {
        throw "Release workflow does not gate publication on evidence: $contract"
    }
}
$validateReleaseJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'validate_release'
$verifySignedJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'verify_signed_release'
$nMinusOneJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'verify_n_minus_one'
$sealReleaseJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'seal_release'
$attestReleaseJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'attest_release'
$publishReleaseJob = Get-WorkflowJobText `
    -Source $workflow `
    -JobName 'publish_release'

foreach ($contract in @(
        'Require a resumable stable release with expected asset names',
        'Existing release assets are not an exact expected subset.',
        'Published release is incomplete; refusing to mutate it.',
        '[string]$asset.state -cne ''uploaded''',
        'if (-not [bool]$release.isDraft -and',
        '$seen.Count -ne $expected.Count')) {
    if (-not $validateReleaseJob.Contains($contract)) {
        throw "Initial release reconciliation policy is missing: $contract"
    }
}
foreach ($contract in @(
        'verify_remote_release subset "$verification_root/existing"',
        'gh release download "$RELEASE_TAG"',
        '--pattern "$remote_name"',
        'local_hash="$(sha256sum -- "$local_path"',
        'remote_hash="$(sha256sum -- "$remote_path"',
        'if [[ "$remote_hash" != "$local_hash" ]]',
        'Existing release asset differs from the locally sealed artifact',
        'asset_snapshot_before="$(',
        'asset_snapshot_after="$(',
        'Release assets changed while their bytes were being verified',
        'MISSING_ASSETS+=("$candidate")',
        'if (( ${#MISSING_ASSETS[@]} > 0 )); then',
        'gh release upload "$RELEASE_TAG" "${MISSING_ASSETS[@]}"',
        'verify_remote_release exact "$verification_root/before-publication"',
        'Published release is incomplete; refusing to mutate it.',
        'Already-published release exactly matches the locally sealed assets',
        'verify_remote_release exact "$verification_root/after-publication"')) {
    if (-not $publishReleaseJob.Contains($contract)) {
        throw "Retry-safe release publication policy is missing: $contract"
    }
}
foreach ($forbidden in @(
        '--clobber',
        'Require an empty existing draft release',
        'Draft release must have no assets before the run',
        'gh release upload "$RELEASE_TAG" "${assets[@]}"')) {
    if ($workflow.Contains($forbidden)) {
        throw "Release publication retains a destructive one-shot policy: $forbidden"
    }
}
$subsetVerificationIndex = $publishReleaseJob.IndexOf(
    'verify_remote_release subset "$verification_root/existing"',
    [StringComparison]::Ordinal)
$missingUploadIndex = $publishReleaseJob.IndexOf(
    'gh release upload "$RELEASE_TAG" "${MISSING_ASSETS[@]}"',
    [StringComparison]::Ordinal)
$missingUploadGuardIndex = $publishReleaseJob.IndexOf(
    'if (( ${#MISSING_ASSETS[@]} > 0 )); then',
    [StringComparison]::Ordinal)
$publishedRerunIndex = $publishReleaseJob.IndexOf(
    'Already-published release exactly matches the locally sealed assets',
    [StringComparison]::Ordinal)
$exactPrePublishIndex = $publishReleaseJob.IndexOf(
    'verify_remote_release exact "$verification_root/before-publication"',
    [StringComparison]::Ordinal)
$publishTransitionIndex = $publishReleaseJob.IndexOf(
    'gh release edit "$RELEASE_TAG" --draft=false',
    [StringComparison]::Ordinal)
$exactPostPublishIndex = $publishReleaseJob.IndexOf(
    'verify_remote_release exact "$verification_root/after-publication"',
    [StringComparison]::Ordinal)
if ($subsetVerificationIndex -lt 0 -or
    $publishedRerunIndex -le $subsetVerificationIndex -or
    $missingUploadGuardIndex -le $publishedRerunIndex -or
    $missingUploadIndex -le $subsetVerificationIndex -or
    $missingUploadIndex -le $missingUploadGuardIndex -or
    $exactPrePublishIndex -le $missingUploadIndex -or
    $publishTransitionIndex -le $exactPrePublishIndex -or
    $exactPostPublishIndex -le $publishTransitionIndex) {
    throw (
        'Release publication must verify an expected subset, upload only ' +
        'missing assets, re-download the exact set before publication, and ' +
        'verify the published set afterward.')
}
if ([regex]::Matches(
        $publishReleaseJob,
        '(?m)^\s+gh release edit "\$RELEASE_TAG" --draft=false'
    ).Count -ne 1 -or
    [regex]::Matches(
        $publishReleaseJob,
        '(?m)^\s+gh release upload "\$RELEASE_TAG"'
    ).Count -ne 1 -or
    [regex]::Matches(
        $publishReleaseJob,
        '(?m)^\s+assert_tag_commit\s*$'
    ).Count -lt 4) {
    throw (
        'Release reconciliation must have one publication transition, one ' +
        'missing-only upload site, and repeated immutable-tag checks.')
}
if ($verifySignedJob.Contains('Invoke-MsiNMinusOneUpgradeGate.ps1') -or
    -not $verifySignedJob.Contains('Test-MsiLifecycle.ps1') -or
    [regex]::Matches(
        $workflow,
        '(?m)Invoke-MsiNMinusOneUpgradeGate\.ps1'
    ).Count -ne 1) {
    throw (
        'Current signed-MSI lifecycle and N-1 execution are not isolated ' +
        'into distinct workflow jobs.')
}
foreach ($contract in @(
        '${{ needs.sign_msi.outputs.release_artifact_id }}',
        'attestations: read',
        'upgrade-evidence/',
        'WINDOWS_PREVIOUS_SIGNING_CERTIFICATE_THUMBPRINTS',
        '-GitHubCliPath $gh',
        '-AllowedPreviousSignerThumbprints $allowedSigners')) {
    if (-not $nMinusOneJob.Contains($contract)) {
        throw "Isolated N-1 workflow job lacks authentication contract: $contract"
    }
}
if ($nMinusOneJob.Contains('path: release-assets/') -or
    -not $nMinusOneJob.Contains('Upload only N-1 evidence')) {
    throw 'The executable N-1 job can upload more than its isolated evidence.'
}
foreach ($contract in @(
        '${{ needs.sign_msi.outputs.release_artifact_id }}',
        '${{ needs.verify_n_minus_one.outputs.evidence_artifact_id }}',
        'Test-MsiUpgradeEvidence.ps1',
        'Upload sealed release assets')) {
    if (-not $sealReleaseJob.Contains($contract)) {
        throw "Fresh release sealer lacks pristine-input contract: $contract"
    }
}
if ($sealReleaseJob.Contains('Test-MsiLifecycle.ps1') -or
    $sealReleaseJob.Contains('Invoke-MsiNMinusOneUpgradeGate.ps1') -or
    -not $attestReleaseJob.Contains(
        '${{ needs.seal_release.outputs.release_artifact_id }}') -or
    -not $publishReleaseJob.Contains(
        '${{ needs.seal_release.outputs.release_artifact_id }}')) {
    throw (
        'Final sealing, attestation, or publication can consume a workspace ' +
        'that executed an MSI.')
}
if ([regex]::Matches(
        $workflow,
        '(?m)DesktopPet-AI-Edition-\$PRODUCT_VERSION\.upgrade-evidence\.json'
    ).Count -lt 2) {
    throw 'Upgrade evidence is not included in both final validation and upload.'
}

$evidenceValidator =
    Join-Path $repoRoot 'packaging\Test-MsiUpgradeEvidence.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $tempRoot (
    'DesktopPet-UpgradeEvidence-' + [Guid]::NewGuid().ToString('N'))
$evidencePath = Join-Path $scratch 'evidence.json'
$dummyHash = 'a' * 64
try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $gateFixture = Join-Path $scratch 'gate-atomic-publication'
    $gateRuntime = Join-Path $gateFixture 'runtime'
    New-Item -ItemType Directory -Path $gateRuntime -Force | Out-Null
    $gateCurrentMsi = Join-Path $gateFixture 'current.msi'
    $gateManifest = Join-Path $gateFixture 'runtime-files.txt'
    $gateEvidence = Join-Path $gateFixture 'gate-evidence.json'
    $gateDownload = Join-Path $gateFixture 'downloads'
    [IO.File]::WriteAllText(
        $gateCurrentMsi,
        'dummy-current-msi',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $gateManifest,
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    $gateParameters = @{
        Repository = 'bigfnj/desktopPet'
        CurrentReleaseTag = 'v9.8.7'
        CurrentMsiPath = $gateCurrentMsi
        CurrentRuntimeRoot = $gateRuntime
        RuntimeManifestPath = $gateManifest
        DownloadRoot = $gateDownload
        GitHubToken = 'self-test-token'
    }
    & {
        function Invoke-RestMethod {
            param(
                [switch]$UseBasicParsing,
                [int]$TimeoutSec,
                [hashtable]$Headers,
                [string]$Uri
            )
            return @()
        }

        [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
            @gateParameters `
            -EvidencePath $gateEvidence)
        $gateDocument =
            Get-Content -LiteralPath $gateEvidence -Raw |
                ConvertFrom-Json
        if ([string]$gateDocument.reason -cne 'no_prior_public_msi') {
            throw 'Mocked no-prior gate did not publish valid evidence.'
        }

        $gateCurrentHash = (
            Get-FileHash `
                -LiteralPath $gateCurrentMsi `
                -Algorithm SHA256).Hash
        $directAliasRejected = $false
        try {
            [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
                @gateParameters `
                -EvidencePath $gateCurrentMsi)
        }
        catch {
            $directAliasRejected =
                $_.Exception.Message -match
                    'overlaps a protected packaging input'
        }
        if (-not $directAliasRejected -or
            (Get-FileHash `
                -LiteralPath $gateCurrentMsi `
                -Algorithm SHA256).Hash -cne $gateCurrentHash) {
            throw 'N-1 evidence direct-input alias was not rejected safely.'
        }

        $hardLinkEvidence =
            Join-Path $gateFixture 'gate-evidence-hardlink.json'
        New-Item `
            -ItemType HardLink `
            -Path $hardLinkEvidence `
            -Target $gateCurrentMsi | Out-Null
        $hardLinkRejected = $false
        try {
            [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
                @gateParameters `
                -EvidencePath $hardLinkEvidence)
        }
        catch {
            $hardLinkRejected =
                $_.Exception.Message -match 'hard-link alias'
        }
        if (-not $hardLinkRejected) {
            throw 'N-1 evidence hard-link alias was not rejected.'
        }
        Remove-Item -LiteralPath $hardLinkEvidence -Force

        $outsideEvidenceRoot =
            Join-Path $gateFixture 'outside-evidence-root'
        New-Item -ItemType Directory -Path $outsideEvidenceRoot |
            Out-Null
        $outsideSentinel =
            Join-Path $outsideEvidenceRoot 'must-survive.txt'
        [IO.File]::WriteAllText(
            $outsideSentinel,
            'must-survive',
            (New-Object Text.UTF8Encoding($false)))
        $linkedEvidenceRoot =
            Join-Path $gateFixture 'linked-evidence-root'
        $evidenceJunction = New-Item `
            -ItemType Junction `
            -Path $linkedEvidenceRoot `
            -Target $outsideEvidenceRoot
        try {
            $reparseRejected = $false
            try {
                [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
                    @gateParameters `
                    -EvidencePath (
                        Join-Path $linkedEvidenceRoot 'evidence.json'))
            }
            catch {
                $reparseRejected =
                    $_.Exception.Message -match 'reparse point'
            }
            if (-not $reparseRejected -or
                [IO.File]::ReadAllText($outsideSentinel) -cne
                    'must-survive') {
                throw (
                    'N-1 evidence reparse parent was not rejected safely.')
            }
        }
        finally {
            if (Test-Path -LiteralPath $linkedEvidenceRoot) {
                $linkedEvidenceItem =
                    Get-Item -LiteralPath $linkedEvidenceRoot -Force
                if (($linkedEvidenceItem.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -eq 0) {
                    throw (
                        'N-1 evidence fixture unexpectedly stopped being ' +
                        'a junction.')
                }
                [IO.Directory]::Delete($linkedEvidenceItem.FullName)
            }
        }

        [IO.File]::WriteAllText(
            $gateEvidence,
            "{`"lastGood`":true}`n",
            (New-Object Text.UTF8Encoding($false)))
        $lastGoodHash = (
            Get-FileHash `
                -LiteralPath $gateEvidence `
                -Algorithm SHA256).Hash
        $heldEvidence = [IO.File]::Open(
            $gateEvidence,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $publicationRejected = $false
        try {
            try {
                [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
                    @gateParameters `
                    -EvidencePath $gateEvidence)
            }
            catch {
                $publicationRejected = $true
            }
        }
        finally {
            $heldEvidence.Dispose()
        }
        if (-not $publicationRejected -or
            (Get-FileHash `
                -LiteralPath $gateEvidence `
                -Algorithm SHA256).Hash -cne $lastGoodHash) {
            throw (
                'Failed N-1 evidence publication did not preserve the ' +
                'last-good document.')
        }

        $overlapParameters = $gateParameters.Clone()
        $overlapParameters.DownloadRoot = $gateRuntime
        $downloadOverlapRejected = $false
        try {
            [void](Invoke-DesktopPetMsiNMinusOneUpgradePolicy `
                @overlapParameters `
                -EvidencePath $gateEvidence)
        }
        catch {
            $downloadOverlapRejected =
                $_.Exception.Message -match
                    'overlaps the current runtime root'
        }
        if (-not $downloadOverlapRejected) {
            throw 'N-1 download staging accepted the current runtime root.'
        }
    }

    $document = [ordered]@{
        schemaVersion = 1
        status = 'not_applicable'
        reason = 'no_prior_public_msi'
        currentReleaseTag = 'v9.8.7'
        currentMsiSha256 = $dummyHash
        previousReleaseTag = $null
    }
    [IO.File]::WriteAllText(
        $evidencePath,
        (($document | ConvertTo-Json -Depth 4) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    & $evidenceValidator `
        -EvidencePath $evidencePath `
        -ExpectedCurrentReleaseTag 'v9.8.7' `
        -ExpectedCurrentMsiSha256 $dummyHash

    $document.status = 'passed'
    [void]$document.Remove('reason')
    [IO.File]::WriteAllText(
        $evidencePath,
        (($document | ConvertTo-Json -Depth 4) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    $rejected = $false
    try {
        & $evidenceValidator `
            -EvidencePath $evidencePath `
            -ExpectedCurrentReleaseTag 'v9.8.7' `
            -ExpectedCurrentMsiSha256 $dummyHash
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Incomplete passed upgrade evidence was accepted.'
    }

    $passedDocument = [ordered]@{
        schemaVersion = 1
        status = 'passed'
        currentReleaseTag = 'v9.8.7'
        currentProductVersion = '9.8.7'
        currentProductCode = '{11111111-1111-1111-1111-111111111111}'
        currentMsiSha256 = $dummyHash
        previousReleaseTag = 'v9.8.6'
        previousProductVersion = '9.8.6'
        previousProductCode = '{22222222-2222-2222-2222-222222222222}'
        previousMsiSha256 = 'b' * 64
        upgradeCode = '{33333333-3333-3333-3333-333333333333}'
        runtimeFileCount = 43
        exactCurrentRuntimeInstalled = $true
        obsoleteFileProbe = 'DesktopPet.obsolete-upgrade-probe'
        obsoleteFileRemoved = $true
        settingsSha256 = 'c' * 64
        settingsPreservedThroughUpgradeAndUninstall = $true
        downgradeRejected = $true
        downgradeExitCode = 1603
        uninstallCompleted = $true
        inputMsiHashesPreserved = $true
        previousMsiGitHubAttestationVerified = $true
        previousMsiAttestationRepository = 'bigfnj/desktopPet'
        previousMsiAttestationWorkflow =
            'bigfnj/desktopPet/.github/workflows/release.yml'
        previousMsiAttestationSourceRef = 'refs/tags/v9.8.6'
        previousMsiAttestationSourceDigest = 'd' * 40
        previousMsiAttestationPredicateType =
            'https://slsa.dev/provenance/v1'
        previousMsiAttestationDeniedSelfHostedRunners = $true
        previousMsiSignerThumbprint = 'A' * 40
        previousMsiTimestampPresent = $true
    }
    [IO.File]::WriteAllText(
        $evidencePath,
        (($passedDocument | ConvertTo-Json -Depth 5) +
            [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    & $evidenceValidator `
        -EvidencePath $evidencePath `
        -ExpectedCurrentReleaseTag 'v9.8.7' `
        -ExpectedCurrentMsiSha256 $dummyHash `
        -ExpectedAttestationRepository 'bigfnj/desktopPet'

    $policyMutations = [ordered]@{
        previousMsiAttestationWorkflow =
            'bigfnj/desktopPet/.github/workflows/other.yml'
        previousMsiAttestationSourceRef = 'refs/heads/master'
        previousMsiAttestationSourceDigest = 'not-a-commit'
        previousMsiAttestationPredicateType =
            'https://example.invalid/predicate'
        previousMsiAttestationDeniedSelfHostedRunners = $false
    }
    foreach ($property in $policyMutations.Keys) {
        $original = $passedDocument[$property]
        $passedDocument[$property] = $policyMutations[$property]
        [IO.File]::WriteAllText(
            $evidencePath,
            (($passedDocument | ConvertTo-Json -Depth 5) +
                [Environment]::NewLine),
            (New-Object Text.UTF8Encoding($false)))
        $policyMutationRejected = $false
        try {
            & $evidenceValidator `
                -EvidencePath $evidencePath `
                -ExpectedCurrentReleaseTag 'v9.8.7' `
                -ExpectedCurrentMsiSha256 $dummyHash `
                -ExpectedAttestationRepository 'bigfnj/desktopPet'
        }
        catch {
            $policyMutationRejected = $true
        }
        if (-not $policyMutationRejected) {
            throw "N-1 attestation policy mutation '$property' was accepted."
        }
        $passedDocument[$property] = $original
    }

    $passedDocument.exactCurrentRuntimeInstalled = 'true'
    [IO.File]::WriteAllText(
        $evidencePath,
        (($passedDocument | ConvertTo-Json -Depth 5) +
            [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    $stringBooleanRejected = $false
    try {
        & $evidenceValidator `
            -EvidencePath $evidencePath `
            -ExpectedCurrentReleaseTag 'v9.8.7' `
            -ExpectedCurrentMsiSha256 $dummyHash
    }
    catch {
        $stringBooleanRejected = $true
    }
    if (-not $stringBooleanRejected) {
        throw 'String-spoofed Boolean upgrade evidence was accepted.'
    }

    [IO.File]::Delete($evidencePath)
    $missingRejected = $false
    try {
        & $evidenceValidator `
            -EvidencePath $evidencePath `
            -ExpectedCurrentReleaseTag 'v9.8.7' `
            -ExpectedCurrentMsiSha256 $dummyHash
    }
    catch {
        $missingRejected =
            $_.Exception.Message -match
                '(?i)machine-readable N-1 upgrade evidence is absent'
    }
    if (-not $missingRejected) {
        throw 'Absent machine-readable upgrade evidence was not rejected.'
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        . (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}

Write-Host (
    'PASS: Syft archive, compiler/reference toolchain, independent rebuild, ' +
    'and machine-readable N-1 MSI upgrade publication gates are locked.'
) -ForegroundColor Green
