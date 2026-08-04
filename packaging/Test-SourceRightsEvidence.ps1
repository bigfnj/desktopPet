#requires -Version 5
[CmdletBinding()]
param(
    [string]$EvidencePath,
    [string]$RepositoryRoot,
    [switch]$RequireReleaseApproval,
    [switch]$AllowUntrackedDevelopment,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Utf8Strict = New-Object Text.UTF8Encoding($false, $true)
$RequiredAssets = New-Object 'Collections.Generic.Dictionary[string,string]' (
    [StringComparer]::Ordinal)
$RequiredAssets.Add('src/Fortunes/fortunes.txt', 'corpus')
$RequiredAssets.Add('src/Models/bge-small.onnx', 'model')
$RequiredAssets.Add('src/Models/bge-small.vocab.txt', 'model')
$RequiredAssets.Add('@engine-source', 'engine-source-set')
$RequiredAssets.Add('@bundled-art', 'art-set')
$RequiredAssets.Add('@downloadable-pet-art', 'art-set')

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
    Write-Output -NoEnumerate $property.Value
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Context
    )

    [string[]]$observed = @($Object.PSObject.Properties.Name)
    if ($observed.Count -ne $Expected.Count) {
        throw (
            "$Context property count is $($observed.Count); expected " +
            "$($Expected.Count)."
        )
    }
    foreach ($name in $Expected) {
        if ($observed -cnotcontains $name) {
            throw "$Context is missing exact property '$name'."
        }
    }
    foreach ($name in $observed) {
        if ($Expected -cnotcontains $name) {
            throw "$Context has unexpected property '$name'."
        }
    }
}

function Assert-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Context,
        [string]$RequiredPrefix
    )

    if ($RelativePath -cnotmatch '^[A-Za-z0-9._/-]+$' -or
        $RelativePath -match '(?:^|/)\.\.(?:/|$)' -or
        $RelativePath.Contains('\') -or
        [IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Context path is unsafe: '$RelativePath'."
    }
    if (-not [string]::IsNullOrEmpty($RequiredPrefix) -and
        -not $RelativePath.StartsWith(
            $RequiredPrefix,
            [StringComparison]::Ordinal)) {
        throw "$Context path must be below '$RequiredPrefix'."
    }
}

function Resolve-TrackedRepositoryFile {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][string]$GitPath,
        [switch]$AllowUntracked
    )

    Assert-SafeRelativePath -RelativePath $RelativePath -Context $Context
    $fullPath = [IO.Path]::GetFullPath(
        (Join-Path $RepoRoot $RelativePath.Replace('/', '\')))
    if (-not $fullPath.StartsWith(
            $RepoRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context path escapes the repository: '$RelativePath'."
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Context file is missing: '$RelativePath'."
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Context file must not be a reparse point: '$RelativePath'."
    }
    $tracked = @(
        & $GitPath -C $RepoRoot ls-files -- $RelativePath 2>$null
    )
    if (($tracked.Count -ne 1 -or
         [string]$tracked[0] -cne $RelativePath) -and
        -not $AllowUntracked) {
        throw "$Context file is not tracked exactly by Git: '$RelativePath'."
    }
    return $fullPath
}

function Assert-BoundedText {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][int]$MaximumLength
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt $MaximumLength -or
        $Value -cne $Value.Trim() -or
        $Value -match '\p{Cc}') {
        throw "$Context is empty, untrimmed, too long, or contains a control character."
    }
}

function Get-StrictJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    try {
        $json = $Utf8Strict.GetString($bytes)
    }
    catch [Text.DecoderFallbackException] {
        throw "Source-rights manifest is not strict UTF-8: $Path"
    }
    try {
        return ($json | ConvertFrom-Json)
    }
    catch {
        throw "Source-rights manifest is not valid JSON: $($_.Exception.Message)"
    }
}

function Get-ByteSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($Bytes)
        )).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-SourceSetMemberSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$SetId,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$FullPath,
        [switch]$AllowDevelopmentBytes
    )

    [byte[]]$bytes = [IO.File]::ReadAllBytes($FullPath)
    if ($SetId -cne '@engine-source') {
        return Get-ByteSha256 -Bytes $bytes
    }

    $canonical = New-Object IO.MemoryStream
    $sawCrLf = $false
    try {
        for ($index = 0; $index -lt $bytes.Length; $index++) {
            if ($bytes[$index] -ne 13) {
                $canonical.WriteByte($bytes[$index])
                continue
            }
            if ($index + 1 -ge $bytes.Length -or
                $bytes[$index + 1] -ne 10) {
                throw (
                    "Source-rights set '$SetId' file contains a lone " +
                    "carriage return: '$RelativePath'."
                )
            }
            $sawCrLf = $true
            $canonical.WriteByte(10)
            $index++
        }
        if ($sawCrLf -and -not $AllowDevelopmentBytes) {
            throw (
                "Source-rights set '$SetId' file must use LF-only release " +
                "bytes: '$RelativePath'."
            )
        }
        return Get-ByteSha256 -Bytes $canonical.ToArray()
    }
    finally {
        $canonical.Dispose()
    }
}

