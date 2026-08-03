#requires -Version 5
[CmdletBinding()]
param(
    [string]$LockPath,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [string]$ToolPath,
    [switch]$GlobalExtension,
    [string]$ProvenancePath,
    [ValidatePattern('^[a-z0-9_]*$')][string]$ProvenancePrefix = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $scriptDirectory 'wix-toolchain-lock.json'
}

. (Join-Path $scriptDirectory 'StagingPathSafety.ps1')
. (Join-Path $scriptDirectory 'WixToolchainPolicy.ps1')

function Write-DesktopPetNewUtf8File {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Text)
    $stream = New-Object IO.FileStream(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Save-DesktopPetHttpsFileCreateNew {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Uri]$Uri,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Uri.Scheme -cne 'https') {
        throw "Locked package download must use HTTPS: $Uri"
    }
    $output = New-Object IO.FileStream(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::WriteThrough)
    $response = $null
    $responseStream = $null
    try {
        $request = [Net.HttpWebRequest]::Create($Uri)
        $request.Method = 'GET'
        $request.Timeout = 120000
        $request.ReadWriteTimeout = 120000
        $request.AllowAutoRedirect = $true
        $request.MaximumAutomaticRedirections = 5
        $request.UserAgent = 'DesktopPet locked WiX bootstrap'
        $request.AutomaticDecompression =
            [Net.DecompressionMethods]::None
        $response = $request.GetResponse()
        if ([int]$response.StatusCode -ne 200) {
            throw (
                "Locked package download returned HTTP " +
                "$([int]$response.StatusCode): $Uri")
        }
        if ($response.ResponseUri.Scheme -cne 'https') {
            throw (
                'Locked package download redirected outside HTTPS: ' +
                $response.ResponseUri)
        }
        $responseStream = $response.GetResponseStream()
        $responseStream.CopyTo($output, 65536)
        $output.Flush($true)
    }
    finally {
        if ($null -ne $responseStream) {
            $responseStream.Dispose()
        }
        if ($null -ne $response) {
            $response.Dispose()
        }
        $output.Dispose()
    }
}

$resolvedLock = [IO.Path]::GetFullPath($LockPath)
$lockParent = Split-Path -Parent $resolvedLock
if (-not (Test-Path -LiteralPath $resolvedLock -PathType Leaf)) {
    throw "WiX toolchain lock is missing: $resolvedLock"
}
[void](Assert-DesktopPetPathChainSafe `
    -Path $resolvedLock `
    -TrustedRoot $lockParent)
if ([DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
        $resolvedLock) -ne 1) {
    throw "WiX toolchain lock must not be a hard-link alias: $resolvedLock"
}

$requestedPackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$packageParent = Split-Path -Parent $requestedPackageRoot
if ([string]::IsNullOrWhiteSpace($packageParent) -or
    -not (Test-Path -LiteralPath $packageParent -PathType Container)) {
    throw "WiX package-root parent must already exist: $packageParent"
}
[void](Assert-DesktopPetPathChainSafe `
    -Path $requestedPackageRoot `
    -TrustedRoot $packageParent)
if (Test-Path -LiteralPath $requestedPackageRoot) {
    throw (
        'WiX PackageRoot must be an absent, private per-run directory: ' +
        $requestedPackageRoot)
}

$resolvedPackageRoot = Join-Path $requestedPackageRoot (
    '.DesktopPet-wix-' + [Guid]::NewGuid().ToString('N'))

