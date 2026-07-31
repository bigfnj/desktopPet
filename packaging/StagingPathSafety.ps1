#requires -Version 5

# Shared fail-closed filesystem policy for destructive packaging staging work.
# Callers must provide a trusted existing root, an allowed staging root at or
# below it, and a candidate strictly below the allowed root.

if ($null -eq ('DesktopPet.Packaging.FinalPathResolver' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet.Packaging
{
    public static class FinalPathResolver
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileAddFile = 0x00000002;
        private const uint DeleteAccess = 0x00010000;
        private const uint GenericRead = 0x80000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const int FileRenameInfo = 3;
        private const int FileDispositionInfo = 4;

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle ReOpenFile(
            SafeFileHandle originalFile,
            uint desiredAccess,
            uint shareMode,
            uint flagsAndAttributes);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CreateDirectoryW(
            string path,
            IntPtr securityAttributes);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        private static string GetFinalPathFromHandle(
            SafeFileHandle handle,
            string path)
        {
            StringBuilder buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the final path: " + path);
            }
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (length == 0 || length >= buffer.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not resolve the complete final path: " + path);
                }
            }

            string resolved = buffer.ToString();
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (resolved.StartsWith(
                uncPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                resolved = @"\\" + resolved.Substring(uncPrefix.Length);
            }
            else if (resolved.StartsWith(
                devicePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                resolved = resolved.Substring(devicePrefix.Length);
            }
            return Path.GetFullPath(resolved);
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Filesystem path cannot be empty.", "path");
            }
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
            return full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static bool IsWithin(
            string path,
            string root,
            bool allowRoot)
        {
            string candidate = NormalizePath(path);
            string boundary = NormalizePath(root);
            if (String.Equals(
                candidate,
                boundary,
                StringComparison.OrdinalIgnoreCase))
            {
                return allowRoot;
            }
            string prefix = boundary.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
                ? boundary
                : boundary + Path.DirectorySeparatorChar;
            return candidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static SafeFileHandle OpenDirectoryHandle(
            string path,
            uint desiredAccess)
        {
            SafeFileHandle handle = CreateFileW(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "Could not retain a staging directory handle: " + path);
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not validate a retained staging directory: " +
                        path);
                }
                if ((information.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Staging directory is a reparse point: " + path);
                }
                if ((information.FileAttributes &
                        FileAttributeDirectory) == 0)
                {
                    throw new InvalidOperationException(
                        "Staging path is not a directory: " + path);
                }
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public sealed class DirectoryChainLease : IDisposable
        {
            private SafeFileHandle[] handles;

            internal DirectoryChainLease(
                SafeFileHandle[] retainedHandles,
                string rootFinalPath,
                string finalPath)
            {
                handles = retainedHandles;
                RootFinalPath = rootFinalPath;
                FinalPath = finalPath;
            }

            public string RootFinalPath { get; private set; }
            public string FinalPath { get; private set; }

            internal SafeFileHandle FinalHandle
            {
                get
                {
                    if (handles == null || handles.Length == 0)
                    {
                        throw new ObjectDisposedException(
                            "DirectoryChainLease");
                    }
                    return handles[handles.Length - 1];
                }
            }

            public void Dispose()
            {
                SafeFileHandle[] retained = handles;
                handles = null;
                if (retained == null)
                {
                    return;
                }
                for (int index = retained.Length - 1; index >= 0; index--)
                {
                    retained[index].Dispose();
                }
            }
        }

        private static DirectoryChainLease OpenDirectoryChainLease(
            string path,
            string trustedRoot,
            uint finalDesiredAccess)
        {
            string fullRoot = NormalizePath(trustedRoot);
            string fullPath = NormalizePath(path);
            if (!IsWithin(fullPath, fullRoot, true))
            {
                throw new InvalidOperationException(
                    "Retained directory path escaped its trusted root: " +
                    fullPath);
            }

            string relative = fullPath.Substring(fullRoot.Length).TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string[] segments = relative.Length == 0
                ? new string[0]
                : relative.Split(
                    new char[] {
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    },
                    StringSplitOptions.RemoveEmptyEntries);
            SafeFileHandle[] handles =
                new SafeFileHandle[checked(segments.Length + 1)];
            int retainedCount = 0;
            string rootFinal = null;
            string current = fullRoot;
            try
            {
                for (int index = 0; index < handles.Length; index++)
                {
                    // A zero-access directory handle does not establish the
                    // sharing reservation needed to reject a later rename or
                    // deletion. Request list access on every retained chain
                    // component so omitting FILE_SHARE_DELETE actually pins
                    // every ancestor as well as the final directory.
                    uint access = FileListDirectory;
                    if (index == handles.Length - 1)
                    {
                        access |= finalDesiredAccess;
                    }
                    SafeFileHandle handle =
                        OpenDirectoryHandle(current, access);
                    handles[index] = handle;
                    retainedCount++;
                    string final = GetFinalPathFromHandle(handle, current);
                    if (index == 0)
                    {
                        rootFinal = final;
                    }
                    else if (!IsWithin(final, rootFinal, false))
                    {
                        throw new InvalidOperationException(
                            "Retained directory escaped the trusted physical " +
                            "root '" + rootFinal + "': " + final);
                    }
                    if (index < segments.Length)
                    {
                        current = Path.Combine(current, segments[index]);
                    }
                }
                string finalPath = GetFinalPathFromHandle(
                    handles[handles.Length - 1],
                    fullPath);
                DirectoryChainLease lease = new DirectoryChainLease(
                    handles,
                    rootFinal,
                    finalPath);
                handles = null;
                return lease;
            }
            finally
            {
                if (handles != null)
                {
                    for (int index = retainedCount - 1; index >= 0; index--)
                    {
                        handles[index].Dispose();
                    }
                }
            }
        }

        public static DirectoryChainLease AcquireDirectoryChainLease(
            string path,
            string trustedRoot)
        {
            return OpenDirectoryChainLease(path, trustedRoot, 0);
        }

        public sealed class ValidatedPublication : IDisposable
        {
            private DirectoryChainLease temporaryParent;
            private DirectoryChainLease destinationParent;
            private SafeFileHandle temporaryFile;
            private SealedStagedFileLease sealedTemporary;
            private bool ownsTemporaryFile;
            private string temporaryFinalPath;
            private string destinationName;
            private string destinationFinalPath;

            internal ValidatedPublication(
                DirectoryChainLease temporaryParentLease,
                DirectoryChainLease destinationParentLease,
                SafeFileHandle temporaryHandle,
                string expectedTemporaryFinalPath,
                string destinationFileName,
                string expectedDestinationFinalPath)
            {
                temporaryParent = temporaryParentLease;
                destinationParent = destinationParentLease;
                temporaryFile = temporaryHandle;
                ownsTemporaryFile = true;
                temporaryFinalPath = expectedTemporaryFinalPath;
                destinationName = destinationFileName;
                destinationFinalPath = expectedDestinationFinalPath;
            }

            internal ValidatedPublication(
                DirectoryChainLease destinationParentLease,
                SealedStagedFileLease sealedTemporaryFile,
                string expectedTemporaryFinalPath,
                string destinationFileName,
                string expectedDestinationFinalPath)
            {
                destinationParent = destinationParentLease;
                sealedTemporary = sealedTemporaryFile;
                temporaryFile = sealedTemporaryFile.PublicationHandle;
                ownsTemporaryFile = false;
                temporaryFinalPath = expectedTemporaryFinalPath;
                destinationName = destinationFileName;
                destinationFinalPath = expectedDestinationFinalPath;
            }

            public void AssertTemporaryIdentity(
                ValidatedMutableFileLease expected)
            {
                if (temporaryFile == null)
                {
                    throw new ObjectDisposedException("ValidatedPublication");
                }
                if (expected == null)
                {
                    throw new ArgumentNullException("expected");
                }
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(
                        temporaryFile,
                        out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not validate atomic-publication input identity.");
                }
                string finalPath = GetFinalPathFromHandle(
                    temporaryFile,
                    expected.FinalPath);
                expected.AssertObservedIdentity(
                    information,
                    finalPath,
                    "Atomic-publication input");
            }

            public void AssertDestinationIdentity(
                ValidatedMutableFileLease expected)
            {
                if (temporaryFile == null)
                {
                    throw new ObjectDisposedException("ValidatedPublication");
                }
                if (expected == null)
                {
                    throw new ArgumentNullException("expected");
                }
                if (!String.Equals(
                        NormalizePath(expected.FinalPath),
                        NormalizePath(destinationFinalPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Atomic-publication destination identity was sealed " +
                        "for a different path: " + expected.FinalPath);
                }
                expected.AssertCurrentPathIdentity();
            }

            private static string ComputeFileSha256(
                string path,
                ValidatedMutableFileLease expectedIdentity,
                string context,
                bool shareDelete)
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    shareDelete
                        ? FileShare.Read | FileShare.Delete
                        : FileShare.Read,
                    65536,
                    FileOptions.SequentialScan))
                {
                    ByHandleFileInformation information;
                    if (!GetFileInformationByHandle(
                            stream.SafeFileHandle,
                            out information))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not validate " + context +
                            " while hashing: " + path);
                    }
                    string finalPath =
                        GetFinalPathFromHandle(stream.SafeFileHandle, path);
                    if (expectedIdentity != null)
                    {
                        expectedIdentity.AssertObservedIdentity(
                            information,
                            finalPath,
                            context);
                    }
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        return BitConverter.ToString(
                            sha256.ComputeHash(stream)).Replace("-", "");
                    }
                }
            }

            private static void AssertExpectedSha256(
                string observed,
                string expected,
                string context)
            {
                if (String.IsNullOrWhiteSpace(expected) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(
                        expected,
                        @"\A[0-9A-Fa-f]{64}\z"))
                {
                    throw new ArgumentException(
                        context + " expected SHA-256 is invalid.",
                        "expected");
                }
                if (!String.Equals(
                        observed,
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        context + " content changed after validation; " +
                        "expected " + expected.ToUpperInvariant() +
                        ", observed " + observed + ".");
                }
            }

            public void AssertTemporarySha256(string expected)
            {
                if (temporaryFile == null)
                {
                    throw new ObjectDisposedException("ValidatedPublication");
                }
                string observed = sealedTemporary == null
                    ? ComputeFileSha256(
                        temporaryFinalPath,
                        null,
                        "Atomic-publication input",
                        true)
                    : sealedTemporary.ComputeHash("SHA256");
                AssertExpectedSha256(
                    observed,
                    expected,
                    "Atomic-publication input");
            }

            public void AssertDestinationSha256(
                string expected,
                ValidatedMutableFileLease expectedIdentity)
            {
                if (temporaryFile == null)
                {
                    throw new ObjectDisposedException("ValidatedPublication");
                }
                string observed = ComputeFileSha256(
                    destinationFinalPath,
                    expectedIdentity,
                    "Atomic-publication destination",
                    false);
                AssertExpectedSha256(
                    observed,
                    expected,
                    "Atomic-publication destination");
            }

            public void Publish(bool replaceIfExists)
            {
                if (temporaryFile == null)
                {
                    throw new ObjectDisposedException("ValidatedPublication");
                }
                byte[] nameBytes =
                    Encoding.Unicode.GetBytes(destinationFinalPath);
                int rootOffset = IntPtr.Size == 8 ? 8 : 4;
                int lengthOffset = checked(rootOffset + IntPtr.Size);
                int nameOffset = checked(lengthOffset + 4);
                // FILE_RENAME_INFO has a trailing WCHAR[1] and native
                // alignment padding. Allocate the complete fixed structure
                // plus the variable filename, as required by
                // SetFileInformationByHandle; a field-offset-only allocation
                // lets the kernel read beyond the buffer on x64.
                int fixedStructureSize = checked(nameOffset + 4);
                int bufferSize = checked(
                    fixedStructureSize + nameBytes.Length);
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    for (int index = 0; index < bufferSize; index++)
                    {
                        Marshal.WriteByte(buffer, index, 0);
                    }
                    Marshal.WriteByte(
                        buffer,
                        0,
                        replaceIfExists ? (byte)1 : (byte)0);
                    Marshal.WriteIntPtr(
                        buffer,
                        rootOffset,
                        IntPtr.Zero);
                    Marshal.WriteInt32(
                        buffer,
                        lengthOffset,
                        nameBytes.Length);
                    Marshal.Copy(
                        nameBytes,
                        0,
                        IntPtr.Add(buffer, nameOffset),
                        nameBytes.Length);
                    if (!SetFileInformationByHandle(
                        temporaryFile,
                        FileRenameInfo,
                        buffer,
                        checked((uint)bufferSize)))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not atomically publish the validated file.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                string publishedFinal = GetFinalPathFromHandle(
                    temporaryFile,
                    destinationFinalPath);
                if (!String.Equals(
                    NormalizePath(publishedFinal),
                    NormalizePath(destinationFinalPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Published file did not land at its retained-handle " +
                        "destination: " + publishedFinal);
                }
            }

            public void Dispose()
            {
                if (temporaryFile != null)
                {
                    if (ownsTemporaryFile)
                    {
                        temporaryFile.Dispose();
                    }
                    temporaryFile = null;
                }
                sealedTemporary = null;
                if (destinationParent != null)
                {
                    destinationParent.Dispose();
                    destinationParent = null;
                }
                if (temporaryParent != null)
                {
                    temporaryParent.Dispose();
                    temporaryParent = null;
                }
            }
        }

        public static ValidatedPublication OpenValidatedPublication(
            string temporaryPath,
            string destinationPath,
            string trustedRoot)
        {
            string temporaryFull = NormalizePath(temporaryPath);
            string destinationFull = NormalizePath(destinationPath);
            string temporaryParentPath = Path.GetDirectoryName(temporaryFull);
            string destinationParentPath =
                Path.GetDirectoryName(destinationFull);
            if (String.Equals(
                temporaryParentPath,
                destinationParentPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Validated publication requires separate parent directories.");
            }

            DirectoryChainLease temporaryParent = null;
            DirectoryChainLease destinationParent = null;
            SafeFileHandle temporaryFile = null;
            try
            {
                temporaryParent = OpenDirectoryChainLease(
                    temporaryParentPath,
                    trustedRoot,
                    FileListDirectory);
                destinationParent = OpenDirectoryChainLease(
                    destinationParentPath,
                    trustedRoot,
                    FileListDirectory | FileAddFile);

                temporaryFile = CreateFileW(
                    temporaryFull,
                    GenericRead | DeleteAccess,
                    FileShareRead,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagSequentialScan,
                    IntPtr.Zero);
                if (temporaryFile.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    temporaryFile.Dispose();
                    temporaryFile = null;
                    throw new Win32Exception(
                        error,
                        "Could not retain the atomic-publication input: " +
                        temporaryFull);
                }

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(
                    temporaryFile,
                    out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not validate the atomic-publication input: " +
                        temporaryFull);
                }
                if ((information.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Atomic-publication input is a reparse point: " +
                        temporaryFull);
                }
                if ((information.FileAttributes &
                        FileAttributeDirectory) != 0)
                {
                    throw new InvalidOperationException(
                        "Atomic-publication input is not a regular file: " +
                        temporaryFull);
                }
                if (information.NumberOfLinks != 1)
                {
                    throw new InvalidOperationException(
                        "Atomic-publication input is a hard-link alias: " +
                        temporaryFull);
                }
                string temporaryFinal = GetFinalPathFromHandle(
                    temporaryFile,
                    temporaryFull);
                string expectedTemporaryFinal = Path.Combine(
                    temporaryParent.FinalPath,
                    Path.GetFileName(temporaryFull));
                if (!String.Equals(
                    NormalizePath(temporaryFinal),
                    NormalizePath(expectedTemporaryFinal),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Atomic-publication input escaped its retained parent: " +
                        temporaryFinal);
                }

                string destinationName = Path.GetFileName(destinationFull);
                string destinationFinal = Path.Combine(
                    destinationParent.FinalPath,
                    destinationName);
                ValidatedPublication publication =
                    new ValidatedPublication(
                        temporaryParent,
                        destinationParent,
                        temporaryFile,
                        temporaryFinal,
                        destinationName,
                        destinationFinal);
                temporaryParent = null;
                destinationParent = null;
                temporaryFile = null;
                return publication;
            }
            finally
            {
                if (temporaryFile != null)
                {
                    temporaryFile.Dispose();
                }
                if (destinationParent != null)
                {
                    destinationParent.Dispose();
                }
                if (temporaryParent != null)
                {
                    temporaryParent.Dispose();
                }
            }
        }

        public static ValidatedPublication OpenValidatedPublication(
            SealedStagedFileLease sealedTemporary,
            string destinationPath,
            string trustedRoot)
        {
            if (sealedTemporary == null)
            {
                throw new ArgumentNullException("sealedTemporary");
            }
            string temporaryFull =
                NormalizePath(sealedTemporary.OriginalPath);
            string destinationFull = NormalizePath(destinationPath);
            string trustedFull = NormalizePath(trustedRoot);
            if (!IsWithin(temporaryFull, trustedFull, false) ||
                !IsWithin(destinationFull, trustedFull, false))
            {
                throw new InvalidOperationException(
                    "Sealed atomic publication escaped its trusted root.");
            }
            string temporaryParentPath =
                Path.GetDirectoryName(temporaryFull);
            string destinationParentPath =
                Path.GetDirectoryName(destinationFull);
            if (String.Equals(
                temporaryParentPath,
                destinationParentPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Validated publication requires separate parent directories.");
            }

            DirectoryChainLease destinationParent = null;
            try
            {
                sealedTemporary.Revalidate();
                destinationParent = OpenDirectoryChainLease(
                    destinationParentPath,
                    trustedFull,
                    FileListDirectory | FileAddFile);
                string destinationName = Path.GetFileName(destinationFull);
                string destinationFinal = Path.Combine(
                    destinationParent.FinalPath,
                    destinationName);
                ValidatedPublication publication =
                    new ValidatedPublication(
                        destinationParent,
                        sealedTemporary,
                        sealedTemporary.FinalPath,
                        destinationName,
                        destinationFinal);
                destinationParent = null;
                return publication;
            }
            finally
            {
                if (destinationParent != null)
                {
                    destinationParent.Dispose();
                }
            }
        }

        public sealed class ValidatedDeletion : IDisposable
        {
            private DirectoryChainLease parent;
            private SafeFileHandle target;
            private bool isDirectory;

            internal ValidatedDeletion(
                DirectoryChainLease parentLease,
                SafeFileHandle targetHandle,
                bool targetIsDirectory)
            {
                parent = parentLease;
                target = targetHandle;
                isDirectory = targetIsDirectory;
            }

            public bool IsDirectory
            {
                get { return isDirectory; }
            }

            public void Delete()
            {
                if (target == null)
                {
                    throw new ObjectDisposedException("ValidatedDeletion");
                }
                IntPtr disposition = Marshal.AllocHGlobal(1);
                try
                {
                    Marshal.WriteByte(disposition, 0, 1);
                    if (!SetFileInformationByHandle(
                        target,
                        FileDispositionInfo,
                        disposition,
                        1))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not delete the retained staging entry.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(disposition);
                }
                target.Dispose();
                target = null;
            }

            public void Dispose()
            {
                if (target != null)
                {
                    target.Dispose();
                    target = null;
                }
                if (parent != null)
                {
                    parent.Dispose();
                    parent = null;
                }
            }
        }

        public static ValidatedDeletion OpenValidatedDeletion(
            string path,
            string allowedRoot,
            string trustedRoot)
        {
            string fullPath = NormalizePath(path);
            string fullRoot = NormalizePath(allowedRoot);
            string fullTrustedRoot = NormalizePath(trustedRoot);
            if (!IsWithin(fullRoot, fullTrustedRoot, true))
            {
                throw new InvalidOperationException(
                    "Deletion allowed root escaped its trusted root: " +
                    fullRoot);
            }
            if (!IsWithin(fullPath, fullRoot, false))
            {
                throw new InvalidOperationException(
                    "Deletion target escaped its allowed root: " + fullPath);
            }
            string parentPath = Path.GetDirectoryName(fullPath);
            DirectoryChainLease parent = null;
            SafeFileHandle target = null;
            try
            {
                parent = OpenDirectoryChainLease(
                    parentPath,
                    fullTrustedRoot,
                    FileListDirectory);
                string allowedFinal = GetFinalPath(fullRoot);
                if (!IsWithin(
                    allowedFinal,
                    parent.RootFinalPath,
                    true))
                {
                    throw new InvalidOperationException(
                        "Deletion allowed root escaped the retained trusted " +
                        "root '" + parent.RootFinalPath + "': " +
                        allowedFinal);
                }
                target = CreateFileW(
                    fullPath,
                    DeleteAccess,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (target.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    target.Dispose();
                    target = null;
                    throw new Win32Exception(
                        error,
                        "Could not retain the staging deletion target: " +
                        fullPath);
                }

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(target, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not validate the staging deletion target: " +
                        fullPath);
                }
                if ((information.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing to delete a staging reparse point: " +
                        fullPath);
                }
                string final = GetFinalPathFromHandle(target, fullPath);
                if (!IsWithin(final, allowedFinal, false))
                {
                    throw new InvalidOperationException(
                        "Deletion target escaped the retained allowed root '" +
                        allowedFinal + "': " + final);
                }
                bool directory =
                    (information.FileAttributes &
                        FileAttributeDirectory) != 0;
                ValidatedDeletion deletion = new ValidatedDeletion(
                    parent,
                    target,
                    directory);
                parent = null;
                target = null;
                return deletion;
            }
            finally
            {
                if (target != null)
                {
                    target.Dispose();
                }
                if (parent != null)
                {
                    parent.Dispose();
                }
            }
        }

        public sealed class ValidatedDirectoryCreation : IDisposable
        {
            private DirectoryChainLease parent;
            private SafeFileHandle created;
            private string path;

            internal ValidatedDirectoryCreation(
                DirectoryChainLease parentLease,
                string directoryPath)
            {
                parent = parentLease;
                path = directoryPath;
            }

            public void Create()
            {
                if (parent == null)
                {
                    throw new ObjectDisposedException(
                        "ValidatedDirectoryCreation");
                }
                if (!CreateDirectoryW(path, IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not create the retained-parent staging " +
                        "directory: " + path);
                }

                SafeFileHandle opened = null;
                try
                {
                    opened = OpenDirectoryHandle(path, FileListDirectory);
                    string createdFinal =
                        GetFinalPathFromHandle(opened, path);
                    string expectedFinal = Path.Combine(
                        parent.FinalPath,
                        Path.GetFileName(path));
                    if (!String.Equals(
                        NormalizePath(createdFinal),
                        NormalizePath(expectedFinal),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Created staging directory escaped its retained " +
                            "parent: " + createdFinal);
                    }
                    // Retain the new leaf as well as its parent chain until the
                    // caller has acquired its long-lived chain lease. This
                    // closes the create-to-lease rename/delete handoff window.
                    created = opened;
                    opened = null;
                }
                finally
                {
                    if (opened != null)
                    {
                        opened.Dispose();
                    }
                }
            }

            public void Dispose()
            {
                if (created != null)
                {
                    created.Dispose();
                    created = null;
                }
                if (parent != null)
                {
                    parent.Dispose();
                    parent = null;
                }
            }
        }

        public static ValidatedDirectoryCreation OpenValidatedDirectoryCreation(
            string path,
            string trustedRoot)
        {
            string fullPath = NormalizePath(path);
            string fullRoot = NormalizePath(trustedRoot);
            if (!IsWithin(fullPath, fullRoot, false))
            {
                throw new InvalidOperationException(
                    "Directory creation target escaped its trusted root: " +
                    fullPath);
            }
            string parentPath = Path.GetDirectoryName(fullPath);
            DirectoryChainLease parent = OpenDirectoryChainLease(
                parentPath,
                fullRoot,
                FileListDirectory | FileAddFile);
            return new ValidatedDirectoryCreation(parent, fullPath);
        }

        public static string GetFinalPath(string path)
        {
            using (SafeFileHandle handle = CreateFileW(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not open a handle for final-path validation: " + path);
                }
                return GetFinalPathFromHandle(handle, path);
            }
        }

        public static uint GetLinkCount(string path)
        {
            using (SafeFileHandle handle = CreateFileW(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not open a handle for link-count validation: " + path);
                }
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read file identity information: " + path);
                }
                return information.NumberOfLinks;
            }
        }

        public sealed class ValidatedMutableFileLease : IDisposable
        {
            private DirectoryChainLease parentLease;
            private SafeFileHandle handle;
            private string path;
            private string root;
            private uint volumeSerialNumber;
            private uint fileIndexHigh;
            private uint fileIndexLow;

            internal ValidatedMutableFileLease(
                DirectoryChainLease retainedParentLease,
                SafeFileHandle retainedHandle,
                string lexicalPath,
                string declaredRoot,
                string finalPath,
                ByHandleFileInformation information)
            {
                parentLease = retainedParentLease;
                handle = retainedHandle;
                path = lexicalPath;
                root = declaredRoot;
                FinalPath = finalPath;
                volumeSerialNumber = information.VolumeSerialNumber;
                fileIndexHigh = information.FileIndexHigh;
                fileIndexLow = information.FileIndexLow;
            }

            public string FinalPath { get; private set; }

            internal static void AssertRegularSingleLink(
                ByHandleFileInformation information,
                string observedPath)
            {
                if ((information.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Mutable packaging file is a reparse point: " +
                        observedPath);
                }
                if ((information.FileAttributes &
                        FileAttributeDirectory) != 0)
                {
                    throw new InvalidOperationException(
                        "Mutable packaging path is not a regular file: " +
                        observedPath);
                }
                if (information.NumberOfLinks != 1)
                {
                    throw new InvalidOperationException(
                        "Mutable packaging file is a hard-link alias: " +
                        observedPath);
                }
            }

            private void AssertSameIdentity(
                ByHandleFileInformation information,
                string observedPath)
            {
                if (information.VolumeSerialNumber != volumeSerialNumber ||
                    information.FileIndexHigh != fileIndexHigh ||
                    information.FileIndexLow != fileIndexLow)
                {
                    throw new InvalidOperationException(
                        "Mutable packaging file identity changed: " +
                        observedPath);
                }
            }

            internal void AssertObservedIdentity(
                ByHandleFileInformation information,
                string observedFinalPath,
                string context)
            {
                AssertRegularSingleLink(information, observedFinalPath);
                AssertSameIdentity(information, observedFinalPath);
                if (!String.Equals(
                        NormalizePath(observedFinalPath),
                        NormalizePath(FinalPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        context + " final path differs from its sealed " +
                        "identity: " + observedFinalPath);
                }
            }

            public void AssertCurrentPathIdentity()
            {
                using (SafeFileHandle observed = CreateFileW(
                    path,
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero))
                {
                    if (observed.IsInvalid)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not reopen sealed mutable packaging file " +
                            "by path: " + path);
                    }
                    ByHandleFileInformation observedInformation;
                    if (!GetFileInformationByHandle(
                            observed,
                            out observedInformation))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not validate sealed mutable packaging " +
                            "path identity: " + path);
                    }
                    string observedFinal =
                        GetFinalPathFromHandle(observed, path);
                    AssertObservedIdentity(
                        observedInformation,
                        observedFinal,
                        "Mutable packaging path");
                }
            }

            public void Revalidate()
            {
                if (handle == null || handle.IsClosed || handle.IsInvalid)
                {
                    throw new ObjectDisposedException(
                        "ValidatedMutableFileLease");
                }

                ByHandleFileInformation retainedInformation;
                if (!GetFileInformationByHandle(
                        handle,
                        out retainedInformation))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not revalidate retained mutable packaging " +
                        "file identity: " + path);
                }
                string retainedFinal = GetFinalPathFromHandle(handle, path);
                AssertObservedIdentity(
                    retainedInformation,
                    retainedFinal,
                    "Retained mutable packaging file");

                AssertCurrentPathIdentity();
            }

            public SealedStagedFileLease Seal()
            {
                return Seal(null);
            }

            public SealedStagedFileLease Seal(
                Action afterSealAcquiredBeforeMutableRelease)
            {
                if (handle == null || handle.IsClosed || handle.IsInvalid)
                {
                    throw new ObjectDisposedException(
                        "ValidatedMutableFileLease");
                }

                SealedStagedFileLease sealedFile = null;
                try
                {
                    // Acquire the deny-write read handle while this mutable
                    // identity/name reservation is still live. Only after the
                    // exact identity is cross-checked is the mutable handle
                    // consumed, so there is no close/reopen substitution gap.
                    sealedFile = OpenRetainedSingleLinkFile(
                        path,
                        root,
                        false);
                    sealedFile.AssertMatchesExpectedIdentity(
                        this,
                        "Mutable-to-sealed staged-file transition");
                    if (afterSealAcquiredBeforeMutableRelease != null)
                    {
                        afterSealAcquiredBeforeMutableRelease();
                    }
                    Revalidate();
                    sealedFile.Revalidate();
                    Dispose();
                    return sealedFile;
                }
                catch
                {
                    if (sealedFile != null)
                    {
                        sealedFile.Dispose();
                    }
                    throw;
                }
            }

            public void Dispose()
            {
                if (handle != null)
                {
                    handle.Dispose();
                    handle = null;
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                    parentLease = null;
                }
            }
        }

        public static ValidatedMutableFileLease OpenValidatedMutableFile(
            string path,
            string root)
        {
            string fullPath = NormalizePath(path);
            string fullRoot = NormalizePath(root);
            if (!IsWithin(fullPath, fullRoot, false))
            {
                throw new InvalidOperationException(
                    "Mutable packaging file escaped its declared root: " +
                    fullPath);
            }
            string volumeRoot = Path.GetPathRoot(fullRoot);
            string parentPath = Path.GetDirectoryName(fullPath);
            DirectoryChainLease parentLease = null;
            SafeFileHandle handle = null;
            try
            {
                parentLease = OpenDirectoryChainLease(
                    parentPath,
                    volumeRoot,
                    FileListDirectory);
                string rootFinal = GetFinalPath(fullRoot);
                if (!IsWithin(rootFinal, parentLease.RootFinalPath, true))
                {
                    throw new InvalidOperationException(
                        "Mutable packaging root escaped its retained volume " +
                        "root: " + rootFinal);
                }

                handle = CreateFileW(
                    fullPath,
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;
                    throw new Win32Exception(
                        error,
                        "Could not open mutable packaging file for retained " +
                        "identity validation: " + fullPath);
                }
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read mutable packaging file identity: " +
                        fullPath);
                }
                ValidatedMutableFileLease.AssertRegularSingleLink(
                    information,
                    fullPath);
                string finalPath = GetFinalPathFromHandle(handle, fullPath);
                if (!IsWithin(finalPath, rootFinal, false) ||
                    !String.Equals(
                        NormalizePath(finalPath),
                        NormalizePath(fullPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Mutable packaging file escaped its retained declared " +
                        "root '" + rootFinal + "': " + finalPath);
                }
                ValidatedMutableFileLease result =
                    new ValidatedMutableFileLease(
                        parentLease,
                        handle,
                        fullPath,
                        fullRoot,
                        finalPath,
                        information);
                parentLease = null;
                handle = null;
                return result;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                }
            }
        }

        public sealed class SealedStagedFileLease : IDisposable
        {
            private DirectoryChainLease parentLease;
            private FileStream stream;
            private SafeFileHandle publicationControl;
            private string path;
            private uint volumeSerialNumber;
            private uint fileIndexHigh;
            private uint fileIndexLow;

            internal SealedStagedFileLease(
                DirectoryChainLease retainedParentLease,
                SafeFileHandle retainedHandle,
                string lexicalPath,
                string finalPath,
                ByHandleFileInformation information)
            {
                parentLease = retainedParentLease;
                stream = new FileStream(
                    retainedHandle,
                    FileAccess.Read,
                    65536,
                    false);
                path = lexicalPath;
                FinalPath = finalPath;
                volumeSerialNumber = information.VolumeSerialNumber;
                fileIndexHigh = information.FileIndexHigh;
                fileIndexLow = information.FileIndexLow;
            }

            public string FinalPath { get; private set; }
            public string OriginalPath
            {
                get { return path; }
            }

            internal SafeFileHandle RetainedHandle
            {
                get
                {
                    if (stream == null)
                    {
                        throw new ObjectDisposedException(
                            "SealedStagedFileLease");
                    }
                    return stream.SafeFileHandle;
                }
            }

            internal SafeFileHandle PublicationHandle
            {
                get
                {
                    if (publicationControl != null)
                    {
                        if (publicationControl.IsClosed ||
                            publicationControl.IsInvalid)
                        {
                            throw new ObjectDisposedException(
                                "SealedStagedFileLease");
                        }
                        return publicationControl;
                    }

                    SafeFileHandle reopened = ReOpenFile(
                        RetainedHandle,
                        GenericRead | DeleteAccess,
                        FileShareRead | FileShareDelete,
                        FileFlagOpenReparsePoint | FileFlagSequentialScan);
                    if (reopened.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        reopened.Dispose();
                        throw new Win32Exception(
                            error,
                            "Could not acquire the exact sealed staged-file " +
                            "publication handle.");
                    }
                    try
                    {
                        ByHandleFileInformation information;
                        if (!GetFileInformationByHandle(
                                reopened,
                                out information))
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "Could not validate the sealed staged-file " +
                                "publication handle.");
                        }
                        string reopenedFinal =
                            GetFinalPathFromHandle(reopened, path);
                        AssertObservedFileIdentity(
                            information,
                            reopenedFinal,
                            "Sealed staged-file publication handle");
                        publicationControl = reopened;
                        reopened = null;
                        return publicationControl;
                    }
                    finally
                    {
                        if (reopened != null)
                        {
                            reopened.Dispose();
                        }
                    }
                }
            }

            public void AcquirePublicationControl()
            {
                SafeFileHandle ignored = PublicationHandle;
            }

            public void AcquireExclusivePublicationControl()
            {
                if (publicationControl != null)
                {
                    throw new InvalidOperationException(
                        "Publication control was already acquired.");
                }

                SafeFileHandle reopened = ReOpenFile(
                    RetainedHandle,
                    GenericRead | DeleteAccess,
                    FileShareRead,
                    FileFlagOpenReparsePoint | FileFlagSequentialScan);
                if (reopened.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    reopened.Dispose();
                    throw new Win32Exception(
                        error,
                        "Could not acquire exclusive exact-object sealed " +
                        "staged-file control.");
                }
                try
                {
                    ByHandleFileInformation information;
                    if (!GetFileInformationByHandle(
                            reopened,
                            out information))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not validate exclusive sealed staged-file " +
                            "control.");
                    }
                    string reopenedFinal =
                        GetFinalPathFromHandle(reopened, path);
                    AssertObservedFileIdentity(
                        information,
                        reopenedFinal,
                        "Exclusive sealed staged-file control");
                    publicationControl = reopened;
                    reopened = null;
                }
                finally
                {
                    if (reopened != null)
                    {
                        reopened.Dispose();
                    }
                }
            }

            internal void AssertObservedIdentity(
                ByHandleFileInformation information,
                string observedFinalPath,
                string context)
            {
                AssertObservedFileIdentity(
                    information,
                    observedFinalPath,
                    context);
                if (!String.Equals(
                        NormalizePath(observedFinalPath),
                        NormalizePath(FinalPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        context + " sealed identity changed because its final " +
                        "path moved: " + observedFinalPath);
                }
            }

            internal void AssertObservedFileIdentity(
                ByHandleFileInformation information,
                string observedPath,
                string context)
            {
                ValidatedMutableFileLease.AssertRegularSingleLink(
                    information,
                    observedPath);
                if (information.VolumeSerialNumber != volumeSerialNumber ||
                    information.FileIndexHigh != fileIndexHigh ||
                    information.FileIndexLow != fileIndexLow)
                {
                    throw new InvalidOperationException(
                        context + " sealed identity changed: " +
                        observedPath);
                }
            }

            private void GetRetainedInformation(
                out ByHandleFileInformation information,
                out string finalPath)
            {
                SafeFileHandle retained = RetainedHandle;
                if (!GetFileInformationByHandle(retained, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read retained sealed staged-file identity: " +
                        path);
                }
                finalPath = GetFinalPathFromHandle(retained, path);
            }

            public void AssertSameFile(
                SealedStagedFileLease expected,
                string context)
            {
                if (expected == null)
                {
                    throw new ArgumentNullException("expected");
                }
                if (String.IsNullOrWhiteSpace(context))
                {
                    context = "Sealed staged file";
                }
                ByHandleFileInformation information;
                string observedFinal;
                GetRetainedInformation(out information, out observedFinal);
                expected.AssertObservedFileIdentity(
                    information,
                    observedFinal,
                    context);
            }

            public void AssertMatchesExpectedIdentity(
                ValidatedMutableFileLease expected,
                string context)
            {
                if (expected == null)
                {
                    throw new ArgumentNullException("expected");
                }
                if (String.IsNullOrWhiteSpace(context))
                {
                    context = "Sealed staged file";
                }
                ByHandleFileInformation information;
                string observedFinal;
                GetRetainedInformation(out information, out observedFinal);
                expected.AssertObservedIdentity(
                    information,
                    observedFinal,
                    context);
            }

            public void AssertRetainedPath(
                string expectedPath,
                string context)
            {
                if (String.IsNullOrWhiteSpace(expectedPath))
                {
                    throw new ArgumentException(
                        "Expected retained path cannot be empty.",
                        "expectedPath");
                }
                if (String.IsNullOrWhiteSpace(context))
                {
                    context = "Sealed staged file";
                }
                ByHandleFileInformation information;
                string observedFinal;
                GetRetainedInformation(out information, out observedFinal);
                AssertObservedFileIdentity(
                    information,
                    observedFinal,
                    context);
                if (!String.Equals(
                        NormalizePath(observedFinal),
                        NormalizePath(expectedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        context + " retained path differs from its expected " +
                        "path '" + NormalizePath(expectedPath) + "': " +
                        observedFinal);
                }
            }

            public void RevalidateRetainedIdentity()
            {
                ByHandleFileInformation information;
                string observedFinal;
                GetRetainedInformation(out information, out observedFinal);
                AssertObservedFileIdentity(
                    information,
                    observedFinal,
                    "Retained sealed staged file");
            }

            private void AssertRecoveryIdentity(
                ByHandleFileInformation information,
                string observedPath,
                string context)
            {
                if ((information.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        context + " is a reparse point: " + observedPath);
                }
                if ((information.FileAttributes &
                        FileAttributeDirectory) != 0)
                {
                    throw new InvalidOperationException(
                        context + " is not a regular file: " + observedPath);
                }
                if (information.VolumeSerialNumber != volumeSerialNumber ||
                    information.FileIndexHigh != fileIndexHigh ||
                    information.FileIndexLow != fileIndexLow)
                {
                    throw new InvalidOperationException(
                        context + " identity changed: " + observedPath);
                }
            }

            public bool RetainedPathEquals(string expectedPath)
            {
                if (String.IsNullOrWhiteSpace(expectedPath))
                {
                    throw new ArgumentException(
                        "Expected retained path cannot be empty.",
                        "expectedPath");
                }
                ByHandleFileInformation information;
                string observedFinal;
                GetRetainedInformation(out information, out observedFinal);
                AssertRecoveryIdentity(
                    information,
                    observedFinal,
                    "Retained sealed staged file");
                return String.Equals(
                    NormalizePath(observedFinal),
                    NormalizePath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }

            private void Rewind()
            {
                if (stream == null)
                {
                    throw new ObjectDisposedException(
                        "SealedStagedFileLease");
                }
                stream.Position = 0;
            }

            public string ReadAllTextUtf8(long maximumBytes)
            {
                if (maximumBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException("maximumBytes");
                }
                if (stream == null)
                {
                    throw new ObjectDisposedException(
                        "SealedStagedFileLease");
                }
                if (stream.Length > maximumBytes)
                {
                    throw new InvalidOperationException(
                        "Sealed staged file exceeds its maximum strict UTF-8 " +
                        "metadata size of " + maximumBytes + " bytes: " +
                        FinalPath);
                }
                Rewind();
                using (StreamReader reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    false,
                    65536,
                    true))
                {
                    string text = reader.ReadToEnd();
                    if (text.Length > 0 && text[0] == '\uFEFF')
                    {
                        return text.Substring(1);
                    }
                    return text;
                }
            }

            public string ComputeHash(string algorithm)
            {
                HashAlgorithm hashAlgorithm;
                if (String.Equals(
                    algorithm,
                    "SHA1",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA1.Create();
                }
                else if (String.Equals(
                    algorithm,
                    "SHA256",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA256.Create();
                }
                else if (String.Equals(
                    algorithm,
                    "SHA512",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA512.Create();
                }
                else
                {
                    throw new ArgumentException(
                        "Unsupported sealed staged-file hash algorithm: " +
                        algorithm,
                        "algorithm");
                }

                using (hashAlgorithm)
                {
                    Rewind();
                    return BitConverter.ToString(
                        hashAlgorithm.ComputeHash(stream)).Replace("-", "");
                }
            }

            public void CopyToFile(string destinationPath)
            {
                Rewind();
                using (FileStream output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.WriteThrough))
                {
                    stream.CopyTo(output, 65536);
                    output.Flush(true);
                }
            }

            public void CopyTo(Stream destination)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException("destination");
                }
                Rewind();
                stream.CopyTo(destination, 65536);
            }

            public void Revalidate()
            {
                ByHandleFileInformation information;
                string finalPath;
                GetRetainedInformation(out information, out finalPath);
                AssertObservedIdentity(
                    information,
                    finalPath,
                    "Retained sealed staged file");

                using (SafeFileHandle observed = CreateFileW(
                    path,
                    GenericRead,
                    FileShareRead | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero))
                {
                    if (observed.IsInvalid)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not reopen sealed staged file by path: " +
                            path);
                    }
                    if (!GetFileInformationByHandle(observed, out information))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not validate sealed staged-file path " +
                            "identity: " + path);
                    }
                    finalPath = GetFinalPathFromHandle(observed, path);
                    AssertObservedIdentity(
                        information,
                        finalPath,
                        "Sealed staged-file path");
                }
            }

            public void RenameRetained(
                string destinationPath,
                bool replaceIfExists)
            {
                RenameRetainedCore(
                    destinationPath,
                    replaceIfExists,
                    true);
            }

            public void RenameRetainedForRecovery(string destinationPath)
            {
                RenameRetainedCore(destinationPath, false, false);
            }

            private void RenameRetainedCore(
                string destinationPath,
                bool replaceIfExists,
                bool requireSingleLink)
            {
                // Link count and reparse/type state are checked immediately
                // before the linearizing rename as well as after it. Callers
                // can therefore recover a post-check alias injection instead
                // of silently committing an aliased public object.
                if (requireSingleLink)
                {
                    RevalidateRetainedIdentity();
                }
                else
                {
                    ByHandleFileInformation recoveryInformation;
                    string recoveryFinal;
                    GetRetainedInformation(
                        out recoveryInformation,
                        out recoveryFinal);
                    AssertRecoveryIdentity(
                        recoveryInformation,
                        recoveryFinal,
                        "Recovery source");
                }
                string destinationFull = NormalizePath(destinationPath);
                byte[] nameBytes = Encoding.Unicode.GetBytes(destinationFull);
                int rootOffset = IntPtr.Size == 8 ? 8 : 4;
                int lengthOffset = checked(rootOffset + IntPtr.Size);
                int nameOffset = checked(lengthOffset + 4);
                int fixedStructureSize = checked(nameOffset + 4);
                int bufferSize = checked(
                    fixedStructureSize + nameBytes.Length);
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    for (int index = 0; index < bufferSize; index++)
                    {
                        Marshal.WriteByte(buffer, index, 0);
                    }
                    Marshal.WriteByte(
                        buffer,
                        0,
                        replaceIfExists ? (byte)1 : (byte)0);
                    Marshal.WriteIntPtr(
                        buffer,
                        rootOffset,
                        IntPtr.Zero);
                    Marshal.WriteInt32(
                        buffer,
                        lengthOffset,
                        nameBytes.Length);
                    Marshal.Copy(
                        nameBytes,
                        0,
                        IntPtr.Add(buffer, nameOffset),
                        nameBytes.Length);
                    if (!SetFileInformationByHandle(
                            PublicationHandle,
                            FileRenameInfo,
                            buffer,
                            checked((uint)bufferSize)))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not atomically rename the sealed staged " +
                            "file to: " + destinationFull);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                if (requireSingleLink)
                {
                    AssertRetainedPath(
                        destinationFull,
                        "Renamed sealed staged file");
                }
                else
                {
                    ByHandleFileInformation recoveryInformation;
                    string recoveryFinal;
                    GetRetainedInformation(
                        out recoveryInformation,
                        out recoveryFinal);
                    AssertRecoveryIdentity(
                        recoveryInformation,
                        recoveryFinal,
                        "Renamed recovery file");
                    if (!String.Equals(
                            NormalizePath(recoveryFinal),
                            destinationFull,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Recovery file did not land at its retained-handle " +
                            "destination: " + recoveryFinal);
                    }
                }
            }

            public void DeleteRetained()
            {
                IntPtr disposition = Marshal.AllocHGlobal(1);
                try
                {
                    Marshal.WriteByte(disposition, 0, 1);
                    if (!SetFileInformationByHandle(
                            PublicationHandle,
                            FileDispositionInfo,
                            disposition,
                            1))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not delete the exact retained sealed " +
                            "staged file.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(disposition);
                }
            }

            public void Dispose()
            {
                if (publicationControl != null)
                {
                    publicationControl.Dispose();
                    publicationControl = null;
                }
                if (stream != null)
                {
                    stream.Dispose();
                    stream = null;
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                    parentLease = null;
                }
            }
        }

        public static SealedStagedFileLease OpenSealedStagedFile(
            string path,
            string root)
        {
            return OpenRetainedSingleLinkFile(path, root, false);
        }

        public static SealedStagedFileLease OpenReplaceableDestinationFile(
            string path,
            string root)
        {
            // Open the initial identity with permissive sharing only long
            // enough for AcquirePublicationControl to derive an exact-object
            // DELETE-capable, deny-write handle through ReOpenFile. The caller
            // immediately revalidates identity, path, and SHA-256 before use.
            return OpenRetainedSingleLinkFile(path, root, true);
        }

        private static SealedStagedFileLease OpenRetainedSingleLinkFile(
            string path,
            string root,
            bool allowWriteSharing)
        {
            string fullPath = NormalizePath(path);
            string fullRoot = NormalizePath(root);
            if (!IsWithin(fullPath, fullRoot, false))
            {
                throw new InvalidOperationException(
                    "Sealed staged file escaped its declared root: " +
                    fullPath);
            }
            string volumeRoot = Path.GetPathRoot(fullRoot);
            string parentPath = Path.GetDirectoryName(fullPath);
            DirectoryChainLease parentLease = null;
            SafeFileHandle handle = null;
            try
            {
                parentLease = OpenDirectoryChainLease(
                    parentPath,
                    volumeRoot,
                    FileListDirectory);
                string rootFinal = GetFinalPath(fullRoot);
                if (!IsWithin(rootFinal, parentLease.RootFinalPath, true))
                {
                    throw new InvalidOperationException(
                        "Sealed staged-file root escaped its retained volume " +
                        "root: " + rootFinal);
                }

                // The sealed variant intentionally omits FILE_SHARE_WRITE,
                // freezing the exact object's bytes while still permitting
                // ordinary readers. Its DELETE-capable publication handle is
                // later reopened from this exact handle, never from the path.
                // Replaceable transaction variants opt into write sharing and
                // are always rechecked by retained identity plus SHA-256.
                handle = CreateFileW(
                    fullPath,
                    GenericRead,
                    FileShareRead |
                        FileShareDelete |
                        (allowWriteSharing ? FileShareWrite : 0),
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagSequentialScan,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;
                    throw new Win32Exception(
                        error,
                        "Could not acquire sealed staged-file handle: " +
                        fullPath);
                }
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read sealed staged-file identity: " +
                        fullPath);
                }
                ValidatedMutableFileLease.AssertRegularSingleLink(
                    information,
                    fullPath);
                string finalPath = GetFinalPathFromHandle(handle, fullPath);
                if (!IsWithin(finalPath, rootFinal, false) ||
                    !String.Equals(
                        NormalizePath(finalPath),
                        NormalizePath(fullPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Sealed staged file escaped its retained declared " +
                        "root '" + rootFinal + "': " + finalPath);
                }
                SealedStagedFileLease result =
                    new SealedStagedFileLease(
                        parentLease,
                        handle,
                        fullPath,
                        finalPath,
                        information);
                parentLease = null;
                handle = null;
                return result;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                }
            }
        }

        public sealed class ValidatedInputFile : IDisposable
        {
            private FileStream stream;
            private DirectoryChainLease parentLease;

            internal ValidatedInputFile(
                DirectoryChainLease retainedParentLease,
                SafeFileHandle handle,
                string finalPath,
                uint linkCount)
            {
                parentLease = retainedParentLease;
                stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    65536,
                    false);
                FinalPath = finalPath;
                LinkCount = linkCount;
                Length = stream.Length;
            }

            public string FinalPath { get; private set; }
            public uint LinkCount { get; private set; }
            public long Length { get; private set; }

            private void Rewind()
            {
                if (stream == null)
                {
                    throw new ObjectDisposedException("ValidatedInputFile");
                }
                stream.Position = 0;
            }

            public void CopyTo(Stream destination)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException("destination");
                }
                Rewind();
                stream.CopyTo(destination, 65536);
            }

            public void CopyToFile(string destinationPath)
            {
                Rewind();
                using (FileStream output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.WriteThrough))
                {
                    stream.CopyTo(output, 65536);
                    output.Flush(true);
                }
            }

            public string ReadAllTextUtf8(long maximumBytes)
            {
                if (maximumBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException("maximumBytes");
                }
                if (Length > maximumBytes)
                {
                    throw new InvalidOperationException(
                        "Packaging input exceeds its maximum strict UTF-8 " +
                        "metadata size of " + maximumBytes + " bytes: " +
                        FinalPath);
                }
                Rewind();
                using (StreamReader reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    false,
                    65536,
                    true))
                {
                    string text = reader.ReadToEnd();
                    if (text.Length > 0 && text[0] == '\uFEFF')
                    {
                        return text.Substring(1);
                    }
                    return text;
                }
            }

            public string ComputeHash(string algorithm)
            {
                HashAlgorithm hashAlgorithm;
                if (String.Equals(
                    algorithm,
                    "SHA1",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA1.Create();
                }
                else if (String.Equals(
                    algorithm,
                    "SHA256",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA256.Create();
                }
                else if (String.Equals(
                    algorithm,
                    "SHA512",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hashAlgorithm = SHA512.Create();
                }
                else
                {
                    throw new ArgumentException(
                        "Unsupported packaging input hash algorithm: " + algorithm,
                        "algorithm");
                }

                using (hashAlgorithm)
                {
                    Rewind();
                    byte[] hash = hashAlgorithm.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }

            public void Dispose()
            {
                if (stream != null)
                {
                    stream.Dispose();
                    stream = null;
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                    parentLease = null;
                }
            }
        }

        public static ValidatedInputFile OpenValidatedInputFile(
            string path,
            string root,
            bool rejectHardLinks)
        {
            string fullPath = NormalizePath(path);
            string fullRoot = NormalizePath(root);
            if (!IsWithin(fullPath, fullRoot, false))
            {
                throw new InvalidOperationException(
                    "Packaging input escaped its declared root: " + fullPath);
            }
            string volumeRoot = Path.GetPathRoot(fullRoot);
            string parentPath = Path.GetDirectoryName(fullPath);
            DirectoryChainLease parentLease = null;
            SafeFileHandle handle = null;
            try
            {
                parentLease = OpenDirectoryChainLease(
                    parentPath,
                    volumeRoot,
                    FileListDirectory);
                string rootFinal = GetFinalPath(fullRoot);
                if (!IsWithin(rootFinal, parentLease.RootFinalPath, true))
                {
                    throw new InvalidOperationException(
                        "Packaging input root escaped its retained volume " +
                        "root: " + rootFinal);
                }

                handle = CreateFileW(
                fullPath,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;
                    throw new Win32Exception(
                        error,
                        "Could not open packaging input for retained-handle " +
                        "validation: " + fullPath);
                }
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not read packaging input identity: " + path);
                }
                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Packaging input is a reparse point: " + path);
                }
                if ((information.FileAttributes & FileAttributeDirectory) != 0)
                {
                    throw new InvalidOperationException(
                        "Packaging input is not a regular file: " + path);
                }
                if (rejectHardLinks && information.NumberOfLinks != 1)
                {
                    throw new InvalidOperationException(
                        "Packaging input is a hard-link alias: " + path);
                }

                string finalPath = GetFinalPathFromHandle(handle, fullPath);
                if (!IsWithin(finalPath, rootFinal, false))
                {
                    throw new InvalidOperationException(
                        "Packaging input escaped its retained declared root '" +
                        rootFinal + "': " + finalPath);
                }
                ValidatedInputFile result = new ValidatedInputFile(
                    parentLease,
                    handle,
                    finalPath,
                    information.NumberOfLinks);
                parentLease = null;
                handle = null;
                return result;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                if (parentLease != null)
                {
                    parentLease.Dispose();
                }
            }
        }
    }
}
'@
}

