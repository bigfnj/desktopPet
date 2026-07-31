#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [switch]$UseIsolatedInstallRoot,
    [switch]$UseNonReleaseMutatedMsi,
    [switch]$RequireValidSignature,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
    throw "MSI not found: $MsiPath"
}
if ($UseNonReleaseMutatedMsi -and -not $UseIsolatedInstallRoot) {
    throw '-UseNonReleaseMutatedMsi requires -UseIsolatedInstallRoot.'
}
if ($UseNonReleaseMutatedMsi -and $RequireValidSignature) {
    throw (
        '-UseNonReleaseMutatedMsi cannot be combined with ' +
        '-RequireValidSignature because database mutation invalidates Authenticode.'
    )
}

$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'ProductVersion.props') -Raw
$productName = [string]$props.Project.PropertyGroup.DesktopPetProductName
if ([string]::IsNullOrWhiteSpace($productName)) {
    throw 'ProductVersion.props does not define DesktopPetProductName.'
}

$absoluteMsi = [IO.Path]::GetFullPath($MsiPath)
$originalMsiLease = $null
$executionMsiLease = $null
try {
$originalMsiLease = Open-DesktopPetValidatedInputFile `
    -Path $absoluteMsi `
    -Root (Split-Path -Parent $absoluteMsi)
$msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
if (-not [IO.Path]::IsPathRooted($msiExec) -or
    -not (Test-Path -LiteralPath $msiExec -PathType Leaf)) {
    throw "The absolute Windows Installer executable was not found: $msiExec"
}

& (Join-Path $PSScriptRoot 'Test-MsiUpgradeSchedule.ps1') -MsiPath $absoluteMsi

function Get-MsiArtifactIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $signerThumbprint = if ($null -ne $signature.SignerCertificate) {
        ([string]$signature.SignerCertificate.Thumbprint).Replace(
            ' ', '').ToUpperInvariant()
    }
    else { '' }
    $timestampThumbprint = if ($null -ne $signature.TimeStamperCertificate) {
        ([string]$signature.TimeStamperCertificate.Thumbprint).Replace(
            ' ', '').ToUpperInvariant()
    }
    else { '' }
    return [pscustomobject]@{
        Sha256 = (
            Get-FileHash -LiteralPath $Path -Algorithm SHA256
        ).Hash.ToUpperInvariant()
        SignatureStatus = [string]$signature.Status
        SignerThumbprint = $signerThumbprint
        TimestampThumbprint = $timestampThumbprint
    }
}

$originalMsiIdentity = Get-MsiArtifactIdentity -Path $absoluteMsi
if ($RequireValidSignature -and (
        $originalMsiIdentity.SignatureStatus -cne 'Valid' -or
        [string]::IsNullOrWhiteSpace(
            $originalMsiIdentity.SignerThumbprint) -or
        [string]::IsNullOrWhiteSpace(
            $originalMsiIdentity.TimestampThumbprint))) {
    throw (
        'The lifecycle input must have a valid Authenticode signature, signer, ' +
        'and timestamp when -RequireValidSignature is used.'
    )
}

function Assert-OriginalMsiPreserved {
    $observed = Get-MsiArtifactIdentity -Path $absoluteMsi
    foreach ($propertyName in @(
            'Sha256',
            'SignatureStatus',
            'SignerThumbprint',
            'TimestampThumbprint')) {
        if ([string]$observed.$propertyName -cne
            [string]$originalMsiIdentity.$propertyName) {
            throw (
                "MSI lifecycle changed original artifact identity field " +
                "'$propertyName'."
            )
        }
    }
    if ($RequireValidSignature -and
        $observed.SignatureStatus -cne 'Valid') {
        throw 'The original MSI Authenticode signature is no longer valid.'
    }
}

function Get-MsiScalar {
    param([Parameter(Mandatory = $true)][string]$Query)

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($absoluteMsi, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI metadata query returned no rows: $Query"
        }
        return [string]$record.StringData(1)
    }
    finally {
        foreach ($value in @($record, $view, $database, $installer)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
            }
        }
    }
}

function Get-MsiColumnValues {
    param([Parameter(Mandatory = $true)][string]$Query)

    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($absoluteMsi, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        $values = @()
        while ($null -ne ($record = $view.Fetch())) {
            $values += [string]$record.StringData(1)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            $record = $null
        }
        return $values
    }
    finally {
        foreach ($value in @($record, $view, $database, $installer)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
            }
        }
    }
}

