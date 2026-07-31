# Release provenance and verification

Official releases are produced only by `.github/workflows/release.yml` from the exact `vX.Y.Z` tag.
The workflow verifies that the tag commit is checked out and that `X.Y.Z` matches
`ProductVersion.props`. It then performs a locked x64 build, runs product self-tests, signs the
executable, packages the signed ZIP, builds and validates the MSI from the same runtime manifest,
signs the MSI, emits an SPDX JSON SBOM and SHA-256 checksums, and creates a GitHub build-provenance
attestation.

CI artifacts whose names contain `UNSIGNED-CI` are test artifacts, not releases.

## Verify an official release online

Download all five versioned assets plus `SHA256SUMS.txt` from the same GitHub Release into an
otherwise empty `.\release` directory. Before trusting the checksum or provenance files, obtain the
exact 40-character commit for the protected `vX.Y.Z` tag from the reviewed release record. Do not
copy that expected commit from the still-unverified build-provenance file.

Run the following in Windows PowerShell with a current GitHub CLI. Replace the version and source
digest first. The placeholder deliberately fails closed.

```powershell
$ErrorActionPreference = 'Stop'
$Version = '2.0.0'
$ExpectedSourceDigest = 'REPLACE_WITH_THE_40_HEX_RELEASE_TAG_COMMIT'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release version must have the form X.Y.Z: $Version"
}
if ($ExpectedSourceDigest -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Set ExpectedSourceDigest to the reviewed 40-character release-tag commit.'
}
$ExpectedSourceDigest = $ExpectedSourceDigest.ToLowerInvariant()

$Repository = 'bigfnj/desktopPet'
$SignerWorkflow = 'bigfnj/desktopPet/.github/workflows/release.yml'
$Tag = "v$Version"
$SourceRef = "refs/tags/$Tag"
$ReleaseBase = "DesktopPet-AI-Edition-$Version-Windows-x64"
$ReleaseDir = (Resolve-Path -LiteralPath .\release).Path
$ProvenanceName = "DesktopPet-AI-Edition-$Version.build-provenance.txt"
$AttestedFiles = @(
    "$ReleaseBase.zip"
    "$ReleaseBase.msi"
    "DesktopPet-AI-Edition-$Version.spdx.json"
    $ProvenanceName
    "DesktopPet-AI-Edition-$Version.upgrade-evidence.json"
    'SHA256SUMS.txt'
) | Sort-Object

$ActualFiles = @(
    Get-ChildItem -LiteralPath $ReleaseDir -Force -Recurse |
        ForEach-Object {
            $Relative = $_.FullName.Substring($ReleaseDir.Length + 1)
            if ($_.PSIsContainer) { "$Relative/" } else { $Relative }
        } |
        Sort-Object
)
if (@(Compare-Object $AttestedFiles $ActualFiles).Count -ne 0) {
    throw 'Release directory does not contain exactly the six published release files.'
}

$Gh = (Get-Command gh -ErrorAction Stop).Source
$AttestationPolicy = @(
    '--repo', $Repository
    '--signer-workflow', $SignerWorkflow
    '--source-ref', $SourceRef
    '--source-digest', $ExpectedSourceDigest
    '--predicate-type', 'https://slsa.dev/provenance/v1'
    '--deny-self-hosted-runners'
)
function Assert-GitHubAttestation {
    param([Parameter(Mandatory = $true)][string]$ArtifactPath)

    & $Gh attestation verify $ArtifactPath @AttestationPolicy
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub attestation verification failed: $ArtifactPath"
    }
}

# Authenticate the provenance carrier before reading its signer identity.
$ProvenancePath = Join-Path $ReleaseDir $ProvenanceName
Assert-GitHubAttestation -ArtifactPath $ProvenancePath

$ProvenanceValues = @{}
foreach ($Line in Get-Content -LiteralPath $ProvenancePath) {
    $Match = [regex]::Match(
        $Line,
        '^(?<key>[a-z0-9_]+)=(?<value>.*)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $Match.Success -or
        $ProvenanceValues.ContainsKey($Match.Groups['key'].Value)) {
        throw "Malformed or duplicate build-provenance line: $Line"
    }
    $ProvenanceValues[$Match.Groups['key'].Value] =
        $Match.Groups['value'].Value
}
foreach ($RequiredKey in @(
        'repository',
        'tag',
        'commit',
        'signing_certificate_thumbprint')) {
    if (-not $ProvenanceValues.ContainsKey($RequiredKey) -or
        [string]::IsNullOrWhiteSpace([string]$ProvenanceValues[$RequiredKey])) {
        throw "Authenticated build provenance lacks '$RequiredKey'."
    }
}
if ([string]$ProvenanceValues['repository'] -cne $Repository -or
    [string]$ProvenanceValues['tag'] -cne $Tag -or
    ([string]$ProvenanceValues['commit']).ToLowerInvariant() -cne
        $ExpectedSourceDigest) {
    throw 'Authenticated build provenance does not match the expected repository, tag, and commit.'
}
$ExpectedSignerThumbprint = (
    [string]$ProvenanceValues['signing_certificate_thumbprint']
).Replace(' ', '').ToUpperInvariant()
if ($ExpectedSignerThumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'Authenticated build provenance contains an invalid signer thumbprint.'
}

# Require the same repository/workflow/tag/commit policy for every other release file.
foreach ($Name in $AttestedFiles | Where-Object { $_ -cne $ProvenanceName }) {
    Assert-GitHubAttestation -ArtifactPath (Join-Path $ReleaseDir $Name)
}

function Assert-AuthenticodeSigner {
    param([Parameter(Mandatory = $true)][string]$Path)

    $Signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($Signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for '$Path': $($Signature.Status)."
    }
    $ObservedThumbprint = if ($null -ne $Signature.SignerCertificate) {
        ([string]$Signature.SignerCertificate.Thumbprint).
            Replace(' ', '').ToUpperInvariant()
    }
    else {
        ''
    }
    if ($ObservedThumbprint -cne $ExpectedSignerThumbprint) {
        throw "Authenticode signer mismatch for '$Path': '$ObservedThumbprint'."
    }
    if ($null -eq $Signature.TimeStamperCertificate) {
        throw "Authenticode timestamp is missing for '$Path'."
    }
}

$MsiPath = Join-Path $ReleaseDir "$ReleaseBase.msi"
Assert-AuthenticodeSigner -Path $MsiPath

$ExtractRoot = Join-Path (Split-Path -Parent $ReleaseDir) (
    "DesktopPet-$Version-attested")
if (Test-Path -LiteralPath $ExtractRoot) {
    throw "Refusing to reuse extraction directory: $ExtractRoot"
}
Expand-Archive -LiteralPath (Join-Path $ReleaseDir "$ReleaseBase.zip") `
    -DestinationPath $ExtractRoot
