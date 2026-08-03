#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$ReferenceRoot,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StagingPathSafety.ps1')

foreach ($path in @($ZipPath, $MsiPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Package not found: $path"
    }
}
if (-not (Test-Path -LiteralPath $ReferenceRoot -PathType Container)) {
    throw "Reference runtime directory not found: $ReferenceRoot"
}

$absoluteZip = (Resolve-Path -LiteralPath $ZipPath).Path
$absoluteMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$absoluteReference = (Resolve-Path -LiteralPath $ReferenceRoot).Path
$runtimeManifest = Join-Path $PSScriptRoot 'runtime-files.txt'
$msiTableVerifier = Join-Path $PSScriptRoot 'Test-MsiPayloadTable.ps1'
$msiInput = $null
try {
$msiInput = Open-DesktopPetValidatedInputFile `
    -Path $absoluteMsi `
    -Root (Split-Path -Parent $absoluteMsi)
foreach ($requiredVerifierInput in @($runtimeManifest, $msiTableVerifier)) {
    if (-not (Test-Path -LiteralPath $requiredVerifierInput -PathType Leaf)) {
        throw "Packaged-payload verifier input is missing: $requiredVerifierInput"
    }
}

# Validate every authored MSI File row before administrative extraction. Merely
# locating DesktopPet.exe in the extracted image would miss files authored into
# a sibling Directory subtree.
& $msiTableVerifier `
    -MsiPath $absoluteMsi `
    -ManifestPath $runtimeManifest

$msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
if (-not [IO.Path]::IsPathRooted($msiExec) -or
    -not (Test-Path -LiteralPath $msiExec -PathType Leaf)) {
    throw "The absolute Windows Installer executable was not found: $msiExec"
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

$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-PayloadTest-' + [Guid]::NewGuid().ToString('N'))
$zipRoot = Join-Path $scratchRoot 'zip'
$msiRoot = Join-Path $scratchRoot 'msi'
$msiLog = Join-Path $scratchRoot 'administrative-extract.log'

try {
    New-Item -ItemType Directory -Path $zipRoot, $msiRoot -Force | Out-Null

    Expand-Archive -LiteralPath $absoluteZip -DestinationPath $zipRoot
    $portableMarker = Join-Path $zipRoot 'DesktopPet.portable'
    if (-not (Test-Path -LiteralPath $portableMarker -PathType Leaf)) {
        throw 'Portable ZIP is missing DesktopPet.portable.'
    }
    $markerSource = Join-Path $PSScriptRoot 'DesktopPet.portable'
    if ((Get-FileHash -LiteralPath $portableMarker -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $markerSource -Algorithm SHA256).Hash) {
        throw 'Portable ZIP marker differs from its canonical package source.'
    }
    & (Join-Path $PSScriptRoot 'Test-RuntimePayload.ps1') `
        -PayloadRoot $zipRoot `
        -ReferenceRoot $absoluteReference `
        -AllowedExtraFiles @('DesktopPet.portable') `
        -AllowedExtraDirectories @('pets', 'fortunes')

    $arguments = "/a `"$absoluteMsi`" /qn /norestart TARGETDIR=`"$msiRoot`" /l*v `"$msiLog`""
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $msiExec
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'packaged-payload-before-msiexec-start' `
            -Path $absoluteMsi
        if (-not $process.Start()) {
            throw 'MSI administrative extraction could not be started.'
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessTree -Process $process
            throw "MSI administrative extraction timed out after $TimeoutSeconds seconds. Log: $msiLog"
        }
        $process.WaitForExit()
        $rawExitCode = $process.ExitCode
        if ($null -eq $rawExitCode) {
            throw "MSI administrative extraction returned no observable exit code. Log: $msiLog"
        }
        $exitCode = [int]$rawExitCode
        if ($exitCode -notin @(0, 3010)) {
            throw "MSI administrative extraction failed (exit $exitCode). Log: $msiLog"
        }
    }
    finally {
        $process.Dispose()
    }

    $installedExecutables = @(
        Get-ChildItem -LiteralPath $msiRoot -Filter 'DesktopPet.exe' -File -Recurse
    )
    if ($installedExecutables.Count -ne 1) {
        throw "Expected one DesktopPet.exe in the MSI administrative image; found $($installedExecutables.Count)."
    }
    $msiPayloadRoot = Split-Path -Parent $installedExecutables[0].FullName
    & (Join-Path $PSScriptRoot 'Test-RuntimePayload.ps1') `
        -PayloadRoot $msiPayloadRoot `
        -ReferenceRoot $absoluteReference

    Write-Host 'ZIP and MSI contain the exact canonical runtime payload.' -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $msiLog -PathType Leaf) {
        Write-Warning "Tail of ${msiLog}:"
        Get-Content -LiteralPath $msiLog -Tail 80 | Write-Warning
    }
    throw
}
finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-PayloadTest-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
}
finally {
    if ($null -ne $msiInput) {
        $msiInput.Dispose()
    }
}
