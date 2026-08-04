#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
$builderPath = Join-Path $repoRoot 'src\Fortunes\build-corpus.sh'
$builder = Get-Content -LiteralPath $builderPath -Raw
$git = (Get-Command git -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source   # several git.exe on CI PATH -> take the effective one
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-CorpusProvenance-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$originalNoReplace = [Environment]::GetEnvironmentVariable(
    'GIT_NO_REPLACE_OBJECTS',
    [EnvironmentVariableTarget]::Process)

function Invoke-FixtureGit {
    param(
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [switch]$NoReplaceObjects
    )

    $saved = [Environment]::GetEnvironmentVariable(
        'GIT_NO_REPLACE_OBJECTS',
        [EnvironmentVariableTarget]::Process)
    $savedErrorAction = $ErrorActionPreference
    try {
        if ($NoReplaceObjects) {
            $env:GIT_NO_REPLACE_OBJECTS = '1'
        }
        else {
            [Environment]::SetEnvironmentVariable(
                'GIT_NO_REPLACE_OBJECTS',
                $null,
                [EnvironmentVariableTarget]::Process)
        }
        # Windows PowerShell promotes native stderr to ErrorRecord objects. Keep
        # expected Git diagnostics capturable and decide success from the exit code.
        $ErrorActionPreference = 'Continue'
        $effectiveArguments = @()
        if ($NoReplaceObjects) {
            $effectiveArguments += '--no-replace-objects'
        }
        $effectiveArguments += @('-C', $scratch)
        $effectiveArguments += $ArgumentList
        $output = @(& $git @effectiveArguments 2>&1)
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw (
                "Disposable Git fixture failed (exit $exitCode): " +
                ($output -join [Environment]::NewLine))
        }
        return $output
    }
    finally {
        $ErrorActionPreference = $savedErrorAction
        [Environment]::SetEnvironmentVariable(
            'GIT_NO_REPLACE_OBJECTS',
            $saved,
            [EnvironmentVariableTarget]::Process)
    }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    [void](Invoke-FixtureGit -ArgumentList @('init', '--quiet'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'config', 'user.name', 'DesktopPet provenance self-test'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'config', 'user.email', 'desktop-pet-self-test@example.invalid'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'config', 'core.autocrlf', 'false'))

    $sourcePath = Join-Path $scratch 'classic_philosophy'
    [IO.File]::WriteAllText(
        $sourcePath,
        "Reviewed source bytes.`n",
        $utf8)
    [void](Invoke-FixtureGit -ArgumentList @('add', '--', 'classic_philosophy'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'commit', '--quiet', '-m', 'reviewed source'))
    $reviewedCommit = [string](
        Invoke-FixtureGit -ArgumentList @('rev-parse', 'HEAD'))

    [IO.File]::WriteAllText(
        $sourcePath,
        "Replacement-controlled source bytes.`n",
        $utf8)
    [void](Invoke-FixtureGit -ArgumentList @('add', '--', 'classic_philosophy'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'commit', '--quiet', '-m', 'replacement source'))
    $replacementCommit = [string](
        Invoke-FixtureGit -ArgumentList @('rev-parse', 'HEAD'))
    [void](Invoke-FixtureGit -ArgumentList @(
        'replace', $reviewedCommit, $replacementCommit))

    $reportedCommit = [string](Invoke-FixtureGit -ArgumentList @(
        'rev-parse', '--verify', "$reviewedCommit^{commit}"))
    $substitutedBytes = [string](Invoke-FixtureGit -ArgumentList @(
        'show', "${reviewedCommit}:classic_philosophy"))
    if ($reportedCommit -cne $reviewedCommit -or
        $substitutedBytes -cne 'Replacement-controlled source bytes.') {
        throw 'Disposable fixture did not reproduce Git replacement-object substitution.'
    }

    $originalBytes = [string](Invoke-FixtureGit `
        -NoReplaceObjects `
        -ArgumentList @('show', "${reviewedCommit}:classic_philosophy"))
    if ($originalBytes -cne 'Reviewed source bytes.') {
        throw 'GIT_NO_REPLACE_OBJECTS=1 did not restore reviewed object semantics.'
    }
    $replaceRefs = @(
        Invoke-FixtureGit `
            -NoReplaceObjects `
            -ArgumentList @(
                'for-each-ref',
                '--format=%(refname)',
                'refs/replace')
    )
    if ($replaceRefs.Count -ne 1 -or
        $replaceRefs[0] -cne "refs/replace/$reviewedCommit") {
        throw 'Replacement-ref enumeration did not expose the disposable attack ref.'
    }

    if ($builder -notmatch
        '(?m)^GIT_BIN="\$\(type -P git 2>/dev/null \|\| true\)"$' -or
        $builder -notmatch
        '(?ms)^git_provenance\(\) \{\r?\n' +
        '\s+GIT_NO_REPLACE_OBJECTS=1 "\$GIT_BIN" --no-replace-objects "\$@"\r?\n\}$') {
        throw 'Corpus builder does not use the resolved, replacement-disabled Git wrapper.'
    }
    if ($builder -notmatch
        '(?m)^\s+git_provenance -C "\$SRC" for-each-ref ' +
        "--format='%\(refname\)' refs/replace 2>/dev/null$" -or
        $builder -notmatch
        'source repository contains prohibited Git replacement ref:') {
        throw 'Corpus builder does not fail closed when refs/replace/* entries exist.'
    }

    $unguardedGit = @(
        $builder -split '\r?\n' |
            Where-Object {
                $_ -notmatch '^\s*#' -and
                $_ -match '^\s*git(?:\s|$)'
            }
    )
    if ($unguardedGit.Count -ne 0) {
        throw (
            'Corpus builder contains a Git invocation outside git_provenance: ' +
            ($unguardedGit -join ' | '))
    }

    foreach ($requiredCommand in @(
            'rev-parse --show-toplevel',
            "rev-parse --verify 'HEAD^{commit}'",
            'ls-tree "$SOURCE_COMMIT"',
            'hash-object --no-filters')) {
        if (-not $builder.Contains("git_provenance -C `"`$SRC`" $requiredCommand")) {
            throw "Corpus builder bypasses its Git wrapper for: $requiredCommand"
        }
    }

    Write-Host (
        'PASS: corpus provenance rejects Git replace refs and all source-object ' +
        'queries disable replacement lookup.')
}
finally {
    [Environment]::SetEnvironmentVariable(
        'GIT_NO_REPLACE_OBJECTS',
        $originalNoReplace,
        [EnvironmentVariableTarget]::Process)
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ($resolvedScratch.StartsWith(
            $tempRoot + [IO.Path]::DirectorySeparatorChar +
                'DesktopPet-CorpusProvenance-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
