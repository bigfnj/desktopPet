#requires -Version 5
[CmdletBinding()]
param(
    [string]$SbomPath,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300,
    [switch]$AllowDirtyDevelopment,
    [switch]$AllowDocumentedReleaseBlockers
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-ReleaseGate-' + [Guid]::NewGuid().ToString('N'))
$toolRun = 0
$script:pathPolicy = $null

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)
    try {
        if ($Process.HasExited) { return }
    }
    catch {
        return
    }
    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
    }
    else {
        try { $Process.Kill() } catch { }
    }
}

function Invoke-BoundedTool {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $repoRoot
    )

    $script:toolRun++
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    Set-ReleaseGateProcessArguments `
        -StartInfo $startInfo `
        -ArgumentList $ArgumentList
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "$Name could not be started."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree -Process $process
            throw "$Name timed out after $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "$Name failed with exit code $($process.ExitCode).`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
        }
        return [pscustomobject]@{
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-BootstrapGit {
    param(
        [Parameter(Mandatory = $true)][string]$GitPath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [int[]]$AllowedExitCodes = @(0)
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $GitPath -C $repoRoot @ArgumentList 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw (
            "Bootstrap git $($ArgumentList -join ' ') failed with exit code " +
            "$exitCode`: $($output -join [Environment]::NewLine)")
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Assert-PathPolicyBootstrap {
    param(
        [Parameter(Mandatory = $true)][string]$GitPath,
        [switch]$AllowDirtyDevelopment
    )

    $isCi = (
        (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -and
            $env:GITHUB_ACTIONS -ine 'false') -or
        (-not [string]::IsNullOrWhiteSpace($env:CI) -and
            $env:CI -ine 'false')
    )
    if ($AllowDirtyDevelopment -and $isCi) {
        throw (
            'AllowDirtyDevelopment is a local-only diagnostic override and ' +
            'is disabled in GitHub Actions and CI.'
        )
    }

    $policyRelative = 'packaging/ReleaseGate.PathPolicy.ps1'
    $current = $repoRoot
    foreach ($segment in $policyRelative.Split('/')) {
        $matches = @(
            Get-ChildItem -LiteralPath $current -Force |
                Where-Object { $_.Name -ceq $segment }
        )
        if ($matches.Count -ne 1) {
            throw (
                'Release-gate path-policy bootstrap file is missing with ' +
                "exact case: '$policyRelative'."
            )
        }
        if (($matches[0].Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw (
                'Release-gate path-policy bootstrap file traverses a ' +
                "reparse point: '$policyRelative'."
            )
        }
        $current = $matches[0].FullName
    }
    if (-not (Test-Path -LiteralPath $current -PathType Leaf)) {
        throw "Release-gate path-policy bootstrap file is missing: '$policyRelative'."
    }

    if ($AllowDirtyDevelopment) {
        return [IO.Path]::GetFullPath($current)
    }

    $trackedResult = Invoke-BootstrapGit `
        -GitPath $GitPath `
        -ArgumentList @('ls-files', '--cached', '--', $policyRelative)
    $trackedMatches = @(
        $trackedResult.Output |
            ForEach-Object { ([string]$_).Replace('\', '/') } |
            Where-Object { $_ -ceq $policyRelative }
    )
    if ($trackedMatches.Count -ne 1) {
        throw (
            'Release-gate path-policy bootstrap file is not tracked with ' +
            "exact case: '$policyRelative'."
        )
    }
    $ignoreResult = Invoke-BootstrapGit `
        -GitPath $GitPath `
        -ArgumentList @(
            'check-ignore',
            '--no-index',
            '--quiet',
            '--',
            $policyRelative
        ) `
        -AllowedExitCodes @(0, 1)
    if ($ignoreResult.ExitCode -eq 0) {
        throw "Release-gate path-policy bootstrap file is ignored: '$policyRelative'."
    }

    $statusResult = Invoke-BootstrapGit `
        -GitPath $GitPath `
        -ArgumentList @(
            'status',
            '--porcelain=v1',
            '--untracked-files=all'
        )
    $dirtyEntries = @(
        $statusResult.Output |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($dirtyEntries.Count -gt 0) {
        $detail = @($dirtyEntries | Select-Object -First 20) -join '; '
        if ($dirtyEntries.Count -gt 20) {
            $detail += "; ... ($($dirtyEntries.Count - 20) more)"
        }
        throw (
            'Release source is dirty or contains untracked files. Commit the ' +
            "exact release snapshot before loading path policy: $detail"
        )
    }
    [void](Invoke-BootstrapGit `
        -GitPath $GitPath `
        -ArgumentList @('diff', '--check'))
    [void](Invoke-BootstrapGit `
        -GitPath $GitPath `
        -ArgumentList @('diff', '--cached', '--check'))
    return [IO.Path]::GetFullPath($current)
}

function Resolve-MSBuild {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $candidate = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }
    $known = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $known -PathType Leaf) { return $known }
    throw 'A stable MSBuild executable was not found.'
}

function Resolve-Bash {
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    $gitRoot = Split-Path (Split-Path $git -Parent) -Parent
    $candidates = @(
        (Join-Path $gitRoot 'bin\bash.exe'),
        (Join-Path $env:ProgramFiles 'Git\bin\bash.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $command = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($command -and
        $command.Source -notlike "$env:SystemRoot\System32\bash.exe" -and
        (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }
    throw 'Git Bash was not found; shell syntax validation cannot run.'
}

function Resolve-Yq {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_TOOLBOX)) {
        $candidates += Join-Path $env:CODEX_TOOLBOX 'native\yq\yq.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += Join-Path $env:LOCALAPPDATA 'DevToolbox\native\yq\yq.exe'
    }
    $command = Get-Command yq.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'yq was not found; YAML syntax validation cannot run.'
}

function Get-ExistingRepoPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    if ($null -eq $script:pathPolicy) {
        throw 'Release-gate repository path policy is not initialized.'
    }
    return Get-ReleaseGateRepoPath `
        -Policy $script:pathPolicy `
        -RelativePath $RelativePath
}

function Assert-NoNul {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
        throw "Text source contains a NUL byte: $Path"
    }
}

function Get-Utf8LineStats {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(0, 1024)][int]$ExpectedFieldCount = 0
    )

    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $lineCount = 0
    try {
        foreach ($line in [IO.File]::ReadLines($Path, $strictUtf8)) {
            $lineCount++
            if ($ExpectedFieldCount -gt 0) {
                $fields = $line.Split(
                    [char[]]@([char]9),
                    [StringSplitOptions]::None)
                if ($fields.Length -ne $ExpectedFieldCount) {
                    throw "line $lineCount has $($fields.Length) tab-separated fields; expected $ExpectedFieldCount"
                }
            }
        }
    }
    catch {
        throw "UTF-8 record validation failed for '$Path': $($_.Exception.Message)"
    }

    return [pscustomobject]@{ Lines = $lineCount }
}

function Get-NuGetLegalSourcePath {
    param(
        [Parameter(Mandatory = $true)][string]$NuGetRoot,
        [Parameter(Mandatory = $true)][string]$Package,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $packageRoot = [IO.Path]::GetFullPath((Join-Path $NuGetRoot (
        $Package.ToLowerInvariant() + '\' + $Version))).TrimEnd('\')
    $candidate = [IO.Path]::GetFullPath((Join-Path $packageRoot (
        $RelativePath.Replace('/', '\'))))
    if (-not $candidate.StartsWith(
            $packageRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "NuGet legal source escaped package root: $Package $Version $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Locked NuGet legal source is missing: $candidate"
    }
    return $candidate
}

function Get-OptionalInventoryString {
    param(
        [Parameter(Mandatory = $true)][object]$Package,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Package.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return ''
    }
    if ($property.Value -isnot [string]) {
        throw (
            "Package '$($Package.name)' optional '$Name' value must be a " +
            'string when present.'
        )
    }
    return [string]$property.Value
}

function Get-InventoryRootRelationship {
    param([Parameter(Mandatory = $true)][object]$Package)

    $relationship = Get-OptionalInventoryString `
        -Package $Package `
        -Name 'relationshipToRoot'
    if ([string]::IsNullOrWhiteSpace($relationship)) {
        return 'DEPENDS_ON'
    }
    if ($relationship -cnotin @(
            'DEPENDS_ON',
            'BUILD_TOOL_OF',
            'BUILD_DEPENDENCY_OF')) {
        throw (
            "Package '$($Package.name)' declares unsupported " +
            "relationshipToRoot '$relationship'."
        )
    }
    return $relationship
}

try {
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    $pathPolicyScript = Assert-PathPolicyBootstrap `
        -GitPath $git `
        -AllowDirtyDevelopment:$AllowDirtyDevelopment
    . $pathPolicyScript
    $script:pathPolicy = New-ReleaseGatePathPolicy `
        -RepositoryRoot $repoRoot `
        -GitPath $git `
        -AllowDirtyDevelopment:$AllowDirtyDevelopment
    [void](Get-ExistingRepoPath 'packaging/ReleaseGate.PathPolicy.ps1')
    . (Get-ExistingRepoPath 'packaging/StagingPathSafety.ps1')
    . (Get-ExistingRepoPath 'packaging/NuGetAuditPolicy.ps1')
    Assert-ReleaseGateWhitespaceClean -Policy $script:pathPolicy

    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $msbuild = Resolve-MSBuild
    $bash = Resolve-Bash
    $yq = Resolve-Yq
    $yqVersion = ((Invoke-BoundedTool -Name 'yq version' -FilePath $yq `
        -ArgumentList @('--version')).StdOut).Trim()
    if ($yqVersion -notmatch '(?m)version v4\.53\.2\s*$') {
        throw "The release gate requires yq v4.53.2 exactly; found '$yqVersion'."
    }

    $trackedFiles = @($script:pathPolicy.TrackedFiles | Sort-Object)
    $sourceFiles = @(Get-ReleaseGateSourceFiles -Policy $script:pathPolicy)

    # Generated labeling material is intentionally present in some worktrees, but
    # it must remain ignored and must never be read, tracked, or packaged here.
    $protectedExact = @(
        'src/Fortunes/label-input.tsv',
        'src/Fortunes/labels-store.tsv',
        'src/Fortunes/.texts',
        'src/Fortunes/.batchtexts',
        'src/Fortunes/label-batch.txt'
    )
    $trackedProtected = @(
        $trackedFiles | Where-Object {
            $protectedExact -contains $_ -or $_.StartsWith(
                'src/Fortunes/label-chunks/',
                [StringComparison]::Ordinal)
        }
    )
    if ($trackedProtected.Count -gt 0) {
        throw "Protected labeling artifacts are tracked: $($trackedProtected -join ', ')"
    }
    # The index can still name intentionally deleted files until the eventual
    # commit. Parse only files that exist in this source snapshot.
    $sourceFiles = @(
        $sourceFiles | Where-Object {
            Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf
        }
    )

    $gitIgnorePath = Get-ExistingRepoPath '.gitignore'
    Assert-NoNul -Path $gitIgnorePath
    $gitIgnoreLines = @(Get-Content -LiteralPath $gitIgnorePath)
    $requiredIgnores = @(
        '/src/Fortunes/label-input.tsv',
        '/src/Fortunes/labels-store.tsv',
        '/src/Fortunes/label-chunks/',
        '/src/Fortunes/.texts',
        '/src/Fortunes/.batchtexts',
        '/src/Fortunes/label-batch.txt'
    )
    foreach ($rule in $requiredIgnores) {
        if ($gitIgnoreLines -cnotcontains $rule) {
            throw "The protected-artifact ignore rule is missing: $rule"
        }
    }

    $codeOwnersPath = Get-ExistingRepoPath '.github/CODEOWNERS'
    Assert-NoNul -Path $codeOwnersPath
    $codeOwnerLines = @(
        Get-Content -LiteralPath $codeOwnersPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    )
    foreach ($pattern in @(
            '*',
            '/.github/workflows/',
            '/packaging/',
            '/installer/',
            '/ProductVersion.props',
            '/src/packages.lock.json',
            '/Tools/PetTester/packages.lock.json',
            '/tests/DesktopPet.CoreTests/packages.lock.json',
            '/THIRD_PARTY_NOTICES.md')) {
        $owned = @(
            $codeOwnerLines | Where-Object {
                $_ -match ('^' + [regex]::Escape($pattern) +
                    '\s+@[A-Za-z0-9-]+(?:\s+@[A-Za-z0-9-]+)*\s*$')
            }
        )
        if ($owned.Count -ne 1) {
            throw "CODEOWNERS must assign exactly one valid rule for '$pattern'."
        }
    }

    $attributesPath = Get-ExistingRepoPath '.gitattributes'
    Assert-NoNul -Path $attributesPath
    $attributeLines = @(Get-Content -LiteralPath $attributesPath)
    foreach ($rule in @('* text=auto eol=lf', '*.bat text eol=crlf', '*.cmd text eol=crlf')) {
        if ($attributeLines -cnotcontains $rule) {
            throw "The deterministic EOL rule is missing from .gitattributes: $rule"
        }
    }

    $textExtensions = @(
        '.config', '.cs', '.csproj', '.gitattributes', '.gitignore', '.json',
        '.md', '.props', '.ps1', '.py', '.resx', '.settings', '.sh',
        '.shfbproj', '.sln', '.targets', '.tsv', '.txt', '.wxs', '.xsd', '.xml',
        '.yaml', '.yml'
    )
    foreach ($relative in $sourceFiles) {
        if ($protectedExact -contains $relative -or
            $relative.StartsWith('src/Fortunes/label-chunks/', [StringComparison]::Ordinal)) {
            continue
        }
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        if ($relative -in @('.gitignore', '.gitattributes') -or
            $textExtensions -contains $extension) {
            Assert-NoNul -Path (Get-ExistingRepoPath $relative)
        }
    }

    $powerShellFiles = @($sourceFiles | Where-Object { $_.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase) })
    foreach ($relative in $powerShellFiles) {
        $tokens = $null
        $parseErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile(
            (Get-ExistingRepoPath $relative),
            [ref]$tokens,
            [ref]$parseErrors)
        if (@($parseErrors).Count -gt 0) {
            $detail = (@($parseErrors) | ForEach-Object { $_.Message }) -join '; '
            throw "PowerShell parse failed for '$relative': $detail"
        }
    }

    $shellFiles = @($sourceFiles | Where-Object { $_.EndsWith('.sh', [StringComparison]::OrdinalIgnoreCase) })
    foreach ($relative in $shellFiles) {
        [void](Invoke-BoundedTool -Name "bash -n $relative" -FilePath $bash `
            -ArgumentList @('-n', $relative))
    }

    $xmlExtensions = @(
        '.config', '.csproj', '.props', '.resx', '.settings', '.shfbproj',
        '.targets', '.wxs', '.xsd', '.xml'
    )
    $xmlSettings = New-Object Xml.XmlReaderSettings
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    foreach ($relative in $sourceFiles) {
        if ($xmlExtensions -notcontains [IO.Path]::GetExtension($relative).ToLowerInvariant()) {
            continue
        }
        $reader = [Xml.XmlReader]::Create((Get-ExistingRepoPath $relative), $xmlSettings)
        try {
            while ($reader.Read()) { }
        }
        finally {
            $reader.Dispose()
        }
    }

    $jsonFiles = @($sourceFiles | Where-Object { $_.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) })
    foreach ($relative in $jsonFiles) {
        [void](Get-Content -LiteralPath (Get-ExistingRepoPath $relative) -Raw | ConvertFrom-Json)
    }
    $yamlFiles = @(
        $sourceFiles | Where-Object {
            $_.EndsWith('.yml', [StringComparison]::OrdinalIgnoreCase) -or
            $_.EndsWith('.yaml', [StringComparison]::OrdinalIgnoreCase)
        }
    )
    $externalUsesCount = 0
    foreach ($relative in $yamlFiles) {
        [void](Invoke-BoundedTool -Name "YAML parse $relative" -FilePath $yq `
            -ArgumentList @('eval', '.', $relative))
        $usesResult = Invoke-BoundedTool -Name "YAML uses extraction $relative" `
            -FilePath $yq `
            -ArgumentList @(
                'eval',
                '-o=json',
                '-I=0',
                '[..|.uses?]|map(select(.!=null))',
                $relative
            )
        $parsedUsesReferences = $usesResult.StdOut | ConvertFrom-Json
        $usesReferences = @(
            foreach ($parsedUsesReference in $parsedUsesReferences) {
                $parsedUsesReference
            }
        )
        foreach ($usesReferenceValue in $usesReferences) {
            if ($usesReferenceValue -isnot [string]) {
                throw "Workflow '$relative' contains a non-string uses reference."
            }
            $usesReference = [string]$usesReferenceValue
            if ($usesReference.StartsWith('./', [StringComparison]::Ordinal)) {
                if ($usesReference -notmatch '^\./[A-Za-z0-9._/-]+$' -or
                    $usesReference -match '(?:^|/)\.\.(?:/|$)') {
                    throw "Workflow '$relative' contains unsafe local uses reference '$usesReference'."
                }
                continue
            }

            $externalUsesCount++
            if ($usesReference.StartsWith('docker://', [StringComparison]::OrdinalIgnoreCase)) {
                if ($usesReference -cnotmatch '^docker://[^@\s]+@sha256:[0-9a-f]{64}$') {
                    throw "Workflow '$relative' must pin container uses reference '$usesReference' to a lowercase SHA-256 digest."
                }
                continue
            }
            if ($usesReference -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9._/-]+)?@[0-9a-fA-F]{40}$') {
                throw "Workflow '$relative' must pin external uses reference '$usesReference' to an exact 40-hex commit."
            }
        }
    }
    Write-Host "Workflow action pins verified: $externalUsesCount external uses references are immutable."

    $sourceAssetSpecPath = Get-ExistingRepoPath 'packaging/source-assets.json'
    $sourceAssetSpec = Get-Content -LiteralPath $sourceAssetSpecPath -Raw | ConvertFrom-Json
    if ([int]$sourceAssetSpec.schemaVersion -ne 1) {
        throw "Unsupported source-asset schema: $($sourceAssetSpec.schemaVersion)"
    }
    $requiredSourceAssets = [ordered]@{
        'src/Fortunes/fortunes.txt' = 'tsv-5'
        'src/Models/bge-small.onnx' = 'binary'
        'src/Models/bge-small.vocab.txt' = 'utf8-lines'
    }
    $observedSourceAssets = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    foreach ($asset in @($sourceAssetSpec.assets)) {
        $relative = [string]$asset.path
        if (-not $requiredSourceAssets.Contains($relative)) {
            throw "Unexpected pinned source asset: '$relative'."
        }
        if (-not $observedSourceAssets.Add($relative)) {
            throw "Duplicate pinned source asset: '$relative'."
        }
        if ([string]$asset.format -cne [string]$requiredSourceAssets[$relative]) {
            throw "Pinned source asset '$relative' has an unexpected format."
        }
        if ([long]$asset.bytes -le 0) {
            throw "Pinned source asset '$relative' has an invalid byte count."
        }
        $expectedHash = [string]$asset.sha256
        if ($expectedHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Pinned source asset '$relative' has an invalid SHA-256."
        }

        $assetPath = Get-ExistingRepoPath $relative
        $assetInfo = Get-Item -LiteralPath $assetPath
        if ($assetInfo.Length -ne [long]$asset.bytes) {
            throw "Pinned source asset '$relative' byte count changed. Expected $($asset.bytes); found $($assetInfo.Length)."
        }
        $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
        if ($actualHash -ine $expectedHash) {
            throw "Pinned source asset '$relative' SHA-256 changed. Expected $expectedHash; found $actualHash."
        }

        switch ([string]$asset.format) {
            'tsv-5' {
                if ([int]$asset.records -le 0) {
                    throw "Pinned TSV source asset '$relative' has an invalid record count."
                }
                $stats = Get-Utf8LineStats -Path $assetPath -ExpectedFieldCount 5
                if ($stats.Lines -ne [int]$asset.records) {
                    throw "Pinned TSV source asset '$relative' record count changed. Expected $($asset.records); found $($stats.Lines)."
                }
            }
            'utf8-lines' {
                if ([int]$asset.lines -le 0) {
                    throw "Pinned text source asset '$relative' has an invalid line count."
                }
                $stats = Get-Utf8LineStats -Path $assetPath
                if ($stats.Lines -ne [int]$asset.lines) {
                    throw "Pinned text source asset '$relative' line count changed. Expected $($asset.lines); found $($stats.Lines)."
                }
            }
            'binary' { }
            default {
                throw "Unsupported pinned source-asset format '$($asset.format)'."
            }
        }
    }
    $missingSourceAssets = @(
        $requiredSourceAssets.Keys |
            Where-Object { -not $observedSourceAssets.Contains([string]$_) }
    )
    if ($missingSourceAssets.Count -gt 0) {
        throw "Pinned source assets are missing from the inventory: $($missingSourceAssets -join ', ')"
    }

    & (Get-ExistingRepoPath 'tests/sbom-runtime-refresh-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/sbom-inventory-negative-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/release-gate-tracked-input-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/packaging-entrypoints-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/release-toolchain-and-upgrade-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/wix-toolchain-policy-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/syft-output-transaction-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/staging-path-safety-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/retained-staging-mutation-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/msi-lifecycle-artifact-selftest.ps1')
    & (Get-ExistingRepoPath 'tests/nuget-audit-policy-selftest.ps1')

    $embeddedCorpusValidator =
        Get-ExistingRepoPath 'packaging/Test-EmbeddedCorpus.ps1'
    & $embeddedCorpusValidator -SelfTest
    & $embeddedCorpusValidator `
        -CorpusPath (Get-ExistingRepoPath 'src/Fortunes/fortunes.txt') `
        -AllowKnownDuplicate:(
            $AllowDirtyDevelopment -or
            $AllowDocumentedReleaseBlockers)

    $sourceRightsValidator =
        Get-ExistingRepoPath 'packaging/Test-SourceRightsEvidence.ps1'
    $sourceRightsEvidence =
        Get-ExistingRepoPath 'packaging/source-rights-evidence.json'
    & $sourceRightsValidator -SelfTest
    & $sourceRightsValidator `
        -EvidencePath $sourceRightsEvidence `
        -RepositoryRoot $repoRoot `
        -AllowUntrackedDevelopment:$AllowDirtyDevelopment `
        -RequireReleaseApproval:(
            -not (
                $AllowDirtyDevelopment -or
                $AllowDocumentedReleaseBlockers))

    $packCatalogPath = Get-ExistingRepoPath 'packs/packs.json'
    $packCatalog = Get-Content -LiteralPath $packCatalogPath -Raw |
        ConvertFrom-Json
    foreach ($pack in @($packCatalog.packs)) {
        $packId = [string]$pack.id
        [void](Get-ExistingRepoPath "packs/$packId.txt")
    }
    $packCatalogValidator =
        Get-ExistingRepoPath 'packaging/Test-PackCatalog.ps1'
    & $packCatalogValidator -SelfTest
    & $packCatalogValidator `
        -CatalogPath $packCatalogPath `
        -RepositoryRoot $repoRoot `
        -TimeoutSeconds ([Math]::Min($TimeoutSeconds, 300))
    $packRightsValidator = Get-ExistingRepoPath 'packaging/Test-PackRightsEvidence.ps1'
    $packRightsEvidence = Get-ExistingRepoPath 'packaging/pack-rights-evidence.json'
    $packRightsEvidenceDocument =
        Get-Content -LiteralPath $packRightsEvidence -Raw |
            ConvertFrom-Json
    foreach ($approval in @($packRightsEvidenceDocument.approvals)) {
        [void](Get-ExistingRepoPath ([string]$approval.evidencePath))
    }
    & $packRightsValidator -SelfTest
    & $packRightsValidator `
        -CatalogPath $packCatalogPath `
        -EvidencePath $packRightsEvidence `
        -RepositoryRoot $repoRoot
    $projectPath = Get-ExistingRepoPath 'src/DesktopPet_Portable.csproj'
    $petTesterPath = Get-ExistingRepoPath 'Tools/PetTester/PetTester.csproj'
    $coreTestsPath = Get-ExistingRepoPath 'tests/DesktopPet.CoreTests/DesktopPet.CoreTests.csproj'
    $lockPaths = @(
        (Get-ExistingRepoPath 'src/packages.lock.json'),
        (Get-ExistingRepoPath 'Tools/PetTester/packages.lock.json'),
        (Get-ExistingRepoPath 'tests/DesktopPet.CoreTests/packages.lock.json')
    )
    $lockHashesBefore = @{}
    foreach ($path in $lockPaths) {
        $lockHashesBefore[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    # Full MSBuild is required for this old-style PackageReference project. The
    # dotnet restore front end drops its runtime-specific lock targets.
    $restoreProjects = @($projectPath, $petTesterPath, $coreTestsPath)
    foreach ($restoreProject in $restoreProjects) {
        $restoreArguments = @(
            $restoreProject,
            '-t:restore',
            '-p:Configuration=Release',
            '-p:Platform=x64',
            '-p:RestoreLockedMode=true',
            '-nologo',
            '-v:minimal'
        )
        [void](Invoke-BoundedTool -Name "locked restore $restoreProject" `
            -FilePath $msbuild -ArgumentList $restoreArguments)
    }

    $vulnerable = @()
    foreach ($candidateProject in @($projectPath, $petTesterPath, $coreTestsPath)) {
        $audit = Invoke-BoundedTool -Name "NuGet vulnerability audit $candidateProject" `
            -FilePath $dotnet `
            -ArgumentList @(
                'list', $candidateProject, 'package',
                '--vulnerable', '--include-transitive',
                '--format', 'json', '--output-version', '1', '--no-restore'
            )
        $inventory = Invoke-BoundedTool -Name "NuGet full package inventory $candidateProject" `
            -FilePath $dotnet `
            -ArgumentList @(
                'list', $candidateProject, 'package',
                '--include-transitive',
                '--format', 'json', '--output-version', '1', '--no-restore'
            )
        $completedAuditJson = Complete-NuGetVulnerabilityAuditJson `
            -VulnerabilityJson $audit.StdOut `
            -InventoryJson $inventory.StdOut `
            -ExpectedProjectPath $candidateProject `
            -ExpectedFramework 'net48'
        $vulnerable += @(
            Read-NuGetVulnerabilityAudit `
                -Json $completedAuditJson `
                -ExpectedProjectPath $candidateProject `
                -ExpectedFramework 'net48'
        )
    }
    if ($vulnerable.Count -gt 0) {
        $detail = ($vulnerable | ForEach-Object {
            "$($_.id) $($_.resolvedVersion)"
        } | Sort-Object -Unique) -join ', '
        throw "NuGet vulnerability audit found vulnerable packages: $detail"
    }
    foreach ($path in $lockPaths) {
        $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($after -ne $lockHashesBefore[$path]) {
            throw "A locked restore or audit changed the lock file: $path"
        }
    }

    $mainLock = Get-Content -LiteralPath $lockPaths[0] -Raw | ConvertFrom-Json
    $targetFramework = '.NETFramework,Version=v4.8'
    $targetProperty = $mainLock.dependencies.PSObject.Properties[$targetFramework]
    if ($null -eq $targetProperty) {
        throw "Main lock file lacks the canonical target '$targetFramework'."
    }
    $lockedTarget = $targetProperty.Value
    foreach ($property in $lockedTarget.PSObject.Properties) {
        if ([string]$property.Value.type -ne 'Project' -and
            [string]::IsNullOrWhiteSpace([string]$property.Value.contentHash)) {
            throw "Locked package '$($property.Name)' has no content hash."
        }
    }
    $expectedRids = @('win', 'win-arm64', 'win-x64', 'win-x86')
    foreach ($rid in $expectedRids) {
        if ($null -eq $mainLock.dependencies.PSObject.Properties["$targetFramework/$rid"]) {
            throw "Main lock file lacks runtime target '$targetFramework/$rid'."
        }
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $releasePropertyGroups = @(
        $projectXml.SelectNodes('//*[local-name()="PropertyGroup"]') |
            Where-Object {
                $condition = $_.Attributes['Condition']
                $null -ne $condition -and [string]$condition.Value -match
                    [regex]::Escape("'`$(Configuration)|`$(Platform)' == 'Release|x64'")
            }
    )
    if ($releasePropertyGroups.Count -ne 1) {
        throw "Expected exactly one Release|x64 property group; found $($releasePropertyGroups.Count)."
    }
    $releaseDebugType = $releasePropertyGroups[0].SelectSingleNode(
        './*[local-name()="DebugType"]')
    $releaseDebugSymbols = $releasePropertyGroups[0].SelectSingleNode(
        './*[local-name()="DebugSymbols"]')
    if ($null -eq $releaseDebugType -or
        [string]$releaseDebugType.InnerText -ine 'none' -or
        $null -eq $releaseDebugSymbols -or
        [string]$releaseDebugSymbols.InnerText -ine 'false') {
        throw 'Release x64 must omit debug symbols so the shipped PE cannot expose an absolute build path.'
    }
    $directNodes = @($projectXml.SelectNodes('//*[local-name()="PackageReference"]'))
    $directProjectPackages = @{}
    foreach ($node in $directNodes) {
        $directProjectPackages[[string]$node.Include] = [string]$node.Version
    }
    foreach ($property in $lockedTarget.PSObject.Properties) {
        if ([string]$property.Value.type -ne 'Direct') { continue }
        $declaredVersion = if ($directProjectPackages.ContainsKey(
                $property.Name)) {
            [string]$directProjectPackages[$property.Name]
        }
        else {
            ''
        }
        $normalizedDeclaredVersion = if (
            $declaredVersion -match '^\[([0-9A-Za-z.+-]+)\]$') {
            [string]$Matches[1]
        }
        else {
            $declaredVersion
        }
        if (-not $directProjectPackages.ContainsKey($property.Name) -or
            $normalizedDeclaredVersion -ne
                [string]$property.Value.resolved) {
            throw "Direct PackageReference and lock disagree for '$($property.Name)'."
        }
    }
    if (@($directProjectPackages.Keys).Count -ne
        @($lockedTarget.PSObject.Properties | Where-Object { [string]$_.Value.type -eq 'Direct' }).Count) {
        throw 'The direct PackageReference count differs from the canonical lock target.'
    }

    $inventoryPath = Get-ExistingRepoPath 'packaging/third-party-packages.json'
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ([int]$inventory.schemaVersion -ne 1 -or
        [string]$inventory.targetFramework -ne $targetFramework) {
        throw 'Unsupported or mismatched third-party package inventory schema.'
    }
    $lockedIdentities = @(
        $lockedTarget.PSObject.Properties |
            ForEach-Object { "$($_.Name)@$($_.Value.resolved)" } |
            Sort-Object
    )
    $inventoryIdentities = @(
        $inventory.packages |
            ForEach-Object { "$($_.name)@$($_.version)" } |
            Sort-Object
    )
    $inventoryDifference = @(Compare-Object $lockedIdentities $inventoryIdentities)
    if ($inventoryDifference.Count -gt 0) {
        $detail = ($inventoryDifference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join '; '
        throw "Third-party inventory and canonical lock disagree: $detail"
    }

    $manifestPath = Get-ExistingRepoPath 'packaging/runtime-files.txt'
    $manifest = @(
        Get-Content -LiteralPath $manifestPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    )
    if ($manifest.Count -eq 0 -or
        @($manifest | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
        throw 'Runtime manifest is empty or contains duplicate entries.'
    }
    foreach ($name in $manifest) {
        if (-not (Test-DesktopPetWindowsLeafName -Name $name)) {
            throw "Runtime manifest entry is not a plain file name: $name"
        }
    }
    $sortedManifest = @($manifest | Sort-Object)
    for ($index = 0; $index -lt $manifest.Count; $index++) {
        if ($manifest[$index] -cne $sortedManifest[$index]) {
            throw 'Runtime manifest entries must remain deterministically sorted.'
        }
    }

    $legalSpecPath = Get-ExistingRepoPath 'packaging/legal-files.json'
    $legalSpec = Get-Content -LiteralPath $legalSpecPath -Raw | ConvertFrom-Json
    if ([int]$legalSpec.schemaVersion -ne 1) {
        throw "Unsupported legal-file schema: $($legalSpec.schemaVersion)"
    }
    $legalOutputNames = @(
        $legalSpec.files | ForEach-Object { [string]$_.outputName }
    )
    if (@($legalOutputNames | Where-Object {
                -not (Test-DesktopPetWindowsLeafName -Name ([string]$_))
            }).Count -gt 0 -or
        @($legalOutputNames | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
        throw 'Legal-file inventory contains an invalid or duplicate output name.'
    }
    $referencedLegalOutputs = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($package in @($inventory.packages)) {
        $rootRelationship = Get-InventoryRootRelationship -Package $package
        $isBuildOnly = $rootRelationship -in @(
            'BUILD_TOOL_OF',
            'BUILD_DEPENDENCY_OF'
        )
        foreach ($runtimeFile in @($package.runtimeFiles)) {
            if (-not (Test-DesktopPetWindowsLeafName `
                    -Name ([string]$runtimeFile))) {
                throw (
                    "Package '$($package.name)' has an unsafe runtime file " +
                    "mapping: '$runtimeFile'.")
            }
        }
        if ($isBuildOnly -and @($package.runtimeFiles).Count -ne 0) {
            throw (
                "Build-only package '$($package.name)' cannot own runtime files."
            )
        }
        $licenseFile = Get-OptionalInventoryString `
            -Package $package `
            -Name 'licenseFile'
        if ([string]::IsNullOrWhiteSpace($licenseFile)) {
            if (-not $isBuildOnly) {
                throw "Runtime package '$($package.name)' has no retained license output."
            }
        }
        else {
            [void]$referencedLegalOutputs.Add($licenseFile)
        }
        $noticeProperty = $package.PSObject.Properties['noticeFiles']
        if ($null -ne $noticeProperty) {
            foreach ($name in @($noticeProperty.Value)) {
                if ([string]::IsNullOrWhiteSpace([string]$name)) {
                    throw "Package '$($package.name)' has an empty retained notice output."
                }
                [void]$referencedLegalOutputs.Add([string]$name)
            }
        }
    }
    $legalOutputDifference = @(
        Compare-Object @($referencedLegalOutputs | Sort-Object) @($legalOutputNames | Sort-Object)
    )
    if ($legalOutputDifference.Count -gt 0) {
        $detail = ($legalOutputDifference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join '; '
        throw "Package legal references and exact legal-file inventory disagree: $detail"
    }

    $expectedManifest = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        'bge-small.onnx', 'bge-small.vocab.txt', 'DesktopPet.exe',
        'DesktopPet.exe.config', 'LICENSE.txt', 'PRIVACY.md', 'PROVENANCE.md',
        'SUPPORT.md', 'THIRD_PARTY_NOTICES.md'
    )) {
        [void]$expectedManifest.Add($name)
    }
    foreach ($package in @($inventory.packages)) {
        foreach ($name in @($package.runtimeFiles)) { [void]$expectedManifest.Add([string]$name) }
        $licenseFile = Get-OptionalInventoryString `
            -Package $package `
            -Name 'licenseFile'
        if (-not [string]::IsNullOrWhiteSpace($licenseFile)) {
            [void]$expectedManifest.Add($licenseFile)
        }
        $noticeProperty = $package.PSObject.Properties['noticeFiles']
        if ($null -ne $noticeProperty) {
            foreach ($name in @($noticeProperty.Value)) { [void]$expectedManifest.Add([string]$name) }
        }
    }
    foreach ($legalFile in @($legalSpec.files)) {
        [void]$expectedManifest.Add([string]$legalFile.outputName)
    }
    $manifestDifference = @(Compare-Object @($expectedManifest | Sort-Object) @($manifest | Sort-Object))
    if ($manifestDifference.Count -gt 0) {
        $detail = ($manifestDifference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join '; '
        throw "Runtime manifest and legal/package inventory disagree: $detail"
    }

    $noticeText = Get-Content -LiteralPath (Get-ExistingRepoPath 'THIRD_PARTY_NOTICES.md') -Raw
    foreach ($package in @($inventory.packages)) {
        $rowPattern = '(?m)^\|\s*' + [regex]::Escape([string]$package.name) +
            '\s*\|\s*' + [regex]::Escape([string]$package.version) + '\s*\|'
        if ($noticeText -notmatch $rowPattern) {
            throw "THIRD_PARTY_NOTICES.md lacks $($package.name) $($package.version)."
        }
    }

    $nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    } else {
        Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
    }
    $lockedPackageIdentities = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($identity in $inventoryIdentities) {
        [void]$lockedPackageIdentities.Add([string]$identity)
    }

    foreach ($package in @($inventory.packages)) {
        $packageIdentity = "$($package.name)@$($package.version)"
        $licenseFile = Get-OptionalInventoryString `
            -Package $package `
            -Name 'licenseFile'
        $packageRoot = [IO.Path]::GetFullPath((Join-Path $nugetRoot (
            ([string]$package.name).ToLowerInvariant() + '\' +
            [string]$package.version))).TrimEnd('\')
        $resolvedNuGetRoot = [IO.Path]::GetFullPath($nugetRoot).TrimEnd('\')
        if (-not $packageRoot.StartsWith(
                $resolvedNuGetRoot + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet package root escaped the configured cache: $packageIdentity"
        }
        $nuspecs = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File)
        if ($nuspecs.Count -ne 1) {
            throw "Expected exactly one nuspec for locked package '$packageIdentity'; found $($nuspecs.Count)."
        }

        $nuspecReader = [Xml.XmlReader]::Create($nuspecs[0].FullName, $xmlSettings)
        $nuspecXml = New-Object Xml.XmlDocument
        $nuspecXml.XmlResolver = $null
        try {
            $nuspecXml.Load($nuspecReader)
        }
        finally {
            $nuspecReader.Dispose()
        }
        $licenseNode = $nuspecXml.SelectSingleNode(
            '/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
        if ($null -eq $licenseNode) {
            $rootRelationship = Get-InventoryRootRelationship -Package $package
            $isBuildOnly = $rootRelationship -in @(
                'BUILD_TOOL_OF',
                'BUILD_DEPENDENCY_OF'
            )
            $licenseEvidenceProperty =
                $package.PSObject.Properties['licenseEvidence']
            $licenseUrlNode = $nuspecXml.SelectSingleNode(
                '/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="licenseUrl"]')
            if (-not $isBuildOnly -or
                $null -eq $licenseEvidenceProperty -or
                [string]$licenseEvidenceProperty.Value.type -cne
                    'nuspec-license-url' -or
                $null -eq $licenseUrlNode -or
                [string]::IsNullOrWhiteSpace(
                    [string]$licenseEvidenceProperty.Value.value) -or
                [string]$licenseUrlNode.InnerText -cne
                    [string]$licenseEvidenceProperty.Value.value) {
                throw (
                    "Locked package '$packageIdentity' has no modern nuspec " +
                    'license declaration or exact approved legacy ' +
                    'nuspec-license-url evidence.'
                )
            }
            if ([string]$package.license -cne 'MIT') {
                throw (
                    "Legacy license evidence for '$packageIdentity' is " +
                    "approved only for the inventoried MIT license."
                )
            }
            continue
        }
        $declaredLicenseType = ([string]$licenseNode.Attributes['type'].Value).ToLowerInvariant()
        $declaredLicense = ([string]$licenseNode.InnerText).Trim()
        if ($declaredLicenseType -eq 'expression') {
            if ($declaredLicense -cne [string]$package.license) {
                throw (
                    "Locked package '$packageIdentity' declares license expression " +
                    "'$declaredLicense', not inventory value '$($package.license)'.")
            }
        }
        elseif ($declaredLicenseType -eq 'file') {
            [void](Get-NuGetLegalSourcePath `
                -NuGetRoot $nugetRoot `
                -Package ([string]$package.name) `
                -Version ([string]$package.version) `
                -RelativePath $declaredLicense)
            $matchingLegalSources = @(
                $legalSpec.files | Where-Object {
                    if ([string]$_.outputName -ine $licenseFile -or
                        [string]$_.sourceKind -ine 'nuget') {
                        return $false
                    }
                    if ("$($_.package)@$($_.version)" -ieq $packageIdentity -and
                        [string]$_.sourcePath -ieq $declaredLicense) {
                        return $true
                    }
                    $equivalentProperty = $_.PSObject.Properties['equivalentSources']
                    if ($null -eq $equivalentProperty) { return $false }
                    return @(
                        $equivalentProperty.Value | Where-Object {
                            "$($_.package)@$($_.version)" -ieq $packageIdentity -and
                            [string]$_.sourcePath -ieq $declaredLicense
                        }
                    ).Count -gt 0
                }
            )
            if ($matchingLegalSources.Count -ne 1) {
                throw (
                    "Locked package '$packageIdentity' file license '$declaredLicense' " +
                    "is not mapped exactly once to '$licenseFile'.")
            }
        }
        else {
            throw "Locked package '$packageIdentity' uses unsupported nuspec license type '$declaredLicenseType'."
        }
    }

    $legalSources = @{}
    foreach ($legalFile in @($legalSpec.files)) {
        $expectedLegalHash = [string]$legalFile.sha256
        if ($expectedLegalHash -cnotmatch '^[0-9A-F]{64}$') {
            throw "Legal source '$($legalFile.outputName)' has an invalid uppercase SHA-256."
        }
        if ([string]$legalFile.sourceKind -eq 'repository') {
            $sourcePath = Get-ExistingRepoPath ([string]$legalFile.sourcePath)
        }
        elseif ([string]$legalFile.sourceKind -eq 'nuget') {
            $identity = "$($legalFile.package)@$($legalFile.version)"
            if (-not $lockedPackageIdentities.Contains($identity)) {
                throw "NuGet legal source is not a locked package identity: $identity"
            }
            $sourcePath = Get-NuGetLegalSourcePath `
                -NuGetRoot $nugetRoot `
                -Package ([string]$legalFile.package) `
                -Version ([string]$legalFile.version) `
                -RelativePath ([string]$legalFile.sourcePath)
        }
        else {
            throw "Unsupported legal source kind '$($legalFile.sourceKind)'."
        }
        $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        if ($hash -cne $expectedLegalHash) {
            throw "Legal source hash mismatch for '$($legalFile.outputName)'."
        }

        $equivalentProperty = $legalFile.PSObject.Properties['equivalentSources']
        if ($null -ne $equivalentProperty) {
            foreach ($equivalent in @($equivalentProperty.Value)) {
                $equivalentIdentity = "$($equivalent.package)@$($equivalent.version)"
                if (-not $lockedPackageIdentities.Contains($equivalentIdentity)) {
                    throw "Equivalent legal source is not a locked package identity: $equivalentIdentity"
                }
                $equivalentPath = Get-NuGetLegalSourcePath `
                    -NuGetRoot $nugetRoot `
                    -Package ([string]$equivalent.package) `
                    -Version ([string]$equivalent.version) `
                    -RelativePath ([string]$equivalent.sourcePath)
                $equivalentHash = (
                    Get-FileHash -LiteralPath $equivalentPath -Algorithm SHA256).Hash
                if ($equivalentHash -cne $expectedLegalHash) {
                    throw (
                        "Equivalent legal source '$equivalentIdentity' is not byte-identical " +
                        "to '$($legalFile.outputName)'.")
                }
            }
        }
        $legalSources[[string]$legalFile.outputName] = $sourcePath
    }

    $releaseOutput = Join-Path $repoRoot 'build\DesktopPetPortable\bin\Release\x64'
    if (Test-Path -LiteralPath $releaseOutput -PathType Container) {
        foreach ($entry in $legalSources.GetEnumerator()) {
            $outputPath = Join-Path $releaseOutput $entry.Key
            if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
                throw "Built runtime lacks retained legal file '$($entry.Key)'."
            }
            if ((Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash) {
                throw "Built runtime did not retain '$($entry.Key)' byte-for-byte."
            }
        }
    }

    [xml]$productProps = Get-Content -LiteralPath (Get-ExistingRepoPath 'ProductVersion.props') -Raw
    $productVersion = [string]$productProps.Project.PropertyGroup.DesktopPetVersion
    if ($productVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "DesktopPetVersion is not a three-part numeric version: '$productVersion'."
    }
    $versionParts = @($productVersion.Split('.') | ForEach-Object { [int]$_ })
    if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
        throw "DesktopPetVersion exceeds MSI's 255.255.65535 limit: '$productVersion'."
    }

    if (-not [string]::IsNullOrWhiteSpace($SbomPath)) {
        $resolvedSbom = if ([IO.Path]::IsPathRooted($SbomPath)) {
            [IO.Path]::GetFullPath($SbomPath)
        } else {
            [IO.Path]::GetFullPath((Join-Path $repoRoot $SbomPath))
        }
        & (Get-ExistingRepoPath 'packaging/Test-SbomInventory.ps1') `
            -SbomPath $resolvedSbom `
            -InventoryPath $inventoryPath
        if ($LASTEXITCODE -ne 0) { throw 'SPDX inventory validation failed.' }
    }

    $toolVersions = [ordered]@{
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        powershell = [string]$PSVersionTable.PSVersion
        gitPath = $git
        gitVersion = ((Invoke-BoundedTool -Name 'git version' -FilePath $git -ArgumentList @('--version')).StdOut).Trim()
        dotnetPath = $dotnet
        dotnetVersion = ((Invoke-BoundedTool -Name 'dotnet version' -FilePath $dotnet -ArgumentList @('--version')).StdOut).Trim()
        msbuildPath = $msbuild
        msbuildVersion = ((Invoke-BoundedTool -Name 'MSBuild version' -FilePath $msbuild -ArgumentList @('-version', '-nologo')).StdOut).Trim()
        bashPath = $bash
        bashVersion = (((Invoke-BoundedTool -Name 'bash version' -FilePath $bash -ArgumentList @('--version')).StdOut -split '\r?\n')[0]).Trim()
        yqPath = $yq
        yqVersion = $yqVersion
    }
    $recordRoot = Join-Path $repoRoot 'build\release-gate'
    New-Item -ItemType Directory -Path $recordRoot -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $recordRoot 'tool-versions.json'),
        (($toolVersions | ConvertTo-Json -Depth 4) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))

    Write-Host (
        "Release gate passed: {0} PowerShell, {1} shell, {2} XML, {3} JSON, {4} YAML; {5} locked packages; {6} runtime files." -f
        $powerShellFiles.Count,
        $shellFiles.Count,
        @($sourceFiles | Where-Object { $xmlExtensions -contains [IO.Path]::GetExtension($_).ToLowerInvariant() }).Count,
        $jsonFiles.Count,
        $yamlFiles.Count,
        $lockedIdentities.Count,
        $manifest.Count
    ) -ForegroundColor Green
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-ReleaseGate-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
