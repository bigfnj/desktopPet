#requires -Version 5
<#
.SYNOPSIS
    Build and validate the per-user DesktopPet AI Edition x64 MSI.

.DESCRIPTION
    Product metadata is read from ProductVersion.props. Runtime files are copied
    into a fresh staging directory from packaging\runtime-files.txt, and a WiX
    fragment is generated from that same manifest before every build.

    Requires WiX 5.0.2 and WixToolset.UI.wixext 5.0.2.

    Production artifacts are always Release and ICE-validated.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Config = 'Release',
    # Skip the Windows Installer ICE pass. ICE runs through msiexec, which serialises across the whole
    # machine, so ANY interactive install sitting on a dialog blocks this build indefinitely -- including
    # the maintainer's own smoke test of the previous release. For local iteration only: the release
    # workflow never passes it, so a published MSI is always ICE-validated.
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'

$installerRoot = $PSScriptRoot
$repoRoot = Split-Path $installerRoot -Parent
$stagingPathSafety =
    Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
if (-not (Test-Path -LiteralPath $stagingPathSafety -PathType Leaf)) {
    throw "Staging path-safety policy is missing: $stagingPathSafety"
}
. $stagingPathSafety
$wixToolchainPolicy =
    Join-Path $repoRoot 'packaging\WixToolchainPolicy.ps1'
$wixToolchainLock =
    Join-Path $repoRoot 'packaging\wix-toolchain-lock.json'
if (-not (Test-Path -LiteralPath $wixToolchainPolicy -PathType Leaf) -or
    -not (Test-Path -LiteralPath $wixToolchainLock -PathType Leaf)) {
    throw 'The locked WiX toolchain policy or digest lock is missing.'
}
. $wixToolchainPolicy
$distributionDirectory = Join-Path $repoRoot 'dist'
[void](Assert-DesktopPetPathChainSafe `
    -Path $distributionDirectory `
    -TrustedRoot $repoRoot)
if (-not (Test-Path -LiteralPath $distributionDirectory)) {
    New-Item -ItemType Directory -Path $distributionDirectory |
        Out-Null
}
[void](Assert-DesktopPetPathChainSafe `
    -Path $distributionDirectory `
    -TrustedRoot $repoRoot)
if (-not (Test-Path -LiteralPath $distributionDirectory -PathType Container)) {
    throw "Distribution output root is not a directory: $distributionDirectory"
}
$productPropsPath = Join-Path $repoRoot 'ProductVersion.props'
$runtimeManifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
$wixSourcePath = Join-Path $installerRoot 'DesktopPet.wxs'
$licensePath = Join-Path $installerRoot 'license.rtf'
$fragmentGenerator =
    Join-Path $installerRoot 'New-RuntimeWixFragment.ps1'
$outputDirectory = Join-Path $repoRoot "build\DesktopPetPortable\bin\$Config\x64"
$buildRoot = Join-Path $repoRoot 'build'
$stagingVariant = 'installer-staging\release'
$stagingDirectory = Join-Path $buildRoot "$stagingVariant\runtime"
$artifactStagingDirectory =
    Join-Path $buildRoot "$stagingVariant\artifact"
$generatedFragment =
    Join-Path $buildRoot "$stagingVariant\RuntimeFiles.generated.wxs"
$normalizedPayloadTimestamp = [DateTime]::SpecifyKind(
    [DateTime]'2000-01-01T00:00:00',
    [DateTimeKind]::Utc)
$maximumPackagingMetadataBytes = 1MB

function Get-ProductProperty {
    param([xml]$Document, [string]$Name)
    $value = [string]$Document.Project.PropertyGroup.$Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "ProductVersion.props is missing '$Name'."
    }
    return $value
}

