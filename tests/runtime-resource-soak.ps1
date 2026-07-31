#requires -Version 5
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [ValidateRange(1, 30)][int]$EnabledStartupDeadlineSeconds = 10,
    [ValidateRange(1, 120)][int]$StabilizationSeconds = 10,
    [ValidateRange(10, 600)][int]$DurationSeconds = 30,
    [ValidateRange(10, 300)][int]$CompletionGraceSeconds = 60,
    [ValidateRange(250, 10000)][int]$SampleIntervalMilliseconds = 1000,
    [ValidateRange(0, 1000)][int]$MaximumHandleGrowth = 16,
    [ValidateRange(0, 1000)][int]$MaximumGdiGrowth = 16,
    [ValidateRange(0, 1000)][int]$MaximumUserGrowth = 16,
    [ValidateRange(0, 1073741824)][long]$MaximumPrivateByteGrowth = 64MB
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot (
        'build\DesktopPetPortable\bin\Release\x64\DesktopPet.exe')
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratchRoot = Join-Path $resolvedTemp (
    'DesktopPet-ResourceSoak-' + [Guid]::NewGuid().ToString('N'))
$churnMarker = Join-Path $scratchRoot 'resource-churn-result.json'
$churnTargetSeconds = $StabilizationSeconds + $DurationSeconds
$churnIntervalMilliseconds = 250
$churnCycles = [Math]::Max(
    12,
    [Math]::Min(100, [Math]::Ceiling($DurationSeconds / 2)))
$churnMinimumDurationMilliseconds = $churnTargetSeconds * 1000
$churnExitDelayMilliseconds = [Math]::Min(
    30000,
    [Math]::Max(5000, ($SampleIntervalMilliseconds * 2) + 1000))
$originalDataRoot = $env:DESKTOPPET_DATA_ROOT
$process = $null
$startupProcess = $null
$samples = New-Object 'Collections.Generic.List[object]'

