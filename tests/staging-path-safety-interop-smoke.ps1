#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Staging path-safety interop smoke test requires Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')

$tempRoot = Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot (
    'DesktopPet-StagingInterop-' + [Guid]::NewGuid().ToString('N'))
$private = Join-Path $scratch 'private'
$published = Join-Path $scratch 'published'

try {
    New-Item -ItemType Directory -Path $private, $published | Out-Null
    $destination = Join-Path $published 'résumé-文件.bin'
    foreach ($content in @('first-unicode-publication', 'replacement-bytes')) {
        $temporary = Join-Path $private (
            [Guid]::NewGuid().ToString('N') + '-tęmp-文件.tmp')
        [IO.File]::WriteAllText(
            $temporary,
            $content,
            (New-Object Text.UTF8Encoding($false)))
        [void](Publish-DesktopPetAtomicFile `
            -TemporaryPath $temporary `
            -DestinationPath $destination `
            -TrustedRoot $scratch)
        if ([IO.File]::ReadAllText($destination) -cne $content) {
            throw 'Retained-handle Unicode publication returned wrong bytes.'
        }
    }

    $stage = Join-Path $scratch 'reset-stage'
    Reset-DesktopPetStagingDirectory `
        -Path $stage `
        -AllowedRoot $scratch `
        -TrustedRoot $tempRoot
    [IO.File]::WriteAllText(
        (Join-Path $stage 'payload.bin'),
        'delete-on-reset')
    Reset-DesktopPetStagingDirectory `
        -Path $stage `
        -AllowedRoot $scratch `
        -TrustedRoot $tempRoot
    if (@(Get-ChildItem -LiteralPath $stage -Force).Count -ne 0) {
        throw 'Retained-handle reset did not recreate an empty directory.'
    }

    Write-Host (
        'PASS: fresh x{0} retained-handle publish/create/delete interop.' -f
        ([IntPtr]::Size * 8)
    ) -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
