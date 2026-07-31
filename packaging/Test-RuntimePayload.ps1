#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [string]$ReferenceRoot,
    [string]$ManifestPath,
    [string[]]$AllowedExtraFiles = @()
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
$resolvedPayloadRoot = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$actual = @(
    Get-ChildItem -LiteralPath $resolvedPayloadRoot -File -Recurse |
        ForEach-Object {
            $_.FullName.Substring($resolvedPayloadRoot.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        } |
        Sort-Object
)

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
Write-Host "Runtime payload verified: $($expected.Count) files$suffix." -ForegroundColor Green
