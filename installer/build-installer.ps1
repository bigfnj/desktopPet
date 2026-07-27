#requires -Version 5
<#
.SYNOPSIS
    Build the DesktopPet AI Edition MSI (per-user, no admin) from a built exe.

.DESCRIPTION
    Wraps `wix build`. Supplies the bindpaths (so DesktopPet.wxs can reference
    DesktopPet.exe + license.rtf by bare name) and the WiX UI extension. Build the
    app first: ..\build.ps1 -Release   (or pass -Config Debug).

    Requires the WiX .NET tool:  dotnet tool install --global wix

.EXAMPLE
    .\build-installer.ps1              # packages the Release x64 exe
.EXAMPLE
    .\build-installer.ps1 -Config Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path $here -Parent

$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) {
    $cmd = Get-Command wix -ErrorAction SilentlyContinue
    if ($cmd) { $wix = $cmd.Source } else { throw "wix not found. Install it: dotnet tool install --global wix" }
}

$exeDir = if ($Config -eq 'Release') {
    Join-Path $root 'build\DesktopPetPortable\bin\Release\x64'
} else {
    Join-Path $root 'build\DesktopPetPortable\bin\Debug'
}
$exe = Join-Path $exeDir 'DesktopPet.exe'
if (-not (Test-Path $exe)) { throw "$exe not found - build the app first (run ..\build.ps1 -$Config)." }

# Ensure the WiX UI extension is available (idempotent; ignore 'already added').
try { & $wix extension add -g WixToolset.UI.wixext 2>&1 | Out-Null } catch { }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$msi = Join-Path $dist 'DesktopPet-AI-Edition.msi'

Write-Host "wix   : $wix"    -ForegroundColor DarkGray
Write-Host "exe   : $exe"    -ForegroundColor DarkGray
Write-Host "out   : $msi"    -ForegroundColor DarkGray

& $wix build (Join-Path $here 'DesktopPet.wxs') `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -bindpath $exeDir `
    -bindpath $here `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host ("MSI -> {0} ({1:N0} KB)" -f $msi, ((Get-Item $msi).Length / 1KB)) -ForegroundColor Green
