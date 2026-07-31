#requires -Version 5

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($requiredCommand in @(
        'Assert-DesktopPetOutputFileSafe',
        'Copy-DesktopPetValidatedInputFile',
        'Get-DesktopPetFinalPath',
        'Open-DesktopPetSealedStagedFile',
        'Open-DesktopPetValidatedInputFile',
        'Open-DesktopPetValidatedMutableFile',
        'Publish-DesktopPetAtomicFile',
        'Remove-DesktopPetTreeNode')) {
    if ($null -eq (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
        throw (
            "Syft output transaction requires the staging path-safety " +
            "command '$requiredCommand'.")
    }
}

function Close-DesktopPetSyftResources {
    [CmdletBinding()]
    param(
        [object[]]$Resources = @(),
        [object]$PrimaryError,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $cleanupErrors =
        New-Object 'Collections.Generic.List[Exception]'
    foreach ($resource in $Resources) {
        if ($null -eq $resource) {
            continue
        }
        try {
            $resource.Dispose()
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($cleanupErrors.Count -eq 0) {
        return
    }
    if ($null -eq $PrimaryError) {
        throw $cleanupErrors[0]
    }
    Write-Warning (
        "$Context cleanup also failed; preserving the primary error. " +
        "Cleanup error: $($cleanupErrors[0].Message)")
}

function Assert-DesktopPetSyftProvenancePreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Syft provenance file does not exist: $Path"
    }
    $resolved = Assert-DesktopPetOutputFileSafe `
        -Path $Path `
        -TrustedRoot $Root `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories
    $input = Open-DesktopPetValidatedInputFile `
        -Path $resolved `
        -Root $Root
    $preflightPrimaryError = $null
    try {
        if ($input.Length -gt 16MB) {
            throw "Syft provenance file exceeds 16 MiB: $resolved"
        }
        # Prove the complete existing document can be consumed before any
        # runtime hashing, download, or scan work begins.
        [void]$input.ReadAllTextUtf8(16MB)
    }
    catch {
        $preflightPrimaryError = $_
        throw
    }
    finally {
        Close-DesktopPetSyftResources `
            -Resources @($input) `
            -PrimaryError $preflightPrimaryError `
            -Context 'Syft provenance preflight'
    }
    return $resolved
}

function New-DesktopPetSyftBoundProvenanceDocument {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory = $true)][string]$ExistingContent,
        [Parameter(Mandatory = $true)][string[]]$EvidenceLines,
        [Parameter(Mandatory = $true)][string]$StagedOutputSha256
    )

    if ($StagedOutputSha256 -notmatch '\A[0-9A-Fa-f]{64}\z') {
        throw 'Syft provenance SBOM binding SHA-256 is invalid.'
    }
    foreach ($line in $EvidenceLines) {
        if ($null -eq $line -or
            $line -match '(\r|\n)' -or
            $line.StartsWith(
                'syft_sbom_spdx_sha256=',
                [StringComparison]::Ordinal)) {
            throw 'Syft provenance evidence contains an unsafe binding line.'
        }
    }

    $bindingLine =
        'syft_sbom_spdx_sha256=' +
        $StagedOutputSha256.ToUpperInvariant()
    $allEvidenceLines = @($EvidenceLines + $bindingLine)
    $evidence =
        ($allEvidenceLines -join [Environment]::NewLine) +
        [Environment]::NewLine
    $boundary = ''
    if ($ExistingContent.Length -gt 0 -and
        $ExistingContent -notmatch '(\r\n|\n|\r)\z') {
        $boundary = [Environment]::NewLine
    }
    return [pscustomobject]@{
        Content = $ExistingContent + $boundary + $evidence
        Evidence = $evidence
        BindingLine = $bindingLine
    }
}

function Copy-DesktopPetSyftTransactionFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$TrustedRoot,
        [object]$SealedSourceFile,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $destinationFull = Assert-DesktopPetOutputFileSafe `
        -Path $DestinationPath `
        -TrustedRoot $TrustedRoot `
        -ProtectedPaths @($ProtectedPaths + $SourcePath) `
        -ProtectedDirectories $ProtectedDirectories
    if (Test-Path -LiteralPath $destinationFull) {
        throw (
            "Syft transaction copy destination already exists: " +
            $destinationFull)
    }

    $input = $null
    $ownsInput = $false
    $destinationLease = $null
    $copyPrimaryError = $null
    try {
        if ($null -eq $SealedSourceFile) {
            $input = Open-DesktopPetValidatedInputFile `
                -Path $SourcePath `
                -Root $SourceRoot
            $ownsInput = $true
        }
        else {
            if (-not ($SealedSourceFile -is
                    [DesktopPet.Packaging.FinalPathResolver+SealedStagedFileLease])) {
                throw (
                    'SealedSourceFile must be a retained sealed staged-file ' +
                    'lease.')
            }
            $sourceFull = [IO.Path]::GetFullPath($SourcePath)
            if (-not $SealedSourceFile.OriginalPath.Equals(
                    $sourceFull,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Syft transaction copy received a sealed source for a ' +
                    "different path: $($SealedSourceFile.OriginalPath)")
            }
            $SealedSourceFile.Revalidate()
            $input = $SealedSourceFile
        }
        $destinationLease =
            [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
                (Split-Path -Parent $destinationFull),
                $TrustedRoot)
        $input.CopyToFile($destinationFull)
    }
    catch {
        $copyPrimaryError = $_
        throw
    }
    finally {
        Close-DesktopPetSyftResources `
            -Resources @(
                $destinationLease,
                $(if ($ownsInput) { $input })) `
            -PrimaryError $copyPrimaryError `
            -Context 'Syft transaction copy'
    }
    return $destinationFull
}

function New-DesktopPetSyftFileSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $resolved = Assert-DesktopPetOutputFileSafe `
        -Path $Path `
        -TrustedRoot $Root `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories
    $snapshot = [ordered]@{
        Path = $resolved
        Root = [IO.Path]::GetFullPath($Root)
        Existed = $false
        BackupPath = $null
        SealedBackupFile = $null
        Length = [long]0
        Sha256 = $null
    }
    if (-not (Test-Path -LiteralPath $resolved)) {
        return [pscustomobject]$snapshot
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Syft transaction destination is not a regular file: $resolved"
    }

    $backupFull = Assert-DesktopPetOutputFileSafe `
        -Path $BackupPath `
        -TrustedRoot $Root `
        -ProtectedPaths @($ProtectedPaths + $resolved) `
        -ProtectedDirectories $ProtectedDirectories
    if (Test-Path -LiteralPath $backupFull) {
        throw "Syft transaction backup already exists: $backupFull"
    }

    $input = $null
    $destinationLease = $null
    $backupMutableFile = $null
    $backupSealedFile = $null
    $snapshotPrimaryError = $null
    try {
        $input = Open-DesktopPetValidatedInputFile `
            -Path $resolved `
            -Root $Root
        $destinationLease =
            [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
                (Split-Path -Parent $backupFull),
                $Root)
        $input.CopyToFile($backupFull)

        $snapshot.Existed = $true
        $snapshot.BackupPath = $backupFull
        $snapshot.Length = [long]$input.Length
        $snapshot.Sha256 = $input.ComputeHash('SHA256')

        # Seal and retain the recovery copy itself before any commit. Its hash
        # is read through that exact handle, so rollback never trusts a later
        # pathname reopen of the backup.
        $backupMutableFile = Open-DesktopPetValidatedMutableFile `
            -Path $backupFull `
            -Root (Split-Path -Parent $backupFull)
        $backupSealedFile = $backupMutableFile.Seal()
        $backupMutableFile = $null
        if ($backupSealedFile.ComputeHash('SHA256') -cne
            [string]$snapshot.Sha256) {
            throw "Syft transaction backup verification failed: $backupFull"
        }
        $snapshot.SealedBackupFile = $backupSealedFile
        $backupSealedFile = $null
    }
    catch {
        $snapshotPrimaryError = $_
        throw
    }
    finally {
        Close-DesktopPetSyftResources `
            -Resources @(
                $backupSealedFile,
                $backupMutableFile,
                $destinationLease,
                $input) `
            -PrimaryError $snapshotPrimaryError `
            -Context 'Syft transaction snapshot'
    }
    return [pscustomobject]$snapshot
}

function Test-DesktopPetSyftSnapshotMatches {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Snapshot)

    if (-not [bool]$Snapshot.Existed) {
        return -not (Test-Path -LiteralPath ([string]$Snapshot.Path))
    }
    if (-not (Test-Path `
            -LiteralPath ([string]$Snapshot.Path) `
            -PathType Leaf)) {
        return $false
    }

    $input = $null
    $matches = $false
    $cleanupErrors =
        New-Object 'Collections.Generic.List[Exception]'
    try {
        $input = Open-DesktopPetValidatedInputFile `
            -Path ([string]$Snapshot.Path) `
            -Root ([string]$Snapshot.Root)
        $matches = (
            [long]$input.Length -eq [long]$Snapshot.Length -and
            $input.ComputeHash('SHA256') -ceq [string]$Snapshot.Sha256)
    }
    catch {
        $matches = $false
    }
    finally {
        if ($null -ne $input) {
            try {
                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'syft-snapshot-match-before-dispose' `
                    -Path ([string]$Snapshot.Path)
            }
            catch {
                $cleanupErrors.Add($_.Exception)
                $matches = $false
            }
            try {
                $input.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
                $matches = $false
            }
        }
        if ($cleanupErrors.Count -gt 0) {
            Write-Warning (
                'Syft snapshot predicate cleanup failed; conservatively ' +
                'reporting a mismatch. Cleanup error: ' +
                $cleanupErrors[0].Message)
        }
    }
    return $matches
}

