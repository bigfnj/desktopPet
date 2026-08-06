using DesktopPet.Modules;

namespace DesktopPet.FortunesModule
{
    /// <summary>
    /// The Fortunes module (S3) — boundary scaffold. When the engine relocates here it will subscribe to
    /// <see cref="IHost.PetLanded"/>, <see cref="IHost.PetPoked"/>, and <see cref="IHost.RegisterDropResponder"/>,
    /// pick a fortune (dumb random or smart ONNX-semantic) from the user's installed packs, and speak it via
    /// <see cref="IHost.SayAll"/> — using <see cref="IHost.CaptureScreenContext"/> for smart routing, and
    /// <see cref="IHost.GetStorage"/>/<see cref="IHost.GetSettings"/> for its packs, vector cache, and config.
    /// It ships NO fortune content: a fresh install is silent until the user adds a pack (see BACKLOG.md).
    /// For now this is a no-op so the base keeps owning fortunes until the relocation lands (no regression).
    /// </summary>
    public sealed class FortunesModule : IModule
    {
        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "0.1.0",   // engine relocation raises this to 1.0.0
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host) { /* engine wiring lands in the S3 relocation commits */ }

        public void Shutdown() { }
    }
}
