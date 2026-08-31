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

# All THREE window borders must raise their discriminator. The flag algebra is asserted in
# --hardening-selftest, but nothing there can see whether the detection sites actually pass the new value:
# revert any one of them to a bare WINDOW and every other check stays green while that edge silently stops
# being distinguishable. Anchored on the three distinct comparisons so the checks cannot pass by matching
# the same line three times.
# Anchored on each site's own comment rather than on its comparison: the comparisons sit several hundred
# characters from the call once the reasoning above them is written down, and a distance-based anchor that
# has to be widened every time a comment grows is a check that will eventually be widened into uselessness.
Assert-True (
    $formPetSource -match '(?s)// left window border![\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_LEFT' -and
    $formPetSource -match '(?s)// right window border![\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_RIGHT' -and
    $formPetSource -match '(?s)FallDetect\(y\)[\s\S]{0,600}?TOnly\.WINDOW \| TNextAnimation\.TOnly\.WINDOW_TOP'
) 'each window border raises which edge it is'

# Window-side grip: the parts that need a real window on screen and so cannot be asserted anywhere else.
#
# 1. The rect is RE-READ every tick. Caching it at grip time leaves a pet pinned to where a window used to
#    be after you drag, resize or close it, which is the most obviously broken thing this can do.
# 2. A degenerate rect releases. Minimised windows report one, and pinning to it teleports the pet.
# 3. hwndWindow is a PROPERTY that clears the grip. Nine sites drop that handle for their own reasons and a
#    grip surviving one of them pins the pet to a window it is no longer tracking.
Assert-True (
    $formPetSource -match '(?s)windowGrip != WindowGrip\.None[\s\S]{0,600}?GetWindowRect\(new HandleRef\(this, hwndWindow\)' -and
    $formPetSource -match '(?s)windowGrip != WindowGrip\.None[\s\S]{0,600}?gripRect\.Right <= gripRect\.Left' -and
    # The CONDITION, not just the assignment. Asserting only that the property body mentions
    # `windowGrip = WindowGrip.None` passes a setter whose guard has been disabled -- the statement is
    # still there, just unreachable. Negative-tested: this is the version that fails when the guard goes.
    $formPetSource -match '(?s)IntPtr hwndWindow\s*\{[\s\S]{0,900}?if \(value == \(IntPtr\)0\)[\s\S]{0,300}?windowGrip = WindowGrip\.None;'
) 'a window grip re-reads the window every tick and is dropped with the handle'

# The vertical limits of a grip are the WINDOW's, and they must be tested BEFORE the screen ones. Reorder
# them and a gripping pet climbs straight past the frame it is holding, up to the top of the screen.
#
# By POSITION rather than by a bounded regex, because the property is an ordering and a distance-based
# pattern only approximates it: the branch is nearly two thousand characters long, so any regex wide enough
# to span it would also match the two branches in the wrong order.
$gripBranch = $formPetSource.IndexOf('else if (gripping)')
$downBranch = $formPetSource.IndexOf('else if(y > 0)')
Assert-True (
    $gripBranch -gt 0 -and $downBranch -gt $gripBranch -and
    $formPetSource.Substring($gripBranch, $downBranch - $gripBranch).Contains('gripRect.Bottom') -and
    $formPetSource.Substring($gripBranch, $downBranch - $gripBranch).Contains('gripRect.Top')
) 'a gripping pet is bounded by the window, checked before the screen'

# Letting go must work in both directions. ReleaseWindowGrip implements "let go" by playing the fall
# animation; nothing did the inverse, so a graph that transitioned INTO fall by its own <next> edge kept the
# grip. Under a window that is permanent: the underside branch above pins y to 0 and both of its release
# conditions test y, so neither can ever fire again.
#
# GripMustRelease is a pure static with its own assertions in --hardening-selftest, so what is checked HERE is
# the thing a unit test cannot see: that SetNewAnimationCore actually calls it, and that the call is wired to
# the fall animation and to clearing hwndWindow. A correct predicate nobody invokes is the exact failure mode
# the standing rule about source-text checks warns about.
$setNewCore = $formPetSource.IndexOf('private void SetNewAnimationCore(int id)')
$coreEnd    = $formPetSource.IndexOf('private void NextStep()', $setNewCore)
$coreBody   = if ($setNewCore -gt 0 -and $coreEnd -gt $setNewCore) { $formPetSource.Substring($setNewCore, $coreEnd - $setNewCore) } else { '' }
Assert-True (
    $coreBody -match 'GripMustRelease\(' -and
    # ...told which animation it is entering, or it can never answer the fall case.
    $coreBody -match '(?s)GripMustRelease\([\s\S]{0,200}?id == Animations\.AnimationFall' -and
    # ...and the release it guards is the one that clears the handle. Clearing windowGrip alone leaves the
    # pet "on" a window it is no longer pinned to, which is why ReleaseWindowGrip exists as one place.
    $coreBody -match '(?s)GripMustRelease\([\s\S]{0,400}?hwndWindow = \(IntPtr\)0'
) 'entering an animation that cannot hold a grip drops the window handle'

