#requires -Version 5
[CmdletBinding()]
param(
    [string]$CatalogPath,
    [string]$EvidencePath,
    [string]$RepositoryRoot,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Context is missing required property '$Name'."
    }
    return $property.Value
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $observed = @($Object.PSObject.Properties.Name | Sort-Object)
    $difference = @(Compare-Object @($Expected | Sort-Object) $observed)
    if ($difference.Count -ne 0) {
        $detail = ($difference | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)"
        }) -join '; '
        throw "$Context properties are not exact: $detail"
    }
}

function Assert-BoundedText {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Context,
        [ValidateRange(1, 8192)][int]$MinimumLength = 1,
        [ValidateRange(1, 8192)][int]$MaximumLength = 200
    )

    if ($Value -isnot [string]) {
        throw "$Context must be a string."
    }
    $text = [string]$Value
    if ($text.Length -lt $MinimumLength -or
        $text.Length -gt $MaximumLength -or
        $text -cne $text.Trim() -or
        $text -match '[\x00-\x1f\x7f]') {
        throw (
            "$Context must be $MinimumLength..$MaximumLength trimmed " +
            'non-control characters.'
        )
    }
}

function Assert-LicenseExpression {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Context
    )

    Assert-BoundedText -Value $Value -Context $Context `
        -MinimumLength 2 -MaximumLength 200
    $expression = [string]$Value
    if ($expression -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.+() -]*$' -or
        $expression -match (
            '(?i)(?:^|[ ()_-])(?:fair[ -]?use|personal[ -]?use|' +
            'all[ -]?rights[ -]?reserved|copyright(?:ed)?|mixed|unknown|' +
            'unverified|tbd|todo|placeholder|none)(?:$|[ ()_-])')) {
        throw (
            "$Context must be a concrete SPDX-style expression or " +
            'LicenseRef value, not a non-grant or placeholder label.'
        )
    }
}

function Assert-GrantText {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Context
    )

    Assert-BoundedText -Value $Value -Context $Context `
        -MinimumLength 32 -MaximumLength 2000
    if ([string]$Value -match (
            '(?i)\b(?:fair[ -]?use|personal[ -]?use|no grant|' +
            'not granted|unknown|unverified|pending|tbd|todo|placeholder)\b')) {
        throw "$Context contains a non-grant or placeholder statement."
    }
}

