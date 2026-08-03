#requires -Version 5
<#
.SYNOPSIS
    Split the monolithic fortune packs into per-source packs and emit the
    collection grouping map.

.DESCRIPTION
    Each pack line is tab-separated with the per-show/per-author source tag in
    column 1. This partitions every pack by column 1 into packs\<source>.txt
    (content-preserving, line order kept), writes packs\collections.json (each
    original pack becomes a named collection listing its member sources), and
    removes the original monolithic files. Run New-ContentCatalog.ps1 afterward
    to regenerate catalog.json from the per-source files.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$packsRoot = Join-Path $RepoRoot 'packs'
if (-not (Test-Path -LiteralPath $packsRoot -PathType Container)) {
    throw "packs directory not found: $packsRoot"
}

# Collection identity carried over from the retired embedded packs.json. The key
# is the original monolithic pack file name (without extension).
$collectionMeta = [ordered]@{
    'dadjokes'       = @{ name = 'Dad Jokes';                desc = 'Groan-worthy puns from r/DadJokes (top-voted).';                 vibe = 'clean' }
    'bofh'           = @{ name = 'BOFH Excuses';             desc = 'The Bastard Operator From Hell excuse server.';                  vibe = 'clean' }
    'tech'           = @{ name = 'Tech & Hackers';           desc = 'Programming epigrams, hacker wisdom, RFC 1925, Larry Wall.';     vibe = 'clean' }
    'philosophy'     = @{ name = 'Philosophy & Wisdom';      desc = 'Classic & modern philosophy, Tao, aphorisms.';                   vibe = 'clean' }
    'literary'       = @{ name = 'Literary & Creative';      desc = 'Authors, artists, poets, Oblique Strategies.';                   vibe = 'clean' }
    'comedy'         = @{ name = 'Comedy & One-liners';      desc = 'Groucho, Jack Handey, Red Green, entertainers.';                 vibe = 'clean' }
    'facts'          = @{ name = 'Facts & Trivia';           desc = 'Real facts, Chuck Norris facts, fortune cookies.';               vibe = 'clean' }
    'tv-clean'       = @{ name = 'Pop-Culture TV (clean)';   desc = 'Simpsons, Futurama, MST3K, Star Trek, Firefly...';               vibe = 'pg13' }
    'tv-mature'      = @{ name = 'Pop-Culture TV (mature)';  desc = 'South Park, Sopranos, The Wire, Always Sunny... strong language.'; vibe = 'mature' }
    'showerthoughts' = @{ name = 'Reddit Showerthoughts';    desc = '~10k shower-thought observations.';                              vibe = 'mixed' }
    'spicy'          = @{ name = 'Spicy';                    desc = 'Yo-mama jokes, George Carlin, SubGenius, RAW.';                  vibe = 'edgy' }
    'nsfw'           = @{ name = 'NSFW (fortune -o)';        desc = 'The classic offensive fortune set, hate files removed. Adults only.'; vibe = 'nsfw' }
}
$license = 'LicenseRef-DesktopPet-Community'

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$collections = @()
$writtenFiles = @()
$sourcesSeen = @{}
$totalInputLines = 0
$totalOutputLines = 0

foreach ($packId in $collectionMeta.Keys) {
    $packFile = Join-Path $packsRoot "$packId.txt"
    if (-not (Test-Path -LiteralPath $packFile -PathType Leaf)) {
        throw "Monolithic pack is missing: $packFile"
    }
    $lines = @([IO.File]::ReadAllLines($packFile))
    $totalInputLines += $lines.Count

    # Partition by column 1 (source), preserving line order within each source.
    $bySource = [ordered]@{}
    foreach ($line in $lines) {
        if ($line.Length -eq 0) { continue }
        $tab = $line.IndexOf("`t")
        if ($tab -lt 1) { throw "Malformed line in $packId (no source column): $line" }
        $source = $line.Substring(0, $tab)
        if (-not $bySource.Contains($source)) {
            $bySource[$source] = New-Object 'System.Collections.Generic.List[string]'
        }
        $bySource[$source].Add($line)
    }

    $memberSources = @()
    foreach ($source in $bySource.Keys) {
        if ($sourcesSeen.ContainsKey($source)) {
            throw "Source '$source' appears in more than one pack ($($sourcesSeen[$source]) and $packId); split needs a disambiguation rule."
        }
        $sourcesSeen[$source] = $packId
        if ($source -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,62}[A-Za-z0-9]$' -and
            $source -notmatch '^[A-Za-z0-9]$') {
            throw "Source tag is not a safe id/filename: '$source'"
        }
        $memberSources += $source
        $outPath = Join-Path $packsRoot "$source.txt"
        $body = ($bySource[$source] -join "`n") + "`n"
        if (-not $WhatIfOnly) {
            [IO.File]::WriteAllText($outPath, $body, $utf8NoBom)
        }
        $writtenFiles += "$source.txt"
        $totalOutputLines += $bySource[$source].Count
    }

    $meta = $collectionMeta[$packId]
    $collections += [ordered]@{
        id      = $packId
        name    = $meta.name
        desc    = $meta.desc
        vibe    = $meta.vibe
        license = $license
        sources = @($memberSources | Sort-Object)
    }
}

if ($totalInputLines -ne $totalOutputLines) {
    throw "Line count changed during split: in=$totalInputLines out=$totalOutputLines"
}

$collectionsDoc = [ordered]@{
    version     = 1
    collections = @($collections)
}
$collectionsPath = Join-Path $packsRoot 'collections.json'
if (-not $WhatIfOnly) {
    [IO.File]::WriteAllText(
        $collectionsPath,
        ($collectionsDoc | ConvertTo-Json -Depth 6),
        $utf8NoBom)

    # Remove the monolithic packs now that every line lives in a per-source file.
    # Skip single-source packs whose only source shares the pack's name: that file
    # IS its per-source pack (deleting it would discard the content just written).
    foreach ($packId in $collectionMeta.Keys) {
        if ($sourcesSeen.ContainsKey($packId)) { continue }
        Remove-Item -LiteralPath (Join-Path $packsRoot "$packId.txt") -Force
    }
}

Write-Host (
    "Split {0} collections -> {1} per-source packs ({2} lines preserved)." -f
    $collections.Count, $writtenFiles.Count, $totalOutputLines) -ForegroundColor Green
if ($WhatIfOnly) { Write-Host '(WhatIfOnly: no files written or removed)' -ForegroundColor Yellow }
