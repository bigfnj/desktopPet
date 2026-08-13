#requires -Version 5
<#
.SYNOPSIS
    Fail when a module's published zip is older than the module's source.

.DESCRIPTION
    modules-dist\<id>.zip is the payload the in-app catalog downloads and installs, and it is a
    committed artifact: nothing rebuilds it automatically, so it silently rots whenever module
    source lands without someone remembering to re-run New-ModuleDistZip.ps1 + New-ContentCatalog.ps1.

    That is not hypothetical. Twice in one day:
      * fortunes.zip shipped without the built-in fortune corpus, because the S3 move dropped the
        EmbeddedResource from the base csproj and the module never picked it up. A lean install had
        nothing to say, and nothing anywhere reported it.
      * aibrain.zip sat one release behind PR #71, so every catalog install got an AI Brain with no
        Windows OCR fallback -- i.e. no screen reading at all without a separate Tesseract install.

    The check is deliberately git-based rather than a rebuild-and-compare-hashes: comparing hashes
    would need the module DLLs to build byte-identically across SDK versions and checkout paths,
    which is a stronger promise than this repo makes today. Commit ordering is exact, cheap, and
    environment-independent -- if modules/<Id>/ has a commit newer than the newest commit touching
    modules-dist/<id>.zip, the published payload cannot contain that change.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER ModuleId
    Check only this module. Defaults to every id listed in modules-dist\modules.json.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ModuleId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

function Get-LastCommit([string]$path) {
    # -- separates the pathspec from revisions, so a path that looks like a ref cannot be mistaken
    # for one. Empty output = the path has no commits (untracked or never committed).
    $sha = & git -C $RepoRoot log -1 --format=%H -- $path 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git log failed for '$path'." }
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    return $sha.Trim()
}

$modulesJson = Join-Path $RepoRoot 'modules-dist\modules.json'
if (-not (Test-Path -LiteralPath $modulesJson)) {
    throw "modules.json not found at $modulesJson"
}

$ids = @()
foreach ($m in (Get-Content -LiteralPath $modulesJson -Raw | ConvertFrom-Json).modules) {
    $ids += $m.id
}
if ($ModuleId) {
    if ($ids -notcontains $ModuleId) { throw "Module '$ModuleId' is not listed in modules.json." }
    $ids = @($ModuleId)
}

$stale = @()
foreach ($id in $ids) {
    # The module's source directory is capitalized (modules\Fortunes) while its id and zip are not;
    # resolve the real directory rather than assuming either casing survives a case-sensitive host.
    $sourceDirectory = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'modules') -Directory |
        Where-Object { $_.Name -ieq $id } |
        Select-Object -First 1
    if (-not $sourceDirectory) {
        throw "Module '$id' is listed in modules.json but has no source directory under modules\."
    }

    $zipRelative = "modules-dist/$id.zip"
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $zipRelative))) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is missing" }
        continue
    }

    $zipCommit = Get-LastCommit $zipRelative
    if (-not $zipCommit) {
        $stale += [pscustomobject]@{ Id = $id; Reason = "$zipRelative is not committed" }
        continue
    }

    # Commits touching the module's source that the published zip cannot possibly contain. Markdown is
    # excluded because it never reaches the assembly -- modules\Fortunes\BACKLOG.md would otherwise
    # demand a 31 MB republish for a note. Everything else stays in scope on purpose: images and
    # welcome.json are embedded resources, and probe/self-test code compiles into the shipped DLL just
    # like anything else, so it genuinely does make the published payload stale.
    $sourceRelative = "modules/$($sourceDirectory.Name)"
    $newer = @(& git -C $RepoRoot log --format='%h %s' "$zipCommit..HEAD" -- $sourceRelative ":(exclude)$sourceRelative/**/*.md" ":(exclude)$sourceRelative/*.md")
    if ($LASTEXITCODE -ne 0) { throw "git log failed comparing '$sourceRelative' against $zipCommit." }

    if ($newer.Count -gt 0) {
        $stale += [pscustomobject]@{
            Id     = $id
            Reason = "$sourceRelative has $($newer.Count) commit(s) newer than $zipRelative"
            Detail = $newer
        }
    } else {
        Write-Host "OK   $id -- $zipRelative is current with $sourceRelative"
    }
}

if ($stale.Count -gt 0) {
    Write-Host ''
    foreach ($s in $stale) {
        Write-Host "STALE $($s.Id) -- $($s.Reason)"
        if ($s.PSObject.Properties.Name -contains 'Detail') {
            foreach ($line in $s.Detail) { Write-Host "        $line" }
        }
    }
    Write-Host ''
    throw ("Published module payload(s) are behind their source: " +
           ($stale.Id -join ', ') +
           ". Rebuild (build.ps1 -Release), re-zip (packaging\New-ModuleDistZip.ps1), COMMIT the zip, " +
           "then regenerate the catalog (packaging\New-ContentCatalog.ps1) -- in that order, because " +
           "the catalog hashes the committed blob.")
}

Write-Host "All $($ids.Count) published module payload(s) are current with their source."