function Restore-DesktopPetSyftFileSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][string]$RecoveryStagingRoot,
        [string]$ExpectedCurrentSha256,
        [object]$SealedCurrentFile,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    if (Test-DesktopPetSyftSnapshotMatches -Snapshot $Snapshot) {
        return
    }

    if ([bool]$Snapshot.Existed) {
        if (-not (Test-Path `
                -LiteralPath ([string]$Snapshot.BackupPath) `
                -PathType Leaf)) {
            throw (
                "Syft transaction recovery backup is missing: " +
                [string]$Snapshot.BackupPath)
        }
        $rollbackPath = Join-Path $RecoveryStagingRoot (
            [IO.Path]::GetFileName([string]$Snapshot.Path) +
            '.rollback-' + [Guid]::NewGuid().ToString('N'))
        $rollbackMutableFile = $null
        $rollbackSealedFile = $null
        $rollbackPrimaryError = $null
        try {
            [void](Copy-DesktopPetSyftTransactionFile `
                -SourcePath ([string]$Snapshot.BackupPath) `
                -SourceRoot $RecoveryStagingRoot `
                -DestinationPath $rollbackPath `
                -TrustedRoot ([string]$Snapshot.Root) `
                -SealedSourceFile $Snapshot.SealedBackupFile `
                -ProtectedPaths $ProtectedPaths `
                -ProtectedDirectories $ProtectedDirectories)
            $rollbackMutableFile = Open-DesktopPetValidatedMutableFile `
                -Path $rollbackPath `
                -Root $RecoveryStagingRoot
            $rollbackSealedFile = $rollbackMutableFile.Seal()
            $rollbackMutableFile = $null
            $rollbackSha256 =
                $rollbackSealedFile.ComputeHash('SHA256')
            if ($rollbackSha256 -cne [string]$Snapshot.Sha256) {
                throw (
                    'Syft rollback copy differs from its retained exact ' +
                    "backup: $rollbackPath")
            }

            $publishParameters = @{
                TemporaryPath = $rollbackPath
                DestinationPath = [string]$Snapshot.Path
                TrustedRoot = [string]$Snapshot.Root
                ProtectedPaths = @(
                    $ProtectedPaths + [string]$Snapshot.BackupPath)
                ProtectedDirectories = $ProtectedDirectories
                SealedTemporaryFile = $rollbackSealedFile
                ExpectedTemporarySha256 = $rollbackSha256
            }
            if (Test-Path -LiteralPath ([string]$Snapshot.Path) -PathType Leaf) {
                if ([string]::IsNullOrWhiteSpace($ExpectedCurrentSha256)) {
                    throw (
                        'Syft rollback requires the exact current destination ' +
                        'SHA-256 before replacing it.')
                }
                $publishParameters.ExpectedDestinationSha256 =
                    $ExpectedCurrentSha256
            }
            elseif (Test-Path -LiteralPath ([string]$Snapshot.Path)) {
                throw (
                    'Syft rollback target exists but is not a regular file: ' +
                    [string]$Snapshot.Path)
            }
            else {
                $publishParameters.DestinationMustBeAbsent = $true
            }
            [void](Publish-DesktopPetAtomicFile @publishParameters)
        }
        catch {
            $rollbackPrimaryError = $_
            throw
        }
        finally {
            $rollbackCleanupErrors =
                New-Object 'Collections.Generic.List[Exception]'
            if ($null -ne $rollbackSealedFile) {
                try {
                    $rollbackSealedFile.Dispose()
                }
                catch {
                    $rollbackCleanupErrors.Add($_.Exception)
                }
            }
            if ($null -ne $rollbackMutableFile) {
                try {
                    $rollbackMutableFile.Dispose()
                }
                catch {
                    $rollbackCleanupErrors.Add($_.Exception)
                }
            }
            if ($rollbackCleanupErrors.Count -gt 0) {
                if ($null -eq $rollbackPrimaryError) {
                    throw $rollbackCleanupErrors[0]
                }
                Write-Warning (
                    'Syft rollback retained-handle cleanup also failed; ' +
                    'preserving the primary error. Cleanup error: ' +
                    $rollbackCleanupErrors[0].Message)
            }
        }
    }
    elseif (Test-Path -LiteralPath ([string]$Snapshot.Path)) {
        if ($null -eq $SealedCurrentFile -or
            -not ($SealedCurrentFile -is
                [DesktopPet.Packaging.FinalPathResolver+SealedStagedFileLease])) {
            throw (
                'Syft absent-state rollback found a destination but has no ' +
                'retained exact-object lease; refusing to delete a possible ' +
                "concurrent file: $($Snapshot.Path)")
        }
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-syft-absent-rollback-delete' `
            -Path ([string]$Snapshot.Path)
        $SealedCurrentFile.AssertRetainedPath(
            [string]$Snapshot.Path,
            'Published Syft SBOM absent-state rollback input')
        if ([string]::IsNullOrWhiteSpace($ExpectedCurrentSha256) -or
            $SealedCurrentFile.ComputeHash('SHA256') -cne
                $ExpectedCurrentSha256) {
            throw (
                'Syft absent-state rollback exact object does not match the ' +
                'published SHA-256; refusing rollback.')
        }
        $abortedPath = Join-Path $RecoveryStagingRoot (
            [IO.Path]::GetFileName([string]$Snapshot.Path) +
            '.aborted-publication-' + [Guid]::NewGuid().ToString('N'))
        $SealedCurrentFile.RenameRetained($abortedPath, $false)
    }

    if (-not (Test-DesktopPetSyftSnapshotMatches -Snapshot $Snapshot)) {
        throw (
            "Syft transaction rollback did not restore exact prior state: " +
            [string]$Snapshot.Path)
    }
}

function Write-DesktopPetSyftRecoveryMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$StagingRoots,
        [Parameter(Mandatory = $true)][string]$OriginalError,
        [Parameter(Mandatory = $true)][string[]]$RollbackErrors
    )

    $text = @(
        'DesktopPet Syft output transaction recovery is required.'
        "original_error=$OriginalError"
        'rollback_errors_begin'
        $RollbackErrors
        'rollback_errors_end'
        'Exact prior-byte backups in this staging directory must be retained.'
        ''
    ) -join [Environment]::NewLine
    foreach ($root in @($StagingRoots | Select-Object -Unique)) {
        try {
            [IO.File]::WriteAllText(
                (Join-Path $root 'RECOVERY_REQUIRED.txt'),
                $text,
                (New-Object Text.UTF8Encoding($false)))
        }
        catch {
            # The exception metadata below remains authoritative if even the
            # human-readable recovery marker cannot be written.
        }
    }
}

function Test-DesktopPetSyftTransactionRequiresRecovery {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current.Data.Contains('DesktopPetRetainRecoveryStaging') -and
            [bool]$current.Data['DesktopPetRetainRecoveryStaging']) {
            return $true
        }
        $current = $current.InnerException
    }
    return $false
}

function Get-DesktopPetSyftTransactionJournalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    $journalName =
        '.' + [IO.Path]::GetFileName($outputFull) +
        '.syft-output-transaction.json'
    return Assert-DesktopPetOutputFileSafe `
        -Path (Join-Path (Split-Path -Parent $outputFull) $journalName) `
        -TrustedRoot $OutputRoot `
        -ProtectedPaths @($outputFull)
}

function New-DesktopPetSyftTransactionJournal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Journal,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $resolved = Assert-DesktopPetOutputFileSafe `
        -Path $Path `
        -TrustedRoot $Root `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories
    if (Test-Path -LiteralPath $resolved) {
        throw "Syft transaction journal already exists: $resolved"
    }

    $journalText =
        ($Journal | ConvertTo-Json -Depth 8) + [Environment]::NewLine
    $journalBytes =
        (New-Object Text.UTF8Encoding($false)).GetBytes($journalText)
    $directoryLease = $null
    $stream = $null
    $mutableFile = $null
    $sealedFile = $null
    $journalPrimaryError = $null
    try {
        $directoryLease =
            [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
                (Split-Path -Parent $resolved),
                $Root)
        $stream = New-Object IO.FileStream(
            $resolved,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        $stream.Write($journalBytes, 0, $journalBytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null

        $mutableFile = Open-DesktopPetValidatedMutableFile `
            -Path $resolved `
            -Root (Split-Path -Parent $resolved)
        $sealedFile = $mutableFile.Seal()
        $mutableFile = $null
        # Upgrade the retained sealed identity to a DELETE-capable handle that
        # shares read only. This exact-object handle is both the durable journal
        # and the live non-write/non-delete-share transaction exclusion lease.
        $sealedFile.AcquireExclusivePublicationControl()
        $sealedFile.AssertRetainedPath(
            $resolved,
            'Live Syft output transaction journal')
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $expectedHash =
                ([BitConverter]::ToString(
                    $hasher.ComputeHash($journalBytes))).Replace('-', '')
        }
        finally {
            $hasher.Dispose()
        }
        if ($sealedFile.ComputeHash('SHA256') -cne $expectedHash) {
            throw "Syft transaction journal verification failed: $resolved"
        }
        $result = [pscustomobject]@{
            Path = $resolved
            SealedFile = $sealedFile
            Sha256 = $expectedHash
        }
        $sealedFile = $null
        return $result
    }
    catch {
        $journalPrimaryError = $_
        if (Test-Path -LiteralPath $resolved) {
            $failure = New-Object InvalidOperationException(
                (
                    'Syft transaction journal creation was interrupted after ' +
                    'the durable path appeared. Publication is blocked for ' +
                    "recovery: $resolved. Original failure: " +
                    $_.Exception.Message),
                $_.Exception)
            $failure.Data['DesktopPetRetainRecoveryStaging'] = $true
            $failure.Data['DesktopPetSyftTransactionJournal'] = $resolved
            $journalPrimaryError = $failure
            throw $failure
        }
        throw
    }
    finally {
        Close-DesktopPetSyftResources `
            -Resources @(
                $sealedFile,
                $mutableFile,
                $stream,
                $directoryLease) `
            -PrimaryError $journalPrimaryError `
            -Context 'Syft transaction journal creation'
    }
}

function Remove-DesktopPetSyftTransactionJournal {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$JournalLease)

    $JournalLease.SealedFile.AssertRetainedPath(
        [string]$JournalLease.Path,
        'Syft output transaction journal')
    if ($JournalLease.SealedFile.ComputeHash('SHA256') -cne
        [string]$JournalLease.Sha256) {
        throw 'Syft output transaction journal changed before cleanup.'
    }
    $JournalLease.SealedFile.DeleteRetained()
}

function Throw-DesktopPetSyftExistingJournal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $journalInput = $null
    $journal = $null
    try {
        $journalInput = Open-DesktopPetValidatedInputFile `
            -Path $JournalPath `
            -Root $OutputRoot
        $journal =
            $journalInput.ReadAllTextUtf8(1MB) | ConvertFrom-Json
    }
    catch {
        $journal = $null
    }
    finally {
        if ($null -ne $journalInput) {
            try {
                $journalInput.Dispose()
            }
            catch {
                # The fail-closed recovery exception below remains primary.
            }
        }
    }

    $recoveryRoots = @()
    if ($null -ne $journal) {
        foreach ($candidate in @(
                $journal.outputStagingRoot,
                $journal.provenanceStagingRoot)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$candidate)) {
                $recoveryRoots += [string]$candidate
            }
        }
    }
    $message = (
        'A prior Syft two-output transaction journal is still present. ' +
        'Publication is blocked fail-closed because an abrupt interruption may ' +
        'have occurred between commits. Inspect the journal and retained exact ' +
        "backups before recovery: $JournalPath")
    $failure = New-Object InvalidOperationException($message)
    $failure.Data['DesktopPetRetainRecoveryStaging'] = $true
    $failure.Data['DesktopPetRecoveryStaging'] =
        ($recoveryRoots | Select-Object -Unique) -join ';'
    $failure.Data['DesktopPetSyftTransactionJournal'] = $JournalPath
    throw $failure
}

function Publish-DesktopPetSyftOutputTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StagedOutputPath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [string]$StagedProvenancePath,
        [string]$ProvenancePath,
        [string]$ProvenanceRoot,
        [string]$ExpectedProvenanceSha256,
        [object]$SealedStagedOutputFile,
        [object]$ExpectedStagedOutputIdentity,
        [string]$ExpectedStagedOutputSha256,
        [object]$SealedStagedProvenanceFile,
        [object]$ExpectedStagedProvenanceIdentity,
        [string]$ExpectedStagedProvenanceSha256,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $hasProvenance =
        -not [string]::IsNullOrWhiteSpace($ProvenancePath)
    if ($hasProvenance -ne
        (-not [string]::IsNullOrWhiteSpace($StagedProvenancePath)) -or
        $hasProvenance -ne
        (-not [string]::IsNullOrWhiteSpace($ProvenanceRoot))) {
        throw (
            'Syft transaction requires staged path, destination path, and ' +
            'root together for provenance.')
    }
    if ($hasProvenance) {
        if ([string]::IsNullOrWhiteSpace($ExpectedProvenanceSha256) -or
            $ExpectedProvenanceSha256 -notmatch '\A[0-9A-Fa-f]{64}\z') {
            throw (
                'Syft transaction requires the exact pre-staging provenance ' +
                'SHA-256 when provenance publication is requested.')
        }
        $ExpectedProvenanceSha256 =
            $ExpectedProvenanceSha256.ToUpperInvariant()
    }
    elseif (-not [string]::IsNullOrWhiteSpace(
            $ExpectedProvenanceSha256)) {
        throw (
            'Syft transaction received an expected provenance hash without ' +
            'provenance publication paths.')
    }

    $ownsSealedStagedOutputFile = $false
    $ownsSealedStagedProvenanceFile = $false
    $outputSnapshot = $null
    $provenanceSnapshot = $null
    $provenanceInitialSnapshot = $null
    $journalLease = $null
    $provenancePublicationLease = $null
    $transactionPrimaryError = $null

    try {
    $outputCrossProtected = @($ProtectedPaths)
    if ($hasProvenance) {
        $outputCrossProtected += $ProvenancePath
    }
    $resolvedOutput = Assert-DesktopPetOutputFileSafe `
        -Path $OutputPath `
        -TrustedRoot $OutputRoot `
        -ProtectedPaths $outputCrossProtected `
        -ProtectedDirectories $ProtectedDirectories
    $journalPath = Get-DesktopPetSyftTransactionJournalPath `
        -OutputPath $resolvedOutput `
        -OutputRoot $OutputRoot
    if (Test-Path -LiteralPath $journalPath) {
        Throw-DesktopPetSyftExistingJournal `
            -JournalPath $journalPath `
            -OutputPath $resolvedOutput `
            -OutputRoot $OutputRoot
    }
    $resolvedStagedOutput = Assert-DesktopPetOutputFileSafe `
        -Path $StagedOutputPath `
        -TrustedRoot $OutputRoot `
        -ProtectedPaths @($outputCrossProtected + $resolvedOutput) `
        -ProtectedDirectories $ProtectedDirectories
    if (-not (Test-Path -LiteralPath $resolvedStagedOutput -PathType Leaf)) {
        throw "Staged Syft SBOM is missing: $resolvedStagedOutput"
    }

    $resolvedProvenance = $null
    $resolvedStagedProvenance = $null
    if ($hasProvenance) {
        $resolvedProvenance = Assert-DesktopPetOutputFileSafe `
            -Path $ProvenancePath `
            -TrustedRoot $ProvenanceRoot `
            -ProtectedPaths @($ProtectedPaths + $resolvedOutput) `
            -ProtectedDirectories $ProtectedDirectories
        if (-not (Test-Path `
                -LiteralPath $resolvedProvenance `
                -PathType Leaf)) {
            throw (
                'Canonical Syft provenance is missing before transaction: ' +
                $resolvedProvenance)
        }
        $resolvedStagedProvenance = Assert-DesktopPetOutputFileSafe `
            -Path $StagedProvenancePath `
            -TrustedRoot $ProvenanceRoot `
            -ProtectedPaths @(
                $ProtectedPaths +
                $resolvedOutput +
                $resolvedProvenance) `
            -ProtectedDirectories $ProtectedDirectories
        if (-not (Test-Path `
                -LiteralPath $resolvedStagedProvenance `
                -PathType Leaf)) {
            throw (
                "Staged Syft provenance is missing: " +
            $resolvedStagedProvenance)
        }
    }

    if ($null -eq $SealedStagedOutputFile) {
        $SealedStagedOutputFile = Open-DesktopPetSealedStagedFile `
            -Path $resolvedStagedOutput `
            -Root (Split-Path -Parent $resolvedStagedOutput)
        $ownsSealedStagedOutputFile = $true
    }
    elseif (-not ($SealedStagedOutputFile -is
            [DesktopPet.Packaging.FinalPathResolver+SealedStagedFileLease])) {
        throw (
            'SealedStagedOutputFile must be returned by mutableLease.Seal() ' +
            'or Open-DesktopPetSealedStagedFile.')
    }
    if (-not $SealedStagedOutputFile.OriginalPath.Equals(
            $resolvedStagedOutput,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Syft transaction received a sealed SBOM for a different path: ' +
            $SealedStagedOutputFile.OriginalPath)
    }
    $SealedStagedOutputFile.Revalidate()
    if ($null -ne $ExpectedStagedOutputIdentity) {
        $SealedStagedOutputFile.AssertMatchesExpectedIdentity(
            $ExpectedStagedOutputIdentity,
            'Staged Syft SBOM')
    }
    $observedStagedOutputSha256 =
        $SealedStagedOutputFile.ComputeHash('SHA256')
    if ([string]::IsNullOrWhiteSpace($ExpectedStagedOutputSha256)) {
        $ExpectedStagedOutputSha256 = $observedStagedOutputSha256
    }
    elseif ($ExpectedStagedOutputSha256 -notmatch
            '\A[0-9A-Fa-f]{64}\z') {
        throw 'Staged Syft SBOM expected SHA-256 is invalid.'
    }
    else {
        $ExpectedStagedOutputSha256 =
            $ExpectedStagedOutputSha256.ToUpperInvariant()
        if ($observedStagedOutputSha256 -cne
                $ExpectedStagedOutputSha256) {
            throw 'Staged Syft SBOM content changed after validation.'
        }
    }

    if ($hasProvenance) {
        if ($null -eq $SealedStagedProvenanceFile) {
            $SealedStagedProvenanceFile =
                Open-DesktopPetSealedStagedFile `
                    -Path $resolvedStagedProvenance `
                    -Root (Split-Path -Parent $resolvedStagedProvenance)
            $ownsSealedStagedProvenanceFile = $true
        }
        elseif (-not ($SealedStagedProvenanceFile -is
                [DesktopPet.Packaging.FinalPathResolver+SealedStagedFileLease])) {
            throw (
                'SealedStagedProvenanceFile must be returned by ' +
                'mutableLease.Seal() or Open-DesktopPetSealedStagedFile.')
        }
        if (-not $SealedStagedProvenanceFile.OriginalPath.Equals(
                $resolvedStagedProvenance,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Syft transaction received sealed provenance for a different ' +
                "path: $($SealedStagedProvenanceFile.OriginalPath)")
        }
        $SealedStagedProvenanceFile.Revalidate()
        if ($null -ne $ExpectedStagedProvenanceIdentity) {
            $SealedStagedProvenanceFile.AssertMatchesExpectedIdentity(
                $ExpectedStagedProvenanceIdentity,
                'Staged Syft provenance')
        }
        $observedStagedProvenanceSha256 =
            $SealedStagedProvenanceFile.ComputeHash('SHA256')
        if ([string]::IsNullOrWhiteSpace(
                $ExpectedStagedProvenanceSha256)) {
            $ExpectedStagedProvenanceSha256 =
                $observedStagedProvenanceSha256
        }
        elseif ($ExpectedStagedProvenanceSha256 -notmatch
                '\A[0-9A-Fa-f]{64}\z') {
            throw 'Staged Syft provenance expected SHA-256 is invalid.'
        }
        else {
            $ExpectedStagedProvenanceSha256 =
                $ExpectedStagedProvenanceSha256.ToUpperInvariant()
            if ($observedStagedProvenanceSha256 -cne
                    $ExpectedStagedProvenanceSha256) {
                throw (
                    'Staged Syft provenance content changed after validation.')
            }
        }
    }

    $outputStagingRoot = Split-Path -Parent $resolvedStagedOutput
    $outputBackup = Join-Path $outputStagingRoot (
        '.' + [IO.Path]::GetFileName($resolvedOutput) + '.previous-' +
        [Guid]::NewGuid().ToString('N'))
    $provenanceStagingRoot = $null
    $provenanceBackup = $null
    if ($hasProvenance) {
        $provenanceStagingRoot =
            Split-Path -Parent $resolvedStagedProvenance
        $provenanceBackup = Join-Path $provenanceStagingRoot (
            '.' + [IO.Path]::GetFileName($resolvedProvenance) + '.previous-' +
            [Guid]::NewGuid().ToString('N'))
    }

    # Preserve exact byte snapshots of every destination before committing
    # either output. These originals remain in staging even during rollback.
    $outputSnapshot = New-DesktopPetSyftFileSnapshot `
        -Path $resolvedOutput `
        -Root $OutputRoot `
        -BackupPath $outputBackup `
        -ProtectedPaths $outputCrossProtected `
        -ProtectedDirectories $ProtectedDirectories
    if ($hasProvenance) {
        $provenanceSnapshot = New-DesktopPetSyftFileSnapshot `
            -Path $resolvedProvenance `
            -Root $ProvenanceRoot `
            -BackupPath $provenanceBackup `
            -ProtectedPaths @($ProtectedPaths + $resolvedOutput) `
            -ProtectedDirectories $ProtectedDirectories
        $provenanceInitialSnapshot = $provenanceSnapshot
        if (-not [string]::IsNullOrWhiteSpace(
                $ExpectedProvenanceSha256) -and
            [string]$provenanceSnapshot.Sha256 -cne
                $ExpectedProvenanceSha256) {
            throw (
                'Syft provenance changed after its staged append was ' +
                "prepared: $resolvedProvenance")
        }
    }

    $commitBackup = $null
    if ($hasProvenance) {
        $commitBackup = Join-Path $provenanceStagingRoot (
            '.' + [IO.Path]::GetFileName($resolvedProvenance) +
            '.commit-previous-' + [Guid]::NewGuid().ToString('N'))
    }
    $journalData = [ordered]@{
        schemaVersion = 1
        state = 'prepared-before-first-commit'
        createdUtc = [DateTime]::UtcNow.ToString('o')
        outputPath = $resolvedOutput
        outputRoot = [IO.Path]::GetFullPath($OutputRoot)
        outputStagingRoot = $outputStagingRoot
        outputStagedPath = $resolvedStagedOutput
        outputExisted = [bool]$outputSnapshot.Existed
        outputOriginalSha256 = [string]$outputSnapshot.Sha256
        outputNewSha256 = $ExpectedStagedOutputSha256
        outputBackupPath = [string]$outputSnapshot.BackupPath
        provenancePath = $resolvedProvenance
        provenanceRoot = $(
            if ($hasProvenance) {
                [IO.Path]::GetFullPath($ProvenanceRoot)
            }
        )
        provenanceStagingRoot = $provenanceStagingRoot
        provenanceStagedPath = $resolvedStagedProvenance
        provenanceOriginalSha256 = $(
            if ($null -ne $provenanceInitialSnapshot) {
                [string]$provenanceInitialSnapshot.Sha256
            }
        )
        provenanceNewSha256 = $ExpectedStagedProvenanceSha256
        provenanceBackupPath = $(
            if ($null -ne $provenanceInitialSnapshot) {
                [string]$provenanceInitialSnapshot.BackupPath
            }
        )
        provenanceCommitBackupPath = $commitBackup
    }
    $journalProtectedPaths = @(
        @(
            $ProtectedPaths +
            $resolvedOutput +
            $resolvedStagedOutput +
            $resolvedProvenance +
            $resolvedStagedProvenance) |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_)
            })
    $journalLease = New-DesktopPetSyftTransactionJournal `
        -Path $journalPath `
        -Root $OutputRoot `
        -Journal $journalData `
        -ProtectedPaths $journalProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories

    $provenanceAttempted = $false
    $publishedProvenanceGuard = $null
    $outputAttempted = $false
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'syft-transaction-journal-prepared' `
            -Path $journalPath
        # Provenance commits first. Therefore any provenance publication
        # failure occurs before the canonical SBOM is touched.
        if ($hasProvenance) {
            $provenancePublicationProtected = @(
                $ProtectedPaths + $resolvedOutput)
            foreach ($backupPath in @(
                    $outputSnapshot.BackupPath,
                    $provenanceInitialSnapshot.BackupPath)) {
                if (-not [string]::IsNullOrWhiteSpace(
                        [string]$backupPath)) {
                    $provenancePublicationProtected += $backupPath
                }
            }

            # Capture and retain the exact destination present immediately
            # before the CAS publication. This keeps the historical recovery
            # artifact while the shared publisher binds the replacement to the
            # sealed staged object and rejects a later destination race. The
            # shared publisher owns the retained-handle destination recovery
            # and provenanceStateUncertain boundary.
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation 'before-publish-lease' `
                -Path $resolvedProvenance
            $commitSnapshot = New-DesktopPetSyftFileSnapshot `
                -Path $resolvedProvenance `
                -Root $ProvenanceRoot `
                -BackupPath $commitBackup `
                -ProtectedPaths @(
                    $provenancePublicationProtected +
                    $resolvedStagedProvenance) `
                -ProtectedDirectories $ProtectedDirectories
            $provenanceSnapshot = $commitSnapshot
            if (-not [bool]$provenanceSnapshot.Existed -or
                [string]$provenanceSnapshot.Sha256 -cne
                    $ExpectedProvenanceSha256) {
                throw (
                    'Syft provenance changed after its staged append was ' +
                    "prepared: $resolvedProvenance")
            }

            # Retain the legacy hook immediately before the publication call;
            # the shared publisher invokes it again at its own linearization
            # boundary.
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation 'publish' `
                -Path $resolvedProvenance
            $provenancePublicationLease =
                Open-DesktopPetSealedStagedFile `
                    -Path $resolvedStagedProvenance `
                    -Root $provenanceStagingRoot
            $provenancePublicationLease.AssertSameFile(
                $SealedStagedProvenanceFile,
                'Syft provenance publication lease')
            $provenanceAttempted = $true
            [void](Publish-DesktopPetAtomicFile `
                -TemporaryPath $resolvedStagedProvenance `
                -DestinationPath $resolvedProvenance `
                -TrustedRoot $ProvenanceRoot `
                -ProtectedPaths $provenancePublicationProtected `
                -ProtectedDirectories $ProtectedDirectories `
                -SealedTemporaryFile $provenancePublicationLease `
                -ExpectedTemporarySha256 `
                    $ExpectedStagedProvenanceSha256 `
                -ExpectedDestinationSha256 $ExpectedProvenanceSha256)
            # Publication leaves a DELETE-capable control handle cached on the
            # sealed lease. Release it before acquiring the no-delete-sharing
            # read guard that spans the second commit. The destination is
            # immediately re-hashed through that retained guard.
            $provenancePublicationLease.Dispose()
            $provenancePublicationLease = $null
            $publishedProvenanceGuard =
                Open-DesktopPetValidatedInputFile `
                    -Path $resolvedProvenance `
                    -Root $ProvenanceRoot
            if ($publishedProvenanceGuard.ComputeHash('SHA256') -cne
                    $ExpectedStagedProvenanceSha256) {
                throw (
                    'Syft provenance commit did not retain the validated ' +
                    'sealed staged bytes.')
            }
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation 'syft-transaction-after-first-commit' `
                -Path $resolvedProvenance
        }

        $outputAttempted = $true
        $outputPublicationProtected = @($outputCrossProtected)
        $publicationBackupPaths = @($outputSnapshot.BackupPath)
        if ($null -ne $provenanceInitialSnapshot) {
            $publicationBackupPaths +=
                $provenanceInitialSnapshot.BackupPath
        }
        if ($null -ne $provenanceSnapshot) {
            $publicationBackupPaths += $provenanceSnapshot.BackupPath
        }
        foreach ($backupPath in $publicationBackupPaths) {
            if (-not [string]::IsNullOrWhiteSpace([string]$backupPath)) {
                $outputPublicationProtected += $backupPath
            }
        }
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $resolvedStagedOutput `
            -DestinationPath $resolvedOutput `
            -TrustedRoot $OutputRoot `
            -ProtectedPaths $outputPublicationProtected `
            -ProtectedDirectories $ProtectedDirectories `
            -SealedTemporaryFile $SealedStagedOutputFile `
            -ExpectedTemporarySha256 $ExpectedStagedOutputSha256 `
            -ExpectedDestinationSha256 $outputSnapshot.Sha256 `
            -DestinationMustBeAbsent:(-not [bool]$outputSnapshot.Existed))
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'syft-transaction-after-second-commit' `
            -Path $resolvedOutput
        Remove-DesktopPetSyftTransactionJournal -JournalLease $journalLease
    }
    catch {
        $commitFailure = $_
        $recoveryRoots = @($outputStagingRoot)
        if ($hasProvenance) {
            $recoveryRoots += $provenanceStagingRoot
        }
        if ($commitFailure.Exception.Data.Contains(
                'DesktopPetSimulatedAbruptTermination') -and
            [bool]$commitFailure.Exception.Data[
                'DesktopPetSimulatedAbruptTermination']) {
            $commitFailure.Exception.Data[
                'DesktopPetRetainRecoveryStaging'] = $true
            $commitFailure.Exception.Data['DesktopPetRecoveryStaging'] =
                ($recoveryRoots -join ';')
            $commitFailure.Exception.Data[
                'DesktopPetSyftTransactionJournal'] = $journalPath
            throw $commitFailure
        }
        $rollbackErrors =
            New-Object 'Collections.Generic.List[string]'
        if ($null -ne $provenancePublicationLease) {
            try {
                $provenancePublicationLease.Dispose()
                $provenancePublicationLease = $null
            }
            catch {
                $rollbackErrors.Add(
                    'Provenance publication-lease release failed: ' +
                    $_.Exception.Message)
            }
        }
        if ($outputAttempted -and
            -not (Test-DesktopPetSyftSnapshotMatches `
                -Snapshot $outputSnapshot)) {
            try {
                if ([bool]$outputSnapshot.Existed) {
                    try {
                        $SealedStagedOutputFile.AssertRetainedPath(
                            $resolvedOutput,
                            'Published Syft SBOM rollback input')
                        if (-not (Test-Path -LiteralPath $resolvedStagedOutput)) {
                            $SealedStagedOutputFile.RenameRetained(
                                $resolvedStagedOutput,
                                $false)
                        }
                    }
                    catch {
                        # The exact object normally remains at its staged path
                        # when publication fails before commit. The hash-bound
                        # CAS rollback still rejects concurrent destination
                        # bytes.
                    }
                }
                Restore-DesktopPetSyftFileSnapshot `
                    -Snapshot $outputSnapshot `
                    -RecoveryStagingRoot $outputStagingRoot `
                    -ExpectedCurrentSha256 $ExpectedStagedOutputSha256 `
                    -SealedCurrentFile $SealedStagedOutputFile `
                    -ProtectedPaths $outputCrossProtected `
                    -ProtectedDirectories $ProtectedDirectories
            }
            catch {
                $rollbackErrors.Add(
                    "SBOM rollback failed: $($_.Exception.Message)")
            }
        }
        if ($null -ne $publishedProvenanceGuard) {
            try {
                $publishedProvenanceGuard.Dispose()
                $publishedProvenanceGuard = $null
            }
            catch {
                $rollbackErrors.Add(
                    'Published provenance guard release failed: ' +
                    $_.Exception.Message)
            }
        }
        if ($provenanceAttempted -and
            -not (Test-DesktopPetSyftSnapshotMatches `
                -Snapshot $provenanceSnapshot)) {
            try {
                Restore-DesktopPetSyftFileSnapshot `
                    -Snapshot $provenanceSnapshot `
                    -RecoveryStagingRoot $provenanceStagingRoot `
                    -ExpectedCurrentSha256 `
                        $ExpectedStagedProvenanceSha256 `
                    -ProtectedPaths @($ProtectedPaths + $resolvedOutput) `
                    -ProtectedDirectories $ProtectedDirectories
            }
            catch {
                $rollbackErrors.Add(
                    "Provenance rollback failed: $($_.Exception.Message)")
            }
        }

        if ($rollbackErrors.Count -gt 0) {
            Write-DesktopPetSyftRecoveryMarker `
                -StagingRoots $recoveryRoots `
                -OriginalError $commitFailure.Exception.Message `
                -RollbackErrors $rollbackErrors.ToArray()
            $failureMessage = (
                "Syft output transaction failed and rollback was " +
                "incomplete. Recovery staging was retained: {0}. " +
                "Original failure: {1}. Rollback failure: {2}"
            ) -f
                ($recoveryRoots -join '; '),
                $commitFailure.Exception.Message,
                ($rollbackErrors -join ' | ')
            $failure = New-Object InvalidOperationException(
                $failureMessage,
                $commitFailure.Exception)
            $failure.Data['DesktopPetRetainRecoveryStaging'] = $true
            $failure.Data['DesktopPetRecoveryStaging'] =
                ($recoveryRoots -join ';')
            throw $failure
        }
        try {
            Remove-DesktopPetSyftTransactionJournal -JournalLease $journalLease
        }
        catch {
            $failure = New-Object InvalidOperationException(
                (
                    'Syft output transaction rolled back, but its durable ' +
                    'journal could not be removed. Publication remains blocked ' +
                    "for recovery: $journalPath. Cleanup: " +
                    $_.Exception.Message),
                $commitFailure.Exception)
            $failure.Data['DesktopPetRetainRecoveryStaging'] = $true
            $failure.Data['DesktopPetRecoveryStaging'] =
                ($recoveryRoots -join ';')
            $failure.Data['DesktopPetSyftTransactionJournal'] = $journalPath
            throw $failure
        }
        throw $commitFailure
    }
    }
    catch {
        $transactionPrimaryError = $_
        throw
    }
    finally {
        $cleanupErrors =
            New-Object 'Collections.Generic.List[Exception]'
        if ($null -ne $publishedProvenanceGuard) {
            try {
                $publishedProvenanceGuard.Dispose()
                $publishedProvenanceGuard = $null
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        if ($null -ne $provenancePublicationLease) {
            try {
                $provenancePublicationLease.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        $snapshotFiles = New-Object 'Collections.Generic.List[object]'
        foreach ($snapshot in @(
                $outputSnapshot,
                $provenanceInitialSnapshot,
                $provenanceSnapshot)) {
            if ($null -eq $snapshot -or
                $null -eq $snapshot.SealedBackupFile) {
                continue
            }
            $alreadyAdded = $false
            foreach ($existing in $snapshotFiles) {
                if ([object]::ReferenceEquals(
                        $existing,
                        $snapshot.SealedBackupFile)) {
                    $alreadyAdded = $true
                    break
                }
            }
            if (-not $alreadyAdded) {
                $snapshotFiles.Add($snapshot.SealedBackupFile)
            }
        }
        foreach ($snapshotFile in $snapshotFiles) {
            try {
                $snapshotFile.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        if ($null -ne $journalLease -and
            $null -ne $journalLease.SealedFile) {
            try {
                $journalLease.SealedFile.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        if ($ownsSealedStagedProvenanceFile -and
            $null -ne $SealedStagedProvenanceFile) {
            try {
                $SealedStagedProvenanceFile.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        if ($ownsSealedStagedOutputFile -and
            $null -ne $SealedStagedOutputFile) {
            try {
                $SealedStagedOutputFile.Dispose()
            }
            catch {
                $cleanupErrors.Add($_.Exception)
            }
        }
        if ($cleanupErrors.Count -gt 0) {
            if ($null -eq $transactionPrimaryError) {
                throw $cleanupErrors[0]
            }
            Write-Warning (
                'Syft transaction retained-handle cleanup also failed; ' +
                'preserving the primary error. Cleanup error: ' +
                $cleanupErrors[0].Message)
        }
    }
}
