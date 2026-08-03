#requires -Version 5
[CmdletBinding()]
param(
    [switch]$PublicationFailureOnly,
    # Local unprivileged runs can omit only the symbolic-link fixture. CI keeps
    # the default and therefore still requires full reparse-point coverage.
    [switch]$SkipSymbolicLinkCase
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-ZipSelfTest-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $scratch 'runtime'
$first = Join-Path $scratch 'first.zip'
$second = Join-Path $scratch 'second.zip'
$guarded = Join-Path $scratch 'guarded-last-good.zip'
$expanded = Join-Path $scratch 'expanded'
$manifest = Join-Path $scratch 'runtime-files.txt'
$marker = Join-Path $scratch 'DesktopPet.portable'
$script:testJunctions = @()

function Remove-TestJunction {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Refusing to remove a non-junction ZIP fixture path: $Path"
    }
    [IO.Directory]::Delete($item.FullName)
}

try {
    New-Item -ItemType Directory -Path $runtime, $expanded -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $runtime 'zeta.bin'),
        "zeta`r`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $runtime 'Alpha.txt'),
        "alpha`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $manifest,
        "zeta.bin`nAlpha.txt`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        $marker,
        "DesktopPet portable mode`n",
        (New-Object Text.UTF8Encoding($false)))

    $zipScript = Join-Path $repoRoot 'packaging\New-DeterministicPortableZip.ps1'
    $unsafeRuntimeNames = @(
        'Alpha.txt.',
        'CON',
        'aux.txt',
        ('COM' + [char]0x00B9 + '.log'),
        ('LPT' + [char]0x00B2)
    )
    for ($unsafeIndex = 0;
        $unsafeIndex -lt $unsafeRuntimeNames.Count;
        $unsafeIndex++) {
        $unsafeName = $unsafeRuntimeNames[$unsafeIndex]
        $unsafeManifest =
            Join-Path $scratch "unsafe-runtime-$unsafeIndex.txt"
        $unsafeArchive =
            Join-Path $scratch "unsafe-runtime-$unsafeIndex.zip"
        [IO.File]::WriteAllText(
            $unsafeManifest,
            ($unsafeName + [Environment]::NewLine),
            (New-Object Text.UTF8Encoding($false)))
        $unsafeFailure = $null
        try {
            & $zipScript `
                -RuntimeRoot $runtime `
                -DestinationPath $unsafeArchive `
                -ManifestPath $unsafeManifest `
                -MarkerPath $marker *> $null
        }
        catch {
            $unsafeFailure = $_
        }
        if ($null -eq $unsafeFailure -or
            $unsafeFailure.Exception.Message -notmatch
                '(?i)unsafe.*(?:Windows leaf|reserved)') {
            $unsafeDetail = if ($null -eq $unsafeFailure) {
                '<accepted>'
            }
            else {
                $unsafeFailure.Exception.Message
            }
            throw (
                "Portable ZIP accepted unsafe Win32 runtime leaf name " +
                "'$unsafeName', or rejected it for an unexpected reason: " +
                $unsafeDetail)
        }
        if (Test-Path -LiteralPath $unsafeArchive) {
            throw (
                "Unsafe Win32 runtime leaf name '$unsafeName' created an " +
                'output archive.')
        }
    }
    $protectedFiles = @(
        $manifest,
        $marker,
        (Join-Path $runtime 'zeta.bin'),
        (Join-Path $runtime 'Alpha.txt'))
    $protectedHashes = @{}
    foreach ($protectedFile in $protectedFiles) {
        $protectedHashes[$protectedFile] = (
            Get-FileHash -LiteralPath $protectedFile -Algorithm SHA256).Hash
    }

    [IO.File]::WriteAllBytes(
        $guarded,
        [Text.Encoding]::ASCII.GetBytes('last-good-portable-zip-bytes'))
    $lastGoodHash = (
        Get-FileHash -LiteralPath $guarded -Algorithm SHA256).Hash
    $corruptCompletedStagedArchive = {
        param([Parameter(Mandatory = $true)][string]$StagedArchivePath)

        if (-not (Test-Path -LiteralPath $StagedArchivePath -PathType Leaf) -or
            (Get-Item -LiteralPath $StagedArchivePath).Length -eq 0) {
            throw 'ZIP fault injection did not receive a completed staged archive.'
        }
        [IO.File]::WriteAllBytes(
            $StagedArchivePath,
            [Text.Encoding]::ASCII.GetBytes(
                'adversarial-corruption-after-archive-creation'))
    }
    $verificationFailure = $null
    try {
        & $zipScript `
            -RuntimeRoot $runtime `
            -DestinationPath $guarded `
            -ManifestPath $manifest `
            -MarkerPath $marker `
            -AdditionalStagedArchiveValidation $corruptCompletedStagedArchive `
            *> $null
    }
    catch {
        $verificationFailure = $_
    }
    if ($null -eq $verificationFailure -or
        $verificationFailure.Exception.Message -notmatch
            '(?i)staged portable ZIP verification failed') {
        throw (
            'Portable ZIP staged-corruption regression did not fail during ' +
            'mandatory pre-publication readback.')
    }
    if ((Get-FileHash -LiteralPath $guarded -Algorithm SHA256).Hash -cne
        $lastGoodHash) {
        throw (
            'Failed staged ZIP verification replaced the last-good archive.')
    }
    if (@(Get-ChildItem `
            -LiteralPath $scratch `
            -Directory `
            -Filter '.DesktopPet-zip-*').Count -ne 0) {
        throw 'Failed staged ZIP verification left private staging behind.'
    }
    if ($PublicationFailureOnly) {
        Write-Host (
            'PASS: staged portable ZIP corruption is rejected before ' +
            'publication and preserves the last-good destination.')
        return
    }

    foreach ($unsafeDestination in @(
            $manifest,
            $marker,
            (Join-Path $runtime 'Alpha.txt'),
            (Join-Path $runtime 'unexpected-output.zip'))) {
        $accepted = $true
        $message = ''
        try {
            & $zipScript `
                -RuntimeRoot $runtime `
                -DestinationPath $unsafeDestination `
                -ManifestPath $manifest `
                -MarkerPath $marker *> $null
        }
        catch {
            $accepted = $false
            $message = $_.Exception.Message
        }
        if ($accepted -or
            $message -notmatch 'overlaps a protected packaging input') {
            throw (
                "Unsafe portable ZIP output was not rejected fail-closed: " +
                "$unsafeDestination ($message)")
        }
        foreach ($protectedFile in $protectedFiles) {
            $observedHash = (
                Get-FileHash -LiteralPath $protectedFile -Algorithm SHA256).Hash
            if ($observedHash -cne $protectedHashes[$protectedFile]) {
                throw "Portable ZIP alias rejection modified input: $protectedFile"
            }
        }
        if ($unsafeDestination.EndsWith(
                'unexpected-output.zip',
                [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $unsafeDestination)) {
            throw 'Rejected in-runtime portable ZIP output was created.'
        }
    }

    $externalRoot = Join-Path $scratch 'external-input'
    New-Item -ItemType Directory -Path $externalRoot -Force | Out-Null
    $externalSentinel = Join-Path $externalRoot 'external-sentinel.bin'
    [IO.File]::WriteAllBytes(
        $externalSentinel,
        [Text.Encoding]::ASCII.GetBytes('external-input-must-survive'))
    $externalHash = (
        Get-FileHash -LiteralPath $externalSentinel -Algorithm SHA256).Hash

    $linkCases = @(
            [pscustomobject]@{
                Name = 'runtime-file-hard-link'
                ExpectedMessage = '(?i)hard-link alias'
                Kind = 'HardLink'
            },
            [pscustomobject]@{
                Name = 'runtime-root-junction'
                ExpectedMessage = '(?i)reparse point'
                Kind = 'Junction'
            },
            [pscustomobject]@{
                Name = 'runtime-file-symbolic-link'
                ExpectedMessage = '(?i)reparse point'
                Kind = 'SymbolicLink'
            })
    if ($SkipSymbolicLinkCase) {
        $linkCases = @(
            $linkCases | Where-Object Kind -cne 'SymbolicLink')
    }
    foreach ($linkCase in $linkCases) {
        $caseRoot = Join-Path $scratch $linkCase.Name
        $caseRuntime = Join-Path $caseRoot 'runtime'
        $caseManifest = Join-Path $caseRoot 'runtime-files.txt'
        $caseMarker = Join-Path $caseRoot 'DesktopPet.portable'
        $caseZip = Join-Path $caseRoot 'rejected.zip'
        New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
        [IO.File]::WriteAllText(
            $caseManifest,
            "payload.bin`n",
            (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText(
            $caseMarker,
            "DesktopPet portable mode`n",
            (New-Object Text.UTF8Encoding($false)))

        if ($linkCase.Kind -ceq 'Junction') {
            $junctionTarget = Join-Path $caseRoot 'junction-target'
            New-Item -ItemType Directory -Path $junctionTarget -Force |
                Out-Null
            Copy-Item `
                -LiteralPath $externalSentinel `
                -Destination (Join-Path $junctionTarget 'payload.bin')
            $junction = New-Item `
                -ItemType Junction `
                -Path $caseRuntime `
                -Target $junctionTarget `
                -ErrorAction Stop
            if (($junction.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw 'ZIP fixture runtime junction is not a reparse point.'
            }
            $script:testJunctions += $caseRuntime
        }
        else {
            New-Item -ItemType Directory -Path $caseRuntime -Force |
                Out-Null
            $linkPath = Join-Path $caseRuntime 'payload.bin'
            try {
                New-Item `
                    -ItemType $linkCase.Kind `
                    -Path $linkPath `
                    -Target $externalSentinel `
                    -ErrorAction Stop | Out-Null
            }
            catch [UnauthorizedAccessException] {
                throw (
                    'Portable ZIP source-symlink regression requires a ' +
                    'Windows token permitted to create symbolic links; the ' +
                    'production reparse-point check remains enabled. ' +
                    $_.Exception.Message)
            }
        }

        $failure = $null
        try {
            & $zipScript `
                -RuntimeRoot $caseRuntime `
                -DestinationPath $caseZip `
                -ManifestPath $caseManifest `
                -MarkerPath $caseMarker *> $null
        }
        catch {
            $failure = $_
        }
        if ($null -eq $failure) {
            throw "Portable ZIP accepted unsafe source: $($linkCase.Name)"
        }
        if ($failure.Exception.Message -notmatch $linkCase.ExpectedMessage) {
            throw (
                "Portable ZIP unsafe-source case '$($linkCase.Name)' failed " +
                "for an unexpected reason: $($failure.Exception.Message)")
        }
        if (Test-Path -LiteralPath $caseZip) {
            throw (
                "Portable ZIP unsafe-source case '$($linkCase.Name)' " +
                'created an output archive.')
        }
        if ((Get-FileHash `
                -LiteralPath $externalSentinel `
                -Algorithm SHA256).Hash -cne $externalHash) {
            throw (
                "Portable ZIP unsafe-source case '$($linkCase.Name)' " +
                'modified the external sentinel.')
        }
        if ($linkCase.Kind -ceq 'Junction') {
            Remove-TestJunction -Path $caseRuntime
        }
    }

    & $zipScript `
        -RuntimeRoot $runtime `
        -DestinationPath $first `
        -ManifestPath $manifest `
        -MarkerPath $marker
    Start-Sleep -Milliseconds 1100
    & $zipScript `
        -RuntimeRoot $runtime `
        -DestinationPath $second `
        -ManifestPath $manifest `
        -MarkerPath $marker

    $firstHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
    if ($firstHash -ne $secondHash) {
        throw 'Equivalent portable ZIP inputs did not produce identical bytes.'
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($first)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        $expectedNames = @('Alpha.txt', 'DesktopPet.portable', 'zeta.bin')
        if (@(Compare-Object $expectedNames $names -SyncWindow 0).Count -ne 0) {
            throw "ZIP entry order is not canonical: $($names -join ', ')"
        }
        foreach ($entry in $archive.Entries) {
            if ($entry.LastWriteTime.DateTime -ne
                [DateTime]'1980-01-01T00:00:00') {
                throw "ZIP timestamp is not normalized for '$($entry.FullName)'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    Expand-Archive -LiteralPath $first -DestinationPath $expanded
    & (Join-Path $repoRoot 'packaging\Test-RuntimePayload.ps1') `
        -PayloadRoot $expanded `
        -ReferenceRoot $runtime `
        -ManifestPath $manifest `
        -AllowedExtraFiles @('DesktopPet.portable')

    $unexpected = Join-Path $expanded 'nested'
    New-Item -ItemType Directory -Path $unexpected -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $unexpected 'extra.txt'), 'extra')
    try {
        & (Join-Path $repoRoot 'packaging\Test-RuntimePayload.ps1') `
            -PayloadRoot $expanded `
            -ManifestPath $manifest `
            -AllowedExtraFiles @('DesktopPet.portable')
        throw 'Nested package debris was accepted.'
    }
    catch {
        if ($_.Exception.Message -eq 'Nested package debris was accepted.') {
            throw
        }
    }

    # Bundled-content (-ContentDirectories) coverage: nested entries are added
    # deterministically, survive readback, pass the payload gate under an allowed
    # directory, and an unsafe content prefix is rejected fail-closed.
    $contentSource = Join-Path $scratch 'content-source'
    $contentPets = Join-Path $contentSource 'pets\demo'
    $contentFortunes = Join-Path $contentSource 'fortunes'
    New-Item -ItemType Directory -Path $contentPets, $contentFortunes -Force |
        Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $contentPets 'animations.xml'),
        "<pet/>`n",
        (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText(
        (Join-Path $contentFortunes 'demo.txt'),
        "a demo fortune`n",
        (New-Object Text.UTF8Encoding($false)))
    $contentDirs = @(
        @{ Prefix = 'pets'; Source = (Join-Path $contentSource 'pets') }
        @{ Prefix = 'fortunes'; Source = $contentFortunes }
    )
    $contentFirst = Join-Path $scratch 'content-first.zip'
    $contentSecond = Join-Path $scratch 'content-second.zip'
    & $zipScript -RuntimeRoot $runtime -DestinationPath $contentFirst `
        -ManifestPath $manifest -MarkerPath $marker `
        -ContentDirectories $contentDirs
    Start-Sleep -Milliseconds 1100
    & $zipScript -RuntimeRoot $runtime -DestinationPath $contentSecond `
        -ManifestPath $manifest -MarkerPath $marker `
        -ContentDirectories $contentDirs
    if ((Get-FileHash -LiteralPath $contentFirst -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $contentSecond -Algorithm SHA256).Hash) {
        throw 'Bundled-content portable ZIP was not deterministic.'
    }
    $contentArchive = [IO.Compression.ZipFile]::OpenRead($contentFirst)
    try {
        $contentNames = @($contentArchive.Entries | ForEach-Object FullName)
        foreach ($required in @('pets/demo/animations.xml', 'fortunes/demo.txt')) {
            if ($contentNames -cnotcontains $required) {
                throw "Bundled-content ZIP is missing entry '$required'."
            }
        }
    }
    finally {
        $contentArchive.Dispose()
    }
    $contentExpanded = Join-Path $scratch 'content-expanded'
    Expand-Archive -LiteralPath $contentFirst -DestinationPath $contentExpanded
    & (Join-Path $repoRoot 'packaging\Test-RuntimePayload.ps1') `
        -PayloadRoot $contentExpanded `
        -ReferenceRoot $runtime `
        -ManifestPath $manifest `
        -AllowedExtraFiles @('DesktopPet.portable') `
        -AllowedExtraDirectories @('pets', 'fortunes')

    $unsafePrefixZip = Join-Path $scratch 'content-unsafe.zip'
    $unsafePrefixFailure = $null
    try {
        & $zipScript -RuntimeRoot $runtime -DestinationPath $unsafePrefixZip `
            -ManifestPath $manifest -MarkerPath $marker `
            -ContentDirectories @(
                @{ Prefix = 'CON'; Source = $contentFortunes }
            ) *> $null
    }
    catch {
        $unsafePrefixFailure = $_
    }
    if ($null -eq $unsafePrefixFailure -or
        $unsafePrefixFailure.Exception.Message -notmatch '(?i)prefix is unsafe') {
        throw 'Bundled-content ZIP accepted an unsafe content prefix.'
    }
    if (Test-Path -LiteralPath $unsafePrefixZip) {
        throw 'Rejected bundled-content ZIP created an output archive.'
    }

    $linkCoverage = if ($SkipSymbolicLinkCase) {
        'source junction/hard-link rejection'
    }
    else {
        'source symlink/junction/hard-link rejection'
    }
    Write-Host (
        'PASS: deterministic portable ZIP, protected-output aliases, ' +
        "Win32 leaf-name rejection, $linkCoverage, external sentinel " +
        'preservation, staged-corruption last-good preservation, ' +
        'bundled-content determinism/rejection, and marker regression harness.')
}
finally {
    foreach ($junctionPath in @($script:testJunctions | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $junctionPath) {
            Remove-TestJunction -Path $junctionPath
        }
    }
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-ZipSelfTest-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
