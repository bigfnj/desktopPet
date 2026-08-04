[CmdletBinding()]
param(
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This harness loads the shipped net48 product assembly and instantiates its WinForms types, whose
# resource loading uses BinaryFormatter -- disabled under modern .NET (PowerShell 7). Re-launch under
# Windows PowerShell 5.1 (.NET Framework), which hosts the assembly the way it actually runs, so the
# harness works regardless of which shell the calling CI step uses.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell)) {
        throw "Windows PowerShell 5.1 is required to host the net48 product assembly: $windowsPowerShell"
    }
    $forwardArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $forwardArguments += @('-ExecutablePath', $ExecutablePath)
    }
    & $windowsPowerShell @forwardArguments
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ExecutablePath = Join-Path (
        Split-Path -Parent $scriptRoot
    ) 'build\DesktopPetPortable\bin\Release\x64\DesktopPet.exe'
}

function Assert-Equal {
    param(
        [object] $Expected,
        [object] $Actual,
        [string] $Name
    )

    if ($Expected -ne $Actual) {
        throw "$Name expected '$Expected', got '$Actual'."
    }
    Write-Host "PASS: $Name"
}

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Name
    )

    if (-not $Condition) {
        throw "$Name failed."
    }
    Write-Host "PASS: $Name"
}

