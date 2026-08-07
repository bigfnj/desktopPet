using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopPet
{
    /// <summary>
    /// Same-directory durable replace helper, copied into the Fortunes module (S3) from the base's
    /// AppSettingsStore so the relocated engine has no dependency on the base assembly. Only
    /// <see cref="ReplaceExisting(string,string,string,CancellationToken)"/> is carried (the engine's
    /// sole use, in FortuneFileImporter); the base's <c>TryWriteAllText</c>, which couples to AppPaths, is
    /// intentionally omitted. Kept in namespace <c>DesktopPet</c> so the relocated <c>DesktopPet.Ai</c>
    /// engine resolves it exactly as it did in the base (nested-namespace visibility).
    /// </summary>
    internal static class AtomicFile
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken)
        {
            ReplaceExisting(
                temporaryPath,
                destinationPath,
                backupPath,
                cancellationToken,
                null);
        }

        internal static void ReplaceExisting(
            string temporaryPath,
            string destinationPath,
            string backupPath,
            CancellationToken cancellationToken,
            Action<string, string, string, bool> replaceFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (replaceFile == null)
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                else
                    replaceFile(temporaryPath, destinationPath, backupPath, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Portable copies can live on filesystems where File.Replace is unavailable.
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(backupPath))
                File.Copy(destinationPath, backupPath, true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!MoveFileEx(
                    temporaryPath,
                    destinationPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
