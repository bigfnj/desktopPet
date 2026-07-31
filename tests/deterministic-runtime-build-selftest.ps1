#requires -Version 5
[CmdletBinding()]
param(
    [switch]$KeepVerifiedRuntime,
    [string]$ProvenancePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Deterministic runtime rebuild testing requires Windows MSBuild.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$buildScript = Join-Path $repoRoot 'build.ps1'
$projectPath = Join-Path $repoRoot 'src\DesktopPet_Portable.csproj'
$lockPath = Join-Path $repoRoot 'src\packages.lock.json'
$manifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
$runtimeRoot =
    Join-Path $repoRoot 'build\DesktopPetPortable\bin\Release\x64'
$pathSafety = Join-Path $repoRoot 'packaging\StagingPathSafety.ps1'
. $pathSafety

$compilerId = 'Microsoft.Net.Compilers.Toolset'
$compilerVersion = '4.14.0'
$referenceId = 'Microsoft.NETFramework.ReferenceAssemblies.net48'
$referenceVersion = '1.0.3'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$packageReferences = @(
    $project.SelectNodes(
        '/*[local-name()="Project"]/*[local-name()="ItemGroup"]/' +
        '*[local-name()="PackageReference"]')
)
foreach ($expected in @(
        [pscustomobject]@{
            Id = $compilerId
            Version = "[$compilerVersion]"
        },
        [pscustomobject]@{
            Id = $referenceId
            Version = "[$referenceVersion]"
        })) {
    $matches = @(
        $packageReferences |
            Where-Object { [string]$_.Include -ceq $expected.Id })
    if ($matches.Count -ne 1 -or
        [string]$matches[0].Version -cne $expected.Version -or
        [string]$matches[0].PrivateAssets -cne 'all') {
        throw (
            "Project must contain one private exact package pin " +
            "$($expected.Id) $($expected.Version).")
    }
}

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$framework = $lock.dependencies.'.NETFramework,Version=v4.8'
if ($null -eq $framework) {
    throw 'packages.lock.json has no .NET Framework 4.8 dependency graph.'
}
$lockedPackages = @{}
foreach ($expected in @(
        [pscustomobject]@{ Id = $compilerId; Version = $compilerVersion },
        [pscustomobject]@{ Id = $referenceId; Version = $referenceVersion })) {
    $entry = $framework.($expected.Id)
    if ($null -eq $entry -or
        [string]$entry.type -cne 'Direct' -or
        [string]$entry.requested -cne
            "[$($expected.Version), $($expected.Version)]" -or
        [string]$entry.resolved -cne $expected.Version -or
        [string]$entry.contentHash -cnotmatch
            '^[A-Za-z0-9+/]{86}==$') {
        throw "Locked build tool package is missing or not exact: $($expected.Id)"
    }
    $lockedPackages[$expected.Id] = $entry
}

$runtimeFiles = @(
    Get-Content -LiteralPath $manifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($runtimeFiles.Count -eq 0) {
    throw 'Runtime payload manifest is empty.'
}

$tempRoot = Get-DesktopPetCanonicalPath -Path ([IO.Path]::GetTempPath())
$scratchRoot = Join-Path $tempRoot (
    'DesktopPet-DeterministicRuntime-' + [Guid]::NewGuid().ToString('N'))
$preservedRuntime = Join-Path $scratchRoot 'preserved-runtime'
$runtimeExisted = Test-Path -LiteralPath $runtimeRoot -PathType Container

function Get-FileHashMap {
    param([Parameter(Mandatory = $true)][string]$Root)

    $map = New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::Ordinal)
    foreach ($name in $runtimeFiles) {
        if (-not (Test-DesktopPetWindowsLeafName -Name $name) -or
            $map.ContainsKey($name)) {
            throw "Runtime manifest contains an unsafe or duplicate name: '$name'."
        }
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Runtime rebuild is missing manifest file: $path"
        }
        $map.Add(
            $name,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
    }
    return ,$map
}

function Get-ContentTreeHash {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolved = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    $records = @(
        Get-ChildItem -LiteralPath $resolved -File -Recurse |
            Where-Object { $_.Name -cne '.nupkg.metadata' } |
            ForEach-Object {
                $relative = $_.FullName.Substring($resolved.Length + 1).
                    Replace('\', '/')
                $hash = (
                    Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                ).Hash
                "$relative|$($_.Length)|$hash"
            } |
            Sort-Object
    )
    if ($records.Count -eq 0) {
        throw "Locked package content tree is empty: $resolved"
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($records -join "`n"))
        return ([BitConverter]::ToString(
            $sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-MapsEqual {
    param(
        [Parameter(Mandatory = $true)]$First,
        [Parameter(Mandatory = $true)]$Second
    )
    if ($First.Count -ne $Second.Count) {
        throw 'Independent runtime rebuilds produced different file counts.'
    }
    foreach ($name in $First.Keys) {
        if (-not $Second.ContainsKey($name) -or
            [string]$First[$name] -cne [string]$Second[$name]) {
            throw "Independent runtime rebuild hash mismatch: $name"
        }
    }
}

function Invoke-IndependentBuild {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$InvocationRoot
    )

    $packageCache = Join-Path $InvocationRoot 'nuget-packages'
    $httpCache = Join-Path $InvocationRoot 'nuget-http-cache'
    $temporary = Join-Path $InvocationRoot 'temp'
    $working = Join-Path $InvocationRoot 'working directory'
    New-Item -ItemType Directory `
        -Path $packageCache, $httpCache, $temporary, $working `
        -Force | Out-Null

    $priorPackages = $env:NUGET_PACKAGES
    $priorHttpCache = $env:NUGET_HTTP_CACHE_PATH
    $priorTemp = $env:TEMP
    $priorTmp = $env:TMP
    $priorCi = $env:CI
    $priorSharedCompilation = $env:UseSharedCompilation
    $priorNodeReuse = $env:MSBUILDDISABLENODEREUSE
    Push-Location $working
    try {
        $env:NUGET_PACKAGES = $packageCache
        $env:NUGET_HTTP_CACHE_PATH = $httpCache
        $env:TEMP = $temporary
        $env:TMP = $temporary
        $env:CI = 'true'
        # Force each invocation to execute csc.exe from its own verified package
        # tree instead of reusing a compiler server or an MSBuild worker node.
        $env:UseSharedCompilation = 'false'
        $env:MSBUILDDISABLENODEREUSE = '1'
        & $buildScript -Release -Clean -LockedRestore | Out-Host
    }
    finally {
        $env:NUGET_PACKAGES = $priorPackages
        $env:NUGET_HTTP_CACHE_PATH = $priorHttpCache
        $env:TEMP = $priorTemp
        $env:TMP = $priorTmp
        $env:CI = $priorCi
        $env:UseSharedCompilation = $priorSharedCompilation
        $env:MSBUILDDISABLENODEREUSE = $priorNodeReuse
        Pop-Location
    }

    $compilerRoot = Join-Path $packageCache (
        'microsoft.net.compilers.toolset\' + $compilerVersion)
    $referenceRoot = Join-Path $packageCache (
        'microsoft.netframework.referenceassemblies.net48\' +
        $referenceVersion)
    $csc = @(
        Get-ChildItem -LiteralPath $compilerRoot `
            -Filter 'csc.exe' -File -Recurse)
    $referenceMscorlib = Join-Path $referenceRoot (
        'build\.NETFramework\v4.8\mscorlib.dll')
    if ($csc.Count -ne 1 -or
        -not (Test-Path -LiteralPath $referenceMscorlib -PathType Leaf)) {
        throw "$Name did not restore the locked compiler/reference toolchain."
    }

    [xml]$generatedProps =
        Get-Content -LiteralPath (
            Join-Path $repoRoot 'src\obj\DesktopPet_Portable.csproj.nuget.g.props') `
            -Raw
    [xml]$generatedTargets =
        Get-Content -LiteralPath (
            Join-Path $repoRoot 'src\obj\DesktopPet_Portable.csproj.nuget.g.targets') `
            -Raw
    $packageRootNodes = @(
        $generatedProps.SelectNodes(
            '/*[local-name()="Project"]/*[local-name()="PropertyGroup"]/' +
            '*[local-name()="NuGetPackageRoot"]'))
    $expectedPackageRoot = [IO.Path]::GetFullPath($packageCache).TrimEnd('\')
    if ($packageRootNodes.Count -ne 1) {
        throw "$Name generated an ambiguous NuGetPackageRoot."
    }
    $observedPackageRootText =
        ([string]$packageRootNodes[0].InnerText).TrimEnd('\')
    $userProfileToken = '$(UserProfile)'
    if ($observedPackageRootText.StartsWith(
            $userProfileToken,
            [StringComparison]::OrdinalIgnoreCase)) {
        $profileRoot = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::UserProfile)
        $profileSuffix = $observedPackageRootText.Substring(
            $userProfileToken.Length).TrimStart('\')
        $observedPackageRootText = Join-Path $profileRoot $profileSuffix
    }
    if ($observedPackageRootText.Contains('$(')) {
        throw (
            "$Name generated an unresolved NuGetPackageRoot: " +
            "'$observedPackageRootText'.")
    }
    $observedPackageRoot =
        [IO.Path]::GetFullPath($observedPackageRootText)
    if (-not $observedPackageRoot.Equals(
            $expectedPackageRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            "$Name restored through unexpected NuGetPackageRoot " +
            "'$observedPackageRoot'; expected '$expectedPackageRoot'.")
    }
    $propsImports = @(
        $generatedProps.SelectNodes('//*[local-name()="Import"]') |
            ForEach-Object { [string]$_.Project })
    $targetsImports = @(
        $generatedTargets.SelectNodes('//*[local-name()="Import"]') |
            ForEach-Object { [string]$_.Project })
    $compilerImport = (
        '$(NuGetPackageRoot)\microsoft.net.compilers.toolset\' +
        "$compilerVersion\build\Microsoft.Net.Compilers.Toolset.props")
    $referenceImport = (
        '$(NuGetPackageRoot)\microsoft.netframework.referenceassemblies.net48\' +
        "$referenceVersion\build\" +
        'Microsoft.NETFramework.ReferenceAssemblies.net48.targets')
    if ($propsImports -cnotcontains $compilerImport -or
        $targetsImports -cnotcontains $referenceImport) {
        throw "$Name did not import both locked build-only package paths."
    }

    return [pscustomobject]@{
        Runtime = Get-FileHashMap -Root $runtimeRoot
        CompilerTree = Get-ContentTreeHash -Root $compilerRoot
        ReferenceTree = Get-ContentTreeHash -Root $referenceRoot
        CompilerPath = $csc[0].FullName
        ReferencePath = $referenceMscorlib
    }
}

$verified = $false
try {
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    if ($runtimeExisted) {
        New-Item -ItemType Directory -Path $preservedRuntime -Force |
            Out-Null
        foreach ($item in @(
                Get-ChildItem -LiteralPath $runtimeRoot -Force)) {
            Copy-Item -LiteralPath $item.FullName `
                -Destination $preservedRuntime -Recurse -Force
        }
    }

    $first = Invoke-IndependentBuild `
        -Name 'First locked runtime rebuild' `
        -InvocationRoot (Join-Path $scratchRoot 'first')
    $second = Invoke-IndependentBuild `
        -Name 'Second locked runtime rebuild' `
        -InvocationRoot (Join-Path $scratchRoot 'second different depth')

    Assert-MapsEqual -First $first.Runtime -Second $second.Runtime
    if ($first.CompilerTree -cne $second.CompilerTree) {
        throw 'Independent restores produced different compiler package content.'
    }
    if ($first.ReferenceTree -cne $second.ReferenceTree) {
        throw 'Independent restores produced different reference-assembly package content.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ProvenancePath)) {
        $resolvedProvenance = [IO.Path]::GetFullPath($ProvenancePath)
        if (-not (Test-Path -LiteralPath $resolvedProvenance -PathType Leaf)) {
            throw "Compiler provenance file does not exist: $resolvedProvenance"
        }
        $lines = @(
            "compiler_package=$compilerId@$compilerVersion"
            (
                "compiler_package_content_hash={0}" -f
                [string]$lockedPackages[$compilerId].contentHash)
            "compiler_package_tree_sha256=$($first.CompilerTree.ToLowerInvariant())"
            "reference_package=$referenceId@$referenceVersion"
            (
                "reference_package_content_hash={0}" -f
                [string]$lockedPackages[$referenceId].contentHash)
            "reference_package_tree_sha256=$($first.ReferenceTree.ToLowerInvariant())"
            'independent_runtime_rebuild_hashes_match=true'
        )
        [IO.File]::AppendAllText(
            $resolvedProvenance,
            (($lines -join [Environment]::NewLine) +
                [Environment]::NewLine),
            (New-Object Text.UTF8Encoding($false)))
    }

    $verified = $true
    Write-Host (
        "PASS: two independent locked compiler/reference restores produced " +
        "$($runtimeFiles.Count) byte-identical runtime files."
    ) -ForegroundColor Green
}
finally {
    if (-not ($KeepVerifiedRuntime -and $verified)) {
        $allowedRoot = Join-Path $repoRoot 'build'
        Reset-DesktopPetStagingDirectory `
            -Path $runtimeRoot `
            -AllowedRoot $allowedRoot `
            -TrustedRoot $repoRoot
        if ($runtimeExisted) {
            foreach ($item in @(
                    Get-ChildItem -LiteralPath $preservedRuntime -Force)) {
                Copy-Item -LiteralPath $item.FullName `
                    -Destination $runtimeRoot -Recurse -Force
            }
        }
    }
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-DesktopPetSafeDirectory `
            -Path $scratchRoot `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
