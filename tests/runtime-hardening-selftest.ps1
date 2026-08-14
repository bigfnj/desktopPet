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
    $formPetSource.Contains('rctO.Right <= rctO.Left') -and
    $formPetSource.Contains('rctO.Bottom <= rctO.Top') -and
    $formPetSource.Contains('DesktopGeometry.TryScaleWindowRelativeX(')
) 'window following rejects collapsed rectangles and uses safe relative scaling'

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

# Any redirected child-process output must pin its own encoding. Leaving StandardOutputEncoding unset
# decodes the stream via GetConsoleOutputCP(), which is 0 in a GUI process with no console, and .NET reads
# codepage 0 as CP_ACP (the system ANSI codepage). That silently mojibake'd every non-ASCII glyph tesseract
# read off the screen before it ever reached the model. Repo-wide because the next redirect will be written
# by someone who never met this bug.
$redirectOffenders = @()
foreach ($file in Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($text -notmatch 'RedirectStandardOutput\s*=\s*true') { continue }
    if ($text -notmatch 'StandardOutputEncoding\s*=') {
        $redirectOffenders += $file.FullName.Substring($repoRoot.Length + 1)
    }
}
Assert-True ($redirectOffenders.Count -eq 0) (
    'every RedirectStandardOutput pins StandardOutputEncoding' +
    $(if ($redirectOffenders.Count -gt 0) { " (offenders: $($redirectOffenders -join ', '))" } else { '' }))

# Module payloads must be unpacked OFF the UI thread. fortunes.zip is ~31 MB, and unpacking it
# synchronously froze the settings window for seconds during an install or update. Nothing else catches
# this: there is no .editorconfig and CA1849 is not surfaced at warning severity, which is how the
# synchronous version shipped in the first place.
$modulesPaneSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\Wpf\ModulesPaneControl.cs') -Raw
Assert-True (
    $modulesPaneSource.Contains('ZipFile.ExtractToDirectoryAsync(') -and
    $modulesPaneSource -notmatch '(?<!Async)\bExtractToDirectory\('
) 'module payloads are extracted asynchronously, never on the UI thread'

Write-Host 'PASS: runtime hardening source invariants.'
