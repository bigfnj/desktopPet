using System;
using DesktopPet.Modules;

namespace DesktopPet.AiBrainModule
{
    /// <summary>
    /// The AI-brain module (S4): the optional, off-by-default screen-commentary LLM. When live it will own
    /// the "ask about my screen" flow (hotkey + tray + the arbitrated drop, outranking Fortunes), the idle
    /// commentary loop, and the emotion->animation reaction — all through host services (SayAll,
    /// PlayAnimationAll, CaptureScreenContext, RegisterHotkey, GetStorage/GetSettings, RegisterDropResponder).
    ///
    /// S4a-2 is the DORMANT scaffold: Init subscribes to nothing and starts nothing, so the base keeps
    /// owning the AI brain at runtime (no double-ask, no hotkey collision) until the flip in S4b. The engine
    /// relocates here (dormant) in S4a-3. This mirrors how the Fortunes engine landed dormant before its flip.
    /// </summary>
    public sealed class AiBrainModule : IModule
    {
        private IHost _host;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "aibrain",
            Name = "AI Brain",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            // The full capability set this module will use once live (surfaced at consent time in S7). It is
            // OFF by default, so nothing here is exercised until the user enables it.
            Permissions = ModulePermissions.Speech | ModulePermissions.Animation |
                          ModulePermissions.ScreenContext | ModulePermissions.Network |
                          ModulePermissions.Hotkey | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            // Dormant (S4a): hold the host reference but wire nothing. The base still owns every AI trigger
            // until the flip, so an active module here would double-fire. Behavior arrives in S4b.
            _host = host;
        }

        public void Shutdown()
        {
            _host = null;
        }
    }
}
