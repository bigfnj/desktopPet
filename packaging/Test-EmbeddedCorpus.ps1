#requires -Version 5
[CmdletBinding()]
param(
    [string]$CorpusPath,
    [switch]$AllowKnownDuplicate,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MaximumCorpusBytes = 64MB
$MaximumLineBytes = 64KB
$MaximumRows = 200000
$KnownDuplicateSha256 =
    '2130d2c0da0bffd1c834981dca76c99fad2a8f479cca40e1968baf811d06289e'
$Utf8Strict = New-Object Text.UTF8Encoding($false, $true)
$Topics = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($value in @(
        'tech', 'science', 'work-money', 'love', 'family', 'faith',
        'society', 'food', 'nature', 'arts', 'health-body', 'life')) {
    [void]$Topics.Add($value)
}
$Genres = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($value in @(
        'tv-quote', 'observation', 'joke', 'pun', 'quip', 'aphorism',
        'wisdom', 'fact', 'insult', 'verse', 'dark', 'uplifting')) {
    [void]$Genres.Add($value)
}

function Get-ByteSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
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

function Assert-TrimmedBoundedField {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][int]$MinimumLength,
        [Parameter(Mandatory = $true)][int]$MaximumLength,
        [Parameter(Mandatory = $true)][int]$LineNumber
    )

    if ($Value.Length -lt $MinimumLength -or
        $Value.Length -gt $MaximumLength) {
        throw (
            "Embedded corpus line $LineNumber has $Name length " +
            "$($Value.Length); expected $MinimumLength..$MaximumLength."
        )
    }
    if ($Value -cne $Value.Trim()) {
        throw "Embedded corpus line $LineNumber has untrimmed $Name."
    }
    if (Test-ContainsControlCharacter -Value $Value) {
        throw "Embedded corpus line $LineNumber has a control character in $Name."
    }
}