# The Pets pane diffed the catalog by ID alone, so a pet already installed was filtered out of "available to
# download" however much its CONTENT had changed. A corrected pet reached new downloads only, and the pane said
# "you already have every available pet" while an update sat there. PetProvenance now answers it by hash.
#
# Asserted HERE rather than only in the unit table because the classifier is useless unless the pane calls it,
# and because the shipped bug was precisely a missing comparison rather than a wrong one.
$petsPane = Get-Content -Raw (Join-Path $repoRoot 'src\Portable\Wpf\PetsPaneControl.cs')
Assert-True (
    # A third list exists and is rendered from a STALENESS diff, not from the id diff.
    $petsPane -match '(?s)private List<CatalogPet> DiffStale\(\)[\s\S]{0,900}?PetProvenance\.IsStale\(FreshnessOf\(pet\)\)' -and
    # ...and it is actually wired to the button that checks the catalog, or nothing ever populates it.
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1200}?DiffStale\(\)' -and
    $petsPane -match '(?s)CheckButton_Click[\s\S]{0,1400}?RenderUpdates\(' -and
    # The freshness verdict must come from the shared classifier, not from a second opinion in the UI.
    $petsPane -match 'PetProvenance\.Classify\(' -and
    # Installing must stamp provenance from the DOWNLOADED BYTES. Stamping from a re-read file would still
    # work today, but hashing what was verified is what makes the comparison exact.
    $petsPane -match 'PetProvenance\.WriteStamp\([^)]*PetProvenance\.HashBytes\(bytes\)' -and
    # And the confirm prompt must be driven by the classifier, so the prompt cannot disagree with the badge.
    $petsPane -match 'PetProvenance\.UpdateWouldDiscardChanges\(' -and
    # The status line has to be derived from the STALE count too. The old one read "you already have every
    # available pet", which was true by the ID diff and false in the only sense that mattered, and a pane that
    # renders the update cards while still saying that is the shipped bug with extra steps.
    #
    # Deliberately NOT a ban on that sentence: the comments in there explain the bug and quote it, and a check
    # that forbids describing a bug is a check that gets deleted. Assert the derivation instead.
    $petsPane -match '(?s)stalePets\.Count[\s\S]{0,600}?_status\.Text'
) 'the Pets pane offers a content update, stamps what it installed, and says so'

# The window UNDERSIDE, checked before the screen's top border for the same reason the window top is
# checked before the taskbar: a window is inside the screen, so testing the screen first lets a jumping pet
# pass straight through one on its way to the top of the display.
#
# Ordering alone is not the property, and asserting only that was negative-tested away: a RiseDetect call
# that appears first but is gated behind something unreachable still satisfies it. The load-bearing part is
# that the screen-top test is CHAINED off the underside result (`else if`), so the two cannot both fire on
# one tick -- a pet that just grabbed an overhang must not also be snapped to the top of the display.
$upBranch    = $formPetSource.IndexOf('else if(y < 0)')
$screenTop   = $formPetSource.IndexOf('else if (PositionY + y < workArea.Y)', $upBranch)
$riseCall    = $formPetSource.IndexOf('RiseDetect(y, ins)', $upBranch)
Assert-True (
    $upBranch -gt 0 -and $riseCall -gt $upBranch -and $screenTop -gt $riseCall -and
    # ...and nothing re-tests the screen top unconditionally alongside it.
    $formPetSource.IndexOf('if (PositionY + y < workArea.Y)', $upBranch) -eq ($screenTop + 5)
) 'a window underside is checked before the top of the screen, and the screen top is chained off it'

# RiseDetect claims hwndWindow on the way in. If nothing wants to hang there it MUST give it back, or the
# pet believes it is standing on a window it is merely underneath and the gravity branch starts following
# that window around.
Assert-True (
    $formPetSource -match '(?s)RiseDetect\(y, ins\)[\s\S]{0,1600}?else\s*\{[\s\S]{0,400}?hwndWindow = \(IntPtr\)0;'
) 'a refused window underside gives the window handle back'

# A maximised window's bottom edge sits on the work area, directly over a pet standing on the taskbar.
# Without the clearance test the pet grabs the underside on the first tick of every jump it ever makes.
Assert-True (
    $formPetSource -match '(?s)private WindowTopHit RiseDetect\([\s\S]{0,3000}?rct\.Bottom >= ScreenArea\.Y \+ ScreenArea\.Height'
) 'the underside test ignores a window whose bottom is the work area'

Write-Host 'PASS: runtime hardening source invariants.'