Assert-AuthenticodeSigner -Path (Join-Path $ExtractRoot 'DesktopPet.exe')
```

The first attestation check authenticates the build-provenance asset before its
`signing_certificate_thumbprint` value is used. The remaining checks require every published file
to come from the protected release workflow at the expected tag and source commit, then require the
MSI and packaged EXE to have a valid timestamped Authenticode signature from that exact certificate.
Do not install or run the release unless this online procedure and the complete checksum/SBOM
procedure below both pass.

## Verify the SBOM and packaged payload offline

The following checks make no network requests once the inputs have been collected. They establish
that the downloaded release files agree with `SHA256SUMS.txt`, that the SBOM is valid SPDX 2.3 JSON,
and that its runtime file names and SHA-256 values exactly match the ZIP or MSI payload. They do not
by themselves authenticate the publisher or resolve the third-party rights blockers in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Authenticate the release attestation while
online, or transfer the files and checksum manifest through another authenticated channel, before
relying on an offline result.

### One-time preparation before disconnecting

Install Python 3 and the `jsonschema` package in the offline environment ahead of time. Also retain
the official SPDX 2.3 JSON schema from the immutable SPDX specification commit shown below:

```powershell
$SchemaCommit = 'aadf3b0b8dbbabdb4d880b0fc714255fea436ff7'
$SchemaUri = "https://raw.githubusercontent.com/spdx/spdx-spec/$SchemaCommit/schemas/spdx-schema.json"
Invoke-WebRequest -UseBasicParsing -Uri $SchemaUri -OutFile .\spdx-schema-2.3.json

