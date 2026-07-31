#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$pathPolicyScript =
    Join-Path $repoRoot 'packaging\ReleaseGate.PathPolicy.ps1'
. $pathPolicyScript
$releaseGateSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Invoke-ReleaseGate.ps1') -Raw
$productSelfTestsSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packaging\Invoke-ProductSelfTests.ps1') -Raw
foreach ($boundedProcessSource in @(
        [pscustomobject]@{
            Name = 'release gate'
            Source = $releaseGateSource
        },
        [pscustomobject]@{
            Name = 'product self-tests'
            Source = $productSelfTestsSource
        })) {
    if (-not $boundedProcessSource.Source.Contains(
            'Set-ReleaseGateProcessArguments') -or
        $boundedProcessSource.Source -match
            '\.Arguments\s*=\s*\$ArgumentList\s*-join') {
        throw (
            "$($boundedProcessSource.Name) does not use the shared exact-argv " +
            'ProcessStartInfo policy.')
    }
}
$git = (Get-Command git.exe -ErrorAction Stop).Source
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-ReleaseGatePolicy-' + [Guid]::NewGuid().ToString('N'))
$outsideScratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-ReleaseGateOutside-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$negativeControlCount = 0

function Write-Utf8 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, $utf8)
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $accepted = $true
    $message = ''
    try {
        & $Action
    }
    catch {
        $accepted = $false
        $message = $_.Exception.Message
    }
    if ($accepted) {
        throw "Release-gate path-policy negative control was accepted: $Name"
    }
    if ($message -notmatch $ExpectedMessage) {
        throw (
            "Release-gate path-policy negative control '$Name' failed for an " +
            "unexpected reason: $message"
        )
    }
    $script:negativeControlCount++
}

try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('init', '--quiet'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('config', 'user.name', 'DesktopPet self-test'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @(
            'config',
            'user.email',
            'desktop-pet-self-test@example.invalid'
        ))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('config', 'core.quotePath', 'true'))

    Write-Utf8 `
        -Path (Join-Path $scratch '.gitignore') `
        -Value "critical/ignored.json`n"
    Write-Utf8 `
        -Path (Join-Path $scratch 'critical\Tracked.json') `
        -Value "{`"tracked`":true}`n"
    Write-Utf8 `
        -Path (Join-Path $scratch 'nested.json') `
        -Value "{`"path`":`"critical/untracked.json`"}`n"
    $unicodeRelative = 'critical/caf' + [char]0x00E9 + '.ps1'
    Write-Utf8 `
        -Path (Join-Path $scratch $unicodeRelative) `
        -Value "Write-Output 'unicode tracked source'`n"
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('add', '--all'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('commit', '--quiet', '-m', 'self-test baseline'))

    $strictPolicy = New-ReleaseGatePathPolicy `
        -RepositoryRoot $scratch `
        -GitPath $git
    if (-not $strictPolicy.TrackedFiles.Contains($unicodeRelative) -or
        @(Get-ReleaseGateSourceFiles -Policy $strictPolicy) -cnotcontains
            $unicodeRelative) {
        throw (
            'NUL-delimited Git enumeration did not preserve the exact tracked ' +
            "Unicode path '$unicodeRelative'.")
    }
    [void](Get-ReleaseGateRepoPath `
        -Policy $strictPolicy `
        -RelativePath 'critical/Tracked.json')

    $argumentFixtureRoot = Join-Path $scratch 'argument fixture'
    New-Item -ItemType Directory -Path $argumentFixtureRoot -Force | Out-Null
    $argumentProbe = Join-Path $argumentFixtureRoot 'argv probe.ps1'
    $argumentOutput = Join-Path $argumentFixtureRoot 'captured arguments.txt'
    Write-Utf8 -Path $argumentProbe -Value @'
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Captured
)
[IO.File]::WriteAllLines(
    $OutputPath,
    $Captured,
    (New-Object Text.UTF8Encoding($false)))
