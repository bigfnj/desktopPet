#requires -Version 5
<#
.SYNOPSIS
    Build and optionally package the supported DesktopPet Windows x64 application.

.DESCRIPTION
    The supported product is src\DesktopPet_Portable.csproj, built as x64. Product
    identity comes from ProductVersion.props and package contents come from
    packaging\runtime-files.txt.

    The script never terminates running processes. If an existing DesktopPet instance
    has locked a build output, close that instance and run the build again.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Release -Zip -LockedRestore
.EXAMPLE
    .\build.ps1 -Zip -DevelopmentPackage
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Release,
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$Zip,
    [switch]$LockedRestore,
    [switch]$PackageOnly,
    [switch]$DevelopmentPackage
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src'
$projectPath = Join-Path $sourceRoot 'DesktopPet_Portable.csproj'
$productPropsPath = Join-Path $repoRoot 'ProductVersion.props'
$runtimeManifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$outputDirectory = Join-Path $repoRoot "build\DesktopPetPortable\bin\$configuration\x64"
$executablePath = Join-Path $outputDirectory 'DesktopPet.exe'

if ($NoRestore -and $LockedRestore) {
    throw '-NoRestore and -LockedRestore cannot be used together.'
}
if ($PackageOnly -and -not $Release) {
    throw '-PackageOnly is supported only with -Release.'
}
if ($PackageOnly -and ($Clean -or $Run)) {
    throw '-PackageOnly cannot be combined with -Clean or -Run.'
}
if ($DevelopmentPackage -and -not $Zip) {
    throw '-DevelopmentPackage requires -Zip.'
}
if ($DevelopmentPackage -and $Release) {
    throw '-DevelopmentPackage is reserved for Debug artifacts and cannot be combined with -Release.'
}
if ($Zip -and -not $Release -and -not $DevelopmentPackage) {
    throw (
        'Production portable packaging requires -Release. To create a ' +
        'conspicuously named Debug artifact, also specify -DevelopmentPackage.'
    )
}

$stagingPathSafety =
    Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $stagingPathSafety -PathType Leaf)) {
    throw "Staging path-safety policy is missing: $stagingPathSafety"
}
. $stagingPathSafety

function Find-MSBuild {
    # CI pins a stable Visual Studio toolchain with setup-msbuild. Honor that
    # explicit PATH selection before probing machine-wide installations.
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $candidate = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }

    $known = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $known) { return $known }

    throw 'MSBuild.exe was not found. Install the Visual Studio .NET desktop-development workload or put MSBuild on PATH.'
}

function Get-CanonicalProductVersion {
    if (-not (Test-Path -LiteralPath $productPropsPath)) {
        throw "Canonical product metadata is missing: $productPropsPath"
    }
    [xml]$props = Get-Content -LiteralPath $productPropsPath -Raw
    $value = [string]$props.Project.PropertyGroup.DesktopPetVersion
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "DesktopPetVersion must be a three-part numeric version; found '$value'."
    }
    return $value
}

function Get-RuntimeManifest {
    if (-not (Test-Path -LiteralPath $runtimeManifestPath)) {
        throw "Runtime payload manifest is missing: $runtimeManifestPath"
    }

    $entries = @(
        Get-Content -LiteralPath $runtimeManifestPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    )
    if ($entries.Count -eq 0) { throw 'Runtime payload manifest is empty.' }

    $duplicates = @($entries | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "Runtime payload manifest contains duplicate entries: $($duplicates.Name -join ', ')"
    }

    foreach ($entry in $entries) {
        if (-not (Test-DesktopPetWindowsLeafName -Name $entry)) {
            throw "Runtime payload entries must be plain file names: '$entry'"
        }
    }
    return $entries
}

function Assert-RuntimeOutput {
    param([string[]]$Manifest)

    foreach ($name in $Manifest) {
        $path = Join-Path $outputDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required runtime payload is missing: $path"
        }
    }

    $excludedDeveloperExtensions = @('.pdb', '.xml', '.lib', '.exp')
    $actualRuntimeFiles = @(
        Get-ChildItem -LiteralPath $outputDirectory -File |
            Where-Object { $excludedDeveloperExtensions -notcontains $_.Extension.ToLowerInvariant() } |
            Select-Object -ExpandProperty Name |
            Sort-Object
    )
    $expectedRuntimeFiles = @($Manifest | Sort-Object)
    $difference = @(Compare-Object $expectedRuntimeFiles $actualRuntimeFiles)
    if ($difference.Count -gt 0) {
        $detail = ($difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; '
        throw "Build output and packaging\runtime-files.txt disagree: $detail"
    }
}

$productVersion = Get-CanonicalProductVersion
$runtimeManifest = @(Get-RuntimeManifest)

