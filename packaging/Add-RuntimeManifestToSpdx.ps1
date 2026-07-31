#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [string]$RuntimeRoot,
    [string]$RuntimeManifestPath,
    [string]$InventoryPath,
    [string]$LockFilePath,
    [switch]$RefreshRuntimeEvidence
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

function Get-DesktopPetDeterministicSpdxCreated {
    $epochText = [string]$env:SOURCE_DATE_EPOCH
    if ([string]::IsNullOrWhiteSpace($epochText)) {
        $repositoryRoot =
            [IO.Path]::GetFullPath((Split-Path $scriptDirectory -Parent))
        $git = Get-Command git.exe -ErrorAction SilentlyContinue
        if ($null -eq $git) {
            $git = Get-Command git -ErrorAction SilentlyContinue
        }
        if ($null -eq $git) {
            throw (
                'SOURCE_DATE_EPOCH is unset and Git is unavailable; a ' +
                'deterministic SPDX creation time cannot be derived.')
        }
        $epochOutput = @(
            & $git.Source -C $repositoryRoot show -s --format=%ct HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or $epochOutput.Count -ne 1) {
            throw (
                'SOURCE_DATE_EPOCH is unset and the source commit time ' +
                'could not be derived from Git.')
        }
        $epochText = ([string]$epochOutput[0]).Trim()
    }
    else {
        $epochText = $epochText.Trim()
    }

    if ($epochText -cnotmatch '^\d{1,12}$') {
        throw "SOURCE_DATE_EPOCH is not a non-negative integer: '$epochText'."
    }
    $epoch = [long]$epochText
    if ($epoch -gt 253402300799L) {
        throw "SOURCE_DATE_EPOCH exceeds the supported UTC range: '$epochText'."
    }
    try {
        $created =
            [DateTimeOffset]::FromUnixTimeSeconds($epoch).UtcDateTime
    }
    catch {
        throw "SOURCE_DATE_EPOCH is outside the supported UTC range: '$epochText'."
    }
    return $created.ToString(
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        [Globalization.CultureInfo]::InvariantCulture)
}

$runtimeInputs =
    New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::OrdinalIgnoreCase)
$metadataInputs = New-Object 'Collections.Generic.List[IDisposable]'
$maximumRuntimeManifestBytes = 1MB
$maximumPackageMetadataBytes = 16MB
$sbomStagingDirectory = $null
$sbomStagingDirectoryLease = $null
$temporarySbom = $null
$temporarySbomLease = $null
$sealedTemporarySbom = $null
$temporarySbomHash = $null
$sbomInput = $null
$sbomPrimaryError = $null
try {
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $scriptDirectory (
        '..\build\DesktopPetPortable\bin\Release\x64')
}
if ([string]::IsNullOrWhiteSpace($RuntimeManifestPath)) {
    $RuntimeManifestPath =
        Join-Path $scriptDirectory 'runtime-files.txt'
}
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath =
        Join-Path $scriptDirectory 'third-party-packages.json'
}
if ([string]::IsNullOrWhiteSpace($LockFilePath)) {
    $LockFilePath =
        Join-Path $scriptDirectory '..\src\packages.lock.json'
}

$resolvedRuntime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$resolvedManifest = (Resolve-Path -LiteralPath $RuntimeManifestPath).Path
$resolvedInventory = (Resolve-Path -LiteralPath $InventoryPath).Path
$resolvedLockFile = (Resolve-Path -LiteralPath $LockFilePath).Path

$manifestInput = Open-DesktopPetValidatedInputFile `
    -Path $resolvedManifest `
    -Root (Split-Path -Parent $resolvedManifest)
$metadataInputs.Add($manifestInput)
$inventoryInput = Open-DesktopPetValidatedInputFile `
    -Path $resolvedInventory `
    -Root (Split-Path -Parent $resolvedInventory)
$metadataInputs.Add($inventoryInput)
$lockInput = Open-DesktopPetValidatedInputFile `
    -Path $resolvedLockFile `
    -Root (Split-Path -Parent $resolvedLockFile)
$metadataInputs.Add($lockInput)
$manifestText =
    $manifestInput.ReadAllTextUtf8($maximumRuntimeManifestBytes)
$inventoryText =
    $inventoryInput.ReadAllTextUtf8($maximumPackageMetadataBytes)
$lockText =
    $lockInput.ReadAllTextUtf8($maximumPackageMetadataBytes)

$destinationSbom = (Resolve-Path -LiteralPath $SbomPath).Path
$sbomParent = Split-Path -Parent $destinationSbom
$sbomStagingDirectory = Join-Path $sbomParent (
    '.DesktopPet-sbom-' + [Guid]::NewGuid().ToString('N'))
$sbomStagingDirectoryLease = Open-DesktopPetNewScratchDirectory `
    -Path $sbomStagingDirectory `
    -AllowedRoot $sbomParent `
    -TrustedRoot $sbomParent `
    -ProtectedPaths @(
        $destinationSbom,
        $resolvedManifest,
        $resolvedInventory,
        $resolvedLockFile) `
    -ProtectedDirectories @($resolvedRuntime)
$temporarySbom = Join-Path $sbomStagingDirectory (
    [IO.Path]::GetFileName($destinationSbom) + '.tmp')
$temporarySbom = Assert-DesktopPetOutputFileSafe `
    -Path $temporarySbom `
    -TrustedRoot $sbomStagingDirectory `
    -ProtectedPaths @($destinationSbom)
