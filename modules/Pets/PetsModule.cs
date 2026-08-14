using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopPet.Modules;

namespace DesktopPet.PetsModule
{
    /// <summary>
    /// The Pets module (S6 phase 2). Contributes the "Pets" options pane — a roster of installed pet types
    /// (Use / Add / Remove / size / sound per pet, via the new per-row <see cref="RowAction"/>s) plus an
    /// "Available online" download list — driving everything through <see cref="IPetManager"/>
    /// (<c>host.GetPetManager()</c>). The host keeps owning the pet engine, the persisted mix / size / sound /
    /// active-id, and the MAX_SHEEPS cap; this module owns only the UI. Ships no pet content: the built-in
    /// default and any bundled pets come from the host, downloads come from the catalog.
    /// </summary>
    public sealed class PetsModule : IModule
    {
        private IHost _host;

        // Last online browse (catalog pets not installed yet) + the subset the user ticked for download.
        // In-memory only: browsing/ticking writes nothing; "Download selected" is the only thing that fetches.
        private readonly List<CatalogItem> _availablePets = new List<CatalogItem>();
        private readonly HashSet<string> _selectedPets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "pets",
            Name = "Pets",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            // Downloads pet skins from the catalog (Network) and writes them into the pet library (Storage).
            Permissions = ModulePermissions.Network | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            host.AddOptionsPane(BuildPane());
            host.AddTrayItems(BuildTrayItems());
        }

        public void Shutdown() { _host = null; }

        // ---- tray: Add a pet ▸ / Remove a pet ▸ (lazy submenus rebuilt on open) ---------------------

        private IEnumerable<TrayItem> BuildTrayItems()
        {
            return new[]
            {
                new TrayItem { Label = "Add a pet", Group = 5, Order = 0, BuildChildren = BuildAddPetChildren },
                new TrayItem { Label = "Remove a pet", Group = 5, Order = 1, BuildChildren = BuildRemovePetChildren },
            };
        }

        private IEnumerable<TrayItem> BuildAddPetChildren()
        {
            var items = new List<TrayItem>();
            IPetManager pm = Manager();
            if (pm == null) return items;
            bool full = pm.IsAtMax;
            foreach (PetTypeInfo t in pm.InstalledTypes())
            {
                if (t == null || string.IsNullOrEmpty(t.TypeId)) continue;
                string id = t.TypeId;
                IPetManager mgr = pm;
                items.Add(new TrayItem
                {
                    Label = t.DisplayName ?? id,
                    Visible = () => !mgr.IsAtMax,
                    Click = () => mgr.SpawnOne(id),
                });
            }
            if (full) items.Add(new TrayItem { Label = "(maximum pets reached)" });
            return items;
        }