function Assert-EmbeddedCorpus {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$PermitKnownDuplicate
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $item = Get-Item -LiteralPath $resolvedPath -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Embedded corpus must be a regular, non-reparse file: $resolvedPath"
    }
    if ($item.Length -lt 1 -or $item.Length -gt $MaximumCorpusBytes) {
        throw (
            "Embedded corpus byte length $($item.Length) is outside 1.." +
            "$MaximumCorpusBytes."
        )
    }

    [byte[]]$bytes = [IO.File]::ReadAllBytes($resolvedPath)
    try {
        $content = $Utf8Strict.GetString($bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw "Embedded corpus is not strict UTF-8: $resolvedPath"
    }
    $normalized = $content.Replace("`r`n", "`n")
    if ($normalized.IndexOf("`r", [StringComparison]::Ordinal) -ge 0) {
        throw 'Embedded corpus contains a bare carriage return.'
    }
    if ($normalized.EndsWith("`n", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if ($normalized.Length -eq 0) {
        throw 'Embedded corpus contains no rows.'
    }

    [string[]]$lines = $normalized.Split([char]10)
    if ($lines.Count -gt $MaximumRows) {
        throw (
            "Embedded corpus row count $($lines.Count) exceeds " +
            "$MaximumRows."
        )
    }

    $seenRows = New-Object 'Collections.Generic.Dictionary[string,int]' (
        [StringComparer]::Ordinal)
    $seenTexts = New-Object 'Collections.Generic.Dictionary[string,int]' (
        [StringComparer]::Ordinal)
    $knownDuplicateCount = 0
    $crossMetadataTextDuplicateCount = 0

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $lineNumber = $index + 1
        $line = $lines[$index]
        $lineBytes = $Utf8Strict.GetBytes($line)
        if ($lineBytes.Length -gt $MaximumLineBytes) {
            throw (
                "Embedded corpus line $lineNumber is $($lineBytes.Length) " +
                "bytes; limit is $MaximumLineBytes."
            )
        }
        [string[]]$fields = $line.Split([char]9)
        if ($fields.Count -ne 6) {
            throw (
                "Embedded corpus line $lineNumber has $($fields.Count) " +
                'fields; schema v2 requires exactly 6.'
            )
        }

        Assert-TrimmedBoundedField -Name 'source' -Value $fields[0] `
            -MinimumLength 1 -MaximumLength 128 -LineNumber $lineNumber
        if (-not $Topics.Contains($fields[1])) {
            throw (
                "Embedded corpus line $lineNumber has unsupported topic " +
                "'$($fields[1])'."
            )
        }
        if (-not $Genres.Contains($fields[2])) {
            throw (
                "Embedded corpus line $lineNumber has unsupported genre " +
                "'$($fields[2])'."
            )
        }
        if ($fields[3] -cnotin @('general', 'edgy', 'nsfw')) {
            throw (
                "Embedded corpus line $lineNumber has invalid level " +
                "'$($fields[3])'."
            )
        }
        if ($fields[4] -cnotin @('0', '1')) {
            throw (
                "Embedded corpus line $lineNumber has invalid profanity " +
                "flag '$($fields[4])'."
            )
        }
        Assert-TrimmedBoundedField -Name 'text' -Value $fields[5] `
            -MinimumLength 8 -MaximumLength 280 -LineNumber $lineNumber

        $duplicateRow = $seenRows.ContainsKey($line)
        $duplicateText = $seenTexts.ContainsKey($fields[5])
        if ($duplicateRow) {
            $rowHash = Get-ByteSha256 -Bytes $lineBytes
            $knownDuplicate = (
                $duplicateText -and
                $rowHash -ceq $KnownDuplicateSha256
            )
            if ($knownDuplicate) {
                if (-not $PermitKnownDuplicate) {
                    throw (
                        'Known duplicate release blocker: embedded corpus ' +
                        "lines $($seenRows[$line]) and $lineNumber have row " +
                        "SHA-256 $KnownDuplicateSha256."
                    )
                }
                $knownDuplicateCount++
                if ($knownDuplicateCount -gt 1) {
                    throw (
                        'Known duplicate row occurs more than twice; the ' +
                        'development exception permits exactly one duplicate.'
                    )
                }
                continue
            }
            throw (
                "Embedded corpus line $lineNumber duplicates full row " +
                "$($seenRows[$line])."
            )
        }

        $seenRows.Add($line, $lineNumber)
        # Identical spoken text with distinct source/topic/genre/severity metadata is retained as
        # separate provenance. Count that condition, but require exact full rows to be unique.
        if ($duplicateText) {
            $crossMetadataTextDuplicateCount++
        }
        else {
            $seenTexts.Add($fields[5], $lineNumber)
        }
    }

    $duplicateStatus = if ($knownDuplicateCount -eq 1) {
        ' Known protected duplicate explicitly allowed for development only.'
    }
    else {
        ''
    }
    Write-Host (
        "Embedded corpus validated: $($lines.Count) schema-v2 rows, " +
        "$($bytes.Length) bytes, $crossMetadataTextDuplicateCount " +
        "cross-metadata text duplicate(s).$duplicateStatus"
    ) -ForegroundColor Green
}

function Invoke-EmbeddedCorpusSelfTest {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) (
        'DesktopPet-EmbeddedCorpus-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        function Write-Utf8Fixture {
            param(
                [Parameter(Mandatory = $true)][string]$Name,
                [Parameter(Mandatory = $true)][string]$Content
            )
            $path = Join-Path $scratch $Name
            [IO.File]::WriteAllBytes($path, $Utf8Strict.GetBytes($Content))
            return $path
        }
        function Assert-Rejected {
            param(
                [Parameter(Mandatory = $true)][string]$Name,
                [Parameter(Mandatory = $true)][scriptblock]$Operation,
                [Parameter(Mandatory = $true)][string]$ExpectedMessage
            )
            $accepted = $true
            $message = ''
            try {
                & $Operation *> $null
            }
            catch {
                $accepted = $false
                $message = $_.Exception.Message
            }
            if ($accepted) {
                throw "Embedded-corpus negative control was accepted: $Name"
            }
            if ($message -notmatch $ExpectedMessage) {
                throw (
                    "Embedded-corpus control '$Name' failed unexpectedly: " +
                    $message
                )
            }
        }

        $tab = [char]9
        $validRow =
            "sample${tab}life${tab}quip${tab}general${tab}0${tab}A valid fixture fortune."
        $validPath = Write-Utf8Fixture -Name 'valid.txt' -Content $validRow
        Assert-EmbeddedCorpus -Path $validPath

        $invalidUtf8Path = Join-Path $scratch 'invalid-utf8.txt'
        [IO.File]::WriteAllBytes(
            $invalidUtf8Path,
            [byte[]](0x73, 0x61, 0x6d, 0x70, 0x6c, 0x65, 0xff))
        Assert-Rejected -Name 'invalid UTF-8' -ExpectedMessage 'strict UTF-8' `
            -Operation { Assert-EmbeddedCorpus -Path $invalidUtf8Path }

        $badSchema = Write-Utf8Fixture -Name 'bad-schema.txt' -Content (
            "sample${tab}life${tab}quip${tab}general${tab}A missing field.")
        Assert-Rejected -Name 'schema shape' -ExpectedMessage 'exactly 6' `
            -Operation { Assert-EmbeddedCorpus -Path $badSchema }

        $badTopic = Write-Utf8Fixture -Name 'bad-topic.txt' -Content (
            "sample${tab}nope${tab}quip${tab}general${tab}0${tab}A valid fixture fortune.")
        Assert-Rejected -Name 'unsupported topic' -ExpectedMessage 'unsupported topic' `
            -Operation { Assert-EmbeddedCorpus -Path $badTopic }

        $badGenre = Write-Utf8Fixture -Name 'bad-genre.txt' -Content (
            "sample${tab}life${tab}nope${tab}general${tab}0${tab}A valid fixture fortune.")
        Assert-Rejected -Name 'unsupported genre' -ExpectedMessage 'unsupported genre' `
            -Operation { Assert-EmbeddedCorpus -Path $badGenre }

        $controlPath = Write-Utf8Fixture -Name 'control.txt' -Content (
            "sam$([char]1)ple${tab}life${tab}quip${tab}general${tab}0${tab}" +
            'A valid fixture fortune.')
        Assert-Rejected -Name 'control character' `
            -ExpectedMessage 'control character' `
            -Operation { Assert-EmbeddedCorpus -Path $controlPath }

        $duplicatePath = Write-Utf8Fixture -Name 'duplicate.txt' -Content (
            "$validRow`n$validRow")
        Assert-Rejected -Name 'ordinary duplicate' `
            -ExpectedMessage 'duplicates full row' `
            -Operation { Assert-EmbeddedCorpus -Path $duplicatePath }

        $otherSourceRow =
            "other${tab}life${tab}quip${tab}general${tab}0${tab}A valid fixture fortune."
        $duplicateTextPath = Write-Utf8Fixture `
            -Name 'duplicate-text.txt' -Content "$validRow`n$otherSourceRow"
        Assert-EmbeddedCorpus -Path $duplicateTextPath

        $knownRow = (
            "showerthoughts${tab}arts${tab}observation${tab}general${tab}0${tab}" +
            'The laugh track in "How I Met Your Mother" would make more ' +
            'sense if it were two kids laughing, rather than a studio audience.'
        )
        if ((Get-ByteSha256 -Bytes $Utf8Strict.GetBytes($knownRow)) -cne
            $KnownDuplicateSha256) {
            throw 'Self-test known-duplicate fixture does not match its pin.'
        }
        $knownPath = Write-Utf8Fixture -Name 'known-duplicate.txt' `
            -Content "$knownRow`n$knownRow"
        Assert-Rejected -Name 'known duplicate release mode' `
            -ExpectedMessage 'Known duplicate release blocker' `
            -Operation { Assert-EmbeddedCorpus -Path $knownPath }
        Assert-EmbeddedCorpus -Path $knownPath -PermitKnownDuplicate

        $overlongText = 'x' * 281
        $overlongPath = Write-Utf8Fixture -Name 'overlong.txt' -Content (
            "sample${tab}life${tab}quip${tab}general${tab}0${tab}$overlongText")
        Assert-Rejected -Name 'overlong text' -ExpectedMessage 'text length' `
            -Operation { Assert-EmbeddedCorpus -Path $overlongPath }

        Write-Host (
            'Embedded-corpus self-tests passed: strict UTF-8, exact schema, ' +
            'topic/genre, control, bounds, full-row uniqueness, cross-metadata ' +
            'text provenance, and known-blocker controls.'
        ) -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $scratch) {
            $resolvedScratch = [IO.Path]::GetFullPath($scratch)
            $resolvedTemp = [IO.Path]::GetFullPath(
                [IO.Path]::GetTempPath()).TrimEnd('\')
            if (-not $resolvedScratch.StartsWith(
                    $resolvedTemp + '\DesktopPet-EmbeddedCorpus-',
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove unsafe self-test path: $resolvedScratch"
            }
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-EmbeddedCorpusSelfTest
}
if (-not [string]::IsNullOrWhiteSpace($CorpusPath)) {
    Assert-EmbeddedCorpus -Path $CorpusPath `
        -PermitKnownDuplicate:$AllowKnownDuplicate
}
elseif (-not $SelfTest) {
    throw 'Specify -CorpusPath or -SelfTest.'
}
