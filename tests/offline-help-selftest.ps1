#requires -Version 5
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
$sourcePath = Join-Path $repoRoot 'src\Portable\FormHelp.cs'
$designerPath = Join-Path $repoRoot 'src\Portable\FormHelp.designer.cs'
$privacyPath = Join-Path $repoRoot 'PRIVACY.md'
$source = Get-Content -LiteralPath $sourcePath -Raw
$designer = Get-Content -LiteralPath $designerPath -Raw
$privacy = Get-Content -LiteralPath $privacyPath -Raw

if (($source + $designer) -match '\bWebBrowser\b|http://') {
    throw 'Help still embeds the legacy browser or a plain-HTTP destination.'
}
if ($source -notmatch
    'https://github\.com/bigfnj/desktopPet#readme' -or
    $source -notmatch 'UseShellExecute\s*=\s*true' -or
    $source -notmatch 'UriSchemeHttps' -or
    $source -notmatch '"/bigfnj/desktopPet"') {
    throw 'Help does not use an explicit HTTPS handoff to the default browser.'
}
if ($designer -notmatch '\bRichTextBox\b' -or
    $designer -notmatch 'ReadOnly\s*=\s*true' -or
    $designer -notmatch 'DetectUrls\s*=\s*true' -or
    $designer -notmatch '\.LinkClicked\s*\+=') {
    throw 'Help does not contain a read-only local help surface.'
}
foreach ($directDocument in @(
        'PRIVACY.md',
        'SUPPORT.md',
        'SECURITY.md',
        'grimoire/03-pet-xml-format.md',
        'packs/README.md',
        'docs/RELEASE-CHECKLIST.md')) {
    if (-not $source.Contains(
            'https://github.com/bigfnj/desktopPet/blob/master/' +
            $directDocument)) {
        throw "Help lacks a direct HTTPS link to $directDocument."
    }
}
if (-not $source.Contains(
        'Right-click the pet to poke it; right-click the tray icon') -or
    $source.Contains(
        'Right-click the pet for actions, options, and Exit.')) {
    throw 'Help describes the pet and tray-icon interactions incorrectly.'
}
if ($privacy -notmatch 'Help window itself is fully local' -or
    $privacy -notmatch 'online-documentation link') {
    throw 'Privacy notice does not describe Help network behavior.'
}

Write-Host 'PASS: Help is local-first with explicit HTTPS-only browser handoff.'
