#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$validator = Join-Path $repoRoot 'packaging\Test-SbomInventory.ps1'
. (Join-Path $repoRoot 'packaging\SpdxDocumentIdentity.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-SbomInventory-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 30) + [Environment]::NewLine),
        $utf8)
}

try {
    $runtime = Join-Path $scratch 'runtime'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    $runtimeExecutable = Join-Path $runtime 'DesktopPet.exe'
    $runtimeLibrary = Join-Path $runtime 'Example.Runtime.dll'
    [IO.File]::WriteAllBytes(
        $runtimeExecutable,
        [Text.Encoding]::ASCII.GetBytes('desktop-pet-runtime'))
    [IO.File]::WriteAllBytes(
        $runtimeLibrary,
        [Text.Encoding]::ASCII.GetBytes('example-runtime-library'))
    $executableSha1 = (
        Get-FileHash -LiteralPath $runtimeExecutable -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    $executableSha256 = (
        Get-FileHash -LiteralPath $runtimeExecutable -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $librarySha1 = (
        Get-FileHash -LiteralPath $runtimeLibrary -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    $librarySha256 = (
        Get-FileHash -LiteralPath $runtimeLibrary -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $lockSha512Bytes = New-Object byte[] 64
    $lockSha512Base64 = [Convert]::ToBase64String($lockSha512Bytes)
    $lockSha512Hex = ([BitConverter]::ToString(
        $lockSha512Bytes)).Replace('-', '').ToLowerInvariant()
    $verificationSha1Values = [string[]]@(
        $executableSha1,
        $librarySha1
    )
    [Array]::Sort($verificationSha1Values, [StringComparer]::Ordinal)
    $verificationCodeAlgorithm = [Security.Cryptography.SHA1]::Create()
    try {
        $packageVerificationCode = ([BitConverter]::ToString(
            $verificationCodeAlgorithm.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes(
                    ($verificationSha1Values -join ''))))).
            Replace('-', '').ToLowerInvariant()
    }
    finally {
        $verificationCodeAlgorithm.Dispose()
    }

    $manifestPath = Join-Path $scratch 'runtime-files.txt'
    [IO.File]::WriteAllText(
        $manifestPath,
        "DesktopPet.exe`r`nExample.Runtime.dll`r`n",
        $utf8)
    $inventoryPath = Join-Path $scratch 'third-party-packages.json'
    $lockPath = Join-Path $scratch 'packages.lock.json'
    $targetFramework = '.NETFramework,Version=v4.8'
    Write-JsonFile -Path $inventoryPath -Value ([pscustomobject][ordered]@{
        schemaVersion = 1
        targetFramework = $targetFramework
        packages = @(
            [pscustomobject][ordered]@{
                name = 'Example.Runtime'
                version = '1.2.3'
                license = 'MIT'
                runtimeFiles = @('Example.Runtime.dll')
            }
        )
    })
    $lockTarget = [pscustomobject]@{}
    $lockTarget | Add-Member `
        -NotePropertyName 'Example.Runtime' `
        -NotePropertyValue ([pscustomobject][ordered]@{
            type = 'Direct'
            requested = '[1.2.3, )'
            resolved = '1.2.3'
            contentHash = $lockSha512Base64
        })
    $lockDependencies = [pscustomobject]@{}
    $lockDependencies | Add-Member `
        -NotePropertyName $targetFramework `
        -NotePropertyValue $lockTarget
    Write-JsonFile -Path $lockPath -Value ([pscustomobject][ordered]@{
        version = 1
        dependencies = $lockDependencies
    })

    $documentId = 'SPDXRef-DOCUMENT'
    $rootId = 'SPDXRef-Package-DesktopPet-AI-Edition-Runtime'
    $packageId = 'SPDXRef-Package-example-runtime'
    $executableId = 'SPDXRef-File-desktop-pet'
    $libraryId = 'SPDXRef-File-example-runtime'
    $baseline = [pscustomobject][ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = $documentId
        name = 'DesktopPet-AI-Edition-Windows-x64-runtime'
        documentNamespace =
            'urn:desktop-pet:spdx-document-identity-pending'
        creationInfo = [pscustomobject][ordered]@{
            created = '2000-01-01T00:00:00Z'
            creators = @(
                'Tool: Syft self-test',
                'Tool: DesktopPet SPDX runtime normalizer'
            )
        }
        packages = @(
            [pscustomobject][ordered]@{
                name = 'DesktopPet AI Edition Windows x64 runtime'
                SPDXID = $rootId
                downloadLocation = 'NOASSERTION'
                filesAnalyzed = $true
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
                packageVerificationCode = [pscustomobject][ordered]@{
                    packageVerificationCodeValue =
                        $packageVerificationCode
                }
            },
            [pscustomobject][ordered]@{
                name = 'Example.Runtime'
                versionInfo = '1.2.3'
                SPDXID = $packageId
                sourceInfo = (
                    'Canonical NuGet identity from packages.lock.json and ' +
                    'packaging/third-party-packages.json.')
                licenseDeclared = 'MIT'
                licenseConcluded = 'MIT'
                copyrightText = 'NOASSERTION'
                downloadLocation = (
                    'https://api.nuget.org/v3-flatcontainer/' +
                    'example.runtime/1.2.3/example.runtime.1.2.3.nupkg')
                filesAnalyzed = $false
                checksums = @(
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA512'
                        checksumValue = $lockSha512Hex
                    }
                )
                externalRefs = @(
                    [pscustomobject][ordered]@{
                        referenceCategory = 'PACKAGE-MANAGER'
                        referenceType = 'purl'
                        referenceLocator = 'pkg:nuget/Example.Runtime@1.2.3'
                    }
                )
            }
        )
        files = @(
            [pscustomobject][ordered]@{
                fileName = './DesktopPet.exe'
                SPDXID = $executableId
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
                checksums = @(
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA1'
                        checksumValue = $executableSha1
                    },
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA256'
                        checksumValue = $executableSha256
                    }
                )
            },
            [pscustomobject][ordered]@{
                fileName = './Example.Runtime.dll'
                SPDXID = $libraryId
                licenseConcluded = 'NOASSERTION'
                copyrightText = 'NOASSERTION'
                checksums = @(
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA1'
                        checksumValue = $librarySha1
                    },
                    [pscustomobject][ordered]@{
                        algorithm = 'SHA256'
                        checksumValue = $librarySha256
                    }
                )
            }
        )
        relationships = @(
            [pscustomobject][ordered]@{
                spdxElementId = $documentId
                relationshipType = 'DESCRIBES'
                relatedSpdxElement = $rootId
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relationshipType = 'CONTAINS'
                relatedSpdxElement = $executableId
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relationshipType = 'CONTAINS'
                relatedSpdxElement = $libraryId
            },
            [pscustomobject][ordered]@{
                spdxElementId = $rootId
                relationshipType = 'DEPENDS_ON'
                relatedSpdxElement = $packageId
            },
            [pscustomobject][ordered]@{
                spdxElementId = $libraryId
                relationshipType = 'GENERATED_FROM'
                relatedSpdxElement = $packageId
            }
        )
    }
    $baseline.documentNamespace = (
        'https://github.com/bigfnj/desktopPet/spdx/runtime-document/v1/' +
        (Get-DesktopPetSpdxDocumentIdentity -Document $baseline))
    $baselinePath = Join-Path $scratch 'baseline.spdx.json'
    Write-JsonFile -Value $baseline -Path $baselinePath
    & $validator `
        -SbomPath $baselinePath `
        -InventoryPath $inventoryPath `
        -RuntimeManifestPath $manifestPath `
        -RuntimeRoot $runtime `
        -LockFilePath $lockPath

    $baselineHash = (
        Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash
    $script:inventorySchemaBoundaryHookReached = $false
    $script:inventorySchemaBoundaryMoveBlocked = $false
    $script:DesktopPetStagingMutationTestHook = {
        param($observedOperation, $observedPath)
        if ($observedOperation -cne
            'sbom-inventory-semantic-before-schema') {
            return
        }
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
        $script:inventorySchemaBoundaryHookReached = $true
        try {
            Move-Item `
                -LiteralPath $observedPath `
                -Destination ($observedPath + '.substituted') `
                -ErrorAction Stop
        }
        catch {
            $script:inventorySchemaBoundaryMoveBlocked = $true
        }
    }
    try {
        & {
            . $validator `
                -SbomPath $baselinePath `
                -InventoryPath $inventoryPath `
                -RuntimeManifestPath $manifestPath `
                -RuntimeRoot $runtime `
                -LockFilePath $lockPath
        }
    }
    finally {
        Remove-Variable `
            -Name DesktopPetStagingMutationTestHook `
            -Scope Script `
            -ErrorAction SilentlyContinue
    }
    if (-not $script:inventorySchemaBoundaryHookReached -or
        -not $script:inventorySchemaBoundaryMoveBlocked -or
        -not (Test-Path -LiteralPath $baselinePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash -cne
            $baselineHash) {
        throw (
            'SBOM semantic/schema boundary did not retain and validate the ' +
            'same exact document identity.')
    }

    $negativeControlCount = 0
    function Invoke-NegativeControl {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][scriptblock]$Mutation,
            [Parameter(Mandatory = $true)][string]$ExpectedMessage
        )

        $document = Get-Content -LiteralPath $baselinePath -Raw |
            ConvertFrom-Json
        & $Mutation $document
        $path = Join-Path $scratch "$Name.spdx.json"
        Write-JsonFile -Value $document -Path $path
        $accepted = $true
        $rejectionMessage = ''
        try {
            & $validator `
                -SbomPath $path `
                -InventoryPath $inventoryPath `
                -RuntimeManifestPath $manifestPath `
                -RuntimeRoot $runtime `
                -LockFilePath $lockPath *> $null
        }
        catch {
            $accepted = $false
            $rejectionMessage = $_.Exception.Message
        }
        if ($accepted) {
            throw "SBOM negative control was accepted: $Name"
        }
        if ($rejectionMessage -notmatch $ExpectedMessage) {
            throw "SBOM negative control '$Name' failed for an unexpected reason: $rejectionMessage"
        }
        $script:negativeControlCount++
    }

    Invoke-NegativeControl 'missing-runtime-evidence' {
        param($document)
        $document.files = @($document.files | Select-Object -Skip 1)
    } 'exact runtime manifest'
    Invoke-NegativeControl 'local-document-name' {
        param($document)
        $document.name = 'D:\checkout\build\sbom-input'
    } 'canonical portable runtime name'
    Invoke-NegativeControl 'local-document-namespace' {
        param($document)
        $document.documentNamespace =
            'https://anchore.com/syft/dir/D-checkout-build-sbom-input'
    } 'not bound to the exact canonical document identity'
    Invoke-NegativeControl 'local-root-name' {
        param($document)
        $document.packages[0].name =
            'D:\checkout\build\sbom-input'
    } 'canonical portable name'
    Invoke-NegativeControl 'local-root-id' {
        param($document)
        $oldRootId = [string]$document.packages[0].SPDXID
        $localRootId = 'SPDXRef-DocumentRoot-D--checkout-build-sbom-input'
        $document.packages[0].SPDXID = $localRootId
        foreach ($relationship in @($document.relationships)) {
            if ([string]$relationship.spdxElementId -ceq $oldRootId) {
                $relationship.spdxElementId = $localRootId
            }
            if ([string]$relationship.relatedSpdxElement -ceq $oldRootId) {
                $relationship.relatedSpdxElement = $localRootId
            }
        }
    } 'canonical portable ID'
    Invoke-NegativeControl 'extra-runtime-evidence' {
        param($document)
        $document.files += [pscustomobject][ordered]@{
            fileName = './unexpected.bin'
            SPDXID = 'SPDXRef-File-unexpected'
            checksums = @(
                [pscustomobject][ordered]@{
                    algorithm = 'SHA256'
                    checksumValue = ('0' * 64)
                }
            )
        }
    } 'exact runtime manifest'
    Invoke-NegativeControl 'wrong-runtime-sha1' {
        param($document)
        $document.files[0].checksums[0].checksumValue = ('0' * 40)
    } 'does not match the canonical input'
    Invoke-NegativeControl 'wrong-runtime-sha256' {
        param($document)
        $document.files[0].checksums[1].checksumValue = ('0' * 64)
    } 'does not match the canonical input'
    Invoke-NegativeControl 'duplicate-runtime-sha1' {
        param($document)
        $document.files[0].checksums +=
            $document.files[0].checksums[0]
    } 'exactly one SHA-1 and one SHA-256'
    Invoke-NegativeControl 'duplicate-runtime-sha256' {
        param($document)
        $document.files[0].checksums +=
            $document.files[0].checksums[1]
    } 'exactly one SHA-1 and one SHA-256'
    Invoke-NegativeControl 'extra-runtime-checksum' {
        param($document)
        $document.files[0].checksums +=
            [pscustomobject][ordered]@{
                algorithm = 'MD5'
                checksumValue = ('0' * 32)
            }
    } 'exactly one SHA-1 and one SHA-256'
    Invoke-NegativeControl 'missing-root-package-verification-code' {
        param($document)
        $document.packages[0].PSObject.Properties.Remove(
            'packageVerificationCode')
    } 'packageVerificationCode does not match'
    Invoke-NegativeControl 'wrong-root-package-verification-code' {
        param($document)
        $document.packages[0].packageVerificationCode.
            packageVerificationCodeValue = ('0' * 40)
    } 'packageVerificationCode does not match'
    Invoke-NegativeControl 'root-files-analyzed-false' {
        param($document)
        $document.packages[0].filesAnalyzed = $false
    } 'document-root package must set filesAnalyzed=true'
    Invoke-NegativeControl 'nuget-files-analyzed-true' {
        param($document)
        $document.packages[1].filesAnalyzed = $true
    } 'metadata-only NuGet package .* filesAnalyzed=false'
    Invoke-NegativeControl 'altered-locked-identity' {
        param($document)
        $document.packages[1].externalRefs[0].referenceLocator =
            'pkg:nuget/Negative.Control@0.0.0'
    } 'unrecognized NuGet identity'
    Invoke-NegativeControl 'missing-locked-identity' {
        param($document)
        $document.packages[1].externalRefs = @()
    } 'canonical package metadata and inventory disagree'
    Invoke-NegativeControl 'duplicate-package-spdx-id' {
        param($document)
        $document.packages[1].SPDXID = $document.packages[0].SPDXID
    } 'duplicate package SPDX ID'
    Invoke-NegativeControl 'duplicate-file-spdx-id' {
        param($document)
        $document.files[1].SPDXID = $document.files[0].SPDXID
    } 'duplicate file SPDX ID'
    Invoke-NegativeControl 'dangling-relationship-target' {
        param($document)
        $document.relationships += [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relationshipType = 'CONTAINS'
            relatedSpdxElement = 'SPDXRef-does-not-exist'
        }
    } 'dangling relationship endpoint'
    Invoke-NegativeControl 'duplicate-relationship' {
        param($document)
        $document.relationships += $document.relationships[0]
    } 'duplicate relationship'
    Invoke-NegativeControl 'invalid-relationship-type' {
        param($document)
        $packageRelationship = @(
            $document.relationships |
                Where-Object {
                    [string]$_.relatedSpdxElement -ceq $packageId
                }
        )[0]
        $packageRelationship.relationshipType = 'NOT_A_RELATIONSHIP'
    } 'unsupported SPDX 2.3 relationship type'
    Invoke-NegativeControl 'invalid-spdx-version' {
        param($document)
        $document.spdxVersion = 'SPDX-2.not-a-version'
    } 'Expected an SPDX 2.3 JSON document'
    Invoke-NegativeControl 'invalid-data-license' {
        param($document)
        $document.dataLicense = 'NOASSERTION'
    } 'Expected SPDX dataLicense CC0-1.0'
    Invoke-NegativeControl 'missing-normalizer-creator' {
        param($document)
        $document.creationInfo.creators = @('Tool: Syft self-test')
    } 'must preserve generator attribution and identify'
    Invoke-NegativeControl 'missing-document-description' {
        param($document)
        $document.relationships = @(
            $document.relationships |
                Where-Object {
                    [string]$_.relationshipType -cne 'DESCRIBES'
                }
        )
    } 'must DESCRIBE exactly one local package root'
    Invoke-NegativeControl 'orphaned-runtime-file' {
        param($document)
        $document.relationships = @(
            $document.relationships |
                Where-Object {
                    [string]$_.relatedSpdxElement -cne $libraryId
                }
        )
    } 'does not CONTAIN runtime file'
    Invoke-NegativeControl 'undeclared-external-document' {
        param($document)
        $document.relationships += [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relationshipType = 'CONTAINS'
            relatedSpdxElement =
                'DocumentRef-undeclared:SPDXRef-external-file'
        }
    } 'dangling relationship endpoint'
    Invoke-NegativeControl 'mislabeled-locked-purl' {
        param($document)
        $document.packages[1].externalRefs[0].referenceCategory = 'SECURITY'
        $document.packages[1].externalRefs[0].referenceType = 'cpe23Type'
    } 'not labeled as a PACKAGE-MANAGER purl'
    Invoke-NegativeControl 'duplicate-locked-identity' {
        param($document)
        $duplicate = Get-Content -LiteralPath $baselinePath -Raw |
            ConvertFrom-Json
        $duplicatePackage = $duplicate.packages[1]
        $duplicatePackage.SPDXID =
            'SPDXRef-Package-example-runtime-duplicate'
        $document.packages += $duplicatePackage
        $document.relationships += [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $duplicatePackage.SPDXID
        }
    } 'duplicate canonical NuGet identity'
    Invoke-NegativeControl 'invalid-document-spdx-id' {
        param($document)
        $document.SPDXID = 'not-an-spdx-id'
    } 'document ID must be exactly'
    Invoke-NegativeControl 'external-document-reference' {
        param($document)
        $document | Add-Member `
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
    } 'must be self-contained'
    Invoke-NegativeControl 'disconnected-locked-package' {
        param($document)
        $document.relationships = @(
            $document.relationships |
                Where-Object {
                    [string]$_.relatedSpdxElement -cne $packageId
                }
        )
    } 'root-to-package relationship mapping is not exact'
    Invoke-NegativeControl 'disconnected-non-lock-package' {
        param($document)
        $document.packages += [pscustomobject][ordered]@{
            name = 'Disconnected non-lock package'
            versionInfo = '1.0.0'
            SPDXID = 'SPDXRef-Package-disconnected-non-lock'
            downloadLocation = 'NOASSERTION'
            filesAnalyzed = $false
            licenseDeclared = 'NOASSERTION'
            licenseConcluded = 'NOASSERTION'
        }
    } 'exactly one non-NuGet document root'
    Invoke-NegativeControl 'wrong-locked-license' {
        param($document)
        $document.packages[1].licenseDeclared = 'NOASSERTION'
    } 'license metadata for canonical package'
    Invoke-NegativeControl 'missing-locked-license' {
        param($document)
        $document.packages[1].PSObject.Properties.Remove(
            'licenseConcluded')
    } 'license metadata for canonical package'
    Invoke-NegativeControl 'wrong-nuget-download-location' {
        param($document)
        $document.packages[1].downloadLocation =
            'https://github.com/example/runtime'
    } 'not the exact NuGet flat-container package URL'
    Invoke-NegativeControl 'wrong-lock-content-checksum' {
        param($document)
        $document.packages[1].checksums[0].checksumValue = ('f' * 128)
    } 'does not match the exact packages.lock.json contentHash'
    Invoke-NegativeControl 'missing-lock-content-checksum' {
        param($document)
        $document.packages[1].checksums = @()
    } 'does not match the exact packages.lock.json contentHash'

    Invoke-NegativeControl 'spurious-nuget-package' {
        param($document)
        $document.packages += [pscustomobject][ordered]@{
            name = 'Json.NET'
            versionInfo = '13.0.4.30916'
            SPDXID = 'SPDXRef-Package-spurious-json-net'
            sourceInfo = 'assembly inference'
            filesAnalyzed = $false
            licenseDeclared = 'NOASSERTION'
            licenseConcluded = 'NOASSERTION'
            externalRefs = @(
                [pscustomobject][ordered]@{
                    referenceCategory = 'PACKAGE-MANAGER'
                    referenceType = 'purl'
                    referenceLocator =
                        'pkg:nuget/Json.NET@13.0.4.30916'
                }
            )
        }
        $document.relationships += [pscustomobject][ordered]@{
            spdxElementId = $rootId
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = 'SPDXRef-Package-spurious-json-net'
        }
    } 'unrecognized NuGet identity'
    Invoke-NegativeControl 'missing-package-file-mapping' {
        param($document)
        $document.relationships = @(
            $document.relationships |
                Where-Object {
                    -not (
                        [string]$_.spdxElementId -ceq $libraryId -and
                        [string]$_.relationshipType -ceq
                            'GENERATED_FROM' -and
                        [string]$_.relatedSpdxElement -ceq $packageId
                    )
                }
        )
    } 'package-to-runtime-file mapping is not exact'
    Invoke-NegativeControl 'wrong-package-file-mapping' {
        param($document)
        $mapping = @(
            $document.relationships |
                Where-Object {
                    [string]$_.spdxElementId -ceq $libraryId -and
                    [string]$_.relationshipType -ceq 'GENERATED_FROM'
                }
        )[0]
        $mapping.spdxElementId = $executableId
    } 'package-to-runtime-file mapping is not exact'
    Invoke-NegativeControl 'false-package-contains-file' {
        param($document)
        $mapping = @(
            $document.relationships |
                Where-Object {
                    [string]$_.spdxElementId -ceq $libraryId -and
                    [string]$_.relationshipType -ceq 'GENERATED_FROM'
                }
        )[0]
        $mapping.spdxElementId = $packageId
        $mapping.relationshipType = 'CONTAINS'
        $mapping.relatedSpdxElement = $libraryId
    } 'filesAnalyzed=false.*must not contain files'
    Invoke-NegativeControl 'file-contained-by-false-package' {
        param($document)
        $mapping = @(
            $document.relationships |
                Where-Object {
                    [string]$_.spdxElementId -ceq $libraryId -and
                    [string]$_.relationshipType -ceq 'GENERATED_FROM'
                }
        )[0]
        $mapping.relationshipType = 'CONTAINED_BY'
    } 'filesAnalyzed=false.*must not contain files'

    Write-Host (
        "PASS: SBOM inventory baseline and $negativeControlCount " +
        'fail-closed negative controls.'
    ) -ForegroundColor Green
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $resolvedTemp = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-SbomInventory-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        [IO.Directory]::Delete($resolvedScratch, $true)
    }
}