        private IEnumerable<TrayItem> BuildRemovePetChildren()
        {
            var items = new List<TrayItem>();
            IPetManager pm = Manager();
            if (pm == null) return items;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PetTypeInfo t in pm.InstalledTypes())
                if (t != null && !string.IsNullOrEmpty(t.TypeId)) names[t.TypeId] = t.DisplayName ?? t.TypeId;
            foreach (PetCount c in pm.OnScreenMix())
            {
                if (c == null || string.IsNullOrEmpty(c.TypeId)) continue;
                string id = c.TypeId;
                string name;
                if (!names.TryGetValue(id, out name)) name = id;
                IPetManager mgr = pm;
                items.Add(new TrayItem { Label = name + " ×" + c.Count, Click = () => mgr.RemoveOne(id) });
            }
            if (items.Count == 0) items.Add(new TrayItem { Label = "(no pets on screen)" });
            return items;
        }

        private IPetManager Manager()
        {
            IHost host = _host;
            return host != null ? host.GetPetManager() : null;
        }

        // ---- the Pets pane -------------------------------------------------------------------------

        private OptionsPane BuildPane()
        {
            return new OptionsPane
            {
                Title = "Pets",
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Your pets",
                        HideCheckbox = true,          // button-driven rows, not enable/disable ticks
                        Filterable = true,
                        LoadItems = LoadPetRows,
                        EmptyHint = "No pets found.",
                    },
                    new ListCard
                    {
                        Title = "Available online",
                        LoadItems = LoadAvailablePetItems,
                        SetChecked = SetPetSelected,
                        Filterable = true,
                        EmptyHint = "Click “Check online for pets” to see what the catalog offers.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Check online for pets", InvokeAsync = CheckPetsOnlineAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Download selected", InvokeAsync = DownloadPetsAsync, ReloadPaneAfter = true },
                        },
                    },
                },
            };
        }

        // ---- "Your pets": one row per installed type, with per-row action buttons -------------------

        private IReadOnlyList<ListItem> LoadPetRows()
        {
            var items = new List<ListItem>();
            IPetManager pm = Manager();
            if (pm == null) return items;

            var mix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (PetCount c in pm.OnScreenMix())
                if (c != null) mix[c.TypeId ?? ""] = c.Count;

            foreach (PetTypeInfo t in pm.InstalledTypes())
                if (t != null && !string.IsNullOrEmpty(t.TypeId))
                    items.Add(BuildPetRow(pm, t, mix));
            return items;
        }

        private ListItem BuildPetRow(IPetManager pm, PetTypeInfo t, IDictionary<string, int> mix)
        {
            int count;
            if (!mix.TryGetValue(t.TypeId, out count)) count = 0;
            string name = t.DisplayName ?? t.TypeId;

            var actions = new List<RowAction>
            {
                new RowAction
                {
                    Label = "Use",
                    ReloadCardAfter = true,
                    InvokeAsync = () => Task.FromResult(pm.SetActiveType(t.TypeId)
                        ? "✓ Now using " + name + "."
                        : "✗ Couldn't switch to that pet."),
                },
                new RowAction
                {
                    Label = "＋ Add",
                    ReloadCardAfter = true,
                    InvokeAsync = () => Task.FromResult(
                        pm.IsAtMax ? "✗ Maximum pets already on screen."
                        : (pm.SpawnOne(t.TypeId) ? "✓ Added " + name + "." : "✗ Couldn't add that pet.")),
                },
            };
            if (count > 0)
                actions.Add(new RowAction
                {
                    Label = "－ Remove",
                    ReloadCardAfter = true,
                    InvokeAsync = () => Task.FromResult(pm.RemoveOne(t.TypeId)
                        ? "✓ Removed one " + name + "."
                        : "✗ Couldn't remove that pet."),
                });

            int size = pm.GetSizeLevel(t.TypeId);
            actions.Add(new RowAction
            {
                Label = SizeLabel(size),
                ReloadCardAfter = true,
                InvokeAsync = () =>
                {
                    int next = (size + 1) % 4;   // auto -> small -> medium -> large -> auto
                    return Task.FromResult(pm.SetSizeLevel(t.TypeId, next)
                        ? "✓ Size set to " + SizeWord(next) + " (applies to new " + name + ")."
                        : "✗ Couldn't set the size.");
                },
            });

            bool sound = pm.GetSoundEnabled(t.TypeId);
            actions.Add(new RowAction
            {
                Label = sound ? "♪ On" : "♪ Off",
                ReloadCardAfter = true,
                InvokeAsync = () => Task.FromResult(pm.SetSoundEnabled(t.TypeId, !sound)
                    ? "✓ Sound " + (!sound ? "on" : "off") + " for " + name + "."
                    : "✗ Couldn't toggle sound."),
            });

            return new ListItem
            {
                Id = t.TypeId,
                Label = name + (t.IsBuiltIn ? " (default)" : ""),
                Detail = count > 0 ? ("×" + count + " on screen") : "not on screen",
                RowActions = actions,
            };
        }

        private static string SizeLabel(int level) { return level <= 0 ? "Size: auto" : ("Size: " + SizeWord(level)); }
        private static string SizeWord(int level)
        {
            switch (level)
            {
                case 1: return "small";
                case 2: return "medium";
                case 3: return "large";
                default: return "auto";
            }
        }

        // ---- "Available online": browse + download catalog pets through the host --------------------

        private IReadOnlyList<ListItem> LoadAvailablePetItems()
        {
            var items = new List<ListItem>();
            foreach (CatalogItem p in _availablePets)
                if (p != null && !string.IsNullOrEmpty(p.Id))
                    items.Add(new ListItem
                    {
                        Id = p.Id,
                        Label = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
                        Detail = ApproximateSize(p.Bytes),
                        Checked = _selectedPets.Contains(p.Id),
                    });
            return items;
        }

        private void SetPetSelected(string id, bool selected)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (selected) _selectedPets.Add(id); else _selectedPets.Remove(id);
        }

        /// <summary>Fetch the catalog and list the pets not installed yet (read-only: nothing downloaded).</summary>
        private async Task<string> CheckPetsOnlineAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            try
            {
                IReadOnlyList<CatalogItem> items = await host.FetchCatalogItemsAsync(CatalogKinds.Pet).ConfigureAwait(false);
                int available = CacheMissingPets(items);
                if (items.Count == 0) return "The catalog lists no pets.";
                return available == 0
                    ? ("You already have every catalog pet (" + items.Count + ").")
                    : (available + (available == 1 ? " pet" : " pets") +
                       " available — tick the ones you want, then “Download selected”.");
            }
            catch (Exception ex) { return "✗ Couldn't reach the catalog: " + Short(ex.Message); }
        }

        /// <summary>Download the ticked pets and install each verified animations.xml into the library.</summary>
        private async Task<string> DownloadPetsAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            IPetManager pm = host.GetPetManager();
            if (pm == null) return "No pet manager.";
            if (_availablePets.Count == 0) return "Nothing listed — click “Check online for pets” first.";
            if (_selectedPets.Count == 0) return "No pets ticked — choose some, then Download selected.";

            var pending = new List<CatalogItem>();
            foreach (CatalogItem it in _availablePets)
                if (it != null && _selectedPets.Contains(it.Id)) pending.Add(it);

            int installed = 0, failed = 0;
            string lastError = "";
            foreach (CatalogItem it in pending)
            {
                try
                {
                    byte[] bytes = await host.DownloadCatalogItemAsync(CatalogKinds.Pet, it.Id).ConfigureAwait(false);
                    string err = null;
                    if (bytes != null && bytes.Length > 0 && pm.InstallType(it.Id, bytes, out err))
                    {
                        _selectedPets.Remove(it.Id);
                        installed++;
                    }
                    else
                    {
                        failed++;
                        if (!string.IsNullOrEmpty(err)) lastError = Short(err);
                    }
                }
                catch (Exception ex) { failed++; lastError = Short(ex.Message); }
            }

            // Drop the now-installed ones so the card shows what is still missing.
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PetTypeInfo t in pm.InstalledTypes())
                if (t != null && !string.IsNullOrEmpty(t.TypeId)) installedIds.Add(t.TypeId);
            _availablePets.RemoveAll(p => p == null || installedIds.Contains(p.Id));

            string status = "Downloaded " + installed + (installed == 1 ? " pet." : " pets.");
            if (failed > 0)
                status += " " + failed + (failed == 1 ? " pet" : " pets") + " failed" +
                    (lastError.Length > 0 ? " (" + lastError + ")" : "") + ".";
            return status;
        }

        /// <summary>Replace the cached browse result with the catalog pets not on disk yet; returns the count.</summary>
        private int CacheMissingPets(IReadOnlyList<CatalogItem> items)
        {
            _availablePets.Clear();
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IPetManager pm = Manager();
            if (pm != null)
                foreach (PetTypeInfo t in pm.InstalledTypes())
                    if (t != null && !t.IsBuiltIn && !string.IsNullOrEmpty(t.TypeId)) installed.Add(t.TypeId);
            if (items != null)
                foreach (CatalogItem it in items)
                    if (it != null && !string.IsNullOrEmpty(it.Id) && !installed.Contains(it.Id)) _availablePets.Add(it);
            _selectedPets.RemoveWhere(id => installed.Contains(id));
            return _availablePets.Count;
        }

        private static string ApproximateSize(int bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 160 ? message.Substring(0, 160) + "…" : message;
        }

        /// <summary>Self-test hook (NOT the ABI): the pane title the module contributes.</summary>
        public string SelfTestPaneTitle() { return "Pets"; }
    }
}