if (-not ('DesktopPet.ResourceSoak.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DesktopPet.ResourceSoak
{
    public static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
'@
}

function Stop-TestProcess {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    try {
        if ($Process.HasExited) { return }
    }
    catch {
        return
    }

    try {
        [void]$Process.CloseMainWindow()
        if ($Process.WaitForExit(2000)) { return }
    }
    catch {
    }

    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $Process.Id /T /F 2>&1 | Out-Null
        try { [void]$Process.WaitForExit(5000) } catch { }
    }
    else {
        try {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
        catch {
        }
    }
}

function Get-ResourceSample {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    $Process.Refresh()
    if ($Process.HasExited) {
        throw "DesktopPet exited during the resource soak with code $($Process.ExitCode)."
    }

    return [pscustomobject][ordered]@{
        ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        Handles = $Process.HandleCount
        GdiObjects = [DesktopPet.ResourceSoak.NativeMethods]::GetGuiResources(
            $Process.Handle, 0)
        UserObjects = [DesktopPet.ResourceSoak.NativeMethods]::GetGuiResources(
            $Process.Handle, 1)
        PrivateBytes = $Process.PrivateMemorySize64
        WorkingSet = $Process.WorkingSet64
    }
}

try {
    $runningCopies = @(
        Get-Process -Name 'DesktopPet' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $resolvedExecutable,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    $false
                }
            }
    )
    if ($runningCopies.Count -gt 0) {
        throw 'Refusing the resource soak because this DesktopPet executable is already running.'
    }

    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
    $settings = [ordered]@{
        SchemaVersion = 1
        SmartFortunes = $true
        AiBrainEnabled = $false
        HotkeyEnabled = $false
        IdleCommentaryEnabled = $false
        AutoStartServer = $false
        WarmUpOnLaunch = $false
    }
    [IO.File]::WriteAllText(
        (Join-Path $scratchRoot 'ai-settings.json'),
        ($settings | ConvertTo-Json),
        (New-Object Text.UTF8Encoding($false)))

    $env:DESKTOPPET_DATA_ROOT = $scratchRoot
    $startupInfo = New-Object Diagnostics.ProcessStartInfo
    $startupInfo.FileName = $resolvedExecutable
    $startupInfo.WorkingDirectory = Split-Path $resolvedExecutable -Parent
    $startupInfo.UseShellExecute = $false
    $startupInfo.CreateNoWindow = $true
    $startupInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startupProcess = New-Object Diagnostics.Process
    $startupProcess.StartInfo = $startupInfo
    if (-not $startupProcess.Start()) {
        throw 'The SmartFortunes-enabled startup process could not be started.'
    }
    if (-not $startupProcess.WaitForInputIdle(
            $EnabledStartupDeadlineSeconds * 1000)) {
        throw "SmartFortunes-enabled startup did not enter a responsive message loop within $EnabledStartupDeadlineSeconds seconds."
    }
    if ($startupProcess.HasExited) {
        throw "DesktopPet exited during SmartFortunes-enabled startup with code $($startupProcess.ExitCode)."
    }
    Stop-TestProcess -Process $startupProcess
    $startupProcess.Dispose()
    $startupProcess = $null

    $settings.SmartFortunes = $false
    [IO.File]::WriteAllText(
        (Join-Path $scratchRoot 'ai-settings.json'),
        ($settings | ConvertTo-Json),
        (New-Object Text.UTF8Encoding($false)))

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $resolvedExecutable
    $startInfo.Arguments = '--resource-churn-selftest'
    $startInfo.WorkingDirectory = Split-Path $resolvedExecutable -Parent
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.EnvironmentVariables['DESKTOPPET_DATA_ROOT'] = $scratchRoot
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_CYCLES'] =
        [string]$churnCycles
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_INTERVAL_MS'] =
        [string]$churnIntervalMilliseconds
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_MIN_DURATION_MS'] =
        [string]$churnMinimumDurationMilliseconds
    $startInfo.EnvironmentVariables['DESKTOPPET_RESOURCE_CHURN_EXIT_DELAY_MS'] =
        [string]$churnExitDelayMilliseconds
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'The isolated DesktopPet process could not be started.'
    }
    $env:DESKTOPPET_DATA_ROOT = $originalDataRoot

    if ($process.WaitForExit($StabilizationSeconds * 1000)) {
        throw "DesktopPet exited during stabilization with code $($process.ExitCode)."
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $completionDeadline =
        ($DurationSeconds + $CompletionGraceSeconds) * 1000
    while ($stopwatch.ElapsedMilliseconds -lt $completionDeadline) {
        $samples.Add((Get-ResourceSample -Process $process))
        if (Test-Path -LiteralPath $churnMarker -PathType Leaf) {
            break
        }
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
    }
    if (-not (Test-Path -LiteralPath $churnMarker -PathType Leaf)) {
        throw "The dynamic resource churn did not publish its completion marker after $($stopwatch.Elapsed.TotalSeconds.ToString('F1')) seconds."
    }
    if (-not $process.HasExited) {
        $samples.Add((Get-ResourceSample -Process $process))
    }

    $churn = Get-Content -LiteralPath $churnMarker -Raw |
        ConvertFrom-Json
    $requiredCounters = @(
        'speechAndPetCycles',
        'optionsCycles',
        'optionsCancellationCycles',
        'aboutCycles',
        'helpCycles',
        'trayAndMenuCycles'
    )
    $actualChurnCycles = [int]$churn.cycles
    if ($churn.result -ne 'PASS' -or
        $actualChurnCycles -lt $churnCycles -or
        [int]$churn.targetCycles -ne $churnCycles -or
        [long]$churn.elapsedMilliseconds -lt
            $churnMinimumDurationMilliseconds -or
        [long]$churn.minimumDurationMilliseconds -ne
            $churnMinimumDurationMilliseconds) {
        throw "The dynamic resource churn marker reported failure: $($churn | ConvertTo-Json -Compress)."
    }
    foreach ($counter in $requiredCounters) {
        if ([int]$churn.$counter -ne $actualChurnCycles) {
            throw "The dynamic resource churn counter '$counter' was $($churn.$counter), expected $actualChurnCycles."
        }
    }

    if (-not $process.WaitForExit($churnExitDelayMilliseconds + 5000)) {
        throw 'The dynamic resource churn did not exit after publishing its completion marker.'
    }
    if ($process.ExitCode -ne 0) {
        throw "The dynamic resource churn exited with code $($process.ExitCode)."
    }
    $stopwatch.Stop()

    if ($samples.Count -lt 2) {
        throw 'The resource soak produced too few samples.'
    }
    $first = $samples[0]
    $last = $samples[$samples.Count - 1]
    $sampledDurationMilliseconds =
        [long]$last.ElapsedMilliseconds -
        [long]$first.ElapsedMilliseconds
    if ($sampledDurationMilliseconds -lt ($DurationSeconds * 1000)) {
        throw "Dynamic resource churn was sampled for only $sampledDurationMilliseconds ms; expected at least $($DurationSeconds * 1000) ms."
    }
    $growth = [pscustomobject][ordered]@{
        Handles = [long]$last.Handles - [long]$first.Handles
        GdiObjects = [long]$last.GdiObjects - [long]$first.GdiObjects
        UserObjects = [long]$last.UserObjects - [long]$first.UserObjects
        PrivateBytes = [long]$last.PrivateBytes - [long]$first.PrivateBytes
        WorkingSet = [long]$last.WorkingSet - [long]$first.WorkingSet
    }

    if ($first.GdiObjects -lt 1 -or $first.UserObjects -lt 1) {
        throw 'Windows GUI resource counters were unavailable for the running process.'
    }
    if ($growth.Handles -gt $MaximumHandleGrowth) {
        throw "Handle growth exceeded the bound: $($growth.Handles) > $MaximumHandleGrowth."
    }
    if ($growth.GdiObjects -gt $MaximumGdiGrowth) {
        throw "GDI object growth exceeded the bound: $($growth.GdiObjects) > $MaximumGdiGrowth."
    }
    if ($growth.UserObjects -gt $MaximumUserGrowth) {
        throw "USER object growth exceeded the bound: $($growth.UserObjects) > $MaximumUserGrowth."
    }
    if ($growth.PrivateBytes -gt $MaximumPrivateByteGrowth) {
        throw "Private-byte growth exceeded the bound: $($growth.PrivateBytes) > $MaximumPrivateByteGrowth."
    }

    [pscustomobject][ordered]@{
        Result = 'PASS'
        SmartFortunesEnabledStartup = 'PASS'
        DynamicResourceChurn = $churn
        Samples = $samples.Count
        DurationSeconds = [Math]::Round(
            $sampledDurationMilliseconds / 1000,
            1)
        First = $first
        Last = $last
        Growth = $growth
    } | ConvertTo-Json -Depth 5
}
finally {
    $env:DESKTOPPET_DATA_ROOT = $originalDataRoot
    if ($null -ne $process) {
        Stop-TestProcess -Process $process
        $process.Dispose()
    }
    if ($null -ne $startupProcess) {
        Stop-TestProcess -Process $startupProcess
        $startupProcess.Dispose()
    }

    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    if ($resolvedScratch.StartsWith(
            $resolvedTemp + '\DesktopPet-ResourceSoak-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScratch)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
