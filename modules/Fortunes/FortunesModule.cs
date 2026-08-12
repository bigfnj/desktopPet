using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopPet.Ai;
using DesktopPet.Modules;

namespace DesktopPet.FortunesModule
{
    /// <summary>
    /// The Fortunes module (S3). Owns the pet's fortune voice: a personalized welcome on the first spawn,
    /// then a fortune on land / poke (1-2) / the periodic drop — spoken from the module's relocated engine
    /// (dumb random + smart ONNX-semantic pick). It ships NO fortune content, so with no installed pack it's
    /// silent except the welcome; the engine reads packs from the module's own storage. Since S3d it is the
    /// LIVE source (the base no longer speaks fortunes); the poke escalation's ignore/sass/escape stay in the
    /// base engine, which just raises PetPoked with the count.
    /// </summary>
    public sealed class FortunesModule : IModule
    {
        private IHost _host;
        private string[] _welcome;
        private readonly Random _rand = new Random();
        private bool _welcomed;

        private FortuneProvider _provider;   // the relocated engine (packs -> filtered pool)
        private SmartFortunes _smart;        // optional ONNX semantic picker (null when disabled/unavailable)
        private IPet _lastPet;               // most-recently-seen pet, for screen-context capture on the drop path
        private IDisposable _dropResponder;
        private IDisposable _pokeResponder;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "fortunes",
            Name = "Fortunes",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.ScreenContext | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _welcome = LoadWelcomeCorpus();

            // Point the engine at the module's own storage, then build the pool from the user's packs (empty
            // by default = silent) and warm the smart picker when enabled. All best-effort: a failure here
            // leaves the module welcome-only rather than breaking the host.
            try
            {
                IModuleStorage storage = host.GetStorage("fortunes");
                if (storage != null && !string.IsNullOrEmpty(storage.DataDirectory))
                    FortunePaths.SetRoot(storage.DataDirectory);
            }
            catch { }
            RebuildEngine();

            host.PetSpawned += OnPetSpawned;
            host.PetLanded += OnPetLanded;
            host.PetPoked += OnPetPoked;
            _dropResponder = host.RegisterDropResponder(0, OnDrop);   // lowest priority; the AI brain (S4) will outrank
            // Poke 1 of a session: speak a fortune if the user's "Trigger Speech" choice lets us win the
            // arbitration. Same priority ordering as the drop (the AI brain outranks), but that only decides
            // ties when the user hasn't picked a specific source.
            _pokeResponder = host.RegisterPokeResponder(Info.Id, 0, SpeakFortune);

            // Contribute the fortunes settings as a schema-driven OptionsPane (S5b): the host renders it in
            // the WPF settings window and round-trips values through Load/Save, which persist to the module's
            // own host.GetSettings("fortunes") store and rebuild the live engine so a change takes effect on
            // the running pet at once. The richer sources / genres / packs list is a follow-up (it needs a
            // list-card primitive); this pane covers the selection + content-level toggles.
            host.AddOptionsPane(BuildOptionsPane());
        }

        /// <summary>(Re)build the engine from the current saved settings: rebuild the pool from the user's
        /// packs and, when smart picks are on, (re)warm the semantic index. Called at Init and after the
        /// Options pane saves, so a settings change (or a pack added to the folder) applies without a restart.</summary>
        private void RebuildEngine()
        {
            try
            {
                FortuneSettings settings = LoadFortuneSettings(_host);
                _provider = new FortuneProvider(settings);
                SmartFortunes old = _smart;
                _smart = null;
                if (old != null) { try { old.Dispose(); } catch { } }
                if (settings.SmartFortunes)
                {
                    var sm = new SmartFortunes();
                    sm.Warm(_provider.PoolEntries());
                    _smart = sm;
                }
            }
            catch { _provider = null; _smart = null; }
        }

