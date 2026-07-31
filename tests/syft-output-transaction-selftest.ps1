#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
. (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
. (Join-Path $repoRoot 'packaging\SyftOutputTransaction.ps1')

$tempRoot =
    Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot (
    'DesktopPet-SyftTransactionSelfTest-' +
    [Guid]::NewGuid().ToString('N'))

function New-SyftTransactionCase {
    param([Parameter(Mandatory = $true)][string]$Name)

    $caseRoot = Join-Path $scratch $Name
    $artifactRoot = Join-Path $caseRoot 'artifacts'
    $outputStaging = Join-Path $artifactRoot 'output-staging'
    $provenanceStaging = Join-Path $artifactRoot 'provenance-staging'
    New-Item -ItemType Directory -Path @(
        $artifactRoot,
        $outputStaging,
        $provenanceStaging) -Force | Out-Null

    $output = Join-Path $artifactRoot 'DesktopPet.spdx.json'
    $provenance = Join-Path $artifactRoot 'BUILD-PROVENANCE.txt'
    $stagedOutput = Join-Path $outputStaging 'DesktopPet.spdx.json.tmp'
    $stagedProvenance =
        Join-Path $provenanceStaging 'BUILD-PROVENANCE.txt.tmp'
    [IO.File]::WriteAllText(
        $output,
        '{"lastGoodSbom":true}',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $provenance,
        "last_good_provenance=true`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $stagedOutput,
        '{"spdxVersion":"SPDX-2.3","newSbom":true}',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $stagedProvenance,
        "last_good_provenance=true`nnew_syft_evidence=true`n",
        (New-Object Text.UTF8Encoding($false)))

    return [pscustomobject]@{
        Root = $caseRoot
        ArtifactRoot = $artifactRoot
        OutputStaging = $outputStaging
        ProvenanceStaging = $provenanceStaging
        Output = $output
        Provenance = $provenance
        StagedOutput = $stagedOutput
        StagedProvenance = $stagedProvenance
        OutputHash = (
            Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        ProvenanceHash = (
            Get-FileHash -LiteralPath $provenance -Algorithm SHA256).Hash
    }
}

function Assert-SyftSeededHashesUnchanged {
    param(
        [Parameter(Mandatory = $true)]$Case,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $outputHash = (
        Get-FileHash -LiteralPath $Case.Output -Algorithm SHA256).Hash
    $provenanceHash = (
        Get-FileHash -LiteralPath $Case.Provenance -Algorithm SHA256).Hash
    if ($outputHash -cne $Case.OutputHash -or
        $provenanceHash -cne $Case.ProvenanceHash) {
        throw (
            "Syft transaction case '$Name' changed a seeded destination. " +
            "SBOM=$outputHash; provenance=$provenanceHash")
    }
}

function Invoke-SyftTransactionFixture {
    param(
        [Parameter(Mandatory = $true)]$Case,
        [hashtable]$AdditionalParameters = @{}
    )

    $parameters = @{
        StagedOutputPath = $Case.StagedOutput
        OutputPath = $Case.Output
        OutputRoot = $Case.ArtifactRoot
        StagedProvenancePath = $Case.StagedProvenance
        ProvenancePath = $Case.Provenance
        ProvenanceRoot = $Case.ArtifactRoot
        ExpectedProvenanceSha256 = $Case.ProvenanceHash
    }
    foreach ($key in $AdditionalParameters.Keys) {
        $parameters[$key] = $AdditionalParameters[$key]
    }
    Publish-DesktopPetSyftOutputTransaction @parameters
}

function New-SyftSimulatedAbruptException {
    param([Parameter(Mandatory = $true)][string]$Boundary)

    $exception = New-Object InvalidOperationException(
        "Simulated abrupt Syft interruption at $Boundary.")
    $exception.Data['DesktopPetSimulatedAbruptTermination'] = $true
    return $exception
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $missingNewlineHash = -join ('A' * 64)
    $missingNewlineDocument =
        New-DesktopPetSyftBoundProvenanceDocument `
            -ExistingContent 'existing_without_newline=true' `
            -EvidenceLines @('syft_version=1.42.3') `
            -StagedOutputSha256 $missingNewlineHash
    $expectedBoundary =
        'existing_without_newline=true' +
        [Environment]::NewLine +
        'syft_version=1.42.3' +
        [Environment]::NewLine +
        'syft_sbom_spdx_sha256=' + $missingNewlineHash +
        [Environment]::NewLine
    if ($missingNewlineDocument.Content -cne $expectedBoundary -or
        $missingNewlineDocument.BindingLine -cne
            ('syft_sbom_spdx_sha256=' + $missingNewlineHash)) {
        throw (
            'Syft provenance did not add a canonical newline boundary and ' +
            'exact staged-SBOM SHA-256 binding.')
    }

    $missingPreflight = New-SyftTransactionCase -Name 'missing-preflight'
    $missingPath =
        Join-Path $missingPreflight.ArtifactRoot 'missing-provenance.txt'
    $missingRejected = $false
    try {
        [void](Assert-DesktopPetSyftProvenancePreflight `
            -Path $missingPath `
            -Root $missingPreflight.ArtifactRoot `
            -ProtectedPaths @($missingPreflight.Output))
    }
    catch {
        $missingRejected =
            $_.Exception.Message -match '(?i)provenance file does not exist'
    }
    if (-not $missingRejected -or
        (Test-Path -LiteralPath $missingPath)) {
        throw 'Missing Syft provenance did not fail during preflight.'
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $missingPreflight `
        -Name 'missing-preflight'

    $invalidPreflight = New-SyftTransactionCase -Name 'invalid-preflight'
    [IO.File]::WriteAllBytes(
        $invalidPreflight.Provenance,
        [byte[]]@(0xC3, 0x28))
    $invalidPreflight.ProvenanceHash = (
        Get-FileHash `
            -LiteralPath $invalidPreflight.Provenance `
            -Algorithm SHA256).Hash
    $invalidRejected = $false
    try {
        [void](Assert-DesktopPetSyftProvenancePreflight `
            -Path $invalidPreflight.Provenance `
            -Root $invalidPreflight.ArtifactRoot `
            -ProtectedPaths @($invalidPreflight.Output))
    }
    catch {
        $invalidRejected =
            $_.Exception.Message -match '(?i)(utf-?8|fallback|byte)'
    }
    if (-not $invalidRejected) {
        throw 'Invalid UTF-8 Syft provenance passed its early preflight.'
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $invalidPreflight `
        -Name 'invalid-preflight'

    $missingStaged = New-SyftTransactionCase -Name 'missing-staged'
    Remove-DesktopPetTreeNode `
        -Path $missingStaged.StagedProvenance `
        -AllowedRoot $missingStaged.ArtifactRoot `
        -AllowedFinalRoot (
            Get-DesktopPetFinalPath -Path $missingStaged.ArtifactRoot) `
        -TrustedRoot $missingStaged.ArtifactRoot
    $missingStagedRejected = $false
    try {
        Invoke-SyftTransactionFixture -Case $missingStaged
    }
    catch {
        $missingStagedRejected =
            $_.Exception.Message -match '(?i)staged Syft provenance is missing'
    }
    if (-not $missingStagedRejected) {
        throw 'Missing staged Syft provenance did not fail before publication.'
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $missingStaged `
        -Name 'missing-staged'

    $cleanupPredicate = New-SyftTransactionCase -Name 'cleanup-predicate'
    $cleanupSnapshot = New-DesktopPetSyftFileSnapshot `
        -Path $cleanupPredicate.Output `
        -Root $cleanupPredicate.ArtifactRoot `
        -BackupPath (
            Join-Path $cleanupPredicate.OutputStaging 'predicate-backup.tmp')
    $script:cleanupPredicateInjected = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if (-not $script:cleanupPredicateInjected -and
            $Operation -ceq 'syft-snapshot-match-before-dispose') {
            $script:cleanupPredicateInjected = $true
            throw 'Injected snapshot predicate disposal failure.'
        }
    }
    $cleanupPredicateThrew = $false
    try {
        $cleanupPredicateResult =
            Test-DesktopPetSyftSnapshotMatches -Snapshot $cleanupSnapshot
    }
    catch {
        $cleanupPredicateThrew = $true
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $cleanupSnapshot.SealedBackupFile.Dispose()
    }
    if ($cleanupPredicateThrew -or
        $cleanupPredicateResult -or
        -not $script:cleanupPredicateInjected) {
        throw (
            'Snapshot predicate cleanup failure did not conservatively return ' +
            'false without overriding control flow.')
    }

    $borrowed = New-SyftTransactionCase -Name 'borrowed-sealed-leases'
    $borrowedOutputHash = (
        Get-FileHash -LiteralPath $borrowed.StagedOutput -Algorithm SHA256).Hash
    $borrowedProvenanceHash = (
        Get-FileHash `
            -LiteralPath $borrowed.StagedProvenance `
            -Algorithm SHA256).Hash
    $borrowedOutputLease =
        Open-DesktopPetSealedStagedFile `
            -Path $borrowed.StagedOutput `
            -Root $borrowed.OutputStaging
    $borrowedProvenanceLease =
        Open-DesktopPetSealedStagedFile `
            -Path $borrowed.StagedProvenance `
            -Root $borrowed.ProvenanceStaging
    try {
        Invoke-SyftTransactionFixture `
            -Case $borrowed `
            -AdditionalParameters @{
                SealedStagedOutputFile = $borrowedOutputLease
                ExpectedStagedOutputSha256 = $borrowedOutputHash
                SealedStagedProvenanceFile = $borrowedProvenanceLease
                ExpectedStagedProvenanceSha256 = $borrowedProvenanceHash
            }
        if ($borrowedOutputLease.ComputeHash('SHA256') -cne
                $borrowedOutputHash -or
            $borrowedProvenanceLease.ComputeHash('SHA256') -cne
                $borrowedProvenanceHash) {
            throw 'Borrowed sealed Syft leases changed after publication.'
        }
    }
    finally {
        $borrowedProvenanceLease.Dispose()
        $borrowedOutputLease.Dispose()
    }

    foreach ($boundaryCase in @(
            [pscustomobject]@{
                Name = 'journal-boundary'
                Operation = 'syft-transaction-journal-prepared'
                OutputState = 'old'
                ProvenanceState = 'old'
            },
            [pscustomobject]@{
                Name = 'first-commit-boundary'
                Operation = 'syft-transaction-after-first-commit'
                OutputState = 'old'
                ProvenanceState = 'new'
            },
            [pscustomobject]@{
                Name = 'second-commit-boundary'
                Operation = 'syft-transaction-after-second-commit'
                OutputState = 'new'
                ProvenanceState = 'new'
            })) {
        $boundary = New-SyftTransactionCase -Name $boundaryCase.Name
        $boundaryNewOutputHash = (
            Get-FileHash `
                -LiteralPath $boundary.StagedOutput `
                -Algorithm SHA256).Hash
        $boundaryNewProvenanceHash = (
            Get-FileHash `
                -LiteralPath $boundary.StagedProvenance `
                -Algorithm SHA256).Hash
        $script:boundaryOperation = $boundaryCase.Operation
        $script:liveJournalMoveBlocked = $false
        $script:liveJournalDeleteBlocked = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($Operation, $Path)
            if ($Operation -ceq $script:boundaryOperation) {
                if ($Operation -ceq
                    'syft-transaction-journal-prepared') {
                    try {
                        Move-Item `
                            -LiteralPath $Path `
                            -Destination ($Path + '.attacker-moved') `
                            -ErrorAction Stop
                    }
                    catch {
                        $script:liveJournalMoveBlocked = $true
                    }
                    try {
                        Remove-Item `
                            -LiteralPath $Path `
                            -Force `
                            -ErrorAction Stop
                    }
                    catch {
                        $script:liveJournalDeleteBlocked = $true
                    }
                }
                throw (
                    New-SyftSimulatedAbruptException `
                        -Boundary $script:boundaryOperation)
            }
        }
        $boundaryFailure = $null
        try {
            Invoke-SyftTransactionFixture -Case $boundary
        }
        catch {
            $boundaryFailure = $_
        }
        finally {
            Remove-Variable `
                -Name DesktopPetStagingMutationTestHook `
                -Scope Script `
                -ErrorAction SilentlyContinue
        }
        $boundaryJournal =
            Get-DesktopPetSyftTransactionJournalPath `
                -OutputPath $boundary.Output `
                -OutputRoot $boundary.ArtifactRoot
        if ($null -eq $boundaryFailure -or
            -not (Test-DesktopPetSyftTransactionRequiresRecovery `
                -Exception $boundaryFailure.Exception) -or
            -not (Test-Path -LiteralPath $boundaryJournal -PathType Leaf)) {
            throw (
                "Simulated interruption at '$($boundaryCase.Operation)' did " +
                'not retain a durable recovery journal.')
        }
        if ($boundaryCase.Operation -ceq
                'syft-transaction-journal-prepared' -and
            (-not $script:liveJournalMoveBlocked -or
             -not $script:liveJournalDeleteBlocked)) {
            throw (
                'The live Syft transaction journal did not deny concurrent ' +
                'rename/delete while the transaction lease was active.')
        }
        $boundaryJournalData =
            Get-Content -LiteralPath $boundaryJournal -Raw |
                ConvertFrom-Json
        if ([int]$boundaryJournalData.schemaVersion -ne 1 -or
            [string]$boundaryJournalData.state -cne
                'prepared-before-first-commit' -or
            [string]$boundaryJournalData.outputNewSha256 -cne
                $boundaryNewOutputHash -or
            [string]$boundaryJournalData.provenanceNewSha256 -cne
                $boundaryNewProvenanceHash -or
            -not (Test-Path `
                -LiteralPath $boundaryJournalData.outputBackupPath `
                -PathType Leaf) -or
            -not (Test-Path `
                -LiteralPath $boundaryJournalData.provenanceBackupPath `
                -PathType Leaf) -or
            (Get-FileHash `
                -LiteralPath $boundaryJournalData.outputBackupPath `
                -Algorithm SHA256).Hash -cne $boundary.OutputHash -or
            (Get-FileHash `
                -LiteralPath $boundaryJournalData.provenanceBackupPath `
                -Algorithm SHA256).Hash -cne $boundary.ProvenanceHash) {
            throw (
                "Durable journal at '$($boundaryCase.Operation)' did not " +
                'identify exact old/new hashes and retained backup evidence.')
        }
        $expectedBoundaryOutputHash = if (
            $boundaryCase.OutputState -ceq 'new') {
            $boundaryNewOutputHash
        }
        else {
            $boundary.OutputHash
        }
        $expectedBoundaryProvenanceHash = if (
            $boundaryCase.ProvenanceState -ceq 'new') {
            $boundaryNewProvenanceHash
        }
        else {
            $boundary.ProvenanceHash
        }
        if ((Get-FileHash `
                -LiteralPath $boundary.Output `
                -Algorithm SHA256).Hash -cne
                $expectedBoundaryOutputHash -or
            (Get-FileHash `
                -LiteralPath $boundary.Provenance `
                -Algorithm SHA256).Hash -cne
                $expectedBoundaryProvenanceHash) {
            throw (
                "Simulated interruption at '$($boundaryCase.Operation)' " +
                'did not stop at the expected commit boundary.')
        }
        $blockedByJournal = $false
        try {
            Invoke-SyftTransactionFixture -Case $boundary
        }
        catch {
            $blockedByJournal =
                $_.Exception.Message -match
                    '(?i)prior Syft two-output transaction journal'
        }
        if (-not $blockedByJournal) {
            throw (
                "A surviving journal from '$($boundaryCase.Operation)' did " +
                'not fail closed on the next invocation.')
        }
    }

    $absentClean = New-SyftTransactionCase -Name 'absent-output-clean-rollback'
    Remove-DesktopPetTreeNode `
        -Path $absentClean.Output `
        -AllowedRoot $absentClean.ArtifactRoot `
        -AllowedFinalRoot (
            Get-DesktopPetFinalPath -Path $absentClean.ArtifactRoot) `
        -TrustedRoot $absentClean.ArtifactRoot
    $script:absentCleanInjected = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if (-not $script:absentCleanInjected -and
            $Operation -ceq 'syft-transaction-after-second-commit') {
            $script:absentCleanInjected = $true
            throw 'Injected clean absent-destination rollback.'
        }
    }
    $absentCleanFailure = $null
    try {
        Invoke-SyftTransactionFixture -Case $absentClean
    }
    catch {
        $absentCleanFailure = $_
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    $absentCleanJournal =
        Get-DesktopPetSyftTransactionJournalPath `
            -OutputPath $absentClean.Output `
            -OutputRoot $absentClean.ArtifactRoot
    $absentCleanOutputPresent =
        Test-Path -LiteralPath $absentClean.Output
    $absentCleanJournalPresent =
        Test-Path -LiteralPath $absentCleanJournal
    $absentCleanObservedProvenanceHash = $null
    if (Test-Path -LiteralPath $absentClean.Provenance -PathType Leaf) {
        $absentCleanObservedProvenanceHash = (
            Get-FileHash `
                -LiteralPath $absentClean.Provenance `
                -Algorithm SHA256).Hash
    }
    if ($null -eq $absentCleanFailure -or
        -not $script:absentCleanInjected -or
        $absentCleanOutputPresent -or
        $absentCleanJournalPresent -or
        $absentCleanObservedProvenanceHash -cne
            $absentClean.ProvenanceHash) {
        throw (
            'Exact-handle absent-state rollback did not restore absence and ' +
            "the original provenance. Failure=$($absentCleanFailure.Exception.Message); " +
            "outputPresent=$absentCleanOutputPresent; " +
            "journalPresent=$absentCleanJournalPresent; " +
            "provenance=$absentCleanObservedProvenanceHash")
    }

    $absentRace = New-SyftTransactionCase -Name 'absent-output-race'
    Remove-DesktopPetTreeNode `
        -Path $absentRace.Output `
        -AllowedRoot $absentRace.ArtifactRoot `
        -AllowedFinalRoot (
            Get-DesktopPetFinalPath -Path $absentRace.ArtifactRoot) `
        -TrustedRoot $absentRace.ArtifactRoot
    $absentCompetitor = Join-Path $absentRace.ArtifactRoot (
        'absent-race-competitor.tmp')
    $script:absentDisplaced = Join-Path $absentRace.ArtifactRoot (
        'absent-race-published.displaced')
    [IO.File]::WriteAllText(
        $absentCompetitor,
        'concurrent_output=true',
        (New-Object Text.UTF8Encoding($false)))
    $absentCompetitorHash = (
        Get-FileHash -LiteralPath $absentCompetitor -Algorithm SHA256).Hash
    $script:absentCompetitor = $absentCompetitor
    $script:absentOutput = $absentRace.Output
    $script:absentFailureInjected = $false
    $script:absentCompetitorInstalled = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if (-not $script:absentFailureInjected -and
            $Operation -ceq 'syft-transaction-after-second-commit') {
            $script:absentFailureInjected = $true
            throw 'Injected failure after absent-destination SBOM commit.'
        }
        if ($script:absentFailureInjected -and
            -not $script:absentCompetitorInstalled -and
            $Operation -ceq 'before-syft-absent-rollback-delete') {
            [IO.File]::Replace(
                $script:absentCompetitor,
                $script:absentOutput,
                $script:absentDisplaced,
                $true)
            $script:absentCompetitorInstalled = $true
        }
    }
    $absentFailure = $null
    try {
        Invoke-SyftTransactionFixture -Case $absentRace
    }
    catch {
        $absentFailure = $_
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if ($null -eq $absentFailure -or
        -not (Test-DesktopPetSyftTransactionRequiresRecovery `
            -Exception $absentFailure.Exception) -or
        -not $script:absentCompetitorInstalled -or
        (Get-FileHash `
            -LiteralPath $absentRace.Output `
            -Algorithm SHA256).Hash -cne $absentCompetitorHash) {
        throw (
            'Absent-state rollback did not preserve a raced-in competitor and ' +
            'retain recovery evidence.')
    }

    $locked = New-SyftTransactionCase -Name 'locked-provenance'
    $heldProvenance = [IO.File]::Open(
        $locked.Provenance,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $script:lockedProvenancePath =
        [IO.Path]::GetFullPath($locked.Provenance)
    $script:lockedPublicationReached = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if ($Operation -ceq 'publish' -and
            [IO.Path]::GetFullPath($Path).Equals(
                $script:lockedProvenancePath,
                [StringComparison]::OrdinalIgnoreCase)) {
            $script:lockedPublicationReached = $true
        }
    }
    $lockedRejected = $false
    try {
        try {
            Invoke-SyftTransactionFixture -Case $locked
        }
        catch {
            $lockedRejected = $true
        }
    }
    finally {
        $heldProvenance.Dispose()
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $lockedRejected -or
        -not $script:lockedPublicationReached) {
        throw (
            'Locked Syft provenance did not fail at publication. ' +
            "Reached=$($script:lockedPublicationReached)")
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $locked `
        -Name 'locked-provenance'

    $injected = New-SyftTransactionCase -Name 'injected-second-commit'
    $script:injectedOutputPath =
        [IO.Path]::GetFullPath($injected.Output)
    $script:injectedOutputReached = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if ($Operation -ceq 'publish' -and
            [IO.Path]::GetFullPath($Path).Equals(
                $script:injectedOutputPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            $script:injectedOutputReached = $true
            throw 'Injected second-commit SBOM publication failure.'
        }
    }
    $injectedRejected = $false
    $injectedFailureMessage = ''
    try {
        Invoke-SyftTransactionFixture -Case $injected
    }
    catch {
        $injectedFailureMessage = $_.Exception.Message
        $injectedRejected =
            $injectedFailureMessage -match '(?i)injected second-commit'
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $injectedRejected -or
        -not $script:injectedOutputReached) {
        throw (
            'Injected second Syft publication failure was not exercised. ' +
            "Reached=$($script:injectedOutputReached); " +
            "failure=$injectedFailureMessage")
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $injected `
        -Name 'injected-second-commit'

    $race = New-SyftTransactionCase -Name 'commit-time-replacement'
    $externalProvenance = Join-Path $race.ArtifactRoot (
        'concurrent-provenance.tmp')
    $externalDisplaced = Join-Path $race.ArtifactRoot (
        'concurrent-provenance.displaced')
    [IO.File]::WriteAllText(
        $externalProvenance,
        "concurrent_provenance=true`n",
        (New-Object Text.UTF8Encoding($false)))
    $externalHash = (
        Get-FileHash -LiteralPath $externalProvenance -Algorithm SHA256).Hash
    $script:raceProvenancePath =
        [IO.Path]::GetFullPath($race.Provenance)
    $script:raceInjected = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if (-not $script:raceInjected -and
            $Operation -ceq 'before-publish-lease' -and
            [IO.Path]::GetFullPath($Path).Equals(
                $script:raceProvenancePath,
                [StringComparison]::OrdinalIgnoreCase)) {
            $script:raceInjected = $true
            [IO.File]::Replace(
                $externalProvenance,
                $script:raceProvenancePath,
                $externalDisplaced,
                $true)
        }
    }
    $raceRejected = $false
    try {
        Invoke-SyftTransactionFixture -Case $race
    }
    catch {
        $raceRejected =
            $_.Exception.Message -match '(?i)provenance changed'
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    $raceOutputHash = (
        Get-FileHash -LiteralPath $race.Output -Algorithm SHA256).Hash
    $raceProvenanceHash = (
        Get-FileHash -LiteralPath $race.Provenance -Algorithm SHA256).Hash
    $commitBackups = @(
        Get-ChildItem `
            -LiteralPath $race.ProvenanceStaging `
            -File `
            -Filter '.BUILD-PROVENANCE.txt.commit-previous-*')
    if (-not $raceRejected -or
        -not $script:raceInjected -or
        $raceOutputHash -cne $race.OutputHash -or
        $raceProvenanceHash -cne $externalHash -or
        $commitBackups.Count -ne 1 -or
        (Get-FileHash `
            -LiteralPath $commitBackups[0].FullName `
            -Algorithm SHA256).Hash -cne $externalHash) {
        throw (
            'Commit-time provenance replacement was not preserved. ' +
            "Rejected=$raceRejected; injected=$($script:raceInjected); " +
            "SBOM=$raceOutputHash; provenance=$raceProvenanceHash; " +
            "commitBackups=$($commitBackups.Count)")
    }

    $between = New-SyftTransactionCase -Name 'between-commits'
    $script:betweenExternalProvenance = Join-Path $between.ArtifactRoot (
        'between-commits-provenance.tmp')
    $script:betweenExternalDisplaced = Join-Path $between.ArtifactRoot (
        'between-commits-provenance.displaced')
    [IO.File]::WriteAllText(
        $script:betweenExternalProvenance,
        "between_commits_external=true`n",
        (New-Object Text.UTF8Encoding($false)))
    $script:betweenOutputPath =
        [IO.Path]::GetFullPath($between.Output)
    $script:betweenProvenancePath =
        [IO.Path]::GetFullPath($between.Provenance)
    $script:betweenMutationBlocked = $false
    $script:betweenMutationSucceeded = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if ($Operation -cne 'before-publish-lease' -or
            -not [IO.Path]::GetFullPath($Path).Equals(
                $script:betweenOutputPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        try {
            [IO.File]::Replace(
                $script:betweenExternalProvenance,
                $script:betweenProvenancePath,
                $script:betweenExternalDisplaced,
                $true)
            $script:betweenMutationSucceeded = $true
            throw (
                'Injected between-commits provenance replacement ' +
                'unexpectedly succeeded.')
        }
        catch {
            if (-not $script:betweenMutationSucceeded) {
                $script:betweenMutationBlocked = $true
                throw (
                    'Injected between-commits provenance replacement was ' +
                    'blocked by the retained publication guard.')
            }
            throw
        }
    }
    $betweenRejected = $false
    try {
        Invoke-SyftTransactionFixture -Case $between
    }
    catch {
        $betweenRejected = $true
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $betweenRejected -or
        -not $script:betweenMutationBlocked -or
        $script:betweenMutationSucceeded) {
        throw (
            'Published provenance was not guarded through the SBOM commit. ' +
            "Rejected=$betweenRejected; " +
            "blocked=$($script:betweenMutationBlocked); " +
            "succeeded=$($script:betweenMutationSucceeded)")
    }
    Assert-SyftSeededHashesUnchanged `
        -Case $between `
        -Name 'between-commits'

    $recovery = New-SyftTransactionCase -Name 'rollback-failure-recovery'
    $script:recoveryOutputPath =
        [IO.Path]::GetFullPath($recovery.Output)
    $script:recoveryProvenancePath =
        [IO.Path]::GetFullPath($recovery.Provenance)
    $script:recoveryOutputFailed = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($Operation, $Path)
        if ($Operation -cne 'publish') {
            return
        }
        $fullPath = [IO.Path]::GetFullPath($Path)
        if ($fullPath.Equals(
                $script:recoveryOutputPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            $script:recoveryOutputFailed = $true
            throw 'Injected recovery-case SBOM publication failure.'
        }
        if ($script:recoveryOutputFailed -and
            $fullPath.Equals(
                $script:recoveryProvenancePath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Injected provenance rollback failure.'
        }
    }
    $recoveryFailure = $null
    try {
        Invoke-SyftTransactionFixture -Case $recovery
    }
    catch {
        $recoveryFailure = $_
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if ($null -eq $recoveryFailure -or
        -not (Test-DesktopPetSyftTransactionRequiresRecovery `
            -Exception $recoveryFailure.Exception)) {
        throw 'Rollback failure did not request recovery-staging retention.'
    }
    foreach ($stagingRoot in @(
            $recovery.OutputStaging,
            $recovery.ProvenanceStaging)) {
        if (-not (Test-Path `
                -LiteralPath (Join-Path $stagingRoot 'RECOVERY_REQUIRED.txt') `
                -PathType Leaf)) {
            throw "Rollback failure did not retain a marker in $stagingRoot"
        }
    }
    $outputBackups = @(
        Get-ChildItem `
            -LiteralPath $recovery.OutputStaging `
            -File `
            -Filter '.DesktopPet.spdx.json.previous-*')
    if ($outputBackups.Count -ne 1 -or
        (Get-FileHash `
            -LiteralPath $outputBackups[0].FullName `
            -Algorithm SHA256).Hash -cne $recovery.OutputHash) {
        throw 'Rollback failure did not retain the exact SBOM backup.'
    }
    $provenanceBackups = @(
        Get-ChildItem `
            -LiteralPath $recovery.ProvenanceStaging `
            -File `
            -Filter '.BUILD-PROVENANCE.txt.previous-*')
    if ($provenanceBackups.Count -ne 1 -or
        (Get-FileHash `
            -LiteralPath $provenanceBackups[0].FullName `
            -Algorithm SHA256).Hash -cne $recovery.ProvenanceHash) {
        throw 'Rollback failure did not retain the exact provenance backup.'
    }
    $provenanceCommitBackups = @(
        Get-ChildItem `
            -LiteralPath $recovery.ProvenanceStaging `
            -File `
            -Filter '.BUILD-PROVENANCE.txt.commit-previous-*')
    if ($provenanceCommitBackups.Count -ne 1 -or
        (Get-FileHash `
            -LiteralPath $provenanceCommitBackups[0].FullName `
            -Algorithm SHA256).Hash -cne $recovery.ProvenanceHash) {
        throw (
            'Rollback failure did not retain the exact commit-time ' +
            'provenance backup.')
    }

    # Prove that the retained exact backup is independently usable, then leave
    # the temp-only fixture in its seeded state before normal cleanup.
    $manualRollback = Join-Path $recovery.ProvenanceStaging (
        'manual-provenance-rollback.tmp')
    [void](Copy-DesktopPetSyftTransactionFile `
        -SourcePath $provenanceBackups[0].FullName `
        -SourceRoot $recovery.ProvenanceStaging `
        -DestinationPath $manualRollback `
        -TrustedRoot $recovery.ArtifactRoot)
    [void](Publish-DesktopPetAtomicFile `
        -TemporaryPath $manualRollback `
        -DestinationPath $recovery.Provenance `
        -TrustedRoot $recovery.ArtifactRoot `
        -ProtectedPaths @($provenanceBackups[0].FullName))
    Assert-SyftSeededHashesUnchanged `
        -Case $recovery `
        -Name 'rollback-failure-manual-recovery'

    Write-Host (
        'PASS: Syft output transaction preflights provenance; binds the exact ' +
        'SBOM hash across a canonical newline; preserves borrowed sealed ' +
        'leases; contains predicate cleanup failures; journals before either ' +
        'commit and fails closed after simulated interruption at every ' +
        'boundary; preserves raced-in absent-state competitors; guards both ' +
        'commits with CAS rollback; and retains exact recovery evidence when ' +
        'rollback is injected to fail.')
}
finally {
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $scratch) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
