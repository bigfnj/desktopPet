#requires -Version 5

# Shared filesystem helpers for packaging staging work.
#
# This is the plain-PowerShell packaging toolkit for an unsigned hobby ZIP+MSI.
# It keeps the function names, parameters, and observable contracts the packaging
# scripts depend on (validated input handles, sealed staged files, atomic-enough
# publish, scratch directories, safe deletes) while doing ordinary file I/O.

# ----------------------------------------------------------------------------
# Lightweight file handles. These implement IDisposable so callers can collect
# them in a List[IDisposable] exactly like the previous native leases.
# ----------------------------------------------------------------------------

class DesktopPetPackagingHashUtil {
    static [System.Security.Cryptography.HashAlgorithm] Create([string]$algorithm) {
        switch ($algorithm.ToUpperInvariant()) {
            'SHA1' { return [System.Security.Cryptography.SHA1]::Create() }
            'SHA256' { return [System.Security.Cryptography.SHA256]::Create() }
            'SHA512' { return [System.Security.Cryptography.SHA512]::Create() }
        }
        throw "Unsupported packaging hash algorithm: $algorithm"
    }

    static [string] HashFile([string]$path, [string]$algorithm) {
        $algo = [DesktopPetPackagingHashUtil]::Create($algorithm)
        try {
            $stream = [System.IO.File]::Open(
                $path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            try {
                return [BitConverter]::ToString($algo.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $algo.Dispose()
        }
    }

    static [string] HashBytes([byte[]]$bytes, [string]$algorithm) {
        $algo = [DesktopPetPackagingHashUtil]::Create($algorithm)
        try {
            return [BitConverter]::ToString($algo.ComputeHash($bytes)).Replace('-', '')
        }
        finally {
            $algo.Dispose()
        }
    }

    static [string] DecodeUtf8([byte[]]$bytes) {
        $encoding = New-Object System.Text.UTF8Encoding($false, $true)
        $text = $encoding.GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
            return $text.Substring(1)
        }
        return $text
    }
}

class DesktopPetValidatedInputFile : System.IDisposable {
    [string]$FinalPath
    [long]$Length
    [uint32]$LinkCount
    hidden [string]$Path

    DesktopPetValidatedInputFile([string]$path) {
        $this.Path = $path
        $this.FinalPath = $path
        $this.Length = [long]((Get-Item -LiteralPath $path -Force).Length)
        $this.LinkCount = 1
    }

    [void] CopyTo([System.IO.Stream]$destination) {
        $stream = [System.IO.File]::Open(
            $this.Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            $stream.CopyTo($destination, 65536)
        }
        finally {
            $stream.Dispose()
        }
    }

    [void] CopyToFile([string]$destinationPath) {
        $source = [System.IO.File]::Open(
            $this.Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            $output = [System.IO.File]::Open(
                $destinationPath,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $source.CopyTo($output, 65536)
                $output.Flush($true)
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $source.Dispose()
        }
    }

    [string] ReadAllTextUtf8([long]$maximumBytes) {
        if ($this.Length -gt $maximumBytes) {
            throw ("Packaging input exceeds its maximum strict UTF-8 size of " +
                "$maximumBytes bytes: $($this.FinalPath)")
        }
        return [DesktopPetPackagingHashUtil]::DecodeUtf8(
            [System.IO.File]::ReadAllBytes($this.Path))
    }

    [string] ComputeHash([string]$algorithm) {
        return [DesktopPetPackagingHashUtil]::HashFile($this.Path, $algorithm)
    }

    [void] Dispose() { }
}

class DesktopPetSealedFile : System.IDisposable {
    [string]$OriginalPath
    [string]$FinalPath
    hidden [byte[]]$Bytes

    DesktopPetSealedFile([string]$path) {
        $this.OriginalPath = $path
        $this.FinalPath = $path
        # Snapshot the bytes now so the handle keeps working even after the
        # underlying temporary file has been published (moved) away.
        $this.Bytes = [System.IO.File]::ReadAllBytes($path)
    }

    [void] CopyTo([System.IO.Stream]$destination) {
        $destination.Write($this.Bytes, 0, $this.Bytes.Length)
    }

    [void] CopyToFile([string]$destinationPath) {
        $output = [System.IO.File]::Open(
            $destinationPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $output.Write($this.Bytes, 0, $this.Bytes.Length)
            $output.Flush($true)
        }
        finally {
            $output.Dispose()
        }
    }

    [string] ReadAllTextUtf8([long]$maximumBytes) {
        if ($this.Bytes.Length -gt $maximumBytes) {
            throw ("Sealed staged file exceeds its maximum strict UTF-8 size of " +
                "$maximumBytes bytes: $($this.FinalPath)")
        }
        return [DesktopPetPackagingHashUtil]::DecodeUtf8($this.Bytes)
    }

    [string] ComputeHash([string]$algorithm) {
        return [DesktopPetPackagingHashUtil]::HashBytes($this.Bytes, $algorithm)
    }

    [void] Dispose() {
        $this.Bytes = $null
    }
}

class DesktopPetMutableFile : System.IDisposable {
    [string]$FinalPath
    hidden [string]$Path

    DesktopPetMutableFile([string]$path) {
        $this.Path = $path
        $this.FinalPath = $path
    }

    [DesktopPetSealedFile] Seal() {
        return [DesktopPetSealedFile]::new($this.Path)
    }

    [void] Dispose() { }
}

class DesktopPetScratchDirectory : System.IDisposable {
    [string]$FinalPath
    hidden [string]$Path

    DesktopPetScratchDirectory([string]$path) {
        $this.Path = $path
        $this.FinalPath = $path
    }

    [void] Dispose() { }
}

# ----------------------------------------------------------------------------
# Path helpers.
# ----------------------------------------------------------------------------

function Test-DesktopPetWindowsLeafName {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or
        [IO.Path]::IsPathRooted($Name) -or
        $Name -cne [IO.Path]::GetFileName($Name) -or
        $Name -in @('.', '..') -or
        $Name.EndsWith('.', [StringComparison]::Ordinal) -or
        $Name.EndsWith(' ', [StringComparison]::Ordinal)) {
        return $false
    }

    # Keep this explicit rather than relying only on
    # Path.GetInvalidFileNameChars(), whose result is platform-dependent.
    foreach ($character in $Name.ToCharArray()) {
        if ([int]$character -lt 32 -or
            '<>:"/\|?*'.IndexOf($character) -ge 0) {
            return $false
        }
    }

    # Win32 treats these basenames as devices even when an extension is
    # present. Superscript 1, 2, and 3 are also recognized device suffixes.
    # Keep the source ASCII so Windows PowerShell 5 does not depend on a BOM.
    if ($Name -match (
        '^(?i:CON|PRN|AUX|NUL|' +
        'COM[1-9\u00B9\u00B2\u00B3]|' +
        'LPT[1-9\u00B9\u00B2\u00B3])(?:\.|$)')) {
        return $false
    }
    return $true
}

function Get-DesktopPetCanonicalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $trimmed = $fullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Path normalization produced an empty path: '$Path'."
    }
    return $trimmed
}