$resolvedToolPath = $null
if (-not [string]::IsNullOrWhiteSpace($ToolPath)) {
    $resolvedToolPath = [IO.Path]::GetFullPath($ToolPath)
    $toolParent = Split-Path -Parent $resolvedToolPath
    if ([string]::IsNullOrWhiteSpace($toolParent) -or
        -not (Test-Path -LiteralPath $toolParent -PathType Container)) {
        throw "WiX tool-path parent must already exist: $toolParent"
    }
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedToolPath `
        -TrustedRoot $toolParent)
    if (Test-Path -LiteralPath $resolvedToolPath) {
        throw (
            'WiX ToolPath must be an absent, private per-run directory: ' +
            $resolvedToolPath)
    }
}
if (-not $GlobalExtension -and $null -eq $resolvedToolPath) {
    throw (
        'A non-global WiX extension installation requires a private ' +
        '-ToolPath so its locked DLL has a reusable location.')
}

$resolvedProvenance = $null
$provenanceParent = $null
if (-not [string]::IsNullOrWhiteSpace($ProvenancePath)) {
    $resolvedProvenance = [IO.Path]::GetFullPath($ProvenancePath)
    $provenanceParent = Split-Path -Parent $resolvedProvenance
    if (-not (Test-Path -LiteralPath $provenanceParent -PathType Container)) {
        throw "WiX provenance parent directory not found: $provenanceParent"
    }
    $protectedProvenanceDirectories = @($requestedPackageRoot)
    if ($null -ne $resolvedToolPath) {
        $protectedProvenanceDirectories += $resolvedToolPath
    }
    $resolvedProvenance = Assert-DesktopPetOutputFileSafe `
        -Path $resolvedProvenance `
        -TrustedRoot $provenanceParent `
        -ProtectedPaths @(
            $resolvedLock,
            $MyInvocation.MyCommand.Path) `
        -ProtectedDirectories $protectedProvenanceDirectories
}

$packageRootLease = $null
$resolvedPackageRootLease = $null
$toolPathLease = $null
$nugetConfigInput = $null
$toolNugetConfigInput = $null
$toolNugetConfigPath = $null
$wixToolInputs = New-Object 'Collections.Generic.List[IDisposable]'
$wixExtensionInputs =
    New-Object 'Collections.Generic.List[IDisposable]'
$lockedWixPayload = $null
$packageFileLeases =
    New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::OrdinalIgnoreCase)
try {
    $protectedScratchPaths = @(
        $resolvedLock,
        $MyInvocation.MyCommand.Path)
    if ($null -ne $resolvedProvenance) {
        $protectedScratchPaths += $resolvedProvenance
    }
    $protectedPackageDirectories = @()
    if ($null -ne $resolvedToolPath) {
        $protectedPackageDirectories += $resolvedToolPath
    }

    $packageRootLease = Open-DesktopPetNewScratchDirectory `
        -Path $requestedPackageRoot `
        -AllowedRoot $packageParent `
        -TrustedRoot $packageParent `
        -ProtectedPaths $protectedScratchPaths `
        -ProtectedDirectories $protectedPackageDirectories
    $resolvedPackageRootLease = Open-DesktopPetNewScratchDirectory `
        -Path $resolvedPackageRoot `
        -AllowedRoot $requestedPackageRoot `
        -TrustedRoot $packageParent `
        -ProtectedPaths $protectedScratchPaths `
        -ProtectedDirectories $protectedPackageDirectories

    if ($null -ne $resolvedToolPath) {
        $toolPathLease = Open-DesktopPetNewScratchDirectory `
            -Path $resolvedToolPath `
            -AllowedRoot (Split-Path -Parent $resolvedToolPath) `
            -TrustedRoot (Split-Path -Parent $resolvedToolPath) `
            -ProtectedPaths $protectedScratchPaths `
            -ProtectedDirectories @($requestedPackageRoot)
    }

$lockInput = Open-DesktopPetValidatedInputFile `
    -Path $resolvedLock `
    -Root $lockParent
try {
    $lockText = $lockInput.ReadAllTextUtf8(1MB)
}
finally {
    $lockInput.Dispose()
}
$lock = ConvertFrom-Json -InputObject $lockText
if ([int]$lock.schemaVersion -ne 1) {
    throw "Unsupported WiX toolchain lock schema: $($lock.schemaVersion)"
}
if ([string]$lock.wixVersion -cne '5.0.2') {
    throw "The WiX toolchain lock must pin WiX 5.0.2; found '$($lock.wixVersion)'."
}

