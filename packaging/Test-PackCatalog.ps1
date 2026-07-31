#requires -Version 5
[CmdletBinding()]
param(
    [string]$CatalogPath,
    [string]$RepositoryRoot,
    [string]$DataValidatorPath,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 30,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path $scriptDirectory -Parent
}
if ([string]::IsNullOrWhiteSpace($DataValidatorPath)) {
    $DataValidatorPath = Join-Path $scriptDirectory 'Test-PackData.ps1'
}

$resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$resolvedDataValidator = [IO.Path]::GetFullPath($DataValidatorPath)
if (-not (Test-Path -LiteralPath $resolvedDataValidator -PathType Leaf)) {
    throw "Pack-data validator not found: $resolvedDataValidator"
}
$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-PackCatalog-' + [Guid]::NewGuid().ToString('N'))

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-PinnedPackBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$PackId,
        [Parameter(Mandatory = $true)][string]$Revision
    )

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $client = New-Object Net.Http.HttpClient($handler)
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        try {
            $response = $client.GetAsync($Url).GetAwaiter().GetResult()
        }
        catch {
            throw (
                "Pack '$PackId' is not remotely addressable at pinned revision " +
                "'$Revision': $($_.Exception.Message)"
            )
        }
        if ([int]$response.StatusCode -ne 200) {
            throw (
                "Pack '$PackId' is not remotely addressable at pinned revision " +
                "'$Revision': HTTP $([int]$response.StatusCode) " +
                "($($response.ReasonPhrase))."
            )
        }
        return ,([byte[]]$response.Content.ReadAsByteArrayAsync().
            GetAwaiter().GetResult())
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}

function Assert-PackCatalogLicense {
    param(
        [Parameter(Mandatory = $true)][object]$Pack,
        [Parameter(Mandatory = $true)][string]$PackId,
        [Parameter(Mandatory = $true)][bool]$RedistributionApproved
    )

    $licenseProperty = $Pack.PSObject.Properties['license']
    if ($null -eq $licenseProperty -or $licenseProperty.Value -isnot [string]) {
        throw "Pack '$PackId' must declare a string license value."
    }
    $license = [string]$licenseProperty.Value
    if ($license.Length -lt 2 -or
        $license.Length -gt 200 -or
        $license -cne $license.Trim() -or
        $license -match '[\x00-\x1f\x7f]') {
        throw "Pack '$PackId' license must be 2..200 trimmed non-control characters."
    }
    if ($RedistributionApproved -and
        ($license -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.+() -]*$' -or
         $license -match (
            '(?i)(?:^|[ ()_-])(?:fair[ -]?use|personal[ -]?use|' +
            'all[ -]?rights[ -]?reserved|copyright(?:ed)?|mixed|unknown|' +
            'unverified|tbd|todo|placeholder|none)(?:$|[ ()_-])'))) {
        throw (
            "Approved pack '$PackId' license must be a concrete SPDX-style " +
            'expression or LicenseRef value, not a non-grant label.'
        )
    }
}