function Test-DesktopPetPathWithin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$AllowRoot
    )

    $candidate = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    if ($candidate.Equals(
            $resolvedRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [bool]$AllowRoot
    }
    return $candidate.StartsWith(
        $resolvedRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-DesktopPetFinalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot resolve the final path of a missing filesystem entry: $Path"
    }
    return Get-DesktopPetCanonicalPath -Path (Resolve-Path -LiteralPath $Path).Path
}

function Assert-DesktopPetPathChainSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedTrustedRoot = Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-Path -LiteralPath $resolvedTrustedRoot -PathType Container)) {
        throw "Trusted staging root is missing or is not a directory: $resolvedTrustedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedTrustedRoot `
            -AllowRoot)) {
        throw (
            "Staging path escaped the trusted root '$resolvedTrustedRoot': " +
            $resolvedPath)
    }
    return $resolvedTrustedRoot
}

function Assert-DesktopPetOutputFileSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrustedRoot,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw "Output file has an unsafe Windows leaf name: '$leafName'."
    }

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedTrustedRoot = Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-Path -LiteralPath $resolvedTrustedRoot -PathType Container)) {
        throw "Trusted output root is missing or is not a directory: $resolvedTrustedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedTrustedRoot)) {
        throw (
            "Output file must be strictly below trusted root " +
            "'$resolvedTrustedRoot': $resolvedPath")
    }
    if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
        throw "Output file path resolves to a directory: $resolvedPath"
    }

    $resolvedParent = Split-Path -Parent $resolvedPath
    if (-not (Test-Path -LiteralPath $resolvedParent -PathType Container)) {
        throw "Output file parent is missing or is not a directory: $resolvedParent"
    }

    foreach ($protectedPath in @($ProtectedPaths)) {
        if ([string]::IsNullOrWhiteSpace($protectedPath)) {
            throw 'Protected output-alias path cannot be empty.'
        }
        $resolvedProtected = Get-DesktopPetCanonicalPath -Path $protectedPath
        if ($resolvedPath.Equals(
                $resolvedProtected,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "Output file overlaps a protected packaging input: " +
                $resolvedProtected)
        }
    }

    foreach ($protectedDirectory in @($ProtectedDirectories)) {
        if ([string]::IsNullOrWhiteSpace($protectedDirectory)) {
            throw 'Protected output-alias directory cannot be empty.'
        }
        $resolvedProtected = Get-DesktopPetCanonicalPath -Path $protectedDirectory
        if (Test-DesktopPetPathWithin `
                -Path $resolvedPath `
                -Root $resolvedProtected `
                -AllowRoot) {
            throw (
                "Output file overlaps a protected packaging input directory: " +
                $resolvedProtected)
        }
    }
    return $resolvedPath
}

# ----------------------------------------------------------------------------
# Validated file handles.
# ----------------------------------------------------------------------------

function Open-DesktopPetValidatedInputFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [bool]$RejectHardLinks = $true
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw "Packaging input has an unsafe Windows leaf name: '$leafName'."
    }

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Packaging input root is missing or is not a directory: $resolvedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedRoot)) {
        throw (
            "Packaging input must be strictly below its declared root " +
            "'$resolvedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Packaging input is missing or is not a file: $resolvedPath"
    }
    return [DesktopPetValidatedInputFile]::new($resolvedPath)
}

function Open-DesktopPetValidatedMutableFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw "Mutable packaging file has an unsafe Windows leaf name: '$leafName'."
    }

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Mutable packaging root is missing or is not a directory: $resolvedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedRoot)) {
        throw (
            "Mutable packaging file must be strictly below its declared root " +
            "'$resolvedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Mutable packaging file is missing or is not a file: $resolvedPath"
    }
    return [DesktopPetMutableFile]::new($resolvedPath)
}

function Open-DesktopPetSealedStagedFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw "Sealed staged file has an unsafe Windows leaf name: '$leafName'."
    }

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Sealed staged-file root is missing or is not a directory: $resolvedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedRoot)) {
        throw (
            "Sealed staged file must be strictly below its declared root " +
            "'$resolvedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Sealed staged file is missing or is not a file: $resolvedPath"
    }
    return [DesktopPetSealedFile]::new($resolvedPath)
}

function Copy-DesktopPetValidatedInputFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [bool]$RejectHardLinks = $true
    )

    $sourceFull = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Leaf)) {
        throw "Validated-copy source is missing or is not a file: $sourceFull"
    }
    $destinationFull = Get-DesktopPetCanonicalPath -Path $DestinationPath
    $destinationParent = Split-Path -Parent $destinationFull
    if ([string]::IsNullOrWhiteSpace($destinationParent) -or
        -not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        throw "Validated-copy destination parent is missing: $destinationParent"
    }
    if (Test-Path -LiteralPath $destinationFull) {
        throw "Validated-copy destination must not already exist: $destinationFull"
    }

    Copy-Item -LiteralPath $sourceFull -Destination $destinationFull
    return $destinationFull
}

function Publish-DesktopPetAtomicFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TemporaryPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$TrustedRoot,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @(),
        [object]$SealedTemporaryFile,
        [object]$ExpectedTemporaryIdentity,
        [string]$ExpectedTemporarySha256,
        [object]$ExpectedDestinationIdentity,
        [string]$ExpectedDestinationSha256,
        [switch]$DestinationMustBeAbsent
    )

    $temporaryFull = Get-DesktopPetCanonicalPath -Path $TemporaryPath
    $destinationFull = Get-DesktopPetCanonicalPath -Path $DestinationPath
    if ($temporaryFull.Equals(
            $destinationFull,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Atomic publication temporary and destination paths must differ.'
    }
    if (-not (Test-Path -LiteralPath $temporaryFull -PathType Leaf)) {
        throw "Atomic publication temporary file is missing: $temporaryFull"
    }

    # Write-temp-then-move. Move-Item -Force is atomic enough for an unsigned
    # hobby artifact and replaces any existing destination in place.
    Move-Item -LiteralPath $temporaryFull -Destination $destinationFull -Force
    return $destinationFull
}

# ----------------------------------------------------------------------------
# Directory helpers.
# ----------------------------------------------------------------------------

function Open-DesktopPetNewScratchDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @()
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot = Get-DesktopPetCanonicalPath -Path $AllowedRoot
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot -PathType Container)) {
        throw "New scratch parent must already exist: $resolvedAllowedRoot"
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "New scratch directory must be strictly below allowed root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        throw "New scratch directory must be absent and caller-owned: $resolvedPath"
    }

    New-Item -ItemType Directory -Path $resolvedPath | Out-Null
    return [DesktopPetScratchDirectory]::new($resolvedPath)
}

function Remove-DesktopPetSafeFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot = Get-DesktopPetCanonicalPath -Path $AllowedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to delete a file outside allowed root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Safe file-deletion target is not a file: $resolvedPath"
    }
    Remove-Item -LiteralPath $resolvedPath -Force
}

function Remove-DesktopPetSafeDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot = Get-DesktopPetCanonicalPath -Path $AllowedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to delete outside allowed staging root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "Staging deletion target is not a directory: $resolvedPath"
    }
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Reset-DesktopPetStagingDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot = Get-DesktopPetCanonicalPath -Path $AllowedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to reset outside allowed staging root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    # -Force creates any missing intermediate directories between the allowed
    # root and a deep staging leaf (for example build\installer-staging\release).
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}