$packagesById = New-Object 'Collections.Generic.Dictionary[string,object]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($package in @($lock.packages)) {
    $id = [string]$package.id
    $version = [string]$package.version
    $fileName = [string]$package.fileName
    $source = [string]$package.source
    $sha256 = [string]$package.sha256
    $size = [long]$package.size
    if ([string]::IsNullOrWhiteSpace($id) -or
        [string]::IsNullOrWhiteSpace($version) -or
        $fileName -notmatch '^[A-Za-z0-9_.-]+\.nupkg$' -or
        $sha256 -notmatch '^[0-9a-f]{64}$' -or
        $size -le 0 -or
        $packagesById.ContainsKey($id)) {
        throw "The WiX toolchain lock contains invalid or duplicate package metadata for '$id'."
    }
    $expectedSource = (
        'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{2}' -f
        $id.ToLowerInvariant(),
        $version.ToLowerInvariant(),
        $fileName.ToLowerInvariant())
    if ($source -cne $expectedSource) {
        throw "The WiX toolchain package source is not the exact NuGet.org flat-container URL: '$source'."
    }
    $packagesById.Add($id, $package)
}

$expectedIds = @('wix', 'WixToolset.UI.wixext')
if ($packagesById.Count -ne $expectedIds.Count) {
    throw 'The WiX toolchain lock must contain exactly the tool and UI extension packages.'
}
foreach ($id in $expectedIds) {
    if (-not $packagesById.ContainsKey($id)) {
        throw "The WiX toolchain lock is missing package '$id'."
    }
    if ([string]$packagesById[$id].version -cne [string]$lock.wixVersion) {
        throw "The WiX package '$id' does not match the locked toolchain version."
    }
}

foreach ($id in $expectedIds) {
    $package = $packagesById[$id]
    $packagePath = Join-Path $resolvedPackageRoot ([string]$package.fileName)
    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'wix-package-stage-write' `
        -Path $packagePath
    Save-DesktopPetHttpsFileCreateNew `
        -Uri ([Uri][string]$package.source) `
        -Path $packagePath
    $packageFileLease = Open-DesktopPetValidatedInputFile `
        -Path $packagePath `
        -Root $resolvedPackageRoot
    $packageFileLeases.Add($id, $packageFileLease)
    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'wix-package-stage-mutate' `
        -Path $packagePath
    if ([long]$packageFileLease.Length -ne [long]$package.size) {
        throw "Downloaded WiX package '$id' has length $($packageFileLease.Length), expected $($package.size)."
    }
    $observedHash =
        $packageFileLease.ComputeHash('SHA256').ToLowerInvariant()
    if ($observedHash -cne [string]$package.sha256) {
        throw "Downloaded WiX package '$id' failed its locked SHA-256 check."
    }
    & dotnet nuget verify --all $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet signature verification failed for locked WiX package '$id'."
    }
    if ($packageFileLease.ComputeHash('SHA256').ToLowerInvariant() -cne
        [string]$package.sha256) {
        throw "Downloaded WiX package '$id' changed during signature verification."
    }
    if ($id -ceq 'wix') {
        $lockedWixPayload =
            Get-DesktopPetWixToolPayload -PackageInput $packageFileLease
    }
}
if ($null -eq $lockedWixPayload) {
    throw 'The locked WiX executable payload was not resolved from its package.'
}

$escapedPackageRoot = [Security.SecurityElement]::Escape(
    $resolvedPackageRoot)
$nugetConfigPath = Join-Path $resolvedPackageRoot 'NuGet.Config'
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="verified-local" value="$escapedPackageRoot" />
  </packageSources>