function Assert-InvalidDataRejection {
    param(
        [scriptblock] $Action,
        [string] $Name
    )

    try {
        & $Action
    }
    catch {
        $errorObject = $_.Exception
        while ($null -ne $errorObject.InnerException) {
            $errorObject = $errorObject.InnerException
        }
        if ($errorObject -isnot [IO.InvalidDataException]) {
            throw "$Name threw $($errorObject.GetType().FullName), not InvalidDataException."
        }
        Write-Host "PASS: $Name"
        return
    }

    throw "$Name accepted invalid input."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$assembly = [Reflection.Assembly]::LoadFrom($resolvedExecutable)
$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
$publicStatic = [Reflection.BindingFlags]'Public,Static'
$publicInstance = [Reflection.BindingFlags]'Public,Instance'
$nonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$nonPublicInstance = [Reflection.BindingFlags]'NonPublic,Instance'

$formPetSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\FormPet.cs') -Raw
$formSpeechSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\FormSpeech.cs') -Raw
$aiBrainSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\Ai\AiBrain.cs') -Raw
$startUpSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\StartUp.cs') -Raw
$formOptionsSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Portable\FormOptions.cs') -Raw
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

$contextMenuSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\ContextMenus.cs') -Raw
Assert-True (
    $contextMenuSource.Contains(
        'return enabled ? "&Disable AI" : "&Enable AI";') -and
    -not $contextMenuSource.Contains('Unload AI (free VRAM)') -and
    -not $contextMenuSource.Contains('Load AI (uses GPU)') -and
    $contextMenuSource.Contains('Right-click the tray icon for options.')
) 'provider-neutral AI tray labels and accurate test-speech interaction'

$limitsType = $assembly.GetType('DesktopPet.AnimationRuntimeLimits', $true)
function Invoke-RuntimeLimit {
    param(
        [string] $Name,
        [object[]] $Arguments
    )

    return $limitsType.GetMethod($Name, $publicStatic).Invoke($null, $Arguments)
}

Assert-Equal 3 (
    Invoke-RuntimeLimit 'CalculateTotalSteps' ([object[]]@(3, 1, 0))
) 'no-repeat total steps'
Assert-Equal 5 (
    Invoke-RuntimeLimit 'CalculateTotalSteps' ([object[]]@(3, 1, 1))
) 'repeat total steps'
Assert-Equal 2003 (
    Invoke-RuntimeLimit 'CalculateTotalSteps' ([object[]]@(3, 1, [int]::MaxValue))
) 'repeat clamp'
Assert-Equal 1000000 (
    Invoke-RuntimeLimit 'CalculateTotalSteps' ([object[]]@(16384, 0, 1000))
) 'total-step cap'
Assert-Equal 0 (
    Invoke-RuntimeLimit 'LastStepIndex' ([object[]]@(1))
) 'one-frame last step'
Assert-Equal 4 (
    Invoke-RuntimeLimit 'LastStepIndex' ([object[]]@(5))
) 'multi-frame last step'
Assert-Equal 1 (
    Invoke-RuntimeLimit 'InterpolationSteps' ([object[]]@(1))
) 'one-frame interpolation divisor'
Assert-Equal 4 (
    Invoke-RuntimeLimit 'InterpolationSteps' ([object[]]@(5))
) 'endpoint interpolation divisor'

$mappedFrames = @(
    0..4 | ForEach-Object {
        Invoke-RuntimeLimit 'SequenceFrameIndex' (
            [object[]]@([int]$_, 3, 1)
        )
    }
)
Assert-Equal '0,1,2,1,2' ($mappedFrames -join ',') 'repeat-from frame order'

Assert-Equal -8192 (
    Invoke-RuntimeLimit 'ClampLocalPosition' (
        [object[]]@([long][int]::MinValue, 1920)
    )
) 'negative coordinate clamp'
Assert-Equal 10112 (
    Invoke-RuntimeLimit 'ClampLocalPosition' (
        [object[]]@([long][int]::MaxValue, 1920)
    )
) 'positive coordinate clamp'
Assert-Equal 1756 (
    Invoke-RuntimeLimit 'MirrorLocalX' ([object[]]@(100, 1920, 64))
) 'normal mirror arithmetic'
$rightFacingParentX = Invoke-RuntimeLimit 'MirrorLocalX' (
    [object[]]@(100, 1920, 64)
)
$canonicalRightFacingParentX = Invoke-RuntimeLimit 'CanonicalParentX' (
    [object[]]@([int]$rightFacingParentX, $true, 1920, 64)
)
Assert-Equal 100 $canonicalRightFacingParentX (
    'flipped parent is canonicalized before child expression evaluation'
)
$leftFacingChildX = [int]$canonicalRightFacingParentX + 64 + 10
$rightFacingChildX = Invoke-RuntimeLimit 'MirrorLocalX' (
    [object[]]@($leftFacingChildX, 1920, 32)
)
Assert-Equal 1888 ($leftFacingChildX + [int]$rightFacingChildX) (
    'child placement has left-right symmetry with one full-screen mirror'
)
Assert-Equal 10112 (
    Invoke-RuntimeLimit 'MirrorLocalX' (
        [object[]]@([int]::MinValue, 1920, 64)
    )
) 'overflow-safe mirror arithmetic'
Assert-Equal ([int]::MaxValue) (
    Invoke-RuntimeLimit 'ClampLocalPosition' (
        [object[]]@(([long][int]::MaxValue + 1L), [int]::MaxValue)
    )
) 'maximum monitor extent never wraps'
Assert-Equal ([int]::MaxValue) (
    Invoke-RuntimeLimit 'MirrorLocalX' (
        [object[]]@(-100, [int]::MaxValue, 0)
    )
) 'maximum-width mirror never wraps'
Assert-Equal ([double][int]::MaxValue) (
    Invoke-RuntimeLimit 'ClampVirtualPosition' (
        [object[]]@([double]::PositiveInfinity, [int]::MaxValue, [int]::MaxValue)
    )
) 'positive infinite virtual coordinate clamps'
Assert-Equal 14 (
    Invoke-RuntimeLimit 'ClipCut' ([object[]]@(14.0, 64))
) 'first absolute clipping cut'
Assert-Equal 24 (
    Invoke-RuntimeLimit 'ClipCut' ([object[]]@(24.0, 64))
) 'second absolute clipping cut'
Assert-Equal 50 (
    64 - [int](Invoke-RuntimeLimit 'ClipCut' ([object[]]@(14.0, 64)))
) 'first visible clipping extent'
Assert-Equal 40 (
    64 - [int](Invoke-RuntimeLimit 'ClipCut' ([object[]]@(24.0, 64)))
) 'second visible clipping extent is not cumulative'
Assert-Equal 64 (
    Invoke-RuntimeLimit 'ClipCut' ([object[]]@(32768.0, 64))
) 'large positive clipping jump clamps to full extent'
Assert-Equal 0 (
    Invoke-RuntimeLimit 'ClipCut' ([object[]]@(-32768.0, 64))
) 'negative clipping amount is ignored'
Assert-Equal 24 (
    Invoke-RuntimeLimit 'ClipCut' ([object[]]@(24.0, 64))
) 'bottom clipping cut'
Assert-Equal 20 (
    40 -
        [int](Invoke-RuntimeLimit 'ClipCut' ([object[]]@(10.0, 40))) -
        [int](Invoke-RuntimeLimit 'ClipCut' ([object[]]@(10.0, 40)))
) 'simultaneous horizontal cuts retain viewport slice'
Assert-Equal ([int]::MaxValue) (
    Invoke-RuntimeLimit 'ClampFormCoordinate' (
        [object[]]@([double]::PositiveInfinity)
    )
) 'positive form coordinate saturation'
Assert-Equal ([int]::MinValue) (
    Invoke-RuntimeLimit 'ClampFormCoordinate' (
        [object[]]@([double]::NegativeInfinity)
    )
) 'negative form coordinate saturation'
Assert-True (
    [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(-64.0, 100.0, 64, 64, 0, 0, 1920, 1080)
    ))
) 'exact full left cut is outside'
Assert-True (
    [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(1920.0, 100.0, 64, 64, 0, 0, 1920, 1080)
    ))
) 'exact full right cut is outside'
Assert-True (
    [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(100.0, -64.0, 64, 64, 0, 0, 1920, 1080)
    ))
) 'exact full top cut is outside'
Assert-True (
    [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(100.0, 1080.0, 64, 64, 0, 0, 1920, 1080)
    ))
) 'exact full bottom cut is outside'
Assert-True (
    -not [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(-63.0, 100.0, 64, 64, 0, 0, 1920, 1080)
    ))
) 'one-pixel inward slice remains visible'
Assert-True (
    [bool](Invoke-RuntimeLimit 'IsSpriteFullyOutside' (
        [object[]]@(
            4294967294.0,
            4294967294.0,
            64,
            64,
            [int]::MaxValue,
            [int]::MaxValue,
            [int]::MaxValue,
            [int]::MaxValue
        )
    ))
) 'extreme monitor edge arithmetic does not wrap'