function Get-MsiProductState {
    param([Parameter(Mandatory = $true)][string]$ProductCode)

    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return [int]$installer.ProductState($ProductCode)
    }
    finally {
        if ($null -ne $installer -and
            [Runtime.InteropServices.Marshal]::IsComObject($installer)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }
}

function Get-RelatedProductCodes {
    param([Parameter(Mandatory = $true)][string]$UpgradeCode)

    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return @($installer.RelatedProducts($UpgradeCode))
    }
    finally {
        if ($null -ne $installer -and
            [Runtime.InteropServices.Marshal]::IsComObject($installer)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }
}

function New-NonReleaseMutatedLifecycleMsi {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][hashtable]$PropertyValues,
        [switch]$IsolateProductIdentity
    )

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    $installer = $null
    $database = $null
    $componentView = $null
    $record = $null
    $upgradeView = $null
    $propertyView = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($DestinationPath, 1)

        foreach ($entry in $PropertyValues.GetEnumerator()) {
            $escapedName = ([string]$entry.Key).Replace("'", "''")
            $escapedValue = ([string]$entry.Value).Replace("'", "''")
            $propertyView = $database.OpenView(
                "DELETE FROM ``Property`` WHERE ``Property`` = '$escapedName'")
            [void]$propertyView.Execute()
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($propertyView)
            $propertyView = $database.OpenView(
                "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('$escapedName', '$escapedValue')")
            [void]$propertyView.Execute()
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($propertyView)
            $propertyView = $null
        }

        if ($IsolateProductIdentity) {
            $componentView = $database.OpenView(
                'SELECT `Component`, `ComponentId` FROM `Component`')
            [void]$componentView.Execute()
            $componentCount = 0
            while ($null -ne ($record = $componentView.Fetch())) {
                $record.StringData(2) = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
                [void]$componentView.Modify(2, $record)
                $componentCount++
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
                $record = $null
            }
            if ($componentCount -eq 0) {
                throw 'The isolated lifecycle MSI contains no components to isolate.'
            }

            $upgradeView = $database.OpenView('DELETE FROM `Upgrade`')
            [void]$upgradeView.Execute()
        }
        [void]$database.Commit()
    }
    finally {
        foreach ($value in @(
                $record,
                $propertyView,
                $upgradeView,
                $componentView,
                $database,
                $installer)) {
            if ($null -ne $value -and
                [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
            }
        }
    }
}

