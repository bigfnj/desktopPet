using System;
using System.Collections.Generic;

namespace DesktopPet.Modules
{
    // =====================================================================================
    // DesktopPet plugin ABI (v1). A module is a class implementing IModule, shipped as a DLL that
    // references ONLY this assembly. The host loads it (in an isolated AssemblyLoadContext), calls
    // Init(host) once, and Shutdown() on unload. In Init the module subscribes to host lifecycle
    // events, calls host services, and registers its tray/options contributions. Everything here is
    // handle-based (IPet, not the app's FormPet) and framework-agnostic (no WinForms/WPF/System.Drawing)
    // so the contract stays small and stable as the host evolves.
    // =====================================================================================

    /// <summary>A pixel rectangle, so the ABI carries no System.Drawing dependency.</summary>
    public readonly struct PixelRect
    {
        public readonly int X, Y, Width, Height;
        public PixelRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    }

    /// <summary>Identity + requirements a module declares to the host.</summary>
    public sealed class ModuleInfo
    {
        public string Id { get; set; }              // stable, unique, path-safe (e.g. "sound", "fortunes")
        public string Name { get; set; }            // display name
        public string Version { get; set; }         // module version (semver-ish string)
        public string MinHostVersion { get; set; }  // minimum host/ABI version this module needs
        public ModulePermissions Permissions { get; set; }
    }

    /// <summary>Coarse capability flags a module declares (surfaced to the user at install/consent time).</summary>
    [Flags]
    public enum ModulePermissions
    {
        None = 0,
        Speech = 1 << 0,        // calls Say/SayAll
        Animation = 1 << 1,     // plays animations
        ScreenContext = 1 << 2, // reads foreground window / captures the screen
        Network = 1 << 3,       // makes network requests
        Hotkey = 1 << 4,        // registers a global hotkey
        Storage = 1 << 5,       // reads/writes its own data folder
    }

    /// <summary>An on-screen pet, as seen by a module (opaque handle over the host's FormPet).</summary>
    public interface IPet
    {
        int Id { get; }
        bool IsBusy { get; }   // being dragged / mid-interaction
    }

    // ---- lifecycle event payloads ----
    public sealed class PokeInfo { public IPet Pet { get; set; } public int PokeCount { get; set; } }
    public sealed class IdleContext { public IPet Pet { get; set; } public ScreenContext Screen { get; set; } }
    public sealed class AnimationInfo
    {
        public IPet Pet { get; set; }         // may be null for engine-raised animation events (v1); populated later
        public int AnimationId { get; set; }
        public byte[] SoundData { get; set; } // selected sound variant's raw MP3 bytes, or null if the animation has no sound
        public int SoundLoop { get; set; }    // times to repeat the sound (0 = play once); clamped 0..20 by the engine
    }

    /// <summary>Lightweight, on-UI-thread screen context (foreground window + the pet's monitor).</summary>
    public sealed class ScreenContext
    {
        public string WindowTitle { get; set; }
        public string ProcessName { get; set; }
        public PixelRect MonitorBounds { get; set; }
        public string WindowUnderPet { get; set; }   // title of the window the pet is standing on (screen-zone awareness), or null
    }

    /// <summary>A tray context-menu entry contributed by a module (merged with core items by group/order).</summary>
    public sealed class TrayItem
    {
        public string Label { get; set; }                 // display text ('&' marks the mnemonic)
        public int Group { get; set; }                    // items are grouped (separators between groups)
        public int Order { get; set; }                    // order within a group
        public Func<bool> Visible { get; set; }           // re-evaluated when the host signals a state change (null => always)
        public Func<string> DynamicText { get; set; }     // overrides Label each show (e.g. Enable/Disable) (null => Label)
        public Action Click { get; set; }                 // leaf action (null for a pure submenu)
        public Func<IEnumerable<TrayItem>> BuildChildren { get; set; } // lazy submenu, rebuilt on open (null for a leaf)
    }

    // ---- options schema (host renders a consistent UI; a module ships no UI code) ----
    public enum SettingKind { Bool, Int, Text, Enum, Secret }