function Get-SourceSetFiles {
    param(
        [Parameter(Mandatory = $true)][string]$SetId,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$GitPath
    )

    $paths = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    switch ($SetId) {
        '@engine-source' {
            foreach ($fixedPath in @(
                    'ProductVersion.props',
                    'src/DesktopPet_Portable.csproj',
                    'src/Directory.Build.props',
                    'src/app.config',
                    'src/Properties/app.manifest',
                    'src/Properties/Settings.settings')) {
                [void]$paths.Add($fixedPath)
            }
            $projectPath = Join-Path $RepoRoot 'src\DesktopPet_Portable.csproj'
            [xml]$project = Get-Content -LiteralPath $projectPath -Raw
            foreach ($itemGroup in @($project.Project.ItemGroup)) {
                foreach ($nodeName in @('Compile', 'EmbeddedResource')) {
                    foreach ($node in @(
                            $itemGroup.SelectNodes(
                                "./*[local-name()='$nodeName']"))) {
                        $include = [string]$node.Include
                        if ([string]::IsNullOrWhiteSpace($include) -or
                            $include.Contains('$(') -or
                            $include.StartsWith('..', [StringComparison]::Ordinal)) {
                            continue
                        }
                        $relative = (
                            'src/' + $include.Replace('\', '/')
                        )
                        if ($nodeName -eq 'Compile' -or
                            $relative.EndsWith(
                                '.resx',
                                [StringComparison]::OrdinalIgnoreCase) -or
                            $relative -ceq
                                'src/Fortunes/classifier-parity-cases.tsv') {
                            [void]$paths.Add($relative)
                        }
                    }
                }
            }
        }
        '@bundled-art' {
            foreach ($path in @(
                    'src/icon.ico',
                    'src/esheep.png.ico',
                    'src/Images/about.png',
                    'src/Images/esheep.png',
                    'src/Images/exit.png',
                    'src/Images/help.png',
                    'src/Images/install.png',
                    'src/Images/option.png',
                    'src/Resources/animations.xml',
                    'src/Resources/animations.xsd')) {
                [void]$paths.Add($path)
            }
        }
        '@downloadable-pet-art' {
            $trackedPets = @(
                & $GitPath -C $RepoRoot ls-files -- 'Pets' 2>$null
            )
            if ($LASTEXITCODE -ne 0) {
                throw 'Could not enumerate tracked downloadable pet assets.'
            }
            foreach ($path in $trackedPets) {
                $relative = [string]$path
                if ($relative -cmatch (
                        '^Pets/(?:[^/]+/(?:animations[.]xml|icon[.]png)|' +
                        '(?:esheep_ani[.]gif|esheepbackground[.]jpg|pets[.]json))$')) {
                    [void]$paths.Add($relative)
                }
            }
        }
        default {
            throw "Unknown source-rights virtual asset set: '$SetId'."
        }
    }
    if ($paths.Count -eq 0) {
        throw "Source-rights virtual asset set '$SetId' is empty."
    }
    [string[]]$sorted = @($paths)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Get-SourceSetFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$SetId,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$GitPath,
        [switch]$AllowUntracked
    )

    [string[]]$paths = @(
        Get-SourceSetFiles -SetId $SetId -RepoRoot $RepoRoot -GitPath $GitPath
    )
    $builder = New-Object Text.StringBuilder
    foreach ($relativePath in $paths) {
        $fullPath = Resolve-TrackedRepositoryFile `
            -RepoRoot $RepoRoot `
            -RelativePath $relativePath `
            -Context "Source-rights set '$SetId'" `
            -GitPath $GitPath `
            -AllowUntracked:$AllowUntracked
        $hash = Get-SourceSetMemberSha256 `
            -SetId $SetId `
            -RelativePath $relativePath `
            -FullPath $fullPath `
            -AllowDevelopmentBytes:$AllowUntracked
        [void]$builder.Append($hash)
        [void]$builder.Append(' *')
        [void]$builder.Append($relativePath)
        [void]$builder.Append("`n")
    }
    [byte[]]$manifestBytes = $Utf8Strict.GetBytes($builder.ToString())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = ([BitConverter]::ToString(
            $sha256.ComputeHash($manifestBytes)
        )).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    return [pscustomobject]@{
        FileCount = $paths.Count
        Sha256 = $fingerprint
        Paths = @($paths)
    }
}