$sbomInput = Open-DesktopPetValidatedInputFile `
    -Path $destinationSbom `
    -Root $sbomParent
$originalSbomHash = $sbomInput.ComputeHash('SHA256')
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-input-retained-before-parse' `
    -Path $destinationSbom
$originalSbomText = $sbomInput.ReadAllTextUtf8(128MB)
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-stage-write' `
    -Path $temporarySbom
[void](Write-DesktopPetNewFileBytes `
    -Path $temporarySbom `
    -Root $sbomStagingDirectory `
    -Bytes ([byte[]]@()) `
    -ProtectedPaths @($destinationSbom) `
    -MutationOperation 'before-sbom-output-create')
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'before-sbom-mutable-lease' `
    -Path $temporarySbom
$temporarySbomLease = Open-DesktopPetValidatedMutableFile `
    -Path $temporarySbom `
    -Root $sbomStagingDirectory
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-stage-mutate' `
    -Path $temporarySbom
$resolvedSbom = $temporarySbom
$sbom = $originalSbomText | ConvertFrom-Json

if ([string]$sbom.spdxVersion -cne 'SPDX-2.3') {
    throw "Expected an SPDX 2.3 JSON document; found '$($sbom.spdxVersion)'."
}
if ([string]$sbom.dataLicense -cne 'CC0-1.0') {
    throw "Expected SPDX dataLicense CC0-1.0; found '$($sbom.dataLicense)'."
}
$creationInfoProperty = $sbom.PSObject.Properties['creationInfo']
if ($null -eq $creationInfoProperty -or
    $null -eq $creationInfoProperty.Value) {
    throw 'The Syft SPDX document does not contain creationInfo.'
}
$createdProperty =
    $creationInfoProperty.Value.PSObject.Properties['created']
if ($null -eq $createdProperty -or
    [string]::IsNullOrWhiteSpace([string]$createdProperty.Value)) {
    throw 'The Syft SPDX document does not contain creationInfo.created.'
}
if (-not $RefreshRuntimeEvidence) {
    $creationInfoProperty.Value.created =
        Get-DesktopPetDeterministicSpdxCreated
}
else {
    $parsedCreated = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$createdProperty.Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsedCreated)) {
        throw (
            'The existing enriched SPDX creationInfo.created value is ' +
            'not a valid timestamp.')
    }
}
$creatorValues = @(
    @($creationInfoProperty.Value.creators) |
        ForEach-Object { [string]$_ }
)
if ($creatorValues.Count -eq 0 -or
    @($creatorValues | Where-Object {
        $_ -notmatch '^(Person|Organization|Tool): .+'
    }).Count -gt 0) {
    throw 'The Syft SPDX document contains invalid or empty creator metadata.'
}
$normalizerCreator = 'Tool: DesktopPet SPDX runtime normalizer'
if ($creatorValues -cnotcontains $normalizerCreator) {
    $creatorValues += $normalizerCreator
}
$creationInfoProperty.Value.creators = @($creatorValues)

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

$inventory = ConvertFrom-Json -InputObject $inventoryText
if ([int]$inventory.schemaVersion -ne 1) {
    throw "Unsupported third-party inventory schema: $($inventory.schemaVersion)"
}
$targetFramework = [string]$inventory.targetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw 'Third-party inventory does not declare targetFramework.'
}
$inventoryByIdentity = @{}
$inventoryRuntimeFilesByIdentity = @{}
$inventoryRootRelationshipByIdentity = @{}
$allowedInventoryRootRelationships = @(
    'DEPENDS_ON',
    'BUILD_TOOL_OF',
    'BUILD_DEPENDENCY_OF'
)
foreach ($inventoryPackage in @($inventory.packages)) {
    $name = [string]$inventoryPackage.name
    $version = [string]$inventoryPackage.version
    $license = [string]$inventoryPackage.license
    $key = "$($name.ToLowerInvariant())@$version"
    if ([string]::IsNullOrWhiteSpace($name) -or
        [string]::IsNullOrWhiteSpace($version) -or
        [string]::IsNullOrWhiteSpace($license) -or
        $inventoryByIdentity.ContainsKey($key)) {
        throw "Third-party inventory contains invalid or duplicate package metadata: '$key'."
    }
    $runtimeFiles = @(
        @($inventoryPackage.runtimeFiles) |
            ForEach-Object { [string]$_ }
    )
    if (@($runtimeFiles | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
        throw "Third-party package '$key' contains duplicate runtime file mappings."
    }
    foreach ($runtimeFile in $runtimeFiles) {
        if ([string]::IsNullOrWhiteSpace($runtimeFile) -or
            [IO.Path]::IsPathRooted($runtimeFile) -or
            $runtimeFile -ne [IO.Path]::GetFileName($runtimeFile)) {
            throw "Third-party package '$key' contains an unsafe runtime file mapping: '$runtimeFile'."
        }
    }
    $rootRelationshipProperty =
        $inventoryPackage.PSObject.Properties['relationshipToRoot']
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
            "Third-party package '$key' declares unsupported " +
            "relationshipToRoot '$rootRelationship'."
        )
    }
    if ($rootRelationship -cne 'DEPENDS_ON' -and
        $runtimeFiles.Count -ne 0) {
        throw (
            "Build-only third-party package '$key' cannot own runtime files."
        )
    }
    $inventoryByIdentity[$key] = $inventoryPackage
    $inventoryRuntimeFilesByIdentity[$key] = $runtimeFiles
    $inventoryRootRelationshipByIdentity[$key] = $rootRelationship
}
if ($inventoryByIdentity.Count -eq 0) {
    throw 'Third-party package inventory is empty.'
}

