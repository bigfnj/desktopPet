using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;   // Run, Hyperlink (inline clickable size)
using System.Windows.Input;       // Cursors
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Options;   // PetsController, PetRow, IPetRuntime

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Host-built Pets gallery for the WPF settings window (S5b-2c): a card per installed pet (thumbnail +
    /// name + Use/Add/Remove + an Active marker), backed by the base <see cref="PetsController"/>. A footer
    /// "Check for new pets" button (S5b-2c4) fetches the online catalog, diffs it against the locally present
    /// pets, and offers any new ones as download cards — the same HTTPS-trusted, SHA-256-verified path the
    /// classic Options window used, reused here through <see cref="RemoteCatalogClient"/>. Use/Add apply
    /// immediately through the runtime, so this pane has no separate Apply button.
    /// </summary>
    internal sealed class PetsPaneControl : ContentControl
    {
        private readonly PetsController _pets;
        private readonly WrapPanel _grid = new WrapPanel { Margin = new Thickness(4) };
        private readonly TextBlock _availableHeader = new TextBlock
        {
            Text = "Available to download",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 10, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly WrapPanel _availableGrid = new WrapPanel { Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        private readonly Button _checkButton = new Button
        {
            Content = "Check for new pets",
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 0, 0, 4),
        };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 4, 0, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        // The most recent successful catalog fetch, so a download can re-diff locally without re-fetching.
        private RemoteCatalog _lastCatalog;
        private CancellationTokenSource _netCts;

        public PetsPaneControl()
        {
            _pets = new PetsController(Program.Mainthread as IPetRuntime);

            var root = new DockPanel { LastChildFill = true };

            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock { Text = "Pets", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock { Text = "Pick a look for your pet. “Use” replaces the current pet; “Add” spawns one alongside.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            footer.Children.Add(_checkButton);
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scrollContent = new StackPanel();
            scrollContent.Children.Add(_grid);
            scrollContent.Children.Add(_availableHeader);
            scrollContent.Children.Add(_availableGrid);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = scrollContent });
            Content = root;

            _checkButton.Click += CheckButton_Click;
            Unloaded += delegate { try { if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); _netCts = null; } } catch { } };

            Reload();
        }

        private void Reload()
        {
            _grid.Children.Clear();
            try
            {
                _pets.Load();
                Dictionary<string, int> mix = BuildMixDict();
                foreach (PetRow row in _pets.State.Installed)
                    _grid.Children.Add(BuildCard(row, mix));
            }
            catch (Exception ex) { _status.Text = "Couldn't list pets: " + ex.Message; }
        }

        // The live on-screen mix (id -> count). The active/default type's pets are keyed "" (see StartUp.OnScreenMix).
        private static Dictionary<string, int> BuildMixDict()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mix = Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
                if (mix != null)
                    foreach (PetCountEntry e in mix)
                    {
                        string id = e.Id ?? "";
                        int c; d.TryGetValue(id, out c); d[id] = c + e.Count;
                    }
            }
            catch { }
            return d;
        }

        private FrameworkElement BuildCard(PetRow row, Dictionary<string, int> mix)
        {
            string addId = row.IsBuiltIn ? PetCatalog.BuiltInPetId : (row.Id ?? "");
            int onScreen = 0, c;
            if (mix.TryGetValue(addId, out c)) onScreen += c;
            int defaultCount = 0;                      // the active type's pets are keyed "" in the mix
            if (row.IsActive && mix.TryGetValue("", out c)) defaultCount = c;
            onScreen += defaultCount;

            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 224 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(addId);
            if (img == null && row.IsBuiltIn) img = LoadAppIcon();   // the default eSheep isn't in the thumbnail zip
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = row.DisplayName ?? row.Id, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (onScreen > 0)
                nameStack.Children.Add(new TextBlock { Text = (row.IsActive ? "active · " : "") + "on screen: " + onScreen, FontSize = 11, Foreground = Brushes.ForestGreen });
            top.Children.Add(nameStack);
            sp.Children.Add(top);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (!row.IsActive)
            {
                var use = new Button { Content = "Use", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
                use.Click += delegate { _status.Text = _pets.UsePet(addId).Ok ? (row.DisplayName + " is now your pet.") : "Couldn't apply that pet."; Reload(); };
                btns.Children.Add(use);
            }
            var add = new Button { Content = "Add", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
            add.Click += delegate { _status.Text = _pets.AddPet(addId).Ok ? ("Added " + row.DisplayName + ".") : "Couldn't add (max pets reached?)."; Reload(); };
            btns.Children.Add(add);
            if (onScreen > 0)
            {
                string removeId = defaultCount > 0 ? "" : addId;   // remove one of this type (active default = "")
                var remove = new Button { Content = "Remove", Width = 66 };
                remove.Click += delegate
                {
                    try { if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(removeId); } catch { }
                    _status.Text = "Removed one " + row.DisplayName + ".";
                    Reload();
                };
                btns.Children.Add(remove);
            }
            // Uninstall: delete an INSTALLED library pet (downloaded / converted / authored). Never offered
            // for the built-in eSheep or the active pet; only when the pet actually lives in the writable
            // library folder. "Remove" above just despawns one instance -- this deletes it for good.
            if (!row.IsActive && !row.IsBuiltIn && LibraryFolderExists(addId))
            {
                var uninstall = new Button { Content = "Uninstall", Width = 78, Margin = new Thickness(5, 0, 0, 0) };
                string display = row.DisplayName ?? row.Id;
                int onScreenCopy = onScreen;
                uninstall.Click += delegate { UninstallPet(addId, display, onScreenCopy); };
                btns.Children.Add(uninstall);
            }
            sp.Children.Add(btns);

            // Description (a unique quip) + animation/sound counts.
            sp.Children.Add(new TextBlock
            {
                Text = PetBlurbs.For(addId),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            sp.Children.Add(BuildStatsLine(addId, row.DisplayName ?? row.Id, GetStats(addId)));

            card.Child = sp;
            return card;
        }

        // The stats line ("N animations · M sounds · size K") with the size level as an inline, clickable
        // number - styled like the surrounding text (no button/box), just a hand cursor. Clicking cycles
        // 1 -> 2 -> 3. The change is baked in when the pet is next staged, so it applies the next time this
        // pet is added (or on restart); pets of this type already on screen keep their size until then
        // (same as the global size control). The number seeds from the pet's stored override, or the
        // effective (global) level when it has none.
        private FrameworkElement BuildStatsLine(string addId, string displayName, PetStats stats)
        {
            var line = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            string prefix = stats.Animations + (stats.Animations == 1 ? " animation" : " animations");
            if (stats.Sounds > 0) prefix += "  ·  " + stats.Sounds + (stats.Sounds == 1 ? " sound" : " sounds");
            prefix += "  ·  size ";
            line.Inlines.Add(new Run(prefix));

            int shown = ResolveShownLevel(addId);
            var number = new Run(shown.ToString());
            var link = new Hyperlink(number)
            {
                Foreground = Brushes.Gray,        // blend into the stats text
                TextDecorations = null,           // no underline at rest (theme adds one on hover)
                Cursor = Cursors.Hand,
                Focusable = false,                // no focus rectangle -> no visible "box"
                ToolTip = "click to cycle size 1 / 2 / 3",
            };
            link.Click += delegate
            {
                shown = shown % 3 + 1;   // 1 -> 2 -> 3 -> 1
                number.Text = shown.ToString();
                try { if (Program.Mainthread != null) Program.Mainthread.SetPetSize(addId, shown); } catch { }
                _status.Text = displayName + " size set to " + shown + ". Add " + displayName + " (or restart) to see it.";
            };
            line.Inlines.Add(link);

            // Per-pet sound toggle (only for pets that have sounds): an inline clickable "sound on/off",
            // same style as the size number. Takes effect on the next sound (the host checks it at play
            // time), no restage. Keyed by the same id, so it works on this pet type wherever it's on screen.
            if (stats.Sounds > 0)
            {
                line.Inlines.Add(new Run("  ·  "));
                bool enabled = true;
                try { if (Program.MyData != null) enabled = Program.MyData.IsPetSoundEnabled(addId); } catch { }
                var soundRun = new Run(enabled ? "sound on" : "sound off");
                var soundLink = new Hyperlink(soundRun)
                {
                    Foreground = Brushes.Gray,
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    ToolTip = "click to mute / unmute this pet's sounds",
                };
                soundLink.Click += delegate
                {
                    enabled = !enabled;
                    soundRun.Text = enabled ? "sound on" : "sound off";
                    try { if (Program.Mainthread != null) Program.Mainthread.SetPetSound(addId, enabled); } catch { }
                    _status.Text = displayName + (enabled ? " sounds on." : " sounds muted.");
                };
                line.Inlines.Add(soundLink);
            }

            return line;
        }

        // A pet's size level to show: its stored override (1/2/3), else the effective global level.
        private static int ResolveShownLevel(string addId)
        {
            int level = 0;
            try { if (Program.MyData != null) level = Program.MyData.GetPetSizeLevel(addId); } catch { }
            if (level >= 1 && level <= 3) return level;
            int global = 1;
            try { if (Program.MyData != null) global = Program.MyData.GetScale(); } catch { }
            return (global >= 1 && global <= 3) ? global : 1;
        }

        // ---- Check for new pets (online catalog) --------------------------------

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _checkButton.IsEnabled = false;
            _status.Text = "Checking for pets online…";
            try
            {
                if (_netCts != null) { _netCts.Cancel(); _netCts.Dispose(); }
                _netCts = new CancellationTokenSource();
                _lastCatalog = await RemoteCatalogClient.FetchAsync(_netCts.Token);
                if (!IsLoaded) return;
                List<CatalogPet> newPets = DiffNew();
                RenderAvailable(newPets);
                _status.Text = newPets.Count > 0
                    ? ("Found " + newPets.Count + (newPets.Count == 1 ? " new pet" : " new pets") + " available to download.")
                    : "You already have every available pet.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't reach the catalog: " + Short(ex.Message); }
            finally { if (IsLoaded) _checkButton.IsEnabled = true; }
        }

        // Catalog pets that are not already present locally (bundled or downloaded).
        private List<CatalogPet> DiffNew()
        {
            var result = new List<CatalogPet>();
            if (_lastCatalog == null) return result;
            HashSet<string> local = LocalPetIds();
            foreach (CatalogPet pet in _lastCatalog.Pets)
                if (!local.Contains(pet.Id)) result.Add(pet);
            return result;
        }

        private void RenderAvailable(List<CatalogPet> pets)
        {
            _availableGrid.Children.Clear();
            bool any = pets.Count > 0;
            _availableHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _availableGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            foreach (CatalogPet pet in pets)
                _availableGrid.Children.Add(BuildDownloadCard(pet));
        }

        private FrameworkElement BuildDownloadCard(CatalogPet pet)
        {
            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 224 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(pet.Id);
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = PetCatalog.DisplayName(pet.Id, pet.Name), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(pet.Author))
                nameStack.Children.Add(new TextBlock { Text = "by " + pet.Author, FontSize = 11, Foreground = Brushes.Gray });
            top.Children.Add(nameStack);
            sp.Children.Add(top);

            var dl = new Button { Content = "Download", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            dl.Click += async delegate { await DownloadPetAsync(pet, dl); };
            sp.Children.Add(dl);

            card.Child = sp;
            return card;
        }

        private async Task DownloadPetAsync(CatalogPet pet, Button dl)
        {
            if (pet == null) return;
            dl.IsEnabled = false;
            _status.Text = "Downloading " + pet.Name + "…";
            try
            {
                if (_netCts == null) _netCts = new CancellationTokenSource();
                byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                    pet.Url, pet.Sha256, PetXmlValidator.MaximumXmlBytes, _netCts.Token);
                if (!IsLoaded) return;

                // A downloaded file is never trusted blindly: validate structure before it lands on disk.
                string xml = SecureDownload.DecodeUtf8(bytes);
                XmlData.RootNode parsed;
                string validationError;
                if (!PetXmlValidator.TryParse(xml, out parsed, out validationError))
                {
                    _status.Text = pet.Name + " failed validation: " + Short(validationError);
                    return;
                }

                string directory = SafeLibraryDir(pet.Id);
                Directory.CreateDirectory(directory);
                SecureDownload.WriteAllBytesAtomic(Path.Combine(directory, "animations.xml"), bytes);

                _status.Text = "Added " + PetCatalog.DisplayName(pet.Id, pet.Name) + " to your pets.";
                Reload();                        // the new pet is now a local card
                RenderAvailable(DiffNew());      // re-diff against the cached catalog (no re-fetch)
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) _status.Text = "Couldn't download " + pet.Name + ": " + Short(ex.Message); }
            finally { if (IsLoaded) dl.IsEnabled = true; }
        }

        private static HashSet<string> LocalPetIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PetCatalog.PetInfo info in PetCatalog.EnumerateLocal())
                if (!info.IsBuiltIn && !string.IsNullOrEmpty(info.Id)) ids.Add(info.Id);
            return ids;
        }

        private static string SafeLibraryDir(string id)
        {
            if (!SecureDownload.IsSafeId(id)) throw new InvalidDataException("Unsafe pet id.");
            string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(Path.Combine(root, id));
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pet path escapes the library.");
            return directory;
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 200 ? message.Substring(0, 200) + "…" : message;
        }

        // Animation + sound counts read from the pet's XML, cached per id (the sheep XMLs are large).
        private sealed class PetStats { public int Animations; public int Sounds; }
        private static readonly Dictionary<string, PetStats> _statsCache = new Dictionary<string, PetStats>(StringComparer.OrdinalIgnoreCase);
        private static PetStats GetStats(string id)
        {
            lock (_statsCache) { PetStats hit; if (_statsCache.TryGetValue(id, out hit)) return hit; }
            var s = new PetStats();
            try
            {
                string xml, err;
                if (PetCatalog.TryReadPetXml(id, out xml, out err) && !string.IsNullOrEmpty(xml))
                {
                    s.Animations = System.Text.RegularExpressions.Regex.Matches(xml, "<animation\\s").Count;
                    s.Sounds = System.Text.RegularExpressions.Regex.Matches(xml, "<sound\\b").Count;
                }
            }
            catch { }
            lock (_statsCache) { _statsCache[id] = s; }
            return s;
        }

        private static ImageSource LoadThumb(string id)
        {
            try
            {
                byte[] png = PetThumbnails.GetPng(id);
                if (png != null) return FromPng(png);
                // No bundled thumbnail: installed / converted / authored pets aren't in the zip. Fall back to
                // the pet's OWN header icon so its gallery card isn't blank (every animations.xml carries one).
                return LoadPetHeaderIcon(id);
            }
            catch { return null; }
        }

        /// <summary>Decode the &lt;header&gt;&lt;icon&gt; ICO from an installed or bundled pet's animations.xml.
        /// WPF's decoder handles the PNG-in-ICO the Shimeji importer emits as well as ordinary icons; returns
        /// null when there is no such pet folder or no icon.</summary>
        private static ImageSource LoadPetHeaderIcon(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string xmlPath = FindPetXml(id);
            if (xmlPath == null) return null;
            try
            {
                XElement icon = XDocument.Load(xmlPath).Descendants().FirstOrDefault(e => e.Name.LocalName == "icon");
                if (icon == null || string.IsNullOrWhiteSpace(icon.Value)) return null;
                byte[] ico = Convert.FromBase64String(icon.Value.Trim());
                using (var ms = new MemoryStream(ico, false))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return null;
                    BitmapFrame frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch { return null; }
        }

        private static string FindPetXml(string id)
        {
            foreach (string dir in new[] { AppPaths.LibraryPetsDirectory, AppPaths.BundledPetsDirectory })
            {
                try
                {
                    string p = Path.Combine(dir, id, "animations.xml");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // Only a pet that actually lives in the writable library can be uninstalled (deleted). Built-in and
        // bundled pets ship with the app and are not the user's to remove.
        private static bool LibraryFolderExists(string id)
        {
            try { return !string.IsNullOrEmpty(id) && File.Exists(Path.Combine(AppPaths.LibraryPetsDirectory, id, "animations.xml")); }
            catch { return false; }
        }

        private void UninstallPet(string id, string name, int onScreen)
        {
            if (MessageBox.Show(
                    "Uninstall “" + name + "”? This deletes it from your pet library.",
                    "Uninstall pet", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                // Contain the delete strictly inside the library so a stray id can never escape it.
                string root = Path.GetFullPath(AppPaths.LibraryPetsDirectory);
                string dir = Path.GetFullPath(Path.Combine(root, id ?? ""));
                if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _status.Text = "Refused: that pet id is not inside the library.";
                    return;
                }
                for (int i = 0; i < onScreen; i++)
                    try { if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(id); } catch { }
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                _status.Text = "Uninstalled " + name + ".";
            }
            catch (Exception ex) { _status.Text = "Couldn't uninstall: " + ex.Message; }
            Reload();
        }

        /// <summary>Fallback thumbnail for the built-in eSheep (not present in the thumbnail zip): the app icon.</summary>
        private static ImageSource LoadAppIcon()
        {
            try
            {
                using (var bmp = DesktopPet.Properties.Resources.icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return FromPng(ms.ToArray());
                }
            }
            catch { return null; }
        }

        private static ImageSource FromPng(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png, false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