</configuration>
"@
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'wix-package-config-write' `
    -Path $nugetConfigPath
$expectedNugetConfig =
    $nugetConfig.Trim() + [Environment]::NewLine
Write-DesktopPetNewUtf8File `
    -Path $nugetConfigPath `
    -Text $expectedNugetConfig
$nugetConfigInput = Open-DesktopPetValidatedInputFile `
    -Path $nugetConfigPath `
    -Root $resolvedPackageRoot
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'wix-package-config-handoff' `
    -Path $nugetConfigPath
if ($nugetConfigInput.ReadAllTextUtf8(1MB) -cne
    $expectedNugetConfig) {
    throw 'Generated NuGet.Config bytes differ from the validated configuration.'
}

$previousNugetPackages = [Environment]::GetEnvironmentVariable(
    'NUGET_PACKAGES',
    'Process')
try {
    $env:NUGET_PACKAGES = Join-Path $resolvedPackageRoot 'nuget-cache'
    foreach ($id in $expectedIds) {
        if ($packageFileLeases[$id].
                ComputeHash('SHA256').ToLowerInvariant() -cne
            [string]$packagesById[$id].sha256) {
            throw "Locked WiX package '$id' changed before tool installation."
        }
    }
    $toolPackage = $packagesById['wix']
    $toolInstallRoot = $resolvedToolPath
    if ([string]::IsNullOrWhiteSpace($ToolPath)) {
        $toolInstallRoot = Get-DesktopPetDotnetGlobalToolRoot
        $globalWixShim = Join-Path $toolInstallRoot 'wix.exe'
        if (Test-Path -LiteralPath $globalWixShim) {
            throw "Refusing to reuse a pre-existing global WiX executable: $globalWixShim"
        }
        & dotnet tool install `
            --global `
            ([string]$toolPackage.id) `
            --version ([string]$toolPackage.version) `
            --source $resolvedPackageRoot `
            --no-cache
    }
    else {
        $wixShim = Join-Path $resolvedToolPath 'wix.exe'
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'wix-tool-stage-write' `
            -Path $wixShim
        & dotnet tool install `
            ([string]$toolPackage.id) `
            --tool-path $resolvedToolPath `
            --version ([string]$toolPackage.version) `
            --source $resolvedPackageRoot `
            --no-cache
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'The digest-locked WiX tool package could not be installed.'
    }
    $wixStoreRoot = Join-Path $toolInstallRoot (
        '.store\{0}\{1}\{0}\{1}' -f
        ([string]$toolPackage.id).ToLowerInvariant(),
        [string]$toolPackage.version)
    $wix = Join-Path $wixStoreRoot (
        [string]$lockedWixPayload.RelativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) {
        throw "The installed locked WiX package payload is missing: $wix"
    }
    if ($null -ne $resolvedToolPath) {
        [void](Assert-DesktopPetPathChainSafe `
            -Path $resolvedToolPath `
            -TrustedRoot (Split-Path -Parent $resolvedToolPath))
    }
    Invoke-DesktopPetStagingMutationTestHook `
        -Operation 'wix-tool-handoff' `
        -Path $wix
    $installedWixTool = Open-DesktopPetLockedWixExecutable `
        -LockPath $resolvedLock `
        -ToolRoot $toolInstallRoot
    foreach ($installedWixInput in @($installedWixTool.Inputs)) {
        $wixToolInputs.Add($installedWixInput)
    }
    $wix = [string]$installedWixTool.Path
    if ([string]$installedWixTool.Version -cne
        [string]$lock.wixVersion) {
        throw (
            'The installed WiX payload does not match the locked toolchain ' +
            'version.')
    }

    $extensionPackage = $packagesById['WixToolset.UI.wixext']
    $extensionWorkingDirectory = $resolvedPackageRoot
    if (-not $GlobalExtension) {
        $extensionWorkingDirectory = $resolvedToolPath
        $toolNugetConfigPath =
            Join-Path $resolvedToolPath 'NuGet.Config'
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'wix-tool-config-write' `
            -Path $toolNugetConfigPath
        Write-DesktopPetNewUtf8File `
            -Path $toolNugetConfigPath `
            -Text $expectedNugetConfig
        $toolNugetConfigInput = Open-DesktopPetValidatedInputFile `
            -Path $toolNugetConfigPath `
            -Root $resolvedToolPath
        if ($toolNugetConfigInput.ReadAllTextUtf8(1MB) -cne
            $expectedNugetConfig) {
            throw (
                'Tool-local NuGet.Config bytes differ from the validated ' +
                'configuration.')
        }
    }
    Push-Location $extensionWorkingDirectory
    try {
        if ($GlobalExtension) {
            $existingGlobalExtensions = (
                & $wix extension list -g 2>&1 | Out-String
            ).Trim()
            # A freshly installed wix has no global extension cache yet; on a clean
            # runner `wix extension list -g` exits non-zero listing an absent cache.
            # Treat that as "no global extensions" -- the reuse guard below still
            # rejects a pre-existing extension whenever a listing succeeds.
            if ($LASTEXITCODE -ne 0) {
                $existingGlobalExtensions = ''
            }
            if ($existingGlobalExtensions -match
                '(?m)^WixToolset\.UI\.wixext\s+') {
                throw 'Refusing to reuse a pre-existing global WiX UI extension.'
            }
        }
        $extensionAddArguments = @('extension', 'add')
        if ($GlobalExtension) {
            $extensionAddArguments += '-g'
        }
        $extensionAddArguments += (
            '{0}/{1}' -f
            [string]$extensionPackage.id,
            [string]$extensionPackage.version)
        & $wix @extensionAddArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'The digest-locked WiX UI extension could not be installed.'
        }

        $extensionListArguments = @('extension', 'list')
        if ($GlobalExtension) {
            $extensionListArguments += '-g'
        }
        $extensionList = (
            & $wix @extensionListArguments 2>&1 | Out-String
        ).Trim()
        $uiExtensionLines = @(
            $extensionList -split '\r?\n' |
                Where-Object {
                    $_ -match '^WixToolset\.UI\.wixext\s+'
                }
        )
        if ($LASTEXITCODE -ne 0 -or
            $uiExtensionLines.Count -ne 1 -or
            [string]$uiExtensionLines[0] -cne
                'WixToolset.UI.wixext 5.0.2') {
            throw 'The installed WiX UI extension does not match the locked 5.0.2 package.'
        }
    }
    finally {
        Pop-Location
    }
    $installedExtensionRoot = if ($GlobalExtension) {
        Get-DesktopPetWixGlobalExtensionRoot
    }
    else {
        Join-Path $resolvedToolPath '.wix\extensions'
    }
    $installedWixExtension = Open-DesktopPetLockedWixExtension `
        -LockPath $resolvedLock `
        -ExtensionRoot $installedExtensionRoot
    foreach ($installedExtensionInput in
        @($installedWixExtension.Inputs)) {
        $wixExtensionInputs.Add($installedExtensionInput)
    }
    if ($null -ne $toolNugetConfigInput) {
        $toolNugetConfigInput.Dispose()
        $toolNugetConfigInput = $null
        Remove-DesktopPetSafeFile `
            -Path $toolNugetConfigPath `
            -AllowedRoot $resolvedToolPath `
            -TrustedRoot (Split-Path -Parent $resolvedToolPath)
    }
}
finally {
    if ($null -eq $previousNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }
}
foreach ($id in $expectedIds) {
    if ($packageFileLeases[$id].
            ComputeHash('SHA256').ToLowerInvariant() -cne
        [string]$packagesById[$id].sha256) {
        throw "Locked WiX package '$id' changed during tool installation."
    }
}

$wixVersion = ((& $wix --version) | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $wixVersion -notmatch '^5\.0\.2(?:\+|$)') {
    throw "Expected WiX 5.0.2; found '$wixVersion'."
}

if ($null -ne $resolvedProvenance) {
    $provenanceLines = @(
        "$($ProvenancePrefix)wix_version=$wixVersion"
        (
            "$($ProvenancePrefix)wix_tool_package={0}@{1}" -f
            [string]$packagesById['wix'].id,
            [string]$packagesById['wix'].version)
        (
            "$($ProvenancePrefix)wix_tool_package_sha256={0}" -f
            [string]$packagesById['wix'].sha256)
        (
            "$($ProvenancePrefix)wix_extension_package={0}@{1}" -f
            [string]$packagesById['WixToolset.UI.wixext'].id,
            [string]$packagesById['WixToolset.UI.wixext'].version)
        (
            "$($ProvenancePrefix)wix_extension_package_sha256={0}" -f
            [string]$packagesById['WixToolset.UI.wixext'].sha256)
        (
            "$($ProvenancePrefix)wix_package_signature_verification=" +
            'dotnet nuget verify --all')
    )
    $protectedProvenanceDirectories = @($requestedPackageRoot)
    if ($null -ne $resolvedToolPath) {
        $protectedProvenanceDirectories += $resolvedToolPath
    }
    $resolvedProvenance = Assert-DesktopPetOutputFileSafe `
        -Path $resolvedProvenance `
        -TrustedRoot $provenanceParent `
        -ProtectedPaths @(
            $resolvedLock,
            $MyInvocation.MyCommand.Path) `
        -ProtectedDirectories $protectedProvenanceDirectories

    $existingProvenance = ''
    $provenanceDestinationExists = $false
    $provenanceDestinationSha256 = $null
    if (Test-Path -LiteralPath $resolvedProvenance -PathType Leaf) {
        $provenanceInput = Open-DesktopPetValidatedInputFile `
            -Path $resolvedProvenance `
            -Root $provenanceParent
        try {
            $provenanceDestinationSha256 =
                $provenanceInput.ComputeHash('SHA256')
            $existingProvenance =
                $provenanceInput.ReadAllTextUtf8(16MB)
            $provenanceDestinationExists = $true
        }
        finally {
            $provenanceInput.Dispose()
        }
    }

    $provenanceStaging = Join-Path $provenanceParent (
        '.DesktopPet-wix-provenance-' +
        [Guid]::NewGuid().ToString('N'))
    $provenanceStagingLease = $null
    $provenanceFileLease = $null
    $sealedProvenanceFile = $null
    $provenanceSha256 = $null
    $provenancePrimaryError = $null
    $provenanceStagingLease = Open-DesktopPetNewScratchDirectory `
        -Path $provenanceStaging `
        -AllowedRoot $provenanceParent `
        -TrustedRoot $provenanceParent `
        -ProtectedPaths @(
            $resolvedLock,
            $MyInvocation.MyCommand.Path,
            $resolvedProvenance) `
        -ProtectedDirectories $protectedProvenanceDirectories
    try {
        $temporaryProvenance = Join-Path $provenanceStaging (
            [IO.Path]::GetFileName($resolvedProvenance) + '.tmp')
        $temporaryProvenance = Assert-DesktopPetOutputFileSafe `
            -Path $temporaryProvenance `
            -TrustedRoot $provenanceParent `
            -ProtectedPaths @(
                $resolvedLock,
                $MyInvocation.MyCommand.Path,
                $resolvedProvenance) `
            -ProtectedDirectories $protectedProvenanceDirectories
        $appendedText =
            $existingProvenance +
            ($provenanceLines -join [Environment]::NewLine) +
            [Environment]::NewLine
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'wix-provenance-stage-write' `
            -Path $temporaryProvenance
        Write-DesktopPetNewUtf8File `
            -Path $temporaryProvenance `
            -Text $appendedText
        $provenanceHasher = [Security.Cryptography.SHA256]::Create()
        try {
            $expectedProvenanceSha256 = ([BitConverter]::ToString(
                $provenanceHasher.ComputeHash(
                    (New-Object Text.UTF8Encoding($false)).
                        GetBytes($appendedText)))).Replace('-', '')
        }
        finally {
            $provenanceHasher.Dispose()
        }
        $provenanceFileLease = Open-DesktopPetValidatedMutableFile `
            -Path $temporaryProvenance `
            -Root $provenanceStaging
        $sealedProvenanceFile = $provenanceFileLease.Seal()
        $provenanceFileLease = $null
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'wix-provenance-sealed-validate' `
            -Path $temporaryProvenance
        if ($sealedProvenanceFile.ReadAllTextUtf8(16MB) -cne
            $appendedText) {
            throw (
                'Generated WiX provenance differs from its exact in-memory ' +
                'authoring text.')
        }
        $provenanceSha256 =
            $sealedProvenanceFile.ComputeHash('SHA256')
        if ($provenanceSha256 -cne $expectedProvenanceSha256) {
            throw (
                'Generated WiX provenance differs from its exact in-memory ' +
                'authoring bytes.')
        }
        $publishProvenanceParameters = @{
            TemporaryPath = $temporaryProvenance
            DestinationPath = $resolvedProvenance
            TrustedRoot = $provenanceParent
            ProtectedPaths = @(
                $resolvedLock,
                $MyInvocation.MyCommand.Path)
            ProtectedDirectories = $protectedProvenanceDirectories
            SealedTemporaryFile = $sealedProvenanceFile
            ExpectedTemporarySha256 = $provenanceSha256
        }
        if ($provenanceDestinationExists) {
            $publishProvenanceParameters.ExpectedDestinationSha256 =
                $provenanceDestinationSha256
        }
        else {
            $publishProvenanceParameters.DestinationMustBeAbsent = $true
        }
        [void](Publish-DesktopPetAtomicFile @publishProvenanceParameters)
    }
    catch {
        $provenancePrimaryError = $_
        throw
    }
    finally {
        if ($null -ne $sealedProvenanceFile) {
            $sealedProvenanceFile.Dispose()
            $sealedProvenanceFile = $null
        }
        if ($null -ne $provenanceFileLease) {
            $provenanceFileLease.Dispose()
            $provenanceFileLease = $null
        }
        if ($null -ne $provenanceStagingLease) {
            $provenanceStagingLease.Dispose()
            $provenanceStagingLease = $null
        }
        if (Test-Path -LiteralPath $provenanceStaging) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $provenanceStaging `
                    -AllowedRoot $provenanceParent `
                    -TrustedRoot $provenanceParent
            }
            catch {
                if ($null -eq $provenancePrimaryError) {
                    throw
                }
                Write-Warning (
                    'WiX provenance scratch cleanup also failed; preserving ' +
                    "the primary error. Cleanup error: " +
                    $_.Exception.Message)
            }
        }
    }
}

Write-Host (
    "Installed digest-locked WiX {0} and {1} {2} from verified NuGet packages." -f
    [string]$lock.wixVersion,
    [string]$packagesById['WixToolset.UI.wixext'].id,
    [string]$packagesById['WixToolset.UI.wixext'].version
) -ForegroundColor Green
}
finally {
    if ($null -ne $toolNugetConfigInput) {
        $toolNugetConfigInput.Dispose()
    }
    foreach ($wixExtensionInput in $wixExtensionInputs) {
        $wixExtensionInput.Dispose()
    }
    foreach ($wixToolInput in $wixToolInputs) {
        $wixToolInput.Dispose()
    }
    if ($null -ne $nugetConfigInput) {
        $nugetConfigInput.Dispose()
    }
    foreach ($packageFileLease in $packageFileLeases.Values) {
        $packageFileLease.Dispose()
    }
    if ($null -ne $toolPathLease) {
        $toolPathLease.Dispose()
    }
    if ($null -ne $resolvedPackageRootLease) {
        $resolvedPackageRootLease.Dispose()
    }
    if ($null -ne $packageRootLease) {
        $packageRootLease.Dispose()
    }
}
