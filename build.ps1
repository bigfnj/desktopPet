#requires -Version 5
<#
.SYNOPSIS
    Build (and optionally run) the DesktopPet "AI Edition" portable app.

.DESCRIPTION
    One command that encodes this repo's build tribal-knowledge so you never
    have to remember the flags again:

      * kills the running pet first  (the process is named 'eSheep' and it
        LOCKS the output exe -> MSB3027 on rebuild otherwise)
      * restores the NuGet PackageReference set (to the global packages folder)
      * builds the PORTABLE project directly as x64
          - NOT the .sln (DesktopPet.sln drags in the UWP project -> needs a
            UWP workload). Use DesktopPet_Portable.sln if you must open one.
          - NOT AnyCPU (Debug|AnyCPU errors "OutputPath not set").

    The portable build (DesktopPet_Portable.csproj -> DesktopPet.exe) is THE
    product; all the AI-Edition work lives here. Dead/legacy build flavors
    (classic eSheep.exe, UWP Store) are quarantined under src/legacy/.

.EXAMPLE
    .\build.ps1            # Debug x64 build
.EXAMPLE
    .\build.ps1 -Run       # build, then launch the pet
.EXAMPLE
    .\build.ps1 -Release   # Release x64
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Release,
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$proj   = Join-Path $srcDir 'DesktopPet_Portable.csproj'
$config = if ($Release) { 'Release' } else { 'Debug' }
$exe    = if ($Release) {
    Join-Path $root 'build\DesktopPetPortable\bin\Release\x64\DesktopPet.exe'
} else {
    Join-Path $root 'build\DesktopPetPortable\bin\Debug\DesktopPet.exe'
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild `
                 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($p -and (Test-Path $p)) { return $p }
    }
    $known = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $known) { return $known }
    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "MSBuild.exe not found. Install VS with the .NET desktop-development workload, or put MSBuild on PATH."
}

$msb = Find-MSBuild
Write-Host "MSBuild : $msb"    -ForegroundColor DarkGray
Write-Host "Project : $proj"   -ForegroundColor DarkGray

# The running pet is named 'eSheep' and locks the output exe. Kill it before building.
# (Do NOT use -ErrorAction Stop here: the non-matching 'DesktopPet' name would throw
#  and skip the kill, leaving the exe locked.)
$pets = Get-Process -Name eSheep,DesktopPet -ErrorAction SilentlyContinue
if ($pets) {
    Write-Host "Stopping running pet ($($pets.Count)) ..." -ForegroundColor Yellow
    $pets | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

$common = @("-p:Configuration=$config", '-p:Platform=x64', "-p:SolutionDir=$srcDir\", '-nologo', '-v:minimal')

if ($Clean) {
    Write-Host "Cleaning ..." -ForegroundColor Cyan
    & $msb $proj -t:clean @common
}

if (-not $NoRestore) {
    Write-Host "Restoring NuGet packages ..." -ForegroundColor Cyan
    & $msb $proj -t:restore @common
    if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }
}

Write-Host "Building $config|x64 ..." -ForegroundColor Cyan
& $msb $proj -t:build @common
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

if (-not (Test-Path $exe)) { throw "build reported success but exe not found: $exe" }
Write-Host "OK -> $exe" -ForegroundColor Green

# Portable zip: the runtime folder (exe + config + onnx runtime + model), no install needed.
if ($Zip) {
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $zipPath = Join-Path $dist 'DesktopPet-Portable.zip'
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    $srcDir = Split-Path $exe
    $files = Get-ChildItem $srcDir -File | Where-Object { $_.Extension -notin @('.pdb', '.xml', '.lib') }
    Compress-Archive -Path $files.FullName -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Portable zip -> {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

if ($Run) {
    Write-Host "Launching ..." -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
}
