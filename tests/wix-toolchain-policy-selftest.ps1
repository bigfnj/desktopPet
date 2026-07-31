#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'WiX toolchain policy self-test requires Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
. (Join-Path $repoRoot 'packaging\WixToolchainPolicy.ps1')
Add-Type -AssemblyName System.IO.Compression

function Test-SharingViolation {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if (($exception.HResult -band 0xffff) -in @(5, 32, 33)) {
            return $true
        }
        $exception = $exception.InnerException
    }
    return $false
}

function Get-TestSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Add-TestPackageEntry {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $entry = $Archive.CreateEntry(
        $RelativePath,
        [IO.Compression.CompressionLevel]::Optimal)
    $entryStream = $entry.Open()
    try {
        $entryStream.Write($Bytes, 0, $Bytes.Length)
    }
    finally {
        $entryStream.Dispose()
    }
}

function Close-TestLockedWix {
    param([Parameter(Mandatory = $true)]$LockedTool)

    foreach ($input in @($LockedTool.Inputs)) {
        $input.Dispose()
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$dotnetCliHomeFixture = Join-Path $tempRoot 'DesktopPet-DotnetCliHome'
$userProfileFixture = Join-Path $tempRoot 'DesktopPet-UserProfile'
$dotnetCliToolRoot = Get-DesktopPetDotnetGlobalToolRoot `
    -DotnetCliHome $dotnetCliHomeFixture `
    -UserProfile $userProfileFixture
$fallbackToolRoot = Get-DesktopPetDotnetGlobalToolRoot `
    -DotnetCliHome '' `
    -UserProfile $userProfileFixture
$globalExtensionRoot = Get-DesktopPetWixGlobalExtensionRoot `
    -UserProfile $userProfileFixture
if ($dotnetCliToolRoot -cne
        (Join-Path $dotnetCliHomeFixture '.dotnet\tools') -or
    $fallbackToolRoot -cne
        (Join-Path $userProfileFixture '.dotnet\tools') -or
    $globalExtensionRoot -cne
        (Join-Path $userProfileFixture '.wix\extensions')) {
    throw (
        'WiX global-tool root resolution did not honor DOTNET_CLI_HOME and ' +
        'its USERPROFILE fallback.')
}
$relativeHomeRejected = $false
try {
    [void](Get-DesktopPetDotnetGlobalToolRoot `
        -DotnetCliHome 'relative-cli-home' `
        -UserProfile $userProfileFixture)
}
catch {
    $relativeHomeRejected =
        $_.Exception.Message -match '(?i)absolute path'
}
if (-not $relativeHomeRejected) {
    throw 'WiX global-tool root resolution accepted a relative CLI home.'
}

$scratch = Join-Path $tempRoot (
    'DesktopPet-WixPolicy-' + [Guid]::NewGuid().ToString('N'))
$toolRoot = Join-Path $scratch 'tools'
$policyRoot = Join-Path $scratch 'policy'
$version = '5.0.2'
$storeRoot = Join-Path $toolRoot (
    ".store\wix\$version\wix\$version")
$packagePath = Join-Path $storeRoot "wix.$version.nupkg"
$payloadRoot = Join-Path $storeRoot 'tools\net6.0\any'
$executablePath = Join-Path $payloadRoot 'wix.exe'
$dependencyPath = Join-Path $payloadRoot 'wix.dll'
$extraPath = Join-Path $payloadRoot 'attacker-extra.dll'
$missingHoldingPath = Join-Path $scratch 'held-wix.dll'
$hardLinkPath = Join-Path $scratch 'wix-dependency-hardlink.dll'
$extensionRoot = Join-Path $scratch 'extensions'
$extensionVersionRoot = Join-Path $extensionRoot (
    "WixToolset.UI.wixext\$version")
$extensionPath = Join-Path $extensionVersionRoot (
    'wixext5\WixToolset.UI.wixext.dll')
$extensionExtraPath = Join-Path $extensionVersionRoot (
    'wixext5\attacker-extra.dll')
$lockPath = Join-Path $policyRoot 'wix-toolchain-lock.json'
$utf8 = New-Object Text.UTF8Encoding($false)
$payloadBytes = [Text.Encoding]::UTF8.GetBytes(
    'trusted-wix-executable-payload')
$dependencyBytes = [Text.Encoding]::UTF8.GetBytes(
    'trusted-wix-managed-dependency-payload')
$expectedPayloadHash = Get-TestSha256 -Bytes $payloadBytes
$expectedDependencyHash = Get-TestSha256 -Bytes $dependencyBytes
$extensionBytes = [Text.Encoding]::UTF8.GetBytes(
    'trusted-wix-ui-extension-payload')
$expectedExtensionHash = Get-TestSha256 -Bytes $extensionBytes

try {
    New-Item -ItemType Directory `
        -Path (
            Split-Path -Parent $executablePath),
            (Split-Path -Parent $extensionPath),
            $policyRoot `
        -Force | Out-Null
    $packageStream = New-Object IO.FileStream(
        $packagePath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive(
            $packageStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            Add-TestPackageEntry `
                -Archive $archive `
                -RelativePath 'tools/net6.0/any/wix.exe' `
                -Bytes $payloadBytes
            Add-TestPackageEntry `
                -Archive $archive `
                -RelativePath 'tools/net6.0/any/wix.dll' `
                -Bytes $dependencyBytes
        }
        finally {
            $archive.Dispose()
        }
        $packageStream.Flush($true)
    }
    finally {
        $packageStream.Dispose()
    }
    [IO.File]::WriteAllBytes($executablePath, $payloadBytes)
    [IO.File]::WriteAllBytes($dependencyPath, $dependencyBytes)
    [IO.File]::WriteAllBytes($extensionPath, $extensionBytes)

    $packageItem = Get-Item -LiteralPath $packagePath
    $packageHash = (
        Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $lock = [pscustomobject][ordered]@{
        schemaVersion = 1
        wixVersion = $version
        packages = @(
            [pscustomobject][ordered]@{
                id = 'wix'
                version = $version
                fileName = "wix.$version.nupkg"
                source = 'https://example.invalid/wix.nupkg'
                size = [long]$packageItem.Length
                sha256 = $packageHash
            },
            [pscustomobject][ordered]@{
                id = 'WixToolset.UI.wixext'
                version = $version
                fileName = "wixtoolset.ui.wixext.$version.nupkg"
                source = 'https://example.invalid/ui.nupkg'
                size = 1
                sha256 = ('0' * 64)
                installedPayload = [pscustomobject][ordered]@{
                    relativePath =
                        'wixext5/WixToolset.UI.wixext.dll'
                    length = [long]$extensionBytes.Length
                    sha256 = $expectedExtensionHash.ToLowerInvariant()
                }
            }
        )
    }
    [IO.File]::WriteAllText(
        $lockPath,
        (($lock | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
        $utf8)

    $lockedTool = Open-DesktopPetLockedWixExecutable `
        -LockPath $lockPath `
        -ToolRoot $toolRoot
    try {
        $executableInputs = @(
            $lockedTool.Inputs |
                Where-Object {
                    $_.FinalPath.Equals(
                        [IO.Path]::GetFullPath($executablePath),
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        $dependencyInputs = @(
            $lockedTool.Inputs |
                Where-Object {
                    $_.FinalPath.Equals(
                        [IO.Path]::GetFullPath($dependencyPath),
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        $orderedInputPaths = @(
            $lockedTool.Inputs |
                ForEach-Object { [IO.Path]::GetFullPath($_.FinalPath) }
        )
        if ([string]$lockedTool.Path -cne
                [IO.Path]::GetFullPath($executablePath) -or
            [string]$lockedTool.Version -cne $version -or
            @($lockedTool.Inputs).Count -ne 2 -or
            $orderedInputPaths[0] -cne
                [IO.Path]::GetFullPath($dependencyPath) -or
            $orderedInputPaths[1] -cne
                [IO.Path]::GetFullPath($executablePath) -or
            $executableInputs.Count -ne 1 -or
            $dependencyInputs.Count -ne 1 -or
            -not [object]::ReferenceEquals(
                $lockedTool.Input,
                $executableInputs[0]) -or
            $executableInputs[0].ComputeHash('SHA256') -cne
                $expectedPayloadHash -or
            $dependencyInputs[0].ComputeHash('SHA256') -cne
                $expectedDependencyHash) {
            throw (
                'Locked WiX policy did not return the exact deterministic ' +
                'payload input set and executable alias.')
        }

        foreach ($retainedCase in @(
                [pscustomobject]@{
                    Path = $executablePath
                    Input = $executableInputs[0]
                    Hash = $expectedPayloadHash
                },
                [pscustomobject]@{
                    Path = $dependencyPath
                    Input = $dependencyInputs[0]
                    Hash = $expectedDependencyHash
                })) {
            $writeBlocked = $false
            try {
                [IO.File]::WriteAllText(
                    $retainedCase.Path,
                    'attacker-write',
                    $utf8)
            }
            catch {
                $writeBlocked = Test-SharingViolation -ErrorRecord $_
            }
            $moveBlocked = $false
            try {
                Move-Item `
                    -LiteralPath $retainedCase.Path `
                    -Destination ($retainedCase.Path + '.attacker-moved') `
                    -ErrorAction Stop
            }
            catch {
                $moveBlocked = Test-SharingViolation -ErrorRecord $_
            }
            if (-not $writeBlocked -or -not $moveBlocked -or
                $retainedCase.Input.ComputeHash('SHA256') -cne
                    $retainedCase.Hash) {
                throw (
                    'A retained WiX payload handle did not preserve its ' +
                    'exact package bytes against write/rename mutation: ' +
                    $retainedCase.Path)
            }
        }
    }
    finally {
        Close-TestLockedWix -LockedTool $lockedTool
    }

    $lockedExtension = Open-DesktopPetLockedWixExtension `
        -LockPath $lockPath `
        -ExtensionRoot $extensionRoot
    try {
        if ([string]$lockedExtension.Path -cne
                [IO.Path]::GetFullPath($extensionPath) -or
            [string]$lockedExtension.Version -cne $version -or
            @($lockedExtension.Inputs).Count -ne 1 -or
            -not [object]::ReferenceEquals(
                $lockedExtension.Input,
                @($lockedExtension.Inputs)[0]) -or
            $lockedExtension.Input.ComputeHash('SHA256') -cne
                $expectedExtensionHash) {
            throw (
                'Locked WiX UI extension policy did not return the exact ' +
                'retained extension DLL.')
        }

        $extensionWriteBlocked = $false
        try {
            [IO.File]::WriteAllText(
                $extensionPath,
                'attacker-extension-write',
                $utf8)
        }
        catch {
            $extensionWriteBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
        $extensionMoveBlocked = $false
        try {
            Move-Item `
                -LiteralPath $extensionPath `
                -Destination ($extensionPath + '.attacker-moved') `
                -ErrorAction Stop
        }
        catch {
            $extensionMoveBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
        if (-not $extensionWriteBlocked -or
            -not $extensionMoveBlocked -or
            $lockedExtension.Input.ComputeHash('SHA256') -cne
                $expectedExtensionHash) {
            throw (
                'The retained WiX UI extension handle did not preserve its ' +
                'exact locked bytes against write/rename mutation.')
        }
    }
    finally {
        Close-TestLockedWix -LockedTool $lockedExtension
    }

    [IO.File]::WriteAllText(
        $extensionPath,
        'changed-extension-after-release',
        $utf8)
    $extensionMismatchRejected = $false
    try {
        $unexpected = Open-DesktopPetLockedWixExtension `
            -LockPath $lockPath `
            -ExtensionRoot $extensionRoot
        Close-TestLockedWix -LockedTool $unexpected
    }
    catch {
        $extensionMismatchRejected =
            $_.Exception.Message -match
                '(?i)extension differs'
    }
    if (-not $extensionMismatchRejected) {
        throw 'Locked WiX policy accepted a UI extension content mismatch.'
    }
    [IO.File]::WriteAllBytes($extensionPath, $extensionBytes)

    [IO.File]::WriteAllText(
        $extensionExtraPath,
        'attacker-extension-extra',
        $utf8)
    $extensionExtraRejected = $false
    try {
        $unexpected = Open-DesktopPetLockedWixExtension `
            -LockPath $lockPath `
            -ExtensionRoot $extensionRoot
        Close-TestLockedWix -LockedTool $unexpected
    }
    catch {
        $extensionExtraRejected =
            $_.Exception.Message -match
                '(?i)unexpected file'
    }
    if (-not $extensionExtraRejected) {
        throw 'Locked WiX policy accepted an injected extension file.'
    }
    Remove-Item -LiteralPath $extensionExtraPath -Force

    [IO.File]::WriteAllText($dependencyPath, 'changed-after-release', $utf8)
    $mismatchRejected = $false
    try {
        $unexpected = Open-DesktopPetLockedWixExecutable `
            -LockPath $lockPath `
            -ToolRoot $toolRoot
        Close-TestLockedWix -LockedTool $unexpected
    }
    catch {
        $mismatchRejected =
            $_.Exception.Message -match
                '(?i)payload file differs'
    }
    if (-not $mismatchRejected) {
        throw 'Locked WiX policy accepted an adjacent DLL content mismatch.'
    }
    [IO.File]::WriteAllBytes($dependencyPath, $dependencyBytes)

    [IO.File]::WriteAllText($extraPath, 'attacker-extra', $utf8)
    $extraRejected = $false
    try {
        $unexpected = Open-DesktopPetLockedWixExecutable `
            -LockPath $lockPath `
            -ToolRoot $toolRoot
        Close-TestLockedWix -LockedTool $unexpected
    }
    catch {
        $extraRejected =
            $_.Exception.Message -match
                '(?i)unexpected file'
    }
    if (-not $extraRejected) {
        throw 'Locked WiX policy accepted an injected extra payload file.'
    }
    Remove-Item -LiteralPath $extraPath -Force

    Move-Item `
        -LiteralPath $dependencyPath `
        -Destination $missingHoldingPath
    $missingRejected = $false
    try {
        $unexpected = Open-DesktopPetLockedWixExecutable `
            -LockPath $lockPath `
            -ToolRoot $toolRoot
        Close-TestLockedWix -LockedTool $unexpected
    }
    catch {
        $missingRejected =
            $_.Exception.Message -match
                '(?i)missing locked package file'
    }
    finally {
        Move-Item `
            -LiteralPath $missingHoldingPath `
            -Destination $dependencyPath
    }
    if (-not $missingRejected) {
        throw 'Locked WiX policy accepted a missing payload file.'
    }

    $hardLink = New-Item `
        -ItemType HardLink `
        -Path $hardLinkPath `
        -Target $dependencyPath `
        -ErrorAction Stop
    try {
        $hardLinkRejected = $false
        try {
            $unexpected = Open-DesktopPetLockedWixExecutable `
                -LockPath $lockPath `
                -ToolRoot $toolRoot
            Close-TestLockedWix -LockedTool $unexpected
        }
        catch {
            $hardLinkRejected =
                $_.Exception.Message -match
                    '(?i)hard-link alias'
        }
        if (-not $hardLinkRejected) {
            throw 'Locked WiX policy accepted a hard-linked payload file.'
        }
    }
    finally {
        Remove-Item -LiteralPath $hardLink.FullName -Force
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratch `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}

Write-Host (
    'PASS: exact digest-locked WiX payload manifest, retained executable and ' +
    'dependency/UI-extension protection, and mismatch/missing/extra/' +
    'hard-link rejection.'
) -ForegroundColor Green