function Get-PathFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return 'ABSENT'
    }

    $rootItem = Get-Item -LiteralPath $fullPath -Force
    $items = @($rootItem)
    if ($rootItem.PSIsContainer) {
        $items += @(Get-ChildItem -LiteralPath $fullPath -Force -Recurse)
    }
    $records = @(
        $items |
            Sort-Object FullName |
            ForEach-Object {
                $relative = if ($_.FullName.Length -eq $fullPath.Length) {
                    '.'
                }
                else {
                    $_.FullName.Substring($fullPath.Length).TrimStart('\')
                }
                if ($_.PSIsContainer) {
                    "D|$relative|$([int]$_.Attributes)|$($_.CreationTimeUtc.Ticks)|$($_.LastWriteTimeUtc.Ticks)"
                }
                else {
                    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                    "F|$relative|$([int]$_.Attributes)|$($_.Length)|$($_.CreationTimeUtc.Ticks)|$($_.LastWriteTimeUtc.Ticks)|$hash"
                }
            }
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($records -join "`n"))
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

$productCode = Get-MsiScalar -Query (
    'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
if ($productCode -notmatch '^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$') {
    throw "MSI ProductCode is missing or invalid: '$productCode'"
}
$installDirectoryId = Get-MsiScalar -Query (
    'SELECT `Directory` FROM `Directory` WHERE `Directory` = ''INSTALLFOLDER''')
if ($installDirectoryId -cne 'INSTALLFOLDER') {
    throw 'The MSI does not expose the expected public INSTALLFOLDER directory property.'
}
$upgradeCode = Get-MsiScalar -Query (
    'SELECT `UpgradeCode` FROM `Upgrade`')
if ($upgradeCode -notmatch '^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$') {
    throw "MSI UpgradeCode is missing or invalid: '$upgradeCode'"
}
if ($UseIsolatedInstallRoot) {
    $isolationCapability = Get-MsiScalar -Query (
        'SELECT `Value` FROM `Property` WHERE `Property` = ''DESKTOPPET_ISOLATED_LIFECYCLE_CAPABILITY''')
    if ($isolationCapability -cne '1') {
        throw 'The MSI does not declare the required isolated-lifecycle capability.'
    }

    $secureProperties = @(
        (Get-MsiScalar -Query (
            'SELECT `Value` FROM `Property` WHERE `Property` = ''SecureCustomProperties''')
        ).Split(';')
    )
    foreach ($propertyName in @(
            'INSTALLFOLDER',
            'DESKTOPPET_REGISTRYROOT',
            'DESKTOPPET_TEST_DESKTOPFOLDER',
            'DESKTOPPET_TEST_STARTMENUFOLDER')) {
        if ($secureProperties -notcontains $propertyName) {
            throw "The MSI does not secure isolated-lifecycle property '$propertyName'."
        }
    }

    $expectedDirectoryActions = @{
        SetDesktopFolder = @{
            Source = 'DesktopFolder'
            Target = '[DESKTOPPET_TEST_DESKTOPFOLDER]'
            ExecuteSequence = '1002'
        }
        SetAppMenuFolder = @{
            Source = 'AppMenuFolder'
            Target = '[DESKTOPPET_TEST_STARTMENUFOLDER]'
            ExecuteSequence = '1003'
        }
    }
    foreach ($actionName in $expectedDirectoryActions.Keys) {
        $expectedAction = $expectedDirectoryActions[$actionName]
        $source = Get-MsiScalar -Query (
            "SELECT ``Source`` FROM ``CustomAction`` WHERE ``Action`` = '$actionName'")
        $target = Get-MsiScalar -Query (
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = '$actionName'")
        $type = Get-MsiScalar -Query (
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = '$actionName'")
        $executeCondition = Get-MsiScalar -Query (
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$actionName'")
        $executeSequence = Get-MsiScalar -Query (
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$actionName'")
        $uiCondition = Get-MsiScalar -Query (
            "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action`` = '$actionName'")
        $expectedCondition = $expectedAction.Target.Trim('[', ']')
        if ($source -cne $expectedAction.Source -or
            $target -cne $expectedAction.Target -or
            $type -cne '35' -or
            $executeCondition -cne $expectedCondition -or
            $executeSequence -cne $expectedAction.ExecuteSequence -or
            $uiCondition -cne $expectedCondition) {
            throw "The MSI isolated shortcut action '$actionName' is not exact."
        }
    }

    $registryKeys = @(
        Get-MsiColumnValues -Query 'SELECT `Key` FROM `Registry`'
    )
    if ($registryKeys.Count -eq 0 -or
        @($registryKeys | Where-Object {
                -not $_.StartsWith(
                    '[DESKTOPPET_REGISTRYROOT]',
                    [StringComparison]::Ordinal)
            }).Count -ne 0) {
        throw 'The MSI contains registry rows outside its redirectable registry root.'
    }
}

$productState = Get-MsiProductState -ProductCode $productCode
if ($productState -ne -1) {
    throw "Refusing MSI lifecycle test because ProductCode $productCode is already registered (state $productState)."
}

$canonicalInstallRoot = Join-Path (
    [Environment]::GetFolderPath('LocalApplicationData')) "Programs\$productName"
$testId = [Guid]::NewGuid().ToString('N')
$logRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-MsiTest-' + $testId)
$installRoot = if ($UseIsolatedInstallRoot) {
    Join-Path $logRoot 'install'
}
else {
    $canonicalInstallRoot
}
$productionStartMenuShortcut = Join-Path $env:APPDATA (
    "Microsoft\Windows\Start Menu\Programs\$productName\$productName.lnk")
$productionDesktopShortcut = Join-Path (
    [Environment]::GetFolderPath('DesktopDirectory')) "$productName.lnk"
$isolatedShortcutRoot = Join-Path $logRoot 'shortcuts'
$isolatedDesktopFolder = Join-Path $isolatedShortcutRoot 'Desktop'
$isolatedStartMenuFolder = Join-Path $isolatedShortcutRoot 'StartMenu'
$startMenuShortcut = if ($UseIsolatedInstallRoot) {
    Join-Path $isolatedStartMenuFolder "$productName.lnk"
}
else {
    $productionStartMenuShortcut
}
$desktopShortcut = if ($UseIsolatedInstallRoot) {
    Join-Path $isolatedDesktopFolder "$productName.lnk"
}
else {
    $productionDesktopShortcut
}
$isolatedDataRoot = Join-Path $logRoot 'isolated-data'
$isolatedRegistryRoot = "Software\bigfnj\DesktopPetMsiLifecycleTest_$testId"
$isolatedRegistryPath = "HKCU:\$isolatedRegistryRoot"
$installedExe = Join-Path $installRoot 'DesktopPet.exe'
$installedLibrary = Join-Path $installRoot 'Newtonsoft.Json.dll'
$executionMsi = $absoluteMsi
$installed = $false
$installAttempted = $false
$operationTimedOut = $false
$originalDataRoot = $env:DESKTOPPET_DATA_ROOT

$uninstallRoots = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
$existingRegistrations = @(
    foreach ($root in $uninstallRoots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue |
                Get-ItemProperty -ErrorAction SilentlyContinue
        }
    }
)
$sameProductCodeRegistration = @(
    $existingRegistrations |
        Where-Object { $_.PSChildName -ieq $productCode }
)
$sameNameRegistrations = @(
    $existingRegistrations |
        Where-Object {
            $displayNameProperty =
                $_.PSObject.Properties['DisplayName']
            $null -ne $displayNameProperty -and
                [string]$displayNameProperty.Value -eq $productName
        }
)
$relatedProductCodes = @(
    Get-RelatedProductCodes -UpgradeCode $upgradeCode |
        Where-Object { $_ -ine $productCode }
)
if ($sameProductCodeRegistration.Count -ne 0) {
    throw "Refusing MSI lifecycle test because ProductCode $productCode is already registered."
}
$requiresCollisionIsolation = (
    $sameNameRegistrations.Count -ne 0 -or
    $relatedProductCodes.Count -ne 0)
if ($requiresCollisionIsolation -and -not $UseNonReleaseMutatedMsi) {
    throw (
        "Refusing final-artifact MSI lifecycle test because another " +
        "'$productName' product is registered. Use a clean runner. " +
        '-UseNonReleaseMutatedMsi is reserved for explicit local diagnostics.'
    )
}
if ($UseNonReleaseMutatedMsi -and -not $requiresCollisionIsolation) {
    throw (
        '-UseNonReleaseMutatedMsi was requested without a registered-product ' +
        'collision; the original MSI must be tested instead.'
    )
}

$preExistingPaths = @(@(
    $installRoot,
    $startMenuShortcut,
    $desktopShortcut
) | Where-Object { Test-Path -LiteralPath $_ })
if ($preExistingPaths.Count -gt 0) {
    throw "Refusing MSI lifecycle test because test-owned paths already exist for this run."
}
if ($UseIsolatedInstallRoot -and
    (Test-Path -LiteralPath $isolatedRegistryPath)) {
    throw "Refusing MSI lifecycle test because its isolated registry root already exists: $isolatedRegistryPath"
}

if ($UseIsolatedInstallRoot) {
    $resolvedLogRoot = [IO.Path]::GetFullPath($logRoot).TrimEnd('\')
    $resolvedCanonicalRoot = [IO.Path]::GetFullPath($canonicalInstallRoot)
    foreach ($path in @(
            $installRoot,
            $isolatedDesktopFolder,
            $isolatedStartMenuFolder,
            $isolatedDataRoot)) {
        $resolvedPath = [IO.Path]::GetFullPath($path)
        if (-not $resolvedPath.StartsWith(
                $resolvedLogRoot + '\',
                [StringComparison]::OrdinalIgnoreCase) -or
            $resolvedPath.Equals(
                $resolvedCanonicalRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "An isolated MSI lifecycle path is unsafe: $resolvedPath"
        }
    }
    if ($isolatedRegistryRoot -notmatch
        '^Software\\bigfnj\\DesktopPetMsiLifecycleTest_[0-9a-f]{32}$') {
        throw "The isolated MSI lifecycle registry root is unsafe: $isolatedRegistryRoot"
    }
    Write-Host "Isolated MSI lifecycle workspace: $resolvedLogRoot" `
        -ForegroundColor DarkGray
}

$preservedPathFingerprints = @{}
if ($UseIsolatedInstallRoot) {
    foreach ($path in @(
            $canonicalInstallRoot,
            $productionStartMenuShortcut,
            $productionDesktopShortcut)) {
        $preservedPathFingerprints[$path] = Get-PathFingerprint -Path $path
    }
}

function Assert-PreservedProductionPaths {
    foreach ($entry in $preservedPathFingerprints.GetEnumerator()) {
        $observed = Get-PathFingerprint -Path $entry.Key
        if ($observed -cne $entry.Value) {
            throw "Isolated MSI lifecycle changed a preserved production path: $($entry.Key)"
        }
    }
}

function Remove-EmptyIsolatedRegistryTree {
    if (-not $UseIsolatedInstallRoot -or
        -not (Test-Path -LiteralPath $isolatedRegistryPath)) {
        return
    }

    $keys = @(
        Get-Item -LiteralPath $isolatedRegistryPath
        Get-ChildItem -LiteralPath $isolatedRegistryPath -Recurse
    )
    foreach ($key in $keys) {
        $applicationValues = @(
            (Get-ItemProperty -LiteralPath $key.PSPath).PSObject.Properties |
                Where-Object { $_.Name -notlike 'PS*' }
        )
        if ($applicationValues.Count -ne 0) {
            throw "MSI uninstall left values in its isolated registry tree: $($key.Name)"
        }
    }
    Remove-Item -LiteralPath $isolatedRegistryPath -Recurse -Force
}

$isolationArguments = if ($UseIsolatedInstallRoot) {
    @(
        "INSTALLFOLDER=`"$([IO.Path]::GetFullPath($installRoot))`""
        "DESKTOPPET_TEST_DESKTOPFOLDER=`"$([IO.Path]::GetFullPath($isolatedDesktopFolder))`""
        "DESKTOPPET_TEST_STARTMENUFOLDER=`"$([IO.Path]::GetFullPath($isolatedStartMenuFolder))`""
        "DESKTOPPET_REGISTRYROOT=`"$isolatedRegistryRoot`""
    ) -join ' '
}
else {
    ''
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)
    try {
        if ($Process.HasExited) { return }
    }
    catch {
        return
    }
    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
    }
    else {
        try { $Process.Kill() } catch { }
    }
}

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$LogName,
        [string]$AdditionalArguments,
        [int[]]$AllowedExitCodes = @(0, 3010)
    )

    $logPath = Join-Path $logRoot $LogName
    $arguments = @(
        "$Operation `"$executionMsi`""
        $isolationArguments
        $AdditionalArguments
        '/qn'
        '/norestart'
        "/l*v `"$logPath`""
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $arguments = $arguments -join ' '
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $msiExec
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'msi-lifecycle-before-msiexec-start' `
            -Path $executionMsi
        if (-not $process.Start()) {
            throw "msiexec $Operation could not be started."
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Set-Variable -Name operationTimedOut -Scope 1 -Value $true
            Stop-ProcessTree -Process $process
            $terminated = $false
            try {
                $terminated = $process.WaitForExit(10000)
            }
            catch { }
            if (-not $terminated) {
                throw "msiexec $Operation timed out and did not terminate after taskkill. Log: $logPath"
            }
            throw "msiexec $Operation timed out after $TimeoutSeconds seconds. Log: $logPath"
        }
        $process.WaitForExit()
        $rawExitCode = $process.ExitCode
        if ($null -eq $rawExitCode) {
            throw "msiexec $Operation returned no observable exit code. Log: $logPath"
        }
        $exitCode = [int]$rawExitCode
        if ($exitCode -notin $AllowedExitCodes) {
            throw "msiexec $Operation failed (exit $exitCode). Log: $logPath"
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    if ($UseIsolatedInstallRoot) {
        New-Item -ItemType Directory `
            -Path $isolatedDesktopFolder, $isolatedStartMenuFolder `
            -Force | Out-Null
    }
    if ($UseNonReleaseMutatedMsi) {
        $executionMsi =
            Join-Path $logRoot 'NONRELEASE-mutated-lifecycle.msi'
        $isolatedPropertyValues = @{
            INSTALLFOLDER = [IO.Path]::GetFullPath($installRoot)
            DESKTOPPET_TEST_DESKTOPFOLDER =
                [IO.Path]::GetFullPath($isolatedDesktopFolder)
            DESKTOPPET_TEST_STARTMENUFOLDER =
                [IO.Path]::GetFullPath($isolatedStartMenuFolder)
            DESKTOPPET_REGISTRYROOT = $isolatedRegistryRoot
        }
        New-NonReleaseMutatedLifecycleMsi `
            -SourcePath $absoluteMsi `
            -DestinationPath $executionMsi `
            -PropertyValues $isolatedPropertyValues `
            -IsolateProductIdentity
        Assert-OriginalMsiPreserved
        $executionMsiLease = Open-DesktopPetValidatedInputFile `
            -Path $executionMsi `
            -Root $logRoot
        $mutatedIdentity = Get-MsiArtifactIdentity -Path $executionMsi
        if ($executionMsiLease.ComputeHash('SHA256') -cne
            $mutatedIdentity.Sha256) {
            throw 'The retained mutated MSI differs from its validated hash.'
        }
        if ($mutatedIdentity.Sha256 -ceq $originalMsiIdentity.Sha256) {
            throw 'The explicit non-release MSI mutation path did not change the derivative hash.'
        }
        if ($originalMsiIdentity.SignatureStatus -ceq 'Valid' -and
            $mutatedIdentity.SignatureStatus -ceq 'Valid') {
            throw 'A database-mutated MSI unexpectedly retained a valid Authenticode signature.'
        }
        Write-Warning (
            'NON-RELEASE TEST PATH: lifecycle operations use a deliberately ' +
            'mutated, signature-invalid derivative because a local product ' +
            'collision prevents exact-artifact testing.'
        )
    }
    else {
        if ([IO.Path]::GetFullPath($executionMsi) -cne $absoluteMsi) {
            throw 'Release lifecycle testing must execute the original MSI path.'
        }
        Assert-OriginalMsiPreserved
        Write-Host (
            "Lifecycle operations will execute the original MSI unchanged: " +
            "$($originalMsiIdentity.Sha256)"
        ) -ForegroundColor DarkGray
    }
    $env:DESKTOPPET_DATA_ROOT = $isolatedDataRoot

    $installAttempted = $true
    Invoke-MsiExec -Operation '/i' -LogName 'install.log'
    Assert-OriginalMsiPreserved
    $installed = $true
    foreach ($path in @($installedExe, $startMenuShortcut, $desktopShortcut)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Silent install did not create the expected path: $path"
        }
    }
    if ($UseIsolatedInstallRoot -and
        -not (Test-Path -LiteralPath $isolatedRegistryPath)) {
        throw "Silent install did not create the isolated registry root: $isolatedRegistryPath"
    }
    Assert-PreservedProductionPaths

    $registration = @(
        foreach ($root in $uninstallRoots) {
            if (Test-Path -LiteralPath $root) {
                Get-ChildItem -LiteralPath $root -ErrorAction Stop |
                    Get-ItemProperty -ErrorAction Stop |
                    Where-Object {
                        $displayNameProperty =
                            $_.PSObject.Properties['DisplayName']
                        $_.PSChildName -ieq $productCode -and
                        $null -ne $displayNameProperty -and
                        [string]$displayNameProperty.Value -eq
                            $productName
                    }
            }
        }
    ) | Select-Object -First 1
    $noRepairProperty = if ($null -ne $registration) {
        $registration.PSObject.Properties['NoRepair']
    }
    else {
        $null
    }
    if (-not $registration -or
        $null -eq $noRepairProperty -or
        [int]$noRepairProperty.Value -ne 1) {
        throw 'The per-user MSI must disable the misleading Add/Remove Programs repair action.'
    }

    & (Join-Path $PSScriptRoot 'Invoke-ProductSelfTests.ps1') -Executable $installedExe

    $installedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    if (-not (Test-Path -LiteralPath $installedLibrary -PathType Leaf)) {
        throw "The non-executable repair probe is missing: $installedLibrary"
    }
    $libraryHash = (Get-FileHash -LiteralPath $installedLibrary -Algorithm SHA256).Hash
    [IO.File]::WriteAllBytes(
        $installedExe,
        [Text.Encoding]::ASCII.GetBytes(
            'DesktopPet MSI repair verification probe. This file must be replaced.'))
    $damagedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    if ($damagedHash -eq $installedHash) {
        throw 'The MSI repair probe did not change the installed executable hash.'
    }
    Remove-Item -LiteralPath $installedLibrary -Force
    if (Test-Path -LiteralPath $installedLibrary) {
        throw 'The MSI repair probe could not remove the installed library.'
    }

    # Registry key paths are required for per-user profile components (ICE38);
    # request all files explicitly instead of presenting an unreliable ARP repair UI.
    Invoke-MsiExec -Operation '/fa' -LogName 'repair.log'
    Assert-OriginalMsiPreserved
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw 'MSI repair did not preserve DesktopPet.exe.'
    }
    $repairedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    if ($repairedHash -ne $installedHash) {
        throw "MSI repair did not restore DesktopPet.exe exactly. Expected $installedHash; found $repairedHash."
    }
    if (-not (Test-Path -LiteralPath $installedLibrary -PathType Leaf)) {
        throw 'MSI full-file repair did not restore the deleted non-executable payload.'
    }
    $repairedLibraryHash = (
        Get-FileHash -LiteralPath $installedLibrary -Algorithm SHA256).Hash
    if ($repairedLibraryHash -ne $libraryHash) {
        throw "MSI repair did not restore Newtonsoft.Json.dll exactly. Expected $libraryHash; found $repairedLibraryHash."
    }
    & (Join-Path $PSScriptRoot 'Invoke-ProductSelfTests.ps1') -Executable $installedExe
    Assert-PreservedProductionPaths

    Invoke-MsiExec -Operation '/x' -LogName 'uninstall.log'
    Assert-OriginalMsiPreserved
    $remainingState = Get-MsiProductState -ProductCode $productCode
    if ($remainingState -ne -1) {
        throw "Silent uninstall left ProductCode $productCode registered (state $remainingState)."
    }
    $installed = $false
    Remove-EmptyIsolatedRegistryTree
    foreach ($path in @(
            $installRoot,
            $startMenuShortcut,
            $desktopShortcut,
            $isolatedRegistryPath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Silent uninstall left an installed path behind: $path"
        }
    }
    Assert-PreservedProductionPaths

    Write-Host (
        'Silent isolated MSI install, shortcut creation, installed self-tests, ' +
        'EXE/library full-file repair, shortcut removal, uninstall, and MSI ' +
        'hash/signature preservation passed.'
    ) -ForegroundColor Green
}
catch {
    foreach ($log in @(Get-ChildItem -LiteralPath $logRoot -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        Write-Warning "Tail of $($log.FullName):"
        Get-Content -LiteralPath $log.FullName -Tail 80 | Write-Warning
    }
    throw
}
finally {
    $env:DESKTOPPET_DATA_ROOT = $originalDataRoot
    $cleanupSucceeded = $true
    $cleanupState = if ($installAttempted) {
        try {
            Get-MsiProductState -ProductCode $productCode
        }
        catch {
            $cleanupSucceeded = $false
            Write-Warning "Could not determine MSI cleanup state: $($_.Exception.Message)"
            -1
        }
    }
    else {
        -1
    }
    if ($installed -or $cleanupState -ne -1 -or $operationTimedOut) {
        try {
            Invoke-MsiExec -Operation '/x' -LogName 'cleanup-uninstall.log' `
                -AllowedExitCodes @(0, 1605, 3010)
            $remainingCleanupState = Get-MsiProductState -ProductCode $productCode
            if ($remainingCleanupState -ne -1) {
                throw "Cleanup uninstall left ProductCode $productCode registered (state $remainingCleanupState)."
            }
        }
        catch {
            $cleanupSucceeded = $false
            Write-Warning "Cleanup uninstall failed: $($_.Exception.Message)"
        }
    }
    if ($cleanupSucceeded -and
        $UseIsolatedInstallRoot -and
        (Test-Path -LiteralPath $isolatedRegistryPath)) {
        Remove-Item -LiteralPath $isolatedRegistryPath -Recurse -Force
    }
    if ($null -ne $executionMsiLease) {
        $executionMsiLease.Dispose()
        $executionMsiLease = $null
    }
    if ($cleanupSucceeded -and (Test-Path -LiteralPath $logRoot)) {
        $resolvedLogRoot = [IO.Path]::GetFullPath($logRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        if (-not $resolvedLogRoot.StartsWith(
                $resolvedTempRoot + '\DesktopPet-MsiTest-',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unsafe MSI lifecycle scratch root: $resolvedLogRoot"
        }
        Remove-Item -LiteralPath $resolvedLogRoot -Recurse -Force
    }
    elseif (-not $cleanupSucceeded) {
        Write-Warning "Preserving isolated MSI lifecycle evidence after cleanup failure: $logRoot"
    }
    Assert-PreservedProductionPaths
    Assert-OriginalMsiPreserved
}
}
finally {
    if ($null -ne $executionMsiLease) {
        $executionMsiLease.Dispose()
    }
    if ($null -ne $originalMsiLease) {
        $originalMsiLease.Dispose()
    }
}
