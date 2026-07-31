#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$enricher = Join-Path $repoRoot 'packaging\Add-RuntimeManifestToSpdx.ps1'
. (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
. (Join-Path $repoRoot 'packaging\SpdxDocumentIdentity.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-SbomRefresh-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$originalSourceDateEpoch = $env:SOURCE_DATE_EPOCH

function Test-SharingViolation {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if (($exception.HResult -band 0xffff) -in @(5, 32, 33)) {
            return $true
        }
        $exception = $exception.InnerException
    }
    return $false
}

try {
    $env:SOURCE_DATE_EPOCH = '946684800'
    $runtime = Join-Path $scratch 'runtime'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    $runtimeFile = Join-Path $runtime 'DesktopPet.exe'
    $manifest = Join-Path $scratch 'runtime-files.txt'
    $inventoryPath = Join-Path $scratch 'third-party-packages.json'
    $lockPath = Join-Path $scratch 'packages.lock.json'
    $targetFramework = '.NETFramework,Version=v4.8'
    $preSignSbom = Join-Path $scratch 'pre-sign.spdx.json'
    $syftTopologySbom = Join-Path $scratch 'syft-topology.spdx.json'
    $enrichedSbom = Join-Path $scratch 'enriched.spdx.json'
    $negativeSbom = Join-Path $scratch 'negative.spdx.json'
    $deterministicFirstSbom =
        Join-Path $scratch 'deterministic-first.spdx.json'
    $deterministicSecondSbom =
        Join-Path $scratch 'deterministic-second.spdx.json'
    $retainedInputSbom =
        Join-Path $scratch 'retained-input.spdx.json'
    $preSealMutationSbom =
        Join-Path $scratch 'pre-seal-mutation.spdx.json'
    $metadataVariantInventory =
        Join-Path $scratch 'metadata-variant-packages.json'
    $metadataVariantSbom =
        Join-Path $scratch 'metadata-variant.spdx.json'

    $identityFixture = [pscustomobject][ordered]@{
        z = @(
            $true,
            $false,
            $null,
            [int64]17,
            ('caf' + [char]0x00e9),
            "line`n")
        documentNamespace = 'https://example.invalid/ignored-first'
        a = [pscustomobject][ordered]@{
            beta = [string][char]0x03b2
            alpha = 'A'
        }
    }
    $identityFixtureReordered = [pscustomobject][ordered]@{
        a = [pscustomobject][ordered]@{
            alpha = 'A'
            beta = [string][char]0x03b2
        }
        documentNamespace = 'https://example.invalid/ignored-second'
        z = @(
            $true,
            $false,
            $null,
            [int64]17,
            ('caf' + [char]0x00e9),
            "line`n")
    }
    $expectedIdentityFixture =
        'bb65fbe41a6c35a6b558020cbcc77ef33a39dedc9413258636fe788aec8a8e95'
    $firstIdentityFixture =
        Get-DesktopPetSpdxDocumentIdentity -Document $identityFixture
    $secondIdentityFixture =
        Get-DesktopPetSpdxDocumentIdentity -Document $identityFixtureReordered
    $identityFixtureReordered.a.alpha = 'changed'
    $changedIdentityFixture =
        Get-DesktopPetSpdxDocumentIdentity -Document $identityFixtureReordered
    if ($firstIdentityFixture -cne $expectedIdentityFixture -or
        $secondIdentityFixture -cne $expectedIdentityFixture -or
        $changedIdentityFixture -ceq $expectedIdentityFixture) {
        throw (
            'Canonical SPDX identity framing is not stable across object ' +
            'property order and namespace values, or did not bind metadata.'
        )
    }
    Write-Host (
        'PASS: canonical SPDX identity matches its cross-version golden fixture.'
    ) -ForegroundColor Green

    function Assert-EnricherRejects {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][object]$Document,
            [Parameter(Mandatory = $true)][string]$ExpectedMessage
        )

        $path = Join-Path $scratch "$Name.spdx.json"
        [IO.File]::WriteAllText(
            $path,
            (($Document | ConvertTo-Json -Depth 20) +
                [Environment]::NewLine),
            $utf8)
        $originalHash = (
            Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $accepted = $true
        $rejectionMessage = ''
        try {
            & $enricher `
                -SbomPath $path `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $manifest `
                -InventoryPath $inventoryPath `
                -LockFilePath $lockPath *> $null
        }
        catch {
            $accepted = $false
            $rejectionMessage = $_.Exception.Message
        }
        if ($accepted) {
            throw "The SBOM enricher accepted negative control '$Name'."
        }
        if ($rejectionMessage -notmatch $ExpectedMessage) {
            throw "SBOM enricher negative control '$Name' failed for an unexpected reason: $rejectionMessage"
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne
            $originalHash) {
            throw (
                "Rejected SBOM input '$Name' was modified before the " +
                'enricher failed.')
        }
    }

    function Assert-EnricherMetadataRejected {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][string]$ManifestPath,
            [Parameter(Mandatory = $true)][string]$InventoryPath,
            [Parameter(Mandatory = $true)][string]$PackageLockPath,
            [Parameter(Mandatory = $true)][string]$ExpectedMessage
        )

        $path = Join-Path $scratch "$Name.metadata-input.spdx.json"
        Copy-Item -LiteralPath $preSignSbom -Destination $path
        $originalHash = (
            Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $failure = $null
        try {
            & $enricher `
                -SbomPath $path `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $ManifestPath `
                -InventoryPath $InventoryPath `
                -LockFilePath $PackageLockPath *> $null
        }
        catch {
            $failure = $_
        }
        if ($null -eq $failure) {
            throw "SBOM enricher accepted unsafe metadata input '$Name'."
        }
        if ($failure.Exception.Message -notmatch $ExpectedMessage) {
            throw (
                "SBOM metadata-input case '$Name' failed for an unexpected " +
                "reason: $($failure.Exception.Message)")
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne
            $originalHash) {
            throw "Rejected metadata-input case '$Name' modified its SBOM."
        }
    }

    [IO.File]::WriteAllBytes(
        $runtimeFile,
        [Text.Encoding]::ASCII.GetBytes('unsigned-runtime'))
    [IO.File]::WriteAllText(
        $manifest,
        "DesktopPet.exe`r`n",
        $utf8)
    [IO.File]::WriteAllText(
        $inventoryPath,
        (([pscustomobject][ordered]@{
            schemaVersion = 1
            targetFramework = $targetFramework
            packages = @(
                [pscustomobject][ordered]@{
                    name = 'Example.Runtime'
                    version = '1.2.3'
                    license = 'MIT'
                    runtimeFiles = @('DesktopPet.exe')
                },
                [pscustomobject][ordered]@{
                    name = 'Example.Dependency'
                    version = '2.0.0'
                    license = 'Apache-2.0'
                    runtimeFiles = @()
                },
                [pscustomobject][ordered]@{
                    name = 'Example.Compiler'
                    version = '3.0.0'
                    license = 'MIT'
                    relationshipToRoot = 'BUILD_TOOL_OF'
                    runtimeFiles = @()
                },
                [pscustomobject][ordered]@{
                    name = 'Example.ReferenceAssemblies'
                    version = '4.0.0'
                    license = 'MIT'
                    relationshipToRoot = 'BUILD_DEPENDENCY_OF'
                    runtimeFiles = @()
                }
            )
        } | ConvertTo-Json -Depth 10) + [Environment]::NewLine),
        $utf8)
    $lockSha512Bytes = New-Object byte[] 64
    $lockSha512Base64 = [Convert]::ToBase64String($lockSha512Bytes)
    $lockSha512Hex = ([BitConverter]::ToString(
        $lockSha512Bytes)).Replace('-', '').ToLowerInvariant()
    $lockTarget = [pscustomobject]@{}
    $lockTarget | Add-Member `
        -NotePropertyName 'Example.Runtime' `
        -NotePropertyValue ([pscustomobject][ordered]@{
            type = 'Direct'
            requested = '[1.2.3, )'
            resolved = '1.2.3'
            contentHash = $lockSha512Base64
            dependencies = [pscustomobject][ordered]@{
                'Example.Dependency' = '2.0.0'
            }
        })
    $lockTarget | Add-Member `
        -NotePropertyName 'Example.Dependency' `
        -NotePropertyValue ([pscustomobject][ordered]@{
            type = 'Transitive'
            resolved = '2.0.0'
            contentHash = $lockSha512Base64
        })
    $lockTarget | Add-Member `
        -NotePropertyName 'Example.Compiler' `
        -NotePropertyValue ([pscustomobject][ordered]@{
            type = 'Direct'
            requested = '[3.0.0, )'
            resolved = '3.0.0'
            contentHash = $lockSha512Base64
        })
    $lockTarget | Add-Member `
        -NotePropertyName 'Example.ReferenceAssemblies' `
        -NotePropertyValue ([pscustomobject][ordered]@{
            type = 'Direct'
            requested = '[4.0.0, )'
            resolved = '4.0.0'
            contentHash = $lockSha512Base64
        })
    $lockDependencies = [pscustomobject]@{}
    $lockDependencies | Add-Member `
        -NotePropertyName $targetFramework `
        -NotePropertyValue $lockTarget
    [IO.File]::WriteAllText(
        $lockPath,
        (([pscustomobject][ordered]@{
            version = 1
            dependencies = $lockDependencies
        } | ConvertTo-Json -Depth 10) + [Environment]::NewLine),
        $utf8)
    $unsignedHash = (
        Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $unsignedSha1 = (
        Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA1
    ).Hash.ToLowerInvariant()

    $rootId = 'SPDXRef-DocumentRoot-selftest'
    $canonicalRootId =
        'SPDXRef-Package-DesktopPet-AI-Edition-Runtime'
    $packageId = 'SPDXRef-Package-example-runtime'
    $dependencyPackageId = 'SPDXRef-Package-example-dependency'
    $spuriousPackageId = 'SPDXRef-Package-assembly-inference'
    $lockId = 'SPDXRef-File-lock'
    $runtimeId = 'SPDXRef-File-runtime'
    $document = [pscustomobject][ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = 'DesktopPet SBOM refresh self-test'
        documentNamespace = 'https://example.invalid/desktop-pet/sbom-refresh-selftest'
        creationInfo = [pscustomobject][ordered]@{
            created = '2000-01-01T00:00:00Z'
            creators = @('Tool: DesktopPet self-test')
        }
        packages = @(
            [pscustomobject][ordered]@{
                name = 'DesktopPet SBOM refresh self-test root'
                SPDXID = $rootId
                downloadLocation = 'NOASSERTION'
                filesAnalyzed = $true
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
            },
            [pscustomobject][ordered]@{
                name = 'Example.Runtime'
                versionInfo = '1.2.3'
                SPDXID = $packageId
                sourceInfo = '.\packages.lock.json'
                licenseDeclared = 'NOASSERTION'
                licenseConcluded = 'NOASSERTION'
                externalRefs = @(
                    [pscustomobject][ordered]@{
                        referenceCategory = 'PACKAGE-MANAGER'
                        referenceType = 'purl'
                        referenceLocator =
                            'pkg:nuget/Example.Runtime@1.2.3'
                    }
                )
            },
            [pscustomobject][ordered]@{
                name = 'Example.Dependency'
                versionInfo = '2.0.0'
                SPDXID = $dependencyPackageId
                sourceInfo = '.\packages.lock.json'
                licenseDeclared = 'NOASSERTION'
                licenseConcluded = 'NOASSERTION'
                externalRefs = @(
                    [pscustomobject][ordered]@{
                        referenceCategory = 'PACKAGE-MANAGER'
                        referenceType = 'purl'
                        referenceLocator =
                            'pkg:nuget/Example.Dependency@2.0.0'
                    }
                )
            },
            [pscustomobject][ordered]@{
                name = 'Example.Runtime.Assembly'
                versionInfo = '1.2.3.4567'
                SPDXID = $spuriousPackageId
                sourceInfo = '.\Example.Runtime.dll'
                licenseDeclared = 'NOASSERTION'
                licenseConcluded = 'NOASSERTION'
                externalRefs = @(
                    [pscustomobject][ordered]@{
                        referenceCategory = 'PACKAGE-MANAGER'
                        referenceType = 'purl'
                        referenceLocator =
                            'pkg:nuget/Example.Runtime.Assembly@1.2.3.4567'
                    }
                )
            }
        )
        files = @(
            [pscustomobject][ordered]@{
                fileName = './packages.lock.json'
                SPDXID = $lockId
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
                checksums = @(
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA256'
                        checksumValue = ('0' * 64)
                    }
                )
            },
            [pscustomobject][ordered]@{
                fileName = './DesktopPet.exe'
                SPDXID = $runtimeId
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
                checksums = @(
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA256'
                        checksumValue = $unsignedHash
                    },
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA1'
                        checksumValue = $unsignedSha1
                    },
                    [pscustomobject][ordered]@{
                        algorithm = 'MD5'
                        checksumValue = ('2' * 32)
                    }
                )
            }
        )
        relationships = @(
            [pscustomobject][ordered]@{
                spdxElementId = 'SPDXRef-DOCUMENT'
                relatedSpdxElement = $rootId
                relationshipType = 'DESCRIBES'
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relatedSpdxElement = $lockId
                relationshipType = 'CONTAINS'
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relatedSpdxElement = $runtimeId
                relationshipType = 'CONTAINS'
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relatedSpdxElement = $packageId
                relationshipType = 'DEPENDS_ON'
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relatedSpdxElement = $dependencyPackageId
                relationshipType = 'DEPENDS_ON'
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relatedSpdxElement = $spuriousPackageId
                relationshipType = 'DEPENDS_ON'
            }
        )
    }
    [IO.File]::WriteAllText(
        $preSignSbom,
        (($document | ConvertTo-Json -Depth 20) + [Environment]::NewLine),
        $utf8)

    Copy-Item -LiteralPath $preSignSbom -Destination $deterministicFirstSbom
    Copy-Item -LiteralPath $preSignSbom -Destination $retainedInputSbom
    Copy-Item -LiteralPath $preSignSbom -Destination $preSealMutationSbom
    $secondWallClockDocument =
        Get-Content -LiteralPath $preSignSbom -Raw |
            ConvertFrom-Json
    $secondWallClockDocument.creationInfo.created =
        '2035-07-08T09:10:11Z'
    [IO.File]::WriteAllText(
        $deterministicSecondSbom,
        (($secondWallClockDocument | ConvertTo-Json -Depth 20) +
            [Environment]::NewLine),
        $utf8)

    $sbomMutationSentinel = Join-Path $scratch 'mutation-sentinel.txt'
    [IO.File]::WriteAllText(
        $sbomMutationSentinel,
        'sbom-external-sentinel-must-survive',
        $utf8)
    $script:sbomLeafHookReached = $false
    $script:sbomLeafMoveBlocked = $false
    $script:sbomLeafDeleteBlocked = $false
    $script:sbomLeafAliasRejected = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)

        if ($observedOperation -cne 'sbom-stage-mutate') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:sbomLeafHookReached = $true
        try {
            Move-Item `
                -LiteralPath $observedPath `
                -Destination ($observedPath + '.attacker-moved') `
                -ErrorAction Stop
        }
        catch {
            $script:sbomLeafMoveBlocked =
                ($_.Exception.HResult -band 0xffff) -in @(5, 32, 33)
        }
        try {
            Remove-Item -LiteralPath $observedPath -Force -ErrorAction Stop
        }
        catch {
            $script:sbomLeafDeleteBlocked =
                ($_.Exception.HResult -band 0xffff) -in @(5, 32, 33)
        }
        try {
            New-Item `
                -ItemType HardLink `
                -Path $observedPath `
                -Target $sbomMutationSentinel `
                -ErrorAction Stop | Out-Null
        }
        catch {
            $script:sbomLeafAliasRejected = $true
        }
    }
    try {
        & {
            . $enricher `
                -SbomPath $deterministicFirstSbom `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $manifest `
                -InventoryPath $inventoryPath `
                -LockFilePath $lockPath
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $script:sbomLeafHookReached -or
        -not $script:sbomLeafMoveBlocked -or
        -not $script:sbomLeafDeleteBlocked -or
        -not $script:sbomLeafAliasRejected -or
        [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
            $sbomMutationSentinel) -ne 1 -or
        [IO.File]::ReadAllText($sbomMutationSentinel) -cne
            'sbom-external-sentinel-must-survive') {
        throw (
            'SPDX mutable staging leaf did not block rename/delete/hard-link ' +
            'substitution while preserving the external sentinel.')
    }
    $script:sbomSealedHookReached = $false
    $script:sbomSealedWriteBlocked = $false
    $script:sbomSealedWriteSucceeded = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)

        if ($observedOperation -cne 'sbom-sealed-validate') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:sbomSealedHookReached = $true
        try {
            [IO.File]::WriteAllText(
                $observedPath,
                'attacker-in-place-sbom-write')
            $script:sbomSealedWriteSucceeded = $true
        }
        catch {
            $script:sbomSealedWriteBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
    }
    try {
        & {
            . $enricher `
                -SbomPath $deterministicSecondSbom `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $manifest `
                -InventoryPath $inventoryPath `
                -LockFilePath $lockPath
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $script:sbomSealedHookReached -or
        -not $script:sbomSealedWriteBlocked -or
        $script:sbomSealedWriteSucceeded) {
        throw (
            'SPDX sealed output did not reject the final in-place write ' +
            'attempt before semantic validation/hash.')
    }

    $script:sbomInputHookReached = $false
    $script:sbomInputWriteBlocked = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($observedOperation -cne 'sbom-input-retained-before-parse') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:sbomInputHookReached = $true
        try {
            [IO.File]::WriteAllText(
                $observedPath,
                'attacker-input-write',
                (New-Object Text.UTF8Encoding($false)))
        }
        catch {
            $script:sbomInputWriteBlocked =
                Test-SharingViolation -ErrorRecord $_
        }
    }
    try {
        & {
            . $enricher `
                -SbomPath $retainedInputSbom `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $manifest `
                -InventoryPath $inventoryPath `
                -LockFilePath $lockPath
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $script:sbomInputHookReached -or
        -not $script:sbomInputWriteBlocked) {
        throw 'Retained SPDX input did not block an in-place pre-parse write.'
    }

    $preSealOriginalHash = (
        Get-FileHash -LiteralPath $preSealMutationSbom -Algorithm SHA256).Hash
    $script:sbomPreSealHookReached = $false
    $script:sbomPreSealWriteSucceeded = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($observedOperation -cne
            'sbom-final-bytes-written-before-seal') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:sbomPreSealHookReached = $true
        [IO.File]::WriteAllText(
            $observedPath,
            'attacker-pre-seal-write',
            (New-Object Text.UTF8Encoding($false)))
        $script:sbomPreSealWriteSucceeded = $true
    }
    $preSealRejected = $false
    try {
        & {
            . $enricher `
                -SbomPath $preSealMutationSbom `
                -RuntimeRoot $runtime `
                -RuntimeManifestPath $manifest `
                -InventoryPath $inventoryPath `
                -LockFilePath $lockPath
        }
    }
    catch {
        $preSealRejected =
            $_.Exception.Message -match
                'exact intended serialized UTF-8 bytes'
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $script:sbomPreSealHookReached -or
        -not $script:sbomPreSealWriteSucceeded -or
        -not $preSealRejected -or
        (Get-FileHash `
            -LiteralPath $preSealMutationSbom `
            -Algorithm SHA256).Hash -cne $preSealOriginalHash) {
        throw (
            'SPDX pre-seal write mutation was not detected before publication ' +
            'or changed the original destination.')
    }
    $firstDeterministicHash = (
        Get-FileHash `
            -LiteralPath $deterministicFirstSbom `
            -Algorithm SHA256).Hash
    $secondDeterministicHash = (
        Get-FileHash `
            -LiteralPath $deterministicSecondSbom `
            -Algorithm SHA256).Hash
    $deterministicDocument =
        Get-Content -LiteralPath $deterministicFirstSbom -Raw |
            ConvertFrom-Json
    if ($firstDeterministicHash -cne $secondDeterministicHash -or
        [string]$deterministicDocument.creationInfo.created -cne
            '2000-01-01T00:00:00Z') {
        throw (
            'SPDX normalization did not replace Syft wall-clock metadata ' +
            'with deterministic SOURCE_DATE_EPOCH time.')
    }
    Write-Host (
        'PASS: equivalent Syft inputs normalize to byte-identical SPDX.'
    ) -ForegroundColor Green

    $variantInventory =
        Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
    $variantRuntimePackages = @(
        @($variantInventory.packages) |
            Where-Object { [string]$_.name -ceq 'Example.Runtime' }
    )
    if ($variantRuntimePackages.Count -ne 1) {
        throw 'Metadata-only namespace fixture did not find Example.Runtime.'
    }
    $variantRuntimePackages[0].license = 'BSD-3-Clause'
    [IO.File]::WriteAllText(
        $metadataVariantInventory,
        (($variantInventory | ConvertTo-Json -Depth 10) +
            [Environment]::NewLine),
        $utf8)
    Copy-Item -LiteralPath $preSignSbom -Destination $metadataVariantSbom
    & $enricher `
        -SbomPath $metadataVariantSbom `
        -RuntimeRoot $runtime `
        -RuntimeManifestPath $manifest `
        -InventoryPath $metadataVariantInventory `
        -LockFilePath $lockPath
    $metadataVariantDocument =
        Get-Content -LiteralPath $metadataVariantSbom -Raw |
            ConvertFrom-Json
    $baselineRuntimePackage = @(
        @($deterministicDocument.packages) |
            Where-Object { [string]$_.name -ceq 'Example.Runtime' }
    )
    $variantRuntimePackage = @(
        @($metadataVariantDocument.packages) |
            Where-Object { [string]$_.name -ceq 'Example.Runtime' }
    )
    $baselineRuntimeEvidence = @(
        @($deterministicDocument.files) |
            Where-Object { [string]$_.fileName -ceq './DesktopPet.exe' }
    )
    $variantRuntimeEvidence = @(
        @($metadataVariantDocument.files) |
            Where-Object { [string]$_.fileName -ceq './DesktopPet.exe' }
    )
    $baselineRoot = @(
        @($deterministicDocument.packages) |
            Where-Object { [string]$_.SPDXID -ceq $canonicalRootId }
    )
    $variantRoot = @(
        @($metadataVariantDocument.packages) |
            Where-Object { [string]$_.SPDXID -ceq $canonicalRootId }
    )
    if ($baselineRuntimePackage.Count -ne 1 -or
        $variantRuntimePackage.Count -ne 1 -or
        $baselineRuntimeEvidence.Count -ne 1 -or
        $variantRuntimeEvidence.Count -ne 1 -or
        $baselineRoot.Count -ne 1 -or
        $variantRoot.Count -ne 1) {
        throw 'Metadata-only namespace fixture lost canonical package evidence.'
    }
    $baselineChecksumRecords = [string[]]@(
        @($baselineRuntimeEvidence[0].checksums) |
            ForEach-Object {
                "$($_.algorithm)|$($_.checksumValue)"
            }
    )
    $variantChecksumRecords = [string[]]@(
        @($variantRuntimeEvidence[0].checksums) |
            ForEach-Object {
                "$($_.algorithm)|$($_.checksumValue)"
            }
    )
    [Array]::Sort($baselineChecksumRecords, [StringComparer]::Ordinal)
    [Array]::Sort($variantChecksumRecords, [StringComparer]::Ordinal)
    $baselineRelationshipRecords = [string[]]@(
        @($deterministicDocument.relationships) |
            ForEach-Object {
                "$($_.spdxElementId)|$($_.relationshipType)|$($_.relatedSpdxElement)"
            }
    )
    $variantRelationshipRecords = [string[]]@(
        @($metadataVariantDocument.relationships) |
            ForEach-Object {
                "$($_.spdxElementId)|$($_.relationshipType)|$($_.relatedSpdxElement)"
            }
    )
    [Array]::Sort($baselineRelationshipRecords, [StringComparer]::Ordinal)
    [Array]::Sort($variantRelationshipRecords, [StringComparer]::Ordinal)
    if ([string]$baselineRuntimePackage[0].licenseDeclared -cne 'MIT' -or
        [string]$variantRuntimePackage[0].licenseDeclared -cne
            'BSD-3-Clause' -or
        [string]$deterministicDocument.documentNamespace -ceq
            [string]$metadataVariantDocument.documentNamespace -or
        ($baselineChecksumRecords -join "`n") -cne
            ($variantChecksumRecords -join "`n") -or
        ($baselineRelationshipRecords -join "`n") -cne
            ($variantRelationshipRecords -join "`n") -or
        [string]$baselineRoot[0].packageVerificationCode.
            packageVerificationCodeValue -cne
            [string]$variantRoot[0].packageVerificationCode.
                packageVerificationCodeValue) {
        throw (
            'Metadata-only SPDX change did not preserve runtime evidence and ' +
            'graph while producing a distinct document namespace.'
        )
    }
    & (Join-Path $repoRoot 'packaging\Test-SbomInventory.ps1') `
        -SbomPath $deterministicFirstSbom `
        -InventoryPath $inventoryPath `
        -RuntimeManifestPath $manifest `
        -RuntimeRoot $runtime `
        -LockFilePath $lockPath
    & (Join-Path $repoRoot 'packaging\Test-SbomInventory.ps1') `
        -SbomPath $metadataVariantSbom `
        -InventoryPath $metadataVariantInventory `
        -RuntimeManifestPath $manifest `
        -RuntimeRoot $runtime `
        -LockFilePath $lockPath
    Write-Host (
        'PASS: metadata-only SPDX changes receive distinct document namespaces.'
    ) -ForegroundColor Green

    $enricherSource = Get-Content -LiteralPath $enricher -Raw
    foreach ($contract in @(
            '$manifestInput.ReadAllTextUtf8($maximumRuntimeManifestBytes)',
            '$inventoryInput.ReadAllTextUtf8($maximumPackageMetadataBytes)',
            '$lockInput.ReadAllTextUtf8($maximumPackageMetadataBytes)',
            'foreach ($input in $metadataInputs)')) {
        if (-not $enricherSource.Contains($contract)) {
            throw "SBOM retained metadata-input contract is missing: $contract"
        }
    }
    if ($enricherSource -match
        'Get-Content\s+-LiteralPath\s+\$resolved(?:Manifest|Inventory|LockFile)') {
        throw 'SBOM enricher still reads validated metadata inputs by path.'
    }

    foreach ($metadataCase in @(
            [pscustomobject]@{
                Name = 'manifest-hard-link'
                Target = $manifest
                Kind = 'manifest'
            },
            [pscustomobject]@{
                Name = 'inventory-hard-link'
                Target = $inventoryPath
                Kind = 'inventory'
            },
            [pscustomobject]@{
                Name = 'lock-hard-link'
                Target = $lockPath
                Kind = 'lock'
            })) {
        $alias = Join-Path $scratch ($metadataCase.Name + '.input')
        $targetHash = (
            Get-FileHash `
                -LiteralPath $metadataCase.Target `
                -Algorithm SHA256).Hash
        New-Item `
            -ItemType HardLink `
            -Path $alias `
            -Target $metadataCase.Target `
            -ErrorAction Stop | Out-Null
        try {
            $manifestArgument = if ($metadataCase.Kind -ceq 'manifest') {
                $alias
            }
            else {
                $manifest
            }
            $inventoryArgument = if ($metadataCase.Kind -ceq 'inventory') {
                $alias
            }
            else {
                $inventoryPath
            }
            $lockArgument = if ($metadataCase.Kind -ceq 'lock') {
                $alias
            }
            else {
                $lockPath
            }
            Assert-EnricherMetadataRejected `
                -Name $metadataCase.Name `
                -ManifestPath $manifestArgument `
                -InventoryPath $inventoryArgument `
                -PackageLockPath $lockArgument `
                -ExpectedMessage '(?i)hard-link alias'
            if ((Get-FileHash `
                    -LiteralPath $metadataCase.Target `
                    -Algorithm SHA256).Hash -cne $targetHash) {
                throw (
                    "SBOM metadata hard-link case '$($metadataCase.Name)' " +
                    'modified the external target.')
            }
        }
        finally {
            if (Test-Path -LiteralPath $alias) {
                Remove-Item -LiteralPath $alias -Force
            }
        }
    }

    $junctionTarget = Join-Path $scratch 'metadata-junction-target'
    $junctionPath = Join-Path $scratch 'metadata-junction'
    New-Item -ItemType Directory -Path $junctionTarget -Force | Out-Null
    Copy-Item `
        -LiteralPath $manifest `
        -Destination (Join-Path $junctionTarget 'runtime-files.txt')
    $junction = New-Item `
        -ItemType Junction `
        -Path $junctionPath `
        -Target $junctionTarget `
        -ErrorAction Stop
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw 'SBOM metadata junction fixture is not a reparse point.'
    }
    try {
        Assert-EnricherMetadataRejected `
            -Name 'manifest-ancestor-junction' `
            -ManifestPath (Join-Path $junctionPath 'runtime-files.txt') `
            -InventoryPath $inventoryPath `
            -PackageLockPath $lockPath `
            -ExpectedMessage '(?i)reparse point'
    }
    finally {
        if (Test-Path -LiteralPath $junctionPath) {
            [IO.Directory]::Delete($junctionPath)
        }
    }

    $symlinkPath = Join-Path $scratch 'manifest-symbolic-link.input'
    $symlinkCreated = $false
    try {
        try {
            New-Item `
                -ItemType SymbolicLink `
                -Path $symlinkPath `
                -Target $manifest `
                -ErrorAction Stop | Out-Null
            $symlinkCreated = $true
        }
        catch [UnauthorizedAccessException] {
            Write-Warning (
                'SBOM metadata file-symlink fixture could not run under this ' +
                'Windows token; the junction reparse regression remains active.')
        }
        if ($symlinkCreated) {
            Assert-EnricherMetadataRejected `
                -Name 'manifest-symbolic-link' `
                -ManifestPath $symlinkPath `
                -InventoryPath $inventoryPath `
                -PackageLockPath $lockPath `
                -ExpectedMessage '(?i)reparse point'
        }
    }
    finally {
        if ($symlinkCreated -and (Test-Path -LiteralPath $symlinkPath)) {
            Remove-Item -LiteralPath $symlinkPath -Force
        }
    }

    $invalidUtf8Manifest = Join-Path $scratch 'invalid-utf8-manifest.input'
    [IO.File]::WriteAllBytes(
        $invalidUtf8Manifest,
        [byte[]]@(0x44, 0x65, 0x73, 0x6b, 0xc3, 0x28))
    Assert-EnricherMetadataRejected `
        -Name 'manifest-invalid-utf8' `
        -ManifestPath $invalidUtf8Manifest `
        -InventoryPath $inventoryPath `
        -PackageLockPath $lockPath `
        -ExpectedMessage '(?i)translate bytes|fallback'

    $utf16Manifest = Join-Path $scratch 'utf16-manifest.input'
    [IO.File]::WriteAllText(
        $utf16Manifest,
        "DesktopPet.exe`n",
        (New-Object Text.UnicodeEncoding($false, $true, $true)))
    Assert-EnricherMetadataRejected `
        -Name 'manifest-utf16-bom' `
        -ManifestPath $utf16Manifest `
        -InventoryPath $inventoryPath `
        -PackageLockPath $lockPath `
        -ExpectedMessage '(?i)translate bytes|fallback'

    $utf8BomInput = Join-Path $scratch 'utf8-bom-manifest.input'
    [byte[]]$utf8BomBytes = @(
        [byte[]]@(0xef, 0xbb, 0xbf) +
        [Text.Encoding]::UTF8.GetBytes("DesktopPet.exe`n"))
    [IO.File]::WriteAllBytes($utf8BomInput, $utf8BomBytes)
    $utf8BomHandle = Open-DesktopPetValidatedInputFile `
        -Path $utf8BomInput `
        -Root $scratch
    try {
        if ($utf8BomHandle.ReadAllTextUtf8(1KB) -cne
            "DesktopPet.exe`n") {
            throw 'Strict UTF-8 input did not consume only its UTF-8 BOM.'
        }
    }
    finally {
        $utf8BomHandle.Dispose()
    }

    $swapRoot = Join-Path $scratch 'retained-input-swap'
    New-Item -ItemType Directory -Path $swapRoot -Force | Out-Null
    $swapInput = Join-Path $swapRoot 'metadata.json'
    $swapReplacement = Join-Path $swapRoot 'replacement.json'
    $swapBackup = Join-Path $swapRoot 'backup.json'
    [IO.File]::WriteAllText($swapInput, 'retained-original', $utf8)
    [IO.File]::WriteAllText($swapReplacement, 'replacement-bytes', $utf8)
    $retainedSwapInput = Open-DesktopPetValidatedInputFile `
        -Path $swapInput `
        -Root $swapRoot
    try {
        $swapBlocked = $false
        try {
            [IO.File]::Replace(
                $swapReplacement,
                $swapInput,
                $swapBackup,
                $true)
        }
        catch {
            $swapBlocked = $true
        }
        if (-not $swapBlocked -or
            $retainedSwapInput.ReadAllTextUtf8(1KB) -cne
                'retained-original' -or
            [IO.File]::ReadAllText($swapInput) -cne 'retained-original') {
            throw 'Retained SBOM metadata input did not block path replacement.'
        }
        $boundedReadFailure = $null
        try {
            [void]$retainedSwapInput.ReadAllTextUtf8(4)
        }
        catch {
            $boundedReadFailure = $_
        }
        if ($null -eq $boundedReadFailure -or
            $boundedReadFailure.Exception.Message -notmatch
                '(?i)maximum strict UTF-8 metadata size') {
            throw 'Retained metadata input did not enforce its read-size limit.'
        }
    }
    finally {
        $retainedSwapInput.Dispose()
    }
    [IO.File]::Replace(
        $swapReplacement,
        $swapInput,
        $swapBackup,
        $true)
    if ([IO.File]::ReadAllText($swapInput) -cne 'replacement-bytes' -or
        [IO.File]::ReadAllText($swapBackup) -cne 'retained-original') {
        throw 'SBOM metadata swap control did not succeed after handle disposal.'
    }

    $hardLinkTargetSbom = Join-Path $scratch 'hard-link-target.spdx.json'
    $hardLinkAliasSbom = Join-Path $scratch 'hard-link-alias.spdx.json'
    Copy-Item -LiteralPath $preSignSbom -Destination $hardLinkTargetSbom
    New-Item `
        -ItemType HardLink `
        -Path $hardLinkAliasSbom `
        -Target $hardLinkTargetSbom `
        -ErrorAction Stop | Out-Null
    $hardLinkTargetHash = (
        Get-FileHash -LiteralPath $hardLinkTargetSbom -Algorithm SHA256).Hash
    $hardLinkFailure = $null
    try {
        & $enricher `
            -SbomPath $hardLinkAliasSbom `
            -RuntimeRoot $runtime `
            -RuntimeManifestPath $manifest `
            -InventoryPath $inventoryPath `
            -LockFilePath $lockPath *> $null
    }
    catch {
        $hardLinkFailure = $_
    }
    if ($null -eq $hardLinkFailure -or
        $hardLinkFailure.Exception.Message -notmatch '(?i)hard-link alias') {
        $detail = if ($null -eq $hardLinkFailure) {
            'accepted'
        }
        else {
            $hardLinkFailure.Exception.Message
        }
        throw "SBOM enricher hard-link input fixture was not rejected: $detail"
    }
    if ((Get-FileHash `
            -LiteralPath $hardLinkTargetSbom `
            -Algorithm SHA256).Hash -cne $hardLinkTargetHash) {
        throw 'SBOM enricher hard-link rejection modified the target bytes.'
    }

    $danglingDocument = Get-Content -LiteralPath $preSignSbom -Raw |
        ConvertFrom-Json
    $danglingDocument.relationships += [pscustomobject][ordered]@{
        spdxElementId = $rootId
        relatedSpdxElement = 'SPDXRef-does-not-exist'
        relationshipType = 'CONTAINS'
    }
    Assert-EnricherRejects `
        -Name 'dangling-relationship' `
        -Document $danglingDocument `
        -ExpectedMessage 'dangling relationship endpoint'

    $duplicateRelationshipDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $duplicateRelationshipDocument.relationships +=
        $duplicateRelationshipDocument.relationships[0]
    Assert-EnricherRejects `
        -Name 'duplicate-relationship' `
        -Document $duplicateRelationshipDocument `
        -ExpectedMessage 'duplicate relationship'

    $invalidRelationshipTypeDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $packageRelationship = @(
        $invalidRelationshipTypeDocument.relationships |
            Where-Object {
                [string]$_.relatedSpdxElement -ceq $packageId
            }
    )[0]
    $packageRelationship.relationshipType = 'NOT_A_RELATIONSHIP'
    Assert-EnricherRejects `
        -Name 'invalid-relationship-type' `
        -Document $invalidRelationshipTypeDocument `
        -ExpectedMessage 'unsupported SPDX 2.3 relationship type'

    $invalidVersionDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $invalidVersionDocument.spdxVersion = 'SPDX-2.not-a-version'
    Assert-EnricherRejects `
        -Name 'invalid-version' `
        -Document $invalidVersionDocument `
        -ExpectedMessage 'Expected an SPDX 2.3 JSON document'

    $missingDescriptionDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $missingDescriptionDocument.relationships = @(
        $missingDescriptionDocument.relationships |
            Where-Object {
                [string]$_.relationshipType -cne 'DESCRIBES'
            }
    )
    Assert-EnricherRejects `
        -Name 'missing-description' `
        -Document $missingDescriptionDocument `
        -ExpectedMessage 'exactly one Syft document-root description'

    $orphanedFileDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $orphanedFileDocument.relationships = @(
        $orphanedFileDocument.relationships |
            Where-Object {
                [string]$_.relatedSpdxElement -cne $runtimeId
            }
    )
    Assert-EnricherRejects `
        -Name 'orphaned-runtime-file' `
        -Document $orphanedFileDocument `
        -ExpectedMessage 'does not CONTAIN file'

    $unrootedSyftTopologyDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $unrootedSyftTopologyDocument.relationships = @(
        $unrootedSyftTopologyDocument.relationships |
            Where-Object {
                -not (
                    [string]$_.spdxElementId -ceq $rootId -and
                    [string]$_.relationshipType -ceq 'CONTAINS' -and
                    [string]$_.relatedSpdxElement -ceq $runtimeId
                )
            }
    )
    $unrootedSyftTopologyDocument.relationships +=
        [pscustomobject][ordered]@{
            spdxElementId = $packageId
            relatedSpdxElement = $runtimeId
            relationshipType = 'OTHER'
        }
    Assert-EnricherRejects `
        -Name 'unrooted-syft-file-topology' `
        -Document $unrootedSyftTopologyDocument `
        -ExpectedMessage 'does not CONTAIN file'

    $wrongSyftFileEdgeDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $wrongSyftFileEdgeDocument.relationships = @(
        $wrongSyftFileEdgeDocument.relationships |
            Where-Object {
                -not (
                    [string]$_.spdxElementId -ceq $rootId -and
                    [string]$_.relationshipType -ceq 'CONTAINS' -and
                    [string]$_.relatedSpdxElement -ceq $runtimeId
                )
            }
    )
    $wrongSyftFileEdgeDocument.relationships += @(
        [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relatedSpdxElement = $packageId
            relationshipType = 'CONTAINS'
        },
        [pscustomobject][ordered]@{
            spdxElementId = $packageId
            relatedSpdxElement = $runtimeId
            relationshipType = 'DEPENDENCY_OF'
        }
    )
    Assert-EnricherRejects `
        -Name 'wrong-syft-file-edge' `
        -Document $wrongSyftFileEdgeDocument `
        -ExpectedMessage 'does not CONTAIN file'

    $undeclaredExternalDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $undeclaredExternalDocument.relationships +=
        [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relatedSpdxElement =
                'DocumentRef-undeclared:SPDXRef-external-file'
            relationshipType = 'CONTAINS'
        }
    Assert-EnricherRejects `
        -Name 'undeclared-external-document' `
        -Document $undeclaredExternalDocument `
        -ExpectedMessage 'dangling relationship endpoint'

    $invalidDocumentId =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $invalidDocumentId.SPDXID = 'not-an-spdx-id'
    Assert-EnricherRejects `
        -Name 'invalid-document-spdx-id' `
        -Document $invalidDocumentId `
        -ExpectedMessage 'document ID must be exactly'

    $externalDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $externalDocument | Add-Member `
        -NotePropertyName externalDocumentRefs `
        -NotePropertyValue @(
            [pscustomobject][ordered]@{
                externalDocumentId = 'DocumentRef-external'
                spdxDocument = 'https://example.invalid/external.spdx.json'
                checksum = [pscustomobject][ordered]@{
                    algorithm = 'SHA256'
                    checksumValue = ('0' * 64)
                }
            }
        )
    Assert-EnricherRejects `
        -Name 'external-document-reference' `
        -Document $externalDocument `
        -ExpectedMessage 'must be self-contained'

    $disconnectedPackage =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $disconnectedPackage.relationships = @(
        $disconnectedPackage.relationships |
            Where-Object {
                [string]$_.relatedSpdxElement -cne $packageId
            }
    )
    Assert-EnricherRejects `
        -Name 'disconnected-locked-package' `
        -Document $disconnectedPackage `
        -ExpectedMessage 'disconnected from the SPDX document root'

    $disconnectedNonLockPackage =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $disconnectedNonLockPackage.packages += [pscustomobject][ordered]@{
        name = 'Disconnected non-lock package'
        versionInfo = '1.0.0'
        SPDXID = 'SPDXRef-Package-disconnected-non-lock'
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseDeclared = 'NOASSERTION'
        licenseConcluded = 'NOASSERTION'
    }
    Assert-EnricherRejects `
        -Name 'disconnected-non-lock-package' `
        -Document $disconnectedNonLockPackage `
        -ExpectedMessage 'Local package .* is disconnected from the SPDX document root'

    Write-Host (
        'PASS: SBOM enricher rejected 13 graph and version negative controls.'
    )

    $syftTopologyDocument =
        Get-Content -LiteralPath $preSignSbom -Raw | ConvertFrom-Json
    $syftTopologyDocument.relationships = @(
        $syftTopologyDocument.relationships |
            Where-Object {
                -not (
                    [string]$_.spdxElementId -ceq $rootId -and
                    [string]$_.relationshipType -ceq 'CONTAINS' -and
                    [string]$_.relatedSpdxElement -in @(
                        $lockId,
                        $runtimeId
                    )
                )
            }
    )
    $syftTopologyDocument.relationships += @(
        [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relatedSpdxElement = $packageId
            relationshipType = 'CONTAINS'
        },
        [pscustomobject][ordered]@{
            spdxElementId = $packageId
            relatedSpdxElement = $lockId
            relationshipType = 'OTHER'
        },
        [pscustomobject][ordered]@{
            spdxElementId = $packageId
            relatedSpdxElement = $runtimeId
            relationshipType = 'OTHER'
        }
    )
    $syftRuntimeFile = @(
        $syftTopologyDocument.files |
            Where-Object {
                [string]$_.SPDXID -ceq $runtimeId
            }
    )
    if ($syftRuntimeFile.Count -ne 1) {
        throw 'Syft topology fixture does not contain exactly one runtime file.'
    }
    $syftRuntimeFile[0].checksums = @(
        [pscustomobject][ordered]@{
            algorithm = 'SHA1'
            checksumValue = ('0' * 40)
        }
    )
    [IO.File]::WriteAllText(
        $syftTopologySbom,
        (($syftTopologyDocument | ConvertTo-Json -Depth 20) +
            [Environment]::NewLine),
        $utf8)
    & $enricher `
        -SbomPath $syftTopologySbom `
        -RuntimeRoot $runtime `
        -RuntimeManifestPath $manifest `
        -InventoryPath $inventoryPath `
        -LockFilePath $lockPath
    $normalizedSyftTopology =
        Get-Content -LiteralPath $syftTopologySbom -Raw | ConvertFrom-Json
    $normalizedRuntimeContainment = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq $canonicalRootId -and
                [string]$_.relationshipType -ceq 'CONTAINS' -and
                [string]$_.relatedSpdxElement -ceq $runtimeId
            }
    )
    if ($normalizedRuntimeContainment.Count -ne 1 -or
        @($normalizedSyftTopology.files).Count -ne 1) {
        throw 'Syft 1.42 file topology was not normalized to exact root containment.'
    }
    $normalizedSyftChecksums = @(
        $normalizedSyftTopology.files[0].checksums
    )
    if ($normalizedSyftChecksums.Count -ne 2 -or
        [string]$normalizedSyftChecksums[0].algorithm -cne 'SHA1' -or
        [string]$normalizedSyftChecksums[0].checksumValue -cne $unsignedSha1 -or
        [string]$normalizedSyftChecksums[1].algorithm -cne 'SHA256' -or
        [string]$normalizedSyftChecksums[1].checksumValue -cne $unsignedHash) {
        throw (
            'Syft all-zero checksum placeholder was not replaced with the ' +
            'canonical staged-runtime evidence.'
        )
    }
    $normalizedRoot = @(
        $normalizedSyftTopology.packages |
            Where-Object {
                [string]$_.SPDXID -ceq $canonicalRootId
            }
    )
    $normalizedPackages = @(
        $normalizedSyftTopology.packages |
            Where-Object {
                [string]$_.SPDXID -cne $canonicalRootId
            }
    )
    $normalizedPackage = @(
        $normalizedPackages |
            Where-Object {
                @($_.externalRefs | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Runtime@1.2.3'
                }).Count -eq 1
            }
    )
    $normalizedDependencyPackage = @(
        $normalizedPackages |
            Where-Object {
                @($_.externalRefs | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Dependency@2.0.0'
                }).Count -eq 1
            }
    )
    $normalizedCompilerPackage = @(
        $normalizedPackages |
            Where-Object {
                @($_.externalRefs | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Compiler@3.0.0'
                }).Count -eq 1
            }
    )
    $normalizedReferencePackage = @(
        $normalizedPackages |
            Where-Object {
                @($_.externalRefs | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.ReferenceAssemblies@4.0.0'
                }).Count -eq 1
            }
    )
    $normalizedProvenance = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq $runtimeId -and
                [string]$_.relationshipType -ceq 'GENERATED_FROM' -and
                [string]$_.relatedSpdxElement -ceq
                    [string]$normalizedPackage[0].SPDXID
            }
    )
    $invalidPackageContainment = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -cin @(
                    [string]$normalizedPackage[0].SPDXID,
                    [string]$normalizedDependencyPackage[0].SPDXID,
                    [string]$normalizedCompilerPackage[0].SPDXID,
                    [string]$normalizedReferencePackage[0].SPDXID
                ) -and
                [string]$_.relationshipType -ceq 'CONTAINS'
            }
    )
    $normalizedRootDependencies = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq $canonicalRootId -and
                [string]$_.relationshipType -ceq 'DEPENDS_ON'
            }
    )
    $normalizedPackageDependencies = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$normalizedPackage[0].SPDXID -and
                [string]$_.relationshipType -ceq 'DEPENDS_ON'
            }
    )
    $normalizedBuildToolEdges = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$normalizedCompilerPackage[0].SPDXID -and
                [string]$_.relationshipType -ceq 'BUILD_TOOL_OF' -and
                [string]$_.relatedSpdxElement -ceq $canonicalRootId
            }
    )
    $normalizedBuildDependencyEdges = @(
        $normalizedSyftTopology.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$normalizedReferencePackage[0].SPDXID -and
                [string]$_.relationshipType -ceq
                    'BUILD_DEPENDENCY_OF' -and
                [string]$_.relatedSpdxElement -ceq $canonicalRootId
            }
    )
    if ($normalizedRoot.Count -ne 1 -or
        $normalizedRoot[0].filesAnalyzed -ne $true -or
        [string]$normalizedRoot[0].packageVerificationCode.
            packageVerificationCodeValue -notmatch '^[0-9a-f]{40}$' -or
        $normalizedPackages.Count -ne 4 -or
        $normalizedPackage.Count -ne 1 -or
        $normalizedDependencyPackage.Count -ne 1 -or
        $normalizedCompilerPackage.Count -ne 1 -or
        $normalizedReferencePackage.Count -ne 1 -or
        $normalizedPackage[0].filesAnalyzed -ne $false -or
        $normalizedDependencyPackage[0].filesAnalyzed -ne $false -or
        $normalizedCompilerPackage[0].filesAnalyzed -ne $false -or
        $normalizedReferencePackage[0].filesAnalyzed -ne $false -or
        $normalizedProvenance.Count -ne 1 -or
        $invalidPackageContainment.Count -ne 0 -or
        $normalizedRootDependencies.Count -ne 1 -or
        [string]$normalizedRootDependencies[0].relatedSpdxElement -cne
            [string]$normalizedPackage[0].SPDXID -or
        $normalizedPackageDependencies.Count -ne 1 -or
        [string]$normalizedPackageDependencies[0].relatedSpdxElement -cne
            [string]$normalizedDependencyPackage[0].SPDXID -or
        $normalizedBuildToolEdges.Count -ne 1 -or
        $normalizedBuildDependencyEdges.Count -ne 1) {
        throw (
            'Normalized SBOM did not enforce root analysis semantics, exact ' +
            'runtime/build dependency topology, and GENERATED_FROM package ' +
            'provenance.'
        )
    }
    Write-Host (
        'PASS: Syft 1.42 package OTHER file topology normalized to root containment.'
    ) -ForegroundColor Green

    Copy-Item -LiteralPath $preSignSbom -Destination $enrichedSbom

    & $enricher `
        -SbomPath $enrichedSbom `
        -RuntimeRoot $runtime `
        -RuntimeManifestPath $manifest `
        -InventoryPath $inventoryPath `
        -LockFilePath $lockPath
    $canonicalHashBeforeRefresh = (
        Get-FileHash -LiteralPath $enrichedSbom -Algorithm SHA256
    ).Hash
    & $enricher `
        -SbomPath $enrichedSbom `
        -RuntimeRoot $runtime `
        -RuntimeManifestPath $manifest `
        -InventoryPath $inventoryPath `
        -LockFilePath $lockPath `
        -RefreshRuntimeEvidence
    $canonicalHashAfterRefresh = (
        Get-FileHash -LiteralPath $enrichedSbom -Algorithm SHA256
    ).Hash
    if ($canonicalHashAfterRefresh -cne $canonicalHashBeforeRefresh) {
        throw 'Canonical SBOM refresh was not byte-for-byte idempotent.'
    }

    [IO.File]::WriteAllBytes(
        $runtimeFile,
        [Text.Encoding]::ASCII.GetBytes('signed-runtime'))
    $signedHash = (
        Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $signedSha1 = (
        Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    $verificationCodeAlgorithm = [Security.Cryptography.SHA1]::Create()
    try {
        $signedPackageVerificationCode = ([BitConverter]::ToString(
            $verificationCodeAlgorithm.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($signedSha1)))).
            Replace('-', '').ToLowerInvariant()
    }
    finally {
        $verificationCodeAlgorithm.Dispose()
    }
    Copy-Item -LiteralPath $preSignSbom -Destination $negativeSbom

    $failedClosed = $false
    try {
        & $enricher `
            -SbomPath $negativeSbom `
            -RuntimeRoot $runtime `
            -RuntimeManifestPath $manifest `
            -InventoryPath $inventoryPath `
            -LockFilePath $lockPath
    }
    catch {
        $failedClosed =
            $_.Exception.Message -match 'disagrees with the staged file'
    }
    if (-not $failedClosed) {
        throw 'Changed runtime evidence did not fail closed without the refresh switch.'
    }

    & $enricher `
        -SbomPath $enrichedSbom `
        -RuntimeRoot $runtime `
        -RuntimeManifestPath $manifest `
        -InventoryPath $inventoryPath `
        -LockFilePath $lockPath `
        -RefreshRuntimeEvidence

    $refreshed = Get-Content -LiteralPath $enrichedSbom -Raw |
        ConvertFrom-Json
    $files = @($refreshed.files)
    if ($files.Count -ne 1 -or
        [string]$files[0].fileName -cne './DesktopPet.exe') {
        throw 'Refreshed SBOM file evidence is not the exact one-file manifest.'
    }
    $checksums = @($files[0].checksums)
    if ($checksums.Count -ne 2 -or
        [string]$checksums[0].algorithm -cne 'SHA1' -or
        [string]$checksums[0].checksumValue -cne $signedSha1 -or
        [string]$checksums[1].algorithm -cne 'SHA256' -or
        [string]$checksums[1].checksumValue -cne $signedHash) {
        throw (
            'Refreshed SBOM does not contain exactly the signed runtime ' +
            'SHA-1 and SHA-256.'
        )
    }
    $refreshedPackage = @(
        $refreshed.packages |
            Where-Object {
                $externalRefsProperty =
                    $_.PSObject.Properties['externalRefs']
                if ($null -eq $externalRefsProperty) {
                    return $false
                }
                @($externalRefsProperty.Value | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Runtime@1.2.3'
                }).Count -eq 1
            }
    )
    $refreshedDependencyPackage = @(
        $refreshed.packages |
            Where-Object {
                $externalRefsProperty =
                    $_.PSObject.Properties['externalRefs']
                if ($null -eq $externalRefsProperty) {
                    return $false
                }
                @($externalRefsProperty.Value | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Dependency@2.0.0'
                }).Count -eq 1
            }
    )
    $refreshedCompilerPackage = @(
        $refreshed.packages |
            Where-Object {
                $externalRefsProperty =
                    $_.PSObject.Properties['externalRefs']
                if ($null -eq $externalRefsProperty) {
                    return $false
                }
                @($externalRefsProperty.Value | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.Compiler@3.0.0'
                }).Count -eq 1
            }
    )
    $refreshedReferencePackage = @(
        $refreshed.packages |
            Where-Object {
                $externalRefsProperty =
                    $_.PSObject.Properties['externalRefs']
                if ($null -eq $externalRefsProperty) {
                    return $false
                }
                @($externalRefsProperty.Value | Where-Object {
                    [string]$_.referenceLocator -ceq
                        'pkg:nuget/Example.ReferenceAssemblies@4.0.0'
                }).Count -eq 1
            }
    )
    if (@($refreshed.packages).Count -ne 5 -or
        $refreshedPackage.Count -ne 1 -or
        $refreshedDependencyPackage.Count -ne 1 -or
        $refreshedCompilerPackage.Count -ne 1 -or
        $refreshedReferencePackage.Count -ne 1 -or
        $refreshedPackage[0].filesAnalyzed -ne $false -or
        [string]$refreshedPackage[0].licenseDeclared -cne 'MIT' -or
        [string]$refreshedPackage[0].licenseConcluded -cne 'MIT' -or
        [string]$refreshedPackage[0].downloadLocation -cne (
            'https://api.nuget.org/v3-flatcontainer/' +
            'example.runtime/1.2.3/example.runtime.1.2.3.nupkg') -or
        @($refreshedPackage[0].checksums).Count -ne 1 -or
        [string]$refreshedPackage[0].checksums[0].algorithm -cne
            'SHA512' -or
        [string]$refreshedPackage[0].checksums[0].checksumValue -cne
            $lockSha512Hex -or
        [string]$refreshedPackage[0].sourceInfo -notmatch
            'third-party-packages\.json' -or
        $refreshedDependencyPackage[0].filesAnalyzed -ne $false -or
        [string]$refreshedDependencyPackage[0].licenseDeclared -cne
            'Apache-2.0' -or
        [string]$refreshedDependencyPackage[0].licenseConcluded -cne
            'Apache-2.0' -or
        [string]$refreshedDependencyPackage[0].downloadLocation -cne (
            'https://api.nuget.org/v3-flatcontainer/' +
            'example.dependency/2.0.0/example.dependency.2.0.0.nupkg') -or
        @($refreshedDependencyPackage[0].checksums).Count -ne 1 -or
        [string]$refreshedDependencyPackage[0].checksums[0].algorithm -cne
            'SHA512' -or
        [string]$refreshedDependencyPackage[0].checksums[0].checksumValue -cne
            $lockSha512Hex -or
        [string]$refreshedDependencyPackage[0].sourceInfo -notmatch
            'third-party-packages\.json' -or
        $refreshedCompilerPackage[0].filesAnalyzed -ne $false -or
        [string]$refreshedCompilerPackage[0].sourceInfo -notmatch
            "root relationship 'BUILD_TOOL_OF'" -or
        $refreshedReferencePackage[0].filesAnalyzed -ne $false -or
        [string]$refreshedReferencePackage[0].sourceInfo -notmatch
            "root relationship 'BUILD_DEPENDENCY_OF'") {
        throw (
            'Refreshed SBOM did not prune inferred identities and retain only ' +
            'the curated runtime and build-only lock/inventory packages.'
        )
    }
    $refreshedProvenance = @(
        $refreshed.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$files[0].SPDXID -and
                [string]$_.relationshipType -ceq 'GENERATED_FROM' -and
                [string]$_.relatedSpdxElement -ceq
                    [string]$refreshedPackage[0].SPDXID
            }
    )
    if ($refreshedProvenance.Count -ne 1) {
        throw 'Refreshed SBOM lost exact GENERATED_FROM package provenance.'
    }
    $refreshedRootDependencies = @(
        $refreshed.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq $canonicalRootId -and
                [string]$_.relationshipType -ceq 'DEPENDS_ON'
            }
    )
    $refreshedPackageDependencies = @(
        $refreshed.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$refreshedPackage[0].SPDXID -and
                [string]$_.relationshipType -ceq 'DEPENDS_ON'
            }
    )
    $refreshedBuildToolEdges = @(
        $refreshed.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$refreshedCompilerPackage[0].SPDXID -and
                [string]$_.relationshipType -ceq 'BUILD_TOOL_OF' -and
                [string]$_.relatedSpdxElement -ceq $canonicalRootId
            }
    )
    $refreshedBuildDependencyEdges = @(
        $refreshed.relationships |
            Where-Object {
                [string]$_.spdxElementId -ceq
                    [string]$refreshedReferencePackage[0].SPDXID -and
                [string]$_.relationshipType -ceq
                    'BUILD_DEPENDENCY_OF' -and
                [string]$_.relatedSpdxElement -ceq $canonicalRootId
            }
    )
    if ($refreshedRootDependencies.Count -ne 1 -or
        [string]$refreshedRootDependencies[0].relatedSpdxElement -cne
            [string]$refreshedPackage[0].SPDXID -or
        $refreshedPackageDependencies.Count -ne 1 -or
        [string]$refreshedPackageDependencies[0].relatedSpdxElement -cne
            [string]$refreshedDependencyPackage[0].SPDXID -or
        $refreshedBuildToolEdges.Count -ne 1 -or
        $refreshedBuildDependencyEdges.Count -ne 1) {
        throw (
            'Refreshed SBOM lost exact runtime or build-only dependency ' +
            'topology.'
        )
    }
    if (@($refreshed.creationInfo.creators) -cnotcontains
        'Tool: DesktopPet SPDX runtime normalizer' -or
        @($refreshed.creationInfo.creators) -cnotcontains
        'Tool: DesktopPet self-test') {
        throw 'Refreshed SBOM did not preserve and extend creator attribution.'
    }
    $refreshedRoot = @(
        $refreshed.packages |
            Where-Object {
                [string]$_.SPDXID -ceq $canonicalRootId
            }
    )
    if ($refreshedRoot.Count -ne 1 -or
        $refreshedRoot[0].filesAnalyzed -ne $true -or
        [string]$refreshedRoot[0].packageVerificationCode.
            packageVerificationCodeValue -cne
                $signedPackageVerificationCode) {
        throw (
            'Refreshed SBOM root analysis and packageVerificationCode do not ' +
            'match the signed runtime SHA-1 evidence.'
        )
    }

    & (Join-Path $repoRoot 'packaging\Test-SbomInventory.ps1') `
        -SbomPath $enrichedSbom `
        -InventoryPath $inventoryPath `
        -RuntimeManifestPath $manifest `
        -RuntimeRoot $runtime `
        -LockFilePath $lockPath

    Write-Host 'PASS: post-sign SBOM runtime evidence refresh self-test.' `
        -ForegroundColor Green
}
finally {
    if ($null -eq $originalSourceDateEpoch) {
        Remove-Item Env:SOURCE_DATE_EPOCH -ErrorAction SilentlyContinue
    }
    else {
        $env:SOURCE_DATE_EPOCH = $originalSourceDateEpoch
    }
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-SbomRefresh-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
