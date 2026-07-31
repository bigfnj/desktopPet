#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$buildScript = Join-Path $repoRoot 'build.ps1'
$installerScript =
    Join-Path $repoRoot 'installer\build-installer.ps1'
$fragmentGeneratorScript =
    Join-Path $repoRoot 'installer\New-RuntimeWixFragment.ps1'

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][hashtable]$Parameters,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $accepted = $true
    $message = ''
    try {
        & $ScriptPath @Parameters *> $null
    }
    catch {
        $accepted = $false
        $message = $_.Exception.Message
    }
    if ($accepted) {
        throw "Packaging entrypoint negative control was accepted: $Name"
    }
    if ($message -notmatch $ExpectedMessage) {
        throw (
            "Packaging entrypoint negative control '$Name' failed for an " +
            "unexpected reason: $message"
        )
    }
}

Assert-Rejected `
    -Name 'implicit-debug-zip' `
    -ScriptPath $buildScript `
    -Parameters @{ Zip = $true } `
    -ExpectedMessage 'Production portable packaging requires -Release'
Assert-Rejected `
    -Name 'release-development-zip' `
    -ScriptPath $buildScript `
    -Parameters @{
        Release = $true
        Zip = $true
        DevelopmentPackage = $true
    } `
    -ExpectedMessage 'reserved for Debug artifacts'
Assert-Rejected `
    -Name 'development-without-zip' `
    -ScriptPath $buildScript `
    -Parameters @{ DevelopmentPackage = $true } `
    -ExpectedMessage 'requires -Zip'
Assert-Rejected `
    -Name 'implicit-debug-msi' `
    -ScriptPath $installerScript `
    -Parameters @{ Config = 'Debug' } `
    -ExpectedMessage 'requires the explicit -DevelopmentPackage'
Assert-Rejected `
    -Name 'release-development-msi' `
    -ScriptPath $installerScript `
    -Parameters @{ DevelopmentPackage = $true } `
    -ExpectedMessage 'requires -Config Debug'
Assert-Rejected `
    -Name 'unvalidated-production-msi' `
    -ScriptPath $installerScript `
    -Parameters @{ SkipValidation = $true } `
    -ExpectedMessage 'production MSI artifacts must pass ICE validation'

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $tempRoot (
    'DesktopPet-PackagingEntrypoints-' + [Guid]::NewGuid().ToString('N'))
