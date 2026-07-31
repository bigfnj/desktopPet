#requires -Version 5

Set-StrictMode -Version Latest

function Test-ReleaseGateCiEnvironment {
    [CmdletBinding()]
    param()

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -and
        $env:GITHUB_ACTIONS -ine 'false') {
        return $true
    }
    if (-not [string]::IsNullOrWhiteSpace($env:CI) -and
        $env:CI -ine 'false') {
        return $true
    }
    return $false
}

function ConvertTo-ReleaseGateProcessArgument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Argument
    )

    if ($Argument.Length -ne 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    # Quote according to the CommandLineToArgvW/VC runtime rules used by the
    # Windows command-line parsers of Git, PowerShell, MSBuild, and the other
    # release-gate tools.
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashCount++
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $backslashCount) + 1)
            [void]$builder.Append([char]34)
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append([char]92, $backslashCount)
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$builder.Append([char]92, 2 * $backslashCount)
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Set-ReleaseGateProcessArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.ProcessStartInfo]$StartInfo,
        [string[]]$ArgumentList = @()
    )

    $argumentListProperty =
        $StartInfo.PSObject.Properties['ArgumentList']
    if ($null -ne $argumentListProperty) {
        foreach ($argument in $ArgumentList) {
            [void]$StartInfo.ArgumentList.Add([string]$argument)
        }
        return
    }

    $StartInfo.Arguments = (
        @($ArgumentList | ForEach-Object {
            ConvertTo-ReleaseGateProcessArgument -Argument ([string]$_)
        }) -join ' '
    )
}

function Invoke-ReleaseGateGit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$GitPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [int[]]$AllowedExitCodes = @(0)
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $GitPath -C $RepositoryRoot @ArgumentList 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw (
            "git $($ArgumentList -join ' ') failed with exit code " +
            "$exitCode`: $($output -join [Environment]::NewLine)")
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-ReleaseGateGitPathList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$GitPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $GitPath
    Set-ReleaseGateProcessArguments `
        -StartInfo $startInfo `
        -ArgumentList (@('-C', $RepositoryRoot) + $ArgumentList + @('-z'))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $startInfo.StandardOutputEncoding = $strictUtf8
    $startInfo.StandardErrorEncoding = $strictUtf8

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'git path enumeration could not be started.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw (
                "git $($ArgumentList -join ' ') -z failed with exit code " +
                "$($process.ExitCode): $stderr")
        }
    }
    finally {
        $process.Dispose()
    }

    $paths = @()
    if ($stdout.Length -ne 0) {
        if ($stdout[$stdout.Length - 1] -ne [char]0) {
            throw 'git path enumeration returned a non-NUL-terminated result.'
        }
        $paths = @(
            $stdout.Substring(0, $stdout.Length - 1).Split([char]0)
        )
        if (@($paths | Where-Object {
                    [string]::IsNullOrEmpty([string]$_)
                }).Count -ne 0) {
            throw 'git path enumeration returned an empty path entry.'
        }
    }
    return [pscustomobject]@{
        ExitCode = 0
        Output = [string[]]$paths
    }
}

