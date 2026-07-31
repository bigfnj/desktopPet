#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

function Release-ComObject {
    param($InputObject)

    if ($null -ne $InputObject -and
        [Runtime.InteropServices.Marshal]::IsComObject($InputObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $InputObject)
    }
}

function Get-MsiTableRows {
    param(
        [Parameter(Mandatory = $true)]$Database,
        [Parameter(Mandatory = $true)][string]$Query,
        [Parameter(Mandatory = $true)][string[]]$ColumnNames
    )

    $view = $null
    $record = $null
    $rows = @()
    try {
        $view = $Database.OpenView($Query)
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            $values = [ordered]@{}
            for ($index = 0; $index -lt $ColumnNames.Count; $index++) {
                $values[$ColumnNames[$index]] =
                    [string]$record.StringData($index + 1)
            }
            $rows += [pscustomobject]$values
            Release-ComObject $record
            $record = $null
        }
        return $rows
    }
    finally {
        Release-ComObject $record
        Release-ComObject $view
    }
}

function Get-MsiTargetFileName {
    param(
        [Parameter(Mandatory = $true)][string]$EncodedName,
        [Parameter(Mandatory = $true)][string]$FileId
    )

    if ([string]::IsNullOrWhiteSpace($EncodedName)) {
        throw "MSI File row '$FileId' has an empty FileName."
    }

    $nameParts = @($EncodedName.Split([char]'|'))
    if ($nameParts.Count -gt 2 -or
        ($nameParts.Count -eq 2 -and
            [string]::IsNullOrWhiteSpace($nameParts[0]))) {
        throw (
            "MSI File row '$FileId' has malformed short|long FileName " +
            "data: '$EncodedName'.")
    }
    $targetName = $nameParts[$nameParts.Count - 1]
    if (-not (Test-DesktopPetWindowsLeafName -Name $targetName)) {
        throw (
            "MSI File row '$FileId' has an unsafe target FileName: " +
            "'$EncodedName'.")
    }
    return $targetName
}

function Get-MsiTargetDirectoryName {
    param(
        [Parameter(Mandatory = $true)][string]$EncodedName,
        [Parameter(Mandatory = $true)][string]$DirectoryId
    )

    if ([string]::IsNullOrWhiteSpace($EncodedName)) {
        throw "MSI Directory row '$DirectoryId' has an empty DefaultDir."
    }

    $sourceParts = @($EncodedName.Split([char]':'))
    if ($sourceParts.Count -gt 2 -or
        [string]::IsNullOrWhiteSpace($sourceParts[0])) {
        throw (
            "MSI Directory row '$DirectoryId' has malformed target:source " +
            "DefaultDir data: '$EncodedName'.")
    }
    $nameParts = @($sourceParts[0].Split([char]'|'))
    if ($nameParts.Count -gt 2 -or
        @($nameParts | Where-Object {
            [string]::IsNullOrWhiteSpace([string]$_)
        }).Count -gt 0) {
        throw (
            "MSI Directory row '$DirectoryId' has malformed short|long " +
            "target DefaultDir data: '$EncodedName'.")
    }
    foreach ($namePart in $nameParts) {
        if (-not (Test-DesktopPetWindowsLeafName -Name $namePart)) {
            throw (
                "MSI Directory row '$DirectoryId' has an unsafe target " +
                "DefaultDir: '$EncodedName'.")
        }
    }
    return $nameParts[$nameParts.Count - 1]
}

if ($env:OS -cne 'Windows_NT') {
    throw 'MSI payload-table verification requires Windows Installer.'
}
if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
    throw "MSI not found: $MsiPath"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Runtime payload manifest not found: $ManifestPath"
}

$absoluteMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$absoluteManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
if ([IO.Path]::GetExtension($absoluteMsi) -cne '.msi') {
    throw "Payload-table verification accepts only an .msi file: $absoluteMsi"
}