try {
    $runtime = Join-Path $scratch 'runtime'
    $scan = Join-Path $scratch 'scan'
    $outputRoot = Join-Path $scratch 'output'
    New-Item -ItemType Directory `
        -Path $runtime, $scan, $outputRoot `
        -Force | Out-Null
    $manifest = Join-Path $scratch 'runtime-files.txt'
    $runtimeExecutable = Join-Path $runtime 'DesktopPet.exe'
    [IO.File]::WriteAllText(
        $manifest,
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $runtimeExecutable,
        'test runtime',
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $scan 'input.txt'),
        'test scan input',
        (New-Object Text.UTF8Encoding($false)))
    $manifestHash = (
        Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash

    $fragmentOutput =
        Join-Path $outputRoot 'RuntimeFiles.generated.wxs'
    $fragmentSentinel = 'existing-fragment-must-survive-rejection'
    [IO.File]::WriteAllText(
        $fragmentOutput,
        $fragmentSentinel,
        (New-Object Text.UTF8Encoding($false)))

    $collisionManifest = Join-Path $scratch 'runtime-files-collision.txt'
    [IO.File]::WriteAllText(
        $collisionManifest,
        "DesktopPet.exe`nfoo-bar.dll`nfoo.bar.dll`n",
        (New-Object Text.UTF8Encoding($false)))
    Assert-Rejected `
        -Name 'wix-fragment-normalized-identifier-collision' `
        -ScriptPath $fragmentGeneratorScript `
        -Parameters @{
            ManifestPath = $collisionManifest
            OutputPath = $fragmentOutput
        } `
        -ExpectedMessage 'duplicate WiX identifier'
    if (-not (Test-Path -LiteralPath $fragmentOutput -PathType Leaf) -or
        [IO.File]::ReadAllText($fragmentOutput) -cne $fragmentSentinel) {
        throw (
            'Rejected WiX identifier collision modified the existing ' +
            'fragment output.')
    }

    $hardLinkTargetManifest =
        Join-Path $scratch 'runtime-files-hard-link-target.txt'
    $hardLinkManifest =
        Join-Path $scratch 'runtime-files-hard-link.txt'
    [IO.File]::WriteAllText(
        $hardLinkTargetManifest,
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    New-Item `
        -ItemType HardLink `
        -Path $hardLinkManifest `
        -Target $hardLinkTargetManifest `
        -ErrorAction Stop | Out-Null
    Assert-Rejected `
        -Name 'wix-fragment-hard-link-manifest' `
        -ScriptPath $fragmentGeneratorScript `
        -Parameters @{
            ManifestPath = $hardLinkManifest
            OutputPath = $fragmentOutput
        } `
        -ExpectedMessage 'hard-link alias'
    if ([IO.File]::ReadAllText($fragmentOutput) -cne $fragmentSentinel) {
        throw 'Rejected hard-link manifest modified the fragment output.'
    }

    $junctionManifestTargetRoot =
        Join-Path $scratch 'manifest-junction-target'
    New-Item -ItemType Directory `
        -Path $junctionManifestTargetRoot `
        -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $junctionManifestTargetRoot 'runtime-files.txt'),
        "DesktopPet.exe`n",
        (New-Object Text.UTF8Encoding($false)))
    $manifestJunction = Join-Path $scratch 'manifest-junction'
    $manifestJunctionItem = New-Item `
        -ItemType Junction `
        -Path $manifestJunction `
        -Target $junctionManifestTargetRoot `
        -ErrorAction Stop
    try {
        Assert-Rejected `
            -Name 'wix-fragment-manifest-ancestor-junction' `
            -ScriptPath $fragmentGeneratorScript `
            -Parameters @{
                ManifestPath = (
                    Join-Path $manifestJunction 'runtime-files.txt')
                OutputPath = $fragmentOutput
            } `
            -ExpectedMessage 'reparse point'
        if ([IO.File]::ReadAllText($fragmentOutput) -cne
            $fragmentSentinel) {
            throw (
                'Rejected manifest ancestor junction modified the ' +
                'fragment output.')
        }
    }
    finally {
        if (Test-Path -LiteralPath $manifestJunction) {
            $manifestJunctionItem =
                Get-Item -LiteralPath $manifestJunction -Force
            if (($manifestJunctionItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw (
                    'WiX manifest fixture unexpectedly stopped being a ' +
                    'junction.')
            }
            [IO.Directory]::Delete($manifestJunctionItem.FullName)
        }
    }

    & $fragmentGeneratorScript `
        -ManifestPath $manifest `
        -OutputPath $fragmentOutput *> $null
    $fragmentText = [IO.File]::ReadAllText(
        $fragmentOutput,
        (New-Object Text.UTF8Encoding($false, $true)))
    if ($fragmentText -ceq $fragmentSentinel -or
        -not $fragmentText.Contains(
            '<ComponentGroup Id="RuntimeComponents"') -or
        -not $fragmentText.Contains('<File Id="DesktopPetExe"')) {
        throw (
            'WiX fragment generation did not atomically replace the ' +
            'existing output with the expected validated XML.')
    }
    [xml]$fragmentDocument = $fragmentText
    if ($null -eq $fragmentDocument.DocumentElement) {
        throw 'Generated WiX fragment XML has no document element.'
    }
    if (@(Get-ChildItem `
            -LiteralPath $outputRoot `
            -Force `
            -Directory `
            -Filter '.DesktopPet-wix-fragment-*').Count -ne 0) {
        throw 'WiX fragment generation left a private staging directory.'
    }
    if (@(Get-ChildItem `
            -LiteralPath $scratch `
            -Force `
            -Recurse |
            Where-Object Name -Like '*.replace-backup').Count -ne 0) {
        throw 'WiX fragment generation left an atomic replacement backup.'
    }

    Assert-Rejected `
        -Name 'wix-fragment-overwrites-manifest' `
        -ScriptPath $fragmentGeneratorScript `
        -Parameters @{
            ManifestPath = $manifest
            OutputPath = $manifest
        } `
        -ExpectedMessage 'overlaps a protected packaging input'
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne
        $manifestHash) {
        throw 'Rejected WiX fragment alias modified its manifest.'
    }

    $syftScript = Join-Path $repoRoot 'packaging\Invoke-LockedSyft.ps1'
    $safeSbom = Join-Path $outputRoot 'safe.spdx.json'
    Assert-Rejected `
        -Name 'syft-output-overwrites-manifest' `
        -ScriptPath $syftScript `
        -Parameters @{
            ScanRoot = $scan
            OutputPath = $manifest
            RuntimeRoot = $runtime
            RuntimeManifestPath = $manifest
            ToolRoot = (Join-Path $scratch 'tools-output-alias')
        } `
        -ExpectedMessage 'overlaps a protected packaging input'
    Assert-Rejected `
        -Name 'syft-provenance-overwrites-manifest' `
        -ScriptPath $syftScript `
        -Parameters @{
            ScanRoot = $scan
            OutputPath = $safeSbom
            RuntimeRoot = $runtime
            RuntimeManifestPath = $manifest
            ToolRoot = (Join-Path $scratch 'tools-provenance-alias')
            ProvenancePath = $manifest
        } `
        -ExpectedMessage 'overlaps a protected packaging input'
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne
        $manifestHash) {
        throw 'Rejected Syft output/provenance alias modified its manifest.'
    }
    if (Test-Path -LiteralPath $safeSbom) {
        throw 'Rejected Syft provenance alias unexpectedly created an SBOM.'
    }

    $fixtureRepo = Join-Path $scratch 'installer-dist-junction-fixture'
    $fixtureInstallerRoot = Join-Path $fixtureRepo 'installer'
    $fixturePackagingRoot = Join-Path $fixtureRepo 'packaging'
    $outsideDist = Join-Path $scratch 'outside-dist-target'
    New-Item -ItemType Directory `
        -Path $fixtureInstallerRoot, $fixturePackagingRoot, $outsideDist `
        -Force | Out-Null
    Copy-Item -LiteralPath $installerScript `
        -Destination (Join-Path $fixtureInstallerRoot 'build-installer.ps1')
    Copy-Item `
        -LiteralPath (
            Join-Path $repoRoot 'packaging\StagingPathSafety.ps1') `
        -Destination (
            Join-Path $fixturePackagingRoot 'StagingPathSafety.ps1')
    Copy-Item `
        -LiteralPath (
            Join-Path $repoRoot 'packaging\WixToolchainPolicy.ps1') `
        -Destination (
            Join-Path $fixturePackagingRoot 'WixToolchainPolicy.ps1')
    Copy-Item `
        -LiteralPath (
            Join-Path $repoRoot 'packaging\wix-toolchain-lock.json') `
        -Destination (
            Join-Path $fixturePackagingRoot 'wix-toolchain-lock.json')
    $outsideDistSentinel = Join-Path $outsideDist 'must-survive.txt'
    [IO.File]::WriteAllText(
        $outsideDistSentinel,
        'installer-dist-junction-must-survive',
        (New-Object Text.UTF8Encoding($false)))
    $fixtureDist = Join-Path $fixtureRepo 'dist'
    $distJunction = New-Item `
        -ItemType Junction `
        -Path $fixtureDist `
        -Target $outsideDist `
        -ErrorAction Stop
    try {
        Assert-Rejected `
            -Name 'installer-dist-junction' `
            -ScriptPath (
                Join-Path $fixtureInstallerRoot 'build-installer.ps1') `
            -Parameters @{} `
            -ExpectedMessage 'reparse point'
        if (-not (Test-Path -LiteralPath $outsideDistSentinel -PathType Leaf) -or
            [IO.File]::ReadAllText($outsideDistSentinel) -cne
                'installer-dist-junction-must-survive') {
            throw 'Rejected installer dist junction modified its target.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixtureDist) {
            $distItem =
                Get-Item -LiteralPath $fixtureDist -Force -ErrorAction Stop
            if (($distItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw 'Installer dist fixture unexpectedly stopped being a junction.'
            }
            [IO.Directory]::Delete($distItem.FullName)
        }
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        . (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}

$wixSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\DesktopPet.wxs') -Raw
foreach ($requiredVariable in @(
        'UpgradeCode="$(var.UpgradeCode)"',
        'Value="$(var.RegistryRoot)"',
        'Guid="$(var.InstallFolderStateComponentGuid)"',
        'Guid="$(var.StartMenuFolderStateComponentGuid)"')) {
    if (-not $wixSource.Contains($requiredVariable)) {
        throw "WiX source does not use required packaging identity variable: $requiredVariable"
    }
}
$installerSource = Get-Content -LiteralPath $installerScript -Raw
$fragmentGeneratorSource =
    Get-Content -LiteralPath $fragmentGeneratorScript -Raw
$portableZipSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\New-DeterministicPortableZip.ps1') -Raw
$buildSource = Get-Content -LiteralPath $buildScript -Raw
$releaseWorkflow = Get-Content -LiteralPath (
    Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$buildWorkflow = Get-Content -LiteralPath (
    Join-Path $repoRoot '.github\workflows\build.yml') -Raw
foreach ($requiredIdentity in @(
        "artifactBaseName = 'DesktopPet-DEVELOPMENT-Debug'",
        '2E1B0B6A-811C-4621-ABA9-7B6991EC24E8',
        'DesktopPetAIEditionDevelopmentDebug',
        'DesktopPet-AI-Edition-DEVELOPMENT-Debug',
        '8A141C52-9991-418B-A633-116B74DC82B7',
        '7C3B201C-2ACE-44CA-B408-EBBCF13BE18F')) {
    if (-not $installerSource.Contains($requiredIdentity)) {
        throw "Installer script is missing distinct development identity: $requiredIdentity"
    }
}
foreach ($retainedInstallerInputContract in @(
        '$productPropsInput = Open-DesktopPetValidatedInputFile',
        '$retainedInputs.Add($productPropsInput)',
        '$productPropsInput.ReadAllTextUtf8($maximumPackagingMetadataBytes)',
        '$runtimeManifestInput = Open-DesktopPetValidatedInputFile',
        '$retainedInputs.Add($runtimeManifestInput)',
        '$runtimeManifestInput.ReadAllTextUtf8(',
        '$maximumPackagingMetadataBytes) -split',
        'foreach ($wixToolInput in @($wixTool.Inputs))',
        '$retainedInputs.Add($wixToolInput)',
        '$wixExtension = Open-DesktopPetLockedWixExtension',
        'foreach ($wixExtensionInput in @($wixExtension.Inputs))',
        '$retainedInputs.Add($wixExtensionInput)',
        '''-ext'', $wixExtensionPath',
        'SealedTemporaryFile = $sealedStagedMsi',
        '-Operation ''installer-msi-sealed-validate''',
        'foreach ($input in $retainedInputs)',
        '$input.Dispose()',
        'Publish-DesktopPetAtomicFile')) {
    if (-not $installerSource.Contains($retainedInstallerInputContract)) {
        throw (
            'Installer build is missing retained strict-input/atomic-output ' +
            "contract: $retainedInstallerInputContract")
    }
}
if ([regex]::IsMatch(
        $installerSource,
        '\$wixTool\.Input(?!s)')) {
    throw (
        'Installer build retains only the WiX executable instead of every ' +
        'locked tool-package payload input.')
}
if ($installerSource.Contains(
        '''-ext'', ''WixToolset.UI.wixext''')) {
    throw (
        'Installer build resolves the WiX UI extension by mutable cache name ' +
        'instead of its retained digest-locked DLL path.')
}
foreach ($retainedFragmentInputContract in @(
        '$manifestInput = Open-DesktopPetValidatedInputFile',
        '$manifestInput.ReadAllTextUtf8($maximumManifestBytes)',
        '$manifestInput.Dispose()',
        'SealedTemporaryFile = $fragmentSealedFile',
        'ExpectedDestinationSha256',
        'DestinationMustBeAbsent',
        'Publish-DesktopPetAtomicFile')) {
    if (-not $fragmentGeneratorSource.Contains(
            $retainedFragmentInputContract)) {
        throw (
            'WiX fragment generator is missing retained strict-input/' +
            "atomic-output contract: $retainedFragmentInputContract")
    }
}
foreach ($portableZipPublicationContract in @(
        '$sealedTemporaryFile = Open-DesktopPetSealedStagedFile',
        'SealedTemporaryFile = $sealedTemporaryFile',
        'ExpectedDestinationSha256',
        'DestinationMustBeAbsent',
        'Publish-DesktopPetAtomicFile')) {
    if (-not $portableZipSource.Contains(
            $portableZipPublicationContract)) {
        throw (
            'Portable ZIP builder is missing sealed destination-CAS ' +
            "contract: $portableZipPublicationContract")
    }
}
if ($installerSource.Contains('Remove-Item -LiteralPath $msiPath')) {
    throw 'Installer build still deletes the published MSI before replacement.'
}
if (-not $installerSource.Contains('Remove-DesktopPetSafeFile') -or
    $installerSource.Contains('Remove-Item -LiteralPath $wixPdbPath')) {
    throw 'Installer build does not use retained-handle deletion for stale WiX PDB output.'
}
if ([regex]::IsMatch(
        $installerSource,
        '(?is)\bMove-Item\b.{0,250}\$stagedMsiPath')) {
    throw 'Installer build still publishes its staged MSI with Move-Item.'
}
foreach ($directReadPattern in @(
        '(?is)\bGet-Content\b.{0,200}\$productPropsPath',
        '(?is)\bGet-Content\b.{0,200}\$runtimeManifestPath')) {
    if ([regex]::IsMatch($installerSource, $directReadPattern)) {
        throw (
            'Installer build still reads protected metadata through its ' +
            "resolved path: $directReadPattern")
    }
}
if ([regex]::IsMatch(
        $fragmentGeneratorSource,
        '(?is)\bGet-Content\b.{0,200}\$manifestFull')) {
    throw (
        'WiX fragment generator still reads its manifest through the ' +
        'resolved path.')
}
if (-not $buildSource.Contains(
        'DesktopPet-DEVELOPMENT-Debug-Portable.zip')) {
    throw 'Build script is missing the conspicuous Debug portable filename.'
}
foreach ($releaseIdentity in @(
        'UpgradeCode=DBF8DDB3-C4AB-498C-9E55-4193A734C573',
        'RegistryRoot=Software\bigfnj\DesktopPetAIEdition',
        'InstallFolderStateComponentGuid=847518F2-5F18-5950-A7EC-0318DF7D0F09',
        'StartMenuFolderStateComponentGuid=4E90C393-513F-5AC1-B52E-7CC1FF0EE026')) {
    if (-not $releaseWorkflow.Contains($releaseIdentity)) {
        throw "Final release MSI build is missing production identity: $releaseIdentity"
    }
}

foreach ($entrypoint in @(
        [pscustomobject]@{ Name = 'build.ps1'; Source = $buildSource },
        [pscustomobject]@{
            Name = 'installer/build-installer.ps1'
            Source = $installerSource
        })) {
    if (-not $entrypoint.Source.Contains('StagingPathSafety.ps1') -or
        -not $entrypoint.Source.Contains(
            'Reset-DesktopPetStagingDirectory')) {
        throw (
            "Packaging entrypoint '$($entrypoint.Name)' does not use the " +
            'shared fail-closed staging reset.')
    }
}

foreach ($releaseSbomSafetyContract in @(
        '. .\packaging\StagingPathSafety.ps1',
        'Reset-DesktopPetStagingDirectory',
        '-AllowedRoot $buildRoot',
        '-TrustedRoot $repoRoot')) {
    if (-not $releaseWorkflow.Contains($releaseSbomSafetyContract)) {
        throw (
            'Release SBOM staging is missing the physical path-safety ' +
            "contract: $releaseSbomSafetyContract")
    }
}
if ($releaseWorkflow.Contains(
        'Remove-Item -LiteralPath $sbomInput -Recurse -Force')) {
    throw 'Release SBOM staging still uses unsafe recursive lexical cleanup.'
}

foreach ($workflow in @(
        [pscustomobject]@{ Name = 'build'; Source = $buildWorkflow },
        [pscustomobject]@{ Name = 'release'; Source = $releaseWorkflow })) {
    if (-not $workflow.Source.Contains(
            '.\tests\deterministic-msi-selftest.ps1 -KeepVerifiedArtifact')) {
        throw (
            "$($workflow.Name) workflow does not enforce the two-build " +
            'deterministic MSI regression.')
    }
}

foreach ($prereleaseGuard in @(
        '--json tagName,isDraft,isPrerelease,assets',
        'if ($release.isPrerelease -ne $false)',
        'if [[ "$(jq -r ''.isPrerelease'' <<<"$release_json")" != "false" ]]',
        '[[ "$(jq -r ''.isPrerelease'' <<<"$release_after_json")" != "false" ]]',
        '--draft=false --prerelease=false',
        'verify_remote_release exact "$verification_root/before-publication"',
        'verify_remote_release exact "$verification_root/after-publication"')) {
    if (-not $releaseWorkflow.Contains($prereleaseGuard)) {
        throw (
            'Stable release publication is missing prerelease guard: ' +
            $prereleaseGuard)
    }
}
if ([regex]::Matches(
        $releaseWorkflow,
        '(?m)\.isPrerelease').Count -lt 4) {
    throw (
        'Stable release prerelease state is not checked during initial ' +
        'validation and repeatable remote verification.')
}

Write-Host (
    'PASS: production packaging is Release-only; Debug artifacts require ' +
    'explicit opt-in and separate portable/MSI identities; staging safety, ' +
    'retained strict inputs, protected-input alias/collision rejection, ' +
    'atomic fragment/MSI publication, stable-release state, and two-build ' +
    'MSI determinism are workflow-gated.'
) -ForegroundColor Green
