[CmdletBinding()]
param(
    # Accepted for CI compatibility but unused: this script now performs only source-text invariant
    # checks (it reads .cs files, no assembly load). The reflection/runtime half moved in-process to
    # the app's --hardening-selftest flag (no PowerShell hosts a net10 assembly).
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool] $Condition, [string] $Name)
    if (-not $Condition) { throw "$Name failed." }
    Write-Host "PASS: $Name"
}

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot

$formPetSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormPet.cs') -Raw
$formSpeechSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\FormSpeech.cs') -Raw
$aiBrainSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Ai\AiBrain.cs') -Raw
$startUpSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw
$formOptionsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Portable\FormOptions.cs') -Raw
$contextMenuSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\ContextMenus.cs') -Raw

Assert-True (
    $formPetSource -match
        '(?s)Timer1_Tick\(.*?CheckFullScreen\(\);\s*NextStep\(\);' -and
    $formPetSource.Contains('_speech.SetFullscreenSuppressed(') -and
    $formSpeechSource.Contains(
        'internal void SetFullscreenSuppressed(bool suppressed)') -and
    -not $formSpeechSource.Contains(
        'cp.ExStyle |= 0x00000008')
) 'stationary fullscreen polling and speech z-order propagation'
Assert-True (
    $aiBrainSource.Contains('CaptureScreen(captureBounds, 1280)') -and
    $aiBrainSource.Contains('ComputeSignature(captureBounds)') -and
    $startUpSource.Contains('ActiveWindow.CaptureContext(') -and
    $startUpSource.Contains('captureContext.MonitorBounds')
) 'AI capture and idle change detection share the selected monitor'
Assert-True (
    $formPetSource.Contains('rctO.Right <= rctO.Left') -and
    $formPetSource.Contains('rctO.Bottom <= rctO.Top') -and
    $formPetSource.Contains('DesktopGeometry.TryScaleWindowRelativeX(')
) 'window following rejects collapsed rectangles and uses safe relative scaling'

$buildAiTab = [regex]::Match(
    $formOptionsSource,
    '(?s)private void BuildAiTab\(\)\s*\{(?<body>.*?)' +
        '\r?\n\s*\}\s*\r?\n\s*private async Task ClearAiHistoryAsync')
$consentHandler = [regex]::Match(
    $buildAiTab.Groups['body'].Value,
    '(?s)_aiCloudConsent\.CheckedChanged\s*\+=\s*delegate\s*' +
        '\{(?<body>.*?)\r?\n\s*\};')
Assert-True (
    $buildAiTab.Success -and
    $consentHandler.Success -and
    ([regex]::Matches(
        $buildAiTab.Groups['body'].Value,
        '\bStartModelRefresh\(\);')).Count -eq 1 -and
    -not $consentHandler.Groups['body'].Value.Contains(
        'StartModelRefresh();') -and
    $buildAiTab.Groups['body'].Value -match
        '_aiRefreshModelsBtn\.Click[\s\S]*?StartModelRefresh\(\);' -and
    $buildAiTab.Groups['body'].Value -match
        'changing consent remain network-silent'
) 'opening Options and granting consent perform no implicit AI-provider model request'

Assert-True (
    # S4b: the AI-brain tray items (Ask / Enable-Disable) moved out of the base with the AI-brain module,
    # so the base context menu no longer carries any AI tray label. The Test Speech item stays.
    -not $contextMenuSource.Contains('&Enable AI') -and
    -not $contextMenuSource.Contains('&Disable AI') -and
    -not $contextMenuSource.Contains('As&k about my screen') -and
    -not $contextMenuSource.Contains('Unload AI (free VRAM)') -and
    -not $contextMenuSource.Contains('Load AI (uses GPU)') -and
    $contextMenuSource.Contains('Right-click the tray icon for options.')
) 'AI tray items removed from the base (moved to the AiBrain module); test-speech intact'

Write-Host 'PASS: runtime hardening source invariants.'