function Assert-CanonicalSourceRepository {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Context
    )

    Assert-BoundedText -Value $Value -Context $Context `
        -MinimumLength 10 -MaximumLength 2048
    $repository = [string]$Value
    $uri = $null
    if (-not [Uri]::TryCreate($repository, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne [Uri]::UriSchemeHttps -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        $uri.AbsolutePath -ceq '/') {
        throw (
            "$Context must be a canonical HTTPS repository URL with a " +
            'non-root path and no userinfo, query, or fragment.'
        )
    }
}

function Assert-StructuredRightsDocument {
    param(
        [Parameter(Mandatory = $true)][object]$Document,
        [Parameter(Mandatory = $true)][object]$Pack,
        [Parameter(Mandatory = $true)][object]$Approval
    )

    $packId = [string]$Pack.id
    Assert-ExactProperties -Object $Document `
        -Expected @(
            'schemaVersion',
            'packId',
            'catalogRevision',
            'packSha256',
            'recordCount',
            'catalogLicenseExpression',
            'sources'
        ) `
        -Context "Pack '$packId' structured rights document"
    if ([int](Get-RequiredProperty -Object $Document -Name 'schemaVersion' `
            -Context "Pack '$packId' structured rights document") -ne 1) {
        throw "Pack '$packId' has an unsupported rights-document schema."
    }
    if ([string]$Document.packId -cne $packId -or
        [string]$Document.catalogRevision -cne [string]$Approval.catalogRevision -or
        [string]$Document.packSha256 -cne [string]$Approval.packSha256) {
        throw (
            "Pack '$packId' structured rights document is not bound to " +
            'the exact approval identity.'
        )
    }

    $recordCountValue = Get-RequiredProperty -Object $Document `
        -Name 'recordCount' -Context "Pack '$packId' structured rights document"
    if (($recordCountValue -isnot [int] -and
         $recordCountValue -isnot [long]) -or
        [long]$recordCountValue -lt 1 -or
        [long]$recordCountValue -gt 100000 -or
        [long]$recordCountValue -ne [long]$Pack.count) {
        throw (
            "Pack '$packId' structured rights document recordCount does " +
            'not match the exact catalog record count.'
        )
    }
    $recordCount = [int]$recordCountValue

    $catalogLicense = [string](Get-RequiredProperty -Object $Pack `
        -Name 'license' -Context "Pack '$packId'")
    Assert-LicenseExpression -Value $Document.catalogLicenseExpression `
        -Context "Pack '$packId' catalog license expression"
    if ([string]$Document.catalogLicenseExpression -cne $catalogLicense) {
        throw (
            "Pack '$packId' rights-document license expression does not " +
            'exactly match the catalog license.'
        )
    }

    $sourcesProperty = $Document.PSObject.Properties['sources']
    if ($null -eq $sourcesProperty) {
        throw "Pack '$packId' structured rights document is missing required property 'sources'."
    }
    $sourcesValue = $sourcesProperty.Value
    if ($sourcesValue -isnot [Array] -or @($sourcesValue).Count -eq 0) {
        throw "Pack '$packId' rights document must contain a non-empty sources array."
    }

    $sourceIds = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    $coveredRecords = New-Object 'Collections.Generic.HashSet[int]'
    $sourceLicenses = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    foreach ($source in @($sourcesValue)) {
        Assert-ExactProperties -Object $source `
            -Expected @(
                'sourceId',
                'sourceRepository',
                'sourceRevision',
                'licenseExpression',
                'redistributionGrant',
                'obligations',
                'recordRanges'
            ) `
            -Context "Pack '$packId' source rights entry"

        $sourceId = [string](Get-RequiredProperty -Object $source `
            -Name 'sourceId' -Context "Pack '$packId' source rights entry")
        if ($sourceId -cnotmatch (
                '^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$') -or
            -not $sourceIds.Add($sourceId)) {
            throw "Pack '$packId' has invalid or duplicate sourceId '$sourceId'."
        }
        Assert-CanonicalSourceRepository -Value $source.sourceRepository `
            -Context "Pack '$packId' source '$sourceId' repository"
        if ([string]$source.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
            throw (
                "Pack '$packId' source '$sourceId' must pin a lowercase " +
                '40-character source revision.'
            )
        }
        Assert-LicenseExpression -Value $source.licenseExpression `
            -Context "Pack '$packId' source '$sourceId' license expression"
        [void]$sourceLicenses.Add([string]$source.licenseExpression)
        Assert-GrantText -Value $source.redistributionGrant `
            -Context "Pack '$packId' source '$sourceId' redistribution grant"

        $obligationsProperty = $source.PSObject.Properties['obligations']
        if ($null -eq $obligationsProperty) {
            throw (
                "Pack '$packId' source '$sourceId' is missing required " +
                "property 'obligations'."
            )
        }
        $obligationsValue = $obligationsProperty.Value
        if ($obligationsValue -isnot [Array] -or
            @($obligationsValue).Count -eq 0) {
            throw (
                "Pack '$packId' source '$sourceId' must declare a non-empty " +
                'obligations array.'
            )
        }
        $obligations = New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
        foreach ($obligationValue in @($obligationsValue)) {
            Assert-BoundedText -Value $obligationValue `
                -Context "Pack '$packId' source '$sourceId' obligation" `
                -MinimumLength 8 -MaximumLength 500
            $obligation = [string]$obligationValue
            if ($obligation -match '(?i)\b(?:unknown|pending|tbd|todo|placeholder)\b' -or
                -not $obligations.Add($obligation)) {
                throw (
                    "Pack '$packId' source '$sourceId' has a placeholder " +
                    'or duplicate obligation.'
                )
            }
        }

        $rangesProperty = $source.PSObject.Properties['recordRanges']
        if ($null -eq $rangesProperty) {
            throw (
                "Pack '$packId' source '$sourceId' is missing required " +
                "property 'recordRanges'."
            )
        }
        $rangesValue = $rangesProperty.Value
        if ($rangesValue -isnot [Array] -or @($rangesValue).Count -eq 0) {
            throw (
                "Pack '$packId' source '$sourceId' must declare a non-empty " +
                'recordRanges array.'
            )
        }
        foreach ($range in @($rangesValue)) {
            Assert-ExactProperties -Object $range `
                -Expected @('firstRecord', 'lastRecord') `
                -Context "Pack '$packId' source '$sourceId' record range"
            $firstValue = Get-RequiredProperty -Object $range `
                -Name 'firstRecord' `
                -Context "Pack '$packId' source '$sourceId' record range"
            $lastValue = Get-RequiredProperty -Object $range `
                -Name 'lastRecord' `
                -Context "Pack '$packId' source '$sourceId' record range"
            if (($firstValue -isnot [int] -and $firstValue -isnot [long]) -or
                ($lastValue -isnot [int] -and $lastValue -isnot [long])) {
                throw (
                    "Pack '$packId' source '$sourceId' record range values " +
                    'must be integers.'
                )
            }
            $first = [int]$firstValue
            $last = [int]$lastValue
            if ($first -lt 1 -or $last -lt $first -or $last -gt $recordCount) {
                throw (
                    "Pack '$packId' source '$sourceId' record range " +
                    "$first..$last is outside 1..$recordCount."
                )
            }
            foreach ($record in $first..$last) {
                if (-not $coveredRecords.Add($record)) {
                    throw (
                        "Pack '$packId' record $record is covered by more " +
                        'than one source range.'
                    )
                }
            }
        }
    }

    if ($coveredRecords.Count -ne $recordCount) {
        $missing = @(
            foreach ($record in 1..$recordCount) {
                if (-not $coveredRecords.Contains($record)) { $record }
            }
        )
        throw (
            "Pack '$packId' rights coverage is incomplete. Missing records: " +
            (($missing | Select-Object -First 20) -join ', ') +
            $(if ($missing.Count -gt 20) { ', ...' } else { '' })
        )
    }

    $expectedCatalogLicense = @($sourceLicenses | Sort-Object) -join ' AND '
    if ($catalogLicense -cne $expectedCatalogLicense) {
        throw (
            "Pack '$packId' catalog license must be the deterministic AND " +
            "expression of its source licenses: '$expectedCatalogLicense'."
        )
    }
}

function Assert-PackRightsEvidence {
    param(
        [Parameter(Mandatory = $true)][object]$Catalog,
        [Parameter(Mandatory = $true)][object]$Evidence,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $fullRepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
    Assert-ExactProperties -Object $Evidence `
        -Expected @('schemaVersion', 'approvals') `
        -Context 'Pack rights-evidence manifest'
    $manifestSchema = [int](Get-RequiredProperty -Object $Evidence `
        -Name 'schemaVersion' -Context 'Pack rights-evidence manifest')
    if ($manifestSchema -notin @(1, 2)) {
        throw "Unsupported pack rights-evidence schema '$($Evidence.schemaVersion)'."
    }

    $catalogRevisionValue = Get-RequiredProperty -Object $Catalog `
        -Name 'revision' -Context 'Pack catalog'
    $catalogRevision = if ($null -eq $catalogRevisionValue) {
        ''
    }
    else {
        [string]$catalogRevisionValue
    }

    $packs = @(
        Get-RequiredProperty -Object $Catalog -Name 'packs' -Context 'Pack catalog'
    )
    $packById = New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::OrdinalIgnoreCase)
    $approvedPackCount = 0
    foreach ($pack in $packs) {
        $packId = [string](Get-RequiredProperty -Object $pack -Name 'id' `
            -Context 'Pack catalog entry')
        if ($packId -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
            $packById.ContainsKey($packId)) {
            throw "Pack rights validation found an invalid or duplicate pack ID: '$packId'."
        }
        $approval = $pack.PSObject.Properties['redistributionApproved']
        if ($null -eq $approval -or $approval.Value -isnot [bool]) {
            throw "Pack '$packId' must explicitly declare a boolean redistributionApproved value."
        }
        if ([bool]$approval.Value) {
            $approvedPackCount++
        }
        $packHash = [string](Get-RequiredProperty -Object $pack -Name 'sha256' `
            -Context "Pack '$packId'")
        if ($packHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Pack '$packId' has an invalid lowercase SHA-256."
        }
        $packCount = Get-RequiredProperty -Object $pack -Name 'count' `
            -Context "Pack '$packId'"
        if (($packCount -isnot [int] -and $packCount -isnot [long]) -or
            [long]$packCount -lt 1 -or [long]$packCount -gt 100000) {
            throw "Pack '$packId' has an invalid record count."
        }
        $packLicense = Get-RequiredProperty -Object $pack -Name 'license' `
            -Context "Pack '$packId'"
        Assert-BoundedText -Value $packLicense -Context "Pack '$packId' license" `
            -MinimumLength 2 -MaximumLength 200
        if ([bool]$approval.Value) {
            Assert-LicenseExpression -Value $packLicense `
                -Context "Approved pack '$packId' license"
        }
        $packById.Add($packId, $pack)
    }
    if ($approvedPackCount -eq 0 -and
        -not [string]::IsNullOrWhiteSpace($catalogRevision)) {
        throw (
            'A held-only pack catalog must not claim a retrievable revision; ' +
            'set revision to null or empty.'
        )
    }
    if ($approvedPackCount -gt 0 -and
        $catalogRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw (
            'A catalog with approved packs requires a lowercase, ' +
            'commit-pinned SHA-1 revision.'
        )
    }

    $approvalById = New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::OrdinalIgnoreCase)
    $approvalEntries = @(
        Get-RequiredProperty -Object $Evidence -Name 'approvals' `
            -Context 'Pack rights-evidence manifest'
    )
    if ($manifestSchema -eq 1 -and $approvalEntries.Count -ne 0) {
        throw (
            'Pack rights-evidence schema 1 is accepted only for the protected ' +
            'empty compatibility manifest; approval entries require schema 2.'
        )
    }
    foreach ($entry in $approvalEntries) {
        Assert-ExactProperties -Object $entry `
            -Expected @(
                'packId',
                'catalogRevision',
                'packSha256',
                'evidencePath',
                'evidenceSha256',
                'approvedBy',
                'approvedAtUtc'
            ) `
            -Context 'Pack rights approval'

        $packId = [string](Get-RequiredProperty -Object $entry -Name 'packId' `
            -Context 'Pack rights approval')
        if (-not $packById.ContainsKey($packId)) {
            throw "Pack rights evidence names unknown pack '$packId'."
        }
        if ($approvalById.ContainsKey($packId)) {
            throw "Pack rights evidence contains duplicate approval for '$packId'."
        }

        $pack = $packById[$packId]
        $entryRevision = [string]$entry.catalogRevision
        $entryPackHash = [string]$entry.packSha256
        if ($entryRevision -cne $catalogRevision) {
            throw "Pack '$packId' rights evidence is not bound to catalog revision $catalogRevision."
        }
        if ($entryPackHash -cne [string]$pack.sha256) {
            throw "Pack '$packId' rights evidence is not bound to its exact catalog SHA-256."
        }

        $relativeEvidencePath = [string]$entry.evidencePath
        if ($relativeEvidencePath -cnotmatch (
                '^docs/rights/[A-Za-z0-9._/-]+\.json$') -or
            $relativeEvidencePath -match '(?:^|/)\.\.(?:/|$)' -or
            [IO.Path]::IsPathRooted($relativeEvidencePath)) {
            throw (
                "Pack '$packId' rights evidence path is not a safe " +
                'docs/rights JSON path.'
            )
        }
        $fullEvidencePath = [IO.Path]::GetFullPath(
            (Join-Path $fullRepoRoot $relativeEvidencePath.Replace('/', '\')))
        if (-not $fullEvidencePath.StartsWith(
                $fullRepoRoot + '\docs\rights\',
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullEvidencePath -PathType Leaf)) {
            throw "Pack '$packId' rights evidence file is missing or outside docs/rights."
        }
        $declaredEvidenceHash = [string]$entry.evidenceSha256
        if ($declaredEvidenceHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Pack '$packId' rights evidence SHA-256 is invalid."
        }
        $observedEvidenceHash = (
            Get-FileHash -LiteralPath $fullEvidencePath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($observedEvidenceHash -cne $declaredEvidenceHash) {
            throw "Pack '$packId' rights evidence file hash does not match its manifest."
        }
        $evidenceInfo = Get-Item -LiteralPath $fullEvidencePath
        if ($evidenceInfo.Length -lt 64 -or $evidenceInfo.Length -gt (1024 * 1024)) {
            throw (
                "Pack '$packId' structured rights document must be " +
                '64..1048576 bytes.'
            )
        }
        $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
        try {
            $rightsJson = [IO.File]::ReadAllText(
                $fullEvidencePath,
                $strictUtf8)
            $rightsDocument = $rightsJson | ConvertFrom-Json
        }
        catch {
            throw (
                "Pack '$packId' rights evidence is not a strict UTF-8 " +
                "structured JSON document: $($_.Exception.Message)"
            )
        }
        Assert-StructuredRightsDocument -Document $rightsDocument `
            -Pack $pack -Approval $entry

        $approvedBy = [string]$entry.approvedBy
        if ([string]::IsNullOrWhiteSpace($approvedBy) -or
            $approvedBy.Length -gt 200 -or
            $approvedBy -match '[\x00-\x1f\x7f]') {
            throw "Pack '$packId' rights approval has an invalid approver."
        }
        $approvedAt = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact(
                [string]$entry.approvedAtUtc,
                'yyyy-MM-ddTHH:mm:ssZ',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal,
                [ref]$approvedAt)) {
            throw "Pack '$packId' rights approval timestamp must be UTC yyyy-MM-ddTHH:mm:ssZ."
        }

        $approvalById.Add($packId, $entry)
    }

    foreach ($entry in $packById.GetEnumerator()) {
        $approved = [bool]$entry.Value.redistributionApproved
        $hasEvidence = $approvalById.ContainsKey($entry.Key)
        if ($approved -and -not $hasEvidence) {
            throw "Pack '$($entry.Key)' is approved without commit- and hash-bound rights evidence."
        }
        if (-not $approved -and $hasEvidence) {
            throw "Pack '$($entry.Key)' has approval evidence but redistributionApproved is false."
        }
    }

    return [pscustomobject]@{
        PackCount = $packById.Count
        ApprovalCount = $approvalById.Count
    }
}