function Invoke-PackCatalogValidation {
    param(
        [Parameter(Mandatory = $true)][object]$Catalog,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][scriptblock]$FetchBytes,
        [Parameter(Mandatory = $true)][string]$RemoteScratchRoot
    )

    if ([int]$Catalog.version -ne 2) {
        throw "Unsupported pack catalog version: $($Catalog.version)"
    }
    $catalogPacks = @($Catalog.packs)
    $approvedEntryCount = 0
    foreach ($candidatePack in $catalogPacks) {
        $candidateApproval =
            $candidatePack.PSObject.Properties['redistributionApproved']
        if ($null -eq $candidateApproval -or
            $candidateApproval.Value -isnot [bool]) {
            throw 'Every pack must explicitly set redistributionApproved to a boolean.'
        }
        if ([bool]$candidateApproval.Value) {
            $approvedEntryCount++
        }
    }
    $revisionProperty = $Catalog.PSObject.Properties['revision']
    if ($null -eq $revisionProperty) {
        throw 'Pack catalog must explicitly declare revision.'
    }
    $revision = if ($null -eq $revisionProperty.Value) {
        ''
    }
    else {
        [string]$revisionProperty.Value
    }
    if ($approvedEntryCount -eq 0 -and
        -not [string]::IsNullOrWhiteSpace($revision)) {
        throw (
            'A held-only pack catalog must set revision to null or empty; ' +
            'an unpublished/stale commit must not be represented as retrievable provenance.'
        )
    }
    if ($approvedEntryCount -gt 0 -and
        $revision -cnotmatch '^[0-9a-f]{40}$') {
        throw (
            'A catalog with approved packs requires a lowercase, ' +
            "commit-pinned SHA-1 revision: '$revision'."
        )
    }

    $packIds = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
    $catalogPackFiles = @()
    foreach ($pack in $catalogPacks) {
        $packId = [string]$pack.id
        if ($packId -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
            throw "Pack ID is not a safe lowercase slug: '$packId'."
        }
        if (-not $packIds.Add($packId)) {
            throw "Pack catalog contains a duplicate ID: '$packId'."
        }
        if ([int]$pack.dataSchema -notin @(1, 2)) {
            throw "Pack '$packId' has unsupported record schema '$($pack.dataSchema)'."
        }
        $redistributionProperty =
            $pack.PSObject.Properties['redistributionApproved']
        if ($null -eq $redistributionProperty -or
            $redistributionProperty.Value -isnot [bool]) {
            throw "Pack '$packId' must explicitly set redistributionApproved to a boolean."
        }
        if ([int]$pack.count -le 0 -or [long]$pack.bytes -le 0) {
            throw "Pack '$packId' has invalid record or byte metadata."
        }
        $expectedHash = [string]$pack.sha256
        if ($expectedHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Pack '$packId' has an invalid SHA-256."
        }
        $redistributionApproved =
            [bool]$redistributionProperty.Value
        Assert-PackCatalogLicense -Pack $pack -PackId $packId `
            -RedistributionApproved $redistributionApproved
        $urlProperty = $pack.PSObject.Properties['url']
        $catalogUrl = if (
            $null -eq $urlProperty -or
            $null -eq $urlProperty.Value) {
            ''
        }
        else {
            [string]$urlProperty.Value
        }
        $expectedUrl = (
            "https://raw.githubusercontent.com/bigfnj/desktopPet/{0}/packs/{1}.txt" -f
            $revision,
            $packId)
        if (-not $redistributionApproved -and
            -not [string]::IsNullOrWhiteSpace($catalogUrl)) {
            throw (
                "Pack '$packId' is held for rights review, so url must be " +
                'null, absent, or empty.'
            )
        }
        if ($redistributionApproved -and $catalogUrl -cne $expectedUrl) {
            throw "Pack '$packId' URL is not pinned to the catalog revision and exact file."
        }

        $packFileName = "$packId.txt"
        $catalogPackFiles += $packFileName
        $localPath = [IO.Path]::GetFullPath(
            (Join-Path $RepoRoot "packs\$packFileName"))
        $packRoot = [IO.Path]::GetFullPath(
            (Join-Path $RepoRoot 'packs')).TrimEnd('\')
        if (-not $localPath.StartsWith(
                $packRoot + '\',
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $localPath -PathType Leaf)) {
            throw "Pack '$packId' local data file is missing or unsafe: $localPath"
        }
        $localInfo = Get-Item -LiteralPath $localPath
        if ($localInfo.Length -ne [long]$pack.bytes) {
            throw (
                "Pack '$packId' local byte count changed. Expected " +
                "$($pack.bytes); found $($localInfo.Length)."
            )
        }
        $localHash = (
            Get-FileHash -LiteralPath $localPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($localHash -cne $expectedHash) {
            throw (
                "Pack '$packId' local SHA-256 changed. Expected " +
                "$expectedHash; found $localHash."
            )
        }
        & $resolvedDataValidator `
            -Path $localPath `
            -DataSchema ([int]$pack.dataSchema) `
            -ExpectedRowCount ([int]$pack.count)

        if (-not $redistributionApproved) {
            continue
        }

        $remoteResult = @(& $FetchBytes $expectedUrl $packId $revision)
        [byte[]]$remoteBytes = if (
            $remoteResult.Count -eq 1 -and
            $remoteResult[0] -is [byte[]]) {
            $remoteResult[0]
        }
        else {
            [byte[]]$remoteResult
        }
        if ($remoteBytes.Length -ne [long]$pack.bytes) {
            throw (
                "Pack '$packId' pinned remote byte count does not match the " +
                "catalog. Expected $($pack.bytes); found $($remoteBytes.Length)."
            )
        }
        $remoteHash = Get-Sha256Hex -Bytes $remoteBytes
        if ($remoteHash -cne $expectedHash) {
            throw (
                "Pack '$packId' pinned remote SHA-256 does not match the " +
                "catalog. Expected $expectedHash; found $remoteHash."
            )
        }
        $remotePath = Join-Path $RemoteScratchRoot "$packId.remote.txt"
        [IO.File]::WriteAllBytes($remotePath, $remoteBytes)
        & $resolvedDataValidator `
            -Path $remotePath `
            -DataSchema ([int]$pack.dataSchema) `
            -ExpectedRowCount ([int]$pack.count)
    }
    if ($packIds.Count -eq 0) {
        throw 'Pack catalog contains no packs.'
    }

    $diskPackFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'packs') `
            -Filter '*.txt' -File |
            ForEach-Object { $_.Name } |
            Sort-Object
    )
    $difference = @(
        Compare-Object @($catalogPackFiles | Sort-Object) $diskPackFiles)
    if ($difference.Count -gt 0) {
        $detail = ($difference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join '; '
        throw "Pack catalog and packs directory disagree: $detail"
    }
}