Write-Host "Product : DesktopPet AI Edition $productVersion" -ForegroundColor DarkGray
Write-Host "Project : $projectPath" -ForegroundColor DarkGray

if (-not $PackageOnly) {
    $msbuild = Find-MSBuild
    $commonArguments = @(
        "-p:Configuration=$configuration",
        '-p:Platform=x64',
        "-p:SolutionDir=$sourceRoot\",
        '-nologo',
        '-v:minimal'
    )
    $msbuildVersion = (& $msbuild -version -nologo 2>&1 | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($msbuildVersion)) {
        throw "Unable to determine the selected MSBuild version from '$msbuild'."
    }
    Write-Host "MSBuild : $msbuild" -ForegroundColor DarkGray
    Write-Host "Version : $msbuildVersion" -ForegroundColor DarkGray

    if ($Clean) {
        Write-Host 'Cleaning supported x64 output...' -ForegroundColor Cyan
        & $msbuild $projectPath -t:clean @commonArguments
        if ($LASTEXITCODE -ne 0) { throw "clean failed (exit $LASTEXITCODE)" }

        # MSBuild's Clean target knows only about declared outputs. A previous
        # portable launch can leave mutable data (for example data\settings.json)
        # beside the executable, and removed project content can linger there as
        # well. Reset the configuration output under the guarded build root so a
        # clean build can never inherit or test against stale runtime state.
        # On a fresh tree the configuration output does not exist yet: there is no stale runtime
        # state to clear, and its parent chain is absent, which the staging reset cannot open
        # (it retains an existing directory chain to stay TOCTOU-safe). Only reset when the
        # output directory is actually present; a first build then creates it normally.
        if (Test-Path -LiteralPath $outputDirectory -PathType Container) {
            Reset-DesktopPetStagingDirectory `
                -Path $outputDirectory `
                -AllowedRoot (Join-Path $repoRoot 'build') `
                -TrustedRoot $repoRoot
        }
    }

    if (-not $NoRestore) {
        Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
        $restoreArguments = @($commonArguments)
        if ($LockedRestore) { $restoreArguments += '-p:RestoreLockedMode=true' }
        & $msbuild $projectPath -t:restore @restoreArguments
        if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)" }
    }

    Write-Host "Building $configuration|x64..." -ForegroundColor Cyan
    & $msbuild $projectPath -t:build @commonArguments
    if ($LASTEXITCODE -ne 0) {
        throw "build failed (exit $LASTEXITCODE). If DesktopPet.exe is locked, close the running application and retry."
    }
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "The expected executable was not produced: $executablePath"
}

Assert-RuntimeOutput -Manifest $runtimeManifest
Write-Host "Runtime output OK -> $executablePath" -ForegroundColor Green

if ($Zip) {
    $distributionDirectory = Join-Path $repoRoot 'dist'
    New-Item -ItemType Directory -Path $distributionDirectory -Force | Out-Null
    $zipName = if ($DevelopmentPackage) {
        'DesktopPet-DEVELOPMENT-Debug-Portable.zip'
    }
    else {
        'DesktopPet-Portable.zip'
    }
    $zipPath = Join-Path $distributionDirectory $zipName

    # Stage the bundled offline content (portable zip only) through the shared
    # helper so build.ps1 and the release workflow bundle an identical set. The
    # MSI never carries this content, so the installer stays lean.
    $contentStaging = Join-Path $distributionDirectory (
        '.content-' + [Guid]::NewGuid().ToString('N'))
    try {
        & (Join-Path $repoRoot 'packaging\Stage-BundledContent.ps1') `
            -RepoRoot $repoRoot `
            -StagingRoot $contentStaging

        & (Join-Path $repoRoot 'packaging\New-DeterministicPortableZip.ps1') `
            -RuntimeRoot $outputDirectory `
            -DestinationPath $zipPath `
            -ManifestPath $runtimeManifestPath `
            -ContentDirectories @(
                @{ Prefix = 'pets'; Source = (Join-Path $contentStaging 'pets') }
                @{ Prefix = 'fortunes'; Source = (Join-Path $contentStaging 'fortunes') }
            )
    }
    finally {
        if (Test-Path -LiteralPath $contentStaging) {
            Remove-Item -LiteralPath $contentStaging -Recurse -Force
        }
    }

    Write-Host (
        "Portable ZIP -> {0} ({1:N1} MB; bundled pets + fortunes)" -f
        $zipPath,
        ((Get-Item -LiteralPath $zipPath).Length / 1MB)
    ) -ForegroundColor Green
}

if ($Run) {
    Write-Host 'Launching DesktopPet...' -ForegroundColor Cyan
    Start-Process -FilePath $executablePath -WorkingDirectory $outputDirectory
}