$expectedNames = @(
    Get-Content -LiteralPath $absoluteManifest |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
if ($expectedNames.Count -eq 0) {
    throw 'Runtime payload manifest is empty.'
}
$manifestNames = @{}
foreach ($name in $expectedNames) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name)) {
        throw "Runtime payload manifest entry is not a plain file name: '$name'"
    }
    if ($manifestNames.ContainsKey($name)) {
        throw (
            "Runtime payload manifest contains a duplicate or case-colliding " +
            "entry: '$name'")
    }
    $manifestNames[$name] = $name
}

$installer = $null
$database = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    # 0 is msiOpenDatabaseModeReadOnly.
    $database = $installer.OpenDatabase($absoluteMsi, 0)
    $fileRows = @(
        Get-MsiTableRows `
            -Database $database `
            -Query (
                'SELECT `File`, `Component_`, `FileName` FROM `File`') `
            -ColumnNames @('FileId', 'ComponentId', 'EncodedName')
    )
    $componentRows = @(
        Get-MsiTableRows `
            -Database $database `
            -Query (
                'SELECT `Component`, `Directory_` FROM `Component`') `
            -ColumnNames @('ComponentId', 'DirectoryId')
    )
    $directoryRows = @(
        Get-MsiTableRows `
            -Database $database `
            -Query (
                'SELECT `Directory`, `Directory_Parent`, `DefaultDir` ' +
                'FROM `Directory`') `
            -ColumnNames @('DirectoryId', 'ParentId', 'DefaultDir')
    )
    $productNameRows = @(
        Get-MsiTableRows `
            -Database $database `
            -Query (
                "SELECT ``Value`` FROM ``Property`` " +
                "WHERE ``Property``='ProductName'") `
            -ColumnNames @('Value')
    )
}
finally {
    Release-ComObject $database
    Release-ComObject $installer
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

if ($fileRows.Count -eq 0) {
    throw 'MSI File table is empty.'
}
if ($componentRows.Count -eq 0) {
    throw 'MSI Component table is empty.'
}
if ($directoryRows.Count -eq 0) {
    throw 'MSI Directory table is empty.'
}
if ($productNameRows.Count -ne 1 -or
    [string]::IsNullOrWhiteSpace([string]$productNameRows[0].Value)) {
    throw 'MSI Property table must contain exactly one non-empty ProductName.'
}
$productName = [string]$productNameRows[0].Value

$directories = @{}
foreach ($row in $directoryRows) {
    if ([string]::IsNullOrWhiteSpace($row.DirectoryId) -or
        [string]::IsNullOrWhiteSpace($row.DefaultDir)) {
        throw 'MSI Directory table contains an incomplete row.'
    }
    if ($directories.ContainsKey($row.DirectoryId)) {
        throw (
            "MSI Directory table contains a duplicate or case-colliding key: " +
            "'$($row.DirectoryId)'.")
    }
    $directories[$row.DirectoryId] = $row
}
foreach ($row in $directoryRows) {
    if (-not [string]::IsNullOrWhiteSpace($row.ParentId) -and
        -not $directories.ContainsKey($row.ParentId)) {
        throw (
            "MSI Directory '$($row.DirectoryId)' references missing parent " +
            "'$($row.ParentId)'.")
    }
}

# The application payload is permitted only under the declared per-user
# %LOCALAPPDATA%\Programs install directory. Checking the compiled Directory
# graph prevents an identically named directory identifier from being moved to
# another root while still satisfying Component.Directory_ comparisons.
$requiredDirectoryParents = [ordered]@{
    INSTALLFOLDER = 'ProgramsFolder'
    ProgramsFolder = 'LocalAppDataFolder'
    LocalAppDataFolder = 'TARGETDIR'
    TARGETDIR = ''
}
foreach ($entry in $requiredDirectoryParents.GetEnumerator()) {
    if (-not $directories.ContainsKey($entry.Key)) {
        throw "MSI Directory table is missing required directory '$($entry.Key)'."
    }
    $actualParent = [string]$directories[$entry.Key].ParentId
    if ($actualParent -cne [string]$entry.Value) {
        $displayParent = if ([string]::IsNullOrWhiteSpace($actualParent)) {
            '<root>'
        }
        else {
            $actualParent
        }
        $requiredParent = if (
            [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            '<root>'
        }
        else {
            [string]$entry.Value
        }
        throw (
            "MSI Directory '$($entry.Key)' has parent '$displayParent'; " +
            "required parent is '$requiredParent'.")
    }
}
$programsTargetName = Get-MsiTargetDirectoryName `
    -EncodedName ([string]$directories['ProgramsFolder'].DefaultDir) `
    -DirectoryId 'ProgramsFolder'
if ($programsTargetName -cne 'Programs') {
    throw (
        "MSI Directory 'ProgramsFolder' has target name " +
        "'$programsTargetName'; required target name is 'Programs'.")
}
$installTargetName = Get-MsiTargetDirectoryName `
    -EncodedName ([string]$directories['INSTALLFOLDER'].DefaultDir) `
    -DirectoryId 'INSTALLFOLDER'
if ($installTargetName -cne $productName) {
    throw (
        "MSI Directory 'INSTALLFOLDER' has target name " +
        "'$installTargetName'; required target name is ProductName " +
        "'$productName'.")
}

$components = @{}
foreach ($row in $componentRows) {
    if ([string]::IsNullOrWhiteSpace($row.ComponentId) -or
        [string]::IsNullOrWhiteSpace($row.DirectoryId)) {
        throw 'MSI Component table contains an incomplete row.'
    }
    if ($components.ContainsKey($row.ComponentId)) {
        throw (
            "MSI Component table contains a duplicate or case-colliding key: " +
            "'$($row.ComponentId)'.")
    }
    if (-not $directories.ContainsKey($row.DirectoryId)) {
        throw (
            "MSI Component '$($row.ComponentId)' references missing directory " +
            "'$($row.DirectoryId)'.")
    }
    $components[$row.ComponentId] = $row
}

$actualNames = New-Object Collections.Generic.List[string]
$actualNameOwners = @{}
$fileIds = @{}
foreach ($row in $fileRows) {
    if ([string]::IsNullOrWhiteSpace($row.FileId) -or
        [string]::IsNullOrWhiteSpace($row.ComponentId)) {
        throw 'MSI File table contains an incomplete row.'
    }
    if ($fileIds.ContainsKey($row.FileId)) {
        throw (
            "MSI File table contains a duplicate or case-colliding key: " +
            "'$($row.FileId)'.")
    }
    $fileIds[$row.FileId] = $true
    if (-not $components.ContainsKey($row.ComponentId)) {
        throw (
            "MSI File '$($row.FileId)' references missing component " +
            "'$($row.ComponentId)'.")
    }

    $targetName = Get-MsiTargetFileName `
        -EncodedName $row.EncodedName `
        -FileId $row.FileId
    $directoryId = [string]$components[$row.ComponentId].DirectoryId
    if ($directoryId -cne 'INSTALLFOLDER') {
        throw (
            "MSI File '$targetName' targets directory '$directoryId'; every " +
            "packaged file must target the permitted 'INSTALLFOLDER' directory.")
    }
    if ($actualNameOwners.ContainsKey($targetName)) {
        throw (
            "MSI File table contains duplicate or case-colliding target name " +
            "'$targetName' in rows '$($actualNameOwners[$targetName])' and " +
            "'$($row.FileId)'.")
    }
    $actualNameOwners[$targetName] = $row.FileId
    [void]$actualNames.Add($targetName)
}

$difference = @(
    Compare-Object `
        -ReferenceObject @($expectedNames | Sort-Object) `
        -DifferenceObject @($actualNames | Sort-Object) `
        -CaseSensitive
)
if ($difference.Count -gt 0) {
    $detail = (
        $difference |
            ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    ) -join '; '
    throw "MSI File table differs from the runtime manifest: $detail"
}

Write-Host (
    "MSI File/Component/Directory tables verified: $($expectedNames.Count) " +
    "manifest files target INSTALLFOLDER."
) -ForegroundColor Green
