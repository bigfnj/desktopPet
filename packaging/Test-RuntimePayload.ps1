#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [string]$ReferenceRoot,
    [string]$ManifestPath,
    [string[]]$AllowedExtraFiles = @(),
    # Top-level directory names (for example 'pets', 'fortunes') whose entire
    # subtree is permitted beyond the flat manifest. Used only for the portable
    # ZIP, which bundles offline content; the MSI payload passes none.
    [string[]]$AllowedExtraDirectories = @()
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = if (
    -not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
. (Join-Path $scriptDirectory 'StagingPathSafety.ps1')

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $scriptDirectory 'runtime-files.txt'
}

if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
    throw "Payload directory not found: $PayloadRoot"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Runtime payload manifest not found: $ManifestPath"
}

$expected = @(
    Get-Content -LiteralPath $ManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') } |
        Sort-Object
)
$allowed = @(
    $AllowedExtraFiles |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Sort-Object -Unique
)
foreach ($name in $expected) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name)) {
        throw "Runtime manifest entry is unsafe: '$name'"
    }
}
foreach ($name in $allowed) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name) -or
        $expected -contains $name) {
        throw "Allowed extra payload entry is unsafe or duplicates the manifest: '$name'"
    }
}
$allowedDirectories = @(
    $AllowedExtraDirectories |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Sort-Object -Unique
)
foreach ($directory in $allowedDirectories) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $directory)) {
        throw "Allowed extra payload directory is unsafe: '$directory'"
    }
}
$resolvedPayloadRoot = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$actualAll = @(
    Get-ChildItem -LiteralPath $resolvedPayloadRoot -File -Recurse |
        ForEach-Object {
            $_.FullName.Substring($resolvedPayloadRoot.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        }
)
# Files inside an allowed bundled-content directory are verified only for safe
# names, not for exact manifest membership; the flat runtime set is still exact.
$bundledCount = 0
$actual = @(
    foreach ($relative in $actualAll) {
        $segments = @($relative -split '[\\/]')
        if ($segments.Count -gt 1 -and
            $allowedDirectories -contains $segments[0]) {
            foreach ($segment in $segments) {
                if (-not (Test-DesktopPetWindowsLeafName -Name $segment)) {
                    throw "Bundled content payload has an unsafe path: '$relative'"
                }
            }
            $bundledCount++
            continue
        }
        $relative
    }
) | Sort-Object

$completeExpected = @($expected + $allowed | Sort-Object)
$difference = @(Compare-Object $completeExpected $actual)
if ($difference.Count -gt 0) {
    $detail = ($difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; '
    throw "Runtime payload differs from manifest: $detail"
}

if ($ReferenceRoot) {
    if (-not (Test-Path -LiteralPath $ReferenceRoot -PathType Container)) {
        throw "Reference runtime directory not found: $ReferenceRoot"
    }
    foreach ($name in $expected) {
        $payloadHash = (Get-FileHash -LiteralPath (Join-Path $PayloadRoot $name) -Algorithm SHA256).Hash
        $referenceHash = (Get-FileHash -LiteralPath (Join-Path $ReferenceRoot $name) -Algorithm SHA256).Hash
        if ($payloadHash -ne $referenceHash) {
            throw "Runtime payload hash mismatch for '$name'."
        }
    }
}

$suffix = if ($allowed.Count -gt 0) {
    " plus $($allowed.Count) package-specific file(s)"
}
else {
    ''
}
if ($bundledCount -gt 0) {
    $suffix += " plus $bundledCount bundled content file(s)"
}
Write-Host "Runtime payload verified: $($expected.Count) files$suffix." -ForegroundColor Green