function New-ReleaseGatePathPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$GitPath,
        [switch]$AllowDirtyDevelopment
    )

    $resolvedRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Release-gate repository root is missing: $resolvedRoot"
    }
    if ($AllowDirtyDevelopment -and (Test-ReleaseGateCiEnvironment)) {
        throw (
            'AllowDirtyDevelopment is a local-only diagnostic override and ' +
            'is disabled in GitHub Actions and CI.'
        )
    }

    $trackedResult = Invoke-ReleaseGateGitPathList `
        -GitPath $GitPath `
        -RepositoryRoot $resolvedRoot `
        -ArgumentList @('ls-files', '--cached')
    $trackedFiles = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    foreach ($entry in @($trackedResult.Output)) {
        $relative = ([string]$entry).Replace('\', '/')
        if (-not [string]::IsNullOrWhiteSpace($relative)) {
            [void]$trackedFiles.Add($relative)
        }
    }

    $ignoredTrackedResult = Invoke-ReleaseGateGitPathList `
        -GitPath $GitPath `
        -RepositoryRoot $resolvedRoot `
        -ArgumentList @(
            'ls-files',
            '--cached',
            '--ignored',
            '--exclude-standard'
        )
    $ignoredTrackedFiles =
        New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
    foreach ($entry in @($ignoredTrackedResult.Output)) {
        $relative = ([string]$entry).Replace('\', '/')
        if (-not [string]::IsNullOrWhiteSpace($relative)) {
            [void]$ignoredTrackedFiles.Add($relative)
        }
    }

    $statusResult = Invoke-ReleaseGateGit `
        -GitPath $GitPath `
        -RepositoryRoot $resolvedRoot `
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
    if ($dirtyEntries.Count -gt 0 -and -not $AllowDirtyDevelopment) {
        $detail = @($dirtyEntries | Select-Object -First 20) -join '; '
        if ($dirtyEntries.Count -gt 20) {
            $detail += "; ... ($($dirtyEntries.Count - 20) more)"
        }
        throw (
            'Release source is dirty or contains untracked files. Commit the ' +
            "exact release snapshot before running the gate: $detail"
        )
    }
    if ($dirtyEntries.Count -gt 0) {
        Write-Warning (
            'LOCAL DEVELOPMENT ONLY: dirty/untracked source is being checked, ' +
            'but this run is not release evidence.'
        )
    }

    return [pscustomobject]@{
        RepositoryRoot = $resolvedRoot
        GitPath = [IO.Path]::GetFullPath($GitPath)
        AllowDirtyDevelopment = [bool]$AllowDirtyDevelopment
        TrackedFiles = $trackedFiles
        IgnoredTrackedFiles = $ignoredTrackedFiles
        DirtyEntries = $dirtyEntries
    }
}

function ConvertTo-ReleaseGateRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Policy,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\')) {
        throw "Repository input is not a canonical relative path: '$RelativePath'."
    }
    $segments = @($RelativePath.Split('/'))
    if ($segments.Count -eq 0 -or
        @($segments | Where-Object {
                [string]::IsNullOrWhiteSpace($_) -or
                $_ -eq '.' -or
                $_ -eq '..'
            }).Count -gt 0) {
        throw "Repository input is not a canonical relative path: '$RelativePath'."
    }

    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $Policy.RepositoryRoot $RelativePath))
    if (-not $candidate.StartsWith(
            $Policy.RepositoryRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Repository input escaped the root: '$RelativePath'."
    }
    return $RelativePath
}

function Assert-ReleaseGateExactDiskCase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Policy,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $current = $Policy.RepositoryRoot
    foreach ($segment in $RelativePath.Split('/')) {
        $matches = @(
            Get-ChildItem -LiteralPath $current -Force |
                Where-Object { $_.Name -ceq $segment }
        )
        if ($matches.Count -ne 1) {
            throw (
                "Repository input does not exist with exact on-disk case: " +
                "'$RelativePath'."
            )
        }
        if (($matches[0].Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw (
                "Repository input traverses a reparse point: " +
                "'$RelativePath'."
            )
        }
        $current = $matches[0].FullName
    }
    if (-not (Test-Path -LiteralPath $current -PathType Leaf)) {
        throw "Required repository input is not a file: '$RelativePath'."
    }
    return [IO.Path]::GetFullPath($current)
}

function Get-ReleaseGateRepoPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Policy,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $relative = ConvertTo-ReleaseGateRelativePath `
        -Policy $Policy `
        -RelativePath $RelativePath
    $candidate = Assert-ReleaseGateExactDiskCase `
        -Policy $Policy `
        -RelativePath $relative

    $ignoreResult = Invoke-ReleaseGateGit `
        -GitPath $Policy.GitPath `
        -RepositoryRoot $Policy.RepositoryRoot `
        -ArgumentList @(
            'check-ignore',
            '--no-index',
            '--quiet',
            '--',
            $relative
        ) `
        -AllowedExitCodes @(0, 1)
    if ($ignoreResult.ExitCode -eq 0 -or
        $Policy.IgnoredTrackedFiles.Contains($relative)) {
        throw "Required repository input is ignored: '$relative'."
    }

    if (-not $Policy.TrackedFiles.Contains($relative) -and
        -not $Policy.AllowDirtyDevelopment) {
        throw (
            'Required repository input is not tracked with exact case: ' +
            "'$relative'."
        )
    }
    return $candidate
}

function Get-ReleaseGateSourceFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Policy)

    $candidates = if ($Policy.AllowDirtyDevelopment) {
        (
            Invoke-ReleaseGateGitPathList `
                -GitPath $Policy.GitPath `
                -RepositoryRoot $Policy.RepositoryRoot `
                -ArgumentList @(
                    'ls-files',
                    '--cached',
                    '--others',
                    '--exclude-standard'
                )
        ).Output
    }
    else {
        @($Policy.TrackedFiles)
    }

    $sourceFiles = @(
        $candidates |
            ForEach-Object { ([string]$_).Replace('\', '/') } |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                (Test-Path -LiteralPath (
                    Join-Path $Policy.RepositoryRoot $_) -PathType Leaf)
            } |
            Sort-Object -Unique
    )
    foreach ($relative in $sourceFiles) {
        [void](Get-ReleaseGateRepoPath `
            -Policy $Policy `
            -RelativePath $relative)
    }
    return $sourceFiles
}

function Assert-ReleaseGateWhitespaceClean {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Policy)

    foreach ($arguments in @(
            [string[]]@('diff', '--check'),
            [string[]]@('diff', '--cached', '--check')
        )) {
        [void](Invoke-ReleaseGateGit `
            -GitPath $Policy.GitPath `
            -RepositoryRoot $Policy.RepositoryRoot `
            -ArgumentList $arguments)
    }
}