$lock = ConvertFrom-Json -InputObject $lockText
$lockTargetProperty =
    $lock.dependencies.PSObject.Properties[$targetFramework]
if ($null -eq $lockTargetProperty) {
    throw "Canonical lock file does not contain target '$targetFramework'."
}
$lockedByIdentity = @{}
$lockedIdentityByName = @{}
$directLockedIdentities = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($property in $lockTargetProperty.Value.PSObject.Properties) {
    if ([string]$property.Value.type -ceq 'Project') { continue }
    $name = [string]$property.Name
    $version = [string]$property.Value.resolved
    $key = "$($name.ToLowerInvariant())@$version"
    if ([string]::IsNullOrWhiteSpace($version) -or
        $lockedByIdentity.ContainsKey($key)) {
        throw "Canonical lock file contains invalid or duplicate identity '$key'."
    }
    $lockedByIdentity[$key] = $property.Value
    $lowerName = $name.ToLowerInvariant()
    if ($lockedIdentityByName.ContainsKey($lowerName)) {
        throw "Canonical lock file contains multiple versions of '$name'."
    }
    $lockedIdentityByName[$lowerName] = $key
    if ([string]$property.Value.type -ceq 'Direct') {
        [void]$directLockedIdentities.Add($key)
    }
}
$lockDifference = @(
    Compare-Object `
        @($inventoryByIdentity.Keys | Sort-Object) `
        @($lockedByIdentity.Keys | Sort-Object)
)
if ($lockDifference.Count -gt 0) {
    $detail = ($lockDifference | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)"
    }) -join '; '
    throw "Canonical lock file and third-party inventory disagree: $detail"
}
foreach ($identity in $inventoryRootRelationshipByIdentity.Keys) {
    if ([string]$inventoryRootRelationshipByIdentity[$identity] -cne
            'DEPENDS_ON' -and
        -not $directLockedIdentities.Contains($identity)) {
        throw (
            "Build-only third-party package '$identity' must be a direct " +
            "locked build input."
        )
    }
}

$manifest = @(
    $manifestText -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($manifest.Count -eq 0 -or
    @($manifest | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Runtime manifest is empty or contains duplicate entries.'
}
foreach ($name in $manifest) {
    if ([IO.Path]::IsPathRooted($name) -or $name -ne [IO.Path]::GetFileName($name)) {
        throw "Runtime manifest entry is not a plain file name: $name"
    }
}
$evidencePaths = New-Object 'Collections.Generic.Dictionary[string,string]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($name in $manifest) {
    $path = Join-Path $resolvedRuntime $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Runtime evidence file is missing: $path"
    }
    $evidencePaths.Add($name, $path)
    $runtimeInputs.Add(
        $name,
        (Open-DesktopPetValidatedInputFile `
            -Path $path `
            -Root $resolvedRuntime))
}
$runtimeFileOwner = New-Object 'Collections.Generic.Dictionary[string,string]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($identity in $inventoryRuntimeFilesByIdentity.Keys) {
    foreach ($runtimeFile in @($inventoryRuntimeFilesByIdentity[$identity])) {
        if (-not $evidencePaths.ContainsKey($runtimeFile)) {
            throw (
                "Third-party package '$identity' maps runtime file " +
                "'$runtimeFile', but it is absent from the canonical manifest."
            )
        }
        if ($runtimeFileOwner.ContainsKey($runtimeFile)) {
            throw (
                "Runtime file '$runtimeFile' is mapped to both " +
                "'$($runtimeFileOwner[$runtimeFile])' and '$identity'."
            )
        }
        $runtimeFileOwner.Add($runtimeFile, $identity)
    }
}

$filesProperty = $sbom.PSObject.Properties['files']
$files = if ($null -ne $filesProperty) { @($filesProperty.Value) } else { @() }
$relationshipsProperty = $sbom.PSObject.Properties['relationships']
$relationships = if ($null -ne $relationshipsProperty) {
    @($relationshipsProperty.Value)
} else { @() }

$canonicalDocumentName = 'DesktopPet-AI-Edition-Windows-x64-runtime'
$canonicalDocumentRootId =
    'SPDXRef-Package-DesktopPet-AI-Edition-Runtime'
$canonicalDocumentRootName =
    'DesktopPet AI Edition Windows x64 runtime'
$orderedManifest = [string[]]@($manifest)
[Array]::Sort($orderedManifest, [StringComparer]::Ordinal)