$xmlType = $assembly.GetType('DesktopPet.Xml', $true)
$xmlOne = [Activator]::CreateInstance($xmlType, [object[]]@(1))
$xmlTwo = [Activator]::CreateInstance($xmlType, [object[]]@(2))
try {
    $compute = $xmlType.GetMethod('GetXMLCompute', $publicInstance)
    $valueOne = $compute.Invoke($xmlOne, [object[]]@('scale', 'ownership-one'))
    $valueTwo = $compute.Invoke($xmlTwo, [object[]]@('scale', 'ownership-two'))
    $valueType = $assembly.GetType('DesktopPet.TValue', $true)
    $evaluator = $valueType.GetField('Evaluator', $nonPublicInstance)
    Assert-True (
        [object]::ReferenceEquals($xmlOne, $evaluator.GetValue($valueOne))
    ) 'first TValue evaluator ownership'
    Assert-True (
        [object]::ReferenceEquals($xmlTwo, $evaluator.GetValue($valueTwo))
    ) 'second TValue evaluator ownership'
    Assert-Equal 1 (
        $valueType.GetMethod('GetRawValue', $publicInstance).Invoke(
            $valueOne,
            [object[]]@(-1)
        )
    ) 'first evaluator scale isolation'
    Assert-Equal 2 (
        $valueType.GetMethod('GetRawValue', $publicInstance).Invoke(
            $valueTwo,
            [object[]]@(-1)
        )
    ) 'second evaluator scale isolation'

    $animationsType = $assembly.GetType('DesktopPet.Animations', $true)
    $animations = [Activator]::CreateInstance($animationsType, [object[]]@($xmlOne))
    try {
        $nextWeight = $animationsType.GetMethod('NextWeight', $nonPublicInstance)
        $upperBound = [long][int]::MaxValue + 1L
        $allWeightsInRange = $true
        for ($index = 0; $index -lt 2048; $index++) {
            $sample = [long]$nextWeight.Invoke(
                $animations,
                [object[]]@($upperBound)
            )
            if ($sample -lt 0 -or $sample -ge $upperBound) {
                $allWeightsInRange = $false
                break
            }
        }
        Assert-True $allWeightsInRange 'weight selection above int.MaxValue'
    }
    finally {
        $animationsType.GetMethod('Dispose', $publicInstance).Invoke(
            $animations,
            [object[]]@()
        )
    }

    $validateBudget = $xmlType.GetMethod(
        'ValidateSpriteBudget',
        $nonPublicStatic
    )
    $validateBudget.Invoke(
        $null,
        [object[]]@(32, 32, 128, 128)
    ) | Out-Null
    Write-Host 'PASS: exact sprite pixel budget accepted'
    Assert-InvalidDataRejection {
        $validateBudget.Invoke(
            $null,
            [object[]]@(41, 25, 1, 1)
        ) | Out-Null
    } 'oversized generated-frame count rejected'
    Assert-InvalidDataRejection {
        $validateBudget.Invoke(
            $null,
            [object[]]@(32, 32, 256, 256)
        ) | Out-Null
    } 'oversized generated-pixel budget rejected'
}
finally {
    $xmlType.GetMethod('Dispose', $publicInstance).Invoke(
        $xmlOne,
        [object[]]@()
    )
    $xmlType.GetMethod('Dispose', $publicInstance).Invoke(
        $xmlTwo,
        [object[]]@()
    )
}

