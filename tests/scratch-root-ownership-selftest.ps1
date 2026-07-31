#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$pathSafety = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
$syftScript = Join-Path $repoRoot 'packaging\Invoke-LockedSyft.ps1'
$upgradePolicy =
    Join-Path $repoRoot 'packaging\MsiNMinusOneUpgradeGate.Policy.ps1'
$upgradeEntrypoint =
    Join-Path $repoRoot 'packaging\Invoke-MsiNMinusOneUpgradeGate.ps1'
. $pathSafety
. $upgradePolicy

function Assert-Rejected {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $failure = $null
    try {
        & $Action
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw "$Name was accepted."
    }
    if ($failure.Exception.Message -notmatch $ExpectedMessage) {
        throw (
            "$Name failed for the wrong reason: " +
            $failure.Exception.Message)
    }
}

function Get-ExceptionWin32ErrorCode {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    $candidate = $null
    while ($null -ne $current) {
        if ($current.PSObject.Properties.Name -contains 'NativeErrorCode' -and
            $current.NativeErrorCode -gt 0) {
            $candidate = [int]$current.NativeErrorCode
        }
        elseif ($current.HResult -ne 0) {
            $candidate = [int]($current.HResult -band 0xFFFF)
        }
        $current = $current.InnerException
    }
    return $candidate
}

function Assert-MarkersInOrder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string[]]$Markers
    )

    $cursor = -1
    foreach ($marker in $Markers) {
        $position = $Source.IndexOf(
            $marker,
            ($cursor + 1),
            [StringComparison]::Ordinal)
        if ($position -lt 0) {
            throw "$Name is missing ordered contract marker: $marker"
        }
        $cursor = $position
    }
}

function Invoke-MockedNoPriorPolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][hashtable]$Parameters)

    function Invoke-RestMethod {
        param(
            [switch]$UseBasicParsing,
            [int]$TimeoutSec,
            [hashtable]$Headers,
            [string]$Uri
        )
        return @()
    }

    return Invoke-DesktopPetMsiNMinusOneUpgradePolicy @Parameters
}

function Invoke-MockedPriorPolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][hashtable]$Parameters)

    function Invoke-RestMethod {
        param(
            [switch]$UseBasicParsing,
            [int]$TimeoutSec,
            [hashtable]$Headers,
            [string]$Uri
        )
        return [pscustomobject]@{
            tag_name = 'v9.8.6'
            draft = $false
            prerelease = $false
            assets = @(
                [pscustomobject]@{
                    name = 'DesktopPet-AI-Edition-9.8.6-x64.msi'
                    size = 1
                })
        }
    }

    return Invoke-DesktopPetMsiNMinusOneUpgradePolicy @Parameters
}

$syftSource = Get-Content -LiteralPath $syftScript -Raw
$policySource = Get-Content -LiteralPath $upgradePolicy -Raw
$entrypointSource = Get-Content -LiteralPath $upgradeEntrypoint -Raw

Assert-MarkersInOrder `
    -Name 'Locked Syft scratch lease' `
    -Source $syftSource `
    -Markers @(
        '$toolRootLease = Open-DesktopPetNewScratchDirectory',
        'Receive-LockedHttpsFileCreateNew',
        'Expand-Archive',
        '& $syft scan',
        'Publish-DesktopPetSyftOutputTransaction',
        '$toolRootLease.Dispose()')
foreach ($contract in @(
        '$resolvedLock,',
        '$resolvedManifest,',
        '$resolvedOutput)',
        '$resolvedProvenance',
        '$repoRoot,',
        '$resolvedScanRoot,',
        '$resolvedRuntimeRoot)')) {
    if (-not $syftSource.Contains($contract)) {
        throw "Locked Syft scratch protection is missing: $contract"
    }
}
if ($syftSource -match
    '(?s)Reset-DesktopPetStagingDirectory\s+`\s*\r?\n\s*-Path\s+\$resolvedToolRoot') {
    throw 'Locked Syft still resets and adopts a pre-existing ToolRoot.'
}

Assert-MarkersInOrder `
    -Name 'N-1 policy scratch lease' `
    -Source $policySource `
    -Markers @(
        '$downloadRootLease = Open-DesktopPetNewScratchDirectory',
        'Invoke-RestMethod',
        'DownloadRootLease = if',
        '$leaseTransferred = $true')
foreach ($contract in @(
        '$currentMsi,',
        '$manifest,',
        '$evidence)',
        '$repositoryRoot,',
        '$currentRuntime)')) {
    if (-not $policySource.Contains($contract)) {
        throw "N-1 DownloadRoot protection is missing: $contract"
    }
}
Assert-MarkersInOrder `
    -Name 'N-1 operational retained lease' `
    -Source $entrypointSource `
    -Markers @(
        '$downloadRootLease = $gateContext.DownloadRootLease',
        'try {',
        'Receive-ReleaseAsset -Asset',
        'Test-MsiNMinusOneUpgrade.ps1',
        'Publish-DesktopPetMsiNMinusOneEvidence',
        '$downloadRootLease.Dispose()')
if ($entrypointSource -match
    '(?s)Reset-DesktopPetStagingDirectory\s+`\s*\r?\n\s*-Path\s+\$resolvedDownloadRoot') {
    throw 'The N-1 entrypoint still resets and adopts DownloadRoot.'
}

