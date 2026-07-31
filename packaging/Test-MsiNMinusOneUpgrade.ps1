#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousMsiPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$PreviousReleaseTag,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedPreviousSha256,
    [Parameter(Mandatory = $true)][string]$CurrentMsiPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$CurrentReleaseTag,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedCurrentSha256,
    [Parameter(Mandatory = $true)][string]$CurrentRuntimeRoot,
    [Parameter(Mandatory = $true)][string]$RuntimeManifestPath,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [ValidateRange(1, 1800)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

if ($env:OS -cne 'Windows_NT') {
    throw 'N-1 MSI upgrade verification requires Windows Installer.'
}

$previousMsi = (Resolve-Path -LiteralPath $PreviousMsiPath).Path
$currentMsi = (Resolve-Path -LiteralPath $CurrentMsiPath).Path
$currentRuntime = (Resolve-Path -LiteralPath $CurrentRuntimeRoot).Path
$runtimeManifest = (Resolve-Path -LiteralPath $RuntimeManifestPath).Path
$resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
$evidenceParent = Split-Path -Parent $resolvedEvidence
if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container)) {
    throw "Upgrade evidence parent does not exist: $evidenceParent"
}
$evidenceProtectedPaths = @(
    $previousMsi,
    $currentMsi,
    $runtimeManifest)
$evidenceProtectedDirectories = @($currentRuntime)
$resolvedEvidence = Assert-DesktopPetOutputFileSafe `
    -Path $resolvedEvidence `
    -TrustedRoot $evidenceParent `
    -ProtectedPaths $evidenceProtectedPaths `
    -ProtectedDirectories $evidenceProtectedDirectories