function Assert-SourceRightsEvidence {
    param(
        [Parameter(Mandatory = $true)][object]$Evidence,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [switch]$RequireApproval,
        [switch]$AllowUntracked
    )

    $fullRepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $fullRepoRoot -PathType Container)) {
        throw "Source-rights repository root is missing: $fullRepoRoot"
    }
    $gitCommand = Get-Command git -CommandType Application -ErrorAction Stop |
        Select-Object -First 1   # CI runners expose several git.exe on PATH; take the effective one
    $gitPath = $gitCommand.Source
    $detectedRoot = @(
        & $gitPath -C $fullRepoRoot rev-parse --show-toplevel 2>$null
    )
    if ($LASTEXITCODE -ne 0 -or $detectedRoot.Count -ne 1) {
        throw "Source-rights repository root is not a Git worktree: $fullRepoRoot"
    }
    $detectedRootPath = [IO.Path]::GetFullPath(
        ([string]$detectedRoot[0]).Replace('/', '\')).TrimEnd('\')
    if ($detectedRootPath -cne $fullRepoRoot) {
        throw (
            'Source-rights validation requires the exact Git worktree root. ' +
            "Expected '$detectedRootPath'; received '$fullRepoRoot'."
        )
    }

    Assert-ExactProperties -Object $Evidence `
        -Expected @('schemaVersion', 'assets') `
        -Context 'Source-rights manifest'
    $schemaVersion = Get-RequiredProperty -Object $Evidence `
        -Name 'schemaVersion' -Context 'Source-rights manifest'
    if (($schemaVersion -isnot [int] -and $schemaVersion -isnot [long]) -or
        [long]$schemaVersion -ne 1) {
        throw "Unsupported source-rights schema '$schemaVersion'."
    }
    $assetsValue = Get-RequiredProperty -Object $Evidence `
        -Name 'assets' -Context 'Source-rights manifest'
    if ($assetsValue -isnot [Array]) {
        throw 'Source-rights manifest assets must be a JSON array.'
    }

    $assetByPath = New-Object 'Collections.Generic.Dictionary[string,object]' (
        [StringComparer]::Ordinal)
    $blockers = New-Object 'Collections.Generic.List[string]'
    foreach ($asset in @($assetsValue)) {
        Assert-ExactProperties -Object $asset `
            -Expected @(
                'path',
                'assetType',
                'sha256',
                'releaseApproved',
                'sourceApprovals'
            ) `
            -Context 'Source-rights asset'
        $assetPath = [string](Get-RequiredProperty -Object $asset `
            -Name 'path' -Context 'Source-rights asset')
        if ($assetPath.StartsWith('@', [StringComparison]::Ordinal)) {
            if ($assetPath -cnotmatch '^@[a-z0-9]+(?:-[a-z0-9]+)*$') {
                throw "Source-rights virtual asset path is unsafe: '$assetPath'."
            }
        }
        else {
            Assert-SafeRelativePath -RelativePath $assetPath `
                -Context 'Source-rights asset'
        }
        if (-not $RequiredAssets.ContainsKey($assetPath)) {
            throw "Source-rights manifest names an unexpected asset: '$assetPath'."
        }
        if ($assetByPath.ContainsKey($assetPath)) {
            throw "Source-rights manifest duplicates asset '$assetPath'."
        }
        $assetType = [string](Get-RequiredProperty -Object $asset `
            -Name 'assetType' -Context "Source-rights asset '$assetPath'")
        if ($assetType -cne $RequiredAssets[$assetPath]) {
            throw (
                "Source-rights asset '$assetPath' type must be " +
                "'$($RequiredAssets[$assetPath])'."
            )
        }
        $hash = [string](Get-RequiredProperty -Object $asset -Name 'sha256' `
            -Context "Source-rights asset '$assetPath'")
        if ($hash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Source-rights asset '$assetPath' has an invalid SHA-256."
        }
        $approvedProperty = $asset.PSObject.Properties['releaseApproved']
        if ($null -eq $approvedProperty -or
            $approvedProperty.Value -isnot [bool]) {
            throw (
                "Source-rights asset '$assetPath' must declare boolean " +
                'releaseApproved.'
            )
        }
        $approvalsValue = Get-RequiredProperty -Object $asset `
            -Name 'sourceApprovals' `
            -Context "Source-rights asset '$assetPath'"
        if ($approvalsValue -isnot [Array]) {
            throw "Source-rights asset '$assetPath' sourceApprovals must be an array."
        }

        $setFingerprint = $null
        $actualHash = if (
            $assetType -cin @('engine-source-set', 'art-set')) {
            $setFingerprint = Get-SourceSetFingerprint `
                -SetId $assetPath `
                -RepoRoot $fullRepoRoot `
                -GitPath $gitPath `
                -AllowUntracked:$AllowUntracked
            $setFingerprint.Sha256
        }
        else {
            $fullAssetPath = Resolve-TrackedRepositoryFile `
                -RepoRoot $fullRepoRoot `
                -RelativePath $assetPath `
                -Context "Source-rights asset '$assetPath'" `
                -GitPath $gitPath `
                -AllowUntracked:$AllowUntracked
            (
                Get-FileHash -LiteralPath $fullAssetPath -Algorithm SHA256
            ).Hash.ToLowerInvariant()
        }
        if ($actualHash -cne $hash) {
            throw (
                "Source-rights asset '$assetPath' hash mismatch. Declared " +
                "$hash; found $actualHash."
            )
        }

        $approved = [bool]$approvedProperty.Value
        $approvals = @($approvalsValue)
        if (-not $approved) {
            if ($approvals.Count -ne 0) {
                throw (
                    "Source-rights asset '$assetPath' has source approvals " +
                    'while releaseApproved is false.'
                )
            }
            $blockers.Add(
                "Asset '$assetPath' is not release-approved; exact source " +
                'revision, conversion procedure, license evidence, approver, ' +
                'and approval time remain unresolved.'
            )
        }
        elseif ($approvals.Count -eq 0) {
            throw (
                "Source-rights asset '$assetPath' is approved without any " +
                'source/license evidence.'
            )
        }

        $sourceIds = New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
        $sourceSetPaths = @()
        $sourceSetPathLookup = $null
        $coveredSourceSetPaths = $null
        if ($null -ne $setFingerprint) {
            [string[]]$sourceSetPaths = @($setFingerprint.Paths)
            $sourceSetPathLookup =
                New-Object 'Collections.Generic.HashSet[string]' (
                    [StringComparer]::Ordinal)
            $coveredSourceSetPaths =
                New-Object 'Collections.Generic.HashSet[string]' (
                    [StringComparer]::Ordinal)
            foreach ($setPath in $sourceSetPaths) {
                if (-not $sourceSetPathLookup.Add($setPath)) {
                    throw (
                        "Source-rights virtual asset '$assetPath' contains " +
                        "duplicate member '$setPath'."
                    )
                }
            }
        }
        foreach ($approval in $approvals) {
            $expectedApprovalProperties = @(
                'sourceId',
                'sourceRepository',
                'sourceRevision',
                'conversionProcedure',
                'licenseExpression',
                'evidencePath',
                'evidenceSha256',
                'approvedBy',
                'approvedAtUtc'
            )
            if ($null -ne $setFingerprint) {
                $expectedApprovalProperties += 'memberPaths'
            }
            Assert-ExactProperties -Object $approval `
                -Expected $expectedApprovalProperties `
                -Context "Source approval for '$assetPath'"
            $sourceId = [string]$approval.sourceId
            if ($sourceId -cnotmatch (
                    '^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$') -or
                -not $sourceIds.Add($sourceId)) {
                throw (
                    "Source-rights asset '$assetPath' has invalid or duplicate " +
                    "sourceId '$sourceId'."
                )
            }
            if ($null -ne $setFingerprint) {
                $memberPathsValue = Get-RequiredProperty `
                    -Object $approval `
                    -Name 'memberPaths' `
                    -Context (
                        "Source approval '$sourceId' for '$assetPath'")
                if ($memberPathsValue -isnot [Array] -or
                    @($memberPathsValue).Count -eq 0) {
                    throw (
                        "Source approval '$sourceId' for '$assetPath' must " +
                        'declare a non-empty memberPaths array.'
                    )
                }
                foreach ($memberPathValue in @($memberPathsValue)) {
                    if ($memberPathValue -isnot [string]) {
                        throw (
                            "Source approval '$sourceId' for '$assetPath' has " +
                            'a non-string memberPaths value.'
                        )
                    }
                    $memberPath = [string]$memberPathValue
                    Assert-SafeRelativePath -RelativePath $memberPath `
                        -Context (
                            "Source approval '$sourceId' member")
                    if (-not $sourceSetPathLookup.Contains($memberPath)) {
                        throw (
                            "Source approval '$sourceId' for '$assetPath' " +
                            "names non-member path '$memberPath'."
                        )
                    }
                    if (-not $coveredSourceSetPaths.Add($memberPath)) {
                        throw (
                            "Source-rights virtual asset '$assetPath' member " +
                            "'$memberPath' is covered by more than one source " +
                            'approval.'
                        )
                    }
                }
            }
            $sourceRepository = [string]$approval.sourceRepository
            if ($sourceRepository -cnotmatch '^https://[^ \t\r\n]+$') {
                throw (
                    "Source approval '$sourceId' for '$assetPath' must pin an " +
                    'HTTPS source repository.'
                )
            }
            if ([string]$approval.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
                throw (
                    "Source approval '$sourceId' for '$assetPath' must pin a " +
                    'lowercase 40-character revision.'
                )
            }
            Assert-BoundedText -Value ([string]$approval.conversionProcedure) `
                -Context (
                    "Source approval '$sourceId' conversion procedure") `
                -MaximumLength 4096
            Assert-BoundedText -Value ([string]$approval.licenseExpression) `
                -Context (
                    "Source approval '$sourceId' license expression") `
                -MaximumLength 200

            $evidenceRelative = [string]$approval.evidencePath
            Assert-SafeRelativePath -RelativePath $evidenceRelative `
                -Context "Source approval '$sourceId' evidence" `
                -RequiredPrefix 'docs/rights/'
            $evidenceFull = Resolve-TrackedRepositoryFile `
                -RepoRoot $fullRepoRoot `
                -RelativePath $evidenceRelative `
                -Context "Source approval '$sourceId' evidence" `
                -GitPath $gitPath `
                -AllowUntracked:$AllowUntracked
            $evidenceHash = [string]$approval.evidenceSha256
            if ($evidenceHash -cnotmatch '^[0-9a-f]{64}$') {
                throw "Source approval '$sourceId' has an invalid evidence SHA-256."
            }
            $actualEvidenceHash = (
                Get-FileHash -LiteralPath $evidenceFull -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            if ($actualEvidenceHash -cne $evidenceHash) {
                throw (
                    "Source approval '$sourceId' evidence hash mismatch for " +
                    "'$evidenceRelative'."
                )
            }
            Assert-BoundedText -Value ([string]$approval.approvedBy) `
                -Context "Source approval '$sourceId' approver" `
                -MaximumLength 200
            $approvedAt = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParseExact(
                    [string]$approval.approvedAtUtc,
                    'yyyy-MM-ddTHH:mm:ssZ',
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::AssumeUniversal,
                    [ref]$approvedAt)) {
                throw (
                    "Source approval '$sourceId' timestamp must be UTC " +
                    'yyyy-MM-ddTHH:mm:ssZ.'
                )
            }
        }
        if ($approved -and $null -ne $setFingerprint -and
            $coveredSourceSetPaths.Count -ne $sourceSetPathLookup.Count) {
            $missingMembers = @(
                $sourceSetPaths |
                    Where-Object {
                        -not $coveredSourceSetPaths.Contains($_)
                    }
            )
            throw (
                "Source-rights virtual asset '$assetPath' approval coverage " +
                'is incomplete. Missing memberPaths: ' +
                "$($missingMembers -join ', ')."
            )
        }

        $assetByPath.Add($assetPath, $asset)
    }

    if ($assetByPath.Count -ne $RequiredAssets.Count) {
        $missing = @(
            $RequiredAssets.Keys |
                Where-Object { -not $assetByPath.ContainsKey($_) } |
                Sort-Object
        )
        throw (
            'Source-rights manifest does not contain the exact required asset ' +
            "set. Missing: $($missing -join ', ')."
        )
    }
    if ($RequireApproval -and $blockers.Count -ne 0) {
        throw "Source-rights release blockers: $($blockers -join ' | ')"
    }
    return [pscustomobject]@{
        AssetCount = $assetByPath.Count
        ApprovalCount = @(
            $assetByPath.Values |
                Where-Object { [bool]$_.releaseApproved }
        ).Count
        Blockers = @($blockers)
    }
}

function Copy-JsonObject {
    param([Parameter(Mandatory = $true)][object]$InputObject)
    return ($InputObject | ConvertTo-Json -Depth 30 | ConvertFrom-Json)
}

function Assert-SelfTestThrows {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $failure = $null
    try {
        & $Operation *> $null
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw "Source-rights self-test '$Name' did not fail closed."
    }
    if ($failure.Exception.Message -notmatch $ExpectedMessage) {
        throw (
            "Source-rights self-test '$Name' failed unexpectedly: " +
            $failure.Exception.Message
        )
    }
}

function Invoke-SourceRightsSelfTest {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) (
        'DesktopPet-SourceRights-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        foreach ($directory in @(
                'src\Fortunes',
                'src\Models',
                'src\dotNet',
                'src\Images',
                'src\Resources',
                'src\Properties',
                'Pets\sample',
                'docs\rights')) {
            New-Item -ItemType Directory `
                -Path (Join-Path $scratch $directory) -Force | Out-Null
        }
        $fixtureBytes = @{
            'ProductVersion.props' =
                $Utf8Strict.GetBytes("<Project />`n")
            'src/DesktopPet_Portable.csproj' =
                $Utf8Strict.GetBytes(
                    '<Project xmlns="http://schemas.microsoft.com/' +
                    'developer/msbuild/2003"><ItemGroup><Compile Include="' +
                    'dotNet\Engine.cs" /></ItemGroup></Project>' + "`n")
            'src/Directory.Build.props' =
                $Utf8Strict.GetBytes("<Project />`n")
            'src/app.config' =
                $Utf8Strict.GetBytes("<configuration />`n")
            'src/Properties/app.manifest' =
                $Utf8Strict.GetBytes("<assembly />`n")
            'src/Properties/Settings.settings' =
                $Utf8Strict.GetBytes("<Settings />`n")
            'src/dotNet/Engine.cs' =
                $Utf8Strict.GetBytes("internal sealed class Engine { }`n")
            'src/Fortunes/fortunes.txt' =
                $Utf8Strict.GetBytes(
                    "sample`tgeneral`tgeneral`t0`tA valid fixture fortune.")
            'src/Models/bge-small.onnx' = [byte[]](1, 2, 3, 4)
            'src/Models/bge-small.vocab.txt' =
                $Utf8Strict.GetBytes("token`n")
            'src/icon.ico' = [byte[]](5, 6, 7)
            'src/esheep.png.ico' = [byte[]](5, 6, 7)
            'src/Images/about.png' = [byte[]](8)
            'src/Images/esheep.png' = [byte[]](9)
            'src/Images/exit.png' = [byte[]](10)
            'src/Images/help.png' = [byte[]](11)
            'src/Images/install.png' = [byte[]](12)
            'src/Images/option.png' = [byte[]](13)
            'src/Resources/animations.xml' =
                $Utf8Strict.GetBytes("<animations />`n")
            'src/Resources/animations.xsd' =
                $Utf8Strict.GetBytes("<schema />`n")
            'Pets/sample/animations.xml' =
                $Utf8Strict.GetBytes("<animations />`n")
            'Pets/sample/icon.png' = [byte[]](14)
            'Pets/esheep_ani.gif' = [byte[]](15)
            'Pets/esheepbackground.jpg' = [byte[]](16)
            'Pets/pets.json' = $Utf8Strict.GetBytes("{} `n")
        }
        foreach ($entry in $fixtureBytes.GetEnumerator()) {
            [IO.File]::WriteAllBytes(
                (Join-Path $scratch $entry.Key.Replace('/', '\')),
                [byte[]]$entry.Value)
        }
        $reviewRelative = 'docs/rights/source-review.md'
        $reviewPath = Join-Path $scratch $reviewRelative.Replace('/', '\')
        [IO.File]::WriteAllBytes(
            $reviewPath,
            $Utf8Strict.GetBytes("Reviewed fixture source and license.`n"))
        $reviewHash = (
            Get-FileHash -LiteralPath $reviewPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()

        & git -C $scratch init -q
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not initialize source-rights self-test Git repository.'
        }
        & git -C $scratch config core.autocrlf false
        & git -C $scratch add -- src docs
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not stage source-rights self-test files.'
        }

        & git -C $scratch add -- ProductVersion.props Pets
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not stage source-rights self-test source sets.'
        }

        $fixtureGitPath = (
            Get-Command git -CommandType Application -ErrorAction Stop |
                Select-Object -First 1   # several git.exe on CI PATH -> take the effective one
        ).Source
        $expectedSourceSetMembers = [ordered]@{
            '@engine-source' = @(
                'ProductVersion.props',
                'src/DesktopPet_Portable.csproj',
                'src/Directory.Build.props',
                'src/Properties/Settings.settings',
                'src/Properties/app.manifest',
                'src/app.config',
                'src/dotNet/Engine.cs'
            )
            '@bundled-art' = @(
                'src/Images/about.png',
                'src/Images/esheep.png',
                'src/Images/exit.png',
                'src/Images/help.png',
                'src/Images/install.png',
                'src/Images/option.png',
                'src/Resources/animations.xml',
                'src/Resources/animations.xsd',
                'src/esheep.png.ico',
                'src/icon.ico'
            )
            '@downloadable-pet-art' = @(
                'Pets/esheep_ani.gif',
                'Pets/esheepbackground.jpg',
                'Pets/pets.json',
                'Pets/sample/animations.xml',
                'Pets/sample/icon.png'
            )
        }
        foreach ($setId in $expectedSourceSetMembers.Keys) {
            [string[]]$expectedMembers = @(
                $expectedSourceSetMembers[$setId])
            [Array]::Sort($expectedMembers, [StringComparer]::Ordinal)
            [string[]]$actualMembers = @(
                Get-SourceSetFiles `
                    -SetId $setId `
                    -RepoRoot $scratch `
                    -GitPath $fixtureGitPath
            )
            if ($actualMembers.Count -ne $expectedMembers.Count) {
                throw (
                    "Source-rights self-test set '$setId' selected " +
                    "$($actualMembers.Count) members; expected " +
                    "$($expectedMembers.Count)."
                )
            }
            for ($memberIndex = 0;
                $memberIndex -lt $expectedMembers.Count;
                $memberIndex++) {
                if ($actualMembers[$memberIndex] -cne
                    $expectedMembers[$memberIndex]) {
                    throw (
                        "Source-rights self-test set '$setId' member " +
                        "$memberIndex is '$($actualMembers[$memberIndex])'; " +
                        "expected '$($expectedMembers[$memberIndex])'."
                    )
                }
            }
        }

        $assets = foreach ($entry in $RequiredAssets.GetEnumerator()) {
            $assetHash = if (
                $entry.Key.StartsWith('@', [StringComparison]::Ordinal)) {
                (
                    Get-SourceSetFingerprint `
                        -SetId $entry.Key `
                        -RepoRoot $scratch `
                        -GitPath $fixtureGitPath
                ).Sha256
            }
            else {
                (
                    Get-FileHash -LiteralPath (
                        Join-Path $scratch $entry.Key.Replace('/', '\')
                    ) -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
            [pscustomobject][ordered]@{
                path = $entry.Key
                assetType = $entry.Value
                sha256 = $assetHash
                releaseApproved = $false
                sourceApprovals = @()
            }
        }
        $baseline = [pscustomobject][ordered]@{
            schemaVersion = 1
            assets = @($assets)
        }
        $result = Assert-SourceRightsEvidence -Evidence $baseline `
            -RepoRoot $scratch
        if ($result.AssetCount -ne $RequiredAssets.Count -or
            $result.Blockers.Count -ne $RequiredAssets.Count) {
            throw 'Unapproved source-rights fixture did not report every blocker.'
        }
        Assert-SelfTestThrows -Name 'false structural release state' `
            -ExpectedMessage 'Source-rights release blockers' `
            -Operation {
                [void](Assert-SourceRightsEvidence -Evidence $baseline `
                    -RepoRoot $scratch -RequireApproval)
            }

        $approvedWithoutEvidence = Copy-JsonObject $baseline
        $approvedWithoutEvidence.assets[0].releaseApproved = $true
        Assert-SelfTestThrows -Name 'approval without evidence' `
            -ExpectedMessage 'approved without any source/license evidence' `
            -Operation {
                [void](Assert-SourceRightsEvidence `
                    -Evidence $approvedWithoutEvidence -RepoRoot $scratch)
            }

        $wrongHash = Copy-JsonObject $baseline
        $wrongHash.assets[0].sha256 = 'f' * 64
        Assert-SelfTestThrows -Name 'wrong asset hash' `
            -ExpectedMessage 'hash mismatch' `
            -Operation {
                [void](Assert-SourceRightsEvidence -Evidence $wrongHash `
                    -RepoRoot $scratch)
            }

        $fixtureProjectPath = Join-Path $scratch (
            'src\DesktopPet_Portable.csproj')
        $untrackedSourcePath = Join-Path $scratch (
            'src\dotNet\Untracked.cs')
        try {
            [IO.File]::WriteAllBytes(
                $fixtureProjectPath,
                $Utf8Strict.GetBytes(
                    '<Project xmlns="http://schemas.microsoft.com/' +
                    'developer/msbuild/2003"><ItemGroup><Compile Include="' +
                    'dotNet\Engine.cs" /><Compile Include="' +
                    'dotNet\Untracked.cs" /></ItemGroup></Project>' + "`n"))
            [IO.File]::WriteAllBytes(
                $untrackedSourcePath,
                $Utf8Strict.GetBytes(
                    "internal sealed class Untracked { }`n"))
            Assert-SelfTestThrows -Name 'strict untracked release member' `
                -ExpectedMessage 'not tracked exactly by Git' `
                -Operation {
                    [void](Assert-SourceRightsEvidence -Evidence $baseline `
                        -RepoRoot $scratch)
                }
            Assert-SelfTestThrows -Name 'development untracked hash binding' `
                -ExpectedMessage 'hash mismatch' `
                -Operation {
                    [void](Assert-SourceRightsEvidence -Evidence $baseline `
                        -RepoRoot $scratch -AllowUntracked)
                }
        }
        finally {
            [IO.File]::WriteAllBytes(
                $fixtureProjectPath,
                [byte[]]$fixtureBytes['src/DesktopPet_Portable.csproj'])
            if (Test-Path -LiteralPath $untrackedSourcePath) {
                Remove-Item -LiteralPath $untrackedSourcePath -Force
            }
        }

        $fixtureEnginePath = Join-Path $scratch 'src\dotNet\Engine.cs'
        try {
            [IO.File]::WriteAllBytes(
                $fixtureEnginePath,
                $Utf8Strict.GetBytes(
                    "internal sealed class Engine { }`r`n"))
            Assert-SelfTestThrows -Name 'strict CRLF release member' `
                -ExpectedMessage 'must use LF-only release bytes' `
                -Operation {
                    [void](Assert-SourceRightsEvidence -Evidence $baseline `
                        -RepoRoot $scratch)
                }
            $developmentLineEndingResult = Assert-SourceRightsEvidence `
                -Evidence $baseline `
                -RepoRoot $scratch `
                -AllowUntracked
            if ($developmentLineEndingResult.AssetCount -ne
                $RequiredAssets.Count) {
                throw (
                    'Development line-ending normalization did not preserve ' +
                    'the complete source-rights fixture.'
                )
            }
        }
        finally {
            [IO.File]::WriteAllBytes(
                $fixtureEnginePath,
                [byte[]]$fixtureBytes['src/dotNet/Engine.cs'])
        }

        $unsafePath = Copy-JsonObject $baseline
        $unsafePath.assets[0].path = '../outside.txt'
        Assert-SelfTestThrows -Name 'unsafe path' `
            -ExpectedMessage 'path is unsafe' `
            -Operation {
                [void](Assert-SourceRightsEvidence -Evidence $unsafePath `
                    -RepoRoot $scratch)
            }

        $approved = Copy-JsonObject $baseline
        for ($index = 0; $index -lt $approved.assets.Count; $index++) {
            $approved.assets[$index].releaseApproved = $true
            $approvalProperties = [ordered]@{
                sourceId = "fixture-source-$index"
                sourceRepository = 'https://example.invalid/source.git'
                sourceRevision = 'a' * 40
                conversionProcedure =
                    'Copy the reviewed fixture bytes without transformation.'
                licenseExpression = 'MIT'
                evidencePath = $reviewRelative
                evidenceSha256 = $reviewHash
                approvedBy = 'release-review@example.invalid'
                approvedAtUtc = '2026-07-30T12:00:00Z'
            }
            $approvedAssetPath = [string]$approved.assets[$index].path
            if ($approvedAssetPath.StartsWith(
                    '@',
                    [StringComparison]::Ordinal)) {
                $approvalProperties['memberPaths'] = @(
                    Get-SourceSetFiles `
                        -SetId $approvedAssetPath `
                        -RepoRoot $scratch `
                        -GitPath $fixtureGitPath
                )
            }
            $approved.assets[$index].sourceApprovals = @(
                [pscustomobject]$approvalProperties
            )
        }

        $blanketVirtualApproval = Copy-JsonObject $approved
        $blanketVirtualAsset = @(
            $blanketVirtualApproval.assets |
                Where-Object { $_.path -ceq '@bundled-art' }
        )[0]
        $blanketVirtualAsset.sourceApprovals[0].PSObject.Properties.Remove(
            'memberPaths')
        Assert-SelfTestThrows -Name 'blanket virtual-set approval' `
            -ExpectedMessage 'property count' `
            -Operation {
                [void](Assert-SourceRightsEvidence `
                    -Evidence $blanketVirtualApproval -RepoRoot $scratch)
            }

        $incompleteVirtualApproval = Copy-JsonObject $approved
        $incompleteVirtualAsset = @(
            $incompleteVirtualApproval.assets |
                Where-Object { $_.path -ceq '@bundled-art' }
        )[0]
        $incompleteVirtualAsset.sourceApprovals[0].memberPaths = @(
            @($incompleteVirtualAsset.sourceApprovals[0].memberPaths) |
                Select-Object -Skip 1
        )
        Assert-SelfTestThrows -Name 'incomplete virtual-set coverage' `
            -ExpectedMessage 'approval coverage is incomplete' `
            -Operation {
                [void](Assert-SourceRightsEvidence `
                    -Evidence $incompleteVirtualApproval -RepoRoot $scratch)
            }

        $overlappingVirtualApproval = Copy-JsonObject $approved
        $overlappingVirtualAsset = @(
            $overlappingVirtualApproval.assets |
                Where-Object { $_.path -ceq '@bundled-art' }
        )[0]
        $overlap = Copy-JsonObject (
            $overlappingVirtualAsset.sourceApprovals[0])
        $overlap.sourceId = 'fixture-overlap'
        $overlap.memberPaths = @(
            @($overlappingVirtualAsset.sourceApprovals[0].memberPaths)[0]
        )
        $overlappingVirtualAsset.sourceApprovals = @(
            $overlappingVirtualAsset.sourceApprovals[0],
            $overlap
        )
        Assert-SelfTestThrows -Name 'overlapping virtual-set coverage' `
            -ExpectedMessage 'covered by more than one source approval' `
            -Operation {
                [void](Assert-SourceRightsEvidence `
                    -Evidence $overlappingVirtualApproval -RepoRoot $scratch)
            }

        $approvedResult = Assert-SourceRightsEvidence -Evidence $approved `
            -RepoRoot $scratch -RequireApproval
        if ($approvedResult.ApprovalCount -ne $RequiredAssets.Count -or
            $approvedResult.Blockers.Count -ne 0) {
            throw 'Complete approved source-rights fixture did not pass cleanly.'
        }

        Write-Host (
            'Source-rights self-tests passed: valid false state, release ' +
            'blockers, approval evidence, hash, path, strict/development ' +
            'tracking, exact virtual-set coverage, and complete approval.'
        ) -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $scratch) {
            $resolvedScratch = [IO.Path]::GetFullPath($scratch)
            $resolvedTemp = [IO.Path]::GetFullPath(
                [IO.Path]::GetTempPath()).TrimEnd('\')
            if (-not $resolvedScratch.StartsWith(
                    $resolvedTemp + '\DesktopPet-SourceRights-',
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove unsafe self-test root: $resolvedScratch"
            }
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-SourceRightsSelfTest
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath) -or
    -not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    if ([string]::IsNullOrWhiteSpace($EvidencePath) -or
        -not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "Source-rights manifest is missing: '$EvidencePath'."
    }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        throw 'Source-rights repository root is required.'
    }
    $evidence = Get-StrictJson -Path (
        (Resolve-Path -LiteralPath $EvidencePath).Path)
    $result = Assert-SourceRightsEvidence -Evidence $evidence `
        -RepoRoot $RepositoryRoot `
        -RequireApproval:$RequireReleaseApproval `
        -AllowUntracked:$AllowUntrackedDevelopment
    Write-Host (
        'Source-rights structure verified: {0} exact assets, {1} approved.' -f
        $result.AssetCount,
        $result.ApprovalCount
    ) -ForegroundColor Green
    foreach ($blocker in @($result.Blockers)) {
        Write-Host "BLOCKER: $blocker" -ForegroundColor Yellow
    }
}
elseif (-not $SelfTest) {
    throw (
        'Specify -SelfTest or both -EvidencePath and -RepositoryRoot. ' +
        'Use -RequireReleaseApproval in publication gates.'
    )
}
