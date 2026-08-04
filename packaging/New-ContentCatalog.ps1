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
    Pack collection/group metadata (name/desc/license) is reused from
    packs\collections.json; pet authors from Pets\pets.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Branch = 'master',
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

# raw.githubusercontent.com serves the git blob verbatim, and these assets were
# committed with mixed line endings (some CRLF, some LF), so neither the
# working-tree copy nor a normalized copy is universally correct. Hash the actual
# committed blob. If the file is not yet committed (a brand-new pet/pack), fall
# back to the LF-normalized working-tree bytes, matching how git stores a new
# text asset on commit (.gitattributes: * text=auto eol=lf).
function Get-CatalogAsset([string]$RepoRoot, [string]$RelPath, [string]$FullPath) {
    $bytes = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'git'
        $psi.Arguments = "-C `"$RepoRoot`" cat-file blob `"HEAD:$RelPath`""
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        $memory = New-Object System.IO.MemoryStream
        $process.StandardOutput.BaseStream.CopyTo($memory)
        [void]$process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -eq 0) { $bytes = $memory.ToArray() }
    }
    catch {
        $bytes = $null
    }

    if ($null -eq $bytes) {
        $raw = [IO.File]::ReadAllBytes($FullPath)
        $out = New-Object 'System.Collections.Generic.List[byte]' ($raw.Length)
        for ($i = 0; $i -lt $raw.Length; $i++) {
            if ($raw[$i] -eq 13 -and ($i + 1) -lt $raw.Length -and $raw[$i + 1] -eq 10) {
                continue   # drop CR in a CRLF pair; git stores LF for a new text file
            }
            $out.Add($raw[$i])
        }
        $bytes = $out.ToArray()
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    return [pscustomobject]@{ Sha256 = $hash; Bytes = $bytes.Length }
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
    $asset = Get-CatalogAsset $RepoRoot "Pets/$id/animations.xml" $xml
    $pets += [ordered]@{
        id     = $id
        name   = Get-PrettyName $id
        author = if ($authors.ContainsKey($id)) { $authors[$id] } else { '' }
        url    = "$rawBase/Pets/$id/animations.xml"
        sha256 = $asset.Sha256
        bytes  = $asset.Bytes
    }
}

# --- packs (per-source; collection metadata from packs\collections.json) -----
$packsRoot = Join-Path $RepoRoot 'packs'
$sourceCollection = @{}
foreach ($c in
    (Get-Content -LiteralPath (Join-Path $packsRoot 'collections.json') -Raw |
        ConvertFrom-Json).collections) {
    foreach ($src in $c.sources) {
        $sourceCollection[[string]$src] = $c
    }
}
$packs = @()
foreach ($file in
    (Get-ChildItem -LiteralPath $packsRoot -Filter '*.txt' -File | Sort-Object Name)) {
    $id = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    if (-not $sourceCollection.ContainsKey($id)) {
        throw "Pack '$id' has no collection in collections.json (add an entry for it there)."
    }
    $collection = $sourceCollection[$id]
    $lineCount = @(Get-Content -LiteralPath $file.FullName).Count
    $asset = Get-CatalogAsset $RepoRoot "packs/$id.txt" $file.FullName
    $packs += [ordered]@{
        id         = $id
        name       = Get-PrettyName $id
        group      = [string]$collection.name
        desc       = ''
        license    = [string]$collection.license
        url        = "$rawBase/packs/$id.txt"
        sha256     = $asset.Sha256
        bytes      = $asset.Bytes
        count      = $lineCount
        dataSchema = 2
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