$tempRoot =
    [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $tempRoot (
    'DesktopPet-ScratchOwnership-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$transferredLease = $null
$transferredMsiInput = $null
$repoProbe = Join-Path $repoRoot (
    '.DesktopPet-forbidden-download-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null

    # Prove the primitive prevents both leaf and ancestor swaps/deletions while
    # retained, and releases those reservations deterministically on Dispose.
    $ownershipParent = Join-Path $scratch 'ownership-parent'
    $ownershipMoved = Join-Path $scratch 'ownership-parent-moved'
    $ownedRoot = Join-Path $ownershipParent 'owned'
    $ownedMoved = Join-Path $ownershipParent 'owned-moved'
    $outsideRoot = Join-Path $scratch 'outside'
    New-Item -ItemType Directory -Path $ownershipParent, $outsideRoot |
        Out-Null
    $outsideSentinel = Join-Path $outsideRoot 'must-survive.txt'
    [IO.File]::WriteAllText($outsideSentinel, 'outside-sentinel', $utf8)
    $outsideHash = (
        Get-FileHash -LiteralPath $outsideSentinel -Algorithm SHA256).Hash

    $script:createHookObserved = $false
    $script:createHookSwapBlocked = $false
    $script:handoffHookObserved = $false
    $script:handoffLeafRenameBlocked = $false
    $script:handoffAncestorRenameBlocked = $false
    $script:handoffLeafDeleteBlocked = $false
    $script:handoffAncestorDeleteBlocked = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -ceq 'create-new-scratch') {
            $script:createHookObserved = $true
            try {
                Move-Item `
                    -LiteralPath $ownershipParent `
                    -Destination $ownershipMoved `
                    -ErrorAction Stop
            }
            catch {
                $script:createHookSwapBlocked = $true
            }
            return
        }
        if ($operation -cne 'after-create-new-scratch') {
            return
        }
        $script:handoffHookObserved = $true
        try {
            Move-Item `
                -LiteralPath $ownedRoot `
                -Destination $ownedMoved `
                -ErrorAction Stop
        }
        catch {
            $script:handoffLeafRenameBlocked = $true
        }
        try {
            Move-Item `
                -LiteralPath $ownershipParent `
                -Destination $ownershipMoved `
                -ErrorAction Stop
        }
        catch {
            $script:handoffAncestorRenameBlocked = $true
        }
        try {
            Remove-Item `
                -LiteralPath $ownedRoot `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
        catch {
            $script:handoffLeafDeleteBlocked = $true
        }
        try {
            Remove-Item `
                -LiteralPath $ownershipParent `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
        catch {
            $script:handoffAncestorDeleteBlocked = $true
        }
    }
    $ownershipLease = Open-DesktopPetNewScratchDirectory `
        -Path $ownedRoot `
        -AllowedRoot $ownershipParent `
        -TrustedRoot $scratch `
        -ProtectedDirectories @($outsideRoot)
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    try {
        if (-not $script:createHookObserved -or
            -not $script:createHookSwapBlocked -or
            -not $script:handoffHookObserved -or
            -not $script:handoffLeafRenameBlocked -or
            -not $script:handoffAncestorRenameBlocked -or
            -not $script:handoffLeafDeleteBlocked -or
            -not $script:handoffAncestorDeleteBlocked) {
            throw (
                'Scratch creation/handoff hooks did not retain leaf and ' +
                'ancestor rename-delete reservations.')
        }
        Assert-Rejected `
            -Name 'retained scratch leaf rename' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Move-Item `
                    -LiteralPath $ownedRoot `
                    -Destination $ownedMoved `
                    -ErrorAction Stop
            }
        Assert-Rejected `
            -Name 'retained scratch ancestor rename' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Move-Item `
                    -LiteralPath $ownershipParent `
                    -Destination $ownershipMoved `
                    -ErrorAction Stop
            }
        Assert-Rejected `
            -Name 'retained scratch leaf deletion' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Remove-Item `
                    -LiteralPath $ownedRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            }
        Assert-Rejected `
            -Name 'retained scratch ancestor deletion' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Remove-Item `
                    -LiteralPath $ownershipParent `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            }
        [IO.File]::WriteAllText(
            (Join-Path $ownedRoot 'local-write.txt'),
            'local-only',
            $utf8)
    }
    finally {
        $ownershipLease.Dispose()
    }

    Move-Item -LiteralPath $ownedRoot -Destination $ownedMoved
    Move-Item -LiteralPath $ownedMoved -Destination $ownedRoot
    Move-Item -LiteralPath $ownershipParent -Destination $ownershipMoved
    Move-Item -LiteralPath $ownershipMoved -Destination $ownershipParent
    Remove-Item -LiteralPath $ownedRoot -Recurse -Force
    Remove-Item -LiteralPath $ownershipParent -Recurse -Force
    if ((Get-FileHash `
            -LiteralPath $outsideSentinel `
            -Algorithm SHA256).Hash -cne $outsideHash) {
        throw 'Scratch lease mutation probes changed the outside sentinel.'
    }

    $failedCreateParent = Join-Path $scratch 'failed-create-parent'
    $failedCreateRoot = Join-Path $failedCreateParent 'owned'
    New-Item -ItemType Directory -Path $failedCreateParent | Out-Null
    $script:afterCreateFailureObserved = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -ceq 'after-create-new-scratch') {
            $script:afterCreateFailureObserved = $true
            throw 'Injected post-create handoff failure.'
        }
    }
    Assert-Rejected `
        -Name 'post-create scratch handoff failure' `
        -ExpectedMessage 'Injected post-create handoff failure' `
        -Action {
            $unexpectedLease = Open-DesktopPetNewScratchDirectory `
                -Path $failedCreateRoot `
                -AllowedRoot $failedCreateParent `
                -TrustedRoot $scratch
            if ($null -ne $unexpectedLease) {
                $unexpectedLease.Dispose()
            }
        }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:afterCreateFailureObserved -or
        (Test-Path -LiteralPath $failedCreateRoot)) {
        throw 'Failed scratch create-to-lease handoff leaked its owned root.'
    }

    # Existing files that must be mutated by path (for example, MSI COM
    # databases) retain both identity and their parent chain without denying
    # ordinary in-place writes.
    $mutableRoot = Join-Path $scratch 'mutable-file'
    New-Item -ItemType Directory -Path $mutableRoot | Out-Null
    $mutablePath = Join-Path $mutableRoot 'staged.msi'
    $mutableMoved = Join-Path $mutableRoot 'staged-moved.msi'
    $mutableRootMoved = Join-Path $scratch 'mutable-file-moved'
    [IO.File]::WriteAllText($mutablePath, 'before-mutation', $utf8)
    $mutableLease = Open-DesktopPetValidatedMutableFile `
        -Path $mutablePath `
        -Root $mutableRoot
    $mutableAlias = $null
    try {
        [IO.File]::WriteAllText($mutablePath, 'after-mutation', $utf8)
        $mutableLease.Revalidate()
        Assert-Rejected `
            -Name 'retained mutable file rename' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Move-Item `
                    -LiteralPath $mutablePath `
                    -Destination $mutableMoved `
                    -ErrorAction Stop
            }
        Assert-Rejected `
            -Name 'retained mutable file deletion' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Remove-Item `
                    -LiteralPath $mutablePath `
                    -Force `
                    -ErrorAction Stop
            }
        Assert-Rejected `
            -Name 'retained mutable file ancestor rename' `
            -ExpectedMessage '(?i)used by another process|access.*denied' `
            -Action {
                Move-Item `
                    -LiteralPath $mutableRoot `
                    -Destination $mutableRootMoved `
                    -ErrorAction Stop
            }

        $mutableAlias = Join-Path $mutableRoot 'injected-hardlink.msi'
        New-Item `
            -ItemType HardLink `
            -Path $mutableAlias `
            -Target $mutablePath | Out-Null
        Assert-Rejected `
            -Name 'mutable file injected hard link' `
            -ExpectedMessage '(?i)hard-link alias' `
            -Action {
                $mutableLease.Revalidate()
            }
    }
    finally {
        $mutableLease.Dispose()
    }
    if ($null -ne $mutableAlias -and
        (Test-Path -LiteralPath $mutableAlias)) {
        Remove-Item -LiteralPath $mutableAlias -Force
    }
    Move-Item -LiteralPath $mutablePath -Destination $mutableMoved
    Remove-Item -LiteralPath $mutableMoved -Force
    Move-Item -LiteralPath $mutableRoot -Destination $mutableRootMoved
    Remove-Item -LiteralPath $mutableRootMoved -Recurse -Force

    # CreateNew must never truncate a concurrently inserted hard link.
    $newFileRoot = Join-Path $scratch 'create-new-file'
    New-Item -ItemType Directory -Path $newFileRoot | Out-Null
    $competingPath = Join-Path $newFileRoot 'competing.tmp'
    $script:createNewHookObserved = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -cne 'before-create-new-file-race') {
            return
        }
        $script:createNewHookObserved = $true
        New-Item `
            -ItemType HardLink `
            -Path $competingPath `
            -Target $outsideSentinel | Out-Null
    }
    Assert-Rejected `
        -Name 'create-new concurrent hard-link insertion' `
        -ExpectedMessage '(?i)already exists|cannot create a file' `
        -Action {
            [void](Write-DesktopPetNewUtf8File `
                -Path $competingPath `
                -Root $newFileRoot `
                -Content 'must-not-overwrite' `
                -MutationOperation 'before-create-new-file-race')
        }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:createNewHookObserved -or
        (Get-FileHash `
            -LiteralPath $outsideSentinel `
            -Algorithm SHA256).Hash -cne $outsideHash) {
        throw 'CreateNew race overwrote the competing hard-link target.'
    }
    Remove-Item -LiteralPath $competingPath -Force

    # Publication binds the staged identity and the destination state across
    # both mutation hooks.
    $publicationRoot = Join-Path $scratch 'identity-publication'
    $publicationStaging = Join-Path $publicationRoot 'staging'
    $publicationDestinationRoot = Join-Path $publicationRoot 'destination'
    New-Item `
        -ItemType Directory `
        -Path $publicationStaging, $publicationDestinationRoot |
        Out-Null

    $temporary = Join-Path $publicationStaging 'artifact.tmp'
    $temporaryMoved = Join-Path $publicationStaging 'artifact-original.tmp'
    $destination = Join-Path $publicationDestinationRoot 'artifact.bin'
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'sealed-temporary')
    $temporaryIdentity = Open-DesktopPetValidatedMutableFile `
        -Path $temporary `
        -Root $publicationStaging
    $temporaryIdentity.Revalidate()
    $temporaryIdentity.Dispose()
    $temporaryHash = (
        Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    $script:tempSubstitutionObserved = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -cne 'before-publish-lease') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:tempSubstitutionObserved = $true
        Move-Item `
            -LiteralPath $temporary `
            -Destination $temporaryMoved
        [IO.File]::WriteAllText($temporary, 'substituted-temporary', $utf8)
    }
    Assert-Rejected `
        -Name 'atomic publication temporary substitution' `
        -ExpectedMessage '(?i)identity changed|sealed identity' `
        -Action {
            [void](Publish-DesktopPetAtomicFile `
                -TemporaryPath $temporary `
                -DestinationPath $destination `
                -TrustedRoot $publicationRoot `
                -ExpectedTemporaryIdentity $temporaryIdentity `
                -ExpectedTemporarySha256 $temporaryHash `
                -DestinationMustBeAbsent)
        }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:tempSubstitutionObserved -or
        (Test-Path -LiteralPath $destination) -or
        [IO.File]::ReadAllText($temporaryMoved) -cne 'sealed-temporary') {
        throw 'Temporary substitution was not rejected before publication.'
    }
    Remove-Item -LiteralPath $temporary, $temporaryMoved -Force

    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'new-destination-bytes')
    $temporaryIdentity = Open-DesktopPetValidatedMutableFile `
        -Path $temporary `
        -Root $publicationStaging
    $temporaryIdentity.Dispose()
    $temporaryHash = (
        Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($destination, 'sealed-destination', $utf8)
    $destinationIdentity = Open-DesktopPetValidatedMutableFile `
        -Path $destination `
        -Root $publicationDestinationRoot
    $destinationIdentity.Dispose()
    $destinationHash = (
        Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    $destinationMoved =
        Join-Path $publicationDestinationRoot 'artifact-original.bin'
    $script:destinationSubstitutionObserved = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -cne 'publish') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:destinationSubstitutionObserved = $true
        Move-Item `
            -LiteralPath $destination `
            -Destination $destinationMoved
        [IO.File]::WriteAllText($destination, 'substituted-destination', $utf8)
    }
    Assert-Rejected `
        -Name 'atomic publication destination substitution' `
        -ExpectedMessage '(?i)identity changed|sealed identity|retained path' `
        -Action {
            [void](Publish-DesktopPetAtomicFile `
                -TemporaryPath $temporary `
                -DestinationPath $destination `
                -TrustedRoot $publicationRoot `
                -ExpectedTemporaryIdentity $temporaryIdentity `
                -ExpectedTemporarySha256 $temporaryHash `
                -ExpectedDestinationIdentity $destinationIdentity `
                -ExpectedDestinationSha256 $destinationHash)
        }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:destinationSubstitutionObserved -or
        [IO.File]::ReadAllText($destinationMoved) -cne
            'sealed-destination' -or
        [IO.File]::ReadAllText($temporary) -cne
            'new-destination-bytes') {
        throw 'Destination substitution was not rejected before publication.'
    }
    Remove-Item `
        -LiteralPath $temporary, $destination, $destinationMoved `
        -Force

    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'absence-bound-temporary')
    $temporaryIdentity = Open-DesktopPetValidatedMutableFile `
        -Path $temporary `
        -Root $publicationStaging
    $temporaryIdentity.Dispose()
    $temporaryHash = (
        Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    $script:absenceCompetitorObserved = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -cne 'publish') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:absenceCompetitorObserved = $true
        [IO.File]::WriteAllText($destination, 'competing-destination', $utf8)
    }
    Assert-Rejected `
        -Name 'atomic publication expected-absence competitor' `
        -ExpectedMessage (
            '(?i)already exists|cannot create a file|' +
            'could not atomically publish|could not atomically rename') `
        -Action {
            [void](Publish-DesktopPetAtomicFile `
                -TemporaryPath $temporary `
                -DestinationPath $destination `
                -TrustedRoot $publicationRoot `
                -ExpectedTemporaryIdentity $temporaryIdentity `
                -ExpectedTemporarySha256 $temporaryHash `
                -DestinationMustBeAbsent)
        }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:absenceCompetitorObserved -or
        [IO.File]::ReadAllText($destination) -cne
            'competing-destination' -or
        [IO.File]::ReadAllText($temporary) -cne
            'absence-bound-temporary') {
        throw 'Expected-absence publication overwrote its competitor.'
    }
    Remove-Item -LiteralPath $temporary, $destination -Force

    # Expected-absence publication has no old destination to restore, but a
    # post-rename source alias must still be removed from the public name and
    # returned to private staging before the function rejects the commit.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'absence-postcommit-alias')
    $absencePostcommitAlias =
        Join-Path $publicationStaging 'absence-postcommit-alias-link.tmp'
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:absencePostcommitAliasObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-absent-rename') {
                return
            }
            New-Item `
                -ItemType HardLink `
                -Path $absencePostcommitAlias `
                -Target $destination `
                -ErrorAction Stop | Out-Null
            $script:absencePostcommitAliasObserved = $true
        }
        Assert-Rejected `
            -Name 'expected-absence postcommit source alias' `
            -ExpectedMessage '(?i)exact staged input was restored' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')) `
                    -DestinationMustBeAbsent)
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:absencePostcommitAliasObserved -or
            (Test-Path -LiteralPath $destination) -or
            -not $sealedTemporary.RetainedPathEquals($temporary) -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'absence-postcommit-alias' -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $temporary) -ne 2) {
            throw (
                'Expected-absence postcommit alias remained public or exact ' +
                'private recovery failed.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $absencePostcommitAlias, $temporary -Force

    # Seal() acquires its deny-write identity while the mutable leaf/name
    # reservation is still live. The transition callback executes at that
    # exact overlap: rename is blocked by the mutable handle and write is
    # blocked by the newly acquired sealed handle.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'no-gap-seal-transition')
    $transitionMoved = Join-Path $publicationStaging 'transition-moved.tmp'
    $mutableTransition = Open-DesktopPetValidatedMutableFile `
        -Path $temporary `
        -Root $publicationStaging
    $sealedTemporary = $null
    try {
        $script:sealTransitionObserved = $false
        $script:sealTransitionRenameError = $null
        $script:sealTransitionWriteError = $null
        $transitionProbe = [Action]{
            $script:sealTransitionObserved = $true
            try {
                Move-Item `
                    -LiteralPath $temporary `
                    -Destination $transitionMoved `
                    -ErrorAction Stop
            }
            catch {
                $script:sealTransitionRenameError =
                    Get-ExceptionWin32ErrorCode -Exception $_.Exception
            }
            try {
                [IO.File]::WriteAllText(
                    $temporary,
                    'must-not-cross-seal-transition',
                    $utf8)
            }
            catch {
                $script:sealTransitionWriteError =
                    Get-ExceptionWin32ErrorCode -Exception $_.Exception
            }
        }
        $sealedTemporary = $mutableTransition.Seal($transitionProbe)
        if (-not $script:sealTransitionObserved -or
            $script:sealTransitionRenameError -ne 32 -or
            $script:sealTransitionWriteError -ne 32 -or
            (Test-Path -LiteralPath $transitionMoved) -or
            [IO.File]::ReadAllText($temporary) -cne
                'no-gap-seal-transition') {
            throw (
                'Mutable-to-sealed transition exposed a rename/write gap or ' +
                'blocked an ordinary post-seal reader.')
        }
        Assert-Rejected `
            -Name 'consumed mutable lease after Seal' `
            -ExpectedMessage '(?i)disposed' `
            -Action {
                $mutableTransition.Revalidate()
            }
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $publicationRoot `
            -SealedTemporaryFile $sealedTemporary `
            -ExpectedTemporarySha256 (
                $sealedTemporary.ComputeHash('SHA256')) `
            -DestinationMustBeAbsent)
        if ($sealedTemporary.ReadAllTextUtf8(1MB) -cne
            'no-gap-seal-transition') {
            throw 'No-gap sealed transition published the wrong exact object.'
        }
    }
    finally {
        if ($null -ne $sealedTemporary) {
            $sealedTemporary.Dispose()
        }
        if ($null -ne $mutableTransition) {
            $mutableTransition.Dispose()
        }
    }
    Remove-Item -LiteralPath $destination -Force

    # The final semantic read/hash and publication must share one deny-write
    # handle. A post-check writer is blocked without breaking publication.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'sealed-post-check-bytes')
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        if ($sealedTemporary.ReadAllTextUtf8(1MB) -cne
            'sealed-post-check-bytes') {
            throw 'Sealed staged-file semantic read returned wrong bytes.'
        }
        $sealedHash = $sealedTemporary.ComputeHash('SHA256')
        $script:sealedPostCheckObserved = $false
        $script:sealedPostCheckWriteError = $null
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-check') {
                return
            }
            $script:sealedPostCheckObserved = $true
            try {
                [IO.File]::WriteAllText(
                    $temporary,
                    'must-not-mutate-sealed-input',
                    $utf8)
            }
            catch {
                $script:sealedPostCheckWriteError =
                    Get-ExceptionWin32ErrorCode -Exception $_.Exception
            }
        }
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $publicationRoot `
            -SealedTemporaryFile $sealedTemporary `
            -ExpectedTemporarySha256 $sealedHash `
            -DestinationMustBeAbsent)
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.AssertRetainedPath(
            $destination,
            'Sealed post-check publication')
        if (-not $script:sealedPostCheckObserved -or
            $script:sealedPostCheckWriteError -ne 32 -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'sealed-post-check-bytes') {
            throw (
                'Sealed post-check publication did not block an in-place ' +
                'writer or publish the validated bytes.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $destination -Force

    # A name swap after the last check cannot redirect expected-absent
    # publication: the exact retained object is renamed by handle.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'exact-handle-publication')
    $sealedMoved = Join-Path $publicationStaging 'sealed-moved.tmp'
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $sealedHash = $sealedTemporary.ComputeHash('SHA256')
        $script:sealedSwapObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-check') {
                return
            }
            $script:sealedSwapObserved = $true
            Move-Item `
                -LiteralPath $temporary `
                -Destination $sealedMoved `
                -ErrorAction Stop
            [IO.File]::WriteAllText(
                $temporary,
                'path-substitute-must-not-publish',
                $utf8)
        }
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $publicationRoot `
            -SealedTemporaryFile $sealedTemporary `
            -ExpectedTemporarySha256 $sealedHash `
            -DestinationMustBeAbsent)
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:sealedSwapObserved -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'exact-handle-publication' -or
            [IO.File]::ReadAllText($temporary) -cne
                'path-substitute-must-not-publish' -or
            (Test-Path -LiteralPath $sealedMoved)) {
            throw (
                'Expected-absent publication did not bind its rename to the ' +
                'exact sealed staged-file handle.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $temporary, $destination -Force

    # The exact expected-destination handle is frozen before its retained
    # backup move, so a normal post-check writer fails with sharing violation
    # 32 while the exact sealed object is published by no-replace rename.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'replacement-after-destination-mutation')
    [IO.File]::WriteAllText($destination, 'destination-before-check', $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:destinationInPlaceObserved = $false
        $script:destinationInPlaceWriteError = $null
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-check') {
                return
            }
            $script:destinationInPlaceObserved = $true
            try {
                [IO.File]::WriteAllText(
                    $destination,
                    'destination-mutated-after-check',
                    $utf8)
            }
            catch {
                $script:destinationInPlaceWriteError =
                    Get-ExceptionWin32ErrorCode -Exception $_.Exception
            }
        }
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $publicationRoot `
            -SealedTemporaryFile $sealedTemporary `
            -ExpectedTemporarySha256 (
                $sealedTemporary.ComputeHash('SHA256')))
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:destinationInPlaceObserved -or
            $script:destinationInPlaceWriteError -ne 32 -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'replacement-after-destination-mutation' -or
            @(Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*').Count -ne 0) {
            throw (
                'Post-check destination writer was not blocked or exact ' +
                'publication did not clean its capture transaction.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $destination -Force

    # A competitor inserted after the exact backup move wins the public-name
    # gap without being overwritten. The exact prior destination stays in the
    # private transaction and the sealed staged object stays at its input name.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'competitor-gap-new')
    [IO.File]::WriteAllText($destination, 'competitor-gap-original', $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:competitorGapObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-backup') {
                return
            }
            [IO.File]::WriteAllText(
                $destination,
                'competitor-gap-winner',
                $utf8)
            $script:competitorGapObserved = $true
        }
        Assert-Rejected `
            -Name 'post-backup destination competitor' `
            -ExpectedMessage '(?i)recovery evidence was preserved' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $competitorTransactions = @(
            Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*'
        )
        $competitorBackup =
            Join-Path $competitorTransactions[0].FullName 'displaced.bin'
        if (-not $script:competitorGapObserved -or
            [IO.File]::ReadAllText($destination) -cne
                'competitor-gap-winner' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'competitor-gap-new' -or
            $competitorTransactions.Count -ne 1 -or
            [IO.File]::ReadAllText($competitorBackup) -cne
                'competitor-gap-original') {
            throw (
                'Gap competitor was overwritten or exact recovery objects ' +
                'were not preserved.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $temporary, $destination -Force
    Remove-DesktopPetSafeDirectory `
        -Path $competitorTransactions[0].FullName `
        -AllowedRoot $publicationDestinationRoot `
        -TrustedRoot $publicationRoot

    # A raced-in hardlink at the public name is a competitor, not a replacement
    # target. It remains byte-for-byte intact while the exact old destination
    # and sealed staged file survive at their private recovery names.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'hardlink-race-new')
    [IO.File]::WriteAllText($destination, 'hardlink-race-original', $utf8)
    $hardlinkCompetitorSource =
        Join-Path $publicationDestinationRoot 'hardlink-competitor.bin'
    [IO.File]::WriteAllText(
        $hardlinkCompetitorSource,
        'hardlink-race-competitor',
        $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:hardlinkRaceObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-backup') {
                return
            }
            New-Item `
                -ItemType HardLink `
                -Path $destination `
                -Target $hardlinkCompetitorSource `
                -ErrorAction Stop | Out-Null
            $script:hardlinkRaceObserved = $true
        }
        Assert-Rejected `
            -Name 'post-backup hardlink competitor' `
            -ExpectedMessage '(?i)recovery evidence was preserved' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $hardlinkTransactions = @(
            Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*'
        )
        $hardlinkBackup =
            Join-Path $hardlinkTransactions[0].FullName 'displaced.bin'
        if (-not $script:hardlinkRaceObserved -or
            $hardlinkTransactions.Count -ne 1 -or
            [IO.File]::ReadAllText($destination) -cne
                'hardlink-race-competitor' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'hardlink-race-new' -or
            [IO.File]::ReadAllText($hardlinkBackup) -cne
                'hardlink-race-original') {
            throw (
                'Hardlink competitor was overwritten or a retained recovery ' +
                'object was lost.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $destination, $temporary -Force
    Remove-Item -LiteralPath $hardlinkCompetitorSource -Force
    Remove-DesktopPetSafeDirectory `
        -Path $hardlinkTransactions[0].FullName `
        -AllowedRoot $publicationDestinationRoot `
        -TrustedRoot $publicationRoot

    # A raced-in directory reparse point is likewise never opened as the
    # destination and never replaced. Both exact publication objects remain
    # recoverable, while the reparse target stays untouched.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'reparse-race-new')
    [IO.File]::WriteAllText($destination, 'reparse-race-original', $utf8)
    $reparseTarget = Join-Path $publicationRoot 'reparse-race-target'
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    $reparseSentinel = Join-Path $reparseTarget 'sentinel.txt'
    [IO.File]::WriteAllText($reparseSentinel, 'reparse-target-sentinel', $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:reparseRaceObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-backup') {
                return
            }
            New-Item `
                -ItemType Junction `
                -Path $destination `
                -Target $reparseTarget `
                -ErrorAction Stop | Out-Null
            $script:reparseRaceObserved = $true
        }
        Assert-Rejected `
            -Name 'post-backup reparse competitor' `
            -ExpectedMessage '(?i)recovery evidence was preserved' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $reparseTransactions = @(
            Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*'
        )
        $reparseBackup =
            Join-Path $reparseTransactions[0].FullName 'displaced.bin'
        $destinationItem = Get-Item -LiteralPath $destination -Force
        if (-not $script:reparseRaceObserved -or
            $reparseTransactions.Count -ne 1 -or
            ($destinationItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -eq 0 -or
            [IO.File]::ReadAllText($reparseSentinel) -cne
                'reparse-target-sentinel' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'reparse-race-new' -or
            [IO.File]::ReadAllText($reparseBackup) -cne
                'reparse-race-original') {
            throw (
                'Reparse competitor was overwritten, traversed, or caused a ' +
                'retained recovery object to be lost.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
        if (Test-Path -LiteralPath $destination) {
            $reparseCleanupItem =
                Get-Item -LiteralPath $destination -Force
            if (($reparseCleanupItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                [IO.Directory]::Delete($destination)
            }
        }
    }
    Remove-Item -LiteralPath $temporary -Force
    Remove-DesktopPetSafeDirectory `
        -Path $reparseTransactions[0].FullName `
        -AllowedRoot $publicationDestinationRoot `
        -TrustedRoot $publicationRoot
    Remove-Item -LiteralPath $reparseSentinel -Force
    Remove-Item -LiteralPath $reparseTarget -Force

    # A hardlink added to the sealed source immediately before commit is
    # detected by the retained identity check. The old destination is restored
    # and aliased new bytes never reach its public name.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'precommit-alias-new')
    [IO.File]::WriteAllText($destination, 'precommit-alias-original', $utf8)
    $precommitAlias =
        Join-Path $publicationStaging 'precommit-alias-link.tmp'
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:precommitAliasObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-before-final-rename') {
                return
            }
            New-Item `
                -ItemType HardLink `
                -Path $precommitAlias `
                -Target $temporary `
                -ErrorAction Stop | Out-Null
            $script:precommitAliasObserved = $true
        }
        Assert-Rejected `
            -Name 'precommit sealed-source hardlink' `
            -ExpectedMessage '(?i)destination and staged input were restored' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:precommitAliasObserved -or
            [IO.File]::ReadAllText($destination) -cne
                'precommit-alias-original' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'precommit-alias-new' -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $temporary) -ne 2 -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $destination) -ne 1 -or
            @(Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*').Count -ne 0) {
            throw (
                'Precommit sealed-source alias reached the public name or ' +
                'prevented exact destination rollback.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $precommitAlias, $temporary, $destination -Force

    # A hardlink added immediately after the no-replace commit is caught by
    # postcommit retained validation. Recovery moves the exact new public link
    # back by handle before restoring the exact old destination.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'postcommit-alias-new')
    [IO.File]::WriteAllText($destination, 'postcommit-alias-original', $utf8)
    $postcommitAlias =
        Join-Path $publicationStaging 'postcommit-alias-link.tmp'
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:postcommitAliasObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-post-final-rename') {
                return
            }
            New-Item `
                -ItemType HardLink `
                -Path $postcommitAlias `
                -Target $destination `
                -ErrorAction Stop | Out-Null
            $script:postcommitAliasObserved = $true
        }
        Assert-Rejected `
            -Name 'postcommit sealed-source hardlink' `
            -ExpectedMessage '(?i)destination and staged input were restored' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:postcommitAliasObserved -or
            [IO.File]::ReadAllText($destination) -cne
                'postcommit-alias-original' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'postcommit-alias-new' -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $temporary) -ne 2 -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $destination) -ne 1 -or
            @(Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*').Count -ne 0) {
            throw (
                'Postcommit source alias remained public or exact recovery ' +
                'failed to restore both original names.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $postcommitAlias, $temporary, $destination -Force

    # The final pre-linearization checkpoint runs immediately before backup
    # cleanup. A new-public hardlink injected there must still trigger full
    # rollback; old bytes cannot be deleted first and a later check cannot mask
    # the alias as a postcommit failure.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'prelinearization-alias-new')
    [IO.File]::WriteAllText(
        $destination,
        'prelinearization-alias-original',
        $utf8)
    $prelinearizationAlias =
        Join-Path $publicationStaging 'prelinearization-alias-link.tmp'
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:prelinearizationAliasObserved = $false
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-before-backup-cleanup') {
                return
            }
            New-Item `
                -ItemType HardLink `
                -Path $prelinearizationAlias `
                -Target $destination `
                -ErrorAction Stop | Out-Null
            $script:prelinearizationAliasObserved = $true
        }
        Assert-Rejected `
            -Name 'pre-linearization new-public hardlink' `
            -ExpectedMessage '(?i)destination and staged input were restored' `
            -Action {
                [void](Publish-DesktopPetAtomicFile `
                    -TemporaryPath $temporary `
                    -DestinationPath $destination `
                    -TrustedRoot $publicationRoot `
                    -SealedTemporaryFile $sealedTemporary `
                    -ExpectedTemporarySha256 (
                        $sealedTemporary.ComputeHash('SHA256')))
            }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        if (-not $script:prelinearizationAliasObserved -or
            [IO.File]::ReadAllText($destination) -cne
                'prelinearization-alias-original' -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'prelinearization-alias-new' -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $destination) -ne 1 -or
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $temporary) -ne 2 -or
            @(Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*').Count -ne 0) {
            throw (
                'Pre-linearization alias survived at the public destination ' +
                'or exact rollback occurred after old-byte deletion.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item `
        -LiteralPath $prelinearizationAlias, $temporary, $destination `
        -Force

    # Backup cleanup is postcommit housekeeping. A deterministic hardlink makes
    # exact backup deletion unsafe; publication still succeeds, a warning names
    # the retained recovery directory, and both old-byte links survive.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'cleanup-warning-new')
    [IO.File]::WriteAllText($destination, 'cleanup-warning-original', $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    try {
        $script:cleanupFailureObserved = $false
        $script:cleanupAliasPath = $null
        $script:DesktopPetStagingMutationTestHook = {
            param($operation, $path)
            if ($operation -cne 'sealed-publish-before-backup-cleanup') {
                return
            }
            $transaction = @(
                Get-ChildItem `
                    -LiteralPath $publicationDestinationRoot `
                    -Directory `
                    -Filter '.DesktopPet-publish-transaction-*'
            )
            if ($transaction.Count -ne 1) {
                throw 'Could not identify the active publication transaction.'
            }
            $captured =
                Join-Path $transaction[0].FullName 'displaced.bin'
            $script:cleanupAliasPath =
                Join-Path $transaction[0].FullName 'cleanup-alias.bin'
            New-Item `
                -ItemType HardLink `
                -Path $script:cleanupAliasPath `
                -Target $captured `
                -ErrorAction Stop | Out-Null
            $script:cleanupFailureObserved = $true
        }
        $cleanupWarnings = @()
        $cleanupResult = Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $publicationRoot `
            -SealedTemporaryFile $sealedTemporary `
            -ExpectedTemporarySha256 (
                $sealedTemporary.ComputeHash('SHA256')) `
            -WarningVariable cleanupWarnings `
            -WarningAction Stop
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $cleanupTransactions = @(
            Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*'
        )
        $cleanupWarningText = @(
            $cleanupWarnings | ForEach-Object { $_.ToString() }
        ) -join "`n"
        $cleanupBackup =
            Join-Path $cleanupTransactions[0].FullName 'displaced.bin'
        if (-not $script:cleanupFailureObserved -or
            $cleanupResult -cne $destination -or
            $cleanupTransactions.Count -ne 1 -or
            $cleanupWarningText -notmatch
                '(?i)committed successfully.*recovery evidence' -or
            $cleanupWarningText -notmatch
                [regex]::Escape($cleanupTransactions[0].FullName) -or
            $sealedTemporary.ReadAllTextUtf8(1MB) -cne
                'cleanup-warning-new' -or
            [IO.File]::ReadAllText($cleanupBackup) -cne
                'cleanup-warning-original' -or
            [IO.File]::ReadAllText($script:cleanupAliasPath) -cne
                'cleanup-warning-original') {
            throw (
                'Postcommit cleanup failure masked success or failed to ' +
                'preserve and report exact recovery evidence.')
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $sealedTemporary.Dispose()
    }
    Remove-Item -LiteralPath $destination -Force
    Remove-Item -LiteralPath $script:cleanupAliasPath, $cleanupBackup -Force
    Remove-DesktopPetSafeDirectory `
        -Path $cleanupTransactions[0].FullName `
        -AllowedRoot $publicationDestinationRoot `
        -TrustedRoot $publicationRoot

    # An already-open destination writer prevents acquisition of the exact
    # deny-write retained handle. Assert the native sharing-violation code,
    # rather than accepting a broad localized message match.
    [void](Write-DesktopPetNewUtf8File `
        -Path $temporary `
        -Root $publicationStaging `
        -Content 'sharing-violation-new')
    [IO.File]::WriteAllText($destination, 'sharing-violation-original', $utf8)
    $sealedTemporary = Open-DesktopPetSealedStagedFile `
        -Path $temporary `
        -Root $publicationStaging
    $destinationWriter = [IO.File]::Open(
        $destination,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Write,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $sharingFailure = $null
        try {
            [void](Publish-DesktopPetAtomicFile `
                -TemporaryPath $temporary `
                -DestinationPath $destination `
                -TrustedRoot $publicationRoot `
                -SealedTemporaryFile $sealedTemporary `
                -ExpectedTemporarySha256 (
                    $sealedTemporary.ComputeHash('SHA256')))
        }
        catch {
            $sharingFailure = $_
        }
        if ($null -eq $sharingFailure) {
            throw 'Existing destination writer was accepted.'
        }
        $sharingError = Get-ExceptionWin32ErrorCode `
            -Exception $sharingFailure.Exception
        if ($sharingError -ne 32 -or
            @(Get-ChildItem `
                -LiteralPath $publicationDestinationRoot `
                -Directory `
                -Filter '.DesktopPet-publish-transaction-*').Count -ne 0) {
            throw (
                'Existing writer did not fail with Win32 sharing violation 32 ' +
                'or publication changed retained data.')
        }
    }
    finally {
        $destinationWriter.Dispose()
        $sealedTemporary.Dispose()
    }
    if ([IO.File]::ReadAllText($destination) -cne
            'sharing-violation-original' -or
        [IO.File]::ReadAllText($temporary) -cne 'sharing-violation-new') {
        throw 'Sharing-violation rejection changed retained data.'
    }
    Remove-Item -LiteralPath $temporary, $destination -Force

    # Invoke the real Syft entrypoint only through fail-fast preflight cases;
    # none of these cases can reach download or executable launch.
    $syftFixture = Join-Path $scratch 'syft'
    $scanRoot = Join-Path $syftFixture 'scan'
    $runtimeRoot = Join-Path $syftFixture 'runtime'
    $outputParent = Join-Path $syftFixture 'output'
    $toolParent = Join-Path $syftFixture 'tools'
    New-Item `
        -ItemType Directory `
        -Path $scanRoot, $runtimeRoot, $outputParent, $toolParent |
        Out-Null
    $runtimeManifest = Join-Path $syftFixture 'runtime-files.txt'
    $runtimeLeaf = Join-Path $runtimeRoot 'DesktopPet.exe'
    [IO.File]::WriteAllText($runtimeManifest, "DesktopPet.exe`n", $utf8)
    [IO.File]::WriteAllText($runtimeLeaf, 'runtime-bytes', $utf8)
    $syftOutput = Join-Path $outputParent 'output.spdx.json'
    $syftParameters = @{
        LockPath = Join-Path $repoRoot 'packaging\syft-toolchain-lock.json'
        ScanRoot = $scanRoot
        OutputPath = $syftOutput
        RuntimeRoot = $runtimeRoot
        RuntimeManifestPath = $runtimeManifest
    }

    $existingToolRoot = Join-Path $toolParent 'existing-root'
    New-Item -ItemType Directory -Path $existingToolRoot | Out-Null
    $toolSentinel = Join-Path $existingToolRoot 'must-survive.txt'
    [IO.File]::WriteAllText($toolSentinel, 'pre-existing-owned-data', $utf8)
    Assert-Rejected `
        -Name 'pre-existing locked Syft ToolRoot' `
        -ExpectedMessage '(?i)must be absent and caller-owned' `
        -Action {
            & $syftScript @syftParameters -ToolRoot $existingToolRoot
        }
    if ([IO.File]::ReadAllText($toolSentinel) -cne
        'pre-existing-owned-data') {
        throw 'Rejected Syft ToolRoot was modified or adopted.'
    }

    Assert-Rejected `
        -Name 'repository-overlapping locked Syft ToolRoot' `
        -ExpectedMessage '(?i)overlaps a protected path or directory' `
        -Action {
            & $syftScript @syftParameters -ToolRoot $repoProbe
        }
    if (Test-Path -LiteralPath $repoProbe) {
        throw 'Rejected repository-overlapping Syft ToolRoot was created.'
    }

    $scanSentinel = Join-Path $scanRoot 'must-survive.txt'
    [IO.File]::WriteAllText($scanSentinel, 'scan-sentinel', $utf8)
    Assert-Rejected `
        -Name 'scan-overlapping locked Syft ToolRoot' `
        -ExpectedMessage '(?i)overlaps a protected path or directory' `
        -Action {
            & $syftScript @syftParameters -ToolRoot $scanRoot
        }
    if ([IO.File]::ReadAllText($scanSentinel) -cne 'scan-sentinel') {
        throw 'Rejected scan-overlapping Syft ToolRoot changed scan input.'
    }

    Assert-Rejected `
        -Name 'output-overlapping locked Syft ToolRoot' `
        -ExpectedMessage '(?i)overlaps a protected packaging input' `
        -Action {
            & $syftScript @syftParameters -ToolRoot $syftOutput
        }
    if (Test-Path -LiteralPath $syftOutput) {
        throw 'Rejected output-overlapping Syft ToolRoot created output.'
    }

    # The downloader now hashes the exact retained archive handle, so a
    # fabricated archive cannot honestly stand in for the pinned release bytes.
    # Exercise the real publication transaction offline in its dedicated
    # regression, while the fail-fast cases and ordered source contract above
    # cover ToolRoot ownership around download, extraction, scan, and cleanup.
    & (Join-Path $repoRoot 'tests\syft-output-transaction-selftest.ps1')

    # Safe mocked policy calls prove absent-root enforcement, all overlap
    # boundaries, cleanup on no-prior/error, and live lease transfer on prior.
    $gateFixture = Join-Path $scratch 'upgrade-gate'
    $gateRuntime = Join-Path $gateFixture 'runtime'
    New-Item -ItemType Directory -Path $gateRuntime -Force | Out-Null
    $currentMsi = Join-Path $gateFixture 'current.msi'
    $manifest = Join-Path $gateFixture 'runtime-files.txt'
    $evidence = Join-Path $gateFixture 'evidence.json'
    $downloadRoot = Join-Path $gateFixture 'download'
    [IO.File]::WriteAllText($currentMsi, 'current-msi', $utf8)
    [IO.File]::WriteAllText($manifest, "DesktopPet.exe`n", $utf8)
    $gateParameters = @{
        Repository = 'bigfnj/desktopPet'
        CurrentReleaseTag = 'v9.8.7'
        CurrentMsiPath = $currentMsi
        CurrentRuntimeRoot = $gateRuntime
        RuntimeManifestPath = $manifest
        EvidencePath = $evidence
        DownloadRoot = $downloadRoot
        GitHubToken = 'mocked-token'
    }

    $gateOutside = Join-Path $gateFixture 'outside'
    New-Item -ItemType Directory -Path $gateOutside | Out-Null
    $gateOutsideSentinel = Join-Path $gateOutside 'must-survive.txt'
    [IO.File]::WriteAllText($gateOutsideSentinel, 'gate-sentinel', $utf8)
    $gateOutsideHash = (
        Get-FileHash -LiteralPath $gateOutsideSentinel -Algorithm SHA256).Hash
    $downloadMoved = Join-Path $gateFixture 'download-moved'
    $script:publishHookObserved = $false
    $script:publishRenameBlocked = $false
    $script:publishDeleteBlocked = $false
    $script:evidenceWriteHookObserved = $false
    $script:evidenceStageRenameBlocked = $false
    $script:evidenceStageDeleteBlocked = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($operation, $path)
        if ($operation -ceq 'before-nminusone-evidence-write') {
            $script:evidenceWriteHookObserved = $true
            $evidenceStage = Split-Path -Parent $path
            try {
                Move-Item `
                    -LiteralPath $evidenceStage `
                    -Destination ($evidenceStage + '-moved') `
                    -ErrorAction Stop
            }
            catch {
                $script:evidenceStageRenameBlocked = $true
            }
            try {
                Remove-Item `
                    -LiteralPath $evidenceStage `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            }
            catch {
                $script:evidenceStageDeleteBlocked = $true
            }
            return
        }
        if ($operation -cne 'publish') {
            return
        }
        $script:publishHookObserved = $true
        try {
            Move-Item `
                -LiteralPath $downloadRoot `
                -Destination $downloadMoved `
                -ErrorAction Stop
        }
        catch {
            $script:publishRenameBlocked = $true
        }
        try {
            Remove-Item `
                -LiteralPath $downloadRoot `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
        catch {
            $script:publishDeleteBlocked = $true
        }
    }
    $noPriorContext = Invoke-MockedNoPriorPolicy -Parameters $gateParameters
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $noPriorContext.IsComplete -or
        $null -ne $noPriorContext.DownloadRootLease -or
        -not $script:evidenceWriteHookObserved -or
        -not $script:evidenceStageRenameBlocked -or
        -not $script:evidenceStageDeleteBlocked -or
        -not $script:publishHookObserved -or
        -not $script:publishRenameBlocked -or
        -not $script:publishDeleteBlocked -or
        (Test-Path -LiteralPath $downloadRoot) -or
        (Test-Path -LiteralPath $downloadMoved)) {
        throw (
            'No-prior N-1 policy did not retain its scratch lease through ' +
            'publication and cleanly release/remove the owned root.')
    }
    if ((Get-FileHash `
            -LiteralPath $gateOutsideSentinel `
            -Algorithm SHA256).Hash -cne $gateOutsideHash) {
        throw 'N-1 DownloadRoot mutation probes changed the outside sentinel.'
    }

    New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    $preExistingDownloadSentinel =
        Join-Path $downloadRoot 'must-survive.txt'
    [IO.File]::WriteAllText(
        $preExistingDownloadSentinel,
        'unowned-download-data',
        $utf8)
    Assert-Rejected `
        -Name 'pre-existing N-1 DownloadRoot' `
        -ExpectedMessage '(?i)must be absent and caller-owned' `
        -Action {
            [void](Invoke-MockedNoPriorPolicy -Parameters $gateParameters)
        }
    if ([IO.File]::ReadAllText($preExistingDownloadSentinel) -cne
        'unowned-download-data') {
        throw 'Rejected pre-existing DownloadRoot was modified or adopted.'
    }
    Remove-DesktopPetSafeDirectory `
        -Path $downloadRoot `
        -AllowedRoot $gateFixture `
        -TrustedRoot $gateFixture

    foreach ($overlap in @(
            [pscustomobject]@{
                Name = 'repository child'
                Path = $repoProbe
                Message = '(?i)overlaps a protected path'
            },
            [pscustomobject]@{
                Name = 'runtime root'
                Path = $gateRuntime
                Message = '(?i)overlaps the current runtime root'
            },
            [pscustomobject]@{
                Name = 'runtime ancestor'
                Path = $gateFixture
                Message = '(?i)overlaps the current runtime root'
            },
            [pscustomobject]@{
                Name = 'current MSI'
                Path = $currentMsi
                Message = '(?i)overlaps a protected path'
            },
            [pscustomobject]@{
                Name = 'runtime manifest'
                Path = $manifest
                Message = '(?i)overlaps a protected path'
            },
            [pscustomobject]@{
                Name = 'evidence output'
                Path = $evidence
                Message = '(?i)overlaps a protected path'
            })) {
        $parameters = $gateParameters.Clone()
        $parameters.DownloadRoot = [string]$overlap.Path
        Assert-Rejected `
            -Name ("N-1 DownloadRoot overlap: " + $overlap.Name) `
            -ExpectedMessage ([string]$overlap.Message) `
            -Action {
                [void](Invoke-MockedNoPriorPolicy -Parameters $parameters)
            }
    }

    $priorDownloadRoot = Join-Path $gateFixture 'prior-download'
    $priorParameters = $gateParameters.Clone()
    $priorParameters.DownloadRoot = $priorDownloadRoot
    $priorContext = Invoke-MockedPriorPolicy -Parameters $priorParameters
    $transferredLease = $priorContext.DownloadRootLease
    $transferredMsiInput = $priorContext.CurrentMsiInput
    if ($priorContext.IsComplete -or
        $null -eq $transferredLease -or
        $null -eq $transferredMsiInput) {
        throw (
            'Prior-release policy did not transfer live DownloadRoot and ' +
            'current-MSI leases.')
    }
    Assert-Rejected `
        -Name 'transferred N-1 DownloadRoot rename' `
        -ExpectedMessage '(?i)used by another process|access.*denied' `
        -Action {
            Move-Item `
                -LiteralPath $priorDownloadRoot `
                -Destination (Join-Path $gateFixture 'prior-download-moved') `
                -ErrorAction Stop
        }
    Assert-Rejected `
        -Name 'transferred N-1 DownloadRoot deletion' `
        -ExpectedMessage '(?i)used by another process|access.*denied' `
        -Action {
            Remove-Item `
                -LiteralPath $priorDownloadRoot `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
    $transferredLease.Dispose()
    $transferredLease = $null
    $transferredMsiInput.Dispose()
    $transferredMsiInput = $null
    Remove-DesktopPetSafeDirectory `
        -Path $priorDownloadRoot `
        -AllowedRoot $gateFixture `
        -TrustedRoot $gateFixture

    Write-Host (
        'PASS: scratch roots are absent and non-overlapping; atomically ' +
        'created roots retain leaf/ancestor rename-delete reservations through ' +
        'Syft/N-1 work; unowned roots and external sentinels survive; leases ' +
        'release deterministically after completion.') -ForegroundColor Green
}
finally {
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if ($null -ne $transferredLease) {
        $transferredLease.Dispose()
    }
    if ($null -ne $transferredMsiInput) {
        $transferredMsiInput.Dispose()
    }
    if (Test-Path -LiteralPath $repoProbe) {
        throw "Scratch ownership test unexpectedly created repo path: $repoProbe"
    }
    if (Test-Path -LiteralPath $scratch) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
