#requires -Version 5

# The namespace is content-addressed from this type-tagged, length-prefixed
# stream. Object keys are ordinal-sorted, array order is retained, and only the
# root documentNamespace property is omitted to break the self-reference.
# Do not replace this with ConvertTo-Json: its output differs across PowerShell
# and .NET versions.

function Write-DesktopPetSpdxIdentityString {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryWriter]$Writer,

        [Parameter(Mandatory = $true)]
        [Text.UTF8Encoding]$Encoding,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $bytes = $Encoding.GetBytes($Value)
    $Writer.Write([int]$bytes.Length)
    if ($bytes.Length -gt 0) {
        $Writer.Write($bytes)
    }
}

function Write-DesktopPetSpdxIdentityValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [IO.BinaryWriter]$Writer,

        [Parameter(Mandatory = $true)]
        [Text.UTF8Encoding]$Encoding,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Value,

        [bool]$IsDocumentRoot = $false
    )

    if ($null -eq $Value) {
        $Writer.Write([byte]0)
        return
    }
    if ($Value -is [bool]) {
        $Writer.Write([byte]1)
        $Writer.Write([bool]$Value)
        return
    }
    if ($Value -is [string]) {
        $Writer.Write([byte]2)
        Write-DesktopPetSpdxIdentityString `
            -Writer $Writer `
            -Encoding $Encoding `
            -Value ([string]$Value)
        return
    }
    if ($Value -is [sbyte] -or
        $Value -is [byte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64]) {
        $Writer.Write([byte]3)
        Write-DesktopPetSpdxIdentityString `
            -Writer $Writer `
            -Encoding $Encoding `
            -Value ([Convert]::ToString(
                $Value,
                [Globalization.CultureInfo]::InvariantCulture))
        return
    }
    if ($Value -is [single] -or
        $Value -is [double] -or
        $Value -is [decimal]) {
        throw (
            'SPDX document identity does not admit floating-point JSON ' +
            "numbers; found CLR type '$($Value.GetType().FullName)'."
        )
    }

    if ($Value -is [Collections.IDictionary]) {
        $propertyValues =
            New-Object 'Collections.Generic.Dictionary[string,object]' (
                [StringComparer]::Ordinal)
        foreach ($entry in $Value.GetEnumerator()) {
            if ($entry.Key -isnot [string]) {
                throw 'SPDX document identity object keys must be strings.'
            }
            $propertyValues.Add([string]$entry.Key, $entry.Value)
        }
        $propertyNames = [string[]]@(
            $propertyValues.Keys |
                Where-Object {
                    -not ($IsDocumentRoot -and
                        [string]$_ -ceq 'documentNamespace')
                }
        )
        [Array]::Sort($propertyNames, [StringComparer]::Ordinal)
        $Writer.Write([byte]5)
        $Writer.Write([int]$propertyNames.Length)
        foreach ($propertyName in $propertyNames) {
            Write-DesktopPetSpdxIdentityString `
                -Writer $Writer `
                -Encoding $Encoding `
                -Value $propertyName
            Write-DesktopPetSpdxIdentityValue `
                -Writer $Writer `
                -Encoding $Encoding `
                -Value $propertyValues[$propertyName]
        }
        return
    }

    if ($Value -is [Collections.IEnumerable]) {
        $items = New-Object 'Collections.Generic.List[object]'
        foreach ($item in $Value) {
            $items.Add($item)
        }
        $Writer.Write([byte]4)
        $Writer.Write([int]$items.Count)
        foreach ($item in $items) {
            Write-DesktopPetSpdxIdentityValue `
                -Writer $Writer `
                -Encoding $Encoding `
                -Value $item
        }
        return
    }

    $propertyValues =
        New-Object 'Collections.Generic.Dictionary[string,object]' (
            [StringComparer]::Ordinal)
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.MemberType -cne 'NoteProperty') {
            continue
        }
        $propertyValues.Add([string]$property.Name, $property.Value)
    }
    if ($propertyValues.Count -eq 0) {
        throw (
            'SPDX document identity encountered an unsupported CLR value ' +
            "of type '$($Value.GetType().FullName)'."
        )
    }
    $propertyNames = [string[]]@(
        $propertyValues.Keys |
            Where-Object {
                -not ($IsDocumentRoot -and
                    [string]$_ -ceq 'documentNamespace')
            }
    )
    [Array]::Sort($propertyNames, [StringComparer]::Ordinal)
    $Writer.Write([byte]5)
    $Writer.Write([int]$propertyNames.Length)
    foreach ($propertyName in $propertyNames) {
        Write-DesktopPetSpdxIdentityString `
            -Writer $Writer `
            -Encoding $Encoding `
            -Value $propertyName
        Write-DesktopPetSpdxIdentityValue `
            -Writer $Writer `
            -Encoding $Encoding `
            -Value $propertyValues[$propertyName]
    }
}

function Get-DesktopPetSpdxDocumentIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Document
    )

    $encoding = New-Object Text.UTF8Encoding($false, $true)
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream, $encoding, $true)
    try {
        Write-DesktopPetSpdxIdentityString `
            -Writer $writer `
            -Encoding $encoding `
            -Value 'DesktopPet canonical SPDX document identity v1'
        Write-DesktopPetSpdxIdentityValue `
            -Writer $writer `
            -Encoding $encoding `
            -Value $Document `
            -IsDocumentRoot $true
        $writer.Flush()
        $stream.Position = 0
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString(
                $algorithm.ComputeHash($stream))).
                Replace('-', '').
                ToLowerInvariant()
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
