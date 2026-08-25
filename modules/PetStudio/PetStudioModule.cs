using System;
using System.Collections.Generic;
using DesktopPet.ModuleKit;
using DesktopPet.Modules;

namespace DesktopPet.PetStudioModule
{
    /// <summary>
    /// Pet Studio: check a pet's animations.xml, see what will never play, watch it run on the real desktop,
    /// and install it. The replacement for the retired Tools\PetTester, as a module rather than a separate
    /// app, so it is built by the same pipeline, gated by the same CI, delivered by the same catalog, and
    /// installed only by people who actually author pets.
    ///
    /// It validates with the HOST's parser (source-linked, not copied), so its verdict cannot disagree with
    /// what the host will run, and it previews through IPetManager.SpawnPreview, so the author sees the pet
    /// on their actual desktop without it being installed, saved, or added to their pet mix.
    /// </summary>
    public sealed class PetStudioModule : IModule
    {
        private IHost _host;
        private PetStudioWindow _window;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "petstudio",
            Name = "Pet Studio",
            Version = "1.3.0",   // 1.3.0: import Android JSON+WebP bundles too (bundled dwebp decoder), not just desktop skins
            // 1.2.1: .zip import + converter gains (Japanese vocab, nested-sprite detection)
            // 1.2.0: Import Shimeji skin -> convert -> editor + loss report (workshop half)
                                 // 1.1.1: the window's theme comes from IHost.IsDarkTheme, not the OS registry
                                 // 1.1.0: authoring window (editable XML, reachability map, sprite playback)
            // 1.4.7 is the host that added IHost.IsDarkTheme, which the studio's window reads so it matches the
            // app even when the user has PINNED light or dark rather than following the OS. (1.4.6 added
            // IPetManager.PetsDirectory, which the file dialog still uses.) Declaring it means an older host
            // refuses this module with a legible reason instead of loading it and failing at a missing member.
            MinHostVersion = "1.4.7",
            Permissions = ModulePermissions.Pets | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            host.AddTrayItems(new List<TrayItem>
            {
                new TrayItem
                {
                    Label = "Pet Studio…", Group = 40, Order = 0, Click = Open,
                    IconPng = EmbeddedResources.LoadBytes(typeof(PetStudioModule).Assembly, "petstudio.png"),
                },
            });

            host.AddOptionsPane(new OptionsPane
            {
                Title = "Pet Studio",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "about",
                        Label = "What this is",
                        Kind = SettingKind.Info,
                        Group = "Pet Studio",
                    },
                },
                Actions = new[]
                {
                    new PaneAction { Label = "Open Pet Studio…", InvokeAsync = OpenAsync, Group = "Pet Studio" },
                },
                Load = delegate
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        {
                            "about",
                            "Check a pet's animations.xml before you use it: what the host would reject, " +
                            "which animations can never play, and how it actually looks running on your " +
                            "desktop. A preview pet is temporary — it is never saved and never joins your pets."
                        },
                    };
                },
            });
        }

        private System.Threading.Tasks.Task<string> OpenAsync()
        {
            Open();
            return System.Threading.Tasks.Task.FromResult("Pet Studio is open.");
        }

        /// <summary>Show the studio, or bring the existing one forward. One window: a second would let two
        /// previews fight over the same pet slots.</summary>
        private void Open()
        {
            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return;
                }
                _window = new PetStudioWindow(_host);
                _window.Closed += delegate { _window = null; };
                _window.Show();
            }
            catch (Exception ex)
            {
                if (_host != null) _host.SayAll("Pet Studio could not open: " + ex.Message);
            }
        }

        /// <summary>Open the studio (or bring it forward) and immediately start the Shimeji import flow. Public
        /// so the host's Pets pane can deep-link straight here, invoked by reflection over the loaded module
        /// instance (the host cannot cast across the module's load context, and IModule stays frozen).</summary>
        public void OpenForImport()
        {
            Open();
            try { if (_window != null) _window.BeginImport(); }
            catch (Exception ex) { if (_host != null) _host.SayAll("Pet Studio import could not start: " + ex.Message); }
        }

        public void Shutdown()
        {
            PetStudioWindow window = _window;
            _window = null;
            if (window == null) return;
            // Closing removes any live preview: the window owns that handle.
            try { window.Close(); } catch { }
        }
    }
}
