#requires -Version 5
[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -cne 'Windows_NT') {
    throw 'Packaged-payload MSI table self-test requires Windows Installer.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $repoRoot 'dist\DesktopPet-AI-Edition.msi'
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'packaging\runtime-files.txt'
}
$tableVerifier = Join-Path $repoRoot 'packaging\Test-MsiPayloadTable.ps1'
$packagedVerifier = Join-Path $repoRoot 'packaging\Test-PackagedPayloads.ps1'
foreach ($requiredPath in @(
        $MsiPath,
        $ManifestPath,
        $tableVerifier,
        $packagedVerifier)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Packaged-payload table self-test input is missing: $requiredPath"
    }
}

$absoluteMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$absoluteManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$sourceMsiHash = (
    Get-FileHash -LiteralPath $absoluteMsi -Algorithm SHA256).Hash
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratchRoot = Join-Path $tempRoot (
    'DesktopPet-PayloadTable-' + [Guid]::NewGuid().ToString('N'))
$fixtureMsi = Join-Path $scratchRoot 'sibling-directory-fixture.msi'
$directoryNameFixtureMsi =
    Join-Path $scratchRoot 'install-directory-name-fixture.msi'
$placeholderZip = Join-Path $scratchRoot 'unread-placeholder.zip'
$placeholderReference = Join-Path $scratchRoot 'unread-reference'

function Release-ComObject {
    param($InputObject)

    if ($null -ne $InputObject -and
        [Runtime.InteropServices.Marshal]::IsComObject($InputObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $InputObject)
    }
}

function Add-MsiRecord {
    param(
        [Parameter(Mandatory = $true)]$Installer,
        [Parameter(Mandatory = $true)]$Database,
        [Parameter(Mandatory = $true)][string]$Query,
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object[]]$Values
    )

    $view = $null
    $record = $null
    try {
        $view = $Database.OpenView($Query)
        [void]$view.Execute()
        $record = $Installer.CreateRecord($Values.Count)
        for ($index = 0; $index -lt $Values.Count; $index++) {
            $value = $Values[$index]
            if ($null -eq $value) {
                continue
            }
            if ($value -is [int]) {
                $record.IntegerData($index + 1) = [int]$value
            }
            else {
                $record.StringData($index + 1) = [string]$value
            }
        }
        # 1 is msiViewModifyInsert.
        [void]$view.Modify(1, $record)
    }
    finally {
        Release-ComObject $record
        Release-ComObject $view
    }
}

try {
    New-Item -ItemType Directory `
        -Path $scratchRoot, $placeholderReference `
        -Force | Out-Null
    # Test-PackagedPayloads must reject the MSI tables before it attempts to
    # parse this deliberately non-ZIP placeholder or inspect the reference.
    [IO.File]::WriteAllText(
        $placeholderZip,
        'must remain unread',
        (New-Object Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $absoluteMsi -Destination $fixtureMsi
    Copy-Item -LiteralPath $absoluteMsi -Destination $directoryNameFixtureMsi

    # Establish a passing baseline using the exact verifier called by
    # Test-PackagedPayloads.ps1.
    & $tableVerifier `
        -MsiPath $absoluteMsi `
        -ManifestPath $absoluteManifest

    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        # 1 is msiOpenDatabaseModeTransact. Only the disposable copy is opened.
        $database = $installer.OpenDatabase($fixtureMsi, 1)

        # INSTALLFOLDER is also a child of ProgramsFolder, making this a true
        # sibling subtree that the former DesktopPet.exe-root scan ignored.
        Add-MsiRecord `
            -Installer $installer `
            -Database $database `
            -Query (
                'SELECT `Directory`, `Directory_Parent`, `DefaultDir` ' +
                'FROM `Directory`') `
            -Values @(
                'PayloadSiblingDir',
                'ProgramsFolder',
                'PayloadSibling'
            )
        Add-MsiRecord `
            -Installer $installer `
            -Database $database `
            -Query (
                'SELECT `Component`, `ComponentId`, `Directory_`, ' +
                '`Attributes`, `Condition`, `KeyPath` FROM `Component`') `
            -Values @(
                'CmpPayloadSiblingProbe',
                '{5F7319AA-9E48-4CA7-B5F7-778BF30D073A}',
                'PayloadSiblingDir',
                [int]0,
                $null,
                'FilePayloadSiblingProbe'
            )
        Add-MsiRecord `
            -Installer $installer `
            -Database $database `
            -Query (
                'SELECT `File`, `Component_`, `FileName`, `FileSize`, ' +
                '`Version`, `Language`, `Attributes`, `Sequence` FROM `File`') `
            -Values @(
                'FilePayloadSiblingProbe',
                'CmpPayloadSiblingProbe',
                'DesktopPetSiblingProbe.txt',
                [int]1,
                $null,
                $null,
                [int]0,
                [int]32000
            )
        [void]$database.Commit()
    }
    finally {
        Release-ComObject $database
        Release-ComObject $installer
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }

    $failure = $null
    try {
        & $packagedVerifier `
            -ZipPath $placeholderZip `
            -MsiPath $fixtureMsi `
            -ReferenceRoot $placeholderReference *> $null
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw (
            'MSI payload-table negative control accepted a File row in a ' +
            'sibling Directory component.')
    }
    if ($failure.Exception.Message -notmatch
        "targets directory 'PayloadSiblingDir'.*permitted 'INSTALLFOLDER'") {
        throw (
            'MSI payload-table negative control failed for an unexpected ' +
            "reason: $($failure.Exception.Message)")
    }

    $installer = $null
    $database = $null
    $view = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        # 1 is msiOpenDatabaseModeTransact. Only the disposable copy is opened.
        $database = $installer.OpenDatabase($directoryNameFixtureMsi, 1)
        $view = $database.OpenView(
            "UPDATE ``Directory`` SET ``DefaultDir``='UnexpectedProductDir' " +
            "WHERE ``Directory``='INSTALLFOLDER'")
        [void]$view.Execute()
        [void]$database.Commit()
    }
    finally {
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }

    $failure = $null
    try {
        & $packagedVerifier `
            -ZipPath $placeholderZip `
            -MsiPath $directoryNameFixtureMsi `
            -ReferenceRoot $placeholderReference *> $null
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw (
            'MSI payload-table negative control accepted an INSTALLFOLDER ' +
            'target name that differs from ProductName.')
    }
    if ($failure.Exception.Message -notmatch (
            "Directory 'INSTALLFOLDER' has target name " +
            "'UnexpectedProductDir'.*required target name is ProductName")) {
        throw (
            'MSI install-directory negative control failed for an unexpected ' +
            "reason: $($failure.Exception.Message)")
    }

    if ((Get-FileHash -LiteralPath $absoluteMsi -Algorithm SHA256).Hash -cne
        $sourceMsiHash) {
        throw 'MSI payload-table fixture creation modified the source MSI.'
    }

    Write-Host (
        'PASS: MSI table verification accepts the canonical manifest and ' +
        'Test-PackagedPayloads rejects real MSI fixtures with an extra ' +
        'sibling-directory component or a ProductName-mismatched install ' +
        'directory before extraction.'
    ) -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        . (Join-Path $repoRoot 'packaging\StagingPathSafety.ps1')
        Remove-DesktopPetSafeDirectory `
            -Path $scratchRoot `
            -AllowedRoot $tempRoot `
            -TrustedRoot $tempRoot
    }
}