$retainedInputs = New-Object 'Collections.Generic.List[IDisposable]'
$sealedStagedMsi = $null
$validationMsiInput = $null
$validationMsiPath = $null
try {
if (-not (Test-Path -LiteralPath $productPropsPath -PathType Leaf)) {
    throw "Canonical product metadata not found: $productPropsPath"
}
if (-not (Test-Path -LiteralPath $runtimeManifestPath -PathType Leaf)) {
    throw "Runtime payload manifest not found: $runtimeManifestPath"
}
foreach ($authoringPath in @(
        $wixSourcePath,
        $licensePath,
        $fragmentGenerator)) {
    if (-not (Test-Path -LiteralPath $authoringPath -PathType Leaf)) {
        throw "Installer authoring input not found: $authoringPath"
    }
}

$productPropsInput = Open-DesktopPetValidatedInputFile `
    -Path $productPropsPath `
    -Root $repoRoot
$retainedInputs.Add($productPropsInput)
$runtimeManifestInput = Open-DesktopPetValidatedInputFile `
    -Path $runtimeManifestPath `
    -Root (Split-Path -Parent $runtimeManifestPath)
$retainedInputs.Add($runtimeManifestInput)
foreach ($authoringPath in @(
        $wixSourcePath,
        $licensePath,
        $fragmentGenerator)) {
    $authoringInput = Open-DesktopPetValidatedInputFile `
        -Path $authoringPath `
        -Root $installerRoot
    $retainedInputs.Add($authoringInput)
}

[xml]$productProps =
    $productPropsInput.ReadAllTextUtf8($maximumPackagingMetadataBytes)
$productName = Get-ProductProperty $productProps 'DesktopPetProductName'
$manufacturer = Get-ProductProperty $productProps 'DesktopPetPublisher'
$productVersion = Get-ProductProperty $productProps 'DesktopPetVersion'
$repositoryUrl = Get-ProductProperty $productProps 'DesktopPetRepositoryUrl'
$upgradeCode = 'DBF8DDB3-C4AB-498C-9E55-4193A734C573'
$registryRoot = 'Software\bigfnj\DesktopPetAIEdition'
$artifactBaseName = 'DesktopPet-AI-Edition'
$componentNamespace = 'DesktopPet-AI-Edition'
$installFolderStateComponentGuid =
    '847518F2-5F18-5950-A7EC-0318DF7D0F09'
$startMenuFolderStateComponentGuid =
    '4E90C393-513F-5AC1-B52E-7CC1FF0EE026'
if ($productVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "MSI ProductVersion must be a three-part numeric version; found '$productVersion'."
}
$versionParts = @($productVersion.Split('.') | ForEach-Object { [int]$_ })
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "MSI ProductVersion must fit Windows Installer's 255.255.65535 limit; found '$productVersion'."
}

$wixGlobalToolRoot = Get-DesktopPetDotnetGlobalToolRoot
$wixTool = Open-DesktopPetLockedWixExecutable `
    -LockPath $wixToolchainLock `
    -ToolRoot $wixGlobalToolRoot
foreach ($wixToolInput in @($wixTool.Inputs)) {
    $retainedInputs.Add($wixToolInput)
}
$wix = [string]$wixTool.Path

$wixVersion = (& $wix --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $wixVersion -notmatch '^5\.0\.2(?:\+|$)') {
    throw "WiX 5.0.2 is required; found '$wixVersion'."
}

