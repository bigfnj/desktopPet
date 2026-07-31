#requires -Version 5
[CmdletBinding()]
param(
    [string]$Path,
    [ValidateSet(1, 2)][int]$DataSchema = 1,
    [ValidateRange(-1, 100000)][int]$ExpectedRowCount = -1,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MaximumRuntimeCustomPackBytes = 4 * 1024 * 1024
$MaximumRuntimeEntries = 100000
$MaximumTaggedContentCharacters = 16 * 1024 * 1024

$topics = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($value in @(
        'tech', 'science', 'work-money', 'love', 'family', 'faith',
        'society', 'food', 'nature', 'arts', 'life')) {
    [void]$topics.Add($value)
}
$genres = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($value in @(
        'tv-quote', 'observation', 'joke', 'pun', 'quip', 'aphorism',
        'wisdom', 'fact', 'insult', 'verse', 'dark', 'uplifting')) {
    [void]$genres.Add($value)
}
$levels = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($value in @('general', 'edgy', 'nsfw')) {
    [void]$levels.Add($value)
}
$legacyCategories = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($value in @(
        'tech', 'facts', 'work', 'creative', 'wisdom', 'observations',
        'tv', 'nsfw', 'spicy', 'whimsy', 'general', 'custom')) {
    [void]$legacyCategories.Add($value)
}

function Test-ContainsControlCharacter {
    param([Parameter(Mandatory = $true)][string]$Value)
    foreach ($character in $Value.ToCharArray()) {
        if ([char]::IsControl($character)) {
            return $true
        }
    }
    return $false
}

function Assert-CommonFields {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Level,
        [Parameter(Mandatory = $true)][string]$Profanity,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][int]$LineNumber
    )

    if ([string]::IsNullOrEmpty($Source) -or
        $Source.Length -gt 128 -or
        $Source -cne $Source.Trim() -or
        (Test-ContainsControlCharacter -Value $Source)) {
        throw "line $LineNumber source must be 1..128 trimmed non-control characters."
    }
    if (-not $levels.Contains($Level)) {
        throw "line $LineNumber has unknown level '$Level'."
    }
    if ($Profanity -cne '0' -and $Profanity -cne '1') {
        throw "line $LineNumber profanity flag must be 0 or 1."
    }
    if ([string]::IsNullOrEmpty($Text) -or
        $Text.Length -lt 8 -or
        $Text.Length -gt 280 -or
        $Text -cne $Text.Trim() -or
        (Test-ContainsControlCharacter -Value $Text)) {
        throw "line $LineNumber text must be 8..280 trimmed non-control characters."
    }
}

function Assert-PackByteLength {
    param(
        [Parameter(Mandatory = $true)][long]$Length,
        [string]$Context = 'pack'
    )

    if ($Length -lt 1 -or $Length -gt $MaximumRuntimeCustomPackBytes) {
        throw (
            "$Context byte length is outside the runtime custom-pack limit " +
            "of $MaximumRuntimeCustomPackBytes bytes: $Length."
        )
    }
}

function Assert-PackContent {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][ValidateSet(1, 2)][int]$Schema,
        [ValidateRange(-1, 100000)][int]$ExpectedRows = -1,
        [string]$Context = 'pack'
    )

    if ($Content.Length -eq 0) {
        throw "$Context is empty."
    }
    if ($Content.Length -gt $MaximumTaggedContentCharacters) {
        throw "$Context exceeds the runtime tagged-content character limit."
    }

    $expectedFields = if ($Schema -eq 1) { 5 } else { 6 }
    $lineNumber = 0
    $reader = New-Object IO.StringReader($Content)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            $lineNumber++
            if ($lineNumber -gt $MaximumRuntimeEntries) {
                throw "$Context exceeds the runtime maximum row count."
            }
            if ($lineNumber -eq 1 -and
                $line.Length -gt 0 -and
                $line[0] -eq [char]0xFEFF) {
                $line = $line.Substring(1)
            }
            if ($line.Length -eq 0) {
                throw "$Context line $lineNumber is blank."
            }
            if ($line.Length -gt 1024) {
                throw "$Context line $lineNumber exceeds the runtime line limit."
            }
            $fields = $line.Split(
                [char[]]@([char]9),
                [StringSplitOptions]::None)
            if ($fields.Length -ne $expectedFields) {
                throw "$Context line $lineNumber has $($fields.Length) fields; schema v$Schema requires exactly $expectedFields."
            }

            if ($Schema -eq 1) {
                Assert-CommonFields -Source $fields[0] -Level $fields[2] `
                    -Profanity $fields[3] -Text $fields[4] -LineNumber $lineNumber
                if (-not $legacyCategories.Contains($fields[1])) {
                    throw "$Context line $lineNumber has unsupported legacy category '$($fields[1])'."
                }
            }
            else {
                Assert-CommonFields -Source $fields[0] -Level $fields[3] `
                    -Profanity $fields[4] -Text $fields[5] -LineNumber $lineNumber
                if (-not $topics.Contains($fields[1])) {
                    throw "$Context line $lineNumber has unknown topic '$($fields[1])'."
                }
                if (-not $genres.Contains($fields[2])) {
                    throw "$Context line $lineNumber has unknown genre '$($fields[2])'."
                }
            }
        }
    }
    finally {
        $reader.Dispose()
    }

    if ($lineNumber -eq 0) {
        throw "$Context contains no rows."
    }
    if ($ExpectedRows -ge 0 -and $lineNumber -ne $ExpectedRows) {
        throw "$Context row count $lineNumber does not match expected $ExpectedRows."
    }
    return $lineNumber
}

