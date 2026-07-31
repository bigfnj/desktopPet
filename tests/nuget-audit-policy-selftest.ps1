#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$policyPath = Join-Path $repoRoot 'packaging\NuGetAuditPolicy.ps1'
. $policyPath

$expectedProject = Join-Path $repoRoot 'src\DesktopPet_Portable.csproj'
$negativeControlCount = 0

function ConvertTo-FixtureJson {
    param([Parameter(Mandatory = $true)][object]$Value)

    return ConvertTo-Json -InputObject $Value -Depth 10 -Compress
}

function Copy-Fixture {
    param([Parameter(Mandatory = $true)][object]$Value)

    return ConvertFrom-Json -InputObject (ConvertTo-FixtureJson -Value $Value)
}

function New-CleanFixture {
    return [pscustomobject][ordered]@{
        version = 1
        parameters = '--vulnerable --include-transitive'
        sources = @('https://api.nuget.org/v3/index.json')
        projects = @(
            [pscustomobject][ordered]@{
                path = $expectedProject.Replace('\', '/')
                frameworks = @(
                    [pscustomobject][ordered]@{
                        framework = 'net48'
                        topLevelPackages = @()
                        transitivePackages = @()
                    }
                )
            }
        )
    }
}

function New-RawCleanFixture {
    $fixture = New-CleanFixture
    [void]$fixture.projects[0].PSObject.Properties.Remove('frameworks')
    return $fixture
}

function New-InventoryFixture {
    return [pscustomobject][ordered]@{
        version = 1
        parameters = '--include-transitive'
        sources = @('https://api.nuget.org/v3/index.json')
        projects = @(
            [pscustomobject][ordered]@{
                path = $expectedProject.Replace('\', '/')
                frameworks = @(
                    [pscustomobject][ordered]@{
                        framework = 'net48'
                        topLevelPackages = @(
                            [pscustomobject][ordered]@{
                                id = 'Top.Level'
                                requestedVersion = '1.0.0'
                                resolvedVersion = '1.0.0'
                            }
                        )
                        transitivePackages = @()
                    }
                )
            }
        )
    }
}

function New-VulnerableFixture {
    return [pscustomobject][ordered]@{
        version = 1
        parameters = '--vulnerable --include-transitive'
        projects = @(
            [pscustomobject][ordered]@{
                path = $expectedProject.Replace('\', '/')
                frameworks = @(
                    [pscustomobject][ordered]@{
                        framework = 'net48'
                        topLevelPackages = @(
                            [pscustomobject][ordered]@{
                                id = 'Top.Level'
                                requestedVersion = '1.0.0'
                                resolvedVersion = '1.0.1'
                                autoReferenced = $false
                                vulnerabilities = @(
                                    [pscustomobject][ordered]@{
                                        severity = 'High'
                                        advisoryurl =
                                            'https://example.invalid/advisories/top'
                                    }
                                )
                            }
                        )
                        transitivePackages = @(
                            [pscustomobject][ordered]@{
                                id = 'Transitive.Package'
                                resolvedVersion = '2.0.0'
                                vulnerabilities = @(
                                    [pscustomobject][ordered]@{
                                        severity = 'Moderate'
                                        advisoryurl =
                                            'https://example.invalid/advisories/transitive'
                                    }
                                )
                            }
                        )
                    }
                )
            }
        )
    }
}

function Invoke-Fixture {
    param([Parameter(Mandatory = $true)][object]$Fixture)

    return @(
        Read-NuGetVulnerabilityAudit `
            -Json (ConvertTo-FixtureJson -Value $Fixture) `
            -ExpectedProjectPath $expectedProject `
            -ExpectedFramework 'net48'
    )
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $accepted = $true
    $message = ''
    try {
        & $Action
    }
    catch {
        $accepted = $false
        $message = $_.Exception.Message
    }
    if ($accepted) {
        throw "NuGet audit parser negative control was accepted: $Name"
    }
    if ($message -notmatch $ExpectedMessage) {
        throw (
            "NuGet audit parser negative control '$Name' failed for an " +
            "unexpected reason: $message"
        )
    }
    $script:negativeControlCount++
}

$cleanFindings = @(Invoke-Fixture -Fixture (New-CleanFixture))
if ($cleanFindings.Count -ne 0) {
    throw 'A normalized clean version-1 audit produced findings.'
}

$emptyFrameworks = New-CleanFixture
$emptyFrameworks.projects[0].frameworks = @()
Assert-Rejected 'empty-frameworks' {
    [void](Invoke-Fixture -Fixture $emptyFrameworks)
} 'exactly one expected framework .* found 0'

$missingFrameworks = New-RawCleanFixture
Assert-Rejected 'missing-frameworks' {
    [void](Invoke-Fixture -Fixture $missingFrameworks)
} "project lacks required 'frameworks'"

$completedJson = Complete-NuGetVulnerabilityAuditJson `
    -VulnerabilityJson (
        ConvertTo-FixtureJson -Value (New-RawCleanFixture)) `
    -InventoryJson (
        ConvertTo-FixtureJson -Value (New-InventoryFixture)) `
    -ExpectedProjectPath $expectedProject `
    -ExpectedFramework 'net48'
$completedFindings = @(
    Read-NuGetVulnerabilityAudit `
        -Json $completedJson `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48'
)
if ($completedFindings.Count -ne 0) {
    throw 'Completed documented clean audit produced vulnerability findings.'
}
$inventoryWithoutTransitives = New-InventoryFixture
[void]$inventoryWithoutTransitives.projects[0].frameworks[0].
    PSObject.Properties.Remove('transitivePackages')
$completedWithoutTransitives = Complete-NuGetVulnerabilityAuditJson `
    -VulnerabilityJson (
        ConvertTo-FixtureJson -Value (New-RawCleanFixture)) `
    -InventoryJson (
        ConvertTo-FixtureJson -Value $inventoryWithoutTransitives) `
    -ExpectedProjectPath $expectedProject `
    -ExpectedFramework 'net48'
if (@(Read-NuGetVulnerabilityAudit `
        -Json $completedWithoutTransitives `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48').Count -ne 0) {
    throw (
        'A non-empty inventory with no transitive collection did not ' +
        'complete the documented clean audit.')
}

$vulnerableFindings = @(
    Invoke-Fixture -Fixture (New-VulnerableFixture)
)
if ($vulnerableFindings.Count -ne 2 -or
    @($vulnerableFindings | Where-Object {
            $_.id -ceq 'Top.Level' -and
            $_.resolvedVersion -ceq '1.0.1' -and
            $_.framework -ceq 'net48'
        }).Count -ne 1 -or
    @($vulnerableFindings | Where-Object {
            $_.id -ceq 'Transitive.Package' -and
            $_.resolvedVersion -ceq '2.0.0' -and
            $_.framework -ceq 'net48'
        }).Count -ne 1) {
    throw 'The valid vulnerable fixture did not return both package findings.'
}

$normalizedPath = New-CleanFixture
$normalizedPath.projects[0].path =
    ([IO.Path]::GetFullPath($expectedProject)).ToUpperInvariant().
        Replace('\', '/')
[void](Invoke-Fixture -Fixture $normalizedPath)

Assert-Rejected 'invalid-json' {
    [void](Read-NuGetVulnerabilityAudit `
        -Json '{not-json' `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48')
} 'emitted invalid JSON'

Assert-Rejected 'array-root' {
    [void](Read-NuGetVulnerabilityAudit `
        -Json '[1]' `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48')
} 'document root must be a JSON object'

$fixture = New-CleanFixture
[void]$fixture.PSObject.Properties.Remove('version')
Assert-Rejected 'missing-version' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'version'"

$fixture = New-CleanFixture
$fixture.version = '1'
Assert-Rejected 'string-version' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'output version must be the integer 1'

$fixture = New-CleanFixture
$fixture.version = 2
Assert-Rejected 'wrong-version' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'output version must be the integer 1'

$fixture = New-CleanFixture
[void]$fixture.PSObject.Properties.Remove('projects')
Assert-Rejected 'missing-projects' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'projects'"

$fixture = New-CleanFixture
$fixture.projects = [pscustomobject]@{ path = $expectedProject }
Assert-Rejected 'scalar-projects' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'projects' must be a JSON array"

$fixture = New-CleanFixture
$fixture.projects = @()
Assert-Rejected 'empty-projects' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'exactly one project; found 0'

$fixture = New-CleanFixture
$fixture.projects = @(
    [pscustomobject]@{ path = $expectedProject },
    [pscustomobject]@{ path = $expectedProject }
)
Assert-Rejected 'multiple-projects' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'exactly one project; found 2'

$fixture = New-CleanFixture
$fixture.projects = @('not-an-object')
Assert-Rejected 'scalar-project-node' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'project must be a JSON object'

$fixture = New-CleanFixture
[void]$fixture.projects[0].PSObject.Properties.Remove('path')
Assert-Rejected 'missing-project-path' {
    [void](Invoke-Fixture -Fixture $fixture)
} "project lacks required 'path'"

$fixture = New-CleanFixture
$fixture.projects[0].path = Join-Path $repoRoot 'Tools\PetTester\PetTester.csproj'
Assert-Rejected 'wrong-project-path' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'project path does not match the requested project'

$fixture = New-CleanFixture
$fixture.projects[0].frameworks = $null
Assert-Rejected 'null-frameworks' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'frameworks' must be a JSON array"

$fixture = New-CleanFixture
$fixture.projects[0].frameworks =
    [pscustomobject]@{ framework = 'net48' }
Assert-Rejected 'scalar-frameworks' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'frameworks' must be a JSON array"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks = @('net48')
Assert-Rejected 'scalar-framework-node' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'framework\[1\] must be a JSON object'

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].PSObject.Properties.Remove('framework')
Assert-Rejected 'missing-framework-name' {
    [void](Invoke-Fixture -Fixture $fixture)
} "framework\[1\] lacks required 'framework'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].framework = ''
Assert-Rejected 'empty-framework-name' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'framework' must be a non-empty string"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].framework = 'net8.0'
Assert-Rejected 'wrong-framework-name' {
    [void](Invoke-Fixture -Fixture $fixture)
} "expected framework 'net48'.*found 'net8.0'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks += Copy-Fixture `
    -Value $fixture.projects[0].frameworks[0]
Assert-Rejected 'duplicate-framework-node' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'exactly one expected framework .* found 2'

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].PSObject.Properties.Remove(
    'topLevelPackages')
[void]$fixture.projects[0].frameworks[0].PSObject.Properties.Remove(
    'transitivePackages')
$zeroPackageFindings = @(Invoke-Fixture -Fixture $fixture)
if ($zeroPackageFindings.Count -ne 0) {
    throw 'A complete expected framework with zero vulnerable nodes was not clean.'
}

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages =
    [pscustomobject]@{ id = 'scalar' }
Assert-Rejected 'scalar-package-array' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'topLevelPackages' must be a JSON array"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages = @('not-an-object')
Assert-Rejected 'scalar-package-node' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'topLevelPackages\[1\] must be a JSON object'

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].topLevelPackages[0].
    PSObject.Properties.Remove('id')
Assert-Rejected 'missing-package-id' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'id'"

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].topLevelPackages[0].
    PSObject.Properties.Remove('requestedVersion')
Assert-Rejected 'missing-requested-version' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'requestedVersion'"

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].transitivePackages[0].
    PSObject.Properties.Remove('resolvedVersion')
Assert-Rejected 'missing-resolved-version' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'resolvedVersion'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].autoReferenced = 'false'
Assert-Rejected 'string-auto-referenced' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'autoReferenced' must be a JSON Boolean"

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].topLevelPackages[0].
    PSObject.Properties.Remove('vulnerabilities')