$ExpectedSchemaSha256 = '239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b'
$ActualSchemaSha256 = (
    Get-FileHash -LiteralPath .\spdx-schema-2.3.json -Algorithm SHA256
).Hash.ToLowerInvariant()
if ($ActualSchemaSha256 -cne $ExpectedSchemaSha256) {
    throw "Official SPDX schema hash mismatch: $ActualSchemaSha256"
}
```

Copy that verified schema, the five versioned release assets, and `SHA256SUMS.txt` to the offline
machine. Put only the six release files in an otherwise empty `.\release` directory; keep the
schema beside that directory.

### 1. Verify the complete release checksum set

Set the release version, then run this from the directory containing `release`:

```powershell
$Version = '2.0.0'
$ReleaseDir = (Resolve-Path -LiteralPath .\release).Path
$ReleaseBase = "DesktopPet-AI-Edition-$Version-Windows-x64"
$ExpectedAssets = @(
    "$ReleaseBase.zip"
    "$ReleaseBase.msi"
    "DesktopPet-AI-Edition-$Version.spdx.json"
    "DesktopPet-AI-Edition-$Version.build-provenance.txt"
    "DesktopPet-AI-Edition-$Version.upgrade-evidence.json"
) | Sort-Object
$ChecksumPath = Join-Path $ReleaseDir 'SHA256SUMS.txt'

$Declared = @{}
foreach ($Line in Get-Content -LiteralPath $ChecksumPath) {
    if ($Line -cnotmatch '^(?<hash>[0-9a-f]{64}) \*(?<name>[^\\/]+)$') {
        throw "Malformed checksum line: $Line"
    }
    if ($Declared.ContainsKey($Matches.name)) {
        throw "Duplicate checksum entry: $($Matches.name)"
    }
    $AssetPath = Join-Path $ReleaseDir $Matches.name
    if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
        throw "Missing checksummed release asset: $($Matches.name)"
    }
    $Actual = (
        Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($Actual -cne $Matches.hash) {
        throw "Release asset hash mismatch: $($Matches.name)"
    }
    $Declared[$Matches.name] = $Actual
}

$ManifestDifference = @(
    Compare-Object $ExpectedAssets @($Declared.Keys | Sort-Object)
)
$DirectoryAssets = @(
    Get-ChildItem -LiteralPath $ReleaseDir -Force -Recurse |
        ForEach-Object {
            $Relative = $_.FullName.Substring($ReleaseDir.Length + 1)
            if ($_.PSIsContainer) { "$Relative/" } else { $Relative }
        } |
        Where-Object { $_ -cne 'SHA256SUMS.txt' } |
        Sort-Object
)
$DirectoryDifference = @(Compare-Object $ExpectedAssets $DirectoryAssets)
if ($ManifestDifference.Count -ne 0 -or $DirectoryDifference.Count -ne 0) {
    throw 'The checksum manifest or release directory does not contain the exact release asset set.'
}
'Release checksum set: PASS'
```

### 2. Validate the SPDX 2.3 JSON schema

Recheck the retained schema hash, then validate the versioned SBOM. This uses the locally installed
Python package and does not resolve any network references:

```powershell
$Schema = (Resolve-Path -LiteralPath .\spdx-schema-2.3.json).Path
$ExpectedSchemaSha256 = '239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b'
if ((Get-FileHash -LiteralPath $Schema -Algorithm SHA256).Hash.ToLowerInvariant() -cne
    $ExpectedSchemaSha256) {
    throw 'The retained SPDX schema no longer matches the reviewed official schema.'
}
$Sbom = Join-Path $ReleaseDir "DesktopPet-AI-Edition-$Version.spdx.json"

