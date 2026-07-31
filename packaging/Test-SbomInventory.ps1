#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [string]$InventoryPath,
    [string]$RuntimeManifestPath,
    [string]$RuntimeRoot,
    [string]$LockFilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
. (Join-Path $scriptDirectory 'StagingPathSafety.ps1')
. (Join-Path $scriptDirectory 'SpdxDocumentIdentity.ps1')

$runtimeInputs =
    New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::OrdinalIgnoreCase)
$sbomInput = $null
try {
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath =
        Join-Path $scriptDirectory 'third-party-packages.json'
}
if ([string]::IsNullOrWhiteSpace($RuntimeManifestPath)) {
    $RuntimeManifestPath =
        Join-Path $scriptDirectory 'runtime-files.txt'
}
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $scriptDirectory (
        '..\build\DesktopPetPortable\bin\Release\x64')
}
if ([string]::IsNullOrWhiteSpace($LockFilePath)) {
    $LockFilePath =
        Join-Path $scriptDirectory '..\src\packages.lock.json'
}

$resolvedSbom = (Resolve-Path -LiteralPath $SbomPath).Path
$resolvedInventory = (Resolve-Path -LiteralPath $InventoryPath).Path
$resolvedManifest = (Resolve-Path -LiteralPath $RuntimeManifestPath).Path
$resolvedRuntime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$resolvedLockFile = (Resolve-Path -LiteralPath $LockFilePath).Path
$sbomInput = Open-DesktopPetValidatedInputFile `
    -Path $resolvedSbom `
    -Root (Split-Path -Parent $resolvedSbom)
$sbomText = $sbomInput.ReadAllTextUtf8(128MB)
$sbomSha256 = $sbomInput.ComputeHash('SHA256')
$sbom = $sbomText | ConvertFrom-Json
$inventory = Get-Content -LiteralPath $resolvedInventory -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath $resolvedLockFile -Raw | ConvertFrom-Json

if ([string]$sbom.spdxVersion -cne 'SPDX-2.3') {
    throw "Expected an SPDX 2.3 JSON document; found '$($sbom.spdxVersion)'."
}
if ([string]$sbom.dataLicense -cne 'CC0-1.0') {
    throw "Expected SPDX dataLicense CC0-1.0; found '$($sbom.dataLicense)'."
}
$creationInfoProperty = $sbom.PSObject.Properties['creationInfo']
if ($null -eq $creationInfoProperty -or
    $null -eq $creationInfoProperty.Value) {
    throw 'The SPDX document does not contain creationInfo.'
}
$created = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse(
        [string]$creationInfoProperty.Value.created,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$created)) {
    throw 'The SPDX document creationInfo.created value is not a valid timestamp.'
}
$creators = @(
    @($creationInfoProperty.Value.creators) |
        ForEach-Object { [string]$_ }
)
if ($creators.Count -lt 2 -or
    @($creators | Where-Object {
        $_ -notmatch '^(Person|Organization|Tool): .+'
    }).Count -gt 0 -or
    $creators -cnotcontains 'Tool: DesktopPet SPDX runtime normalizer') {
    throw (
        'SPDX creators must preserve generator attribution and identify the ' +
        'DesktopPet runtime normalizer.'
    )
}
$canonicalDocumentName = 'DesktopPet-AI-Edition-Windows-x64-runtime'
$canonicalDocumentRootId =
    'SPDXRef-Package-DesktopPet-AI-Edition-Runtime'
$canonicalDocumentRootName =
    'DesktopPet AI Edition Windows x64 runtime'
if ([string]$sbom.name -cne $canonicalDocumentName) {
    throw (
        'The SPDX document name is not the canonical portable runtime name: ' +
        "'$($sbom.name)'."
    )
}
if ([int]$inventory.schemaVersion -ne 1) {
    throw "Unsupported third-party inventory schema: $($inventory.schemaVersion)"
}
$targetFramework = [string]$inventory.targetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw 'Third-party inventory does not declare targetFramework.'
}