Assert-Rejected 'missing-vulnerabilities' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'vulnerabilities'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities = @()
Assert-Rejected 'empty-vulnerabilities' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'must contain at least one vulnerability'

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities =
    [pscustomobject]@{
        severity = 'High'
        advisoryurl = 'https://example.invalid/advisory'
    }
Assert-Rejected 'scalar-vulnerabilities' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'vulnerabilities' must be a JSON array"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].vulnerabilities =
    @('not-an-object')
Assert-Rejected 'scalar-vulnerability-node' {
    [void](Invoke-Fixture -Fixture $fixture)
} 'vulnerability\[1\] must be a JSON object'

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].topLevelPackages[0].
    vulnerabilities[0].PSObject.Properties.Remove('severity')
Assert-Rejected 'missing-severity' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'severity'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].
    vulnerabilities[0].severity = 'Unknown'
Assert-Rejected 'unknown-severity' {
    [void](Invoke-Fixture -Fixture $fixture)
} "unknown severity 'Unknown'"

$fixture = New-VulnerableFixture
[void]$fixture.projects[0].frameworks[0].topLevelPackages[0].
    vulnerabilities[0].PSObject.Properties.Remove('advisoryurl')
Assert-Rejected 'missing-advisory-url' {
    [void](Invoke-Fixture -Fixture $fixture)
} "lacks required 'advisoryurl'"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].
    vulnerabilities[0].advisoryurl = 'relative/advisory'