function Invoke-DesktopPetStagingMutationTestHook {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $hookVariable = Get-Variable `
        -Name DesktopPetStagingMutationTestHook `
        -Scope Script `
        -ErrorAction SilentlyContinue
    if ($null -ne $hookVariable -and
        $hookVariable.Value -is [scriptblock]) {
        & $hookVariable.Value $Operation $Path
    }
}

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

function Get-DesktopPetExistingPathChain {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) {
        throw "Path has no filesystem root: '$fullPath'."
    }

    $chain = @()
    if (Test-Path -LiteralPath $pathRoot) {
        $chain += Get-Item -LiteralPath $pathRoot -Force -ErrorAction Stop
    }
    $current = $pathRoot
    $remainder = $fullPath.Substring($pathRoot.Length)
    $segments = @(
        $remainder.Split(
            [char[]]@(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)
    )
    foreach ($segment in $segments) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }
        $chain += Get-Item -LiteralPath $current -Force -ErrorAction Stop
    }
    return $chain
}

function Get-DesktopPetFinalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot resolve the final path of a missing filesystem entry: $Path"
    }
    return Get-DesktopPetCanonicalPath -Path (
        [DesktopPet.Packaging.FinalPathResolver]::GetFinalPath(
            [IO.Path]::GetFullPath($Path)))
}

function Assert-DesktopPetPathChainSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
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

    foreach ($item in @(Get-DesktopPetExistingPathChain -Path $resolvedTrustedRoot)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Trusted staging path traverses a reparse point: $($item.FullName)"
        }
    }
    $trustedFinal = Get-DesktopPetFinalPath -Path $resolvedTrustedRoot

    foreach ($item in @(Get-DesktopPetExistingPathChain -Path $resolvedPath)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Staging path traverses a reparse point: $($item.FullName)"
        }
        if (Test-DesktopPetPathWithin `
                -Path $item.FullName `
                -Root $resolvedTrustedRoot `
                -AllowRoot) {
            $itemFinal = Get-DesktopPetFinalPath -Path $item.FullName
            if (-not (Test-DesktopPetPathWithin `
                    -Path $itemFinal `
                    -Root $trustedFinal `
                    -AllowRoot)) {
                throw (
                    "Staging path escaped the trusted physical root " +
                    "'$trustedFinal': $itemFinal")
            }
        }
    }
    return $trustedFinal
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
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
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

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
        throw "Output file path resolves to a directory: $resolvedPath"
    }

    $resolvedParent = Split-Path -Parent $resolvedPath
    if (-not (Test-Path -LiteralPath $resolvedParent -PathType Container)) {
        throw "Output file parent is missing or is not a directory: $resolvedParent"
    }
    $parentFinal = Get-DesktopPetFinalPath -Path $resolvedParent
    $candidateFinal = Join-Path $parentFinal ([IO.Path]::GetFileName($resolvedPath))
    if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
        $candidateFinal = Get-DesktopPetFinalPath -Path $resolvedPath
        $linkCount =
            [DesktopPet.Packaging.FinalPathResolver]::GetLinkCount(
                $resolvedPath)
        if ($linkCount -gt 1) {
            throw (
                "Output file is a hard-link alias and cannot be safely " +
                "replaced or appended: $resolvedPath")
        }
    }

    foreach ($protectedPath in @($ProtectedPaths)) {
        if ([string]::IsNullOrWhiteSpace($protectedPath)) {
            throw 'Protected output-alias path cannot be empty.'
        }
        $resolvedProtected =
            Get-DesktopPetCanonicalPath -Path $protectedPath
        $protectedFinal = $resolvedProtected
        if (Test-Path -LiteralPath $resolvedProtected) {
            $protectedFinal =
                Get-DesktopPetFinalPath -Path $resolvedProtected
        }
        if ($resolvedPath.Equals(
                $resolvedProtected,
                [StringComparison]::OrdinalIgnoreCase) -or
            $candidateFinal.Equals(
                $protectedFinal,
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
        $resolvedProtected =
            Get-DesktopPetCanonicalPath -Path $protectedDirectory
        $protectedFinal = $resolvedProtected
        if (Test-Path -LiteralPath $resolvedProtected -PathType Container) {
            $protectedFinal =
                Get-DesktopPetFinalPath -Path $resolvedProtected
        }
        if ((Test-DesktopPetPathWithin `
                -Path $resolvedPath `
                -Root $resolvedProtected `
                -AllowRoot) -or
            (Test-DesktopPetPathWithin `
                -Path $candidateFinal `
                -Root $protectedFinal `
                -AllowRoot)) {
            throw (
                "Output file overlaps a protected packaging input directory: " +
                $resolvedProtected)
        }
    }
    return $resolvedPath
}

function Write-DesktopPetNewFileBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @(),
        [string]$MutationOperation = 'before-create-new-file'
    )

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Create-new file root does not exist: $resolvedRoot"
    }
    $resolvedPath = Assert-DesktopPetOutputFileSafe `
        -Path $Path `
        -TrustedRoot $resolvedRoot `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories
    $parent = Split-Path -Parent $resolvedPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "Create-new file parent does not exist: $parent"
    }

    $parentLease = $null
    $stream = $null
    $created = $false
    try {
        $parentLease =
            [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
                $parent,
                $resolvedRoot)
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation $MutationOperation `
            -Path $resolvedPath
        $stream = New-Object IO.FileStream(
            $resolvedPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::WriteThrough)
        $created = $true
        if ($Bytes.Length -gt 0) {
            $stream.Write($Bytes, 0, $Bytes.Length)
        }
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        return $resolvedPath
    }
    catch {
        if ($null -ne $stream) {
            $stream.Dispose()
            $stream = $null
        }
        if ($created -and (Test-Path -LiteralPath $resolvedPath)) {
            try {
                Remove-DesktopPetSafeFile `
                    -Path $resolvedPath `
                    -AllowedRoot $resolvedRoot `
                    -TrustedRoot $resolvedRoot
            }
            catch {
                # Preserve the original create/write failure.
            }
        }
        throw
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        if ($null -ne $parentLease) {
            $parentLease.Dispose()
        }
    }
}

