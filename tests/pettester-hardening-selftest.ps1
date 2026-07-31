#requires -Version 5
[CmdletBinding()]
param(
    [string] $PetTesterExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $testsRoot
if ([string]::IsNullOrWhiteSpace($PetTesterExecutable)) {
    $PetTesterExecutable = Join-Path $repoRoot (
        'build\PetTester\bin\Release\x64\PetTester.exe')
}

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Name
    )

    if (-not $Condition) {
        throw "$Name failed."
    }
    Write-Host "PASS: $Name"
}

function Assert-Matches {
    param(
        [string] $Value,
        [string] $Pattern,
        [string] $Name
    )

    Assert-True ($Value -match $Pattern) $Name
}

function Get-RootException {
    param([Parameter(Mandatory = $true)][Exception] $Exception)

    $current = $Exception
    while ($null -ne $current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

$programSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester\Program.cs') -Raw
$projectSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester\PetTester.csproj') -Raw
$solutionSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester.sln') -Raw
$formSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester\Form1.cs') -Raw
$coordinatorSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester\ValidationLoadCoordinator.cs') -Raw
$selfTestSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'Tools\PetTester\SelfTests.cs') -Raw
$validatorSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\dotNet\PetXmlValidator.cs') -Raw
$lockPath = Join-Path $repoRoot 'Tools\PetTester\packages.lock.json'

Assert-True (
    $programSource -notmatch '\bLocalData\b|Path\.GetTempPath'
) 'PetTester startup has no legacy LocalData or shared-TEMP initialization'
Assert-True (
    $projectSource -notmatch 'LocalData(?:\\|/)LocalData\.csproj'
) 'PetTester project has no LocalData project dependency'
Assert-True (
    $solutionSource -notmatch '(?m)^Project\([^\r\n]+\)\s*=\s*"LocalData"'
) 'PetTester solution excludes LocalData'
Assert-Matches $projectSource (
    'src(?:\\|/)dotNet(?:\\|/)Ai(?:\\|/)AiExecutablePolicy\.cs'
) 'PetTester links the path policy required by the shared pet validator'
Assert-True (
    $validatorSource -match 'OpenRetainedDirectoryChain\(candidate\)' -and
    $validatorSource -match 'FileFlagOpenReparsePoint' -and
    $validatorSource -match 'new RetainedLocalXmlFile\('
) 'shared pet validator retains a reparse-free directory chain and exact file handle'
Assert-True (
    $formSource -match (
        '(?s)private void Form1_DragDrop\(.*?' +
        'TryResolveDroppedPetFile\(\s*files\[0\],\s*' +
        'out canonicalPath,\s*out error\).*?' +
        'OpenXMLFile\(canonicalPath\)') -and
    $formSource -notmatch 'OpenXMLFile\(\s*files\[0\]\s*\)'
) 'drag/drop opens only the canonical path returned by the local-file gate'
Assert-True (
    $formSource -match (
        '(?s)private async void OpenXMLFile\(string fileName\).*?' +
        'TryOpenLocalXmlFile\(\s*fileName,\s*out retained,\s*out pathError\).*?' +
        'retained\.OpenRead\(65536\)') -and
    $formSource -notmatch (
        '(?s)private async void OpenXMLFile\(string fileName\).*?' +
        'new FileStream\(\s*fileName')
) 'PetTester reads local XML only through the retained validated handle'

[xml] $projectXml = $projectSource
$projectNamespace = New-Object Xml.XmlNamespaceManager(
    $projectXml.NameTable)
$projectNamespace.AddNamespace(
    'm',
    'http://schemas.microsoft.com/developer/msbuild/2003')
$directPackages = @(
    $projectXml.SelectNodes('//m:PackageReference', $projectNamespace) |
        ForEach-Object { [string] $_.Include }
)
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$baseTargetProperty = @(
    $lock.dependencies.PSObject.Properties |
        Where-Object { $_.Name -ceq '.NETFramework,Version=v4.8' }
)
Assert-True ($baseTargetProperty.Count -eq 1) (
    'PetTester lock has exactly one base .NET Framework 4.8 graph')
$baseEntries = @($baseTargetProperty[0].Value.PSObject.Properties)
$entriesByName = @{}
foreach ($entry in $baseEntries) {
    $entriesByName[$entry.Name] = $entry.Value
}
$reachable = New-Object 'Collections.Generic.HashSet[string]' (
    [StringComparer]::OrdinalIgnoreCase)
$pending = New-Object 'Collections.Generic.Queue[string]'
foreach ($packageName in $directPackages) {
    Assert-True ($entriesByName.ContainsKey($packageName)) (
        "direct package '$packageName' exists in the PetTester lock")
    Assert-True (
        [string] $entriesByName[$packageName].type -ceq 'Direct'
    ) "direct package '$packageName' is marked Direct in the lock"
    if ($reachable.Add($packageName)) { $pending.Enqueue($packageName) }
}
while ($pending.Count -gt 0) {
    $packageName = $pending.Dequeue()
    $dependenciesProperty =
        $entriesByName[$packageName].PSObject.Properties['dependencies']
    if ($null -eq $dependenciesProperty) { continue }
    foreach ($dependency in @($dependenciesProperty.Value.PSObject.Properties)) {
        Assert-True ($entriesByName.ContainsKey($dependency.Name)) (
            "locked dependency '$($dependency.Name)' is present in the graph")
        if ($reachable.Add($dependency.Name)) {
            $pending.Enqueue($dependency.Name)
        }
    }
}
$unreachableLockEntries = @(
    $baseEntries.Name |
        Where-Object { -not $reachable.Contains([string] $_) } |
        Sort-Object
)
Assert-True ($unreachableLockEntries.Count -eq 0) (
    'every base lock entry is reachable from a current PackageReference; ' +
    "unreachable=$($unreachableLockEntries -join ',')")
$projectLockEntries = @(
    foreach ($target in $lock.dependencies.PSObject.Properties) {
        foreach ($entry in $target.Value.PSObject.Properties) {
            if ([string] $entry.Value.type -ceq 'Project') {
                "$($target.Name):$($entry.Name)"
            }
        }
    }
)
Assert-True ($projectLockEntries.Count -eq 0) (
    'PetTester lock contains no removed project dependencies')

Assert-Matches $formSource (
    'CancellationTokenSource\.CreateLinkedTokenSource\s*\('
) 'download creates a linked request/body cancellation source'
Assert-Matches $formSource (
    'requestCancellation\.CancelAfter\(deadline\)'
) 'download applies an explicit full-operation deadline'
Assert-Matches $formSource (
    'Timeout\s*=\s*Timeout\.InfiniteTimeSpan'
) 'download does not rely on the headers-only HttpClient timeout'
Assert-Matches $formSource (
    'throw new TimeoutException\(\s*"The download timed out'
) 'deadline cancellation has a distinct timeout result'
Assert-Matches $formSource (
    'spawn\.Next == null \|\|\s*' +
    '!animationIds\.Contains\(spawn\.Next\.Value\)'
) 'spawn validation checks its target animation ID'
Assert-Matches $formSource (
    'result\.Succeeded = errors == 0'
) 'animation success is derived from the final error count'
Assert-Matches $formSource (
    'ANIMATION WARNING: This pet defines fewer than 3 animations\.'
) 'small valid animation sets are advisory rather than fatal'
Assert-True (
    $formSource -notmatch
        "ANIMATION ERROR: Please add an animation with.*name 'drag'" -and
    $formSource -notmatch
        "ANIMATION ERROR: Please add an animation with.*name 'kill'"
) 'optional drag and kill magic animations are not treated as errors'
Assert-Matches $formSource (
    'childTransitions\.TryGetValue\(\s*' +
    'child\.Id,\s*out targets\)[\s\S]*?' +
    'targets\.Add\(child\.Next\)'
) 'child transitions are indexed by their parent animation'
Assert-True (
    $formSource -notmatch 'reachable\.Add\(child\.Next\)'
) 'child targets do not seed global reachability'
Assert-Matches $formSource (
    'childTransitions\.TryGetValue\(\s*' +
    'animationId,\s*out childTargets\)[\s\S]*?' +
    'reachable\.Add\(childTarget\)[\s\S]*?' +
    'pending\.Enqueue\(childTarget\)'
) 'reachable parents enqueue their child animation targets'
Assert-Matches $formSource (
    'transition == null \|\| transition\.Probability <= 0'
) 'zero-probability transitions do not contribute to reachability'
Assert-Matches $formSource (
    'catch\s*\(Exception ex\)\s*\{\s*errors\+\+'
) 'animation-analysis exceptions increment the final error count'
Assert-Matches $formSource (
    'checkBox3\.Tag = succeeded \? 2 : 0'
) 'animation failure is reflected in the UI state'
Assert-Matches $formSource (
    'links\+\+'
) 'animation link checks advance progress'
Assert-Matches $formSource (
    'FormatProgress\(checkLinks, totLinks\)'
) 'animation link progress reports checked and total links'
Assert-Matches $formSource (
    'checkBox2\.CheckState = CheckState\.Unchecked;\s*' +
    'checkBox2\.Tag = 0;\s*' +
    'checkBox3\.CheckState = CheckState\.Unchecked;\s*' +
    'checkBox3\.Tag = 0;'
) 'malformed XML clears resource and animation validation state'
Assert-Matches $formSource (
    'ValidationLoadSession session = validationLoads\.Begin\(\)'
) 'each local or remote validation starts a superseding load session'
Assert-Matches $formSource (
    'await Task\.Run\(\s*' +
    '\(\) => BuildValidationResult\(content, session\.Token\),\s*' +
    'session\.Token\)'
) 'parse, image, sprite, and animation validation runs on a cancellable worker'
Assert-Matches $formSource (
    'CanAccessControls\(\) && validationLoads\.IsCurrent\(session\)'
) 'asynchronous results publish only while current and controls are alive'
Assert-Matches $formSource (
    'if \(!CanPublish\(session\)\) return;\s*' +
    'PublishValidationResult\(session, result\)'
) 'worker results are published only after a current-generation check'
Assert-Matches $formSource (
    'FormClosed \+= delegate\s*\{\s*validationLoads\.Dispose\(\)'
) 'form closure cancels the active validation load'
Assert-True (
    $formSource -notmatch '\blifetimeCancellation\b'
) 'PetTester has no form-lifetime-only cancellation path'
Assert-Matches $coordinatorSource (
    'CancelCurrentLocked\(\);\s*generation\+\+'
) 'starting a load cancels the previous load before replacement'
Assert-Matches $coordinatorSource (
    'ReferenceEquals\(current, session\)'
) 'load publication is tied to exact session identity'
Assert-Matches $selfTestSource (
    'TestMalformedAndCompleteValidation'
) 'compiled self-test covers malformed and complete pet validation'
Assert-Matches $selfTestSource (
    'TestUnreachableChildReachability[\s\S]*?' +
    'animation 1000[\s\S]*?' +
    'animation 1001'
) 'compiled self-test covers parent-gated child reachability'
Assert-Matches $selfTestSource (
    'TestZeroProbabilityTransitionReachability[\s\S]*?' +
    'animation 1002'
) 'compiled self-test covers zero-probability transition reachability'
Assert-Matches $selfTestSource (
    'first\.Token\.IsCancellationRequested'
) 'compiled self-test covers overlapping-load cancellation'
Assert-Matches $selfTestSource (
    'TestFormCloseCancellation'
) 'compiled self-test closes a real form with an in-flight validation load'
Assert-Matches $selfTestSource (
    'TestInFlightSupersession'
) 'compiled self-test supersedes an in-flight validation worker'
Assert-Matches $selfTestSource (
    'LastPublishedValidationGeneration == currentGeneration'
) 'compiled self-test rejects stale-generation UI publication'
Assert-Matches $selfTestSource (
    'WaitWithMessagePump'
) 'compiled WinForms self-test pumps asynchronous continuations with a timeout'

if (-not ('DesktopPet.Tests.OneShotHttpServer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DesktopPet.Tests
{
    public sealed class OneShotHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly byte[] body;
        private readonly int bodyDelayMilliseconds;
        private readonly ManualResetEventSlim stopped =
            new ManualResetEventSlim(false);
        private TcpClient acceptedClient;

        public OneShotHttpServer(byte[] body, int bodyDelayMilliseconds)
        {
            if (body == null) throw new ArgumentNullException("body");
            if (bodyDelayMilliseconds < 0)
                throw new ArgumentOutOfRangeException(
                    "bodyDelayMilliseconds");

            this.body = body;
            this.bodyDelayMilliseconds = bodyDelayMilliseconds;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "PetTester one-shot HTTP server";
            thread.Start();
        }

        public int Port { get; private set; }
        public bool Accepted { get; private set; }
        public bool HeadersSent { get; private set; }
        public bool BodySent { get; private set; }
        public string Failure { get; private set; }

        private void Run()
        {
            try
            {
                acceptedClient = listener.AcceptTcpClient();
                Accepted = true;
                using (acceptedClient)
                using (NetworkStream stream = acceptedClient.GetStream())
                {
                    stream.ReadTimeout = 2000;
                    int terminatorState = 0;
                    while (terminatorState < 4)
                    {
                        int value = stream.ReadByte();
                        if (value < 0) return;
                        if ((terminatorState == 0 ||
                             terminatorState == 2) && value == 13)
                        {
                            terminatorState++;
                        }
                        else if ((terminatorState == 1 ||
                                  terminatorState == 3) && value == 10)
                        {
                            terminatorState++;
                        }
                        else
                        {
                            terminatorState = value == 13 ? 1 : 0;
                        }
                    }

                    byte[] headers = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/xml; charset=utf-8\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        "Connection: close\r\n\r\n");
                    stream.Write(headers, 0, headers.Length);
                    stream.Flush();
                    HeadersSent = true;
                    if (bodyDelayMilliseconds > 0 &&
                        stopped.Wait(bodyDelayMilliseconds))
                    {
                        return;
                    }
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                    BodySent = true;
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
            catch (System.IO.IOException)
            {
            }
            catch (Exception ex)
            {
                Failure = ex.ToString();
            }
        }

        public void Dispose()
        {
            stopped.Set();
            listener.Stop();
            TcpClient client = acceptedClient;
            if (client != null) client.Close();
            thread.Join(2000);
            stopped.Dispose();
        }
    }
}
'@
}

$resolvedExecutable = (Resolve-Path -LiteralPath $PetTesterExecutable).Path
$assembly = [Reflection.Assembly]::LoadFrom($resolvedExecutable)
$formType = $assembly.GetType('DesktopPet.Form1', $true)
$validatorType = $assembly.GetType('DesktopPet.PetXmlValidator', $true)
$resolveLocalXmlMethod = @(
    $validatorType.GetMethods(
        [Reflection.BindingFlags]'Public,NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'TryResolveLocalXmlFile' -and
            $_.GetParameters().Count -eq 3
        }
)
Assert-True ($resolveLocalXmlMethod.Count -eq 1) (
    'compiled PetTester exposes one shared local pet-path validator')
$openLocalXmlMethod = @(
    $validatorType.GetMethods(
        [Reflection.BindingFlags]'Public,NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'TryOpenLocalXmlFile' -and
            $_.GetParameters().Count -eq 3
        }
)
Assert-True ($openLocalXmlMethod.Count -eq 1) (
    'compiled PetTester exposes one retained local pet-file admission method')
$resolveDroppedPetMethod = @(
    $formType.GetMethods(
        [Reflection.BindingFlags]'Public,NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'TryResolveDroppedPetFile' -and
            $_.GetParameters().Count -eq 3
        }
)
Assert-True ($resolveDroppedPetMethod.Count -eq 1) (
    'compiled PetTester exposes one drag/drop pet-path gate')
$validatorMethod = @(
    $validatorType.GetMethods(
        [Reflection.BindingFlags]'Public,NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'TryParse' -and
            $_.GetParameters().Count -eq 3
        }
)
Assert-True ($validatorMethod.Count -eq 1) (
    'compiled PetTester exposes one three-argument pet validator')
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$petXmlFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Pets') -Directory |
        ForEach-Object {
            Get-Item -LiteralPath (Join-Path $_.FullName 'animations.xml') `
                -ErrorAction SilentlyContinue
        } |
        Sort-Object FullName
)
Assert-True ($petXmlFiles.Count -eq 22) (
    'bundled pet corpus contains the expected 22 definitions')
foreach ($petXmlFile in $petXmlFiles) {
    $petXml = $strictUtf8.GetString(
        [IO.File]::ReadAllBytes($petXmlFile.FullName))
    if ($petXml.Length -gt 0 -and $petXml[0] -eq [char]0xfeff) {
        $petXml = $petXml.Substring(1)
    }
    $validatorArguments = [object[]]@($petXml, $null, $null)
    $valid = [bool]$validatorMethod[0].Invoke(
        $null,
        $validatorArguments)
    Assert-True $valid (
        "bundled pet validates with strict UTF-8: $($petXmlFile.Directory.Name); " +
        "error=$($validatorArguments[2])")
}

$nekoXmlPath = Join-Path $repoRoot 'Pets\neko\animations.xml'
$resolveArguments = [object[]]@(
    [string]$nekoXmlPath,
    [string]'',
    [string]'')
$resolvedLocalXml = [bool]$resolveDroppedPetMethod[0].Invoke(
    $null,
    $resolveArguments)
Assert-True (
    $resolvedLocalXml -and
    [string]::Equals(
        [IO.Path]::GetFullPath($nekoXmlPath),
        [string]$resolveArguments[1],
        [StringComparison]::OrdinalIgnoreCase)
) 'compiled drag/drop gate accepts and canonicalizes a reparse-free local pet XML'
$resolveArguments = [object[]]@(
    '\\attacker.invalid\share\animations.xml',
    [string]'',
    [string]'')
Assert-True (
    -not [bool]$resolveDroppedPetMethod[0].Invoke($null, $resolveArguments)
) 'compiled drag/drop gate rejects UNC pet paths before filesystem traversal'

$pathGateScratch = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-PetTesterPathGate-' + [Guid]::NewGuid().ToString('N'))
$pathGateJunction = Join-Path $pathGateScratch 'linked-pet'
try {
    [IO.Directory]::CreateDirectory($pathGateScratch) | Out-Null
    New-Item `
        -ItemType Junction `
        -Path $pathGateJunction `
        -Target (Split-Path -Parent $nekoXmlPath) `
        -ErrorAction Stop | Out-Null
    Assert-True (
        ([IO.File]::GetAttributes($pathGateJunction) -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) 'drag/drop path fixture is a real reparse point'

    $resolveArguments = [object[]]@(
        [string](Join-Path $pathGateJunction 'animations.xml'),
        [string]'',
        [string]'')
    Assert-True (
        -not [bool]$resolveDroppedPetMethod[0].Invoke(
            $null,
            $resolveArguments)
    ) 'compiled drag/drop gate rejects a pet XML reached through a junction'
}
finally {
    if ([IO.Directory]::Exists($pathGateJunction)) {
        [IO.Directory]::Delete($pathGateJunction, $false)
    }
    if ([IO.Directory]::Exists($pathGateScratch)) {
        [IO.Directory]::Delete($pathGateScratch, $false)
    }
}

$retainedRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DesktopPet-PetTesterRetained-' + [Guid]::NewGuid().ToString('N'))
$retainedDirectory = Join-Path $retainedRoot 'pet'
$retainedMovedDirectory = Join-Path $retainedRoot 'pet-moved'
$retainedPath = Join-Path $retainedDirectory 'animations.xml'
$retainedLease = $null
$retainedStream = $null
try {
    [IO.Directory]::CreateDirectory($retainedDirectory) | Out-Null
    [IO.File]::WriteAllText(
        $retainedPath,
        '<animations />',
        (New-Object Text.UTF8Encoding($false, $true)))
    $openArguments = [object[]]@(
        [string]$retainedPath,
        $null,
        [string]'')
    $openedRetained = [bool]$openLocalXmlMethod[0].Invoke(
        $null,
        $openArguments)
    $retainedLease = $openArguments[1]
    Assert-True (
        $openedRetained -and $null -ne $retainedLease
    ) 'compiled PetTester opens a retained local pet-file lease'

    $writeBlocked = $false
    try {
        [IO.File]::WriteAllText(
            $retainedPath,
            '<attacker />',
            (New-Object Text.UTF8Encoding($false, $true)))
    }
    catch [IO.IOException] {
        $writeBlocked = $true
    }
    catch [UnauthorizedAccessException] {
        $writeBlocked = $true
    }
    $directorySwapBlocked = $false
    try {
        [IO.Directory]::Move(
            $retainedDirectory,
            $retainedMovedDirectory)
    }
    catch [IO.IOException] {
        $directorySwapBlocked = $true
    }
    catch [UnauthorizedAccessException] {
        $directorySwapBlocked = $true
    }

    $openRead = $retainedLease.GetType().GetMethod(
        'OpenRead',
        [Reflection.BindingFlags]'Instance,NonPublic')
    $retainedStream = $openRead.Invoke(
        $retainedLease,
        [object[]]@(4096))
    $reader = [IO.StreamReader]::new(
        $retainedStream,
        (New-Object Text.UTF8Encoding($false, $true)),
        $true,
        1024)
    try {
        $retainedText = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $retainedStream = $null
    }
    Assert-True (
        $writeBlocked -and
        $directorySwapBlocked -and
        $retainedText -ceq '<animations />'
    ) 'compiled PetTester retains exact XML bytes and directory identity through read'
}
finally {
    if ($null -ne $retainedStream) {
        $retainedStream.Dispose()
    }
    if ($null -ne $retainedLease) {
        $retainedLease.Dispose()
    }
    if ([IO.Directory]::Exists($retainedRoot)) {
        [IO.Directory]::Delete($retainedRoot, $true)
    }
}
[xml] $excessSoundXml = $strictUtf8.GetString(
    [IO.File]::ReadAllBytes($nekoXmlPath))
$namespace = New-Object Xml.XmlNamespaceManager(
    $excessSoundXml.NameTable)
$namespace.AddNamespace('p', 'https://esheep.petrucci.ch/')
$nekoSoundVariants = @(
    $excessSoundXml.SelectNodes(
        '//p:sound[@animationid="2"]/p:probability',
        $namespace)
)
Assert-True ($nekoSoundVariants.Count -eq 3) (
    'sound probability negative control found all repeated neko variants')
foreach ($probability in $nekoSoundVariants) {
    $probability.InnerText = '50'
}
$validatorArguments = [object[]]@(
    $excessSoundXml.OuterXml,
    $null,
    $null)
$valid = [bool]$validatorMethod[0].Invoke(
    $null,
    $validatorArguments)
Assert-True (-not $valid) (
    'validator rejects cumulative sound probability above 100 percent')
Assert-Matches ([string]$validatorArguments[2]) (
    'exceed 100 percent'
) 'sound probability rejection explains the cumulative bound'

[xml] $excessSpawnXml = $strictUtf8.GetString(
    [IO.File]::ReadAllBytes($nekoXmlPath))
$spawnNamespace = New-Object Xml.XmlNamespaceManager(
    $excessSpawnXml.NameTable)
$spawnNamespace.AddNamespace('p', 'https://esheep.petrucci.ch/')
$spawnsElement = $excessSpawnXml.SelectSingleNode(
    '//p:spawns',
    $spawnNamespace)
$spawnTemplate = $excessSpawnXml.SelectSingleNode(
    '//p:spawns/p:spawn[1]',
    $spawnNamespace)
Assert-True ($null -ne $spawnsElement -and $null -ne $spawnTemplate) (
    'spawn-count negative control found the neko spawn collection')
for ($spawnIndex = 2; $spawnIndex -le 256; $spawnIndex++) {
    $spawn = $spawnTemplate.CloneNode($true)
    $spawn.SetAttribute('id', $spawnIndex.ToString(
        [Globalization.CultureInfo]::InvariantCulture))
    $spawn.SetAttribute('probability', '0')
    [void]$spawnsElement.AppendChild($spawn)
}
$validatorArguments = [object[]]@(
    $excessSpawnXml.OuterXml,
    $null,
    $null)
$valid = [bool]$validatorMethod[0].Invoke(
    $null,
    $validatorArguments)
Assert-True $valid (
    'validator accepts exactly 256 bounded spawn entries')

$spawn = $spawnTemplate.CloneNode($true)
$spawn.SetAttribute('id', '257')
$spawn.SetAttribute('probability', '0')
[void]$spawnsElement.AppendChild($spawn)
$validatorArguments = [object[]]@(
    $excessSpawnXml.OuterXml,
    $null,
    $null)
$valid = [bool]$validatorMethod[0].Invoke(
    $null,
    $validatorArguments)
Assert-True (-not $valid) (
    'validator rejects a pet with more than 256 spawn entries')
Assert-Matches ([string]$validatorArguments[2]) (
    'too many spawn entries'
) 'spawn-count rejection explains the collection bound'

[xml] $pngIconXml = $strictUtf8.GetString(
    [IO.File]::ReadAllBytes($nekoXmlPath))
$pngIconNamespace = New-Object Xml.XmlNamespaceManager(
    $pngIconXml.NameTable)
$pngIconNamespace.AddNamespace('p', 'https://esheep.petrucci.ch/')
$pngIconNode = $pngIconXml.SelectSingleNode(
    '//p:header/p:icon',
    $pngIconNamespace)
Assert-True ($null -ne $pngIconNode) (
    'icon-container negative control found the neko icon')
$pngIconNode.InnerText =
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+P+/HgAFhAJ/wlseKgAAAABJRU5ErkJggg=='
$validatorArguments = [object[]]@(
    $pngIconXml.OuterXml,
    $null,
    $null)
$valid = [bool]$validatorMethod[0].Invoke(
    $null,
    $validatorArguments)
Assert-True (-not $valid) (
    'shared validator rejects a standalone PNG that the tray Icon loader cannot use')
Assert-Matches ([string]$validatorArguments[2]) (
    'must be an ICO image'
) 'icon-container rejection explains the runtime-compatible format'

$animationsType = $assembly.GetType('DesktopPet.Animations', $true)
$soundType = $assembly.GetType('DesktopPet.TSound', $true)
$selectSoundMethod = $animationsType.GetMethod(
    'SelectSoundForRoll',
    [Reflection.BindingFlags]'NonPublic,Static')
Assert-True ($null -ne $selectSoundMethod) (
    'compiled runtime exposes the deterministic sound-selection seam')
$soundListType = [Collections.Generic.List``1].MakeGenericType($soundType)
$soundVariants = [Activator]::CreateInstance($soundListType)
$firstSound = [Activator]::CreateInstance($soundType)
$secondSound = [Activator]::CreateInstance($soundType)
$firstSound.Probability = 20
$secondSound.Probability = 30
$soundVariants.Add($firstSound)
$soundVariants.Add($secondSound)
try {
    Assert-True ([object]::ReferenceEquals(
        $firstSound,
        $selectSoundMethod.Invoke(
            $null,
            [object[]]@($soundVariants, 19)))) (
        'first sound owns its complete cumulative roll range')
    Assert-True ([object]::ReferenceEquals(
        $secondSound,
        $selectSoundMethod.Invoke(
            $null,
            [object[]]@($soundVariants, 20)))) (
        'second sound starts immediately after the first range')
    Assert-True ($null -eq $selectSoundMethod.Invoke(
        $null,
        [object[]]@($soundVariants, 50))) (
        'unused cumulative probability produces no sound')
}
finally {
    $firstSound.Dispose()
    $secondSound.Dispose()
}
$downloadMethod = $formType.GetMethod(
    'DownloadPetXmlAsync',
    [Reflection.BindingFlags]'NonPublic,Static')
Assert-True ($null -ne $downloadMethod) (
    'compiled PetTester exposes the bounded download helper')
Assert-True (
    @($assembly.GetReferencedAssemblies().Name) -notcontains 'LocalData'
) 'compiled PetTester has no LocalData assembly reference'
$formatProgress = $formType.GetMethod(
    'FormatProgress',
    [Reflection.BindingFlags]'NonPublic,Static')
Assert-True (
    [string]$formatProgress.Invoke(
        $null,
        [object[]]@(3, 5)) -eq '3 / 5 (60%)'
) 'compiled animation progress accounts for checked and total links'

$successBody = [Text.Encoding]::UTF8.GetBytes('<pet />')
$successServer = [DesktopPet.Tests.OneShotHttpServer]::new(
    $successBody,
    0)
try {
    $successUri = [Uri]::new(
        "http://127.0.0.1:$($successServer.Port)/pet")
    $successTask = $downloadMethod.Invoke(
        $null,
        [object[]]@(
            $successUri,
            [TimeSpan]::FromSeconds(2),
            [Threading.CancellationToken]::None
        ))
    try {
        $successResult = $successTask.GetAwaiter().GetResult()
    }
    catch {
        $failureFormat =
            "Complete loopback download failed. Accepted={0}; " +
            "HeadersSent={1}; BodySent={2}; ServerFailure={3}; Error={4}"
        throw ($failureFormat -f
            $successServer.Accepted,
            $successServer.HeadersSent,
            $successServer.BodySent,
            $successServer.Failure,
            $_.Exception.Message)
    }
    Assert-True ($successResult -eq '<pet />') (
        'compiled download reads a complete loopback response body')
}
finally {
    $successServer.Dispose()
}

$slowServer = [DesktopPet.Tests.OneShotHttpServer]::new(
    $successBody,
    5000)
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    $slowUri = [Uri]::new(
        "http://127.0.0.1:$($slowServer.Port)/slow-pet")
    try {
        $slowTask = $downloadMethod.Invoke(
            $null,
            [object[]]@(
                $slowUri,
                [TimeSpan]::FromMilliseconds(250),
                [Threading.CancellationToken]::None
            ))
        $slowTask.GetAwaiter().GetResult() | Out-Null
        throw 'The stalled response body completed without timing out.'
    }
    catch {
        $rootException = Get-RootException $_.Exception
        Assert-True ($rootException -is [TimeoutException]) (
            'stalled response body raises TimeoutException')
    }
}
finally {
    $stopwatch.Stop()
    $slowServer.Dispose()
}
Assert-True ($stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(3)) (
    'stalled body deadline is enforced promptly')

$callerCancellation = New-Object Threading.CancellationTokenSource
try {
    $callerCancellation.Cancel()
    try {
        $cancelledTask = $downloadMethod.Invoke(
            $null,
            [object[]]@(
                [Uri]'http://127.0.0.1:9/cancelled',
                [TimeSpan]::FromSeconds(2),
                $callerCancellation.Token
            ))
        $cancelledTask.GetAwaiter().GetResult() | Out-Null
        throw 'A caller-cancelled download completed unexpectedly.'
    }
    catch {
        $rootException = Get-RootException $_.Exception
        Assert-True (
            $rootException -is [OperationCanceledException] -and
            $rootException -isnot [TimeoutException]
        ) 'caller cancellation remains distinct from deadline timeout'
    }
}
finally {
    $callerCancellation.Dispose()
}

$defaultPetXml = (Resolve-Path -LiteralPath (
    Join-Path $repoRoot 'src\Resources\animations.xml')).Path
$selfTestStartInfo = New-Object Diagnostics.ProcessStartInfo
$selfTestStartInfo.FileName = $resolvedExecutable
$selfTestStartInfo.Arguments =
    '--self-test "' + $defaultPetXml.Replace('"', '""') + '"'
$selfTestStartInfo.UseShellExecute = $false
$selfTestStartInfo.CreateNoWindow = $true
$selfTestStartInfo.RedirectStandardOutput = $true
$selfTestStartInfo.RedirectStandardError = $true
$selfTestProcess = New-Object Diagnostics.Process
$selfTestProcess.StartInfo = $selfTestStartInfo
try {
    Assert-True $selfTestProcess.Start() (
        'compiled PetTester self-test starts')
    $selfTestOutputTask = $selfTestProcess.StandardOutput.ReadToEndAsync()
    $selfTestErrorTask = $selfTestProcess.StandardError.ReadToEndAsync()
    if (-not $selfTestProcess.WaitForExit(120000)) {
        $selfTestProcess.Kill()
        throw 'Compiled PetTester self-test timed out.'
    }
    $selfTestProcess.WaitForExit()
    $selfTestOutput = $selfTestOutputTask.GetAwaiter().GetResult()
    $selfTestError = $selfTestErrorTask.GetAwaiter().GetResult()
    Assert-True ($selfTestProcess.ExitCode -eq 0) (
        "compiled PetTester self-test exits successfully; stderr=$selfTestError")
    Assert-Matches $selfTestOutput (
        'PASS: PetTester runtime self-test'
    ) 'compiled self-test completes full validation coverage'
}
finally {
    $selfTestProcess.Dispose()
}

Write-Host 'PASS: focused PetTester hardening regression harness.'
