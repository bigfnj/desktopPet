# Focused lifetime test for PetTypeRegistry (the loaded-pet-type refcount store behind the
# multiple-pets-on-screen feature). Builds real Xml/Animations by reflection -- the same way
# runtime-hardening-selftest.ps1 does -- and asserts refcount, registry membership,
# dispose-exactly-at-zero, idempotent double-Decrement, DropIfUnused, and DisposeAll. Runs under
# Windows PowerShell 5.1 against the shipped net48 assembly. Throws (non-zero) on any failure.
param(
    [string]$ExecutablePath = 'build\DesktopPetPortable\bin\Release\x64\DesktopPet.exe'
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $ExecutablePath).Path
$asm = [System.Reflection.Assembly]::LoadFrom($exe)
$NI  = [System.Reflection.BindingFlags]'NonPublic,Instance'
$xmlT  = $asm.GetType('DesktopPet.Xml', $true)
$animT = $asm.GetType('DesktopPet.Animations', $true)
$regT  = $asm.GetType('DesktopPet.PetTypeRegistry', $true)
$dispXml  = $xmlT.GetField('disposed', $NI)
$dispAnim = $animT.GetField('disposed', $NI)

function New-Pair([int]$scale) {
    $x = [Activator]::CreateInstance($xmlT, [object[]]@($scale))
    $a = [Activator]::CreateInstance($animT, [object[]]@($x))
    return @{ Xml = $x; Anim = $a }
}
$fail = 0
function Check($cond, $name) {
    if ($cond) { Write-Output "PASS: $name" } else { Write-Output "FAIL: $name"; $script:fail++ }
}

$reg = [Activator]::CreateInstance($regT)
$mAdd = $regT.GetMethod('Add', $NI); $mInc = $regT.GetMethod('Increment', $NI)
$mDec = $regT.GetMethod('Decrement', $NI); $mTry = $regT.GetMethod('TryGet', $NI)
$mDrop = $regT.GetMethod('DropIfUnused', $NI); $mDisposeAll = $regT.GetMethod('DisposeAll', $NI)
function Reg-Has($id) { return [bool]($mTry.Invoke($reg, [object[]]@($id, $null))) }

# lifecycle: Add -> Inc x2 -> Dec x2 -> disposed exactly at zero
$p1 = New-Pair 1
$entry = $mAdd.Invoke($reg, [object[]]@('pink_sheep', $p1.Xml, $p1.Anim))
Check ($entry.RefCount -eq 0) 'Add starts at refcount 0'
Check (Reg-Has 'pink_sheep') 'entry is registered'
$mInc.Invoke($reg, [object[]]@($entry)) | Out-Null
$mInc.Invoke($reg, [object[]]@($entry)) | Out-Null
Check ($entry.RefCount -eq 2) 'two Increments -> refcount 2'
$mDec.Invoke($reg, [object[]]@($entry)) | Out-Null
Check ($entry.RefCount -eq 1 -and (Reg-Has 'pink_sheep')) 'one Decrement -> still alive at 1'
Check (-not [bool]$dispXml.GetValue($p1.Xml)) 'pair NOT disposed while refcount > 0'
$mDec.Invoke($reg, [object[]]@($entry)) | Out-Null
Check (-not (Reg-Has 'pink_sheep')) 'removed from registry at refcount 0'
Check ([bool]$dispXml.GetValue($p1.Xml) -and [bool]$dispAnim.GetValue($p1.Anim)) 'Xml+Animations disposed exactly at zero'

# idempotent double-Decrement past zero
$threw = $false
try { $mDec.Invoke($reg, [object[]]@($entry)) | Out-Null } catch { $threw = $true }
Check (-not $threw) 'double-Decrement past zero is safe'

# DropIfUnused disposes a staged-but-never-spawned type, and spares one in use
$p2 = New-Pair 2
$e2 = $mAdd.Invoke($reg, [object[]]@('red_sheep', $p2.Xml, $p2.Anim))
$mDrop.Invoke($reg, [object[]]@($e2)) | Out-Null
Check ((-not (Reg-Has 'red_sheep')) -and [bool]$dispXml.GetValue($p2.Xml)) 'DropIfUnused disposes an unspawned type'
$p3 = New-Pair 1
$e3 = $mAdd.Invoke($reg, [object[]]@('blue_sheep', $p3.Xml, $p3.Anim))
$mInc.Invoke($reg, [object[]]@($e3)) | Out-Null
$mDrop.Invoke($reg, [object[]]@($e3)) | Out-Null
Check ((Reg-Has 'blue_sheep') -and -not [bool]$dispXml.GetValue($p3.Xml)) 'DropIfUnused leaves an in-use type alone'

# DisposeAll cleans remaining entries
$mDisposeAll.Invoke($reg, @()) | Out-Null
Check ((-not (Reg-Has 'blue_sheep')) -and [bool]$dispXml.GetValue($p3.Xml)) 'DisposeAll disposes remaining pairs'

if ($fail -ne 0) { throw "PetTypeRegistry self-test: $fail failure(s)." }
Write-Output "PASS: PetTypeRegistry lifetime self-test."