function Publish-NMinusOneEvidence {
    param([Parameter(Mandatory = $true)]$Document)

    $evidenceDestinationExists = $false
    $evidenceDestinationSha256 = $null
    if (Test-Path -LiteralPath $resolvedEvidence -PathType Leaf) {
        $evidenceDestinationInput = Open-DesktopPetValidatedInputFile `
            -Path $resolvedEvidence `
            -Root $evidenceParent
        try {
            $evidenceDestinationSha256 =
                $evidenceDestinationInput.ComputeHash('SHA256')
            $evidenceDestinationExists = $true
        }
        finally {
            $evidenceDestinationInput.Dispose()
        }
    }
    elseif (Test-Path -LiteralPath $resolvedEvidence) {
        throw (
            'N-1 evidence destination is not a regular file: ' +
            $resolvedEvidence)
    }

    $stagingDirectory = Join-Path $evidenceParent (
        '.DesktopPet-nminusone-evidence-' +
        [Guid]::NewGuid().ToString('N'))
    $stagingLease = $null
    $sealedTemporaryEvidence = $null
    $evidencePrimaryError = $null
    $stagingLease = Open-DesktopPetNewScratchDirectory `
        -Path $stagingDirectory `
        -AllowedRoot $evidenceParent `
        -TrustedRoot $evidenceParent `
        -ProtectedPaths @(
            $evidenceProtectedPaths + $resolvedEvidence) `
        -ProtectedDirectories $evidenceProtectedDirectories
    try {
        $temporaryEvidence = Join-Path $stagingDirectory (
            [IO.Path]::GetFileName($resolvedEvidence) + '.tmp')
        $temporaryEvidence = Assert-DesktopPetOutputFileSafe `
            -Path $temporaryEvidence `
            -TrustedRoot $evidenceParent `
            -ProtectedPaths @(
                $evidenceProtectedPaths + $resolvedEvidence) `
            -ProtectedDirectories $evidenceProtectedDirectories
        $evidenceText =
            ($Document | ConvertTo-Json -Depth 8) +
            [Environment]::NewLine
        [void](Write-DesktopPetNewUtf8File `
            -Path $temporaryEvidence `
            -Root $stagingDirectory `
            -Content $evidenceText `
            -ProtectedPaths @(
                $evidenceProtectedPaths + $resolvedEvidence) `
            -ProtectedDirectories $evidenceProtectedDirectories `
            -MutationOperation 'before-nminusone-operational-evidence-write')
        $evidenceHasher = [Security.Cryptography.SHA256]::Create()
        try {
            $expectedEvidenceSha256 = ([BitConverter]::ToString(
                $evidenceHasher.ComputeHash(
                    (New-Object Text.UTF8Encoding($false)).
                        GetBytes($evidenceText)))).Replace('-', '')
        }
        finally {
            $evidenceHasher.Dispose()
        }
        $sealedTemporaryEvidence = Open-DesktopPetSealedStagedFile `
            -Path $temporaryEvidence `
            -Root $stagingDirectory
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'nminusone-operational-evidence-sealed-validate' `
            -Path $temporaryEvidence
        $temporaryEvidenceSha256 =
            $sealedTemporaryEvidence.ComputeHash('SHA256')
        if ($temporaryEvidenceSha256 -cne $expectedEvidenceSha256 -or
            $sealedTemporaryEvidence.ReadAllTextUtf8(16MB) -cne
                $evidenceText) {
            throw (
                'Generated N-1 evidence differs from its exact in-memory ' +
                'authoring bytes.')
        }
        $validated =
            $sealedTemporaryEvidence.ReadAllTextUtf8(16MB) |
                ConvertFrom-Json
        if ([int]$validated.schemaVersion -ne 1 -or
            [string]::IsNullOrWhiteSpace([string]$validated.status)) {
            throw 'Generated N-1 evidence failed its schema sanity check.'
        }
        $publishEvidenceParameters = @{
            TemporaryPath = $temporaryEvidence
            DestinationPath = $resolvedEvidence
            TrustedRoot = $evidenceParent
            ProtectedPaths = $evidenceProtectedPaths
            ProtectedDirectories = $evidenceProtectedDirectories
            SealedTemporaryFile = $sealedTemporaryEvidence
            ExpectedTemporarySha256 = $temporaryEvidenceSha256
        }
        if ($evidenceDestinationExists) {
            $publishEvidenceParameters.ExpectedDestinationSha256 =
                $evidenceDestinationSha256
        }
        else {
            $publishEvidenceParameters.DestinationMustBeAbsent = $true
        }
        [void](Publish-DesktopPetAtomicFile @publishEvidenceParameters)
    }
    catch {
        $evidencePrimaryError = $_
        throw
    }
    finally {
        if ($null -ne $sealedTemporaryEvidence) {
            $sealedTemporaryEvidence.Dispose()
            $sealedTemporaryEvidence = $null
        }
        if ($null -ne $stagingLease) {
            $stagingLease.Dispose()
            $stagingLease = $null
        }
        if (Test-Path -LiteralPath $stagingDirectory) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $stagingDirectory `
                    -AllowedRoot $evidenceParent `
                    -TrustedRoot $evidenceParent
            }
            catch {
                if ($null -eq $evidencePrimaryError) {
                    throw
                }
                Write-Warning (
                    'N-1 evidence scratch cleanup also failed; preserving ' +
                    "the primary error. Cleanup error: " +
                    $_.Exception.Message)
            }
        }
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (
        Get-FileHash -LiteralPath $Path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
}

$previousMsiInput = $null
$currentMsiInput = $null
try {
$previousMsiInput = Open-DesktopPetValidatedInputFile `
    -Path $previousMsi `
    -Root (Split-Path -Parent $previousMsi)
$currentMsiInput = Open-DesktopPetValidatedInputFile `
    -Path $currentMsi `
    -Root (Split-Path -Parent $currentMsi)
$previousHash =
    $previousMsiInput.ComputeHash('SHA256').ToLowerInvariant()
$currentHash =
    $currentMsiInput.ComputeHash('SHA256').ToLowerInvariant()
if ($previousHash -cne $ExpectedPreviousSha256.ToLowerInvariant()) {
    throw 'The downloaded N-1 MSI does not match its public checksum manifest.'
}
if ($currentHash -cne $ExpectedCurrentSha256.ToLowerInvariant()) {
    throw 'The current MSI changed before N-1 upgrade verification.'
}

function Release-ComObject {
    param($Value)
    if ($null -ne $Value -and
        [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}

function Get-MsiScalar {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Query
    )
    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $view = $database.OpenView($Query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI query returned no row: $Query"
        }
        return [string]$record.StringData(1)
    }
    finally {
        foreach ($value in @($record, $view, $database, $installer)) {
            Release-ComObject $value
        }
    }
}

function Get-MsiMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)
    $metadata = [ordered]@{}
    foreach ($propertyName in @(
            'ProductCode',
            'ProductName',
            'ProductVersion')) {
        $escaped = $propertyName.Replace("'", "''")
        $metadata[$propertyName] = Get-MsiScalar `
            -Path $Path `
            -Query (
                "SELECT ``Value`` FROM ``Property`` WHERE " +
                "``Property``='$escaped'")
    }
    $metadata.UpgradeCode = Get-MsiScalar `
        -Path $Path `
        -Query 'SELECT `UpgradeCode` FROM `Upgrade`'
    return [pscustomobject]$metadata
}