# UI supplies the installer dialogs; Util supplies util:CloseApplication, which shuts a running
# DesktopPet down before file costing instead of leaving the user at "unable to automatically close
# all requested applications". Each is verified against its own digest in the lock.
$wixExtensionIds = @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')
$wixExtensionPaths = @()
foreach ($wixExtensionId in $wixExtensionIds) {
    $wixExtension = Open-DesktopPetLockedWixExtension `
        -LockPath $wixToolchainLock `
        -ExtensionRoot (Get-DesktopPetWixGlobalExtensionRoot) `
        -ExtensionId $wixExtensionId
    foreach ($wixExtensionInput in @($wixExtension.Inputs)) {
        $retainedInputs.Add($wixExtensionInput)
    }
    $wixExtensionPaths += [string]$wixExtension.Path
}

$runtimeFiles = @(
    $runtimeManifestInput.ReadAllTextUtf8(
        $maximumPackagingMetadataBytes) -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) { throw 'Runtime payload manifest is empty.' }

Reset-DesktopPetStagingDirectory `
    -Path $stagingDirectory `
    -AllowedRoot $buildRoot `
    -TrustedRoot $repoRoot
foreach ($name in $runtimeFiles) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name)) {
        throw "Runtime payload entries must be plain file names: '$name'"
    }
    $source = Join-Path $outputDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required runtime file is missing. Build the supported $Config|x64 project first: $source"
    }
    $stagedPath = Join-Path $stagingDirectory $name
    [void](Copy-DesktopPetValidatedInputFile `
        -Path $source `
        -Root $outputDirectory `
        -DestinationPath $stagedPath)
    # Cabinet entries retain source modification times. Normalize the staged
    # copies so equal runtime bytes produce equal cabinet bytes on every runner.
    (Get-Item -LiteralPath $stagedPath).LastWriteTimeUtc =
        $normalizedPayloadTimestamp
    $stagedInput = Open-DesktopPetValidatedInputFile `
        -Path $stagedPath `
        -Root $stagingDirectory
    $retainedInputs.Add($stagedInput)
}

& $fragmentGenerator `
    -ManifestPath $runtimeManifestPath `
    -OutputPath $generatedFragment `
    -ComponentNamespace $componentNamespace
if (-not (Test-Path -LiteralPath $generatedFragment -PathType Leaf)) {
    throw "WiX runtime fragment was not generated: $generatedFragment"
}
$generatedFragmentInput = Open-DesktopPetValidatedInputFile `
    -Path $generatedFragment `
    -Root (Split-Path -Parent $generatedFragment)
$retainedInputs.Add($generatedFragmentInput)

$msiPath = Join-Path $distributionDirectory "$artifactBaseName.msi"
$wixPdbPath = Join-Path $distributionDirectory "$artifactBaseName.wixpdb"
$msiDestinationExists = $false
$msiDestinationSha256 = $null
if (Test-Path -LiteralPath $msiPath -PathType Leaf) {
    $msiDestinationInput = Open-DesktopPetValidatedInputFile `
        -Path $msiPath `
        -Root $distributionDirectory
    try {
        $msiDestinationSha256 =
            $msiDestinationInput.ComputeHash('SHA256')
        $msiDestinationExists = $true
    }
    finally {
        $msiDestinationInput.Dispose()
    }
}
elseif (Test-Path -LiteralPath $msiPath) {
    throw "MSI destination is not a regular file: $msiPath"
}
Reset-DesktopPetStagingDirectory `
    -Path $artifactStagingDirectory `
    -AllowedRoot $buildRoot `
    -TrustedRoot $repoRoot
$stagedMsiPath =
    Join-Path $artifactStagingDirectory "$artifactBaseName.msi"
$stagedMsiPath = Assert-DesktopPetOutputFileSafe `
    -Path $stagedMsiPath `
    -TrustedRoot $artifactStagingDirectory `
    -ProtectedPaths @(
        $productPropsPath,
        $runtimeManifestPath,
        $generatedFragment
    ) `
    -ProtectedDirectories @(
        $outputDirectory,
        $stagingDirectory,
        $installerRoot
    )

Write-Host "Product : $productName $productVersion" -ForegroundColor DarkGray
Write-Host "WiX     : $wix" -ForegroundColor DarkGray
Write-Host "Version : $wixVersion" -ForegroundColor DarkGray
Write-Host "Payload : $($runtimeFiles.Count) files from $stagingDirectory" -ForegroundColor DarkGray
Write-Host "Output  : $msiPath" -ForegroundColor DarkGray

$wixArguments = @(
    'build',
    $wixSourcePath,
    $generatedFragment,
    '-ext', $wixExtensionPaths[0],
    '-ext', $wixExtensionPaths[1],
    '-arch', 'x64',
    '-bindpath', $stagingDirectory,
    '-bindpath', $installerRoot,
    '-d', "ProductName=$productName",
    '-d', "Manufacturer=$manufacturer",
    '-d', "ProductVersion=$productVersion",
    '-d', "RepositoryUrl=$repositoryUrl",
    '-d', "UpgradeCode=$upgradeCode",
    '-d', "RegistryRoot=$registryRoot",
    '-d', "InstallFolderStateComponentGuid=$installFolderStateComponentGuid",
    '-d', "StartMenuFolderStateComponentGuid=$startMenuFolderStateComponentGuid",
    '-pdbtype', 'none',
    '-o', $stagedMsiPath
)
& $wix @wixArguments
if ($LASTEXITCODE -ne 0) { throw "WiX build failed (exit $LASTEXITCODE)." }

& (Join-Path $repoRoot 'packaging\Normalize-MsiDeterminism.ps1') `
    -MsiPath $stagedMsiPath `
    -IdentityNamespace $componentNamespace

$sealedStagedMsi = Open-DesktopPetSealedStagedFile `
    -Path $stagedMsiPath `
    -Root $artifactStagingDirectory
$stagedMsiSha256 = $sealedStagedMsi.ComputeHash('SHA256')
$validationMsiPath = Join-Path $artifactStagingDirectory (
    '.validation-' + [Guid]::NewGuid().ToString('N') + '.msi')
$validationMsiPath = Assert-DesktopPetOutputFileSafe `
    -Path $validationMsiPath `
    -TrustedRoot $artifactStagingDirectory `
    -ProtectedPaths @(
        $productPropsPath,
        $runtimeManifestPath,
        $generatedFragment,
        $stagedMsiPath
    ) `
    -ProtectedDirectories @(
        $outputDirectory,
        $stagingDirectory,
        $installerRoot
    )
$sealedStagedMsi.CopyToFile($validationMsiPath)
$validationMsiInput = Open-DesktopPetValidatedInputFile `
    -Path $validationMsiPath `
    -Root $artifactStagingDirectory
$validationPrimaryError = $null
try {
    if ($validationMsiInput.ComputeHash('SHA256') -cne
        $stagedMsiSha256) {
        throw (
            'MSI validation copy differs from the exact sealed staged ' +
            'artifact.')
    }

    & (Join-Path $repoRoot 'packaging\Test-MsiUpgradeSchedule.ps1') `
        -MsiPath $validationMsiPath `
        -SelfTest

    Write-Host 'Running Windows Installer ICE validation...' -ForegroundColor Cyan
    # ICE91 warns whenever a file lives in a fixed per-user directory. This MSI is
    # deliberately Scope=perUser and cannot become per-machine, so ICE91's
    # ALLUSERS portability warning is inapplicable.
    # ICE61 fires because MajorUpgrade sets AllowSameVersionUpgrades="yes" (the Upgrade
    # table's VersionMax is inclusive so a rebuilt same version can replace the prior
    # install); that configuration is deliberate, so its same-version warning is suppressed.
    # All other standard ICEs run.
    if ($SkipValidation) {
        # NOT a return: the copy into dist\ happens after this try/catch/finally, so bailing out here
        # silently leaves the previous build in place while reporting success. Guard the ICE call only.
        Write-Host '  SKIPPED (-SkipValidation). This MSI is NOT release-quality.' -ForegroundColor Yellow
    }
    else {
        & $wix msi validate -sice ICE91 -sice ICE61 $validationMsiPath
        if ($LASTEXITCODE -ne 0) {
            throw "MSI validation failed (exit $LASTEXITCODE)."
        }
    }
}
catch {
    $validationPrimaryError = $_
    throw
}
finally {
    if ($null -ne $validationMsiInput) {
        $validationMsiInput.Dispose()
        $validationMsiInput = $null
    }
    if ($null -ne $validationMsiPath -and
        (Test-Path -LiteralPath $validationMsiPath)) {
        try {
            Remove-DesktopPetSafeFile `
                -Path $validationMsiPath `
                -AllowedRoot $artifactStagingDirectory `
                -TrustedRoot $repoRoot
        }
        catch {
            if ($null -eq $validationPrimaryError) {
                throw
            }
            Write-Warning (
                'MSI validation-copy cleanup also failed; preserving the ' +
                "primary error. Cleanup error: $($_.Exception.Message)")
        }
    }
}