        // The first pet of the session gets a personalized greeting; later spawns don't re-welcome.
        private void OnPetSpawned(IPet pet)
        {
            _lastPet = pet ?? _lastPet;
            if (_welcomed) return;
            _welcomed = true;
            IHost host = _host;
            if (host == null) return;
            string line = PickWelcome(GreetingName(host));
            if (!string.IsNullOrEmpty(line)) host.SayAll(line);
        }

        private void OnPetLanded(IPet pet) { _lastPet = pet ?? _lastPet; SpeakFortune(); }

        // Track the poked pet for screen-context capture; SPEAKING on a poke goes through the arbitrated
        // poke-responder chain instead (see RegisterPokeResponder in Init), so exactly one module wins it.
        private void OnPetPoked(PokeInfo info)
        {
            if (info == null) return;
            _lastPet = info.Pet ?? _lastPet;
        }

        // The periodic drop responder: speak a fortune. Returns true when it actually spoke (handled), so the
        // arbitrated drop chain stops here (fortunes are the lowest-priority default).
        private bool OnDrop() { return SpeakFortune(); }

        /// <summary>
        /// Speak a fortune — smart/contextual pick when the picker is ready, else random from the pool.
        /// Mirrors the old StartUp.SayFortune. Returns true if a line was spoken.
        /// </summary>
        private bool SpeakFortune()
        {
            IHost host = _host;
            FortuneProvider provider = _provider;
            if (host == null || provider == null || !host.SpeechEnabled) return false;

            string f = null;
            // Roughly a third of the time draw from the whole pool even when smart is ready, so a rarely-
            // changing foreground window doesn't lock the pet onto the same handful of context matches.
            bool goRandom = _rand.Next(3) == 0;
            SmartFortunes picker = _smart;
            if (!goRandom && picker != null && picker.Ready)
            {
                try
                {
                    ScreenContext ctx = _lastPet != null ? host.CaptureScreenContext(_lastPet) : null;
                    if (ctx != null) f = picker.Pick(ctx.WindowTitle, ctx.ProcessName);
                }
                catch { f = null; }
            }
            if (string.IsNullOrWhiteSpace(f)) f = provider.Pick();
            if (string.IsNullOrWhiteSpace(f)) return false;
            host.SayAll(f);
            return true;
        }

        private static FortuneSettings LoadFortuneSettings(IHost host)
        {
            var s = new FortuneSettings();
            try
            {
                IModuleSettings ms = host.GetSettings("fortunes");
                if (ms != null)
                {
                    s.SpicyFortunes = ms.GetBool("spicyFortunes", s.SpicyFortunes);
                    s.SpicyTier = ms.Get("spicyTier", s.SpicyTier);
                    s.SpicyOnly = ms.GetBool("spicyOnly", s.SpicyOnly);
                    s.NoProfanity = ms.GetBool("noProfanity", s.NoProfanity);
                    s.SmartFortunes = ms.GetBool("smartFortunes", s.SmartFortunes);
                    s.DisabledSources = SplitList(ms.Get("disabledSources", ""));
                    s.DisabledGenres = SplitList(ms.Get("disabledGenres", ""));
                }
            }
            catch { }
            return s;
        }

        // ---- Options pane (S5b): selection + content-level toggles ---------------------------------