function Get-ProductState {
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return [int]$installer.ProductState($ProductCode)
    }
    finally {
        Release-ComObject $installer
    }
}

function Get-RelatedProducts {
    param([Parameter(Mandatory = $true)][string]$UpgradeCode)
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return @($installer.RelatedProducts($UpgradeCode))
    }
    finally {
        Release-ComObject $installer
    }
}

function Get-InstallLocation {
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return [string]$installer.ProductInfo(
            $ProductCode,
            'InstallLocation')
    }
    finally {
        Release-ComObject $installer
    }
}

$msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
if (-not (Test-Path -LiteralPath $msiExec -PathType Leaf)) {
    throw "Windows Installer executable is missing: $msiExec"
}
$logRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-NMinusOne-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$script:installerQuiescenceConfirmed = $true

function Test-WindowsInstallerQuiescent {
    $mutex = $null
    $acquired = $false
    try {
        $createdNew = $false
        $mutex = [Threading.Mutex]::new(
            $false,
            'Global\_MSIExecute',
            [ref]$createdNew)
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            # WaitOne grants ownership when it reports an abandoned mutex.
            $acquired = $true
        }
        return $acquired
    }
    catch {
        return $false
    }
    finally {
        if ($acquired -and $null -ne $mutex) {
            $mutex.ReleaseMutex()
        }
        if ($null -ne $mutex) {
            $mutex.Dispose()
        }
    }
}

function Wait-WindowsInstallerQuiescence {
    param([ValidateRange(1, 120)][int]$WaitSeconds = 30)

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        do {
            if (Test-WindowsInstallerQuiescent) {
                return $true
            }
            Start-Sleep -Milliseconds 250
        }
        while ($stopwatch.Elapsed.TotalSeconds -lt $WaitSeconds)
        return $false
    }
    finally {
        $stopwatch.Stop()
    }
}

function Stop-ProcessTree {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($Process.HasExited) {
        return
    }
    $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (-not (Test-Path -LiteralPath $taskkill -PathType Leaf)) {
        throw "Cannot stop timed-out $Context because taskkill.exe is missing."
    }

    $stopInfo = New-Object Diagnostics.ProcessStartInfo
    $stopInfo.FileName = $taskkill
    $stopInfo.Arguments = "/PID $($Process.Id) /T /F"
    $stopInfo.UseShellExecute = $false
    $stopInfo.CreateNoWindow = $true
    $stopper = New-Object Diagnostics.Process
    $stopper.StartInfo = $stopInfo
    try {
        if (-not $stopper.Start()) {
            throw "Could not start taskkill for timed-out $Context."
        }
        if (-not $stopper.WaitForExit(10000)) {
            try { $stopper.Kill() } catch { }
            throw "taskkill did not finish within 10 seconds for $Context."
        }
    }
    finally {
        $stopper.Dispose()
    }

    if (-not $Process.WaitForExit(10000)) {
        throw "Timed-out $Context process tree did not exit within 10 seconds."
    }
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Msi,
        [Parameter(Mandatory = $true)][string]$LogName,
        [Parameter(Mandatory = $true)][int[]]$AllowedExitCodes
    )
    $logPath = Join-Path $logRoot $LogName
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $msiExec
    $startInfo.Arguments = (
        "$Operation `"$Msi`" /qn /norestart /l*v `"$logPath`"")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'nminusone-before-msiexec-start' `
            -Path $Msi
        if (-not $process.Start()) {
            throw "Could not start msiexec for $LogName."
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $script:installerQuiescenceConfirmed = $false
            Stop-ProcessTree `
                -Process $process `
                -Context "msiexec $LogName"
            $script:installerQuiescenceConfirmed =
                Wait-WindowsInstallerQuiescence -WaitSeconds 30
            if (-not $script:installerQuiescenceConfirmed) {
                throw (
                    "msiexec timed out for $LogName; its process tree was " +
                    'stopped, but Windows Installer quiescence was not confirmed.')
            }
            throw (
                "msiexec timed out for $LogName; its process tree was stopped " +
                'and Windows Installer quiescence was confirmed.')
        }
        $process.WaitForExit()
        $script:installerQuiescenceConfirmed =
            Wait-WindowsInstallerQuiescence -WaitSeconds 30
        if (-not $script:installerQuiescenceConfirmed) {
            throw (
                "Windows Installer did not become quiescent after $LogName.")
        }
        $exitCode = [int]$process.ExitCode
        if ($exitCode -notin $AllowedExitCodes) {
            throw "msiexec failed for $LogName (exit $exitCode)."
        }
        return $exitCode
    }
    finally {
        $process.Dispose()
    }
}

