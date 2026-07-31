#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$lifecyclePath =
    Join-Path $repoRoot 'packaging\Test-MsiLifecycle.ps1'
$workflowPath =
    Join-Path $repoRoot '.github\workflows\release.yml'
$packagedPayloadPath =
    Join-Path $repoRoot 'packaging\Test-PackagedPayloads.ps1'
$lifecycle = Get-Content -LiteralPath $lifecyclePath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$packagedPayload = Get-Content -LiteralPath $packagedPayloadPath -Raw
. (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')

foreach ($requiredContract in @(
        '[switch]$UseNonReleaseMutatedMsi',
        '[switch]$RequireValidSignature',
        '$executionMsi = $absoluteMsi',
        'if ($UseNonReleaseMutatedMsi)',
        'New-NonReleaseMutatedLifecycleMsi',
        'Get-AuthenticodeSignature -LiteralPath $Path',
        'Get-FileHash -LiteralPath $Path -Algorithm SHA256',
        '$originalMsiLease = Open-DesktopPetValidatedInputFile',
        '$executionMsiLease = Open-DesktopPetValidatedInputFile',
        '''msi-lifecycle-before-msiexec-start''',
        '$isolationArguments',
        'MSI lifecycle changed original artifact identity field')) {
    if (-not $lifecycle.Contains($requiredContract)) {
        throw "MSI lifecycle exact-artifact contract is missing: $requiredContract"
    }
}

$preservationAssertions = [regex]::Matches(
    $lifecycle,
    '(?m)^\s*Assert-OriginalMsiPreserved\s*$')
if ($preservationAssertions.Count -lt 6) {
    throw (
        'MSI lifecycle must assert original hash/signature preservation before ' +
        'and after install, repair, uninstall, and cleanup.'
    )
}
if ($lifecycle -match
    '(?s)if\s*\(\s*\$UseIsolatedInstallRoot\s*\)\s*\{.{0,600}New-NonReleaseMutatedLifecycleMsi') {
    throw (
        '-UseIsolatedInstallRoot must not select the MSI database-mutation path.'
    )
}
$handoffHookIndex = $lifecycle.IndexOf(
    "'msi-lifecycle-before-msiexec-start'",
    [StringComparison]::Ordinal)
$processStartIndex = if ($handoffHookIndex -ge 0) {
    $lifecycle.IndexOf(
        '$process.Start()',
        $handoffHookIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
if ($handoffHookIndex -lt 0 -or $processStartIndex -le $handoffHookIndex) {
    throw 'MSI lifecycle mutation control is not immediately before handoff.'
}
foreach ($packagedPayloadContract in @(
        '$msiInput = Open-DesktopPetValidatedInputFile',
        '''packaged-payload-before-msiexec-start''')) {
    if (-not $packagedPayload.Contains($packagedPayloadContract)) {
        throw (
            'Administrative MSI extraction is missing retained-identity ' +
            "handoff protection: $packagedPayloadContract")
    }
}
if ($workflow -notmatch
    '(?s)Test-MsiLifecycle\.ps1.{0,180}-UseIsolatedInstallRoot\s*`?\s*-RequireValidSignature') {
    throw (
        'Final signed-MSI lifecycle workflow does not require exact valid ' +
        'Authenticode preservation.'
    )
}

$retentionScratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-MsiRetention-' + [Guid]::NewGuid().ToString('N'))
$retainedMsi = Join-Path $retentionScratch 'authenticated.msi'
$retainedLease = $null
try {
    [IO.Directory]::CreateDirectory($retentionScratch) | Out-Null
    [IO.File]::WriteAllText(
        $retainedMsi,
        'authenticated-msi-fixture',
        (New-Object Text.UTF8Encoding($false)))
    $retainedLease = Open-DesktopPetValidatedInputFile `
        -Path $retainedMsi `
        -Root $retentionScratch
    $writeBlocked = $false
    $moveBlocked = $false
    try {
        [IO.File]::WriteAllText($retainedMsi, 'substituted')
    }
    catch {
        $writeBlocked = $true
    }
    try {
        Move-Item `
            -LiteralPath $retainedMsi `
            -Destination ($retainedMsi + '.substituted') `
            -ErrorAction Stop
    }
    catch {
        $moveBlocked = $true
    }
    if (-not $writeBlocked -or -not $moveBlocked -or
        $retainedLease.ComputeHash('SHA256') -cne
            (Get-FileHash -LiteralPath $retainedMsi -Algorithm SHA256).Hash) {
        throw (
            'A retained authenticated MSI identity did not block write/rename ' +
            'substitution before a process handoff.')
    }
}
finally {
    if ($null -ne $retainedLease) {
        $retainedLease.Dispose()
    }
    if (Test-Path -LiteralPath $retentionScratch) {
        [IO.Directory]::Delete($retentionScratch, $true)
    }
}

Write-Host (
    'PASS: release lifecycle selects the original MSI, applies public isolation ' +
    'arguments to its common operation path, preserves hash/signature, and ' +
    'gates mutation behind the explicit non-release switch.'
) -ForegroundColor Green