# Exact relationshipType enum from the SPDX 2.3 JSON schema.
$spdx23RelationshipTypes = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($typeName in @(
        'AMENDS',
        'ANCESTOR_OF',
        'BUILD_DEPENDENCY_OF',
        'BUILD_TOOL_OF',
        'CONTAINED_BY',
        'CONTAINS',
        'COPY_OF',
        'DATA_FILE_OF',
        'DEPENDENCY_MANIFEST_OF',
        'DEPENDENCY_OF',
        'DEPENDS_ON',
        'DESCENDANT_OF',
        'DESCRIBED_BY',
        'DESCRIBES',
        'DEV_DEPENDENCY_OF',
        'DEV_TOOL_OF',
        'DISTRIBUTION_ARTIFACT',
        'DOCUMENTATION_OF',
        'DYNAMIC_LINK',
        'EXAMPLE_OF',
        'EXPANDED_FROM_ARCHIVE',
        'FILE_ADDED',
        'FILE_DELETED',
        'FILE_MODIFIED',
        'GENERATED_FROM',
        'GENERATES',
        'HAS_PREREQUISITE',
        'METAFILE_OF',
        'OPTIONAL_COMPONENT_OF',
        'OPTIONAL_DEPENDENCY_OF',
        'OTHER',
        'PACKAGE_OF',
        'PATCH_APPLIED',
        'PATCH_FOR',
        'PREREQUISITE_FOR',
        'PROVIDED_DEPENDENCY_OF',
        'REQUIREMENT_DESCRIPTION_FOR',
        'RUNTIME_DEPENDENCY_OF',
        'SPECIFICATION_FOR',
        'STATIC_LINK',
        'TEST_CASE_OF',
        'TEST_DEPENDENCY_OF',
        'TEST_OF',
        'TEST_TOOL_OF',
        'VARIANT_OF'
    )) {
    [void]$spdx23RelationshipTypes.Add($typeName)
}

