#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Staging path-safety self-test requires Windows junction support.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$policyPath = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
. $policyPath
$msiNormalizer =
    Join-Path $repoRoot 'packaging\Normalize-MsiDeterminism.ps1'

$tempRoot = Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
$scratchRoot = Join-Path $tempRoot (
    'DesktopPet-StagingPathSafety-' + [Guid]::NewGuid().ToString('N'))
$trustedRoot = Join-Path $scratchRoot 'trusted'
$outsideRoot = Join-Path $scratchRoot 'outside-target'
$outsideSentinel = Join-Path $outsideRoot 'outside-sentinel.txt'
$script:testJunctions = @()

function New-TestJunction {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Target
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Test junction path already exists: $Path"
    }
    $junction = New-Item `
        -ItemType Junction `
        -Path $Path `
        -Target $Target `
        -ErrorAction Stop
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Test path was not created as a reparse point: $Path"
    }
    $script:testJunctions += [IO.Path]::GetFullPath($Path)
}

function Remove-TestJunction {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Refusing to remove a non-junction test path: $Path"
    }
    # Directory.Delete removes the junction entry itself and never traverses it.
    [IO.Directory]::Delete($item.FullName)
}

function Assert-ReparseRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $accepted = $true
    $message = ''
    try {
        & $Action
    }
    catch {
        $accepted = $false
        $message = $_.Exception.Message
    }
    if ($accepted) {
        throw "Unsafe staging reparse case was accepted: $Name"
    }
    if ($message -notmatch '(?i)reparse point') {
        throw (
            "Staging reparse case '$Name' failed for an unexpected reason: " +
            $message)
    }
}

function Assert-OutsideSentinelPreserved {
    if (-not (Test-Path -LiteralPath $outsideSentinel -PathType Leaf)) {
        throw 'A rejected staging operation modified the junction target.'
    }
    $content = [IO.File]::ReadAllText($outsideSentinel)
    if ($content -cne 'outside-target-must-survive') {
        throw 'The outside junction target sentinel content changed.'
    }
}

function Set-AncestorSwapHook {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Moved
    )

    $script:swapOperation = $Operation
    $script:swapSource = $Source
    $script:swapMoved = $Moved
    $script:swapAttempted = $false
    $script:swapSucceeded = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($script:swapAttempted -or
            $observedOperation -cne $script:swapOperation) {
            return
        }
        $script:swapAttempted = $true
        try {
            [IO.Directory]::Move(
                $script:swapSource,
                $script:swapMoved)
            $script:swapSucceeded = $true
            New-TestJunction `
                -Path $script:swapSource `
                -Target $outsideRoot
        }
        catch {
            $script:swapSucceeded = $false
        }
    }
}

function Assert-AncestorSwapBlocked {
    param([Parameter(Mandatory = $true)][string]$Name)

    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:swapAttempted) {
        throw "Retained-handle race barrier was not reached: $Name"
    }
    if ($script:swapSucceeded) {
        if (Test-Path -LiteralPath $script:swapSource) {
            Remove-TestJunction -Path $script:swapSource
        }
        if (Test-Path -LiteralPath $script:swapMoved -PathType Container) {
            [IO.Directory]::Move(
                $script:swapMoved,
                $script:swapSource)
        }
        throw "Ancestor swap succeeded during retained-handle mutation: $Name"
    }
    if (-not (Test-Path -LiteralPath $script:swapSource -PathType Container) -or
        (Test-Path -LiteralPath $script:swapMoved)) {
        throw "Blocked ancestor swap changed its fixture paths: $Name"
    }
    Assert-OutsideSentinelPreserved
}

function Set-PreLeaseSwapHook {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Moved
    )

    $script:preLeaseOperation = $Operation
    $script:preLeaseSource = $Source
    $script:preLeaseMoved = $Moved
    $script:preLeaseSwapCompleted = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($observedOperation -cne $script:preLeaseOperation) {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        [IO.Directory]::Move(
            $script:preLeaseSource,
            $script:preLeaseMoved)
        New-TestJunction `
            -Path $script:preLeaseSource `
            -Target $outsideRoot
        $script:preLeaseSwapCompleted = $true
    }
}

