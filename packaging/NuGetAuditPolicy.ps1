#requires -Version 5

Set-StrictMode -Version Latest

function Get-NuGetAuditProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context,
        [switch]$Optional
    )

    $matches = @(
        $Object.PSObject.Properties |
            Where-Object { $_.Name -ceq $Name }
    )
    if ($matches.Count -eq 0) {
        if ($Optional) {
            return $null
        }
        throw "NuGet audit $Context lacks required '$Name'."
    }
    if ($matches.Count -ne 1) {
        throw "NuGet audit $Context contains duplicate '$Name' properties."
    }
    return $matches[0]
}

function Assert-NuGetAuditObject {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($null -eq $Value -or
        $Value -isnot [Management.Automation.PSCustomObject]) {
        throw "NuGet audit $Context must be a JSON object."
    }
}

function Get-NuGetAuditRequiredString {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $property = Get-NuGetAuditProperty `
        -Object $Object `
        -Name $Name `
        -Context $Context
    if ($property.Value -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "NuGet audit $Context '$Name' must be a non-empty string."
    }
    return [string]$property.Value
}

function Get-NuGetAuditArray {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context,
        [switch]$Optional
    )

    $property = Get-NuGetAuditProperty `
        -Object $Object `
        -Name $Name `
        -Context $Context `
        -Optional:$Optional
    if ($null -eq $property) {
        return $null
    }
    if ($property.Value -isnot [Array]) {
        throw "NuGet audit $Context '$Name' must be a JSON array."
    }
    return ,$property.Value
}

function Assert-NuGetAuditExactProperties {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actual = @($Object.PSObject.Properties | ForEach-Object Name)
    $difference = @(
        Compare-Object `
            -ReferenceObject @($Names | Sort-Object) `
            -DifferenceObject @($actual | Sort-Object) `
            -CaseSensitive
    )
    if ($difference.Count -gt 0 -or $actual.Count -ne $Names.Count) {
        throw (
            "NuGet audit $Context does not have the exact required " +
            "properties: $($Names -join ', ').")
    }
}