[void](Assert-DesktopPetPathChainSafe `
    -Path $distributionDirectory `
    -TrustedRoot $repoRoot)
$msiPath = Assert-DesktopPetOutputFileSafe `
    -Path $msiPath `
    -TrustedRoot $distributionDirectory `
    -ProtectedPaths @(
        $productPropsPath,
        $runtimeManifestPath,
        $generatedFragment,
        $stagedMsiPath
    ) `
    -ProtectedDirectories @(
        $outputDirectory,
        $stagingDirectory,
        $artifactStagingDirectory,
        $installerRoot
    )
$wixPdbPath = Assert-DesktopPetOutputFileSafe `
    -Path $wixPdbPath `
    -TrustedRoot $distributionDirectory `
    -ProtectedPaths @(
        $productPropsPath,
        $runtimeManifestPath,
        $generatedFragment,
        $stagedMsiPath
    ) `
    -ProtectedDirectories @(
        $outputDirectory,
        $stagingDirectory,
        $artifactStagingDirectory,
        $installerRoot
    )
Remove-DesktopPetSafeFile `
    -Path $wixPdbPath `
    -AllowedRoot $distributionDirectory `
    -TrustedRoot $repoRoot
$publishMsiParameters = @{
    TemporaryPath = $stagedMsiPath
    DestinationPath = $msiPath
    TrustedRoot = $repoRoot
    ProtectedPaths = @(
        $productPropsPath,
        $runtimeManifestPath,
        $generatedFragment,
        $wixSourcePath,
        $licensePath,
        $fragmentGenerator
    )
    ProtectedDirectories = @(
        $outputDirectory,
        $stagingDirectory,
        $installerRoot
    )
    SealedTemporaryFile = $sealedStagedMsi
    ExpectedTemporarySha256 = $stagedMsiSha256
}
if ($msiDestinationExists) {
    $publishMsiParameters.ExpectedDestinationSha256 =
        $msiDestinationSha256
}
else {
    $publishMsiParameters.DestinationMustBeAbsent = $true
}
$msiPath = Publish-DesktopPetAtomicFile @publishMsiParameters
[void](Assert-DesktopPetPathChainSafe `
    -Path $msiPath `
    -TrustedRoot $repoRoot)

Write-Host ("MSI OK -> {0} ({1:N1} MB)" -f $msiPath, ((Get-Item -LiteralPath $msiPath).Length / 1MB)) -ForegroundColor Green
}
finally {
    if ($null -ne $validationMsiInput) {
        $validationMsiInput.Dispose()
    }
    if ($null -ne $sealedStagedMsi) {
        $sealedStagedMsi.Dispose()
    }
    foreach ($input in $retainedInputs) {
        $input.Dispose()
    }
}
