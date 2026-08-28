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
$startUpSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw
$petHostSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\Plugins\PetHost.cs') -Raw

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

# A poke must be attributed to the pet the user actually clicked. Only FormPet knows which one that is, and
# the host cannot recover it afterwards -- it falls back to the first pet on screen, so dropping `this` here
# silently reports a poke on pet #5 as a poke on pet #1. Invisible while every speaker broadcasts through
# SayAll, and wrong the instant anything reacts per pet, which is exactly where this is heading.
Assert-True (
    $formPetSource.Contains('OnPetPoked(this)') -and
    -not ($formPetSource -match 'OnPetPoked\(\s*\)')
) 'a poke is attributed to the pet that was actually clicked'

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

# A pet's speech preference must NEVER be keyed by the raw pet-mix id. The mix writes the active/default pet
# as "", but "" in triggerSpeech already means the ALL-PETS entry -- so keying a real pet as "" silently
# rewrites the global preference, and it LOOKS correct because the lookup falls back to global. Every pet type
# other than the active one would test fine. Exactly the class of bug this file exists to catch.
Assert-True (
    $contextMenuSource.Contains('SpeechRoutingKey(') -and
    $contextMenuSource -notmatch 'SetTriggerSpeechModule\(\s*(entry\.Id|mixId)\b' -and
    $petHostSource.Contains('SpeechRoutingKey(')
) 'a pet speech preference is keyed by the routing key, never by the raw mix id'

# The module tray section anchors after Pet Speech. Anchoring on Test Speech (as it did before Pet Speech was
# inserted between them) drops module items into the middle of the base's own speech block.
Assert-True (
    $contextMenuSource.Contains('petSpeechMenuItem ?? testSpeechMenuItem')
) 'module tray items are anchored after the Pet Speech item'

# A reaction belongs to ONE pet. The base's poke sass used to go through SayAll, which is the reported bug:
# poke one pet and every pet on screen says the same line at the same moment.
Assert-True (
    $startUpSource -notmatch 'RandomSass\(\)[\s\S]{0,200}SayAll\('
) 'the poke sass is spoken by the poked pet, not broadcast'

# SayAll and PlayAnimationOnAll must skip authoring previews. They walked sheeps[] directly, so a preview pet
# spoke and emoted -- contradicting the documented "previews are invisible to modules" invariant, which
# otherwise rests solely on DeriveOnScreenMix.
Assert-True (
    # SayAll delegates its fan-out to ShowBubbleOnAll, which is the one place bubbles are broadcast, so that
    # is where the preview filter has to live. Both it and PlayAnimationOnAll must go through PersistentPets
    # rather than walking sheeps[].
    $startUpSource -match '(?s)internal void ShowBubbleOnAll\([\s\S]{0,400}?PersistentPets\(\)' -and
    $startUpSource -match '(?s)public void SayAll\(string text\)[\s\S]{0,600}?ShowBubbleOnAll\(' -and
    $startUpSource -match '(?s)internal void PlayAnimationOnAll\([\s\S]{0,600}?PersistentPets\(\)'
) 'broadcast speech and animation skip preview pets'

# A module can hold an IPet across a slow await and there is no PetRemoved event, so Say must tolerate a pet
# that has closed rather than throwing out of the module's call.
Assert-True (
    $petHostSource -match '(?s)public void Say\(IPet pet, string text\)[\s\S]{0,300}?IsDisposed'
) 'IHost.Say guards a disposed pet'

# Module audio must NEVER enter AudioOutput._cache. That dictionary is keyed by byte[] REFERENCE identity and
# is cleared only in Dispose, so caching synthesized speech would retain every line the pet ever spoke -- plus
# a mixer-format buffer roughly 7x larger than the input. The engine path caches on purpose (a pet has a fixed
# set of animation sounds); the module path must not. Nothing else can catch this: it leaks slowly, only with
# a voice module installed, and never fails a test.
$audioSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\dotNet\AudioOutput.cs') -Raw
# Sliced by POSITION rather than brace-matched. A regex counting braces cannot see past PlayOwned's inner
# lock block, so it silently passed even with `_cache[audio] = samples` injected -- found by negative-testing
# the check itself, which is the only way that class of dud assertion ever surfaces.
$playOwnedStart = $audioSource.IndexOf('public bool PlayOwned(')
$playOwnedEnd = $audioSource.IndexOf('public bool StopOwned(')
Assert-True (
    $playOwnedStart -gt 0 -and $playOwnedEnd -gt $playOwnedStart -and
    -not $audioSource.Substring($playOwnedStart, $playOwnedEnd - $playOwnedStart).Contains('_cache')
) 'module audio is never entered into the decode cache'

# The faceCursor DISPATCH. Converted gaze animations carry <action>faceCursor</action>, the validator accepts
# it, and the pure facing rule is asserted in --hardening-selftest -- but none of that reaches a pet unless
# SetNewAnimationCore actually calls FaceTheCursor when the tag is present. Delete the call and every other
# check in the project still passes: the animation plays, just never aimed. A source-text check because the
# real thing needs a live form, a real cursor and a loaded pet.
Assert-True (
    $formPetSource -match '(?s)"faceCursor"[\s\S]{0,200}?FaceTheCursor\(\);' -and
    # ...and it must SET facing, not toggle it. FlipOrientation here would be wrong half the time, which is
    # exactly the bug that looks like "the gaze works, sometimes".
    $formPetSource -match '(?s)private void FaceTheCursor\(\)[\s\S]{0,600}?IsMovingLeft = ShouldFaceLeft\('
) 'a faceCursor animation aims the pet at the pointer on entry'

Write-Host 'PASS: runtime hardening source invariants.'