function Restore-PreLeaseSwapFixture {
    param([Parameter(Mandatory = $true)][string]$Name)

    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if (-not $script:preLeaseSwapCompleted) {
        throw "Pre-lease ancestor swap hook was not reached: $Name"
    }
    Remove-TestJunction -Path $script:preLeaseSource
    $script:testJunctions = @(
        $script:testJunctions |
            Where-Object {
                -not $_.Equals(
                    [IO.Path]::GetFullPath($script:preLeaseSource),
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    [IO.Directory]::Move(
        $script:preLeaseMoved,
        $script:preLeaseSource)
    Assert-OutsideSentinelPreserved
}

try {
    New-Item -ItemType Directory `
        -Path $trustedRoot, $outsideRoot `
        -Force | Out-Null
    [IO.File]::WriteAllText(
        $outsideSentinel,
        'outside-target-must-survive',
        (New-Object Text.UTF8Encoding($false)))

    $allowedJunction = Join-Path $trustedRoot 'build-junction'
    New-TestJunction -Path $allowedJunction -Target $outsideRoot
    Assert-ReparseRejected -Name 'allowed-root-junction' -Action {
        Reset-DesktopPetStagingDirectory `
            -Path (Join-Path $allowedJunction 'stage') `
            -AllowedRoot $allowedJunction `
            -TrustedRoot $trustedRoot
    }
    Assert-OutsideSentinelPreserved
    Remove-TestJunction -Path $allowedJunction

    # Mirrors release SBOM staging: the allowed build root itself is an
    # attacker-controlled ancestor junction above build\sbom-input.
    $workflowBuildJunction = Join-Path $trustedRoot 'workflow-build'
    New-TestJunction -Path $workflowBuildJunction -Target $outsideRoot
    Assert-ReparseRejected -Name 'release-build-ancestor-junction' -Action {
        Reset-DesktopPetStagingDirectory `
            -Path (Join-Path $workflowBuildJunction 'sbom-input') `
            -AllowedRoot $workflowBuildJunction `
            -TrustedRoot $trustedRoot
    }
    Assert-OutsideSentinelPreserved
    Remove-TestJunction -Path $workflowBuildJunction

    $allowedRoot = Join-Path $trustedRoot 'build'
    New-Item -ItemType Directory -Path $allowedRoot -Force | Out-Null
    $intermediateJunction = Join-Path $allowedRoot 'intermediate'
    New-TestJunction -Path $intermediateJunction -Target $outsideRoot
    Assert-ReparseRejected -Name 'intermediate-junction' -Action {
        Reset-DesktopPetStagingDirectory `
            -Path (Join-Path $intermediateJunction 'stage') `
            -AllowedRoot $allowedRoot `
            -TrustedRoot $trustedRoot
    }
    Assert-OutsideSentinelPreserved
    Remove-TestJunction -Path $intermediateJunction

    $hardLinkOutput = Join-Path $allowedRoot 'hard-link-output.txt'
    New-Item `
        -ItemType HardLink `
        -Path $hardLinkOutput `
        -Target $outsideSentinel `
        -ErrorAction Stop | Out-Null
    $hardLinkAccepted = $true
    try {
        Assert-DesktopPetOutputFileSafe `
            -Path $hardLinkOutput `
            -TrustedRoot $allowedRoot `
            -ProtectedPaths @($outsideSentinel) | Out-Null
    }
    catch {
        $hardLinkAccepted = $false
        if ($_.Exception.Message -notmatch '(?i)hard-link alias') {
            throw
        }
    }
    if ($hardLinkAccepted) {
        throw 'A hard-link alias to a protected input was accepted as output.'
    }
    Assert-OutsideSentinelPreserved
    Remove-Item -LiteralPath $hardLinkOutput -Force
    Assert-OutsideSentinelPreserved

    $sameParentTemporary = Join-Path $allowedRoot 'same-parent.tmp'
    $sameParentDestination = Join-Path $allowedRoot 'same-parent.txt'
    [IO.File]::WriteAllText($sameParentTemporary, 'new-bytes')
    [IO.File]::WriteAllText($sameParentDestination, 'original-bytes')
    $sameParentRejected = $false
    try {
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $sameParentTemporary `
            -DestinationPath $sameParentDestination `
            -TrustedRoot $allowedRoot)
    }
    catch {
        $sameParentRejected =
            $_.Exception.Message -match '(?i)separate private staging directory'
    }
    if (-not $sameParentRejected -or
        [IO.File]::ReadAllText($sameParentTemporary) -cne 'new-bytes' -or
        [IO.File]::ReadAllText($sameParentDestination) -cne 'original-bytes') {
        throw (
            'Atomic publication accepted a shared-parent temporary file or ' +
            'modified a same-parent rejection fixture.')
    }

    $hardLinkMsi = Join-Path $allowedRoot 'hard-link-input.msi'
    New-Item `
        -ItemType HardLink `
        -Path $hardLinkMsi `
        -Target $outsideSentinel `
        -ErrorAction Stop | Out-Null
    $normalizerFailure = $null
    try {
        & $msiNormalizer `
            -MsiPath $hardLinkMsi `
            -IdentityNamespace 'desktop-pet-selftest' *> $null
    }
    catch {
        $normalizerFailure = $_
    }
    if ($null -eq $normalizerFailure -or
        $normalizerFailure.Exception.Message -notmatch
            '(?i)hard-link alias') {
        $detail = if ($null -eq $normalizerFailure) {
            'accepted'
        }
        else {
            $normalizerFailure.Exception.Message
        }
        throw (
            'MSI normalizer did not reject a hard-link input alias: ' +
            $detail)
    }
    Assert-OutsideSentinelPreserved
    Remove-Item -LiteralPath $hardLinkMsi -Force
    Assert-OutsideSentinelPreserved

    $malformedMsi = Join-Path $allowedRoot 'malformed-input.msi'
    [IO.File]::WriteAllBytes(
        $malformedMsi,
        [Text.Encoding]::ASCII.GetBytes(
            'malformed-msi-must-survive-failed-normalization'))
    $malformedHash = (
        Get-FileHash -LiteralPath $malformedMsi -Algorithm SHA256).Hash
    $normalizerFailure = $null
    try {
        & $msiNormalizer `
            -MsiPath $malformedMsi `
            -IdentityNamespace 'desktop-pet-selftest' *> $null
    }
    catch {
        $normalizerFailure = $_
    }
    if ($null -eq $normalizerFailure) {
        throw 'MSI normalizer accepted a malformed normal .msi input.'
    }
    if ((Get-FileHash `
            -LiteralPath $malformedMsi `
            -Algorithm SHA256).Hash -cne $malformedHash) {
        throw 'Failed MSI normalization modified the malformed input bytes.'
    }
    if (@(Get-ChildItem `
            -LiteralPath $allowedRoot `
            -Directory `
            -Filter '.DesktopPet-msi-normalize-*').Count -ne 0) {
        throw 'Failed MSI normalization left a private staging directory.'
    }

    $stage = Join-Path $allowedRoot 'existing-stage'
    Reset-DesktopPetStagingDirectory `
        -Path $stage `
        -AllowedRoot $allowedRoot `
        -TrustedRoot $trustedRoot
    $localSentinel = Join-Path $stage 'local-sentinel.txt'
    [IO.File]::WriteAllText(
        $localSentinel,
        'local-content-must-survive-rejection',
        (New-Object Text.UTF8Encoding($false)))
    $nestedJunction = Join-Path $stage 'nested-junction'
    New-TestJunction -Path $nestedJunction -Target $outsideRoot
    Assert-ReparseRejected -Name 'nested-staging-junction' -Action {
        Reset-DesktopPetStagingDirectory `
            -Path $stage `
            -AllowedRoot $allowedRoot `
            -TrustedRoot $trustedRoot
    }
    Assert-OutsideSentinelPreserved
    if (-not (Test-Path -LiteralPath $localSentinel -PathType Leaf)) {
        throw 'Nested-junction rejection partially deleted the staging tree.'
    }
    Remove-TestJunction -Path $nestedJunction

    $nestedDirectory = Join-Path $stage 'normal-child'
    New-Item -ItemType Directory -Path $nestedDirectory -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $nestedDirectory 'normal-file.txt'),
        'normal-content',
        (New-Object Text.UTF8Encoding($false)))
    Reset-DesktopPetStagingDirectory `
        -Path $stage `
        -AllowedRoot $allowedRoot `
        -TrustedRoot $trustedRoot
    if (-not (Test-Path -LiteralPath $stage -PathType Container)) {
        throw 'Normal staging reset did not recreate the directory.'
    }
    if (@(Get-ChildItem -LiteralPath $stage -Force).Count -ne 0) {
        throw 'Normal staging reset did not recreate an empty directory.'
    }

    $publishAncestor = Join-Path $allowedRoot 'publish-race-parent'
    $publishMoved = Join-Path $allowedRoot 'publish-race-parent-moved'
    $publishPrivate = Join-Path $publishAncestor 'private'
    $publishFinal = Join-Path $publishAncestor 'final'
    New-Item -ItemType Directory `
        -Path $publishPrivate, $publishFinal | Out-Null
    $publishTemporary = Join-Path $publishPrivate 'artifact.tmp'
    $publishDestination = Join-Path $publishFinal 'artifact.bin'
    [IO.File]::WriteAllText($publishTemporary, 'new-published-bytes')
    [IO.File]::WriteAllText($publishDestination, 'old-published-bytes')
    Set-AncestorSwapHook `
        -Operation 'publish' `
        -Source $publishAncestor `
        -Moved $publishMoved
    [void](Publish-DesktopPetAtomicFile `
        -TemporaryPath $publishTemporary `
        -DestinationPath $publishDestination `
        -TrustedRoot $allowedRoot)
    Assert-AncestorSwapBlocked -Name 'atomic-publication'
    if ([IO.File]::ReadAllText($publishDestination) -cne
        'new-published-bytes') {
        throw 'Retained-handle atomic publication produced wrong bytes.'
    }

    $deleteAncestor = Join-Path $allowedRoot 'delete-race-parent'
    $deleteMoved = Join-Path $allowedRoot 'delete-race-parent-moved'
    $deleteTarget = Join-Path $deleteAncestor 'stage'
    New-Item -ItemType Directory -Path $deleteTarget | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $deleteTarget 'payload.bin'),
        'delete-me')
    Set-AncestorSwapHook `
        -Operation 'delete' `
        -Source $deleteAncestor `
        -Moved $deleteMoved
    Remove-DesktopPetSafeDirectory `
        -Path $deleteTarget `
        -AllowedRoot $allowedRoot `
        -TrustedRoot $trustedRoot
    Assert-AncestorSwapBlocked -Name 'retained-handle-deletion'
    if (Test-Path -LiteralPath $deleteTarget) {
        throw 'Retained-handle deletion left its target behind.'
    }

    $fileDeleteAncestor = Join-Path $allowedRoot 'file-delete-race-parent'
    $fileDeleteMoved = Join-Path $allowedRoot 'file-delete-race-parent-moved'
    $fileDeleteTarget = Join-Path $fileDeleteAncestor 'stale.wixpdb'
    New-Item -ItemType Directory -Path $fileDeleteAncestor | Out-Null
    [IO.File]::WriteAllText($fileDeleteTarget, 'delete-this-stale-file')
    Set-AncestorSwapHook `
        -Operation 'delete-file' `
        -Source $fileDeleteAncestor `
        -Moved $fileDeleteMoved
    Remove-DesktopPetSafeFile `
        -Path $fileDeleteTarget `
        -AllowedRoot $allowedRoot `
        -TrustedRoot $trustedRoot
    Assert-AncestorSwapBlocked -Name 'retained-handle-file-deletion'
    if (Test-Path -LiteralPath $fileDeleteTarget) {
        throw 'Retained-handle file deletion left its target behind.'
    }

    $createAncestor = Join-Path $allowedRoot 'create-race-parent'
    $createMoved = Join-Path $allowedRoot 'create-race-parent-moved'
    $createTarget = Join-Path $createAncestor 'stage'
    New-Item -ItemType Directory -Path $createAncestor | Out-Null
    Set-AncestorSwapHook `
        -Operation 'create-staging-root' `
        -Source $createAncestor `
        -Moved $createMoved
    Reset-DesktopPetStagingDirectory `
        -Path $createTarget `
        -AllowedRoot $createAncestor `
        -TrustedRoot $trustedRoot
    Assert-AncestorSwapBlocked -Name 'retained-parent-creation'
    if (-not (Test-Path -LiteralPath $createTarget -PathType Container)) {
        throw 'Retained-parent directory creation did not create its target.'
    }

    $prePublishPrivate = Join-Path $allowedRoot 'pre-publish-private'
    $prePublishAncestor = Join-Path $allowedRoot 'pre-publish-parent'
    $prePublishMoved = Join-Path $allowedRoot 'pre-publish-parent-moved'
    $prePublishFinal = Join-Path $prePublishAncestor 'final'
    New-Item -ItemType Directory `
        -Path $prePublishPrivate, $prePublishFinal | Out-Null
    $prePublishTemporary = Join-Path $prePublishPrivate 'artifact.tmp'
    $prePublishDestination = Join-Path $prePublishFinal 'artifact.bin'
    [IO.File]::WriteAllText($prePublishTemporary, 'new-pre-lease-bytes')
    [IO.File]::WriteAllText($prePublishDestination, 'old-pre-lease-bytes')
    Set-PreLeaseSwapHook `
        -Operation 'before-publish-lease' `
        -Source $prePublishAncestor `
        -Moved $prePublishMoved
    Assert-ReparseRejected -Name 'pre-publication-lease-swap' -Action {
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $prePublishTemporary `
            -DestinationPath $prePublishDestination `
            -TrustedRoot $allowedRoot)
    }
    Restore-PreLeaseSwapFixture -Name 'pre-publication-lease-swap'
    if ([IO.File]::ReadAllText($prePublishDestination) -cne
        'old-pre-lease-bytes') {
        throw 'Pre-lease publication rejection changed destination bytes.'
    }

    $preDeleteAllowed = Join-Path $trustedRoot 'pre-delete-allowed'
    $preDeleteMoved = Join-Path $trustedRoot 'pre-delete-allowed-moved'
    $preDeleteTarget = Join-Path $preDeleteAllowed 'stage'
    New-Item -ItemType Directory -Path $preDeleteTarget | Out-Null
    $preDeletePayload = Join-Path $preDeleteTarget 'payload.bin'
    [IO.File]::WriteAllText($preDeletePayload, 'must-survive-pre-lease')
    Set-PreLeaseSwapHook `
        -Operation 'before-delete-lease' `
        -Source $preDeleteAllowed `
        -Moved $preDeleteMoved
    Assert-ReparseRejected -Name 'pre-deletion-lease-swap' -Action {
        Remove-DesktopPetSafeDirectory `
            -Path $preDeleteTarget `
            -AllowedRoot $preDeleteAllowed `
            -TrustedRoot $trustedRoot
    }
    Restore-PreLeaseSwapFixture -Name 'pre-deletion-lease-swap'
    if ([IO.File]::ReadAllText($preDeletePayload) -cne
        'must-survive-pre-lease') {
        throw 'Pre-lease deletion rejection changed target bytes.'
    }

    $preFileDeleteAllowed = Join-Path $trustedRoot 'pre-file-delete-allowed'
    $preFileDeleteMoved = Join-Path $trustedRoot 'pre-file-delete-allowed-moved'
    $preFileDeleteTarget = Join-Path $preFileDeleteAllowed 'stale.wixpdb'
    New-Item -ItemType Directory -Path $preFileDeleteAllowed | Out-Null
    [IO.File]::WriteAllText(
        $preFileDeleteTarget,
        'must-survive-file-pre-lease')
    Set-PreLeaseSwapHook `
        -Operation 'before-delete-file-lease' `
        -Source $preFileDeleteAllowed `
        -Moved $preFileDeleteMoved
    Assert-ReparseRejected -Name 'pre-file-deletion-lease-swap' -Action {
        Remove-DesktopPetSafeFile `
            -Path $preFileDeleteTarget `
            -AllowedRoot $preFileDeleteAllowed `
            -TrustedRoot $trustedRoot
    }
    Restore-PreLeaseSwapFixture -Name 'pre-file-deletion-lease-swap'
    if ([IO.File]::ReadAllText($preFileDeleteTarget) -cne
        'must-survive-file-pre-lease') {
        throw 'Pre-lease file-deletion rejection changed target bytes.'
    }

    $preCreateAllowed = Join-Path $trustedRoot 'pre-create-allowed'
    $preCreateMoved = Join-Path $trustedRoot 'pre-create-allowed-moved'
    $preCreateTarget = Join-Path $preCreateAllowed 'stage'
    New-Item -ItemType Directory -Path $preCreateAllowed | Out-Null
    Set-PreLeaseSwapHook `
        -Operation 'before-create-staging-root-lease' `
        -Source $preCreateAllowed `
        -Moved $preCreateMoved
    Assert-ReparseRejected -Name 'pre-creation-lease-swap' -Action {
        Reset-DesktopPetStagingDirectory `
            -Path $preCreateTarget `
            -AllowedRoot $preCreateAllowed `
            -TrustedRoot $trustedRoot
    }
    Restore-PreLeaseSwapFixture -Name 'pre-creation-lease-swap'
    if (Test-Path -LiteralPath $preCreateTarget) {
        throw 'Pre-lease creation rejection created its target.'
    }

    $competingLeafParent = Join-Path $allowedRoot 'competing-leaf-parent'
    $competingLeafTarget = Join-Path $competingLeafParent 'stage'
    New-Item -ItemType Directory -Path $competingLeafParent | Out-Null
    $script:competingLeafInserted = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($observedOperation -cne 'before-create-staging-root-lease') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        New-TestJunction `
            -Path $competingLeafTarget `
            -Target $outsideRoot
        $script:competingLeafInserted = $true
    }
    $competingLeafAccepted = $true
    try {
        Reset-DesktopPetStagingDirectory `
            -Path $competingLeafTarget `
            -AllowedRoot $competingLeafParent `
            -TrustedRoot $trustedRoot
    }
    catch {
        $competingLeafAccepted = $false
    }
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if ($competingLeafAccepted -or
        -not $script:competingLeafInserted) {
        throw 'Competing creation-leaf junction was not rejected.'
    }
    Remove-TestJunction -Path $competingLeafTarget
    $script:testJunctions = @(
        $script:testJunctions |
            Where-Object {
                -not $_.Equals(
                    [IO.Path]::GetFullPath($competingLeafTarget),
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    Assert-OutsideSentinelPreserved

    $escapeAccepted = $true
    try {
        Reset-DesktopPetStagingDirectory `
            -Path (Join-Path $trustedRoot 'lexical-escape') `
            -AllowedRoot $allowedRoot `
            -TrustedRoot $trustedRoot
    }
    catch {
        $escapeAccepted = $false
        if ($_.Exception.Message -notmatch '(?i)outside allowed staging root') {
            throw
        }
    }
    if ($escapeAccepted) {
        throw 'Lexically out-of-root staging path was accepted.'
    }
    Assert-OutsideSentinelPreserved

    $interopSmoke = Join-Path $PSScriptRoot (
        'staging-path-safety-interop-smoke.ps1')
    $nativePowerShell = Join-Path $PSHOME 'powershell.exe'
    & $nativePowerShell `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $interopSmoke
    if ($LASTEXITCODE -ne 0) {
        throw "Fresh-process native interop smoke failed: $LASTEXITCODE"
    }
    $x86PowerShell = Join-Path $env:SystemRoot (
        'SysWOW64\WindowsPowerShell\v1.0\powershell.exe')
    if ([Environment]::Is64BitProcess -and
        (Test-Path -LiteralPath $x86PowerShell -PathType Leaf)) {
        & $x86PowerShell `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $interopSmoke
        if ($LASTEXITCODE -ne 0) {
            throw "Fresh-process x86 interop smoke failed: $LASTEXITCODE"
        }
    }

    Write-Host (
        'PASS: staging reset rejects hard-link aliases plus allowed-root, ' +
        'release-build-ancestor, intermediate, and nested junctions; MSI ' +
        'normalization rejects linked/malformed inputs without modification; ' +
        'atomic publication requires private staging; retained handles block ' +
        'in-operation ancestor swaps; pre-lease swaps fail closed for publish, ' +
        'delete, and create; external targets survive; and normal trees reset ' +
        'safely.'
    ) -ForegroundColor Green
}
finally {
    Remove-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    foreach ($junctionPath in @($script:testJunctions | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $junctionPath) {
            Remove-TestJunction -Path $junctionPath
        }
    }
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratchRoot `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