$resourcesType = $assembly.GetType('DesktopPet.Properties.Resources', $true)
$animationsProperty = $resourcesType.GetProperty(
    'animations',
    [Reflection.BindingFlags]'NonPublic,Static'
)
$bundledXml = [string]$animationsProperty.GetValue($null, [object[]]@())
$scaledXml = [Activator]::CreateInstance($xmlType, [object[]]@(4))
try {
    $tryReadXml = $xmlType.GetMethod('TryReadXml', $publicInstance)
    $readArguments = [object[]]@($bundledXml, $null)
    Assert-True (
        [bool]$tryReadXml.Invoke($scaledXml, $readArguments)
    ) 'bundled pet decodes at requested 4x scale'
    $spriteCountProperty = $xmlType.GetProperty(
        'SpriteCount',
        [Reflection.BindingFlags]'Instance,NonPublic'
    )
    $spriteCount = [int]$spriteCountProperty.GetValue(
        $scaledXml,
        [object[]]@()
    )
    Assert-True (
        $spriteCount -le 1024
    ) 'bundled pet generated-frame budget'
    Assert-True (
        ([long]$spriteCount *
            $scaledXml.spriteWidth *
            $scaledXml.spriteHeight) -le (16L * 1024L * 1024L)
    ) 'bundled pet generated-pixel budget'
}
finally {
    $xmlType.GetMethod('Dispose', $publicInstance).Invoke(
        $scaledXml,
        [object[]]@()
    )
}

$formType = $assembly.GetType('DesktopPet.FormPet', $true)
$speechType = $assembly.GetType('DesktopPet.FormSpeech', $true)
$speech = [Activator]::CreateInstance($speechType, $true)
try {
    $setFullscreenSuppressed = $speechType.GetMethod(
        'SetFullscreenSuppressed',
        $nonPublicInstance)
    $setFullscreenSuppressed.Invoke(
        $speech,
        [object[]]@($true))
    Assert-True (-not $speech.TopMost) 'speech bubble yields to fullscreen'
    $setFullscreenSuppressed.Invoke(
        $speech,
        [object[]]@($false))
    Assert-True $speech.TopMost 'speech bubble restores topmost after fullscreen'
}
finally {
    $speech.Dispose()
}

