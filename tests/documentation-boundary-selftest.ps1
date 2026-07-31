#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot

$trackedSandcastleProjects = @(
    & git -C $repoRoot ls-files -- '*.shfbproj'
)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked Sandcastle projects.'
}
$presentTrackedSandcastleProjects = @(
    foreach ($relativePath in $trackedSandcastleProjects) {
        if (Test-Path -LiteralPath (
                Join-Path $repoRoot $relativePath.Replace('/', '\')) -PathType Leaf) {
            $relativePath
        }
    }
)
$onDiskSandcastleProjects = @(
    Get-ChildItem -LiteralPath $repoRoot -Filter '*.shfbproj' -File -Recurse |
        ForEach-Object {
            $_.FullName.Substring($repoRoot.Length).
                TrimStart('\', '/').Replace('\', '/')
        }
)
$authoritativeSandcastleProject = 'Manual/DesktopPet.shfbproj'
if ($presentTrackedSandcastleProjects.Count -ne 1 -or
    $presentTrackedSandcastleProjects[0] -cne $authoritativeSandcastleProject -or
    $onDiskSandcastleProjects.Count -ne 1 -or
    $onDiskSandcastleProjects[0] -cne $authoritativeSandcastleProject) {
    throw (
        'Manual/DesktopPet.shfbproj must be the sole supported Sandcastle ' +
        'project; stale or duplicate generators are forbidden.'
    )
}

[xml]$manualProject = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Manual\DesktopPet.shfbproj') -Raw
$namespace = New-Object Xml.XmlNamespaceManager($manualProject.NameTable)
$namespace.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
$output = [string]$manualProject.SelectSingleNode(
    '//m:OutputPath',
    $namespace).InnerText
if ($output -match '(?i)(?:^|[\\/])docs(?:[\\/]|$)') {
    throw 'The optional Sandcastle project can overwrite the historical docs snapshot.'
}

$manualReadme = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Manual\readme.md') -Raw
$docsIndex = Get-Content -LiteralPath (
    Join-Path $repoRoot 'docs\index.html') -Raw
$legacySiteConfig = Get-Content -LiteralPath (
    Join-Path $repoRoot '_config.yml') -Raw
$legacyDefaultLayout = Get-Content -LiteralPath (
    Join-Path $repoRoot '_layouts\default.html') -Raw
$legacyAppLayout = Get-Content -LiteralPath (
    Join-Path $repoRoot '_layouts\app.html') -Raw
$legacyPetLayout = Get-Content -LiteralPath (
    Join-Path $repoRoot '_layouts\pet.html') -Raw
$legacyCommentsInclude = Get-Content -LiteralPath (
    Join-Path $repoRoot '_includes\disqus.html') -Raw
$legacyPetDownload = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Pets\download.html') -Raw
$legacyDownload = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Download.md') -Raw
$legacyChangelog = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Changelog.md') -Raw
$petFormatGuide = Get-Content -LiteralPath (
    Join-Path $repoRoot 'grimoire\03-pet-xml-format.md') -Raw
$fortuneAssessment = Get-Content -LiteralPath (
    Join-Path $repoRoot 'FORTUNE-SOURCES-ASSESSMENT.md') -Raw
$fortuneSheepPlan = Get-Content -LiteralPath (
    Join-Path $repoRoot 'FORTUNE-SHEEP-PLAN.md') -Raw
$fortuneBuilder = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Fortunes\build-corpus.sh') -Raw
$fortuneTaxonomy = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\Fortunes\TAXONOMY.md') -Raw
$mainReadme = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Readme.md') -Raw
$supportGuide = Get-Content -LiteralPath (
    Join-Path $repoRoot 'SUPPORT.md') -Raw
$provenanceGuide = Get-Content -LiteralPath (
    Join-Path $repoRoot 'PROVENANCE.md') -Raw
$releaseChecklist = Get-Content -LiteralPath (
    Join-Path $repoRoot 'docs\RELEASE-CHECKLIST.md') -Raw
$securityGuide = Get-Content -LiteralPath (
    Join-Path $repoRoot 'SECURITY.md') -Raw
$grimoireReadme = Get-Content -LiteralPath (
    Join-Path $repoRoot 'grimoire\README.md') -Raw
$grimoireHistory = Get-Content -LiteralPath (
    Join-Path $repoRoot 'grimoire\01-history-and-lineage.md') -Raw
$grimoireEcosystem = Get-Content -LiteralPath (
    Join-Path $repoRoot 'grimoire\04-upstream-forks-ecosystem.md') -Raw
$grimoireGlossary = Get-Content -LiteralPath (
    Join-Path $repoRoot 'grimoire\05-glossary-and-faq.md') -Raw
$petsReadme = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Pets\README.md') -Raw
$petInfoPage = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Pets\Info.html') -Raw
$petVideoPage = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Pets\Video.html') -Raw
$privacyGuide = Get-Content -LiteralPath (
    Join-Path $repoRoot 'PRIVACY.md') -Raw
$thirdPartyNotices = Get-Content -LiteralPath (
    Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Raw
$packReadme = Get-Content -LiteralPath (
    Join-Path $repoRoot 'packs\README.md') -Raw
$releaseWorkflow = Get-Content -LiteralPath (
    Join-Path $repoRoot '.github\workflows\release.yml') -Raw
$petEditorSolution = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetEditor.sln') -Raw
$petEditorSentinel = Join-Path $repoRoot 'Tools\PetEditor.UNSUPPORTED-LEGACY.md'
$gitIgnore = Get-Content -LiteralPath (
    Join-Path $repoRoot '.gitignore')

if ($manualReadme -notmatch 'release checklist is the authority' -or
    $manualReadme -notmatch 'sole supported Sandcastle generator' -or
    $docsIndex -notmatch 'archival snapshot of DesktopPet 1\.0\.6') {
    throw 'Documentation authority is not explicit.'
}
if ($grimoireReadme -notmatch 'Authority and scope' -or
    $grimoireReadme -notmatch 'materially modified' -or
    $grimoireEcosystem -match 'byte-for-byte behaviourally identical' -or
    $grimoireEcosystem -match 'or `webxml=<url>`' -or
    $grimoireHistory -match 'physics/animation engine completely untouched' -or
    $grimoireGlossary -match 'single self-contained `?\.exe' -or
    $grimoireGlossary -match 'process shows as `eSheep`' -or
    $grimoireGlossary -match 'parse error silently falls back') {
    throw 'The grimoire retains a known current-runtime contradiction.'
}
if ($petsReadme -notmatch 'does \*\*not\*\* currently offer an online pet-download catalog' -or
    $petsReadme -notmatch '48.48 ICO' -or
    $petsReadme -notmatch '@downloadable-pet-art' -or
    $petsReadme -notmatch 'releaseApproved: false') {
    throw 'Pet authoring guidance does not describe current validation and rights policy.'
}
if ($petInfoPage -match '(?i)<iframe\b' -or
    $petVideoPage -match '(?i)<iframe\b' -or
    $petInfoPage -notmatch 'not loaded automatically' -or
    $petVideoPage -notmatch 'not loaded\s+automatically' -or
    $petInfoPage -notmatch 'rel="noopener noreferrer"' -or
    $petVideoPage -notmatch 'rel="noopener noreferrer"') {
    throw 'Historical video pages still auto-load third-party content.'
}

$msplMarker = (
    'This code is published under the Microsoft Public ' +
    'License (Ms-PL).')
$msplMarkedFiles = @(
    & git -C $repoRoot grep -Il -F $msplMarker --
)
if ($LASTEXITCODE -ne 0 -or $msplMarkedFiles.Count -eq 0) {
    throw 'Unable to enumerate tracked files marked as Microsoft Public License.'
}
foreach ($msplMarkedFile in $msplMarkedFiles) {
    if (-not $thirdPartyNotices.Contains(
            ('`{0}`' -f $msplMarkedFile))) {
        throw (
            'THIRD_PARTY_NOTICES.md does not inventory tracked Ms-PL file: ' +
            $msplMarkedFile)
    }
}

$jqueryArchivePath = 'docs/scripts/jquery-1.11.0.min.js'
$jqueryArchive = Get-Content -LiteralPath (
    Join-Path $repoRoot $jqueryArchivePath.Replace('/', '\')) -Raw
if ($jqueryArchive -notmatch 'jQuery v1\.11\.0' -or
    -not $thirdPartyNotices.Contains(('`{0}`' -f $jqueryArchivePath)) -or
    $thirdPartyNotices -notmatch 'jQuery 1\.11\.0' -or
    $thirdPartyNotices -notmatch '(?is)jQuery 1\.11\.0.*MIT License') {
    throw 'The retained jQuery 1.11.0 archive is not completely inventoried.'
}

$requiredMsplText = @'
This license governs use of the accompanying software. If you use the software, you accept this license. If you do not accept the license, do not use the software.

1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the software.

A "contributor" is any person that distributes its contribution under this license.

"Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free copyright license to reproduce its contribution, prepare derivative works of its contribution, and distribute its contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or otherwise dispose of its contribution in the software or derivative works of the contribution in the software.

3. Conditions and Limitations

(A) No Trademark License- This license does not grant you rights to use any contributors' name, logo, or trademarks.

(B) If you bring a patent claim against any contributor over patents that you claim are infringed by the software, your patent license from such contributor to the software ends automatically.

(C) If you distribute any portion of the software, you must retain all copyright, patent, trademark, and attribution notices that are present in the software.

(D) If you distribute any portion of the software in source code form, you may do so only under this license by including a complete copy of this license with your distribution. If you distribute any portion of the software in compiled or object code form, you may only do so under a license that complies with this license.

(E) The software is licensed "as-is." You bear the risk of using it. The contributors give no express warranties, guarantees or conditions. You may have additional consumer rights under your local laws which this license cannot change. To the extent permitted under your local laws, the contributors exclude the implied warranties of merchantability, fitness for a particular purpose and non-infringement.
'@
$normalizedThirdPartyNotices = (
    ($thirdPartyNotices -replace '(?m)^#{1,6}\s+', '') -replace '\s+', ' '
).Trim()
$normalizedRequiredMsplText = (
    $requiredMsplText -replace '\s+', ' '
).Trim()
if (-not $normalizedThirdPartyNotices.Contains($normalizedRequiredMsplText)) {
    throw 'THIRD_PARTY_NOTICES.md does not retain the complete official Ms-PL text.'
}

if ($securityGuide -notmatch 'security/advisories/new' -or
    $securityGuide -notmatch 'Do not include exploit details' -or
    $supportGuide -notmatch '\[SECURITY\.md\]\(SECURITY\.md\)') {
    throw 'Private security-reporting guidance is missing or not linked from Support.'
}
if ($petEditorSolution -notmatch 'UNSUPPORTED LEGACY' -or
    -not (Test-Path -LiteralPath $petEditorSentinel -PathType Leaf)) {
    throw 'PetEditor does not carry an unmistakable unsupported-legacy sentinel.'
}
if ($gitIgnore -cnotcontains '/Manual/generated-current/') {
    throw 'Optional generated current API output is not ignored.'
}

function Assert-HistoricalJekyllPage {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Permalink
    )

    $frontMatter = [regex]::Match(
        $Content,
        '\A---\r?\n(?<yaml>.*?)\r?\n---(?:\r?\n|$)',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $frontMatter.Success -or
        $frontMatter.Groups['yaml'].Value -notmatch '(?m)^layout:\s+default\s*$' -or
        $frontMatter.Groups['yaml'].Value -notmatch (
            '(?m)^permalink:\s+' + [regex]::Escape($Permalink) + '\s*$')) {
        throw "$Name does not declare its standard-Jekyll layout and permalink."
    }
    if ($Content -notmatch 'Historical archive \(unsupported\)' -or
        $Content -notmatch 'https://github\.com/bigfnj/desktopPet/releases') {
        throw "$Name does not carry the historical-only warning and current release link."
    }
}

function Get-SimpleTopLevelYamlList {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $block = [regex]::Match(
        $Content,
        ('(?ms)^' + [regex]::Escape($Key) +
            ':[ \t]*(?<items>(?:\r?\n[ \t]+-[^\r\n]*)+)'))
    if (-not $block.Success) { return @() }

    $values = New-Object 'Collections.Generic.List[string]'
    foreach ($match in [regex]::Matches(
            $block.Groups['items'].Value,
            '(?m)^[ \t]+-\s*(?<value>[^#\r\n]+?)\s*(?:#.*)?$')) {
        $value = [string]$match.Groups['value'].Value.Trim()
        $value = $value -replace '^[''"]|[''"]$', ''
        $value = $value.Replace('\', '/').Trim('/')
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $values.Add($value)
        }
    }
    return @($values)
}

$jekyllExcludedRoots = @(
    Get-SimpleTopLevelYamlList -Content $legacySiteConfig -Key 'exclude')
$jekyllIncludedRoots = @(
    Get-SimpleTopLevelYamlList -Content $legacySiteConfig -Key 'include')
if ($jekyllExcludedRoots -cnotcontains 'docs' -or
    $jekyllIncludedRoots -ccontains 'docs') {
    throw (
        'The generated docs archive must be excluded from, and cannot be ' +
        're-included in, the public Jekyll topology.'
    )
}

if ($legacySiteConfig -notmatch '(?m)^historical_snapshot:\s+true\s*$' -or
    $legacySiteConfig -notmatch '(?m)^\s+github:\s+bigfnj/desktopPet\s*$' -or
    $legacySiteConfig -notmatch (
        '(?m)^url:\s+"https://bigfnj\.github\.io"\s*$') -or
    $legacySiteConfig -notmatch '(?m)^baseurl:\s+"/desktopPet"\s*$' -or
    $legacySiteConfig -match 'adrianotiger\.github\.io/desktopPet') {
    throw 'The root Jekyll configuration is not explicitly quarantined as historical.'
}
if ($legacyDefaultLayout -notmatch 'Historical upstream website archive\.' -or
    $legacyDefaultLayout -notmatch 'https://github\.com/bigfnj/desktopPet/releases' -or
    $legacyDefaultLayout -match 'href="\{\{\s*site\.url\s*\}\}') {
    throw 'The legacy Jekyll layout lacks a site-wide archive warning or uses stale absolute navigation.'
}
if ($legacyAppLayout -notmatch '\A---\r?\n(?s:.*?)\r?\n---' -or
    $legacyAppLayout -notmatch '(?m)^layout:\s+default\s*$' -or
    $legacyPetDownload -notmatch '(?m)^layout:\s+app\s*$' -or
    $legacyPetDownload -notmatch 'historical interactive browser download preview is disabled' -or
    $legacyPetDownload -notmatch 'https://github\.com/bigfnj/desktopPet/releases') {
    throw 'The historical app/download route does not inherit and display the archive quarantine.'
}

function Get-UnsafeHistoricalConstruct {
    param([Parameter(Mandatory = $true)][string]$Content)

    $patterns = [ordered]@{
        'script element' = '(?is)<script\b'
        'javascript URL' = '(?i)javascript\s*:'
        'dynamic script creation' =
            '(?i)createElement\s*\(\s*[''"]script[''"]'
        'HTML injection sink' = '(?i)\binnerHTML\b'
        'inline event handler' =
            '(?is)<[^>]+\son[a-z][a-z0-9_-]*\s*='
        'HTML srcdoc content' = '(?is)<[^>]+\ssrcdoc\s*='
    }
    foreach ($entry in $patterns.GetEnumerator()) {
        if ($Content -match [string]$entry.Value) {
            return [string]$entry.Key
        }
    }
    return $null
}

function Test-IsJekyllExcludedPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string[]]$ExcludedRoots
    )

    $normalizedPath = $RelativePath.Replace('\', '/').TrimStart('/')
    foreach ($root in $ExcludedRoots) {
        $normalizedRoot = $root.Replace('\', '/').Trim('/')
        if ($normalizedPath.Equals(
                $normalizedRoot,
                [StringComparison]::Ordinal) -or
            $normalizedPath.StartsWith(
                $normalizedRoot + '/',
                [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

$sharedHistoricalTemplates = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot '_layouts') -Filter '*.html' -File
    Get-ChildItem -LiteralPath (Join-Path $repoRoot '_includes') -Filter '*.html' -File
)
foreach ($template in $sharedHistoricalTemplates) {
    $templateContent = Get-Content -LiteralPath $template.FullName -Raw
    $unsafeConstruct = Get-UnsafeHistoricalConstruct -Content $templateContent
    if ($null -ne $unsafeConstruct) {
        throw (
            "Historical shared template contains $unsafeConstruct`: " +
            $template.FullName)
    }
}

$jekyllPublicSourceFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring(
                $repoRoot.Length).TrimStart('\', '/')
            if ($relative -match (
                '^(?i)(?:\.git|build|dist|packages|bin|obj|node_modules|' +
                'Manual|_layouts|_includes)[\\/]')) {
                return $false
            }
            return -not (Test-IsJekyllExcludedPath `
                -RelativePath $relative `
                -ExcludedRoots $jekyllExcludedRoots)
        }
)
$archiveFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File -Recurse)
if ($archiveFiles.Count -eq 0) {
    throw 'The generated docs archive unexpectedly contains no files.'
}
$publicSourcePaths = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::OrdinalIgnoreCase)
foreach ($publicSource in $jekyllPublicSourceFiles) {
    [void]$publicSourcePaths.Add($publicSource.FullName)
}
$archiveLeaks = @(
    $archiveFiles |
        Where-Object { $publicSourcePaths.Contains($_.FullName) } |
        ForEach-Object {
            $_.FullName.Substring($repoRoot.Length).TrimStart('\', '/')
        }
)
if ($archiveLeaks.Count -ne 0) {
    throw (
        "Generated archive files entered the public Jekyll topology:`n - " +
        ($archiveLeaks -join "`n - "))
}

$publishedContentPages = @(
    $jekyllPublicSourceFiles |
        Where-Object { $_.Extension -in @('.html', '.md') }
)
foreach ($page in $publishedContentPages) {
    $pageContent = Get-Content -LiteralPath $page.FullName -Raw
    if ($pageContent -notmatch '\A---\r?\n') { continue }

    $unsafeConstruct = Get-UnsafeHistoricalConstruct -Content $pageContent
    if ($null -ne $unsafeConstruct) {
        $relative = $page.FullName.Substring(
            $repoRoot.Length).TrimStart('\', '/')
        throw "Published historical page contains $unsafeConstruct`: $relative"
    }
}
if ($legacyDefaultLayout -notmatch (
        '\{\{\s*[''"]/Pets/esheepbackground\.jpg[''"]\s*\|\s*relative_url\s*\}\}') -or
    $legacyDefaultLayout -notmatch (
        '\{\{\s*[''"]/Pets/esheep_ani\.gif[''"]\s*\|\s*relative_url\s*\}\}') -or
    $legacyPetLayout -notmatch 'historical browser-based live preview has been disabled' -or
    $legacyCommentsInclude -notmatch 'Historical third-party comments are disabled') {
    throw 'Historical shared templates do not use the repository-owned static fallback.'
}
Assert-HistoricalJekyllPage `
    -Name 'Download.md' `
    -Content $legacyDownload `
    -Permalink '/Download.html'
Assert-HistoricalJekyllPage `
    -Name 'Changelog.md' `
    -Content $legacyChangelog `
    -Permalink '/Changelog.html'

function Test-JekyllRouteSource {
    param([Parameter(Mandatory = $true)][string]$Route)

    $relative = $Route.TrimStart('/')
    if ([string]::IsNullOrEmpty($relative)) {
        $relative = 'index.html'
    }
    elseif ($relative.EndsWith('/', [StringComparison]::Ordinal)) {
        $relative += 'index.html'
    }
    $directPath = Join-Path $repoRoot ($relative -replace '/', '\')
    if (Test-Path -LiteralPath $directPath -PathType Leaf) {
        return $true
    }

    foreach ($page in @($legacyDownload, $legacyChangelog)) {
        if ($page -match (
                '(?m)^permalink:\s+' + [regex]::Escape($Route) + '\s*$')) {
            return $true
        }
    }
    return $false
}

$jekyllLinkFailures = New-Object 'Collections.Generic.List[string]'
$liquidRoutePattern =
    '\{\{\s*[''"](?<route>/[^''"]+)[''"]\s*\|\s*relative_url\s*\}\}'
foreach ($source in @(
        @{ Name = '_layouts/default.html'; Content = $legacyDefaultLayout },
        @{ Name = 'Download.md'; Content = $legacyDownload },
        @{ Name = 'Changelog.md'; Content = $legacyChangelog }
    )) {
    foreach ($match in [regex]::Matches(
            [string]$source.Content,
            $liquidRoutePattern)) {
        $route = [string]$match.Groups['route'].Value
        if (-not (Test-JekyllRouteSource -Route $route)) {
            $jekyllLinkFailures.Add(("{0}: missing route target '{1}'" -f
                    $source.Name,
                    $route))
        }
    }
}
if ($jekyllLinkFailures.Count -gt 0) {
    throw (
        "Raw HTML/Liquid routes have no standard-Jekyll source target:`n - " +
        ($jekyllLinkFailures -join "`n - "))
}

function Get-MarkdownDestination {
    param([Parameter(Mandatory = $true)][string]$RawDestination)

    $candidate = $RawDestination.Trim()
    if ($candidate.StartsWith('<', [StringComparison]::Ordinal)) {
        $closing = $candidate.IndexOf('>')
        if ($closing -lt 1) {
            throw "Malformed angle-bracket Markdown destination: $RawDestination"
        }
        return $candidate.Substring(1, $closing - 1)
    }

    $pathMatch = [regex]::Match($candidate, '^\S+')
    if (-not $pathMatch.Success) { return '' }
    return $pathMatch.Value
}

function Assert-ExactCaseLocalPath {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $pathOnly = $Destination
    $suffix = $pathOnly.IndexOfAny([char[]]'?#')
    if ($suffix -ge 0) {
        $pathOnly = $pathOnly.Substring(0, $suffix)
    }
    if ([string]::IsNullOrWhiteSpace($pathOnly)) { return }

    try {
        $pathOnly = [Uri]::UnescapeDataString($pathOnly)
    }
    catch {
        throw "Destination is not valid percent-encoded text: $Destination"
    }

    $repoRooted = $pathOnly.StartsWith('/', [StringComparison]::Ordinal) -or
        $pathOnly.StartsWith('\', [StringComparison]::Ordinal)
    $current = if ($repoRooted) { $repoRoot } else { $BaseDirectory }
    if ($repoRooted) {
        $pathOnly = $pathOnly -replace '^[\\/]+', ''
    }

    $components = @($pathOnly -split '[\\/]' | Where-Object { $_ -ne '' })
    for ($index = 0; $index -lt $components.Count; $index++) {
        $component = $components[$index]
        if ($component -ceq '.') { continue }
        if ($component -ceq '..') {
            if ([string]::Equals(
                    $current,
                    $repoRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Destination escapes the repository: $Destination"
            }
            $current = Split-Path -Parent $current
            continue
        }

        $entries = @(Get-ChildItem -LiteralPath $current -Force)
        $exact = @($entries | Where-Object { $_.Name -ceq $component })
        if ($exact.Count -eq 0) {
            $differentCase = @($entries | Where-Object { $_.Name -ieq $component })
            if ($differentCase.Count -gt 0) {
                throw (
                    "Path case mismatch: Markdown uses '{0}', disk uses '{1}'." -f
                    $component,
                    $differentCase[0].Name)
            }
            throw "Missing local path component '$component'."
        }
        if ($exact.Count -ne 1) {
            throw "Local path component '$component' is ambiguous."
        }
        if ($index -lt ($components.Count - 1) -and -not $exact[0].PSIsContainer) {
            throw "Local path component '$component' is not a directory."
        }
        $current = $exact[0].FullName
    }

    $repoPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [string]::Equals(
            $current,
            $repoRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        -not $current.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Destination escapes the repository: $Destination"
    }
}

$markdownFailures = New-Object 'Collections.Generic.List[string]'
$markdownFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Filter '*.md' -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring($repoRoot.Length).TrimStart('\', '/')
            $relative -notmatch '^(?i)(?:\.git|build|dist|packages|bin|obj|node_modules)[\\/]'
        }
)
$inlineLinkPattern = '!?\[[^\]\r\n]*\]\((?<destination><[^>\r\n]+>|[^)\r\n]+)\)'
foreach ($markdownFile in $markdownFiles) {
    $content = Get-Content -LiteralPath $markdownFile.FullName -Raw
    foreach ($link in [regex]::Matches($content, $inlineLinkPattern)) {
        try {
            $destination = Get-MarkdownDestination (
                [string]$link.Groups['destination'].Value)
            if ([string]::IsNullOrWhiteSpace($destination) -or
                $destination.StartsWith('#', [StringComparison]::Ordinal) -or
                $destination.StartsWith('//', [StringComparison]::Ordinal) -or
                $destination -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
                continue
            }
            Assert-ExactCaseLocalPath `
                -BaseDirectory $markdownFile.DirectoryName `
                -Destination $destination
        }
        catch {
            $relative = $markdownFile.FullName.Substring(
                $repoRoot.Length).TrimStart('\', '/')
            $markdownFailures.Add(
                ("{0}: '{1}' - {2}" -f
                    $relative,
                    $link.Groups['destination'].Value,
                    $_.Exception.Message))
        }
    }
}
if ($markdownFailures.Count -gt 0) {
    throw (
        "Local Markdown links are missing or use the wrong case:`n - " +
        ($markdownFailures -join "`n - "))
}

foreach ($packRightsContract in @(
        'canonical HTTPS `sourceRepository`',
        'lowercase 40-character `sourceRevision`',
        '`LicenseRef-` `licenseExpression`',
        '`redistributionGrant`',
        '`obligations`',
        '`recordRanges`',
        'no overlap or gap',
        'approval-bearing manifest must use schema 2'
    )) {
    if (-not $packReadme.Contains($packRightsContract)) {
        throw "Pack rights policy lacks structured evidence contract: $packRightsContract"
    }
}
if ($releaseChecklist -notmatch
        '(?is)structured\s+document.*canonical HTTPS repository.*immutable\s+lowercase\s+40-character\s+revision' -or
    $releaseChecklist -notmatch
        '(?is)redistribution\s+grant.*non-placeholder obligations.*complete, non-overlapping ranges') {
    throw 'Release checklist lacks the structured pack-rights approval boundary.'
}

if ($petFormatGuide -notmatch '\|\s*`<end>`\s*\|\s*yes\s*\|' -or
    $petFormatGuide -notmatch 'current validator accepts only the literal actions `flip` and `none`' -or
    $petFormatGuide -notmatch 'up to \*\*256\*\* `<next>` entries' -or
    $petFormatGuide -notmatch 'restricted `SafeExpression` parser' -or
    $petFormatGuide -notmatch '`Tools/PetEditor` is retained as unsupported legacy source' -or
    $petFormatGuide -notmatch 'required `<childs>` container' -or
    $petFormatGuide -notmatch '\[link:https://\.\.\.\]' -or
    $petFormatGuide -notmatch
        '(?is)user must select the link.*default browser.*absolute HTTPS URLs' -or
    $petFormatGuide -match '\[link:http://') {
    throw 'The pet XML guide does not describe the current validator/runtime authoring boundary.'
}
if ($petFormatGuide -match 'System\.Data\.DataTable\.Compute' -or
    $petFormatGuide -match 'up to 10 `<next>`' -or
    $petFormatGuide -match '≤10 next' -or
    $petFormatGuide -match 'Any other string is currently ignored') {
    throw 'The pet XML guide retains a known legacy-format contradiction.'
}
if ($privacyGuide -notmatch
        '(?is)pet author can supply an About link.*never opens that link\s+automatically' -or
    $privacyGuide -notmatch
        '(?is)only selecting the link.*default browser.*intended application policy.*absolute HTTPS About links' -or
    $privacyGuide -notmatch '(?is)Treat the destination as third-party content') {
    throw 'Privacy guidance omits the user-clicked, pet-supplied HTTPS About-link boundary.'
}

$petSchemaPaths = @(
    Join-Path $repoRoot 'Resources\animations.xsd'
    Join-Path $repoRoot 'src\Resources\animations.xsd'
)
foreach ($petSchemaPath in $petSchemaPaths) {
    [xml]$petSchema = Get-Content -LiteralPath $petSchemaPath -Raw
    $petSchemaNamespace = New-Object Xml.XmlNamespaceManager($petSchema.NameTable)
    $petSchemaNamespace.AddNamespace('xsd', 'http://www.w3.org/2001/XMLSchema')
    $endDeclarations = @(
        $petSchema.SelectNodes(
            '//xsd:element[@name="end"]',
            $petSchemaNamespace)
    )
    if ($endDeclarations.Count -ne 1 -or
        [string]$endDeclarations[0].minOccurs -ne '1') {
        throw "Pet schema does not require animation <end>: $petSchemaPath"
    }
}

$categoricalRightsClaims = @(
    'legally[- ]defensible',
    'zero[- ]risk',
    'low risk',
    'legally dubious',
    'legally murky'
)
foreach ($claim in $categoricalRightsClaims) {
    if ($fortuneAssessment -match $claim) {
        throw "Fortune source assessment retains categorical rights claim: $claim"
    }
}
if ($fortuneAssessment -notmatch 'research and planning evidence, not legal clearance' -or
    $fortuneAssessment -notmatch 'remains a public-release blocker' -or
    $fortuneAssessment -notmatch 'No source\s*>?\s*is approved for redistribution') {
    throw 'Fortune source assessment does not state the evidence-based rights boundary.'
}
if ($fortuneBuilder -match '\(Unlicense/public domain\)' -or
    $fortuneBuilder -notmatch (
        '(?i)repository-level label does not\s+establish redistribution rights') -or
    $fortuneBuilder -notmatch '(?i)source-by-source\s+evidence' -or
    $fortuneBuilder -notmatch '(?i)successful corpus build is not rights clearance') {
    throw 'Fortune builder comments overstate upstream rights or omit source-level clearance.'
}
if ($fortuneSheepPlan -notmatch
        'Historical planning snapshot\s+-\s+non-authoritative' -or
    $fortuneSheepPlan -notmatch '\[Readme\.md\]\(Readme\.md\)' -or
    $fortuneSheepPlan -notmatch
        '\[FORTUNE-SOURCES-ASSESSMENT\.md\]\(FORTUNE-SOURCES-ASSESSMENT\.md\)' -or
    $fortuneSheepPlan -notmatch
        '\[docs/RELEASE-CHECKLIST\.md\]\(docs/RELEASE-CHECKLIST\.md\)' -or
    $fortuneSheepPlan -notmatch
        'redistribution rights for the bundled fortune corpus remain unresolved' -or
    $fortuneSheepPlan -notmatch
        'embedding model and vocabulary are bundled' -or
    $fortuneSheepPlan -notmatch
        'AI brain is disabled by\s+>?\s*default') {
    throw 'The historical Fortune Sheep plan lacks its current-authority warning.'
}
if ($fortuneSheepPlan -match
        '(?i)JKirchartz/fortunes\s*\(\s*Unlicense\s*/\s*public domain\s*\)' -or
    $fortuneSheepPlan -match
        '(?i)Model delivered via\s+\*\*first.run onboarding\*\*' -or
    $fortuneSheepPlan -match
        '(?i)peek ON by default') {
    throw 'The historical Fortune Sheep plan retains a known superseded delivery or rights claim.'
}

if ($supportGuide -notmatch '(?i)published release' -or
    $supportGuide -notmatch '(?i)MSI or portable\s+ZIP' -or
    $supportGuide -notmatch '(?i)SHA-256 checksum,\s+signature,\s+and provenance' -or
    $supportGuide -notmatch '(?i)private,\s+local,\s+or\s+CI build' -or
    $supportGuide -notmatch '(?i)exact 40-character Git commit' -or
    $supportGuide -notmatch '(?i)not a release') {
    throw 'Support guidance does not distinguish published releases from exact-commit private builds.'
}

$conservativeFilterMeaning =
    '(?i)recognized\s+profanity\s+(?:or|and)\s+explicit\s+sexual\s+content'
if ($mainReadme -match '(?i)Remove all profanity' -or
    $mainReadme -notmatch $conservativeFilterMeaning -or
    $mainReadme -notmatch '(?i)conservative\s+`prof`\s+flag' -or
    $fortuneTaxonomy -notmatch $conservativeFilterMeaning -or
    $fortuneTaxonomy -notmatch '(?i)conservative\s+flag') {
    throw 'Fortune documentation does not describe the conservative content-filter flag accurately.'
}

if ($provenanceGuide -notmatch 'Verify the SBOM and packaged payload offline' -or
    $provenanceGuide -notmatch 'aadf3b0b8dbbabdb4d880b0fc714255fea436ff7' -or
    $provenanceGuide -notmatch '239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b' -or
    $provenanceGuide -notmatch 'validator_for' -or
    $provenanceGuide -notmatch 'function Test-SbomPayload' -or
    $provenanceGuide -notmatch 'MSI administrative extraction' -or
    $provenanceGuide -notmatch '\$MsiExitCode -notin @\(0, 3010\)' -or
    $provenanceGuide -notmatch 'SBOM and payload file sets differ' -or
    $provenanceGuide -notmatch '56ffdc6ba76d62f976db05045323876276e2cbbceaee4610beb10ffe90e8cb94') {
    throw 'Provenance guide lacks the pinned offline SPDX and payload verification procedure.'
}
if ($provenanceGuide -notmatch 'the five versioned release assets' -or
    $provenanceGuide -notmatch 'only the six release files') {
    throw 'Provenance guide does not state the current exact release asset count.'
}

$onlineVerificationContracts = @(
    'bigfnj/desktopPet/.github/workflows/release.yml',
    "'--signer-workflow', `$SignerWorkflow",
    "'--source-ref', `$SourceRef",
    "'--source-digest', `$ExpectedSourceDigest",
    'refs/tags/$Tag',
    'signing_certificate_thumbprint',
    'Get-AuthenticodeSignature',
    'TimeStamperCertificate'
)
foreach ($contract in $onlineVerificationContracts) {
    if (-not $provenanceGuide.Contains($contract)) {
        throw "Provenance guide lacks fail-closed online verification contract: $contract"
    }
}
$authenticatedProvenanceIndex = $provenanceGuide.IndexOf(
    'Assert-GitHubAttestation -ArtifactPath $ProvenancePath',
    [StringComparison]::Ordinal)
$trustedThumbprintIndex = $provenanceGuide.IndexOf(
    '$ExpectedSignerThumbprint =',
    [StringComparison]::Ordinal)
$authenticodeIndex = $provenanceGuide.IndexOf(
    'function Assert-AuthenticodeSigner',
    [StringComparison]::Ordinal)
if ($authenticatedProvenanceIndex -lt 0 -or
    $trustedThumbprintIndex -le $authenticatedProvenanceIndex -or
    $authenticodeIndex -le $trustedThumbprintIndex) {
    throw (
        'Public verification must authenticate provenance before reading its ' +
        'thumbprint and must pin Authenticode afterward.')
}

foreach ($contract in @(
        'WINDOWS_PREVIOUS_SIGNING_CERTIFICATE_THUMBPRINTS',
        'verify_n_minus_one',
        'seal_release',
        'gh workflow run release.yml --repo bigfnj/desktopPet --ref $Tag',
        '--signer-workflow',
        'refs/tags/vX.Y.Z',
        'pristine signed assets')) {
    if (-not $releaseChecklist.Contains($contract)) {
        throw "Release checklist lacks signer/N-1/sealing contract: $contract"
    }
}
if ($releaseChecklist.Contains(
        'Dispatch the release workflow from the protected default branch')) {
    throw 'Release checklist still instructs branch-scoped attestation dispatch.'
}

$documentedAssetBlock = [regex]::Match(
    $provenanceGuide,
    '(?ms)^\$ExpectedAssets\s*=\s*@\(\s*(?<assets>.*?)^\)\s*\|\s*Sort-Object\s*$')
if (-not $documentedAssetBlock.Success) {
    throw 'Provenance guide does not expose a parseable offline release asset set.'
}
$publicationAssetBlock = [regex]::Match(
    $releaseWorkflow,
    '(?ms)^\s*assets=\(\s*(?<assets>.*?)^\s*\)\s*$')
if (-not $publicationAssetBlock.Success) {
    throw 'Release workflow does not expose a parseable publication asset set.'
}

function Get-NormalizedReleaseAssets {
    param(
        [Parameter(Mandatory = $true)][string]$Block,
        [Parameter(Mandatory = $true)][ValidateSet('documentation', 'workflow')]
        [string]$Source
    )

    $assets = @(
        foreach ($match in [regex]::Matches(
                $Block,
                '(?m)^\s*"(?<asset>[^"]+)"\s*$')) {
            $asset = [string]$match.Groups['asset'].Value
            if ($Source -ceq 'documentation') {
                $asset = $asset.Replace(
                    '$ReleaseBase',
                    'DesktopPet-AI-Edition-{VERSION}-Windows-x64')
                $asset = $asset.Replace('$Version', '{VERSION}')
            }
            else {
                if ($asset.StartsWith(
                        'release-assets/',
                        [StringComparison]::Ordinal)) {
                    $asset = $asset.Substring('release-assets/'.Length)
                }
                $asset = $asset.Replace(
                    '$RELEASE_BASE',
                    'DesktopPet-AI-Edition-{VERSION}-Windows-x64')
                $asset = $asset.Replace('$PRODUCT_VERSION', '{VERSION}')
            }
            $asset
        }
    )
    if ($assets.Count -eq 0 -or
        @($assets | Group-Object | Where-Object Count -ne 1).Count -ne 0) {
        throw "$Source release asset set is empty or contains duplicates."
    }
    return @($assets | Sort-Object)
}

$documentedReleaseAssets = @(
    Get-NormalizedReleaseAssets `
        -Block $documentedAssetBlock.Groups['assets'].Value `
        -Source documentation
    'SHA256SUMS.txt'
) | Sort-Object
$publishedReleaseAssets = @(
    Get-NormalizedReleaseAssets `
        -Block $publicationAssetBlock.Groups['assets'].Value `
        -Source workflow
) | Sort-Object
$requiredUpgradeEvidence =
    'DesktopPet-AI-Edition-{VERSION}.upgrade-evidence.json'
if ($documentedReleaseAssets.Count -ne 6 -or
    $publishedReleaseAssets.Count -ne 6 -or
    $documentedReleaseAssets -cnotcontains $requiredUpgradeEvidence -or
    $publishedReleaseAssets -cnotcontains $requiredUpgradeEvidence) {
    throw (
        'Release documentation and publication must contain five versioned ' +
        'assets, including N-1 upgrade evidence, plus SHA256SUMS.txt.')
}
$releaseAssetDifference = @(
    Compare-Object $documentedReleaseAssets $publishedReleaseAssets)
if ($releaseAssetDifference.Count -ne 0) {
    $detail = @(
        $releaseAssetDifference |
            ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    ) -join '; '
    throw (
        'Offline provenance assets do not exactly match workflow publication: ' +
        $detail)
}

$offlineProcedureStart = $provenanceGuide.IndexOf(
    '### 1. Verify the complete release checksum set',
    [StringComparison]::Ordinal)
if ($offlineProcedureStart -lt 0) {
    throw 'Provenance guide lacks the offline verification command sequence.'
}
$offlineProcedure = $provenanceGuide.Substring($offlineProcedureStart)
if ($offlineProcedure -match '(?i)Invoke-WebRequest|gh\s+attestation|https?://') {
    throw 'The offline SBOM command sequence still requires a network operation.'
}
if ($offlineProcedure -notmatch (
        'Get-ChildItem -LiteralPath \$ReleaseDir -Force -Recurse') -or
    $offlineProcedure -notmatch (
        'if \(\$_\.PSIsContainer\) \{ "\$Relative/" \} else \{ \$Relative \}') -or
    $offlineProcedure -notmatch (
        'Where-Object \{ \$_ -cne ''SHA256SUMS\.txt'' \}')) {
    throw (
        'Offline checksum verification does not recursively enumerate files ' +
        'and directories, so a nested extra could evade the exact asset set.')
}

Write-Host (
    'PASS: documentation authority, sole Sandcastle generator, docs-archive ' +
    'Jekyll quarantine, Ms-PL/jQuery inventory, published-page script safety, ' +
    'exact release assets, HTTPS pet-link/privacy and rights boundaries, ' +
    'build/support/filter semantics, offline SBOM procedure, Jekyll routes, ' +
    'and exact-case local Markdown links.')
