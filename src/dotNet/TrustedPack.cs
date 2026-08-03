namespace DesktopPet
{
    /// <summary>
    /// Lightweight pack model used by the fortune-pack install path. Downloads now come from the
    /// runtime catalog (<see cref="RemoteCatalog"/>); this is populated from a <c>CatalogPack</c>
    /// before staging so the shared importer can name and admit the file.
    /// </summary>
    internal sealed class TrustedPack
    {
        public string Id;
        public string Name;
        public string Description;
        public string License;
        public string Sha256;
        public int Count;
        public int Bytes;
        public int DataSchema;
        public bool RedistributionApproved;
    }
}