$rootIds = @(
    $relationships |
        Where-Object {
            [string]$_.spdxElementId -ceq 'SPDXRef-DOCUMENT' -and
            [string]$_.relationshipType -ceq 'DESCRIBES'
        } |
        ForEach-Object { [string]$_.relatedSpdxElement } |
        Sort-Object -Unique
)
if ($rootIds.Count -ne 1) {
    throw "Expected exactly one Syft document-root description; found $($rootIds.Count)."
}
$inputDocumentRootId = $rootIds[0]
if ($inputDocumentRootId -cne $canonicalDocumentRootId -and
    $inputDocumentRootId -notmatch '^SPDXRef-DocumentRoot-') {
    throw "Unsupported Syft document-root SPDX ID: '$inputDocumentRootId'."
}
$documentRootPackages = @(
    @($sbom.packages) |
        Where-Object { [string]$_.SPDXID -ceq $inputDocumentRootId }
)
if ($documentRootPackages.Count -ne 1) {
    throw (
        "Expected exactly one Syft document-root package '$inputDocumentRootId'; " +
        "found $($documentRootPackages.Count)."
    )
}
$documentRootPackage = $documentRootPackages[0]
if ($inputDocumentRootId -cne $canonicalDocumentRootId) {
    if (@(
            @($sbom.packages) |
                Where-Object {
                    [string]$_.SPDXID -ceq $canonicalDocumentRootId
                }
        ).Count -ne 0) {
        throw "The canonical document-root SPDX ID already exists."
    }
    $documentRootPackage.SPDXID = $canonicalDocumentRootId
    foreach ($relationship in $relationships) {
        if ([string]$relationship.spdxElementId -ceq $inputDocumentRootId) {
            $relationship.spdxElementId = $canonicalDocumentRootId
        }
        if ([string]$relationship.relatedSpdxElement -ceq
            $inputDocumentRootId) {
            $relationship.relatedSpdxElement = $canonicalDocumentRootId
        }
    }
}
$documentRootId = $canonicalDocumentRootId
$documentRootPackage.name = $canonicalDocumentRootName
$sbom.name = $canonicalDocumentName
$sbom.documentNamespace =
    'urn:desktop-pet:spdx-document-identity-pending'

$filesByName = New-Object 'Collections.Generic.Dictionary[string,object]' (
    [StringComparer]::OrdinalIgnoreCase)
$allSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$inputPackageSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$externalDocumentRefsProperty =
    $sbom.PSObject.Properties['externalDocumentRefs']
if ($null -ne $externalDocumentRefsProperty -and
    @($externalDocumentRefsProperty.Value).Count -gt 0) {
    throw 'The release SBOM must be self-contained and cannot declare external SPDX documents.'
}
$prunedSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
$documentSpdxId = [string]$sbom.SPDXID
if ($documentSpdxId -cne 'SPDXRef-DOCUMENT' -or
    -not $allSpdxIds.Add($documentSpdxId)) {
    throw "The SPDX document ID must be exactly 'SPDXRef-DOCUMENT'; found '$documentSpdxId'."
}
foreach ($package in @($sbom.packages)) {
    $packageSpdxId = [string]$package.SPDXID
    if ($packageSpdxId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or
        -not $allSpdxIds.Add($packageSpdxId)) {
        throw "The SPDX SBOM contains an empty or duplicate SPDX ID: '$packageSpdxId'."
    }
    [void]$inputPackageSpdxIds.Add($packageSpdxId)
}

foreach ($package in @($sbom.packages)) {
    $packageSpdxId = [string]$package.SPDXID
    if ($packageSpdxId -cne $documentRootId) {
        [void]$prunedSpdxIds.Add($packageSpdxId)
    }
}

# A directory-scan root is retained only as the document root. Remove any
# assembly-inferred NuGet purl that Syft may have attached to that root.
$rootExternalRefsProperty =
    $documentRootPackage.PSObject.Properties['externalRefs']
if ($null -ne $rootExternalRefsProperty) {
    $documentRootPackage.externalRefs = @(
        @($rootExternalRefsProperty.Value) |
            Where-Object {
                [string]$_.referenceLocator -notmatch '^pkg:nuget/'
            }
    )
}

function Get-CanonicalPackageSpdxId {
    param(
        [Parameter(Mandatory = $true)][string]$Identity,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString(
            $sha256.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($Identity)))).
            Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    $safeName = [regex]::Replace($Name, '[^A-Za-z0-9.-]', '-')
    return "SPDXRef-Package-NuGet-$safeName-$($hash.Substring(0, 16))"
}