$previous = Get-MsiMetadata -Path $previousMsi
$current = Get-MsiMetadata -Path $currentMsi
foreach ($metadata in @($previous, $current)) {
    if ($metadata.ProductCode -notmatch
            '^\{[0-9A-Fa-f-]{36}\}$' -or
        $metadata.UpgradeCode -notmatch
            '^\{[0-9A-Fa-f-]{36}\}$' -or
        $metadata.ProductVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw 'N-1 or current MSI has invalid identity metadata.'
    }
}
if ($previous.UpgradeCode -cne $current.UpgradeCode) {
    throw 'N-1 and current MSI do not share the production UpgradeCode.'
}
if ($previous.ProductCode -ceq $current.ProductCode) {
    throw 'N-1 and current MSI must have different ProductCodes.'
}
if ([version]$previous.ProductVersion -ge [version]$current.ProductVersion) {
    throw 'N-1 MSI version is not lower than the current MSI version.'
}
if ($PreviousReleaseTag -cne "v$($previous.ProductVersion)" -or
    $CurrentReleaseTag -cne "v$($current.ProductVersion)") {
    throw 'Release tags do not match the MSI ProductVersion values.'
}

$relatedBefore = @(Get-RelatedProducts -UpgradeCode $current.UpgradeCode)
if ($relatedBefore.Count -ne 0) {
    throw (
        'N-1 upgrade test requires a clean runner with no related product; ' +
        "found $($relatedBefore -join ', ').")
}

$runtimeFiles = @(
    Get-Content -LiteralPath $runtimeManifest |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)
$currentRuntimeHashes = @{}
foreach ($name in $runtimeFiles) {
    if (-not (Test-DesktopPetWindowsLeafName -Name $name) -or
        $currentRuntimeHashes.ContainsKey($name)) {
        throw "Runtime manifest contains an unsafe or duplicate name: '$name'."
    }
    $path = Join-Path $currentRuntime $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Current signed runtime is missing: $path"
    }
    $currentRuntimeHashes[$name] = Get-Sha256 -Path $path
}