function Invoke-SelfTest {
    $fixtureRoot = Join-Path $scratchRoot 'fixture'
    $fixturePacks = Join-Path $fixtureRoot 'packs'
    New-Item -ItemType Directory -Path $fixturePacks -Force | Out-Null
    $tab = [char]9
    $fixtureText =
        "sample${tab}general${tab}general${tab}0${tab}A valid fixture fortune."
    $utf8 = New-Object Text.UTF8Encoding($false)
    [byte[]]$fixtureBytes = $utf8.GetBytes($fixtureText)
    [IO.File]::WriteAllBytes(
        (Join-Path $fixturePacks 'sample.txt'),
        $fixtureBytes)
    $revision = 'a' * 40
    $fixtureHash = Get-Sha256Hex -Bytes $fixtureBytes
    $baseline = [pscustomobject][ordered]@{
        version = 2
        revision = $null
        packs = @(
            [pscustomobject][ordered]@{
                id = 'sample'
                name = 'Sample'
                desc = 'Offline fixture'
                vibe = 'clean'
                license = 'CC0-1.0'
                count = 1
                bytes = $fixtureBytes.Length
                sha256 = $fixtureHash
                dataSchema = 1
                redistributionApproved = $false
                url = $null
            }
        )
    }
    $unexpectedFetcher = {
        param($Url, $PackId, $PinnedRevision)
        throw "held pack '$PackId' unexpectedly attempted a remote fetch"
    }
    Invoke-PackCatalogValidation `
        -Catalog $baseline `
        -RepoRoot $fixtureRoot `
        -FetchBytes $unexpectedFetcher `
        -RemoteScratchRoot $scratchRoot

    $approved = $baseline | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $approved.revision = $revision
    $approved.packs[0].redistributionApproved = $true
    $approved.packs[0].url = (
        'https://raw.githubusercontent.com/bigfnj/desktopPet/' +
        "$revision/packs/sample.txt")
    $validFetcher = {
        param($Url, $PackId, $PinnedRevision)
        return ,$fixtureBytes
    }.GetNewClosure()
    Invoke-PackCatalogValidation `
        -Catalog $approved `
        -RepoRoot $fixtureRoot `
        -FetchBytes $validFetcher `
        -RemoteScratchRoot $scratchRoot

    function Assert-Rejected {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][object]$Catalog,
            [Parameter(Mandatory = $true)][scriptblock]$Fetcher,
            [Parameter(Mandatory = $true)][string]$ExpectedMessage
        )

        $accepted = $true
        $message = ''
        try {
            Invoke-PackCatalogValidation `
                -Catalog $Catalog `
                -RepoRoot $fixtureRoot `
                -FetchBytes $Fetcher `
                -RemoteScratchRoot $scratchRoot *> $null
        }
        catch {
            $accepted = $false
            $message = $_.Exception.Message
        }
        if ($accepted) {
            throw "Pack-catalog negative control was accepted: $Name"
        }
        if ($message -notmatch $ExpectedMessage) {
            throw (
                "Pack-catalog negative control '$Name' failed for an " +
                "unexpected reason: $message"
            )
        }
    }

    $heldWithUrl = $baseline | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $heldWithUrl.packs[0].url = (
        'https://raw.githubusercontent.com/bigfnj/desktopPet/' +
        "$revision/packs/sample.txt")
    Assert-Rejected `
        -Name 'held-with-url' `
        -Catalog $heldWithUrl `
        -Fetcher $unexpectedFetcher `
        -ExpectedMessage 'held for rights review.*url must be'

    $heldWithRevision = $baseline | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $heldWithRevision.revision = $revision
    Assert-Rejected `
        -Name 'held-with-revision' `
        -Catalog $heldWithRevision `
        -Fetcher $unexpectedFetcher `
        -ExpectedMessage 'held-only pack catalog must set revision to null'

    $missingLicense = $baseline | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $missingLicense.packs[0].PSObject.Properties.Remove('license')
    Assert-Rejected `
        -Name 'missing-license' `
        -Catalog $missingLicense `
        -Fetcher $unexpectedFetcher `
        -ExpectedMessage 'must declare a string license value'

    $nonGrantLicense = $approved | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $nonGrantLicense.packs[0].license = 'fair-use'
    Assert-Rejected `
        -Name 'approved-non-grant-license' `
        -Catalog $nonGrantLicense `
        -Fetcher $validFetcher `
        -ExpectedMessage 'concrete SPDX-style expression.*non-grant label'

    $wrongUrl = $approved | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $wrongUrl.packs[0].url =
        'https://raw.githubusercontent.com/bigfnj/desktopPet/master/packs/sample.txt'
    Assert-Rejected `
        -Name 'branch-url' `
        -Catalog $wrongUrl `
        -Fetcher $validFetcher `
        -ExpectedMessage 'not pinned to the catalog revision'

    $missingFetcher = {
        param($Url, $PackId, $PinnedRevision)
        throw (
            "Pack '$PackId' is not remotely addressable at pinned revision " +
            "'$PinnedRevision': HTTP 404 (Not Found)."
        )
    }
    Assert-Rejected `
        -Name 'unpublished-revision' `
        -Catalog $approved `
        -Fetcher $missingFetcher `
        -ExpectedMessage 'not remotely addressable.*HTTP 404'

    [byte[]]$shortBytes = $fixtureBytes[0..($fixtureBytes.Length - 2)]
    $shortFetcher = {
        param($Url, $PackId, $PinnedRevision)
        return ,$shortBytes
    }.GetNewClosure()
    Assert-Rejected `
        -Name 'remote-length' `
        -Catalog $approved `
        -Fetcher $shortFetcher `
        -ExpectedMessage 'pinned remote byte count'

    [byte[]]$changedBytes = $fixtureBytes.Clone()
    $changedBytes[$changedBytes.Length - 1] =
        [byte]($changedBytes[$changedBytes.Length - 1] -bxor 1)
    $changedFetcher = {
        param($Url, $PackId, $PinnedRevision)
        return ,$changedBytes
    }.GetNewClosure()
    Assert-Rejected `
        -Name 'remote-hash' `
        -Catalog $approved `
        -Fetcher $changedFetcher `
        -ExpectedMessage 'pinned remote SHA-256'

    Write-Host (
        'Pack-catalog self-tests passed: held packs never fetch, approved ' +
        'packs require exact pinned bytes, and held revision, held URL, branch URL, ' +
        'missing/non-grant license, unpublished revision, remote length, and ' +
        'remote hash controls reject.'
    ) -ForegroundColor Green
}