function Write-DesktopPetNewUtf8File {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content,
        [string[]]$ProtectedPaths = @(),
        [string[]]$ProtectedDirectories = @(),
        [string]$MutationOperation = 'before-create-new-file'
    )

    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    return Write-DesktopPetNewFileBytes `
        -Path $Path `
        -Root $Root `
        -Bytes $utf8.GetBytes($Content) `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories `
        -MutationOperation $MutationOperation
}

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

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Packaging input is missing or is not a file: $resolvedPath"
    }

    $rootFinal = Get-DesktopPetFinalPath -Path $resolvedRoot
    $input = $null
    try {
        $input =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedInputFile(
                $resolvedPath,
                $resolvedRoot,
                $RejectHardLinks)
        if (-not (Test-DesktopPetPathWithin `
                -Path $input.FinalPath `
                -Root $rootFinal)) {
            throw (
                "Packaging input escaped physical root '$rootFinal': " +
                $input.FinalPath)
        }
        return $input
    }
    catch {
        if ($null -ne $input) {
            $input.Dispose()
        }
        throw
    }
}

function Open-DesktopPetValidatedMutableFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw (
            "Mutable packaging file has an unsafe Windows leaf name: " +
            "'$leafName'.")
    }

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw (
            "Mutable packaging root is missing or is not a directory: " +
            $resolvedRoot)
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedRoot)) {
        throw (
            "Mutable packaging file must be strictly below its declared root " +
            "'$resolvedRoot': $resolvedPath")
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw (
            "Mutable packaging file is missing or is not a file: " +
            $resolvedPath)
    }

    $rootFinal = Get-DesktopPetFinalPath -Path $resolvedRoot
    $lease = $null
    try {
        $lease =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedMutableFile(
                $resolvedPath,
                $resolvedRoot)
        if (-not (Test-DesktopPetPathWithin `
                -Path $lease.FinalPath `
                -Root $rootFinal)) {
            throw (
                "Mutable packaging file escaped physical root '$rootFinal': " +
                $lease.FinalPath)
        }
        return $lease
    }
    catch {
        if ($null -ne $lease) {
            $lease.Dispose()
        }
        throw
    }
}

function Open-DesktopPetSealedStagedFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $leafName = Split-Path -Leaf $Path
    if (-not (Test-DesktopPetWindowsLeafName -Name $leafName)) {
        throw (
            "Sealed staged file has an unsafe Windows leaf name: " +
            "'$leafName'.")
    }

    $resolvedRoot = Get-DesktopPetCanonicalPath -Path $Root
    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw (
            "Sealed staged-file root is missing or is not a directory: " +
            $resolvedRoot)
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedRoot)) {
        throw (
            "Sealed staged file must be strictly below its declared root " +
            "'$resolvedRoot': $resolvedPath")
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw (
            "Sealed staged file is missing or is not a file: " +
            $resolvedPath)
    }

    $rootFinal = Get-DesktopPetFinalPath -Path $resolvedRoot
    $lease = $null
    try {
        $lease =
            [DesktopPet.Packaging.FinalPathResolver]::OpenSealedStagedFile(
                $resolvedPath,
                $resolvedRoot)
        if (-not (Test-DesktopPetPathWithin `
                -Path $lease.FinalPath `
                -Root $rootFinal)) {
            throw (
                "Sealed staged file escaped physical root '$rootFinal': " +
                $lease.FinalPath)
        }
        return $lease
    }
    catch {
        if ($null -ne $lease) {
            $lease.Dispose()
        }
        throw
    }
}

function Copy-DesktopPetValidatedInputFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [bool]$RejectHardLinks = $true
    )

    $destinationFull = Get-DesktopPetCanonicalPath -Path $DestinationPath
    $destinationParent = Split-Path -Parent $destinationFull
    if ([string]::IsNullOrWhiteSpace($destinationParent) -or
        -not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        throw "Validated-copy destination parent is missing: $destinationParent"
    }
    $destinationFull = Assert-DesktopPetOutputFileSafe `
        -Path $destinationFull `
        -TrustedRoot $destinationParent `
        -ProtectedPaths @($Path) `
        -ProtectedDirectories @($Root)
    if (Test-Path -LiteralPath $destinationFull) {
        throw "Validated-copy destination must not already exist: $destinationFull"
    }

    $input = Open-DesktopPetValidatedInputFile `
        -Path $Path `
        -Root $Root `
        -RejectHardLinks:$RejectHardLinks
    $destinationLease = $null
    try {
        $destinationVolumeRoot =
            [IO.Path]::GetPathRoot($destinationParent)
        $destinationLease = [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
            $destinationParent,
            $destinationVolumeRoot)
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'copy-create' `
            -Path $destinationFull
        $input.CopyToFile($destinationFull)
    }
    finally {
        if ($null -ne $destinationLease) {
            $destinationLease.Dispose()
        }
        $input.Dispose()
    }
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
    $temporaryParent = Split-Path -Parent $temporaryFull
    $destinationParent = Split-Path -Parent $destinationFull
    if ($temporaryParent.Equals(
            $destinationParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Atomic publication requires its temporary file to use a ' +
            'separate private staging directory.')
    }

    $temporaryFull = Assert-DesktopPetOutputFileSafe `
        -Path $temporaryFull `
        -TrustedRoot $TrustedRoot `
        -ProtectedPaths @($ProtectedPaths + $destinationFull) `
        -ProtectedDirectories $ProtectedDirectories
    if (-not (Test-Path -LiteralPath $temporaryFull -PathType Leaf)) {
        throw "Atomic publication temporary file is missing: $temporaryFull"
    }
    $destinationFull = Assert-DesktopPetOutputFileSafe `
        -Path $destinationFull `
        -TrustedRoot $TrustedRoot `
        -ProtectedPaths $ProtectedPaths `
        -ProtectedDirectories $ProtectedDirectories

    if ($DestinationMustBeAbsent -and
        ($null -ne $ExpectedDestinationIdentity -or
         -not [string]::IsNullOrWhiteSpace($ExpectedDestinationSha256))) {
        throw (
            'Atomic publication cannot require both an absent destination ' +
            'and expected destination identity/content.')
    }

    $ownsSealedTemporaryFile = $false
    $expectedDestinationFile = $null
    $publication = $null
    $transactionDirectory = $null
    $transactionDirectoryLease = $null
    $transactionDirectoryCreated = $false
    $preserveRecoveryEvidence = $false
    $publicationCommitted = $false
    try {
        if ($null -eq $SealedTemporaryFile) {
            $SealedTemporaryFile = Open-DesktopPetSealedStagedFile `
                -Path $temporaryFull `
                -Root $temporaryParent
            $ownsSealedTemporaryFile = $true
        }
        elseif (-not ($SealedTemporaryFile -is
                [DesktopPet.Packaging.FinalPathResolver+SealedStagedFileLease])) {
            throw (
                'SealedTemporaryFile must be returned by mutableLease.Seal() ' +
                'or Open-DesktopPetSealedStagedFile.')
        }
        if (-not $SealedTemporaryFile.OriginalPath.Equals(
                $temporaryFull,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Atomic publication received a sealed staged-file lease for ' +
                "a different path: $($SealedTemporaryFile.OriginalPath)")
        }
        $SealedTemporaryFile.Revalidate()
        if ($null -ne $ExpectedTemporaryIdentity) {
            $SealedTemporaryFile.AssertMatchesExpectedIdentity(
                $ExpectedTemporaryIdentity,
                'Atomic-publication input')
        }
        $observedTemporarySha256 =
            $SealedTemporaryFile.ComputeHash('SHA256')
        if ([string]::IsNullOrWhiteSpace($ExpectedTemporarySha256)) {
            $ExpectedTemporarySha256 = $observedTemporarySha256
        }
        elseif ($ExpectedTemporarySha256 -notmatch '\A[0-9A-Fa-f]{64}\z') {
            throw 'Atomic-publication input expected SHA-256 is invalid.'
        }
        elseif ($observedTemporarySha256 -ine $ExpectedTemporarySha256) {
            throw (
                'Atomic-publication input content changed after validation; ' +
                "expected $ExpectedTemporarySha256, observed " +
                "$observedTemporarySha256.")
        }

        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-publish-lease' `
            -Path $destinationFull

        if (-not $DestinationMustBeAbsent) {
            if (Test-Path -LiteralPath $destinationFull -PathType Leaf) {
                $expectedDestinationFile =
                    [DesktopPet.Packaging.FinalPathResolver]::
                        OpenReplaceableDestinationFile(
                            $destinationFull,
                            $destinationParent)
                # ReOpenFile derives the DELETE-capable publication handle
                # from this exact object rather than reopening its path. Its
                # share mode also freezes destination bytes before hashing.
                $expectedDestinationFile.AcquirePublicationControl()
                $expectedDestinationFile.Revalidate()
                if ($null -ne $ExpectedDestinationIdentity) {
                    $expectedDestinationFile.AssertMatchesExpectedIdentity(
                        $ExpectedDestinationIdentity,
                        'Atomic-publication destination')
                }
                $observedDestinationSha256 =
                    $expectedDestinationFile.ComputeHash('SHA256')
                if ([string]::IsNullOrWhiteSpace(
                        $ExpectedDestinationSha256)) {
                    $ExpectedDestinationSha256 =
                        $observedDestinationSha256
                }
                elseif ($ExpectedDestinationSha256 -notmatch
                        '\A[0-9A-Fa-f]{64}\z') {
                    throw (
                        'Atomic-publication destination expected SHA-256 is ' +
                        'invalid.')
                }
                elseif ($observedDestinationSha256 -ine
                        $ExpectedDestinationSha256) {
                    throw (
                        'Atomic-publication destination content changed after ' +
                        "validation; expected $ExpectedDestinationSha256, " +
                        "observed $observedDestinationSha256.")
                }
            }
            elseif (Test-Path -LiteralPath $destinationFull) {
                throw (
                    'Atomic publication destination exists but is not a ' +
                    "regular file: $destinationFull")
            }
            elseif ($null -ne $ExpectedDestinationIdentity -or
                -not [string]::IsNullOrWhiteSpace(
                    $ExpectedDestinationSha256)) {
                throw (
                    'Atomic-publication destination disappeared after its ' +
                    "expected state was recorded: $destinationFull")
            }
            else {
                $DestinationMustBeAbsent = $true
            }
        }

        $publication =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedPublication(
                $SealedTemporaryFile,
                $destinationFull,
                $TrustedRoot)
        $publication.AssertTemporarySha256($ExpectedTemporarySha256)
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'publish' `
            -Path $destinationFull
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'sealed-publish-post-check' `
            -Path $destinationFull
        if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
            $ExpectedTemporarySha256) {
            throw (
                'Atomic-publication input content changed after its sealed ' +
                'post-check.')
        }
        if ($null -ne $expectedDestinationFile) {
            $expectedDestinationFile.AssertRetainedPath(
                $destinationFull,
                'Atomic-publication destination identity')
            if ($expectedDestinationFile.ComputeHash('SHA256') -ine
                $ExpectedDestinationSha256) {
                throw (
                    'Atomic-publication destination content changed after its ' +
                    'sealed post-check.')
            }
        }

        if ($DestinationMustBeAbsent) {
            # SetFileInformationByHandle with ReplaceIfExists=false binds the
            # kernel operation to the exact sealed object and makes the
            # expected-absence decision atomically with the rename.
            $absenceValidationError = $null
            try {
                $SealedTemporaryFile.RenameRetained(
                    $destinationFull,
                    $false)
                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'sealed-publish-post-absent-rename' `
                    -Path $destinationFull
                $SealedTemporaryFile.AssertRetainedPath(
                    $destinationFull,
                    'Published sealed staged file')
                if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
                    $ExpectedTemporarySha256) {
                    throw (
                        'Published sealed staged-file content differs from its ' +
                        'validated SHA-256.')
                }
                [void](Assert-DesktopPetOutputFileSafe `
                    -Path $destinationFull `
                    -TrustedRoot $TrustedRoot `
                    -ProtectedPaths $ProtectedPaths `
                    -ProtectedDirectories $ProtectedDirectories)
                if (-not (Test-Path `
                        -LiteralPath $destinationFull `
                        -PathType Leaf)) {
                    throw (
                        'Atomic publication did not create its destination: ' +
                        $destinationFull)
                }
            }
            catch {
                $absenceValidationError = $_.Exception
            }
            if ($null -ne $absenceValidationError) {
                $absenceRecoveryError = $null
                $absenceRecoveryPath = $null
                try {
                    if (-not $SealedTemporaryFile.RetainedPathEquals(
                            $temporaryFull)) {
                        $absenceRecoveryPath = Join-Path $temporaryParent (
                            '.DesktopPet-publish-recovery-' +
                            [Guid]::NewGuid().ToString('N') +
                            '.bin')
                        # First remove the exact new link from whatever name
                        # its retained handle tracks. This permits recovery even
                        # when a hardlink was inserted after the no-replace
                        # rename, without touching a competing destination.
                        $SealedTemporaryFile.RenameRetainedForRecovery(
                            $absenceRecoveryPath)
                        if (-not (Test-Path -LiteralPath $temporaryFull)) {
                            $SealedTemporaryFile.RenameRetainedForRecovery(
                                $temporaryFull)
                            $absenceRecoveryPath = $null
                        }
                    }
                }
                catch {
                    $absenceRecoveryError = $_.Exception
                }
                if ($null -ne $absenceRecoveryError -or
                    $null -ne $absenceRecoveryPath) {
                    $recoveryDetail = if ($null -ne $absenceRecoveryError) {
                        $absenceRecoveryError.Message
                    }
                    else {
                        'The original staged-input name is occupied.'
                    }
                    throw (
                        'Expected-absence atomic publication failed after its ' +
                        'exact retained rename. The public new-file link was ' +
                        'removed without overwriting a competitor. Recovery ' +
                        "evidence was preserved at '$absenceRecoveryPath'. " +
                        "Validation: $($absenceValidationError.Message) " +
                        "Recovery: $recoveryDetail")
                }
                throw (
                    'Expected-absence atomic publication was rejected; the ' +
                    'exact staged input was restored. Validation: ' +
                    $absenceValidationError.Message)
            }
            # No validation after this point is allowed to turn the accepted
            # no-replace commit into a failure. The final retained
            # identity/hash/path checks above are its linearization point.
            # Subsequent disposal is best-effort and cannot reject the commit.
            $publicationCommitted = $true
        }
        else {
            # Preserve the exact expected destination by retained handle before
            # attempting the no-replace commit. This creates a public-name gap,
            # but no operation in this transaction overwrites an entry that
            # wins that gap: every forward and recovery rename is no-replace.
            $transactionDirectory = Join-Path $destinationParent (
                '.DesktopPet-publish-transaction-' +
                [Guid]::NewGuid().ToString('N'))
            $transactionDirectoryLease = Open-DesktopPetNewScratchDirectory `
                -Path $transactionDirectory `
                -AllowedRoot $destinationParent `
                -TrustedRoot $TrustedRoot `
                -ProtectedPaths @(
                    $ProtectedPaths +
                    $temporaryFull +
                    $destinationFull) `
                -ProtectedDirectories $ProtectedDirectories
            $transactionDirectoryCreated = $true
            $capturedPath =
                Join-Path $transactionDirectory 'displaced.bin'
            $recoveryPath = Join-Path $transactionDirectory (
                'published-' + [Guid]::NewGuid().ToString('N') + '.bin')

            # Set this before the first linearizing rename. If that rename
            # succeeds but its postcondition fails, the exact old object is
            # still retained and the private directory must survive.
            $preserveRecoveryEvidence = $true
            $transactionValidationError = $null
            try {
                $expectedDestinationFile.AssertRetainedPath(
                    $destinationFull,
                    'Expected atomic-publication destination')
                if ($expectedDestinationFile.ComputeHash('SHA256') -ine
                    $ExpectedDestinationSha256) {
                    throw (
                        'Atomic-publication destination content changed before ' +
                        'its exact retained backup move.')
                }
                $expectedDestinationFile.RenameRetained(
                    $capturedPath,
                    $false)
                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'sealed-publish-post-backup' `
                    -Path $destinationFull
                $expectedDestinationFile.AssertRetainedPath(
                    $capturedPath,
                    'Retained atomic-publication backup')
                if ($expectedDestinationFile.ComputeHash('SHA256') -ine
                    $ExpectedDestinationSha256) {
                    throw (
                        'Retained atomic-publication backup differs from the ' +
                        'validated destination bytes.')
                }

                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'sealed-publish-before-final-rename' `
                    -Path $destinationFull
                # Revalidate after the adversarial hook and again inside the
                # retained-handle rename immediately before its kernel call.
                $SealedTemporaryFile.RevalidateRetainedIdentity()
                if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
                    $ExpectedTemporarySha256) {
                    throw (
                        'Atomic-publication input changed before its exact ' +
                        'retained commit.')
                }
                $SealedTemporaryFile.RenameRetained(
                    $destinationFull,
                    $false)
                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'sealed-publish-post-final-rename' `
                    -Path $destinationFull
                $SealedTemporaryFile.AssertRetainedPath(
                    $destinationFull,
                    'Published exact sealed staged file')
                if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
                    $ExpectedTemporarySha256) {
                    throw (
                        'Published exact sealed staged-file content differs ' +
                        'from its validated SHA-256.')
                }
                # This is the last adversarial checkpoint before commit
                # acceptance. Any new-public alias detected here is recovered
                # while the exact prior destination still exists.
                Invoke-DesktopPetStagingMutationTestHook `
                    -Operation 'sealed-publish-before-backup-cleanup' `
                    -Path $destinationFull
                $SealedTemporaryFile.AssertRetainedPath(
                    $destinationFull,
                    'Pre-linearization published sealed staged file')
                if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
                    $ExpectedTemporarySha256) {
                    throw (
                        'Pre-linearization published staged-file content ' +
                        'differs from its validated SHA-256.')
                }
                [void](Assert-DesktopPetOutputFileSafe `
                    -Path $destinationFull `
                    -TrustedRoot $TrustedRoot `
                    -ProtectedPaths $ProtectedPaths `
                    -ProtectedDirectories $ProtectedDirectories)
            }
            catch {
                $transactionValidationError = $_.Exception
            }

            if ($null -ne $transactionValidationError) {
                $rollbackError = $null
                try {
                    Invoke-DesktopPetStagingMutationTestHook `
                        -Operation 'sealed-publish-before-rollback' `
                        -Path $destinationFull

                    # If the exact new object left its original private name,
                    # recover that object by handle. The relaxed recovery
                    # primitive intentionally permits multiple hard links so
                    # an alias race cannot strand new bytes at the public name.
                    $sealedRecoveryPath = $null
                    if (-not $SealedTemporaryFile.RetainedPathEquals(
                            $temporaryFull)) {
                        if (-not (Test-Path -LiteralPath $temporaryFull)) {
                            $SealedTemporaryFile.RenameRetainedForRecovery(
                                $temporaryFull)
                        }
                        else {
                            $sealedRecoveryPath = $recoveryPath
                            $SealedTemporaryFile.RenameRetainedForRecovery(
                                $sealedRecoveryPath)
                        }
                    }

                    $backupAtDestination =
                        $expectedDestinationFile.RetainedPathEquals(
                            $destinationFull)
                    if (-not $backupAtDestination) {
                        if (-not $expectedDestinationFile.RetainedPathEquals(
                                $capturedPath)) {
                            throw (
                                'The exact prior destination is retained at an ' +
                                'unexpected recovery path.')
                        }
                        if (Test-Path -LiteralPath $destinationFull) {
                            throw (
                                'The public destination is occupied by a ' +
                                'competitor; refusing to overwrite it during ' +
                                'recovery.')
                        }
                        $expectedDestinationFile.RenameRetained(
                            $destinationFull,
                            $false)
                    }
                    $expectedDestinationFile.AssertRetainedPath(
                        $destinationFull,
                        'Restored atomic-publication destination')
                    if ($expectedDestinationFile.ComputeHash('SHA256') -ine
                        $ExpectedDestinationSha256) {
                        throw (
                            'Restored destination differs from its validated ' +
                            'SHA-256.')
                    }

                    if ($null -ne $sealedRecoveryPath) {
                        if (Test-Path -LiteralPath $temporaryFull) {
                            throw (
                                'The original staged-input name is occupied; ' +
                                'the exact new object remains in recovery.')
                        }
                        $SealedTemporaryFile.RenameRetainedForRecovery(
                            $temporaryFull)
                    }
                    if (-not $SealedTemporaryFile.RetainedPathEquals(
                            $temporaryFull)) {
                        throw (
                            'Recovery did not return the exact staged input to ' +
                            'its original private name.')
                    }
                    if ($SealedTemporaryFile.ComputeHash('SHA256') -ine
                        $ExpectedTemporarySha256) {
                        throw (
                            'Recovered staged input differs from its validated ' +
                            'SHA-256.')
                    }
                    Invoke-DesktopPetStagingMutationTestHook `
                        -Operation 'sealed-publish-post-rollback' `
                        -Path $destinationFull
                    $preserveRecoveryEvidence = $false
                }
                catch {
                    $rollbackError = $_.Exception
                }
                if ($null -ne $rollbackError) {
                    throw (
                        'Atomic publication failed and lossless recovery could ' +
                        'not restore every original name. No competing entry ' +
                        'was overwritten. Recovery evidence was preserved at ' +
                        "'$transactionDirectory'. Validation: " +
                        "$($transactionValidationError.Message) Recovery: " +
                        $rollbackError.Message)
                }
                throw (
                    'Atomic publication was rejected; the exact destination ' +
                    'and staged input were restored. Validation: ' +
                    $transactionValidationError.Message)
            }

            # The final retained identity/hash/path checks above are the commit
            # linearization point. From here on the exact new destination is
            # accepted; backup and directory cleanup are best-effort and must
            # never convert that accepted commit into a failure.
            $publicationCommitted = $true
            try {
                $expectedDestinationFile.AssertRetainedPath(
                    $capturedPath,
                    'Committed atomic-publication backup')
                if ($expectedDestinationFile.ComputeHash('SHA256') -ine
                    $ExpectedDestinationSha256) {
                    throw (
                        'Committed atomic-publication backup differs from its ' +
                        'validated SHA-256.')
                }
                $expectedDestinationFile.RevalidateRetainedIdentity()
                $expectedDestinationFile.DeleteRetained()
                $preserveRecoveryEvidence = $false
            }
            catch {
                $preserveRecoveryEvidence = $true
                $cleanupWarning = (
                    'Atomic publication committed successfully, but cleanup ' +
                    'of the exact prior destination failed. Recovery evidence ' +
                    "was preserved at '$transactionDirectory'. Cleanup: " +
                    $_.Exception.Message)
                try {
                    Write-Warning $cleanupWarning
                }
                catch {
                    # WarningAction Stop must not reject an already accepted
                    # commit. The warning record was emitted before PowerShell
                    # promoted it; only that promotion is suppressed here.
                }
            }
        }
    }
    finally {
        if ($null -ne $publication) {
            $publication.Dispose()
        }
        if ($null -ne $expectedDestinationFile) {
            $expectedDestinationFile.Dispose()
        }
        if ($null -ne $transactionDirectoryLease) {
            $transactionDirectoryLease.Dispose()
        }
        if ($transactionDirectoryCreated -and
            -not $preserveRecoveryEvidence -and
            (Test-Path -LiteralPath $transactionDirectory)) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $transactionDirectory `
                    -AllowedRoot $destinationParent `
                    -TrustedRoot $TrustedRoot
            }
            catch {
                $preserveRecoveryEvidence = $true
                $cleanupContext = if ($publicationCommitted) {
                    'Atomic publication committed successfully'
                }
                else {
                    'Atomic-publication recovery completed'
                }
                $cleanupWarning = (
                    "$cleanupContext, but its private transaction directory " +
                    "could not be removed. Recovery evidence was preserved at " +
                    "'$transactionDirectory'. Cleanup: " +
                    $_.Exception.Message)
                try {
                    Write-Warning $cleanupWarning
                }
                catch {
                    # Post-linearization cleanup is explicitly non-fatal even
                    # when the caller promotes warnings to terminating errors.
                }
            }
        }
        if ($ownsSealedTemporaryFile -and
            $null -ne $SealedTemporaryFile) {
            $SealedTemporaryFile.Dispose()
        }
    }

    return $destinationFull
}

function Assert-DesktopPetDirectoryTreeSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedFinalRoot
    )

    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Staging tree contains a reparse point: $($item.FullName)"
        }
        $itemFinal = Get-DesktopPetFinalPath -Path $item.FullName
        if (-not (Test-DesktopPetPathWithin `
                -Path $itemFinal `
                -Root $AllowedFinalRoot `
                -AllowRoot)) {
            throw (
                "Staging tree entry escaped the allowed physical root " +
                "'$AllowedFinalRoot': $itemFinal")
        }
        if ($item.PSIsContainer) {
            foreach ($child in @(
                    Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
                $pending.Push($child.FullName)
            }
        }
    }
}

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
    $resolvedAllowedRoot =
        Get-DesktopPetCanonicalPath -Path $AllowedRoot
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedAllowedRoot `
            -Root $resolvedTrustedRoot `
            -AllowRoot)) {
        throw (
            "New scratch allowed root escaped trusted root " +
            "'$resolvedTrustedRoot': $resolvedAllowedRoot")
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "New scratch directory must be strictly below allowed root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot -PathType Container)) {
        throw "New scratch parent must already exist: $resolvedAllowedRoot"
    }

    foreach ($protectedValue in @($ProtectedPaths + $ProtectedDirectories)) {
        if ([string]::IsNullOrWhiteSpace([string]$protectedValue)) {
            continue
        }
        $protected = Get-DesktopPetCanonicalPath -Path $protectedValue
        if ((Test-DesktopPetPathWithin `
                -Path $protected `
                -Root $resolvedPath `
                -AllowRoot) -or
            (Test-DesktopPetPathWithin `
                -Path $resolvedPath `
                -Root $protected `
                -AllowRoot)) {
            throw (
                "New scratch directory overlaps a protected path or " +
                "directory '$protected': $resolvedPath")
        }
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot)
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    if (Test-Path -LiteralPath $resolvedPath) {
        throw (
            "New scratch directory must be absent and caller-owned: " +
            $resolvedPath)
    }

    $creation = $null
    $lease = $null
    $created = $false
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-create-new-scratch-lease' `
            -Path $resolvedPath
        $creation =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedDirectoryCreation(
                $resolvedPath,
                $resolvedTrustedRoot)
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'create-new-scratch' `
            -Path $resolvedPath
        $creation.Create()
        $created = $true
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'after-create-new-scratch' `
            -Path $resolvedPath

        [void](Assert-DesktopPetPathChainSafe `
            -Path $resolvedPath `
            -TrustedRoot $resolvedTrustedRoot)
        $lease =
            [DesktopPet.Packaging.FinalPathResolver]::AcquireDirectoryChainLease(
                $resolvedPath,
                $resolvedTrustedRoot)
        $creation.Dispose()
        $creation = $null
        $result = $lease
        $lease = $null
        return $result
    }
    catch {
        if ($null -ne $lease) {
            $lease.Dispose()
            $lease = $null
        }
        if ($null -ne $creation) {
            $creation.Dispose()
            $creation = $null
        }
        if ($created -and (Test-Path -LiteralPath $resolvedPath)) {
            try {
                Remove-DesktopPetSafeDirectory `
                    -Path $resolvedPath `
                    -AllowedRoot $resolvedAllowedRoot `
                    -TrustedRoot $resolvedTrustedRoot
            }
            catch {
                # Preserve the original creation/lease failure.
            }
        }
        throw
    }
    finally {
        if ($null -ne $lease) {
            $lease.Dispose()
        }
        if ($null -ne $creation) {
            $creation.Dispose()
        }
    }
}

function Remove-DesktopPetSafeFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot =
        Get-DesktopPetCanonicalPath -Path $AllowedRoot
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedAllowedRoot `
            -Root $resolvedTrustedRoot `
            -AllowRoot)) {
        throw (
            "Allowed file-deletion root escaped trusted root " +
            "'$resolvedTrustedRoot': $resolvedAllowedRoot")
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to delete a file outside allowed root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot)
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot -PathType Container)) {
        throw "Allowed file-deletion root is not a directory: $resolvedAllowedRoot"
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Safe file-deletion target is not a file: $resolvedPath"
    }

    $deletion = $null
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-delete-file-lease' `
            -Path $resolvedPath
        $deletion =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedDeletion(
                $resolvedPath,
                $resolvedAllowedRoot,
                $resolvedTrustedRoot)
        if ([bool]$deletion.IsDirectory) {
            throw "Safe file-deletion target changed into a directory: $resolvedPath"
        }
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'delete-file' `
            -Path $resolvedPath
        $deletion.Delete()
    }
    finally {
        if ($null -ne $deletion) {
            $deletion.Dispose()
        }
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        throw "Safe file deletion left the target behind: $resolvedPath"
    }
}

function Remove-DesktopPetTreeNode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$AllowedFinalRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to delete a staging reparse point: $($item.FullName)"
    }
    $itemFinal = Get-DesktopPetFinalPath -Path $item.FullName
    if (-not (Test-DesktopPetPathWithin `
            -Path $itemFinal `
            -Root $AllowedFinalRoot `
            -AllowRoot)) {
        throw (
            "Refusing to delete a path outside the allowed physical root " +
            "'$AllowedFinalRoot': $itemFinal")
    }

    if ($item.PSIsContainer) {
        foreach ($child in @(
                Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
            Remove-DesktopPetTreeNode `
                -Path $child.FullName `
                -AllowedRoot $AllowedRoot `
                -AllowedFinalRoot $AllowedFinalRoot `
                -TrustedRoot $TrustedRoot
        }
        $remaining = @(
            Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)
        if ($remaining.Count -ne 0) {
            throw (
                "Staging directory changed during safe deletion; refusing a " +
                "recursive fallback: $($item.FullName)")
        }
    }
    $deletion = $null
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-delete-lease' `
            -Path $item.FullName
        $deletion =
            [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedDeletion(
                $item.FullName,
                $AllowedRoot,
                $TrustedRoot)
        if ([bool]$deletion.IsDirectory -ne [bool]$item.PSIsContainer) {
            throw (
                "Staging entry type changed before retained-handle deletion: " +
                $item.FullName)
        }
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'delete' `
            -Path $item.FullName
        $deletion.Delete()
    }
    finally {
        if ($null -ne $deletion) {
            $deletion.Dispose()
        }
    }
}

function Remove-DesktopPetSafeDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot =
        Get-DesktopPetCanonicalPath -Path $AllowedRoot
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedAllowedRoot `
            -Root $resolvedTrustedRoot `
            -AllowRoot)) {
        throw (
            "Allowed staging root escaped trusted root '$resolvedTrustedRoot': " +
            $resolvedAllowedRoot)
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to delete outside allowed staging root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot)
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot -PathType Container)) {
        throw "Allowed staging root is not a directory: $resolvedAllowedRoot"
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "Staging deletion target is not a directory: $resolvedPath"
    }

    $allowedFinal = Get-DesktopPetFinalPath -Path $resolvedAllowedRoot
    Assert-DesktopPetDirectoryTreeSafe `
        -Path $resolvedPath `
        -AllowedFinalRoot $allowedFinal
    # Delete one checked node at a time. Never invoke Remove-Item -Recurse:
    # a concurrent reparse insertion is rejected or leaves a non-empty directory.
    Remove-DesktopPetTreeNode `
        -Path $resolvedPath `
        -AllowedRoot $resolvedAllowedRoot `
        -AllowedFinalRoot $allowedFinal `
        -TrustedRoot $resolvedTrustedRoot
    if (Test-Path -LiteralPath $resolvedPath) {
        throw "Safe staging deletion left the target behind: $resolvedPath"
    }
}

function Reset-DesktopPetStagingDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $resolvedPath = Get-DesktopPetCanonicalPath -Path $Path
    $resolvedAllowedRoot =
        Get-DesktopPetCanonicalPath -Path $AllowedRoot
    $resolvedTrustedRoot =
        Get-DesktopPetCanonicalPath -Path $TrustedRoot
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedAllowedRoot `
            -Root $resolvedTrustedRoot `
            -AllowRoot)) {
        throw (
            "Allowed staging root escaped trusted root '$resolvedTrustedRoot': " +
            $resolvedAllowedRoot)
    }
    if (-not (Test-DesktopPetPathWithin `
            -Path $resolvedPath `
            -Root $resolvedAllowedRoot)) {
        throw (
            "Refusing to reset outside allowed staging root " +
            "'$resolvedAllowedRoot': $resolvedPath")
    }

    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot)
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot)) {
        $allowedCreation = $null
        try {
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation 'before-create-allowed-root-lease' `
                -Path $resolvedAllowedRoot
            $allowedCreation = [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedDirectoryCreation(
                $resolvedAllowedRoot,
                $resolvedTrustedRoot)
            Invoke-DesktopPetStagingMutationTestHook `
                -Operation 'create-allowed-root' `
                -Path $resolvedAllowedRoot
            $allowedCreation.Create()
        }
        finally {
            if ($null -ne $allowedCreation) {
                $allowedCreation.Dispose()
            }
        }
    }
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot)
    if (-not (Test-Path -LiteralPath $resolvedAllowedRoot -PathType Container)) {
        throw "Allowed staging root is not a directory: $resolvedAllowedRoot"
    }

    Remove-DesktopPetSafeDirectory `
        -Path $resolvedPath `
        -AllowedRoot $resolvedAllowedRoot `
        -TrustedRoot $resolvedTrustedRoot

    # Revalidate the physical parent immediately before recreating the target.
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    $pathCreation = $null
    try {
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'before-create-staging-root-lease' `
            -Path $resolvedPath
        $pathCreation = [DesktopPet.Packaging.FinalPathResolver]::OpenValidatedDirectoryCreation(
            $resolvedPath,
            $resolvedTrustedRoot)
        Invoke-DesktopPetStagingMutationTestHook `
            -Operation 'create-staging-root' `
            -Path $resolvedPath
        $pathCreation.Create()
    }
    finally {
        if ($null -ne $pathCreation) {
            $pathCreation.Dispose()
        }
    }
    [void](Assert-DesktopPetPathChainSafe `
        -Path $resolvedPath `
        -TrustedRoot $resolvedTrustedRoot)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "Staging reset did not create a directory: $resolvedPath"
    }
    $allowedFinal = Get-DesktopPetFinalPath -Path $resolvedAllowedRoot
    $pathFinal = Get-DesktopPetFinalPath -Path $resolvedPath
    if (-not (Test-DesktopPetPathWithin `
            -Path $pathFinal `
            -Root $allowedFinal)) {
        throw (
            "Recreated staging directory escaped the allowed physical root " +
            "'$allowedFinal': $pathFinal")
    }
}
