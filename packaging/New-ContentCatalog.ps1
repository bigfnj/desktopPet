#requires -Version 5
<#
.SYNOPSIS
    Regenerate catalog.json, the runtime-fetched content catalog for online pet /
    fortune-pack downloads.

.DESCRIPTION
    Lists every pet skin (Pets\<id>\animations.xml) and every fortune pack
    (packs\<id>.txt) with a branch-pinned raw.githubusercontent.com URL plus the
    SHA-256 and byte size of the current file. The app fetches this over HTTPS and
    verifies every download against the recorded hash before install, so content
    added to the repo appears live without shipping a new build.

    Text files are LF-normalized (.gitattributes eol=lf), so the working-tree hash
    equals the git blob that raw.githubusercontent.com serves. Run this whenever
    you add or change a pet or pack, then commit catalog.json alongside the files.
    Pack metadata (name/desc/license/schema) is reused from packs\packs.json;
    pet authors from Pets\pets.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Branch = 'main',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot 'catalog.json'
}
if ($Branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') {
    throw "Unsafe branch ref: '$Branch'"
}
$owner = 'bigfnj'
$repo = 'desktopPet'
$rawBase = "https://raw.githubusercontent.com/$owner/$repo/$Branch"

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-PrettyName([string]$Id) {
    $parts = @($Id -split '[_-]' | Where-Object { $_ })
    (($parts | ForEach-Object {
        $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
    }) -join ' ')
}

# --- pets --------------------------------------------------------------------
$petsRoot = Join-Path $RepoRoot 'Pets'
$authors = @{}
$petsJson = Join-Path $petsRoot 'pets.json'
if (Test-Path -LiteralPath $petsJson) {
    foreach ($p in (Get-Content -LiteralPath $petsJson -Raw | ConvertFrom-Json).pets) {
        $authors[[string]$p.folder] = [string]$p.author
    }
}
$pets = @()
foreach ($dir in (Get-ChildItem -LiteralPath $petsRoot -Directory | Sort-Object Name)) {
    $xml = Join-Path $dir.FullName 'animations.xml'
    if (-not (Test-Path -LiteralPath $xml -PathType Leaf)) { continue }
    $id = $dir.Name
    $pets += [ordered]@{
        id     = $id
        name   = Get-PrettyName $id
        author = if ($authors.ContainsKey($id)) { $authors[$id] } else { '' }
        url    = "$rawBase/Pets/$id/animations.xml"
        sha256 = Get-Sha256 $xml
        bytes  = [int](Get-Item -LiteralPath $xml).Length
    }
}

# --- packs (metadata from packs\packs.json, hashes recomputed) ---------------
$packsRoot = Join-Path $RepoRoot 'packs'
$packMeta = @{}
foreach ($p in
    (Get-Content -LiteralPath (Join-Path $packsRoot 'packs.json') -Raw |
        ConvertFrom-Json).packs) {
    $packMeta[[string]$p.id] = $p
}
$packs = @()
foreach ($file in
    (Get-ChildItem -LiteralPath $packsRoot -Filter '*.txt' -File | Sort-Object Name)) {
    $id = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    $meta = if ($packMeta.ContainsKey($id)) { $packMeta[$id] } else { $null }
    $lineCount = @(Get-Content -LiteralPath $file.FullName).Count
    $packs += [ordered]@{
        id         = $id
        name       = if ($meta) { [string]$meta.name } else { Get-PrettyName $id }
        desc       = if ($meta) { [string]$meta.desc } else { '' }
        license    = if ($meta) { [string]$meta.license } else { 'LicenseRef-DesktopPet-Community' }
        url        = "$rawBase/packs/$id.txt"
        sha256     = Get-Sha256 $file.FullName
        bytes      = [int]$file.Length
        count      = if ($meta -and $meta.count) { [int]$meta.count } else { $lineCount }
        dataSchema = if ($meta -and $meta.dataSchema) { [int]$meta.dataSchema } else { 2 }
    }
}

# Force arrays so a single entry still serializes as a JSON array.
$catalog = [ordered]@{
    version = 1
    pets    = @($pets)
    packs   = @($packs)
}
$json = $catalog | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $OutputPath,
    $json,
    (New-Object Text.UTF8Encoding($false)))
Write-Host (
    "Wrote $($pets.Count) pets + $($packs.Count) packs to $OutputPath" ) `
    -ForegroundColor Green