        // The spice tier is stored as "edgy" | "nsfw"; the pane shows friendly labels and maps back on save.
        private const string TierEdgyDisplay = "Edgy + NSFW";
        private const string TierNsfwDisplay = "True NSFW only";
        private static string TierToDisplay(string tier)
        {
            return string.Equals(tier, "nsfw", StringComparison.OrdinalIgnoreCase) ? TierNsfwDisplay : TierEdgyDisplay;
        }
        private static string DisplayToTier(string display)
        {
            return string.Equals(display, TierNsfwDisplay, StringComparison.Ordinal) ? "nsfw" : "edgy";
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Fortunes",
                Schema = new[]
                {
                    new SettingField { Id = "smartFortunes", Label = "Smart, context-aware picks", Kind = SettingKind.Bool, Group = "Selection" },
                    new SettingField { Id = "spicyFortunes", Label = "Enable spicy content", Kind = SettingKind.Bool, Group = "Content level" },
                    new SettingField { Id = "spicyTier", Label = "Spice level (when spicy is on)", Kind = SettingKind.Enum, Options = new[] { TierEdgyDisplay, TierNsfwDisplay }, Group = "Content level" },
                    new SettingField { Id = "spicyOnly", Label = "Skip the tame ones", Kind = SettingKind.Bool, Group = "Content level" },
                    new SettingField { Id = "noProfanity", Label = "Remove profanity / explicit words", Kind = SettingKind.Bool, Group = "Content level" },
                },
                Load = LoadPaneValues,
                Save = SavePaneValues,
                Actions = new[]
                {
                    new PaneAction { Label = "Rebuild smart index", InvokeAsync = RebuildSmartIndexAsync, Group = "Selection" },
                },
                // (pack browse/download buttons live on the Fortune packs card below, next to the folder ones)
                Lists = new[]
                {
                    new ListCard
                    {
                        Title = "Fortune packs",
                        LoadItems = LoadSourceItems,
                        SetChecked = SetSourceActive,
                        Filterable = true,
                        CollapseGroups = true,
                        EmptyHint = "No fortune packs yet. Use “Available online” below to get them from the " +
                            "catalog, or “Open fortunes folder” to drop your own .txt pack in and Rescan.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Import your own…", InvokeAsync = ImportPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Open fortunes folder", InvokeAsync = OpenFortunesFolderAsync },
                            new PaneAction { Label = "Rescan folder", InvokeAsync = RescanAsync, ReloadPaneAfter = true },
                        },
                    },
                    // Browse -> tick what you want -> download only those. Ticking is deliberately just an
                    // in-memory mark (SetChecked is synchronous, so it must never do network work); the
                    // download button owns the actual fetching and reports progress.
                    new ListCard
                    {
                        Title = "Available online",
                        LoadItems = LoadAvailablePackItems,
                        SetChecked = SetPackSelected,
                        Filterable = true,
                        CollapseGroups = true,
                        EmptyHint = "Click “Check online for packs” to see what the catalog offers.",
                        Actions = new[]
                        {
                            new PaneAction { Label = "Check online for packs", InvokeAsync = CheckPacksOnlineAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Download selected", InvokeAsync = DownloadPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select all", InvokeAsync = SelectAllPacksAsync, ReloadPaneAfter = true },
                            new PaneAction { Label = "Select none", InvokeAsync = SelectNoPacksAsync, ReloadPaneAfter = true },
                        },
                    },
                    new ListCard
                    {
                        Title = "Genres",
                        LoadItems = LoadGenreItems,
                        SetChecked = SetGenreActive,
                        EmptyHint = "Genres appear here once you add a pack.",
                    },
                },
            };
        }

        // ---- list cards: fortune packs (sources) + genres -----------------------------------------

        private IReadOnlyList<ListItem> LoadSourceItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledSources")), StringComparer.OrdinalIgnoreCase);
                foreach (SourceStat st in FortuneProvider.Sources())
                {
                    string detail = st.Count + (st.Count == 1 ? " line" : " lines");
                    if (st.HasSpicy) detail += " · spicy";
                    items.Add(new ListItem
                    {
                        Id = st.Id,
                        Label = PrettySource(st.Id),
                        Detail = detail,
                        Checked = !disabled.Contains(st.Id),
                        // The curated map is the only reliable signal for "is this a catalog pack?" --
                        // SourceStat.Custom is true for ANYTHING in the user's fortunes folder, which
                        // includes every catalog pack once downloaded, so it can't tell them apart.
                        Group = CollectionFor(st.Id),
                    });
                }
            }
            catch { }
            return items;
        }

        // ---- pack -> collection map (embedded copy of packs/collections.json) -----------------------

        private static Dictionary<string, string> _collectionBySource;

        /// <summary>The curated collection name for a pack id, or "Your own packs" when the map has no
        /// entry (a file the user wrote or imported, or a catalog pack newer than this build). Loaded once,
        /// best-effort: a missing or malformed map just means everything groups as the user's own.</summary>
        private static string CollectionFor(string sourceId)
        {
            Dictionary<string, string> map = _collectionBySource;
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string json = ReadEmbeddedText("collections.json");
                    if (json != null)
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            JsonElement collections;
                            if (doc.RootElement.TryGetProperty("collections", out collections) &&
                                collections.ValueKind == JsonValueKind.Array)
                                foreach (JsonElement c in collections.EnumerateArray())
                                {
                                    JsonElement nameEl, sourcesEl;
                                    if (!c.TryGetProperty("name", out nameEl) ||
                                        !c.TryGetProperty("sources", out sourcesEl) ||
                                        sourcesEl.ValueKind != JsonValueKind.Array) continue;
                                    string name = nameEl.GetString() ?? "";
                                    if (name.Length == 0) continue;
                                    foreach (JsonElement src in sourcesEl.EnumerateArray())
                                    {
                                        string id = src.GetString();
                                        if (!string.IsNullOrEmpty(id)) map[id] = name;
                                    }
                                }
                        }
                }
                catch { }
                _collectionBySource = map;
            }
            string group;
            return map.TryGetValue(sourceId ?? "", out group) ? group : "Your own packs";
        }

        private IReadOnlyList<ListItem> LoadGenreItems()
        {
            var items = new List<ListItem>();
            try
            {
                var disabled = new HashSet<string>(SplitList(GetSetting("disabledGenres")), StringComparer.OrdinalIgnoreCase);
                foreach (GenreStat g in FortuneProvider.Genres())
                    items.Add(new ListItem { Id = g.Id, Label = g.Id, Detail = g.Count + (g.Count == 1 ? " line" : " lines"), Checked = !disabled.Contains(g.Id) });
            }
            catch { }
            return items;
        }

        private void SetSourceActive(string id, bool active) { SetDisabled("disabledSources", id, !active); }
        private void SetGenreActive(string id, bool active) { SetDisabled("disabledGenres", id, !active); }

        // Toggle an id in a persisted "disabled" list, then rebuild the live engine so the change applies now.
        private void SetDisabled(string key, string id, bool disabled)
        {
            IHost host = _host;
            if (host == null || string.IsNullOrEmpty(id)) return;
            try
            {
                IModuleSettings ms = host.GetSettings("fortunes");
                if (ms == null) return;
                var set = new List<string>();
                foreach (string x in SplitList(ms.Get(key, "")))
                    if (!string.Equals(x, id, StringComparison.OrdinalIgnoreCase)) set.Add(x);
                if (disabled) set.Add(id);
                ms.Set(key, string.Join("\n", set));
                ms.Save();
                RebuildEngine();
            }
            catch { }
        }

        private Task<string> OpenFortunesFolderAsync()
        {
            try
            {
                string dir = FortunePaths.FortunesDir;   // created on access
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Task.FromResult("Opened the fortunes folder — drop .txt packs there, then Rescan.");
            }
            catch (Exception ex) { return Task.FromResult("Couldn't open the folder: " + ex.Message); }
        }

        private Task<string> RescanAsync()
        {
            try
            {
                RebuildEngine();
                int sources = 0;
                try { sources = FortuneProvider.Sources().Count; } catch { }
                return Task.FromResult(sources == 0
                    ? "No packs found yet."
                    : ("Rescanned — " + sources + (sources == 1 ? " pack" : " packs") + " loaded."));
            }
            catch (Exception ex) { return Task.FromResult("Rescan failed: " + ex.Message); }
        }

        /// <summary>
        /// Import the user's own .txt packs through <see cref="FortuneFileImporter"/> — the strict, bounded,
        /// per-file atomic path (size/entry caps, staged writes) rather than a raw copy, so a malformed or
        /// oversized file is rejected instead of poisoning the pool. The host owns the file dialog (modules
        /// carry no UI framework). Existing files are never silently overwritten: nothing is approved for
        /// overwrite here, so a same-named pack is reported as skipped and the user renames or removes it.
        /// </summary>
        private Task<string> ImportPacksAsync()
        {
            IHost host = _host;
            if (host == null) return Task.FromResult("No host.");
            try
            {
                IReadOnlyList<string> chosen = host.PickFilesToOpen(
                    "Import fortune packs", "Fortune packs", new[] { "txt" });
                if (chosen == null || chosen.Count == 0) return Task.FromResult("");   // cancelled

                FortuneImportBatchResult result = FortuneFileImporter.Import(
                    chosen,
                    FortunePaths.FortunesDir,
                    null,                                   // no overwrite approved (see summary)
                    System.Threading.CancellationToken.None);

                if (result.ImportedCount > 0) RebuildEngine();   // new lines join the pool immediately

                string status = "Imported " + result.ImportedCount +
                    (result.ImportedCount == 1 ? " pack." : " packs.");
                if (result.RejectedCount > 0)
                {
                    string firstError = "";
                    foreach (FortuneImportItemResult item in result.Items)
                        if (!item.Imported && !string.IsNullOrWhiteSpace(item.Error)) { firstError = Short(item.Error); break; }
                    status += " " + result.RejectedCount + (result.RejectedCount == 1 ? " file" : " files") +
                        " rejected" + (firstError.Length > 0 ? " (" + firstError + ")" : "") + ".";
                }
                return Task.FromResult(status);
            }
            catch (Exception ex) { return Task.FromResult("✗ Import failed: " + Short(ex.Message)); }
        }

        // ---- catalog packs (browse + download through the host) -------------------------------------

        // Last browse result (catalog packs not on disk yet) and the subset the user ticked for download.
        // Both are in-memory only: browsing writes nothing, and ticking writes nothing — the download
        // button is the only thing that touches the network or the disk.
        private readonly List<CatalogItem> _availablePacks = new List<CatalogItem>();
        private readonly HashSet<string> _selectedPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<ListItem> LoadAvailablePackItems()
        {
            var items = new List<ListItem>();
            foreach (CatalogItem pack in _availablePacks)
            {
                // The group is its own field now (the card renders collapsible sections), so the detail
                // stays the per-pack facts: how much content, and roughly how big.
                string detail = pack.Count > 0
                    ? (pack.Count + (pack.Count == 1 ? " line" : " lines"))
                    : ApproximateSize(pack.Bytes);
                items.Add(new ListItem
                {
                    Id = pack.Id,
                    Label = string.IsNullOrWhiteSpace(pack.Name) ? PrettySource(pack.Id) : pack.Name,
                    Detail = detail,
                    Checked = _selectedPacks.Contains(pack.Id),
                    Group = string.IsNullOrWhiteSpace(pack.Group) ? CollectionFor(pack.Id) : pack.Group.Trim(),
                });
            }
            return items;
        }

        // Ticking a row only marks it — instant, no network (SetChecked is synchronous by contract).
        private void SetPackSelected(string id, bool selected)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (selected) _selectedPacks.Add(id);
            else _selectedPacks.Remove(id);
        }

        private Task<string> SelectAllPacksAsync()
        {
            foreach (CatalogItem pack in _availablePacks)
                if (!string.IsNullOrEmpty(pack.Id)) _selectedPacks.Add(pack.Id);
            return Task.FromResult(_availablePacks.Count == 0
                ? "Nothing listed yet — click “Check online for packs” first."
                : ("Selected all " + _availablePacks.Count + " packs."));
        }

        private Task<string> SelectNoPacksAsync()
        {
            _selectedPacks.Clear();
            return Task.FromResult("Cleared the selection.");
        }

        /// <summary>Fetch the catalog and list the packs that aren't installed yet. Read-only: nothing is
        /// downloaded, written, or selected — the user picks from the list, then hits Download selected.</summary>
        private async Task<string> CheckPacksOnlineAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            try
            {
                IReadOnlyList<CatalogItem> items = await host.FetchCatalogItemsAsync(CatalogKinds.Pack).ConfigureAwait(false);
                int available = CacheMissingPacks(items);
                if (items.Count == 0) return "The catalog lists no fortune packs.";
                return available == 0
                    ? ("You already have every catalog pack (" + items.Count + ").")
                    : (available + (available == 1 ? " pack" : " packs") +
                       " available — tick the ones you want, then “Download selected”.");
            }
            catch (Exception ex) { return "✗ Couldn't reach the catalog: " + Short(ex.Message); }
        }

        /// <summary>Download the ticked packs, then rebuild the engine so they're live immediately. Each
        /// pack's bytes are HTTPS-fetched and SHA-256-verified by the host before they reach us; we only
        /// decide the filename, and reject any id that isn't a plain pack id so a catalog entry can never
        /// steer the write outside the fortunes folder.</summary>
        private async Task<string> DownloadPacksAsync()
        {
            IHost host = _host;
            if (host == null) return "No host.";
            if (_availablePacks.Count == 0)
                return "Nothing listed yet — click “Check online for packs” first.";
            if (_selectedPacks.Count == 0)
                return "No packs ticked — choose some (or “Select all”), then Download selected.";
            try
            {
                string directory = FortunePaths.FortunesDir;   // created on access
                var pending = new List<CatalogItem>();
                foreach (CatalogItem item in _availablePacks)
                    if (item != null && _selectedPacks.Contains(item.Id)) pending.Add(item);

                int installed = 0, failed = 0;
                string lastError = "";
                foreach (CatalogItem item in pending)
                {
                    try
                    {
                        if (!IsPlainPackId(item.Id)) { failed++; continue; }
                        byte[] bytes = await host.DownloadCatalogItemAsync(CatalogKinds.Pack, item.Id).ConfigureAwait(false);
                        if (bytes == null || bytes.Length == 0) { failed++; continue; }
                        File.WriteAllBytes(Path.Combine(directory, item.Id + ".txt"), bytes);
                        _selectedPacks.Remove(item.Id);
                        installed++;
                    }
                    catch (Exception ex) { failed++; lastError = Short(ex.Message); }
                }

                RebuildEngine();   // the new packs join the pool (and the smart index) right away
                // Drop the installed ones from the available list so the card shows what's still missing.
                CacheMissingPacks(_availablePacks);
                string status = "Downloaded " + installed + (installed == 1 ? " pack." : " packs.");
                if (failed > 0)
                    status += " " + failed + (failed == 1 ? " pack" : " packs") + " failed" +
                        (lastError.Length > 0 ? " (" + lastError + ")" : "") + ".";
                return status;
            }
            catch (Exception ex) { return "✗ Download failed: " + Short(ex.Message); }
        }

        /// <summary>Replace the cached browse result with the catalog packs that aren't on disk yet, and
        /// forget any selection for packs that no longer apply. Returns how many are available.</summary>
        private int CacheMissingPacks(IReadOnlyList<CatalogItem> items)
        {
            var source = new List<CatalogItem>(items ?? (IReadOnlyList<CatalogItem>)new List<CatalogItem>());
            _availablePacks.Clear();
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (SourceStat st in FortuneProvider.Sources()) installed.Add(st.Id);
            }
            catch { }
            foreach (CatalogItem item in source)
                if (item != null && !string.IsNullOrEmpty(item.Id) && !installed.Contains(item.Id))
                    _availablePacks.Add(item);
            _selectedPacks.RemoveWhere(id => installed.Contains(id));
            return _availablePacks.Count;
        }

        private static string ApproximateSize(int bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        // A pack id must be a bare file-name stem: no separators, no drive/relative parts. The host already
        // validates catalog ids, but this module writes the file, so it re-checks rather than trusting.
        private static bool IsPlainPackId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return id.IndexOf("..", StringComparison.Ordinal) < 0 &&
                !string.Equals(id, ".", StringComparison.Ordinal);
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 160 ? message.Substring(0, 160) + "…" : message;
        }

        private string GetSetting(string key)
        {
            try { IModuleSettings ms = _host != null ? _host.GetSettings("fortunes") : null; return ms != null ? ms.Get(key, "") : ""; }
            catch { return ""; }
        }

        // Persisted disabled-list format: ids joined by '\n' (source ids/genres never contain newlines).
        private static List<string> SplitList(string joined)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(joined)) return list;
            foreach (string part in joined.Split('\n'))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// The label for a pack: its curated display name when known, else a prettified id. Pack ids are
        /// raw file stems ("lwall-quotes", "rfc1925", "off-knghtbrd") that say nothing about what the pack
        /// contains, so the curated map is what makes the picker readable; the prettified fallback keeps a
        /// user's own file (or a catalog pack newer than this build) from showing up blank.
        /// </summary>
        private static string PrettySource(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            string name;
            if (PackNames().TryGetValue(id, out name) && !string.IsNullOrWhiteSpace(name)) return name;
            return id.Replace('-', ' ').Replace('_', ' ');
        }

        private static Dictionary<string, string> _packNames;

        private static Dictionary<string, string> PackNames()
        {
            Dictionary<string, string> map = _packNames;
            if (map != null) return map;
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = ReadEmbeddedText("pack-names.json");
                if (json != null)
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        JsonElement names;
                        if (doc.RootElement.TryGetProperty("names", out names) &&
                            names.ValueKind == JsonValueKind.Object)
                            foreach (JsonProperty p in names.EnumerateObject())
                                if (p.Value.ValueKind == JsonValueKind.String)
                                    map[p.Name] = p.Value.GetString() ?? "";
                    }
            }
            catch { }
            _packNames = map;
            return map;
        }

        /// <summary>Read one of the module's embedded JSON maps, or null when absent/unreadable.</summary>
        private static string ReadEmbeddedText(string fileName)
        {
            try
            {
                Assembly asm = typeof(FortunesModule).Assembly;
                string resource = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) { resource = n; break; }
                if (resource == null) return null;
                using (Stream s = asm.GetManifestResourceStream(resource))
                {
                    if (s == null) return null;
                    using (var reader = new StreamReader(s, new UTF8Encoding(false)))
                        return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        private IReadOnlyDictionary<string, string> LoadPaneValues()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            FortuneSettings s = LoadFortuneSettings(_host);
            d["smartFortunes"] = s.SmartFortunes ? "true" : "false";
            d["spicyFortunes"] = s.SpicyFortunes ? "true" : "false";
            d["spicyTier"] = TierToDisplay(s.SpicyTier);
            d["spicyOnly"] = s.SpicyOnly ? "true" : "false";
            d["noProfanity"] = s.NoProfanity ? "true" : "false";
            return d;
        }

        private bool SavePaneValues(IReadOnlyDictionary<string, string> values)
        {
            IHost host = _host;
            if (host == null || values == null) return false;
            IModuleSettings ms = host.GetSettings("fortunes");
            if (ms == null) return false;
            string v; bool b;
            if (values.TryGetValue("smartFortunes", out v) && bool.TryParse(v, out b)) ms.Set("smartFortunes", b ? "true" : "false");
            if (values.TryGetValue("spicyFortunes", out v) && bool.TryParse(v, out b)) ms.Set("spicyFortunes", b ? "true" : "false");
            if (values.TryGetValue("spicyTier", out v) && !string.IsNullOrEmpty(v)) ms.Set("spicyTier", DisplayToTier(v));
            if (values.TryGetValue("spicyOnly", out v) && bool.TryParse(v, out b)) ms.Set("spicyOnly", b ? "true" : "false");
            if (values.TryGetValue("noProfanity", out v) && bool.TryParse(v, out b)) ms.Set("noProfanity", b ? "true" : "false");
            bool ok = ms.Save();
            RebuildEngine();   // re-read + rebuild so the running pet uses the new settings at once
            return ok;
        }

        /// <summary>"Rebuild smart index" action: reload packs from disk and (when smart is on) re-warm the
        /// semantic index, then report status. Also the way to pick up a pack dropped straight into the
        /// folder until the sources/packs card lands.</summary>
        private Task<string> RebuildSmartIndexAsync()
        {
            try
            {
                RebuildEngine();
                return Task.FromResult(SmartStatusText());
            }
            catch (Exception ex) { return Task.FromResult("Rebuild failed: " + ex.Message); }
        }

        private string SmartStatusText()
        {
            SmartFortunes sm = _smart;
            if (sm == null) return "Smart picks are off (random selection).";
            bool ready, complete; int indexed, total;
            sm.WarmProgress(out ready, out complete, out indexed, out total);
            if (total == 0) return "No fortunes yet — add a pack, then rebuild.";
            if (complete) return "Smart index ready — " + indexed + " fortunes indexed.";
            if (ready) return "Smart index warming — " + indexed + " of " + total + " ready (usable now).";
            return "Smart index building… (" + total + " fortunes)";
        }

        /// <summary>Pick a welcome line and substitute the name into its {name} slot (fallback "friend").</summary>
        internal string PickWelcome(string name)
        {
            string[] corpus = _welcome;
            if (corpus == null || corpus.Length == 0) return null;
            string who = string.IsNullOrWhiteSpace(name) ? "friend" : name.Trim();
            string line = corpus[_rand.Next(corpus.Length)];
            return line == null ? null : line.Replace("{name}", who);
        }

        private static string CurrentUserName()
        {
            try
            {
                string u = Environment.UserName;
                return string.IsNullOrWhiteSpace(u) ? "friend" : u;
            }
            catch { return "friend"; }
        }

        // Who to greet: the host-published owner name (set by the AI brain when it's on) wins; otherwise fall
        // back to the Windows user name. Keeps the out-of-box welcome, but lets the configured AI name override.
        private static string GreetingName(IHost host)
        {
            try
            {
                string owner = host != null ? host.OwnerName : null;
                if (!string.IsNullOrWhiteSpace(owner)) return owner.Trim();
            }
            catch { }
            return CurrentUserName();
        }

        public void Shutdown()
        {
            IHost host = _host;
            if (host != null)
            {
                host.PetSpawned -= OnPetSpawned;
                host.PetLanded -= OnPetLanded;
                host.PetPoked -= OnPetPoked;
            }
            if (_dropResponder != null) { try { _dropResponder.Dispose(); } catch { } _dropResponder = null; }
            if (_pokeResponder != null) { try { _pokeResponder.Dispose(); } catch { } _pokeResponder = null; }
            if (_smart != null) { try { _smart.Dispose(); } catch { } _smart = null; }
            _provider = null;
            _host = null;
        }

        /// <summary>
        /// Load the embedded welcome corpus (a JSON array of "{name}"-templated one-liners). Returns an empty
        /// array on any failure so the module simply stays quiet rather than throwing into the host.
        /// </summary>
        private static string[] LoadWelcomeCorpus()
        {
            try
            {
                Assembly asm = typeof(FortunesModule).Assembly;
                string resource = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("welcome.json", StringComparison.OrdinalIgnoreCase)) { resource = n; break; }
                if (resource == null) return Array.Empty<string>();
                using (Stream s = asm.GetManifestResourceStream(resource))
                {
                    if (s == null) return Array.Empty<string>();
                    byte[] buf;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        buf = ms.ToArray();
                    }
                    string[] lines = JsonSerializer.Deserialize<string[]>(new ReadOnlySpan<byte>(buf));
                    return lines ?? Array.Empty<string>();
                }
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Self-test hook (NOT the ABI): number of welcome lines loaded.</summary>
        public int WelcomeCorpusCount() { return _welcome == null ? 0 : _welcome.Length; }
    }
}