function Copy-JsonObject {
    param([Parameter(Mandatory = $true)][object]$InputObject)
    return ($InputObject | ConvertTo-Json -Depth 20 | ConvertFrom-Json)
}

function Assert-SelfTestThrows {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [string]$ExpectedMessage
    )

    $failure = $null
    try {
        & $Operation
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw "Pack rights-evidence self-test '$Name' did not fail closed."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedMessage) -and
        $failure.Exception.Message -notmatch $ExpectedMessage) {
        throw (
            "Pack rights-evidence self-test '$Name' failed for an " +
            "unexpected reason: $($failure.Exception.Message)"
        )
    }
}

function Invoke-SelfTest {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) (
        'DesktopPet-PackRights-' + [Guid]::NewGuid().ToString('N'))
    try {
        $rightsDirectory = Join-Path $scratch 'docs\rights'
        New-Item -ItemType Directory -Path $rightsDirectory -Force | Out-Null
        $reviewPath = Join-Path $rightsDirectory 'sample-rights.json'
        $writeRightsDocument = {
            param([Parameter(Mandatory = $true)][object]$Document)
            $json = ($Document | ConvertTo-Json -Depth 30) + "`n"
            [IO.File]::WriteAllText(
                $reviewPath,
                $json,
                (New-Object Text.UTF8Encoding($false)))
            return (
                Get-FileHash -LiteralPath $reviewPath -Algorithm SHA256
            ).Hash.ToLowerInvariant()
        }.GetNewClosure()

        $catalog = [pscustomobject]@{
            revision = $null
            packs = @(
                [pscustomobject]@{
                    id = 'sample'
                    sha256 = ('b' * 64)
                    count = 2
                    license = 'CC0-1.0'
                    dataSchema = 1
                    redistributionApproved = $false
                }
            )
        }
        $emptyEvidence = [pscustomobject]@{
            schemaVersion = 2
            approvals = @()
        }
        [void](Assert-PackRightsEvidence -Catalog $catalog `
            -Evidence $emptyEvidence -RepoRoot $scratch)

        $dataSchemaTwoCatalog = Copy-JsonObject $catalog
        $dataSchemaTwoCatalog.packs[0].dataSchema = 2
        [void](Assert-PackRightsEvidence -Catalog $dataSchemaTwoCatalog `
            -Evidence $emptyEvidence -RepoRoot $scratch)

        $legacyManifest = Copy-JsonObject $emptyEvidence
        $legacyManifest.schemaVersion = 1
        [void](Assert-PackRightsEvidence -Catalog $catalog `
            -Evidence $legacyManifest -RepoRoot $scratch)

        $unsupportedManifest = Copy-JsonObject $emptyEvidence
        $unsupportedManifest.schemaVersion = 3
        Assert-SelfTestThrows -Name 'unsupported manifest schema' `
            -ExpectedMessage 'Unsupported pack rights-evidence schema' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $catalog `
                    -Evidence $unsupportedManifest -RepoRoot $scratch)
            }

        $approvedCatalog = Copy-JsonObject $catalog
        $approvedCatalog.revision = ('a' * 40)
        $approvedCatalog.packs[0].redistributionApproved = $true
        Assert-SelfTestThrows -Name 'approval without evidence' `
            -ExpectedMessage 'approved without.*rights evidence' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $emptyEvidence -RepoRoot $scratch)
            }

        $validDocument = [pscustomobject][ordered]@{
            schemaVersion = 1
            packId = 'sample'
            catalogRevision = ('a' * 40)
            packSha256 = ('b' * 64)
            recordCount = 2
            catalogLicenseExpression = 'CC0-1.0'
            sources = @(
                [pscustomobject][ordered]@{
                    sourceId = 'sample/source-a'
                    sourceRepository = 'https://example.invalid/source-a'
                    sourceRevision = ('c' * 40)
                    licenseExpression = 'CC0-1.0'
                    redistributionGrant = (
                        'The source owner grants verbatim public redistribution ' +
                        'of the covered record in this pack.'
                    )
                    obligations = @(
                        'Retain the source identifier in the pack rights record.'
                    )
                    recordRanges = @(
                        [pscustomobject][ordered]@{
                            firstRecord = 1
                            lastRecord = 1
                        }
                    )
                },
                [pscustomobject][ordered]@{
                    sourceId = 'sample/source-b'
                    sourceRepository = 'https://example.invalid/source-b'
                    sourceRevision = ('d' * 40)
                    licenseExpression = 'CC0-1.0'
                    redistributionGrant = (
                        'The source owner grants verbatim public redistribution ' +
                        'of the covered record in this pack.'
                    )
                    obligations = @(
                        'Retain the source identifier in the pack rights record.'
                    )
                    recordRanges = @(
                        [pscustomobject][ordered]@{
                            firstRecord = 2
                            lastRecord = 2
                        }
                    )
                }
            )
        }
        $reviewHash = & $writeRightsDocument $validDocument

        $validEvidence = [pscustomobject]@{
            schemaVersion = 2
            approvals = @(
                [pscustomobject]@{
                    packId = 'sample'
                    catalogRevision = ('a' * 40)
                    packSha256 = ('b' * 64)
                    evidencePath = 'docs/rights/sample-rights.json'
                    evidenceSha256 = $reviewHash
                    approvedBy = 'release-review@example.invalid'
                    approvedAtUtc = '2026-07-29T12:00:00Z'
                }
            )
        }
        [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
            -Evidence $validEvidence -RepoRoot $scratch)

        $legacyApprovalEvidence = Copy-JsonObject $validEvidence
        $legacyApprovalEvidence.schemaVersion = 1
        Assert-SelfTestThrows -Name 'approval-bearing legacy manifest' `
            -ExpectedMessage 'approval entries require schema 2' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $legacyApprovalEvidence -RepoRoot $scratch)
            }

        $contentFreeDocument = [pscustomobject][ordered]@{
            review = (
                'Reviewed sample evidence without structured source rights ' +
                'or exact record coverage.'
            )
        }
        $contentFreeHash = & $writeRightsDocument $contentFreeDocument
        $contentFreeEvidence = Copy-JsonObject $validEvidence
        $contentFreeEvidence.approvals[0].evidenceSha256 = $contentFreeHash
        Assert-SelfTestThrows -Name 'content-free rights document' `
            -ExpectedMessage 'properties are not exact' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $contentFreeEvidence -RepoRoot $scratch)
            }

        $branchDocument = Copy-JsonObject $validDocument
        $branchDocument.sources[0].sourceRevision = 'master'
        $branchHash = & $writeRightsDocument $branchDocument
        $branchEvidence = Copy-JsonObject $validEvidence
        $branchEvidence.approvals[0].evidenceSha256 = $branchHash
        Assert-SelfTestThrows -Name 'mutable source revision' `
            -ExpectedMessage '40-character source revision' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $branchEvidence -RepoRoot $scratch)
            }

        $nonGrantDocument = Copy-JsonObject $validDocument
        $nonGrantDocument.catalogLicenseExpression = 'fair-use'
        $nonGrantDocument.sources[0].licenseExpression = 'fair-use'
        $nonGrantHash = & $writeRightsDocument $nonGrantDocument
        $nonGrantEvidence = Copy-JsonObject $validEvidence
        $nonGrantEvidence.approvals[0].evidenceSha256 = $nonGrantHash
        Assert-SelfTestThrows -Name 'non-grant license label' `
            -ExpectedMessage 'non-grant or placeholder label' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $nonGrantEvidence -RepoRoot $scratch)
            }

        $placeholderGrantDocument = Copy-JsonObject $validDocument
        $placeholderGrantDocument.sources[0].redistributionGrant = (
            'TODO: permission remains pending before public redistribution.'
        )
        $placeholderGrantHash = & $writeRightsDocument $placeholderGrantDocument
        $placeholderGrantEvidence = Copy-JsonObject $validEvidence
        $placeholderGrantEvidence.approvals[0].evidenceSha256 =
            $placeholderGrantHash
        Assert-SelfTestThrows -Name 'placeholder redistribution grant' `
            -ExpectedMessage 'non-grant or placeholder statement' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $placeholderGrantEvidence -RepoRoot $scratch)
            }

        $placeholderObligationDocument = Copy-JsonObject $validDocument
        $placeholderObligationDocument.sources[0].obligations[0] =
            'TODO: determine attribution requirements.'
        $placeholderObligationHash =
            & $writeRightsDocument $placeholderObligationDocument
        $placeholderObligationEvidence = Copy-JsonObject $validEvidence
        $placeholderObligationEvidence.approvals[0].evidenceSha256 =
            $placeholderObligationHash
        Assert-SelfTestThrows -Name 'placeholder obligation' `
            -ExpectedMessage 'placeholder or duplicate obligation' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $placeholderObligationEvidence -RepoRoot $scratch)
            }

        $overlapDocument = Copy-JsonObject $validDocument
        $overlapDocument.sources[1].recordRanges[0].firstRecord = 1
        $overlapHash = & $writeRightsDocument $overlapDocument
        $overlapEvidence = Copy-JsonObject $validEvidence
        $overlapEvidence.approvals[0].evidenceSha256 = $overlapHash
        Assert-SelfTestThrows -Name 'overlapping record coverage' `
            -ExpectedMessage 'covered by more than one source range' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $overlapEvidence -RepoRoot $scratch)
            }

        $incompleteCatalog = Copy-JsonObject $approvedCatalog
        $incompleteCatalog.packs[0].count = 3
        $incompleteDocument = Copy-JsonObject $validDocument
        $incompleteDocument.recordCount = 3
        $incompleteHash = & $writeRightsDocument $incompleteDocument
        $incompleteEvidence = Copy-JsonObject $validEvidence
        $incompleteEvidence.approvals[0].evidenceSha256 = $incompleteHash
        Assert-SelfTestThrows -Name 'incomplete record coverage' `
            -ExpectedMessage 'coverage is incomplete.*3' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $incompleteCatalog `
                    -Evidence $incompleteEvidence -RepoRoot $scratch)
            }

        [void](& $writeRightsDocument $validDocument)
        $catalogLicenseMismatch = Copy-JsonObject $approvedCatalog
        $catalogLicenseMismatch.packs[0].license = 'MIT'
        Assert-SelfTestThrows -Name 'catalog license mismatch' `
            -ExpectedMessage 'does not exactly match the catalog license' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $catalogLicenseMismatch `
                    -Evidence $validEvidence -RepoRoot $scratch)
            }

        $wrongHashEvidence = Copy-JsonObject $validEvidence
        $wrongHashEvidence.approvals[0].packSha256 = ('c' * 64)
        Assert-SelfTestThrows -Name 'wrong pack hash' `
            -ExpectedMessage 'not bound to its exact catalog SHA-256' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $approvedCatalog `
                    -Evidence $wrongHashEvidence -RepoRoot $scratch)
            }

        Assert-SelfTestThrows -Name 'held approval evidence' `
            -ExpectedMessage 'not bound to catalog revision' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $catalog `
                    -Evidence $validEvidence -RepoRoot $scratch)
            }

        $heldWithRevision = Copy-JsonObject $catalog
        $heldWithRevision.revision = ('a' * 40)
        Assert-SelfTestThrows -Name 'held catalog stale revision' `
            -ExpectedMessage 'held-only pack catalog' `
            -Operation {
                [void](Assert-PackRightsEvidence -Catalog $heldWithRevision `
                    -Evidence $emptyEvidence -RepoRoot $scratch)
            }
    }
    finally {
        if (Test-Path -LiteralPath $scratch) {
            Remove-Item -LiteralPath $scratch -Recurse -Force
        }
    }

    Write-Host (
        'Pack rights-evidence fail-closed self-tests passed: structured ' +
        'source identity, immutable revisions, concrete grants/licenses, ' +
        'obligations, and exact non-overlapping record coverage are required.'
    ) -ForegroundColor Green
}

if ($SelfTest) {
    Invoke-SelfTest
}

if (-not [string]::IsNullOrWhiteSpace($CatalogPath) -or
    -not [string]::IsNullOrWhiteSpace($EvidencePath) -or
    -not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    foreach ($requiredPath in @($CatalogPath, $EvidencePath)) {
        if ([string]::IsNullOrWhiteSpace($requiredPath) -or
            -not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Pack rights validation input is missing: '$requiredPath'."
        }
    }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot) -or
        -not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        throw "Pack rights validation repository root is missing: '$RepositoryRoot'."
    }

    $catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
    $result = Assert-PackRightsEvidence -Catalog $catalog -Evidence $evidence `
        -RepoRoot $RepositoryRoot
    Write-Host (
        'Pack rights evidence verified: {0} catalog packs, {1} exact approvals.' -f
        $result.PackCount,
        $result.ApprovalCount) -ForegroundColor Green
}
elseif (-not $SelfTest) {
    throw 'Specify -SelfTest or all of -CatalogPath, -EvidencePath, and -RepositoryRoot.'
}