$localAppData = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)).TrimEnd('\')
$settingsRoot = Join-Path $localAppData 'DesktopPet'
if (Test-Path -LiteralPath $settingsRoot) {
    throw "N-1 upgrade test requires an absent canonical settings root: $settingsRoot"
}

$previousInstalled = $false
$currentInstalled = $false
$settingsCreated = $false
$probeName = 'DesktopPet.obsolete-upgrade-probe'
$settingsPath = Join-Path $settingsRoot 'settings.json'
$probeMarker = [Guid]::NewGuid().ToString('N')
$downgradeExitCode = $null
$evidence = $null
try {
    [void](Invoke-Msi `
        -Operation '/i' `
        -Msi $previousMsi `
        -LogName 'previous-install.log' `
        -AllowedExitCodes @(0, 3010))
    $previousInstalled = $true
    if ((Get-ProductState -ProductCode $previous.ProductCode) -ne 5) {
        throw 'N-1 MSI did not reach the installed product state.'
    }

    $installRoot = [IO.Path]::GetFullPath(
        (Get-InstallLocation -ProductCode $previous.ProductCode)).TrimEnd('\')
    if (-not $installRoot.StartsWith(
            $localAppData + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        throw "N-1 MSI reported an unsafe or missing install root: $installRoot"
    }

    $obsoletePath = Join-Path $installRoot $probeName
    [IO.File]::WriteAllText(
        $obsoletePath,
        "obsolete-upgrade-probe:$probeMarker",
        (New-Object Text.UTF8Encoding($false)))
    New-Item -ItemType Directory -Path $settingsRoot -Force | Out-Null
    $settingsCreated = $true
    $settingsJson = [ordered]@{
        schemaVersion = 1
        upgradeProbe = $probeMarker
    } | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText(
        $settingsPath,
        ($settingsJson + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    $settingsHash = Get-Sha256 -Path $settingsPath

    [void](Invoke-Msi `
        -Operation '/i' `
        -Msi $currentMsi `
        -LogName 'current-upgrade.log' `
        -AllowedExitCodes @(0, 3010))
    $currentInstalled = $true
    $previousInstalled = $false
    if ((Get-ProductState -ProductCode $previous.ProductCode) -ne -1 -or
        (Get-ProductState -ProductCode $current.ProductCode) -ne 5) {
        throw 'Major upgrade did not replace the N-1 product registration.'
    }

    $currentInstallRoot = [IO.Path]::GetFullPath(
        (Get-InstallLocation -ProductCode $current.ProductCode)).TrimEnd('\')
    if (-not $currentInstallRoot.StartsWith(
            $localAppData + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $currentInstallRoot -PathType Container)) {
        throw "Current MSI reported an unsafe or missing install root: $currentInstallRoot"
    }
    foreach ($name in $runtimeFiles) {
        $installedPath = Join-Path $currentInstallRoot $name
        if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf) -or
            (Get-Sha256 -Path $installedPath) -cne
                [string]$currentRuntimeHashes[$name]) {
            throw "Major upgrade did not install the exact current runtime file: $name"
        }
    }
    if (Test-Path -LiteralPath $obsoletePath) {
        throw "Major upgrade did not remove obsolete file probe: $obsoletePath"
    }
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf) -or
        (Get-Sha256 -Path $settingsPath) -cne $settingsHash) {
        throw 'Major upgrade did not preserve the exact settings file.'
    }

    $downgradeExitCode = Invoke-Msi `
        -Operation '/i' `
        -Msi $previousMsi `
        -LogName 'downgrade-rejection.log' `
        -AllowedExitCodes @(1603, 1638)
    if ((Get-ProductState -ProductCode $current.ProductCode) -ne 5 -or
        (Get-ProductState -ProductCode $previous.ProductCode) -ne -1) {
        throw 'Rejected downgrade changed product registration state.'
    }
    foreach ($name in $runtimeFiles) {
        $installedPath = Join-Path $currentInstallRoot $name
        if ((Get-Sha256 -Path $installedPath) -cne
            [string]$currentRuntimeHashes[$name]) {
            throw "Rejected downgrade changed current runtime file: $name"
        }
    }
    if ((Get-Sha256 -Path $settingsPath) -cne $settingsHash) {
        throw 'Rejected downgrade changed preserved settings.'
    }

    [void](Invoke-Msi `
        -Operation '/x' `
        -Msi $currentMsi `
        -LogName 'current-uninstall.log' `
        -AllowedExitCodes @(0, 3010))
    $currentInstalled = $false
    if ((Get-ProductState -ProductCode $current.ProductCode) -ne -1 -or
        (Get-ProductState -ProductCode $previous.ProductCode) -ne -1) {
        throw 'Uninstall left an N-1 or current product registration.'
    }
    foreach ($name in $runtimeFiles) {
        if (Test-Path -LiteralPath (Join-Path $currentInstallRoot $name)) {
            throw "Uninstall left current runtime file: $name"
        }
    }
    if ((Get-Sha256 -Path $settingsPath) -cne $settingsHash) {
        throw 'Uninstall did not preserve the user-owned settings file.'
    }
    if ($previousMsiInput.ComputeHash('SHA256').ToLowerInvariant() -cne
            $previousHash -or
        $currentMsiInput.ComputeHash('SHA256').ToLowerInvariant() -cne
            $currentHash) {
        throw 'N-1 upgrade testing changed an input MSI.'
    }

    $evidence = [ordered]@{
        schemaVersion = 1
        status = 'passed'
        currentReleaseTag = $CurrentReleaseTag
        currentProductVersion = $current.ProductVersion
        currentProductCode = $current.ProductCode.ToUpperInvariant()
        currentMsiSha256 = $currentHash
        previousReleaseTag = $PreviousReleaseTag
        previousProductVersion = $previous.ProductVersion
        previousProductCode = $previous.ProductCode.ToUpperInvariant()
        previousMsiSha256 = $previousHash
        upgradeCode = $current.UpgradeCode.ToUpperInvariant()
        runtimeFileCount = $runtimeFiles.Count
        exactCurrentRuntimeInstalled = $true
        obsoleteFileProbe = $probeName
        obsoleteFileRemoved = $true
        settingsSha256 = $settingsHash
        settingsPreservedThroughUpgradeAndUninstall = $true
        downgradeRejected = $true
        downgradeExitCode = [int]$downgradeExitCode
        uninstallCompleted = $true
        inputMsiHashesPreserved = $true
    }
}
catch {
    foreach ($log in @(
            Get-ChildItem -LiteralPath $logRoot `
                -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        Write-Warning "Tail of $($log.FullName):"
        Get-Content -LiteralPath $log.FullName -Tail 60 | Write-Warning
    }
    throw
}
finally {
    $cleanupFailure = $null
    $currentState = $null
    $previousState = $null
    if (-not $script:installerQuiescenceConfirmed) {
        $cleanupFailure = (
            'Skipping MSI cleanup transactions because Windows Installer ' +
            'quiescence was not confirmed; the runner must be discarded.')
        Write-Warning $cleanupFailure
    }
    else {
        try {
            # Cleanup decisions come from Windows Installer product state, not
            # only from the optimistic bookkeeping Booleans above.
            $currentState =
                Get-ProductState -ProductCode $current.ProductCode
            $previousState =
                Get-ProductState -ProductCode $previous.ProductCode
        }
        catch {
            $script:installerQuiescenceConfirmed = $false
            $cleanupFailure = (
                'Could not safely query product state before cleanup: ' +
                $_.Exception.Message)
            Write-Warning $cleanupFailure
        }
    }

    if ($script:installerQuiescenceConfirmed -and
        $null -ne $currentState -and
        [int]$currentState -ne -1) {
        try {
            [void](Invoke-Msi `
                -Operation '/x' `
                -Msi $currentMsi `
                -LogName 'cleanup-current.log' `
                -AllowedExitCodes @(0, 1605, 3010))
            if ((Get-ProductState `
                    -ProductCode $current.ProductCode) -ne -1) {
                throw 'Current product remains registered after cleanup.'
            }
        }
        catch {
            $cleanupFailure =
                "Current MSI cleanup failed: $($_.Exception.Message)"
            Write-Warning $cleanupFailure
        }
    }
    if ($script:installerQuiescenceConfirmed -and
        $null -ne $previousState -and
        [int]$previousState -ne -1) {
        try {
            [void](Invoke-Msi `
                -Operation '/x' `
                -Msi $previousMsi `
                -LogName 'cleanup-previous.log' `
                -AllowedExitCodes @(0, 1605, 3010))
            if ((Get-ProductState `
                    -ProductCode $previous.ProductCode) -ne -1) {
                throw 'Previous product remains registered after cleanup.'
            }
        }
        catch {
            $cleanupFailure =
                "Previous MSI cleanup failed: $($_.Exception.Message)"
            Write-Warning $cleanupFailure
        }
    }
    if ($script:installerQuiescenceConfirmed -and
        $settingsCreated -and
        (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        $observedSettings =
            Get-Content -LiteralPath $settingsPath -Raw -ErrorAction SilentlyContinue
        if ($observedSettings -match [regex]::Escape($probeMarker)) {
            Remove-Item -LiteralPath $settingsPath -Force
            if ((Test-Path -LiteralPath $settingsRoot -PathType Container) -and
                @(Get-ChildItem -LiteralPath $settingsRoot -Force).Count -eq
                    0) {
                Remove-Item -LiteralPath $settingsRoot -Force
            }
        }
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
}

if ($null -eq $evidence) {
    throw 'Successful N-1 verification produced no evidence document.'
}
Publish-NMinusOneEvidence -Document $evidence

Write-Host (
    "PASS: installed $PreviousReleaseTag, upgraded to $CurrentReleaseTag, " +
    'removed the obsolete probe, preserved settings, rejected downgrade, and uninstalled.'
) -ForegroundColor Green
}
finally {
    foreach ($msiInput in @($currentMsiInput, $previousMsiInput)) {
        if ($null -ne $msiInput) {
            $msiInput.Dispose()
        }
    }
}