    public sealed class SettingField
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public SettingKind Kind { get; set; }
        public string[] Options { get; set; }   // for Enum
        public int Min { get; set; }             // for Int
        public int Max { get; set; }             // for Int
        // Optional grouping: fields (and PaneActions) sharing a Group name render in one titled card, and
        // cards flow into responsive columns. null/"" => an untitled default card. Additive since the
        // grouped-settings layout; older modules that don't set it get one default card.
        public string Group { get; set; }
    }

    /// <summary>An action button on an options pane (e.g. "Test connection", "Clear history"). The host
    /// renders a button; on click it disables it, shows a working hint, awaits <see cref="InvokeAsync"/>
    /// (so a slow network probe never freezes the UI), then shows the returned status string. Runs on the
    /// UI thread; the delegate should offload any blocking work itself (await I/O).</summary>
    public sealed class PaneAction
    {
        public string Label { get; set; }
        public Func<System.Threading.Tasks.Task<string>> InvokeAsync { get; set; }
        // Optional: render this action inside the titled card of the matching field Group (e.g. a
        // "Test connection" button in the "Provider" card). null/"" => the untitled default card.
        public string Group { get; set; }
    }

    /// <summary>A module's settings pane. Declarative schema (host-rendered) is the default; secrets are
    /// write-only and never read back into the UI.</summary>
    public sealed class OptionsPane
    {
        public string Title { get; set; }
        public IReadOnlyList<SettingField> Schema { get; set; }

        // Optional action buttons (S5b), rendered below the fields — the schema is data-only, so anything
        // that runs module behavior (test a connection, clear history, ...) is expressed here.
        public IReadOnlyList<PaneAction> Actions { get; set; }

        // Persistence (S5b): the module owns its own store (which may be richer than the host's
        // IModuleSettings — e.g. DPAPI-scoped keys), so it supplies these. Load returns the current value of
        // each schema field by Id (Secret fields: return "" or omit — never the plaintext; a non-empty value
        // just signals "a secret is set" for a placeholder hint). On Apply the host calls Save with the
        // edited values by Id; a Secret key is present ONLY when the user typed a new value (blank = keep the
        // stored one). Save returns false if persistence failed. Null Load/Save => a display-only pane.
        public Func<IReadOnlyDictionary<string, string>> Load { get; set; }
        public Func<IReadOnlyDictionary<string, string>, bool> Save { get; set; }
    }

    /// <summary>Per-module writable data folder (host-provisioned, path-isolated).</summary>
    public interface IModuleStorage
    {
        string DataDirectory { get; }
    }

    /// <summary>Per-module persisted settings (string values; the host owns the atomic/locked writer and
    /// encrypts values whose field Kind is Secret).</summary>
    public interface IModuleSettings
    {
        string Get(string key, string fallback);
        int GetInt(string key, int fallback);
        bool GetBool(string key, bool fallback);
        void Set(string key, string value);
        bool Save();
    }

    /// <summary>The host surface a module talks to. Events fire on the UI thread; services must be called
    /// on the UI thread unless noted.</summary>
    public interface IHost
    {
        string HostVersion { get; }
        bool SpeechEnabled { get; }
        double Volume { get; }          // 0..1

        // ---- lifecycle events (subscribe in Init) ----
        event Action<IPet> PetSpawned;
        event Action<PokeInfo> PetPoked;
        event Action<IPet> PetLanded;
        event Action<IdleContext> PetIdle;
        event Action<AnimationInfo> AnimationStarted;
        event Action HostShutdown;

        // ---- host services ----
        void Say(IPet pet, string text);
        void SayAll(string text);
        bool TryPlayAnimation(IPet pet, string animationName);
        // Play an emotion on every live pet: for each pet, the first candidate its XML actually
        // defines wins (the caller owns the emotion->animation-name mapping). Parallels SayAll.
        void PlayAnimationAll(IReadOnlyList<string> animationCandidates);
        ScreenContext CaptureScreenContext(IPet pet);
        IDisposable RegisterHotkey(string combo, Action onPressed);
        IModuleStorage GetStorage(string moduleId);
        IModuleSettings GetSettings(string moduleId);

        // Periodic "say something?" tick, arbitrated across modules by priority (higher first) until one
        // handler returns true (handled). Lets the AI module outrank Fortunes for the shared drop loop.
        IDisposable RegisterDropResponder(int priority, Func<bool> onDrop);

        // ---- contributions (register in Init) ----
        void AddTrayItems(IEnumerable<TrayItem> items);
        void AddOptionsPane(OptionsPane pane);
    }

    /// <summary>A DesktopPet plugin. Implemented by exactly one public class per module DLL.</summary>
    public interface IModule
    {
        ModuleInfo Info { get; }
        void Init(IHost host);
        void Shutdown();
    }
}