function Assert-SelfTestRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][ValidateSet(1, 2)][int]$Schema
    )

    $failure = $null
    try {
        [void](Assert-PackContent -Content $Content -Schema $Schema `
            -ExpectedRows 1 -Context "self-test '$Name'")
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw "Pack-data self-test '$Name' did not fail closed."
    }
}

function Invoke-SelfTest {
    $tab = [char]9
    $validText = 'A valid fortune.'
    $validV1 = "sample${tab}general${tab}general${tab}0${tab}$validText"
    $validV2 = "sample${tab}life${tab}quip${tab}general${tab}0${tab}$validText"
    [void](Assert-PackContent -Content $validV1 -Schema 1 -ExpectedRows 1 `
        -Context 'valid v1 self-test')
    [void](Assert-PackContent -Content $validV2 -Schema 2 -ExpectedRows 1 `
        -Context 'valid v2 self-test')
    Assert-PackByteLength -Length $MaximumRuntimeCustomPackBytes `
        -Context 'exact byte-limit self-test'
    $byteLimitFailure = $null
    try {
        Assert-PackByteLength -Length ($MaximumRuntimeCustomPackBytes + 1) `
            -Context 'over byte-limit self-test'
    }
    catch {
        $byteLimitFailure = $_
    }
    if ($null -eq $byteLimitFailure) {
        throw 'Pack-data self-test accepted the runtime byte limit plus one.'
    }

    Assert-SelfTestRejected -Name 'BEL text' -Schema 1 -Content (
        "sample${tab}general${tab}general${tab}0${tab}Valid$([char]7) text")
    Assert-SelfTestRejected -Name 'DLE source' -Schema 1 -Content (
        "sam$([char]16)ple${tab}general${tab}general${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'DEL text' -Schema 1 -Content (
        "sample${tab}general${tab}general${tab}0${tab}$validText$([char]127)")
    Assert-SelfTestRejected -Name 'invalid source' -Schema 2 -Content (
        " sample${tab}life${tab}quip${tab}general${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'invalid topic' -Schema 2 -Content (
        "sample${tab}invalid${tab}quip${tab}general${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'invalid genre' -Schema 2 -Content (
        "sample${tab}life${tab}invalid${tab}general${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'invalid level' -Schema 2 -Content (
        "sample${tab}life${tab}quip${tab}invalid${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'invalid profanity' -Schema 2 -Content (
        "sample${tab}life${tab}quip${tab}general${tab}2${tab}$validText")
    Assert-SelfTestRejected -Name 'invalid text' -Schema 2 -Content (
        "sample${tab}life${tab}quip${tab}general${tab}0${tab}short")
    Assert-SelfTestRejected -Name 'invalid legacy category' -Schema 1 -Content (
        "sample${tab}invalid${tab}general${tab}0${tab}$validText")
    Assert-SelfTestRejected -Name 'schema field mismatch' -Schema 2 -Content $validV1

    Write-Host (
        'Pack-data fail-closed self-tests passed: BEL, DLE, DEL, source, topic, ' +
        'genre, level, profanity, text, legacy category, schema shape, and ' +
        'the exact runtime custom-pack byte boundary.'
    ) -ForegroundColor Green
}

if ($SelfTest) {
    Invoke-SelfTest
}

if (-not [string]::IsNullOrWhiteSpace($Path)) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pack data file not found: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    Assert-PackByteLength -Length $file.Length -Context $file.Name
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    try {
        $content = [IO.File]::ReadAllText($file.FullName, $strictUtf8)
    }
    catch {
        throw "Pack data is not strict UTF-8: $($_.Exception.Message)"
    }
    $rows = Assert-PackContent -Content $content -Schema $DataSchema `
        -ExpectedRows $ExpectedRowCount -Context $file.Name
    Write-Host (
        "Pack data verified: $($file.Name), schema v$DataSchema, $rows rows."
    ) -ForegroundColor Green
}
elseif (-not $SelfTest) {
    throw 'Specify -Path or -SelfTest.'
}