$canonicalPackageByIdentity = @{}
$canonicalPackages = @($documentRootPackage)
$packageSpdxIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
[void]$packageSpdxIds.Add($documentRootId)
foreach ($identity in @($inventoryByIdentity.Keys | Sort-Object)) {
    $inventoryPackage = $inventoryByIdentity[$identity]
    $name = [string]$inventoryPackage.name
    $version = [string]$inventoryPackage.version
    $license = [string]$inventoryPackage.license
    $packageSpdxId =
        Get-CanonicalPackageSpdxId -Identity $identity -Name $name
    if (-not $allSpdxIds.Add($packageSpdxId) -and
        -not $prunedSpdxIds.Contains($packageSpdxId)) {
        throw "Generated canonical package SPDX ID collides: '$packageSpdxId'."
    }
    if (-not $packageSpdxIds.Add($packageSpdxId)) {
        throw "Generated duplicate canonical package SPDX ID: '$packageSpdxId'."
    }
    $lockedPackage = $lockedByIdentity[$identity]
    $contentHash = [string]$lockedPackage.contentHash
    try {
        $contentHashBytes = [Convert]::FromBase64String($contentHash)
    }
    catch {
        throw "Canonical lock contentHash for '$identity' is not base64."
    }
    if ($contentHashBytes.Length -ne 64) {
        throw "Canonical lock contentHash for '$identity' is not SHA-512."
    }
    $sha512 = ([BitConverter]::ToString(
        $contentHashBytes)).Replace('-', '').ToLowerInvariant()
    $flatName = $name.ToLowerInvariant()
    $flatVersion = $version.ToLowerInvariant()
    $downloadLocation = (
        'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg' -f
        [Uri]::EscapeDataString($flatName),
        [Uri]::EscapeDataString($flatVersion)
    )
    $canonicalPackage = [pscustomobject][ordered]@{
        name = $name
        SPDXID = $packageSpdxId
        versionInfo = $version
        downloadLocation = $downloadLocation
        filesAnalyzed = $false
        licenseConcluded = $license
        licenseDeclared = $license
        copyrightText = 'NOASSERTION'
        checksums = @(
            [pscustomobject][ordered]@{
                algorithm = 'SHA512'
                checksumValue = $sha512
            }
        )
        sourceInfo = (
            "Canonical NuGet identity and SHA-512 contentHash from " +
            "packages.lock.json target '$targetFramework'; license and " +
            "runtime-file ownership plus root relationship " +
            "'$($inventoryRootRelationshipByIdentity[$identity])' from " +
            "packaging/third-party-packages.json."
        )
        externalRefs = @(
            [pscustomobject][ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = (
                    'pkg:nuget/{0}@{1}' -f
                    [Uri]::EscapeDataString($name),
                    [Uri]::EscapeDataString($version))
            }
        )
    }
    $canonicalPackages += $canonicalPackage
    $canonicalPackageByIdentity[$identity] = $canonicalPackage
}
$lockEvidenceCount = 0
foreach ($file in $files) {
    $fileNameProperty = $file.PSObject.Properties['fileName']
    if ($null -eq $fileNameProperty) {
        throw 'The SPDX SBOM contains a file entry without fileName.'
    }
    $name = [IO.Path]::GetFileName(([string]$fileNameProperty.Value).Replace('/', '\'))
    if ([string]::IsNullOrWhiteSpace($name) -or $filesByName.ContainsKey($name)) {
        throw "The SPDX SBOM contains an empty or duplicate file evidence name: '$name'."
    }
    $fileSpdxId = [string]$file.SPDXID
    if ($fileSpdxId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or
        -not $allSpdxIds.Add($fileSpdxId)) {
        throw "The SPDX SBOM contains an empty or duplicate SPDX ID: '$fileSpdxId'."
    }
    if ($name -ieq 'packages.lock.json') {
        $lockEvidenceCount++
        [void]$prunedSpdxIds.Add([string]$file.SPDXID)
        continue
    }
    if (-not $evidencePaths.ContainsKey($name)) {
        throw "The SPDX SBOM contains unexpected file evidence: '$name'."
    }
    $filesByName.Add($name, $file)
}
if ((!$RefreshRuntimeEvidence -and $lockEvidenceCount -ne 1) -or
    ($RefreshRuntimeEvidence -and $lockEvidenceCount -gt 1)) {
    throw "Expected exactly one packages.lock.json evidence file to prune, or none when refreshing an already-enriched SBOM; found $lockEvidenceCount."
}

$inputRelationshipKeys = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($relationship in $relationships) {
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
    if (-not $inputRelationshipKeys.Add($relationshipKey)) {
        throw "The SPDX SBOM contains a duplicate relationship: '$relationshipKey'."
    }
    foreach ($endpoint in @($sourceId, $targetId)) {
        if (-not $allSpdxIds.Contains($endpoint)) {
            throw "The SPDX SBOM contains a dangling relationship endpoint: '$endpoint'."
        }
    }
}
$documentDescriptions = @(
    $relationships |
        Where-Object {
            [string]$_.spdxElementId -ceq $documentSpdxId -and
            [string]$_.relationshipType -ceq 'DESCRIBES'
        } |
        ForEach-Object { [string]$_.relatedSpdxElement }
)
if ($documentDescriptions.Count -ne 1 -or
    [string]$documentDescriptions[0] -cne $documentRootId -or
    -not $packageSpdxIds.Contains($documentRootId)) {
    throw 'The SPDX document must DESCRIBE exactly the local Syft document-root package.'
}
$rootContainedIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($relationship in $relationships) {
    if ([string]$relationship.spdxElementId -ceq $documentRootId -and
        [string]$relationship.relationshipType -ceq 'CONTAINS') {
        [void]$rootContainedIds.Add(
            [string]$relationship.relatedSpdxElement)
    }
}
$rootContainedPackageIds =
    New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
foreach ($containedId in $rootContainedIds) {
    if ($inputPackageSpdxIds.Contains([string]$containedId)) {
        [void]$rootContainedPackageIds.Add([string]$containedId)
    }
}
foreach ($file in $files) {
    $fileSpdxId = [string]$file.SPDXID
    if ($rootContainedIds.Contains($fileSpdxId)) {
        continue
    }

    # Syft 1.42 represents directory-scan evidence as:
    #   document root CONTAINS package
    #   package OTHER file
    # Accept only that exact rooted topology, then normalize every retained
    # runtime file to an explicit document-root CONTAINS relationship below.
    $supportedSyftOwners = @(
        $relationships |
            Where-Object {
                [string]$_.relationshipType -ceq 'OTHER' -and
                [string]$_.relatedSpdxElement -ceq $fileSpdxId -and
                $rootContainedPackageIds.Contains(
                    [string]$_.spdxElementId)
            } |
            ForEach-Object { [string]$_.spdxElementId } |
            Sort-Object -Unique
    )
    if ($supportedSyftOwners.Count -eq 0) {
        throw (
            "The SPDX document root does not CONTAIN file " +
            "'$($file.fileName)' directly or through the supported " +
            'Syft package topology.'
        )
    }
}
$connectedIds = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
[void]$connectedIds.Add($documentRootId)
do {
    $addedConnection = $false
    foreach ($relationship in $relationships) {
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
foreach ($packageSpdxId in $inputPackageSpdxIds) {
    if ([string]$packageSpdxId -cne $documentRootId -and
        -not $connectedIds.Contains($packageSpdxId)) {
        throw "Local package '$packageSpdxId' is disconnected from the SPDX document root."
    }
}

$relationships = @(
    $relationships | Where-Object {
        -not $prunedSpdxIds.Contains([string]$_.spdxElementId) -and
        -not $prunedSpdxIds.Contains([string]$_.relatedSpdxElement)
    }
)
$relationshipKeys = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::Ordinal)
foreach ($relationship in $relationships) {
    [void]$relationshipKeys.Add(
        "$($relationship.spdxElementId)|$($relationship.relationshipType)|$($relationship.relatedSpdxElement)")
}

$runtimeSha1ByName =
    New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $evidencePaths.GetEnumerator()) {
    $name = $entry.Key
    $input = $runtimeInputs[$name]
    $sha1 = $input.ComputeHash('SHA1').ToLowerInvariant()
    $sha256 = $input.ComputeHash('SHA256').ToLowerInvariant()
    $runtimeSha1ByName.Add($name, $sha1)

    if ($filesByName.ContainsKey($name)) {
        $file = $filesByName[$name]
        $file.fileName = "./$name"
        $checksumsProperty = $file.PSObject.Properties['checksums']
        $checksums = if ($null -ne $checksumsProperty) {
            @($checksumsProperty.Value)
        } else { @() }
        $retainedChecksums = @()
        foreach ($checksum in $checksums) {
            $algorithm = [string]$checksum.algorithm
            $value = [string]$checksum.checksumValue
            if ($algorithm -ieq 'SHA1' -or $algorithm -ieq 'SHA256') {
                $expectedValue = if ($algorithm -ieq 'SHA1') {
                    $sha1
                }
                else {
                    $sha256
                }
                # Syft 1.42.3 emits an all-zero SHA-1 placeholder for some
                # .NET PE package file records because SPDX requires a SHA-1
                # even when that cataloger has no digest. Treat only the exact
                # all-zero digest width as absent evidence; every non-zero
                # mismatch still fails closed below. The canonical evidence is
                # always recomputed from the staged runtime and replaces it.
                $placeholderValue = if ($algorithm -ieq 'SHA1') {
                    '0' * 40
                }
                else {
                    '0' * 64
                }
                if (-not $RefreshRuntimeEvidence -and
                    $value -ine $expectedValue -and
                    $value -cne $placeholderValue) {
                    throw (
                        "Syft $($algorithm.ToUpperInvariant()) evidence for " +
                        "'$name' disagrees with the staged file."
                    )
                }
                continue
            }
            # The published runtime evidence is canonicalized to exactly the
            # recomputed SHA-1 and SHA-256 below. Syft-specific or stale extra
            # checksum algorithms are intentionally not retained.
            continue
        }
        $retainedChecksums += [pscustomobject][ordered]@{
            algorithm = 'SHA1'
            checksumValue = $sha1
        }
        $retainedChecksums += [pscustomobject][ordered]@{
            algorithm = 'SHA256'
            checksumValue = $sha256
        }
        if ($null -ne $checksumsProperty) {
            $file.checksums = @($retainedChecksums)
        }
        else {
            $file | Add-Member -NotePropertyName checksums -NotePropertyValue @($retainedChecksums)
        }
    }
    else {
        $safeName = [regex]::Replace($name, '[^A-Za-z0-9.-]', '-')
        $spdxId = "SPDXRef-File-Runtime-$safeName-$($sha256.Substring(0, 16))"
        if (-not $allSpdxIds.Add($spdxId)) {
            throw "Generated duplicate SPDX ID for runtime evidence '$name'."
        }
        $file = [pscustomobject][ordered]@{
            fileName = "./$name"
            SPDXID = $spdxId
            checksums = @(
                [pscustomobject][ordered]@{
                    algorithm = 'SHA1'
                    checksumValue = $sha1
                },
                [pscustomobject][ordered]@{
                    algorithm = 'SHA256'
                    checksumValue = $sha256
                }
            )
            licenseConcluded = 'NOASSERTION'
            licenseInfoInFiles = @('NOASSERTION')
            copyrightText = 'NOASSERTION'
        }
        $files += $file
        $filesByName.Add($name, $file)
    }

    $relationshipKey = "$documentRootId|CONTAINS|$($file.SPDXID)"
    if ($relationshipKeys.Add($relationshipKey)) {
        $relationships += [pscustomobject][ordered]@{
            spdxElementId = $documentRootId
            relatedSpdxElement = [string]$file.SPDXID
            relationshipType = 'CONTAINS'
        }
    }
}

foreach ($identity in @($canonicalPackageByIdentity.Keys | Sort-Object)) {
    $package = $canonicalPackageByIdentity[$identity]
    if ($directLockedIdentities.Contains($identity)) {
        $rootRelationship =
            [string]$inventoryRootRelationshipByIdentity[$identity]
        $relationshipSource = if ($rootRelationship -ceq 'DEPENDS_ON') {
            $documentRootId
        }
        else {
            [string]$package.SPDXID
        }
        $relationshipTarget = if ($rootRelationship -ceq 'DEPENDS_ON') {
            [string]$package.SPDXID
        }
        else {
            $documentRootId
        }
        $dependencyKey =
            "$relationshipSource|$rootRelationship|$relationshipTarget"
        if ($relationshipKeys.Add($dependencyKey)) {
            $relationships += [pscustomobject][ordered]@{
                spdxElementId = $relationshipSource
                relatedSpdxElement = $relationshipTarget
                relationshipType = $rootRelationship
            }
        }
    }
    $lockedPackage = $lockedByIdentity[$identity]
    $dependenciesProperty =
        $lockedPackage.PSObject.Properties['dependencies']
    if ($null -ne $dependenciesProperty) {
        foreach ($dependencyProperty in
            $dependenciesProperty.Value.PSObject.Properties) {
            $dependencyName = ([string]$dependencyProperty.Name).ToLowerInvariant()
            if (-not $lockedIdentityByName.ContainsKey($dependencyName)) {
                throw (
                    "Locked dependency '$($dependencyProperty.Name)' from " +
                    "'$identity' is absent from the canonical target."
                )
            }
            $dependencyIdentity = $lockedIdentityByName[$dependencyName]
            $dependencyPackage =
                $canonicalPackageByIdentity[$dependencyIdentity]
            $dependencyKey =
                "$($package.SPDXID)|DEPENDS_ON|$($dependencyPackage.SPDXID)"
            if ($relationshipKeys.Add($dependencyKey)) {
                $relationships += [pscustomobject][ordered]@{
                    spdxElementId = [string]$package.SPDXID
                    relatedSpdxElement =
                        [string]$dependencyPackage.SPDXID
                    relationshipType = 'DEPENDS_ON'
                }
            }
        }
    }
    foreach ($runtimeFile in @(
            $inventoryRuntimeFilesByIdentity[$identity])) {
        $file = $filesByName[$runtimeFile]
        $packageFileKey =
            "$($file.SPDXID)|GENERATED_FROM|$($package.SPDXID)"
        if ($relationshipKeys.Add($packageFileKey)) {
            $relationships += [pscustomobject][ordered]@{
                spdxElementId = [string]$file.SPDXID
                relatedSpdxElement = [string]$package.SPDXID
                relationshipType = 'GENERATED_FROM'
            }
        }
    }
}

$verificationSha1Values = [string[]]@(
    $orderedManifest |
        ForEach-Object { [string]$runtimeSha1ByName[[string]$_] }
)
[Array]::Sort($verificationSha1Values, [StringComparer]::Ordinal)
$verificationCodeAlgorithm = [Security.Cryptography.SHA1]::Create()
try {
    $packageVerificationCodeValue = ([BitConverter]::ToString(
        $verificationCodeAlgorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes(
                ($verificationSha1Values -join ''))))).
        Replace('-', '').ToLowerInvariant()
}
finally {
    $verificationCodeAlgorithm.Dispose()
}
$filesAnalyzedProperty =
    $documentRootPackage.PSObject.Properties['filesAnalyzed']
if ($null -ne $filesAnalyzedProperty) {
    $documentRootPackage.filesAnalyzed = $true
}
else {
    $documentRootPackage |
        Add-Member -NotePropertyName filesAnalyzed -NotePropertyValue $true
}
$verificationCode = [pscustomobject][ordered]@{
    packageVerificationCodeValue = $packageVerificationCodeValue
}
$verificationCodeProperty =
    $documentRootPackage.PSObject.Properties['packageVerificationCode']
if ($null -ne $verificationCodeProperty) {
    $documentRootPackage.packageVerificationCode = $verificationCode
}
else {
    $documentRootPackage |
        Add-Member `
            -NotePropertyName packageVerificationCode `
            -NotePropertyValue $verificationCode
}

$sbom.packages = @($canonicalPackages)
$files = @($filesByName.Values | Sort-Object { [string]$_.fileName })
if ($null -ne $filesProperty) {
    $sbom.files = $files
}
else {
    $sbom | Add-Member -NotePropertyName files -NotePropertyValue $files
}
if ($null -ne $relationshipsProperty) {
    $sbom.relationships = @(
        $relationships |
            Sort-Object {
                "$($_.spdxElementId)|$($_.relationshipType)|$($_.relatedSpdxElement)"
            }
    )
}
else {
    $sbom | Add-Member `
        -NotePropertyName relationships `
        -NotePropertyValue @(
            $relationships |
                Sort-Object {
                    "$($_.spdxElementId)|$($_.relationshipType)|$($_.relatedSpdxElement)"
                }
        )
}

$documentIdentity =
    Get-DesktopPetSpdxDocumentIdentity -Document $sbom
$sbom.documentNamespace = (
    'https://github.com/bigfnj/desktopPet/spdx/runtime-document/v1/' +
    $documentIdentity
)

$serializedSbom =
    ($sbom | ConvertTo-Json -Depth 100) + [Environment]::NewLine
$serializedSbomBytes =
    (New-Object Text.UTF8Encoding($false, $true)).GetBytes($serializedSbom)
$serializedHasher = [Security.Cryptography.SHA256]::Create()
try {
    $expectedSerializedSbomHash =
        ([BitConverter]::ToString(
            $serializedHasher.ComputeHash($serializedSbomBytes))).Replace('-', '')
}
finally {
    $serializedHasher.Dispose()
}
[IO.File]::WriteAllBytes($temporarySbom, $serializedSbomBytes)
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-final-bytes-written-before-seal' `
    -Path $temporarySbom
$sealedTemporarySbom = $temporarySbomLease.Seal()
$temporarySbomLease = $null
Invoke-DesktopPetStagingMutationTestHook `
    -Operation 'sbom-sealed-validate' `
    -Path $temporarySbom
$temporarySbomHash =
    $sealedTemporarySbom.ComputeHash('SHA256')
if ($temporarySbomHash -cne $expectedSerializedSbomHash) {
    throw (
        'Sealed SPDX output differs from the exact intended serialized ' +
        'UTF-8 bytes.')
}
$sealedSbomText = $sealedTemporarySbom.ReadAllTextUtf8(128MB)
if (-not $sealedSbomText.Equals(
        $serializedSbom,
        [StringComparison]::Ordinal)) {
    throw 'Sealed SPDX text differs from the intended serialized document.'
}
$sealedSbom = $sealedSbomText | ConvertFrom-Json
$sealedDocumentIdentity =
    Get-DesktopPetSpdxDocumentIdentity -Document $sealedSbom
if ([string]$sealedSbom.spdxVersion -cne 'SPDX-2.3' -or
    [string]$sealedSbom.dataLicense -cne 'CC0-1.0' -or
    $sealedDocumentIdentity -cne $documentIdentity -or
    [string]$sealedSbom.documentNamespace -cne
        [string]$sbom.documentNamespace) {
    throw 'Sealed SPDX output failed semantic identity validation.'
}
$sbomInput.Dispose()
$sbomInput = $null
[void](Publish-DesktopPetAtomicFile `
    -TemporaryPath $temporarySbom `
    -DestinationPath $destinationSbom `
    -TrustedRoot $sbomParent `
    -SealedTemporaryFile $sealedTemporarySbom `
    -ExpectedTemporarySha256 $temporarySbomHash `
    -ExpectedDestinationSha256 $originalSbomHash)
$temporarySbom = $null

Write-Host (
    "SPDX runtime evidence {0}: {1} canonical NuGet packages from the lock/inventory, exactly {2} manifest files with SHA-1/SHA-256 and root package verification code, and {3} GENERATED_FROM package-to-runtime provenance mappings; packages.lock.json file evidence and inferred package identities were pruned." -f
    $(if ($RefreshRuntimeEvidence) { 'refreshed' } else { 'added' }),
    $canonicalPackageByIdentity.Count,
    $manifest.Count,
    $runtimeFileOwner.Count
) -ForegroundColor Green
}
catch {
    $sbomPrimaryError = $_
    throw
}
finally {
    foreach ($input in $metadataInputs) {
        $input.Dispose()
    }
    foreach ($input in @($runtimeInputs.Values)) {
        $input.Dispose()
    }
    if ($null -ne $sbomInput) {
        $sbomInput.Dispose()
        $sbomInput = $null
    }
    if ($null -ne $sealedTemporarySbom) {
        $sealedTemporarySbom.Dispose()
        $sealedTemporarySbom = $null
    }
    if ($null -ne $temporarySbomLease) {
        $temporarySbomLease.Dispose()
        $temporarySbomLease = $null
    }
    if ($null -ne $sbomStagingDirectoryLease) {
        $sbomStagingDirectoryLease.Dispose()
        $sbomStagingDirectoryLease = $null
    }
    if ($null -ne $sbomStagingDirectory -and
        (Test-Path -LiteralPath $sbomStagingDirectory)) {
        $sbomParent = Split-Path -Parent $sbomStagingDirectory
        try {
            Remove-DesktopPetSafeDirectory `
                -Path $sbomStagingDirectory `
                -AllowedRoot $sbomParent `
                -TrustedRoot $sbomParent
        }
        catch {
            if ($null -eq $sbomPrimaryError) {
                throw
            }
            Write-Warning (
                'SPDX scratch cleanup also failed; preserving the primary ' +
                "error. Cleanup error: $($_.Exception.Message)")
        }
    }
}
