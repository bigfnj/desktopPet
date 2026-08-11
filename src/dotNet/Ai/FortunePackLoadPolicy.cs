namespace DesktopPet.Ai
{
    /// <summary>
    /// Resource bounds shared by trusted-catalog admission and pack downloads. These limits describe
    /// what the runtime can accept from the fortunes directory.
    ///
    /// This is the only surviving piece of the former base fortune engine: the engine itself
    /// (FortuneProvider / FortuneFileImporter and their self-tests) moved to the Fortunes module,
    /// but <see cref="DesktopPet.RemoteCatalog"/> still enforces these same per-file/entry bounds
    /// when validating and downloading catalog packs, so the policy stays in the base.
    /// </summary>
    internal static class FortunePackLoadPolicy
    {
        public const int MaximumFiles = 128;
        public const int MaximumFileBytes = 4 * 1024 * 1024;
        public const int MaximumTotalBytes = 16 * 1024 * 1024;
        public const int MaximumEntries = 100000;

        public static bool TryValidatePackMetadata(
            int bytes,
            int entries,
            out string error)
        {
            if (bytes < 1 || bytes > MaximumFileBytes)
            {
                error = "pack byte count is outside the runtime per-file limit";
                return false;
            }
            if (entries < 1 || entries > MaximumEntries)
            {
                error = "pack row count is outside the runtime entry limit";
                return false;
            }
            error = null;
            return true;
        }
    }
}
