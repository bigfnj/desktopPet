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
    // IdleContext and AnimationInfo were removed before the host froze. Their events (PetIdle,
    // AnimationStarted) were declared and bridged but never raised by the host, and shipping a
    // declared-but-silent event in a final contract is a trap: a module author subscribes, sees nothing, and
    // there is no host release left to fix it in. Raising them honestly was the alternative and cost more
    // than it was worth -- the host has no idle policy at all (the real idle predicate, a screen-change
    // delta, lives in the AI-brain module and would have had to ignore a generic host tick), and
    // AnimationInfo.AnimationId is an index into one pet's own XML with no name field and no enumeration
    // verb, so making it usable meant ADDING ABI. Sound reaches the host's own AudioOutput directly, which is
    // what left SoundData/SoundLoop unreachable when the Sound module was retired.

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
        public byte[] IconPng { get; set; }               // optional PNG-encoded icon bytes (null => no icon); kept
                                                            // as raw bytes, not a concrete image type, so the
                                                            // contract carries no System.Drawing dependency
    }

    // ---- options schema (host renders a consistent UI; a module ships no UI code) ----
    // Info is display-only: the host renders the value as text with no editor and never collects it back,
    // so a module can explain state the user can't otherwise see (e.g. "no fortunes match your filters, so
    // the pet will stay silent") instead of failing quietly. A value starting with ✓/✗ is coloured like an
    // action result, matching what the buttons already do.
    public enum SettingKind { Bool, Int, Text, Enum, Secret, Info }

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
        // Optional: after this action runs, ask the host to rebuild the pane so refreshed Load() values
        // show (e.g. a "reset to defaults" that rewrites several settings). Default false. The delegate may
        // also set this from inside InvokeAsync — the host reads it after awaiting — so an action can decline
        // the reload (e.g. when the user cancels a confirmation).
        public bool ReloadPaneAfter { get; set; }
    }

    /// <summary>One checkable row in a <see cref="ListCard"/>: a stable <see cref="Id"/> (passed back to the
    /// toggle callback), a display <see cref="Label"/>, an optional <see cref="Detail"/> (secondary text, e.g.
    /// a line count), and its current <see cref="Checked"/> state.</summary>
    public sealed class ListItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Detail { get; set; }
        public bool Checked { get; set; }
        // Optional grouping label. When any item in a card sets it, the host renders one collapsible
        // section per distinct group (items without one fall under "Other") instead of a flat list —
        // the difference between a browsable 150-pack card and an unreadable wall of checkboxes.
        public string Group { get; set; }
    }

    /// <summary>A dynamic, checkable list rendered as one titled card alongside the schema fields — for
    /// content a flat <see cref="SettingField"/> can't express (installed fortune packs, genres, ...). The
    /// module supplies the current items (<see cref="LoadItems"/>, re-read on each pane build) and a toggle
    /// callback (<see cref="SetChecked"/>, invoked per click with the item Id + new state — or once per
    /// changed item at Apply, see <see cref="DeferChanges"/>); optional card-level buttons reuse
    /// <see cref="PaneAction"/> (set <see cref="PaneAction.ReloadPaneAfter"/> on a mutating button — e.g.
    /// rescan/import — to refresh the card). Data + delegates only, so the ABI stays framework-agnostic:
    /// the host owns the WPF.</summary>
    public sealed class ListCard
    {
        public string Title { get; set; }
        public Func<IReadOnlyList<ListItem>> LoadItems { get; set; }
        public Action<string, bool> SetChecked { get; set; }
        public IReadOnlyList<PaneAction> Actions { get; set; }
        public string EmptyHint { get; set; }   // shown when LoadItems returns nothing
        // Ask the host for a filter box above the list (live substring match over label/detail/group).
        // Worth setting for any card that can hold more than a screenful.
        public bool Filterable { get; set; }
        // Start collapsed when the card is grouped, so a long list opens as a short list of section
        // headers the user expands, rather than every row at once. Ignored when nothing sets a Group.
        public bool CollapseGroups { get; set; }
        // Set when SetChecked is expensive (it re-reads content, rebuilds an index, ...). The host then
        // treats a click as an edit rather than a command: the box moves at once, the pane goes dirty so
        // Apply lights up, and SetChecked runs once per CHANGED item when the user applies, immediately
        // before OptionsPane.Save — so the module can stage the ids and commit them all in Save. Unapplied
        // ticks are discarded on close or on a ReloadPaneAfter action, exactly like unapplied field edits.
        // Leave false for a card whose ticks feed a button rather than the saved settings (a download
        // basket), where deferring would mean the button sees an empty selection.
        public bool DeferChanges { get; set; }
    }

    /// <summary>
    /// One item a module can download from the app's content catalog (a fortune pack today). Metadata
    /// only: the host owns the URL, its HTTPS/repository validation, and the SHA-256 verification, so a
    /// module never builds a download URL or decides for itself whether bytes are trustworthy.
    /// </summary>
    public sealed class CatalogItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }         // browsing group ("" when ungrouped)
        public string Description { get; set; }
        public int Bytes { get; set; }
        public int Count { get; set; }            // entries inside the item (lines in a pack); 0 when n/a
    }

    /// <summary>Catalog sections a module can browse (see <see cref="IHost.FetchCatalogItemsAsync"/>).</summary>
    public static class CatalogKinds
    {
        public const string Pack = "pack";        // fortune packs
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

        // Optional dynamic list cards (S5b): checkable lists a flat schema can't express (fortune packs,
        // genres). Each renders as a titled card alongside the schema groups.
        public IReadOnlyList<ListCard> Lists { get; set; }

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
        // The display name the pet should address the user by, shared across modules (e.g. the AI brain
        // publishes it via SetOwnerName; the fortunes welcome reads it). "" when no module has set one, so
        // a consumer falls back to its own default (e.g. the Windows user name).
        string OwnerName { get; }

        // ---- lifecycle events (subscribe in Init) ----
        // Every event here is RAISED by the host. If you are adding one, wire the raise in the same change:
        // PetIdle and AnimationStarted were removed at the freeze precisely because they were not.
        event Action<IPet> PetSpawned;
        event Action<PokeInfo> PetPoked;
        event Action<IPet> PetLanded;
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

        // Publish the preferred user display name (see OwnerName). A module that owns the user's name (the AI
        // brain) sets it when enabled and clears it ("") when off, so other modules address the user the same.
        void SetOwnerName(string name);

        // Periodic "say something?" tick, arbitrated across modules by priority (higher first) until one
        // handler returns true (handled). Lets the AI module outrank Fortunes for the shared drop loop.
        IDisposable RegisterDropResponder(int priority, Func<bool> onDrop);

        // The FIRST poke of a fresh right-click session ("say something about this?"), arbitrated the same
        // way as the drop: highest priority first until one handler returns true (handled). Separate from
        // the PetPoked event, which stays a plain broadcast every module sees: this chain is the one that
        // gets to SPEAK, so exactly one module wins it. The base's own poke escalation (ignore -> sass ->
        // escape) is unaffected and still runs off the raw poke count. A module registered here is also
        // what the user's "Trigger Speech" preference selects between (registration id = module id).
        IDisposable RegisterPokeResponder(string moduleId, int priority, Func<bool> onPoke);

        // Browse and download a module's downloadable content from the app's HTTPS-fetched, SHA-256-pinned
        // catalog (kind = one of CatalogKinds). The HOST performs the catalog fetch, the per-asset URL
        // validation, the download, and the hash verification; the module decides only what to keep and
        // where to write it inside its own storage. Both throw on a network/verification failure, so a
        // caller reports the message rather than trusting partial content.
        System.Threading.Tasks.Task<IReadOnlyList<CatalogItem>> FetchCatalogItemsAsync(string kind);
        System.Threading.Tasks.Task<byte[]> DownloadCatalogItemAsync(string kind, string id);

        // Ask the user to pick existing files. The HOST owns the dialog, so a module needs no UI framework
        // of its own (modules stay data + delegates). Returns the chosen full paths, or an empty list when
        // the user cancels. Extensions are bare, dot-less ("txt"); call from a PaneAction (UI thread).
        IReadOnlyList<string> PickFilesToOpen(string title, string fileKindLabel, IReadOnlyList<string> extensions);

        // Open an HTTPS link in the user's browser (e.g. "where do I download this optional dependency?").
        // Requires the calling module to declare ModulePermissions.Network — launching a browser is weaker
        // than the raw network access that flag already grants, but it IS user-visible, so it stays behind
        // a declared capability rather than being free to any module. The host validates the URL (HTTPS,
        // real host, no userinfo, length-bounded) and swallows failures. Returns false when refused.
        bool OpenLink(string moduleId, string httpsUrl);

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