@'
import json
import pathlib
import sys
from jsonschema.validators import validator_for

schema_path = pathlib.Path(sys.argv[1])
document_path = pathlib.Path(sys.argv[2])
schema = json.loads(schema_path.read_text(encoding="utf-8"))
document = json.loads(document_path.read_text(encoding="utf-8"))
validator_type = validator_for(schema)
validator_type.check_schema(schema)
errors = sorted(
    validator_type(schema).iter_errors(document),
    key=lambda error: [str(part) for part in error.absolute_path],
)
if errors:
    for error in errors[:20]:
        location = "/".join(str(part) for part in error.absolute_path) or "<root>"
        print(f"{location}: {error.message}", file=sys.stderr)
    raise SystemExit(f"SPDX schema validation failed with {len(errors)} error(s).")
print("SPDX 2.3 schema: PASS")
'@ | python - $Schema $Sbom
if ($LASTEXITCODE -ne 0) {
    throw 'SPDX schema validation failed.'
}
```

### 3. Compare SBOM file evidence with the ZIP and MSI

Define the exact file-set and SHA-256 comparison:

```powershell
function Test-SbomPayload {
    param(
        [Parameter(Mandatory = $true)][string]$SbomPath,
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [hashtable]$AllowedExtraFiles = @{}
    )

    $Document = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
    $Root = (Resolve-Path -LiteralPath $PayloadRoot).Path.TrimEnd('\', '/')
    $RootPrefix = $Root + [IO.Path]::DirectorySeparatorChar
    $Expected = New-Object 'Collections.Generic.Dictionary[string,string]' (
        [StringComparer]::OrdinalIgnoreCase)

    foreach ($File in @($Document.files)) {
        $SpdxName = [string]$File.fileName
        if (-not $SpdxName.StartsWith('./', [StringComparison]::Ordinal)) {
            throw "SBOM file name is not runtime-relative: $SpdxName"
        }
        $Relative = $SpdxName.Substring(2)
        if ([string]::IsNullOrWhiteSpace($Relative) -or
            $Relative -match '(^|/)\.\.?(/|$)' -or
            $Relative.Contains('\')) {
            throw "Unsafe SBOM file name: $SpdxName"
        }
        if ($Expected.ContainsKey($Relative)) {
            throw "Duplicate SBOM file name: $SpdxName"
        }

        $Checksums = @(
            $File.checksums |
                Where-Object { [string]$_.algorithm -ceq 'SHA256' }
        )
        if ($Checksums.Count -ne 1 -or
            [string]$Checksums[0].checksumValue -cnotmatch '^[0-9a-fA-F]{64}$') {
            throw "SBOM file does not have exactly one SHA-256 checksum: $SpdxName"
        }

        $NativeRelative = $Relative.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar)
        $FullPath = [IO.Path]::GetFullPath((Join-Path $Root $NativeRelative))
        if (-not $FullPath.StartsWith(
                $RootPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $FullPath -PathType Leaf)) {
            throw "SBOM file is outside or missing from the payload: $SpdxName"
        }
        $ActualHash = (
            Get-FileHash -LiteralPath $FullPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        $ExpectedHash = (
            [string]$Checksums[0].checksumValue
        ).ToLowerInvariant()
        if ($ActualHash -cne $ExpectedHash) {
            throw "SBOM checksum mismatch: $SpdxName"
        }
        $Expected[$Relative] = $ExpectedHash
    }

    foreach ($Entry in $AllowedExtraFiles.GetEnumerator()) {
        $Relative = [string]$Entry.Key
        $ExpectedHash = ([string]$Entry.Value).ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($Relative) -or
            $Relative -match '(^|/)\.\.?(/|$)' -or
            $Relative.Contains('\') -or
            $ExpectedHash -cnotmatch '^[0-9a-f]{64}$' -or
            $Expected.ContainsKey($Relative)) {
            throw "Invalid allowed package-only file declaration: $Relative"
        }
        $FullPath = [IO.Path]::GetFullPath((
            Join-Path $Root $Relative.Replace(
                '/',
                [IO.Path]::DirectorySeparatorChar)))
        if (-not $FullPath.StartsWith(
                $RootPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $FullPath -PathType Leaf)) {
            throw "Allowed package-only file is outside or missing from the payload: $Relative"
        }
        $ActualHash = (
            Get-FileHash -LiteralPath $FullPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($ActualHash -cne $ExpectedHash) {
            throw "Allowed package-only file checksum mismatch: $Relative"
        }
        $Expected[$Relative] = $ExpectedHash
    }

    $ActualFiles = @(
        Get-ChildItem -LiteralPath $Root -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($RootPrefix.Length).Replace('\', '/')
            } |
            Sort-Object
    )
    $Difference = @(
        Compare-Object @($Expected.Keys | Sort-Object) $ActualFiles
    )
    if ($Difference.Count -ne 0) {
        throw 'SBOM and payload file sets differ.'
    }
    "SBOM payload match ($Root): PASS"
}
```

Extract both packages into a new verification directory and run the comparison:

```powershell
$VerificationRoot = Join-Path $PWD 'payload-verification'
if (Test-Path -LiteralPath $VerificationRoot) {
    throw "Use a new empty verification path: $VerificationRoot"
}
$ZipRoot = Join-Path $VerificationRoot 'zip'
$MsiImage = Join-Path $VerificationRoot 'msi'
New-Item -ItemType Directory -Path $ZipRoot, $MsiImage | Out-Null

$Zip = Join-Path $ReleaseDir "$ReleaseBase.zip"
$Msi = Join-Path $ReleaseDir "$ReleaseBase.msi"
Expand-Archive -LiteralPath $Zip -DestinationPath $ZipRoot

$MsiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
& $MsiExec /a $Msi /qn "TARGETDIR=$MsiImage"
$MsiExitCode = $LASTEXITCODE
if ($MsiExitCode -notin @(0, 3010)) {
    throw "MSI administrative extraction failed with exit code $MsiExitCode."
}
if ($MsiExitCode -eq 3010) {
    'MSI administrative extraction succeeded; Windows requested a reboot.'
}

function Find-PayloadRoot {
    param([Parameter(Mandatory = $true)][string]$SearchRoot)
    $Executables = @(
        Get-ChildItem -LiteralPath $SearchRoot -Filter 'DesktopPet.exe' -File -Recurse
    )
    if ($Executables.Count -ne 1) {
        throw "Expected exactly one DesktopPet.exe below $SearchRoot."
    }
    return $Executables[0].Directory.FullName
}

Test-SbomPayload `
    -SbomPath $Sbom `
    -PayloadRoot (Find-PayloadRoot $ZipRoot) `
    -AllowedExtraFiles @{
        # The portable marker is the one intentional ZIP-only file. Its reviewed bytes are:
        # "DesktopPet portable package marker.`n"
        'DesktopPet.portable' =
            '56ffdc6ba76d62f976db05045323876276e2cbbceaee4610beb10ffe90e8cb94'
    }
Test-SbomPayload -SbomPath $Sbom -PayloadRoot (Find-PayloadRoot $MsiImage)
```

Passing these checks proves exact SBOM-to-runtime agreement plus the one documented, hash-pinned
portable ZIP marker for the downloaded bytes. It does not make an untrusted checksum manifest
authentic, attest to source-code correspondence, or grant redistribution rights.

Do not install an artifact if its checksum, signature, repository identity, or attestation does not
match.
