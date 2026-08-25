using System;
using System.Collections.Generic;
using System.Text;
using DesktopPet.Modules;

namespace DesktopPet.ShimejiImporterModule
{
    /// <summary>
    /// Shimeji Catalog: a curated, browsable list of ready-to-install shimeji pets. This is the STORE half of
    /// the effort (import-your-own lives in Pet Studio). It ships pre-converted pets we have permission to
    /// redistribute, plus attribution, and installs the one you pick through IPetManager.InstallType. It
    /// carries no converter -- browse, Get, done.
    /// </summary>
    public sealed class ShimejiImporterModule : IModule
    {
        private IHost _host;
        private ShimejiCatalogWindow _window;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "shimejiimporter",
            Name = "Shimeji Catalog",
            Version = "0.1.0",   // 0.1.0: MVP browse + Get over a bundled shimeji.org subset (curated, permitted)
            // Needs IPetManager.InstallType (1.4.6) and IHost.IsDarkTheme (1.4.7) for the window theme.
            MinHostVersion = "1.4.7",
            Permissions = ModulePermissions.Pets,
        };

        public void Init(IHost host)
        {
            _host = host;

            host.AddOptionsPane(new OptionsPane
            {
                Title = "Shimeji Catalog",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "about",
                        Label = "What this is",
                        Kind = SettingKind.Info,
                        Group = "Shimeji Catalog",
                    },
                },
                Actions = new[]
                {
                    new PaneAction { Label = "Browse Shimeji Catalog…", InvokeAsync = OpenAsync, Group = "Shimeji Catalog" },
                },
                Load = delegate
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        {
                            "about",
                            "Browse a curated list of ready-to-install shimeji pets and install the ones you " +
                            "like. Each credits its creator and links to its source. To bring in a skin of your " +
                            "own instead, use Pet Studio's Import."
                        },
                    };
                },
            });
        }

        private System.Threading.Tasks.Task<string> OpenAsync()
        {
            Open();
            return System.Threading.Tasks.Task.FromResult("Shimeji Catalog is open.");
        }

        private void Open()
        {
            try
            {
                if (_window != null && _window.IsLoaded) { _window.Activate(); return; }
                _window = new ShimejiCatalogWindow(_host);
                _window.Closed += delegate { _window = null; };
                _window.Show();
            }
            catch (Exception ex)
            {
                if (_host != null) _host.SayAll("Shimeji Catalog could not open: " + ex.Message);
            }
        }

        public void Shutdown()
        {
            ShimejiCatalogWindow window = _window;
            _window = null;
            if (window == null) return;
            try { window.Close(); } catch { }
        }

        /// <summary>
        /// Convention self-test (run by --module-selftest=shimejiimporter through the real loader). Host-free:
        /// loads the embedded catalog manifest and asserts it lists entries whose pre-converted pet XML is
        /// present and looks like an animations.xml. Install itself needs an IPetManager, which the host
        /// re-validates at InstallType time.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            try
            {
                List<CatalogEntry> entries = ShimejiCatalog.LoadEntries();
                if (entries.Count == 0) { detail = "shimeji catalog self-test: manifest lists no entries"; return false; }

                int checkedCount = 0;
                foreach (CatalogEntry e in entries)
                {
                    if (string.IsNullOrEmpty(e.Id)) { detail = "shimeji catalog self-test: an entry has no id"; return false; }
                    string xml = ShimejiCatalog.ReadPetXml(e);
                    if (string.IsNullOrEmpty(xml))
                    { detail = "shimeji catalog self-test: pet xml missing for '" + e.Id + "'"; return false; }
                    if (xml.IndexOf("<animations", StringComparison.OrdinalIgnoreCase) < 0 &&
                        xml.IndexOf("<image", StringComparison.OrdinalIgnoreCase) < 0)
                    { detail = "shimeji catalog self-test: '" + e.Id + "' pet xml is not an animations.xml"; return false; }
                    checkedCount++;
                }

                var sb = new StringBuilder();
                sb.Append("shimeji catalog self-test: " + entries.Count + " curated entries, each with an installable pet xml (");
                sb.Append(checkedCount + " verified)");
                detail = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                detail = "shimeji catalog self-test: threw -- " + ex.Message;
                return false;
            }
        }
    }
}