$expectedAll = @{}
$expectedShipped = @{}
$expectedLicenseByIdentity = @{}
$expectedInventoryPackageByIdentity = @{}
$expectedRuntimeFilesByIdentity = @{}
$expectedRootRelationshipByIdentity = @{}
$allowedInventoryRootRelationships = @(
    'DEPENDS_ON',
    'BUILD_TOOL_OF',
    'BUILD_DEPENDENCY_OF'
)
foreach ($package in @($inventory.packages)) {
    $name = [string]$package.name
    $version = [string]$package.version
    $license = [string]$package.license
    $key = "$($name.ToLowerInvariant())@$version"
    if ([string]::IsNullOrWhiteSpace($name) -or
        [string]::IsNullOrWhiteSpace($version) -or
        [string]::IsNullOrWhiteSpace($license) -or
        $expectedAll.ContainsKey($key)) {
        throw "Third-party inventory contains invalid or duplicate identity '$key'."
    }
    $runtimeFiles = @(
        @($package.runtimeFiles) | ForEach-Object { [string]$_ })
    if (@($runtimeFiles | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
        throw "Third-party inventory package '$key' contains duplicate runtime files."
    }
    foreach ($runtimeFile in $runtimeFiles) {
        if ([string]::IsNullOrWhiteSpace($runtimeFile) -or
            [IO.Path]::IsPathRooted($runtimeFile) -or
            $runtimeFile -ne [IO.Path]::GetFileName($runtimeFile)) {
            throw "Third-party inventory package '$key' contains unsafe runtime file '$runtimeFile'."
        }
    }
    $rootRelationshipProperty =
        $package.PSObject.Properties['relationshipToRoot']
    $rootRelationship = if ($null -eq $rootRelationshipProperty -or
        [string]::IsNullOrWhiteSpace(
            [string]$rootRelationshipProperty.Value)) {
        'DEPENDS_ON'
    }
    else {
        [string]$rootRelationshipProperty.Value
    }
    if ($rootRelationship -cnotin $allowedInventoryRootRelationships) {
        throw (
            "Third-party inventory package '$key' declares unsupported " +
            "relationshipToRoot '$rootRelationship'."
        )
    }
    if ($rootRelationship -cne 'DEPENDS_ON' -and
        $runtimeFiles.Count -ne 0) {
        throw (
            "Build-only third-party package '$key' cannot own runtime files."
        )
    }
    $expectedAll[$key] = $true
    $expectedLicenseByIdentity[$key] = $license
    $expectedInventoryPackageByIdentity[$key] = $package
    $expectedRuntimeFilesByIdentity[$key] = $runtimeFiles
    $expectedRootRelationshipByIdentity[$key] = $rootRelationship
    if ($runtimeFiles.Count -gt 0) {
        $expectedShipped[$key] = $true
    }
}
if ($expectedAll.Count -eq 0) {
    throw 'Third-party package inventory is empty.'
}

$lockTargetProperty =
    $lock.dependencies.PSObject.Properties[$targetFramework]
if ($null -eq $lockTargetProperty) {
    throw "Canonical lock file does not contain target '$targetFramework'."
}
$lockedAll = @{}
$lockedByIdentity = @{}
$lockedIdentityByName = @{}
$directLockedIdentities = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($property in $lockTargetProperty.Value.PSObject.Properties) {
    if ([string]$property.Value.type -ceq 'Project') { continue }
    $version = [string]$property.Value.resolved
    $key = "$(([string]$property.Name).ToLowerInvariant())@$version"
    if ([string]::IsNullOrWhiteSpace($version) -or
        $lockedAll.ContainsKey($key)) {
        throw "Canonical lock file contains invalid or duplicate identity '$key'."
    }
    $lockedAll[$key] = $true
    $lockedByIdentity[$key] = $property.Value
    $lowerName = ([string]$property.Name).ToLowerInvariant()
    if ($lockedIdentityByName.ContainsKey($lowerName)) {
        throw "Canonical lock file contains multiple versions of '$($property.Name)'."
    }
    $lockedIdentityByName[$lowerName] = $key
    if ([string]$property.Value.type -ceq 'Direct') {
        [void]$directLockedIdentities.Add($key)
    }
}
$lockDifference = @(
    Compare-Object `
        @($expectedAll.Keys | Sort-Object) `
        @($lockedAll.Keys | Sort-Object)
)
if ($lockDifference.Count -gt 0) {
    $detail = ($lockDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "Canonical lock file and third-party inventory disagree: $detail"
}
foreach ($identity in $expectedRootRelationshipByIdentity.Keys) {
    if ([string]$expectedRootRelationshipByIdentity[$identity] -cne
            'DEPENDS_ON' -and
        -not $directLockedIdentities.Contains($identity)) {
        throw (
            "Build-only third-party package '$identity' must be a direct " +
            "locked build input."
        )
    }
}

$observedCanonical = @{}
$observedCanonicalPackage = @{}
$nonNugetPackageIds = @()
$allSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$packageSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$packageFilesAnalyzedById =
    New-Object 'Collections.Generic.Dictionary[string,bool]' (
        [StringComparer]::Ordinal)
$externalDocumentRefsProperty =
    $sbom.PSObject.Properties['externalDocumentRefs']
if ($null -ne $externalDocumentRefsProperty -and
    @($externalDocumentRefsProperty.Value).Count -gt 0) {
    throw 'The release SBOM must be self-contained and cannot declare external SPDX documents.'
}
$documentSpdxId = [string]$sbom.SPDXID
if ($documentSpdxId -cne 'SPDXRef-DOCUMENT' -or
    -not $allSpdxIds.Add($documentSpdxId)) {
    throw "The SPDX document ID must be exactly 'SPDXRef-DOCUMENT'; found '$documentSpdxId'."
}
foreach ($package in @($sbom.packages)) {
    $spdxId = [string]$package.SPDXID
    if ($spdxId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or
        -not $allSpdxIds.Add($spdxId)) {
        throw "The SPDX SBOM contains an empty or duplicate package SPDX ID: '$spdxId'."
    }
    [void]$packageSpdxIds.Add($spdxId)
    $filesAnalyzedProperty =
        $package.PSObject.Properties['filesAnalyzed']
    if ($null -eq $filesAnalyzedProperty -or
        $filesAnalyzedProperty.Value -isnot [bool]) {
        throw "SPDX package '$spdxId' must declare boolean filesAnalyzed."
    }
    $filesAnalyzed = [bool]$filesAnalyzedProperty.Value
    $packageFilesAnalyzedById.Add($spdxId, $filesAnalyzed)
    $externalRefsProperty = $package.PSObject.Properties['externalRefs']
    $externalRefs = if ($null -ne $externalRefsProperty) {
        @($externalRefsProperty.Value)
    } else { @() }
    $nugetPurls = @()
    foreach ($reference in $externalRefs) {
        $locator = [string]$reference.referenceLocator
        if ($locator -match '^pkg:nuget/') {
            if ([string]$reference.referenceCategory -cne 'PACKAGE-MANAGER' -or
                [string]$reference.referenceType -cne 'purl') {
                throw "NuGet purl '$locator' is not labeled as a PACKAGE-MANAGER purl."
            }
            $nugetPurls += $locator
        }
    }
    if ($nugetPurls.Count -eq 0) {
        $nonNugetPackageIds += $spdxId
        continue
    }
    if ($nugetPurls.Count -ne 1) {
        throw "Package '$spdxId' does not contain exactly one canonical NuGet purl."
    }
    $purlMatch = [regex]::Match(
        $nugetPurls[0],
        '^pkg:nuget/(?<name>[^@?#]+)@(?<version>[^?#]+)$')
    if (-not $purlMatch.Success) {
        throw "Package '$spdxId' contains a non-canonical NuGet purl."
    }
    if ($filesAnalyzed) {
        throw (
            "Canonical metadata-only NuGet package '$spdxId' must set " +
            'filesAnalyzed=false.'
        )
    }
    if ($null -ne
        $package.PSObject.Properties['packageVerificationCode']) {
        throw (
            "Canonical metadata-only NuGet package '$spdxId' must not " +
            'declare packageVerificationCode.'
        )
    }
    $name = [Uri]::UnescapeDataString($purlMatch.Groups['name'].Value)
    $version =
        [Uri]::UnescapeDataString($purlMatch.Groups['version'].Value)
    $key = "$($name.ToLowerInvariant())@$version"
    if (-not $expectedAll.ContainsKey($key)) {
        throw "The SPDX SBOM contains unrecognized NuGet identity '$key'."
    }
    if ($observedCanonical.ContainsKey($key)) {
        throw "The SPDX SBOM contains duplicate canonical NuGet identity '$key'."
    }
    $inventoryPackage = $expectedInventoryPackageByIdentity[$key]
    if ([string]$package.name -cne [string]$inventoryPackage.name -or
        [string]$package.versionInfo -cne
            [string]$inventoryPackage.version) {
        throw "The SPDX package fields do not match canonical NuGet identity '$key'."
    }
    $sourceInfoProperty = $package.PSObject.Properties['sourceInfo']
    $sourceInfo = if ($null -ne $sourceInfoProperty) {
        [string]$sourceInfoProperty.Value
    }
    else { '' }
    if ($sourceInfo -notmatch '(?i)packages\.lock\.json' -or
        $sourceInfo -notmatch '(?i)third-party-packages\.json') {
        throw "The SPDX package '$key' is not marked as lock/inventory canonical."
    }
    $expectedLicense = [string]$expectedLicenseByIdentity[$key]
    $declaredProperty =
        $package.PSObject.Properties['licenseDeclared']
    $concludedProperty =
        $package.PSObject.Properties['licenseConcluded']
    $declaredLicense = if ($null -ne $declaredProperty) {
        [string]$declaredProperty.Value
    }
    else { '' }
    $concludedLicense = if ($null -ne $concludedProperty) {
        [string]$concludedProperty.Value
    }
    else { '' }
    if ($declaredLicense -cne $expectedLicense -or
        $concludedLicense -cne $expectedLicense) {
        throw "The SPDX license metadata for canonical package '$key' must be '$expectedLicense'."
    }
    $flatName = ([string]$inventoryPackage.name).ToLowerInvariant()
    $flatVersion = ([string]$inventoryPackage.version).ToLowerInvariant()
    $expectedDownloadLocation = (
        'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg' -f
        [Uri]::EscapeDataString($flatName),
        [Uri]::EscapeDataString($flatVersion)
    )
    if ([string]$package.downloadLocation -cne
        $expectedDownloadLocation) {
        throw (
            "The SPDX downloadLocation for '$key' is not the exact NuGet " +
            'flat-container package URL.'
        )
    }
    $contentHash = [string]$lockedByIdentity[$key].contentHash
    try {
        $contentHashBytes = [Convert]::FromBase64String($contentHash)
    }
    catch {
        throw "Canonical lock contentHash for '$key' is not base64."
    }
    if ($contentHashBytes.Length -ne 64) {
        throw "Canonical lock contentHash for '$key' is not SHA-512."
    }
    $expectedSha512 = ([BitConverter]::ToString(
        $contentHashBytes)).Replace('-', '').ToLowerInvariant()
    $sha512Checksums = @(
        @($package.checksums) |
            Where-Object { [string]$_.algorithm -ceq 'SHA512' }
    )
    if ($sha512Checksums.Count -ne 1 -or
        [string]$sha512Checksums[0].checksumValue -cne
            $expectedSha512) {
        throw (
            "The SPDX SHA512 checksum for '$key' does not match the exact " +
            'packages.lock.json contentHash.'
        )
    }
    $observedCanonical[$key] = $true
    $observedCanonicalPackage[$key] = $package
}

$canonicalDifference = @(
    Compare-Object `
        @($expectedAll.Keys | Sort-Object) `
        @($observedCanonical.Keys | Sort-Object)
)
if ($canonicalDifference.Count -gt 0) {
    $detail = ($canonicalDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "The SPDX canonical package metadata and inventory disagree: $detail"
}
if ($nonNugetPackageIds.Count -ne 1 -or
    @($sbom.packages).Count -ne ($expectedAll.Count + 1)) {
    throw (
        'The SPDX SBOM must contain exactly one non-NuGet document root plus ' +
        "$($expectedAll.Count) canonical inventory packages."
    )
}
$documentRootCandidateId = [string]$nonNugetPackageIds[0]
if (-not $packageFilesAnalyzedById[$documentRootCandidateId]) {
    throw 'The SPDX document-root package must set filesAnalyzed=true.'
}

$filesProperty = $sbom.PSObject.Properties['files']
if ($null -eq $filesProperty) {
    throw 'The SPDX SBOM has no file evidence for the canonical runtime manifest.'
}
$manifest = @(
    Get-Content -LiteralPath $resolvedManifest |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($manifest.Count -eq 0 -or
    @($manifest | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Runtime manifest is empty or contains duplicate entries.'
}
$expectedEvidence = New-Object 'Collections.Generic.Dictionary[string,object]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($name in $manifest) {
    if ([IO.Path]::IsPathRooted($name) -or $name -ne [IO.Path]::GetFileName($name)) {
        throw "Runtime manifest entry is not a plain file name: $name"
    }
    $path = Join-Path $resolvedRuntime $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Canonical runtime file is missing while validating the SBOM: $path"
    }
    $input = Open-DesktopPetValidatedInputFile `
        -Path $path `
        -Root $resolvedRuntime
    $runtimeInputs.Add($name, $input)
    $expectedEvidence.Add($name, $input)
}
$observedFiles = New-Object 'Collections.Generic.Dictionary[string,object]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($filesProperty.Value)) {
    $fileNameProperty = $file.PSObject.Properties['fileName']
    if ($null -eq $fileNameProperty) {
        throw 'The SPDX SBOM contains a file evidence entry without fileName.'
    }
    $fileName = [IO.Path]::GetFileName(([string]$fileNameProperty.Value).Replace('/', '\'))
    if ([string]::IsNullOrWhiteSpace($fileName) -or $observedFiles.ContainsKey($fileName)) {
        throw "The SPDX SBOM contains an empty or duplicate file evidence name: '$fileName'."
    }
    if ([string]$fileNameProperty.Value -cne "./$fileName") {
        throw "The SPDX file evidence path is not portable and manifest-relative: '$($fileNameProperty.Value)'."
    }
    $spdxId = [string]$file.SPDXID
    if ($spdxId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or
        -not $allSpdxIds.Add($spdxId)) {
        throw "The SPDX SBOM contains an empty or duplicate file SPDX ID: '$spdxId'."
    }
    $observedFiles.Add($fileName, $file)
}
$fileDifference = @(
    Compare-Object @($expectedEvidence.Keys | Sort-Object) @($observedFiles.Keys | Sort-Object)
)
if ($fileDifference.Count -gt 0) {
    $detail = ($fileDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "The SPDX file evidence is not the exact runtime manifest: $detail"
}
$verificationSha1Values = @()
foreach ($entry in $expectedEvidence.GetEnumerator()) {
    $checksumsProperty = $observedFiles[$entry.Key].PSObject.Properties['checksums']
    $allChecksums = @(
        if ($null -ne $checksumsProperty) {
            @($checksumsProperty.Value)
        }
    )
    $sha1Checksums = @(
        $allChecksums |
            Where-Object { [string]$_.algorithm -ieq 'SHA1' }
    )
    $sha256Checksums = @(
        $allChecksums |
            Where-Object { [string]$_.algorithm -ieq 'SHA256' }
    )
    if ($allChecksums.Count -ne 2 -or
        $sha1Checksums.Count -ne 1 -or
        $sha256Checksums.Count -ne 1) {
        throw (
            "The SPDX file evidence for '$($entry.Key)' must contain exactly " +
            'one SHA-1 and one SHA-256.'
        )
    }
    $expectedSha1 =
        $entry.Value.ComputeHash('SHA1').ToLowerInvariant()
    $expectedSha256 =
        $entry.Value.ComputeHash('SHA256').ToLowerInvariant()
    if ([string]$sha1Checksums[0].checksumValue -cne $expectedSha1) {
        throw "The SPDX SHA-1 for '$($entry.Key)' does not match the canonical input."
    }
    if ([string]$sha256Checksums[0].checksumValue -cne $expectedSha256) {
        throw "The SPDX SHA-256 for '$($entry.Key)' does not match the canonical input."
    }
    $verificationSha1Values += $expectedSha1
}
$verificationSha1Values = [string[]]@($verificationSha1Values)
[Array]::Sort($verificationSha1Values, [StringComparer]::Ordinal)
$verificationCodeAlgorithm = [Security.Cryptography.SHA1]::Create()
try {
    $expectedPackageVerificationCode = ([BitConverter]::ToString(
        $verificationCodeAlgorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes(
                ($verificationSha1Values -join ''))))).
        Replace('-', '').ToLowerInvariant()
}
finally {
    $verificationCodeAlgorithm.Dispose()
}

$expectedRuntimeFiles = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::OrdinalIgnoreCase)
$runtimeFileOwner = New-Object 'Collections.Generic.Dictionary[string,string]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($identity in $expectedRuntimeFilesByIdentity.Keys) {
    foreach ($runtimeFile in @($expectedRuntimeFilesByIdentity[$identity])) {
        if (-not $expectedRuntimeFiles.Add($runtimeFile)) {
            throw (
                "Third-party runtime file '$runtimeFile' is mapped to both " +
                "'$($runtimeFileOwner[$runtimeFile])' and '$identity'."
            )
        }
        $runtimeFileOwner.Add($runtimeFile, $identity)
    }
}
$missingRuntimeFiles = @(
    $expectedRuntimeFiles |
        Where-Object { -not $observedFiles.ContainsKey([string]$_) } |
        Sort-Object
)
if ($missingRuntimeFiles.Count -gt 0) {
    throw "The SPDX SBOM is missing shipped runtime file evidence: $($missingRuntimeFiles -join ', ')"
}

$relationshipsProperty = $sbom.PSObject.Properties['relationships']
if ($null -eq $relationshipsProperty -or
    @($relationshipsProperty.Value).Count -eq 0) {
    throw 'The SPDX SBOM has no relationships connecting its document, packages, and files.'
}
$relationshipKeys = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$fileSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($file in @($filesProperty.Value)) {
    [void]$fileSpdxIds.Add([string]$file.SPDXID)
}
foreach ($relationship in @($relationshipsProperty.Value)) {
    $sourceId = [string]$relationship.spdxElementId
    $relationshipType = [string]$relationship.relationshipType
    $targetId = [string]$relationship.relatedSpdxElement
    if ([string]::IsNullOrWhiteSpace($sourceId) -or
        [string]::IsNullOrWhiteSpace($relationshipType) -or
        [string]::IsNullOrWhiteSpace($targetId)) {
        throw 'The SPDX SBOM contains a relationship with an empty source, type, or target.'
    }
    if (-not $spdx23RelationshipTypes.Contains($relationshipType)) {
        throw "The SPDX SBOM contains an unsupported SPDX 2.3 relationship type: '$relationshipType'."
    }
    $relationshipKey = "$sourceId|$relationshipType|$targetId"
    if (-not $relationshipKeys.Add($relationshipKey)) {
        throw "The SPDX SBOM contains a duplicate relationship: '$relationshipKey'."
    }
    foreach ($endpoint in @($sourceId, $targetId)) {
        if (-not $allSpdxIds.Contains($endpoint)) {
            throw "The SPDX SBOM contains a dangling relationship endpoint: '$endpoint'."
        }
    }
    $falsePackageContainsFile = (
        $relationshipType -ceq 'CONTAINS' -and
        $packageFilesAnalyzedById.ContainsKey($sourceId) -and
        -not $packageFilesAnalyzedById[$sourceId] -and
        $fileSpdxIds.Contains($targetId))
    $fileContainedByFalsePackage = (
        $relationshipType -ceq 'CONTAINED_BY' -and
        $fileSpdxIds.Contains($sourceId) -and
        $packageFilesAnalyzedById.ContainsKey($targetId) -and
        -not $packageFilesAnalyzedById[$targetId])
    if ($falsePackageContainsFile -or $fileContainedByFalsePackage) {
        $packageId = if ($falsePackageContainsFile) {
            $sourceId
        }
        else {
            $targetId
        }
        throw (
            "SPDX package '$packageId' has filesAnalyzed=false and therefore " +
            'must not contain files.'
        )
    }
}
$describedRoots = @(
    @($relationshipsProperty.Value) |
        Where-Object {
            [string]$_.spdxElementId -ceq $documentSpdxId -and
            [string]$_.relationshipType -ceq 'DESCRIBES'
        } |
        ForEach-Object { [string]$_.relatedSpdxElement }
)
if ($describedRoots.Count -ne 1 -or
    -not $packageSpdxIds.Contains($describedRoots[0])) {
    throw 'The SPDX document must DESCRIBE exactly one local package root.'
}
$documentRootId = $describedRoots[0]
if ([string]$documentRootId -cne $documentRootCandidateId) {
    throw (
        'The SPDX document root must be the sole non-NuGet package; found ' +
        "'$documentRootId' instead of '$documentRootCandidateId'."
    )
}
if ([string]$documentRootId -cne $canonicalDocumentRootId) {
    throw (
        'The SPDX document root does not use the canonical portable ID: ' +
        "'$documentRootId'."
    )
}
$documentRootPackage = @(
    @($sbom.packages) |
        Where-Object {
            [string]$_.SPDXID -ceq $documentRootId
        }
)
if ($documentRootPackage.Count -ne 1 -or
    [string]$documentRootPackage[0].name -cne
        $canonicalDocumentRootName) {
    throw 'The SPDX document root does not use the canonical portable name.'
}
if (-not $packageFilesAnalyzedById[$documentRootId]) {
    throw 'The SPDX document-root package must set filesAnalyzed=true.'
}
$verificationCodeProperty =
    $documentRootPackage[0].PSObject.Properties['packageVerificationCode']
$verificationCodeValue = if ($null -ne $verificationCodeProperty) {
    [string]$verificationCodeProperty.Value.packageVerificationCodeValue
}
else {
    ''
}
$excludedFilesProperty = if ($null -ne $verificationCodeProperty) {
    $verificationCodeProperty.Value.PSObject.Properties[
        'packageVerificationCodeExcludedFiles']
}
else {
    $null
}
if ($verificationCodeValue -cne $expectedPackageVerificationCode -or
    ($null -ne $excludedFilesProperty -and
        @($excludedFilesProperty.Value).Count -ne 0)) {
    throw (
        'The SPDX document-root packageVerificationCode does not match the ' +
        'canonical SHA-1 file evidence.'
    )
}
$rootContainedIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($relationship in @($relationshipsProperty.Value)) {
    if ([string]$relationship.spdxElementId -ceq $documentRootId -and
        [string]$relationship.relationshipType -ceq 'CONTAINS') {
        [void]$rootContainedIds.Add(
            [string]$relationship.relatedSpdxElement)
    }
}
foreach ($file in @($filesProperty.Value)) {
    if (-not $rootContainedIds.Contains([string]$file.SPDXID)) {
        throw "The SPDX document root does not CONTAIN runtime file '$($file.fileName)'."
    }
}
if (-not $rootContainedIds.SetEquals($fileSpdxIds)) {
    throw 'The SPDX document root must CONTAIN exactly the canonical runtime files.'
}

$canonicalPackageIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$expectedPackageFileEdges = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$expectedRootRelationships = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$expectedPackageDependencies = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($identity in $expectedAll.Keys) {
    $package = $observedCanonicalPackage[$identity]
    $packageId = [string]$package.SPDXID
    [void]$canonicalPackageIds.Add($packageId)
    if ($directLockedIdentities.Contains($identity)) {
        $rootRelationship =
            [string]$expectedRootRelationshipByIdentity[$identity]
        if ($rootRelationship -ceq 'DEPENDS_ON') {
            [void]$expectedRootRelationships.Add(
                "$documentRootId|DEPENDS_ON|$packageId")
        }
        else {
            [void]$expectedRootRelationships.Add(
                "$packageId|$rootRelationship|$documentRootId")
        }
    }
    $dependenciesProperty =
        $lockedByIdentity[$identity].PSObject.Properties['dependencies']
    if ($null -ne $dependenciesProperty) {
        foreach ($dependencyProperty in
            $dependenciesProperty.Value.PSObject.Properties) {
            $dependencyName =
                ([string]$dependencyProperty.Name).ToLowerInvariant()
            if (-not $lockedIdentityByName.ContainsKey($dependencyName)) {
                throw (
                    "Locked dependency '$($dependencyProperty.Name)' from " +
                    "'$identity' is absent from the canonical target."
                )
            }
            $dependencyIdentity =
                $lockedIdentityByName[$dependencyName]
            $dependencyId = [string]$observedCanonicalPackage[
                $dependencyIdentity].SPDXID
            [void]$expectedPackageDependencies.Add(
                "$packageId|DEPENDS_ON|$dependencyId")
        }
    }
    foreach ($runtimeFile in @(
            $expectedRuntimeFilesByIdentity[$identity])) {
        $fileId = [string]$observedFiles[$runtimeFile].SPDXID
        [void]$expectedPackageFileEdges.Add(
            "$fileId|GENERATED_FROM|$packageId")
    }
}
$actualRootRelationships = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$actualPackageFileEdges = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$actualPackageDependencies = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($relationship in @($relationshipsProperty.Value)) {
    $sourceId = [string]$relationship.spdxElementId
    $type = [string]$relationship.relationshipType
    $targetId = [string]$relationship.relatedSpdxElement
    if (($sourceId -ceq $documentRootId -and
            $canonicalPackageIds.Contains($targetId)) -or
        ($targetId -ceq $documentRootId -and
            $canonicalPackageIds.Contains($sourceId))) {
        [void]$actualRootRelationships.Add(
            "$sourceId|$type|$targetId")
    }
    if ($canonicalPackageIds.Contains($sourceId) -and
        $canonicalPackageIds.Contains($targetId)) {
        [void]$actualPackageDependencies.Add(
            "$sourceId|$type|$targetId")
    }
    $connectsCanonicalPackageAndFile = (
        ($canonicalPackageIds.Contains($sourceId) -and
            $fileSpdxIds.Contains($targetId)) -or
        ($fileSpdxIds.Contains($sourceId) -and
            $canonicalPackageIds.Contains($targetId)))
    if ($connectsCanonicalPackageAndFile) {
        [void]$actualPackageFileEdges.Add(
            "$sourceId|$type|$targetId")
    }
}
$dependencyDifference = @(
    Compare-Object `
        @($expectedRootRelationships | Sort-Object) `
        @($actualRootRelationships | Sort-Object)
)
if ($dependencyDifference.Count -gt 0) {
    $detail = ($dependencyDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "The SPDX root-to-package relationship mapping is not exact: $detail"
}
$packageDependencyDifference = @(
    Compare-Object `
        @($expectedPackageDependencies | Sort-Object) `
        @($actualPackageDependencies | Sort-Object)
)
if ($packageDependencyDifference.Count -gt 0) {
    $detail = ($packageDependencyDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "The SPDX package dependency topology is not exact: $detail"
}
$packageFileDifference = @(
    Compare-Object `
        @($expectedPackageFileEdges | Sort-Object) `
        @($actualPackageFileEdges | Sort-Object)
)
if ($packageFileDifference.Count -gt 0) {
    $detail = ($packageFileDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "The SPDX package-to-runtime-file mapping is not exact: $detail"
}

$connectedIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
[void]$connectedIds.Add($documentRootId)
do {
    $addedConnection = $false
    foreach ($relationship in @($relationshipsProperty.Value)) {
        $sourceId = [string]$relationship.spdxElementId
        $targetId = [string]$relationship.relatedSpdxElement
        if ($connectedIds.Contains($sourceId) -and
            $connectedIds.Add($targetId)) {
            $addedConnection = $true
        }
        if ($connectedIds.Contains($targetId) -and
            $connectedIds.Add($sourceId)) {
            $addedConnection = $true
        }
    }
}
while ($addedConnection)
foreach ($packageSpdxId in $packageSpdxIds) {
    if ([string]$packageSpdxId -cne $documentRootId -and
        -not $connectedIds.Contains($packageSpdxId)) {
        throw "Local package '$packageSpdxId' is disconnected from the SPDX document root."
    }
}

Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-inventory-semantic-before-schema' `
    -Path $resolvedSbom
if ($sbomInput.ComputeHash('SHA256') -cne $sbomSha256) {
    throw 'The retained SPDX document changed before schema validation.'
}
& (Join-Path $scriptDirectory 'Test-SpdxJsonSchema.ps1') `
    -SbomPath $resolvedSbom
if ($LASTEXITCODE -ne 0) {
    throw 'Official SPDX 2.3 JSON schema validation failed.'
}
if ($sbomInput.ComputeHash('SHA256') -cne $sbomSha256) {
    throw 'The retained SPDX document changed during schema validation.'
}

$documentIdentity =
    Get-DesktopPetSpdxDocumentIdentity -Document $sbom
$expectedDocumentNamespace = (
    'https://github.com/bigfnj/desktopPet/spdx/runtime-document/v1/' +
    $documentIdentity
)
if ([string]$sbom.documentNamespace -cne $expectedDocumentNamespace) {
    throw (
        'The SPDX document namespace is not bound to the exact canonical ' +
        "document identity. Expected '$expectedDocumentNamespace'; found " +
        "'$($sbom.documentNamespace)'."
    )
}

Write-Host (
    "SPDX inventory verified: {0} canonical lock/inventory NuGet identities, {1} shipped packages, {2} exact GENERATED_FROM provenance relationships, and all {3} manifest files represented with exact SHA-1/SHA-256 evidence plus the canonical root package verification code." -f
    $expectedAll.Count,
    $expectedShipped.Count,
    $expectedRuntimeFiles.Count,
    $manifest.Count
) -ForegroundColor Green
}
finally {
    if ($null -ne $sbomInput) {
        $sbomInput.Dispose()
        $sbomInput = $null
    }
    foreach ($input in @($runtimeInputs.Values)) {
        $input.Dispose()
    }
}