try {
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    if ($SelfTest) {
        & $resolvedDataValidator -SelfTest
        Invoke-SelfTest
    }
    if (-not [string]::IsNullOrWhiteSpace($CatalogPath)) {
        $resolvedCatalog = (Resolve-Path -LiteralPath $CatalogPath).Path
        $catalog = Get-Content -LiteralPath $resolvedCatalog -Raw |
            ConvertFrom-Json
        $remoteFetcher = {
            param($Url, $PackId, $PinnedRevision)
            return ,(Get-PinnedPackBytes `
                -Url $Url `
                -PackId $PackId `
                -Revision $PinnedRevision)
        }
        Invoke-PackCatalogValidation `
            -Catalog $catalog `
            -RepoRoot $resolvedRepositoryRoot `
            -FetchBytes $remoteFetcher `
            -RemoteScratchRoot $scratchRoot
        Write-Host (
            "Pack catalog verified against local bytes and approved pinned remotes: " +
            "$resolvedCatalog"
        ) -ForegroundColor Green
    }
    elseif (-not $SelfTest) {
        throw 'Specify -CatalogPath or -SelfTest.'
    }
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
        $resolvedTemp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolvedScratch.StartsWith(
                $resolvedTemp + '\DesktopPet-PackCatalog-',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unsafe pack-catalog scratch root: $resolvedScratch"
        }
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