Assert-Rejected 'relative-advisory-url' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'advisoryurl' must be an absolute HTTPS URI"

$fixture = New-VulnerableFixture
$fixture.projects[0].frameworks[0].topLevelPackages[0].
    vulnerabilities[0].advisoryurl = 'file:///C:/untrusted-advisory.txt'
Assert-Rejected 'non-https-advisory-url' {
    [void](Invoke-Fixture -Fixture $fixture)
} "'advisoryurl' must be an absolute HTTPS URI"

$inventoryFixture = New-InventoryFixture
$inventoryFixture.projects[0].frameworks[0].topLevelPackages = @()
Assert-Rejected 'completion-empty-inventory' {
    [void](Complete-NuGetVulnerabilityAuditJson `
        -VulnerabilityJson (
            ConvertTo-FixtureJson -Value (New-RawCleanFixture)) `
        -InventoryJson (ConvertTo-FixtureJson -Value $inventoryFixture) `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48')
} 'full package inventory contains no package nodes'

$inventoryFixture = New-InventoryFixture
$inventoryFixture.projects[0].frameworks[0].framework = 'net8.0'
Assert-Rejected 'completion-wrong-inventory-framework' {
    [void](Complete-NuGetVulnerabilityAuditJson `
        -VulnerabilityJson (
            ConvertTo-FixtureJson -Value (New-RawCleanFixture)) `
        -InventoryJson (ConvertTo-FixtureJson -Value $inventoryFixture) `
        -ExpectedProjectPath $expectedProject `
        -ExpectedFramework 'net48')
} "full package inventory framework does not match.*'net8.0'"

if ($negativeControlCount -ne 38) {
    throw (
        'NuGet audit parser self-test control count changed: ' +
        "$negativeControlCount."
    )
}

Write-Host (
    'PASS: NuGet audit parser accepted normalized clean, independently ' +
    'completed clean, and vulnerable version-1 baselines and rejected 38 ' +
    'malformed, incomplete, or mismatched fixtures.'
) -ForegroundColor Green