function Assert-NuGetAuditVersionOne {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Document,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $versionProperty = Get-NuGetAuditProperty `
        -Object $Document `
        -Name 'version' `
        -Context $Context
    $version = $versionProperty.Value
    $integralTypes = @(
        [byte], [sbyte], [int16], [uint16],
        [int32], [uint32], [int64], [uint64]
    )
    if ($null -eq $version -or
        $integralTypes -notcontains $version.GetType() -or
        [int64]$version -ne 1) {
        throw "NuGet audit $Context version must be the integer 1."
    }
}

function Assert-NuGetAuditExpectedProjectPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ObservedPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    try {
        $normalizedExpectedPath = [IO.Path]::GetFullPath($ExpectedPath)
        $normalizedObservedPath = [IO.Path]::GetFullPath($ObservedPath)
    }
    catch {
        throw "NuGet audit $Context contains an invalid project path."
    }
    if (-not $normalizedObservedPath.Equals(
            $normalizedExpectedPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            "NuGet audit $Context project path does not match the requested " +
            "project. Expected '$normalizedExpectedPath'; found " +
            "'$normalizedObservedPath'.")
    }
}

function Complete-NuGetVulnerabilityAuditJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$VulnerabilityJson,
        [Parameter(Mandatory = $true)][string]$InventoryJson,
        [Parameter(Mandatory = $true)][string]$ExpectedProjectPath,
        [Parameter(Mandatory = $true)][string]$ExpectedFramework
    )

    try {
        $vulnerabilityDocument =
            ConvertFrom-Json -InputObject $VulnerabilityJson
    }
    catch {
        throw (
            'NuGet vulnerability audit emitted invalid JSON: ' +
            $_.Exception.Message)
    }
    Assert-NuGetAuditObject `
        -Value $vulnerabilityDocument `
        -Context 'document root'
    $projects = Get-NuGetAuditArray `
        -Object $vulnerabilityDocument `
        -Name 'projects' `
        -Context 'document root'
    if (@($projects).Count -ne 1) {
        throw (
            'NuGet vulnerability audit must contain exactly one project; ' +
            "found $(@($projects).Count).")
    }
    $project = @($projects)[0]
    Assert-NuGetAuditObject -Value $project -Context 'project'
    $frameworksProperty = Get-NuGetAuditProperty `
        -Object $project `
        -Name 'frameworks' `
        -Context 'project' `
        -Optional
    if ($null -ne $frameworksProperty) {
        # Present-but-empty, malformed, wrong, and populated framework data
        # must be judged by the strict parser. Only the documented omission is
        # eligible for clean-output completion.
        return $VulnerabilityJson
    }

    Assert-NuGetAuditExactProperties `
        -Object $vulnerabilityDocument `
        -Names @('version', 'parameters', 'sources', 'projects') `
        -Context 'raw clean document root'
    Assert-NuGetAuditVersionOne `
        -Document $vulnerabilityDocument `
        -Context 'raw clean document root'
    $parameters = Get-NuGetAuditRequiredString `
        -Object $vulnerabilityDocument `
        -Name 'parameters' `
        -Context 'raw clean document root'
    if ($parameters -cne '--vulnerable --include-transitive') {
        throw (
            'NuGet raw clean vulnerability audit parameters are not the ' +
            "documented exact form: '$parameters'.")
    }
    $sources = Get-NuGetAuditArray `
        -Object $vulnerabilityDocument `
        -Name 'sources' `
        -Context 'raw clean document root'
    if (@($sources).Count -eq 0 -or
        @($sources | Where-Object {
            $_ -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$_)
        }).Count -gt 0) {
        throw (
            'NuGet raw clean vulnerability audit must contain non-empty ' +
            'string package sources.')
    }
    Assert-NuGetAuditExactProperties `
        -Object $project `
        -Names @('path') `
        -Context 'raw clean project'
    $rawProjectPath = Get-NuGetAuditRequiredString `
        -Object $project `
        -Name 'path' `
        -Context 'raw clean project'
    Assert-NuGetAuditExpectedProjectPath `
        -ObservedPath $rawProjectPath `
        -ExpectedPath $ExpectedProjectPath `
        -Context 'raw clean project'

    try {
        $inventoryDocument = ConvertFrom-Json -InputObject $InventoryJson
    }
    catch {
        throw (
            'NuGet full package inventory emitted invalid JSON: ' +
            $_.Exception.Message)
    }
    Assert-NuGetAuditObject `
        -Value $inventoryDocument `
        -Context 'full inventory document root'
    Assert-NuGetAuditVersionOne `
        -Document $inventoryDocument `
        -Context 'full inventory document root'
    $inventoryProjects = Get-NuGetAuditArray `
        -Object $inventoryDocument `
        -Name 'projects' `
        -Context 'full inventory document root'
    if (@($inventoryProjects).Count -ne 1) {
        throw (
            'NuGet full package inventory must contain exactly one project; ' +
            "found $(@($inventoryProjects).Count).")
    }
    $inventoryProject = @($inventoryProjects)[0]
    Assert-NuGetAuditObject `
        -Value $inventoryProject `
        -Context 'full inventory project'
    $inventoryProjectPath = Get-NuGetAuditRequiredString `
        -Object $inventoryProject `
        -Name 'path' `
        -Context 'full inventory project'
    Assert-NuGetAuditExpectedProjectPath `
        -ObservedPath $inventoryProjectPath `
        -ExpectedPath $ExpectedProjectPath `
        -Context 'full inventory project'
    $inventoryFrameworks = Get-NuGetAuditArray `
        -Object $inventoryProject `
        -Name 'frameworks' `
        -Context 'full inventory project'
    if (@($inventoryFrameworks).Count -ne 1) {
        throw (
            'NuGet full package inventory must contain exactly one expected ' +
            "framework '$ExpectedFramework'; found " +
            "$(@($inventoryFrameworks).Count).")
    }
    $inventoryFramework = @($inventoryFrameworks)[0]
    Assert-NuGetAuditObject `
        -Value $inventoryFramework `
        -Context 'full inventory framework[1]'
    $inventoryFrameworkName = Get-NuGetAuditRequiredString `
        -Object $inventoryFramework `
        -Name 'framework' `
        -Context 'full inventory framework[1]'
    if ($inventoryFrameworkName -cne $ExpectedFramework) {
        throw (
            'NuGet full package inventory framework does not match the ' +
            "expected framework '$ExpectedFramework'; found " +
            "'$inventoryFrameworkName'.")
    }

    $inventoryPackageCount = 0
    foreach ($collectionName in @(
            'topLevelPackages',
            'transitivePackages')) {
        $packages = Get-NuGetAuditArray `
            -Object $inventoryFramework `
            -Name $collectionName `
            -Context 'full inventory framework[1]' `
            -Optional
        if ($null -eq $packages) {
            continue
        }
        foreach ($package in @($packages)) {
            $inventoryPackageCount++
            $packageContext = (
                "full inventory framework[1] $collectionName" +
                "[$inventoryPackageCount]")
            Assert-NuGetAuditObject `
                -Value $package `
                -Context $packageContext
            [void](Get-NuGetAuditRequiredString `
                -Object $package `
                -Name 'id' `
                -Context $packageContext)
            if ($collectionName -ceq 'topLevelPackages') {
                [void](Get-NuGetAuditRequiredString `
                    -Object $package `
                    -Name 'requestedVersion' `
                    -Context $packageContext)
            }
            [void](Get-NuGetAuditRequiredString `
                -Object $package `
                -Name 'resolvedVersion' `
                -Context $packageContext)
        }
    }
    if ($inventoryPackageCount -eq 0) {
        throw (
            'NuGet full package inventory contains no package nodes for the ' +
            "expected framework '$ExpectedFramework'.")
    }

    $project | Add-Member `
        -NotePropertyName frameworks `
        -NotePropertyValue @(
            [pscustomobject][ordered]@{
                framework = $ExpectedFramework
                topLevelPackages = @()
                transitivePackages = @()
            })
    return ConvertTo-Json `
        -InputObject $vulnerabilityDocument `
        -Depth 20 `
        -Compress
}

function Read-NuGetVulnerabilityAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$ExpectedProjectPath,
        [Parameter(Mandatory = $true)][string]$ExpectedFramework
    )

    try {
        $document = ConvertFrom-Json -InputObject $Json
    }
    catch {
        throw "NuGet vulnerability audit emitted invalid JSON: $($_.Exception.Message)"
    }
    Assert-NuGetAuditObject -Value $document -Context 'document root'

    $versionProperty = Get-NuGetAuditProperty `
        -Object $document `
        -Name 'version' `
        -Context 'document root'
    $version = $versionProperty.Value
    $integralTypes = @(
        [byte], [sbyte], [int16], [uint16],
        [int32], [uint32], [int64], [uint64]
    )
    if ($null -eq $version -or
        $integralTypes -notcontains $version.GetType() -or
        [int64]$version -ne 1) {
        throw "NuGet vulnerability audit output version must be the integer 1."
    }

    $projects = Get-NuGetAuditArray `
        -Object $document `
        -Name 'projects' `
        -Context 'document root'
    if (@($projects).Count -ne 1) {
        throw (
            'NuGet vulnerability audit must contain exactly one project; ' +
            "found $(@($projects).Count)."
        )
    }

    $project = @($projects)[0]
    Assert-NuGetAuditObject -Value $project -Context 'project'
    $projectPath = Get-NuGetAuditRequiredString `
        -Object $project `
        -Name 'path' `
        -Context 'project'
    try {
        $normalizedExpectedPath =
            [IO.Path]::GetFullPath($ExpectedProjectPath)
        $normalizedProjectPath =
            [IO.Path]::GetFullPath($projectPath)
    }
    catch {
        throw "NuGet vulnerability audit contains an invalid project path."
    }
    if (-not $normalizedProjectPath.Equals(
            $normalizedExpectedPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'NuGet vulnerability audit project path does not match the ' +
            "requested project. Expected '$normalizedExpectedPath'; found " +
            "'$normalizedProjectPath'."
        )
    }

    $frameworks = Get-NuGetAuditArray `
        -Object $project `
        -Name 'frameworks' `
        -Context 'project'
    if (@($frameworks).Count -ne 1) {
        throw (
            'NuGet vulnerability audit must contain exactly one expected ' +
            "framework '$ExpectedFramework'; found $(@($frameworks).Count).")
    }

    $findings = New-Object 'Collections.Generic.List[object]'
    $frameworkIndex = 0
    foreach ($framework in @($frameworks)) {
        $frameworkIndex++
        $frameworkContext = "framework[$frameworkIndex]"
        Assert-NuGetAuditObject `
            -Value $framework `
            -Context $frameworkContext
        $frameworkName = Get-NuGetAuditRequiredString `
            -Object $framework `
            -Name 'framework' `
            -Context $frameworkContext
        if ($frameworkName -cne $ExpectedFramework) {
            throw (
                'NuGet vulnerability audit framework does not match the ' +
                "expected framework '$ExpectedFramework'; found " +
                "'$frameworkName'.")
        }

        foreach ($collectionName in @(
                'topLevelPackages',
                'transitivePackages'
            )) {
            $packages = Get-NuGetAuditArray `
                -Object $framework `
                -Name $collectionName `
                -Context $frameworkContext `
                -Optional
            if ($null -eq $packages) {
                continue
            }

            $packageIndex = 0
            foreach ($package in @($packages)) {
                $packageIndex++
                $packageContext = (
                    "$frameworkContext $collectionName[$packageIndex]"
                )
                Assert-NuGetAuditObject `
                    -Value $package `
                    -Context $packageContext
                $id = Get-NuGetAuditRequiredString `
                    -Object $package `
                    -Name 'id' `
                    -Context $packageContext
                if ($collectionName -ceq 'topLevelPackages') {
                    [void](Get-NuGetAuditRequiredString `
                        -Object $package `
                        -Name 'requestedVersion' `
                        -Context $packageContext)
                }
                $resolvedVersion = Get-NuGetAuditRequiredString `
                    -Object $package `
                    -Name 'resolvedVersion' `
                    -Context $packageContext

                $autoReferenced = Get-NuGetAuditProperty `
                    -Object $package `
                    -Name 'autoReferenced' `
                    -Context $packageContext `
                    -Optional
                if ($null -ne $autoReferenced -and
                    $autoReferenced.Value -isnot [bool]) {
                    throw (
                        "NuGet audit $packageContext 'autoReferenced' must " +
                        'be a JSON Boolean.'
                    )
                }

                $vulnerabilities = Get-NuGetAuditArray `
                    -Object $package `
                    -Name 'vulnerabilities' `
                    -Context $packageContext
                if (@($vulnerabilities).Count -eq 0) {
                    throw (
                        "NuGet audit $packageContext 'vulnerabilities' " +
                        'must contain at least one vulnerability.'
                    )
                }

                $vulnerabilityIndex = 0
                foreach ($vulnerability in @($vulnerabilities)) {
                    $vulnerabilityIndex++
                    $vulnerabilityContext = (
                        "$packageContext vulnerability[$vulnerabilityIndex]"
                    )
                    Assert-NuGetAuditObject `
                        -Value $vulnerability `
                        -Context $vulnerabilityContext
                    $severity = Get-NuGetAuditRequiredString `
                        -Object $vulnerability `
                        -Name 'severity' `
                        -Context $vulnerabilityContext
                    if ($severity -cnotin @(
                            'Low',
                            'Moderate',
                            'High',
                            'Critical'
                        )) {
                        throw (
                            "NuGet audit $vulnerabilityContext has unknown " +
                            "severity '$severity'."
                        )
                    }
                    $advisoryUrl = Get-NuGetAuditRequiredString `
                        -Object $vulnerability `
                        -Name 'advisoryurl' `
                        -Context $vulnerabilityContext
                    $parsedAdvisory = $null
                    if (-not [Uri]::TryCreate(
                            $advisoryUrl,
                            [UriKind]::Absolute,
                            [ref]$parsedAdvisory) -or
                        $parsedAdvisory.Scheme -cne 'https' -or
                        [string]::IsNullOrWhiteSpace(
                            [string]$parsedAdvisory.DnsSafeHost)) {
                        throw (
                            "NuGet audit $vulnerabilityContext 'advisoryurl' " +
                            'must be an absolute HTTPS URI with a host.'
                        )
                    }
                }

                $findings.Add([pscustomobject]@{
                    id = $id
                    resolvedVersion = $resolvedVersion
                    framework = $frameworkName
                })
            }
        }
    }

    return $findings.ToArray()
}