$budgetType = $formType.GetNestedType(
    'ChildBudget',
    [Reflection.BindingFlags]'NonPublic'
)
$tryAcquire = $budgetType.GetMethod('TryAcquire', $publicInstance)
$release = $budgetType.GetMethod('Release', $publicInstance)
$budgets = @(
    [Activator]::CreateInstance($budgetType, $true),
    [Activator]::CreateInstance($budgetType, $true),
    [Activator]::CreateInstance($budgetType, $true)
)
$heldSlots = @(0, 0, 0)
try {
    for ($index = 0; $index -lt 32; $index++) {
        if (-not [bool]$tryAcquire.Invoke($budgets[0], [object[]]@())) {
            throw "The first root stopped at child slot $index."
        }
        $heldSlots[0]++
    }
    Assert-True (
        -not [bool]$tryAcquire.Invoke($budgets[0], [object[]]@())
    ) 'per-root child cap'

    for ($index = 0; $index -lt 32; $index++) {
        if (-not [bool]$tryAcquire.Invoke($budgets[1], [object[]]@())) {
            throw "The process stopped at child slot $($index + 32)."
        }
        $heldSlots[1]++
    }
    Assert-True (
        -not [bool]$tryAcquire.Invoke($budgets[2], [object[]]@())
    ) 'process-global child cap'

    $release.Invoke($budgets[0], [object[]]@())
    $heldSlots[0]--
    Assert-True (
        [bool]$tryAcquire.Invoke($budgets[2], [object[]]@())
    ) 'released global child slot reusable'
    $heldSlots[2]++
}
finally {
    for ($budgetIndex = 0; $budgetIndex -lt $budgets.Count; $budgetIndex++) {
        while ($heldSlots[$budgetIndex] -gt 0) {
            $release.Invoke($budgets[$budgetIndex], [object[]]@())
            $heldSlots[$budgetIndex]--
        }
    }
}

$parentForm = $null
$disposedChildren = @()
$pruneBudget = [Activator]::CreateInstance($budgetType, $true)
try {
    $parentForm = [Activator]::CreateInstance($formType)
    $childrenField = $formType.GetField('childs', $nonPublicInstance)
    $childrenList = $childrenField.GetValue($parentForm)
    $childConstructor = @(
        $formType.GetConstructors($nonPublicInstance) |
            Where-Object { $_.GetParameters().Count -eq 8 }
    )[0]
    if ($null -eq $childConstructor) {
        throw 'The private child FormPet constructor was not found.'
    }

    1..2 | ForEach-Object {
        if (-not [bool]$tryAcquire.Invoke($pruneBudget, [object[]]@())) {
            throw "Unable to reserve child slot $_ for prune regression."
        }
        $child = $childConstructor.Invoke(
            [object[]]@(
                $null,
                $null,
                $parentForm,
                [Drawing.Point]::Empty,
                $false,
                0,
                1,
                $pruneBudget
            )
        )
        [void]$childrenList.Add($child)
        $child.Dispose()
        $disposedChildren += $child
    }

    $formType.GetMethod('PruneClosedChildren', $nonPublicInstance).Invoke(
        $parentForm,
        [object[]]@()
    )
    Assert-Equal 0 $childrenList.Count 'adjacent disposed children pruned once'
    Assert-Equal 0 (
        $budgetType.GetField('active', $nonPublicInstance).GetValue($pruneBudget)
    ) 'disposed-child budget slots released'
}
finally {
    foreach ($child in $disposedChildren) {
        if ($null -ne $child) {
            try { $child.Dispose() } catch { }
        }
    }
    if ($null -ne $parentForm) {
        try { $parentForm.Dispose() } catch { }
    }
    $activeSlots = [int]$budgetType.GetField(
        'active',
        $nonPublicInstance
    ).GetValue($pruneBudget)
    while ($activeSlots -gt 0) {
        $release.Invoke($pruneBudget, [object[]]@())
        $activeSlots--
    }
}

$readBounded = $formType.GetMethod('ReadBoundedPetXml', $nonPublicStatic)
$temporaryFile = Join-Path (
    [IO.Path]::GetTempPath()
) ('DesktopPet-bounded-' + [Guid]::NewGuid().ToString('N') + '.xml')
try {
    [IO.File]::WriteAllText(
        $temporaryFile,
        '<root />',
        (New-Object Text.UTF8Encoding($true))
    )
    Assert-Equal '<root />' (
        [string]$readBounded.Invoke(
            $null,
            [object[]]@([string]$temporaryFile)
        )
    ) 'bounded UTF-8 BOM decode'

    $stream = [IO.File]::Open(
        $temporaryFile,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None
    )
    try {
        $stream.SetLength(4194305)
    }
    finally {
        $stream.Dispose()
    }
    Assert-InvalidDataRejection {
        $readBounded.Invoke(
            $null,
            [object[]]@([string]$temporaryFile)
        ) | Out-Null
    } 'maximum-plus-one XML read rejected'
}
finally {
    if (Test-Path -LiteralPath $temporaryFile) {
        Remove-Item -LiteralPath $temporaryFile -Force
    }
}

Write-Host 'PASS: focused runtime hardening regression harness.'
