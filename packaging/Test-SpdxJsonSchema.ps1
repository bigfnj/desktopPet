#requires -Version 5
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [string]$PythonPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = [Environment]::GetEnvironmentVariable(
        'DESKTOPPET_SBOM_PYTHON')
}
if ([string]::IsNullOrWhiteSpace($PythonPath) -and
    -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $toolboxPython = Join-Path $env:LOCALAPPDATA (
        'DevToolbox\python\.venv\Scripts\python.exe')
    if (Test-Path -LiteralPath $toolboxPython -PathType Leaf) {
        $PythonPath = $toolboxPython
    }
}
if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $command = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $command = Get-Command python -ErrorAction SilentlyContinue
    }
    if ($null -ne $command) {
        $PythonPath = $command.Source
    }
}
if ([string]::IsNullOrWhiteSpace($PythonPath) -or
    -not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw (
        'A Python interpreter with the repository-pinned SBOM validation ' +
        'requirements is required. Set DESKTOPPET_SBOM_PYTHON explicitly.'
    )
}

$resolvedSbom = (Resolve-Path -LiteralPath $SbomPath).Path
$validator = Join-Path $PSScriptRoot 'Test-SpdxJsonSchema.py'
$schema = Join-Path $PSScriptRoot 'spdx-2.3.schema.json.gz.base64'
& $PythonPath $validator $resolvedSbom --schema $schema
if ($LASTEXITCODE -ne 0) {
    throw "Official SPDX 2.3 JSON schema validation failed (exit $LASTEXITCODE)."
}