'@
    $expectedArguments = @(
        'value with spaces',
        'quote"inside',
        'trailing\'
    )
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $PSHOME 'powershell.exe'
    Set-ReleaseGateProcessArguments `
        -StartInfo $startInfo `
        -ArgumentList (
            @(
                '-NoProfile',
                '-NonInteractive',
                '-ExecutionPolicy',
                'Bypass',
                '-File',
                $argumentProbe,
                $argumentOutput
            ) + $expectedArguments)
    $startInfo.WorkingDirectory = $argumentFixtureRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Process-argument boundary fixture could not be started.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw (
                'Process-argument boundary fixture failed with exit code ' +
                "$($process.ExitCode). STDOUT: $stdout STDERR: $stderr")
        }
    }
    finally {
        $process.Dispose()
    }
    $capturedArguments = @(
        Get-Content -LiteralPath $argumentOutput
    )
    if (@(Compare-Object `
            -ReferenceObject $expectedArguments `
            -DifferenceObject $capturedArguments `
            -SyncWindow 0).Count -ne 0) {
        throw (
            'ProcessStartInfo argument setup did not preserve spaces, quotes, ' +
            'and trailing backslashes as exact argv boundaries.')
    }
    [IO.Directory]::Delete($argumentFixtureRoot, $true)

    Assert-Rejected 'exact-case-mismatch' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath 'critical/tracked.json')
    } 'exact on-disk case|tracked with exact case'

    $untrackedPath = Join-Path $scratch 'critical\untracked.json'
    Write-Utf8 -Path $untrackedPath -Value "{}"
    Assert-Rejected 'direct-untracked-input' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath 'critical/untracked.json')
    } 'not tracked with exact case'

    $ignoredPath = Join-Path $scratch 'critical\ignored.json'
    Write-Utf8 -Path $ignoredPath -Value "{}"
    Assert-Rejected 'direct-ignored-input' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath 'critical/ignored.json')
    } 'is ignored'

    $nested = Get-Content -LiteralPath (
        Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath 'nested.json') -Raw |
        ConvertFrom-Json
    Assert-Rejected 'nested-untracked-input' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath ([string]$nested.path))
    } 'not tracked with exact case'
    $nested.path = 'critical/ignored.json'
    Assert-Rejected 'nested-ignored-input' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath ([string]$nested.path))
    } 'is ignored'

    New-Item -ItemType Directory -Path $outsideScratch -Force | Out-Null
    Write-Utf8 `
        -Path (Join-Path $outsideScratch 'outside.json') `
        -Value "{}"
    $junctionPath = Join-Path $scratch 'critical\linked'
    New-Item `
        -ItemType Junction `
        -Path $junctionPath `
        -Target $outsideScratch | Out-Null
    Assert-Rejected 'reparse-point-input' {
        [void](Get-ReleaseGateRepoPath `
            -Policy $strictPolicy `
            -RelativePath 'critical/linked/outside.json')
    } 'traverses a reparse point'
    [IO.Directory]::Delete($junctionPath)

    [IO.File]::Delete($untrackedPath)
    [IO.File]::Delete($ignoredPath)
    Write-Utf8 `
        -Path (Join-Path $scratch 'critical\Tracked.json') `
        -Value "{`"tracked`":false}`n"
    Assert-Rejected 'dirty-tracked-release-source' {
        [void](New-ReleaseGatePathPolicy `
            -RepositoryRoot $scratch `
            -GitPath $git)
    } 'Release source is dirty'
    Write-Utf8 `
        -Path (Join-Path $scratch 'critical\Tracked.json') `
        -Value "{`"tracked`":true}`n"

    Write-Utf8 -Path $untrackedPath -Value "{}"
    Assert-Rejected 'untracked-release-source' {
        [void](New-ReleaseGatePathPolicy `
            -RepositoryRoot $scratch `
            -GitPath $git)
    } 'Release source is dirty'
    [IO.File]::Delete($untrackedPath)

    Write-Utf8 `
        -Path (Join-Path $scratch 'critical\Tracked.json') `
        -Value "{`"tracked`":true}   `n"
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $scratch `
        -ArgumentList @('add', 'critical/Tracked.json'))
    $developmentPolicy = New-ReleaseGatePathPolicy `
        -RepositoryRoot $scratch `
        -GitPath $git `
        -AllowDirtyDevelopment
    Assert-Rejected 'staged-whitespace-error' {
        Assert-ReleaseGateWhitespaceClean -Policy $developmentPolicy
    } 'git diff --cached --check failed'

    $originalCi = $env:CI
    try {
        $env:CI = 'true'
        Assert-Rejected 'ci-development-override' {
            [void](New-ReleaseGatePathPolicy `
                -RepositoryRoot $scratch `
                -GitPath $git `
                -AllowDirtyDevelopment)
        } 'disabled in GitHub Actions and CI'
    }
    finally {
        $env:CI = $originalCi
    }

    $bootstrapRoot = Join-Path $scratch 'bootstrap'
    New-Item -ItemType Directory -Path (
        Join-Path $bootstrapRoot 'packaging') -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'packaging\Invoke-ReleaseGate.ps1') `
        -Destination (
            Join-Path $bootstrapRoot 'packaging\Invoke-ReleaseGate.ps1')
    Copy-Item `
        -LiteralPath $pathPolicyScript `
        -Destination (
            Join-Path $bootstrapRoot 'packaging\ReleaseGate.PathPolicy.ps1')
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $bootstrapRoot `
        -ArgumentList @('init', '--quiet'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $bootstrapRoot `
        -ArgumentList @('config', 'user.name', 'DesktopPet self-test'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $bootstrapRoot `
        -ArgumentList @(
            'config',
            'user.email',
            'desktop-pet-self-test@example.invalid'
        ))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $bootstrapRoot `
        -ArgumentList @('add', '--all'))
    [void](Invoke-ReleaseGateGit `
        -GitPath $git `
        -RepositoryRoot $bootstrapRoot `
        -ArgumentList @('commit', '--quiet', '-m', 'bootstrap baseline'))
    $markerPath = Join-Path $bootstrapRoot 'helper-executed.marker'
    $escapedMarkerPath = $markerPath.Replace("'", "''")
    [IO.File]::AppendAllText(
        (Join-Path $bootstrapRoot (
            'packaging\ReleaseGate.PathPolicy.ps1')),
        (
            "`n[IO.File]::WriteAllText('$escapedMarkerPath'," +
            " 'executed')`n"
        ),
        $utf8)
    Assert-Rejected 'modified-bootstrap-helper' {
        & (Join-Path $bootstrapRoot (
            'packaging\Invoke-ReleaseGate.ps1')) *> $null
    } 'Release source is dirty'
    if (Test-Path -LiteralPath $markerPath) {
        throw 'Modified release-gate bootstrap helper executed before validation.'
    }

    if ($negativeControlCount -ne 11) {
        throw (
            'Release-gate path-policy self-test control count changed: ' +
            "$negativeControlCount."
        )
    }
    Write-Host (
        'PASS: release-gate Unicode path enumeration, exact process argv ' +
        'boundaries, and 11 fail-closed path-policy negative controls.'
    ) -ForegroundColor Green
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $resolvedTemp = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-ReleaseGatePolicy-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
    if ((Test-Path -LiteralPath $outsideScratch -PathType Container) -and
        [IO.Path]::GetFullPath($outsideScratch).StartsWith(
            $resolvedTemp + '\DesktopPet-ReleaseGateOutside-',
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $outsideScratch -Recurse -Force
    }
}
