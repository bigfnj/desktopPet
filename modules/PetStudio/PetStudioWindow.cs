using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.ModuleKit;
using DesktopPet.Modules;
using DesktopPet.Tools.ShimejiConvert;
using DesktopPet.Tools.ShimejiConvert.Emit;
using DesktopPet.Tools.ShimejiConvert.Shimeji;

namespace DesktopPet.PetStudioModule
{
    /// <summary>
    /// The studio window. Built in code rather than XAML to match the host's own WPF panes, and because a
    /// module with a .xaml would need its own build/resource plumbing for one window.
    ///
    /// Layout is one split view: the pet's XML on the left (editable, and the source of truth for preview /
    /// install / save), and on the right a compact report, a colour-coded reachability map of every animation,
    /// and a detail panel that shows the selected animation's sprite frames and where it can go next. Nothing
    /// here decides anything — analysis is PetAnalyzer's job and every pet operation goes through IPetManager,
    /// so this file is layout plus wiring.
    /// </summary>
    internal sealed class PetStudioWindow : Window
    {
        private readonly IHost _host;
        private readonly IPetManager _pets;
        private readonly IModuleSettings _settings;
        // Built in the constructor, not here: it needs _host, and a field initializer runs BEFORE the
        // constructor body assigns it.
        private readonly PetStudioTheme _theme;

        // Top / bottom bars.
        private readonly TextBlock _path = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _installId = new TextBox { Width = 150, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _installButton = new Button { Content = "Install this pet…", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _previewButton = new Button { Content = "Preview on my desktop", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
        private readonly Button _removeButton = new Button { Content = "Remove preview", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        // Left: the editable XML, plus its actions.
        private readonly TextBox _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            IsInactiveSelectionHighlightEnabled = true,
        };
        private readonly Button _reanalyzeButton = new Button { Content = "Re-analyze", Padding = new Thickness(10, 3, 10, 3) };
        private readonly Button _saveButton = new Button { Content = "Save", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };

        // Right: report / map / detail.
        private readonly TextBlock _reportText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        // Shimeji import: a residue/"what didn't convert" readout, shown only after an import.
        private readonly TextBlock _importLossText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private UIElement _importLossSection;
        private const string LastSkinDirKey = "lastSkinDir";
        private string _extractedTemp;   // a .zip skin extracted here for the session; deleted on close
        private readonly WrapPanel _map = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _detailTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detailStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _framesInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly Image _frameImage = new Image { Stretch = Stretch.Uniform, Height = 96, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Button _playButton = new Button { Content = "▶ Play", Padding = new Thickness(8, 2, 8, 2), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _frameStrip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel _transitions = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        private Border _framePreviewBox;

        private readonly Dictionary<int, AnimNode> _nodesById = new Dictionary<int, AnimNode>();
        private readonly Dictionary<int, Border> _chipsById = new Dictionary<int, Border>();
        private Border _selectedChip;

        // Clickable legend filters: each colour category can be shown or hidden in the map.
        private FrameworkElement _swatchRoot, _swatchReachable, _swatchDead;
        private bool _showRoot = true, _showReachable = true, _showDead = true;

        private readonly DispatcherTimer _reanalyzeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        private bool _suppressReanalyze;

        private readonly DispatcherTimer _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        private readonly List<BitmapSource> _playFrames = new List<BitmapSource>();
        private int _playIndex;

        private string _openedPath;
        private PetSprite _sprite;
        private string _spriteKey;
        private IPetPreview _preview;

        internal PetStudioWindow(IHost host)
        {
            _host = host;
            _pets = host != null ? host.GetPetManager("petstudio") : null;
            _settings = host != null ? host.GetSettings("petstudio") : null;
            // Ask the host which theme it is presenting; it owns the light/dark/system preference.
            _theme = PetStudioTheme.Current(host);

            // Muted text (path / status / detail status) tracks the theme so it stays readable on dark chrome.
            _path.Foreground = _theme.Muted;
            _status.Foreground = _theme.Muted;
            _detailStatus.Foreground = _theme.Muted;
            _framesInfo.Foreground = _theme.Muted;

            Title = "Pet Studio";
            Width = 1240;
            Height = 720;
            MinWidth = 900;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _reanalyzeButton.Click += delegate { Analyze(); };
            _saveButton.Click += delegate { Save(); };
            _editor.TextChanged += delegate { OnEditorChanged(); };
            _reanalyzeTimer.Tick += delegate { _reanalyzeTimer.Stop(); Analyze(); };
            _playTimer.Tick += delegate { StepPlay(); };
            _playButton.Click += delegate { TogglePlay(); };

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };
            UIElement topBar = BuildTopBar();
            UIElement bottomBar = BuildBottomBar();
            DockPanel.SetDock(topBar, Dock.Top);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(topBar);
            root.Children.Add(bottomBar);
            root.Children.Add(BuildSplit());
            Content = root;
            _theme.Apply(this);   // paint to match the host; a theme change takes effect on the next open

            ResetDetail();
            if (_pets == null)
                SetStatus("No pet service available — Pet Studio needs the Pets permission.");
            else
                SetStatus("Open a pet's animations.xml to begin.");

            Closed += delegate { _playTimer.Stop(); _reanalyzeTimer.Stop(); RemovePreview(); CleanupExtracted(); };
        }

        // ---- layout ----

        private UIElement _topBar;
        private UIElement BuildTopBar()
        {
            if (_topBar != null) return _topBar;
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock { Text = "install as:", VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Muted });
            right.Children.Add(_installId);
            _installButton.Click += delegate { Install(); };
            right.Children.Add(_installButton);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var openButton = new Button { Content = "Open animations.xml…", Padding = new Thickness(10, 3, 10, 3) };
            openButton.Click += delegate { OpenFile(); };
            left.Children.Add(openButton);
            var importButton = new Button { Content = "Import skin folder…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            importButton.Click += delegate { ImportShimeji(); };
            left.Children.Add(importButton);
            var importZipButton = new Button { Content = "Import .zip…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            importZipButton.Click += delegate { ImportShimejiZip(); };
            left.Children.Add(importZipButton);
            left.Children.Add(new TextBlock { Text = "  ", Width = 8 });
            left.Children.Add(_path);
            bar.Children.Add(left);

            _topBar = bar;
            return bar;
        }

        private UIElement _bottomBar;
        private UIElement BuildBottomBar()
        {
            if (_bottomBar != null) return _bottomBar;
            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _previewButton.Click += delegate { Preview(); };
            _removeButton.Click += delegate { RemovePreview(); };
            buttons.Children.Add(_previewButton);
            buttons.Children.Add(_removeButton);
            DockPanel.SetDock(buttons, Dock.Left);
            bar.Children.Add(buttons);

            _status.Margin = new Thickness(16, 0, 0, 0);
            bar.Children.Add(_status);

            _bottomBar = bar;
            return bar;
        }

        private UIElement BuildSplit()
        {
            // Three resizable columns: the XML, the report + reachability map, and the selected animation.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 240 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });

            AddColumn(grid, 0, Section("Pet XML", BuildEditorPane()), 0);
            Grid.SetColumn(ColumnSplitter(grid, 1), 1);
            AddColumn(grid, 2, BuildReportMapColumn(), 10);
            Grid.SetColumn(ColumnSplitter(grid, 3), 3);
            AddColumn(grid, 4, Section("Selected animation", BuildDetail()), 10);

            return grid;
        }

        private static void AddColumn(Grid grid, int column, UIElement pane, double leftMargin)
        {
            var fe = pane as FrameworkElement;
            if (fe != null) fe.Margin = new Thickness(leftMargin, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            Grid.SetColumn(pane, column);
            grid.Children.Add(pane);
        }

        private GridSplitter ColumnSplitter(Grid grid, int column)
        {
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent };
            grid.Children.Add(splitter);
            return splitter;
        }

        private UIElement BuildReportMapColumn()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // report
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // import loss (import only)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // map

            _reportText.Text = "No pet loaded yet.";
            var report = Section("Report", _reportText);
            Grid.SetRow(report, 0);
            grid.Children.Add(report);

            var importLossScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200, Content = _importLossText };
            _importLossSection = Section("Import loss (what didn't convert)", importLossScroll);
            _importLossSection.Visibility = Visibility.Collapsed;
            Grid.SetRow(_importLossSection, 1);
            grid.Children.Add(_importLossSection);

            var mapArea = new DockPanel { LastChildFill = true };
            var legend = BuildLegend();
            DockPanel.SetDock(legend, Dock.Bottom);
            mapArea.Children.Add(legend);
            var mapScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _map };
            mapArea.Children.Add(mapScroll);
            var mapSection = Section("Reachability map", mapArea);
            Grid.SetRow(mapSection, 2);
            grid.Children.Add(mapSection);

            return grid;
        }

        private UIElement BuildEditorPane()
        {
            var dock = new DockPanel { LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(_reanalyzeButton);
            actions.Children.Add(_saveButton);
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);
            dock.Children.Add(_editor);
            return dock;
        }

        private UIElement BuildDetail()
        {
            var panel = new StackPanel();
            panel.Children.Add(_detailTitle);
            panel.Children.Add(_detailStatus);
            panel.Children.Add(_framesInfo);

            RenderOptions.SetBitmapScalingMode(_frameImage, BitmapScalingMode.NearestNeighbor);
            _framePreviewBox = new Border
            {
                Background = _theme.PreviewBg,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                Child = _frameImage,
            };
            var previewRow = new StackPanel { Orientation = Orientation.Horizontal };
            previewRow.Children.Add(_framePreviewBox);
            _playButton.Margin = new Thickness(8, 6, 0, 0);
            _playButton.VerticalAlignment = VerticalAlignment.Bottom;
            previewRow.Children.Add(_playButton);
            panel.Children.Add(previewRow);

            var stripScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 0),
                Content = _frameStrip,
            };
            panel.Children.Add(stripScroll);
            panel.Children.Add(_transitions);

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private UIElement BuildLegend()
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _swatchRoot = Swatch(_theme.RootFill, _theme.RootStroke, "root",
                "Entry animations the engine can start in directly (drag / fall / kill / sync, or a spawn). Click to show/hide.",
                () => ToggleFilter("root"));
            _swatchReachable = Swatch(_theme.LiveFill, _theme.LiveStroke, "reachable",
                "Reached by a transition from a root. Click to show/hide.", () => ToggleFilter("reachable"));
            _swatchDead = Swatch(_theme.DeadFill, _theme.DeadStroke, "never plays",
                "Unreachable — nothing leads here. Click to show/hide.", () => ToggleFilter("dead"));
            legend.Children.Add(_swatchRoot);
            legend.Children.Add(_swatchReachable);
            legend.Children.Add(_swatchDead);
            legend.Children.Add(new TextBlock { Text = "(click to filter)", Foreground = _theme.Muted, FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center });
            return legend;
        }

        private FrameworkElement Swatch(Brush fill, Brush stroke, string label, string tooltip, Action onClick)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 14, 0),
                Background = Brushes.Transparent,   // makes the whole row hit-testable, not just its children
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tooltip,
            };
            row.Children.Add(new Border { Width = 12, Height = 12, Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = " " + label, Foreground = _theme.Muted, VerticalAlignment = VerticalAlignment.Center });
            row.MouseLeftButtonUp += delegate { onClick(); };
            return row;
        }

        /// <summary>Toggle a colour category's visibility in the map, dimming its legend swatch when hidden.</summary>
        private void ToggleFilter(string category)
        {
            if (category == "root") _showRoot = !_showRoot;
            else if (category == "reachable") _showReachable = !_showReachable;
            else _showDead = !_showDead;

            if (_swatchRoot != null) _swatchRoot.Opacity = _showRoot ? 1.0 : 0.35;
            if (_swatchReachable != null) _swatchReachable.Opacity = _showReachable ? 1.0 : 0.35;
            if (_swatchDead != null) _swatchDead.Opacity = _showDead ? 1.0 : 0.35;

            ApplyMapFilter();
        }

        /// <summary>Show or hide each chip per the current category filters. Chips keep their identity, so a
        /// selection and its detail survive a filter change.</summary>
        private void ApplyMapFilter()
        {
            foreach (KeyValuePair<int, Border> kv in _chipsById)
            {
                AnimNode node;
                if (!_nodesById.TryGetValue(kv.Key, out node)) continue;
                bool show = !node.IsReachable ? _showDead : (node.IsRoot ? _showRoot : _showReachable);
                kv.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>A titled block: a bold caption over its content, filling the rest.</summary>
        private static UIElement Section(string title, UIElement content)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var caption = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(caption, Dock.Top);
            dock.Children.Add(caption);
            dock.Children.Add(content);
            return dock;
        }

        // ---- open / edit / save ----

        private void OpenFile()
        {
            try
            {
                // Own the dialog here rather than call host.PickFilesToOpen: Pet Studio is the one module
                // that already carries a UI framework, and only a self-owned dialog lets it set the starting
                // directory to the author's pet library (or wherever they last worked).
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Open a pet's animations.xml",
                    Filter = "Pet XML (*.xml)|*.xml|All files (*.*)|*.*",
                    CheckFileExists = true,
                    InitialDirectory = InitialOpenDir(),
                };
                if (dialog.ShowDialog(this) != true) return;

                string path = dialog.FileName;
                _openedPath = path;
                _path.Text = path;
                _installId.Text = SuggestId(path);
                _saveButton.IsEnabled = true;
                HideImportLoss();               // the loss readout belongs to an import, not an opened file
                RememberOpenDir(path);
                SetEditorText(File.ReadAllText(path));
                Analyze();
            }
            catch (Exception ex)
            {
                _reportText.Text = "";
                SetStatus("Couldn't read that file: " + ex.Message);
            }
        }

        /// <summary>Set the editor text without kicking off the debounced re-analyze (the caller analyzes).</summary>
        private void SetEditorText(string text)
        {
            _suppressReanalyze = true;
            try { _editor.Text = text ?? ""; }
            finally { _suppressReanalyze = false; }
        }

        private void OnEditorChanged()
        {
            if (_suppressReanalyze) return;
            _reanalyzeTimer.Stop();
            _reanalyzeTimer.Start();   // re-analyze once the typing settles
        }

        private void Save()
        {
            string path = _openedPath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save animations.xml",
                    Filter = "Pet XML (*.xml)|*.xml|All files (*.*)|*.*",
                    FileName = "animations.xml",
                    InitialDirectory = InitialOpenDir(),
                };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }
            try
            {
                // ModuleKit's durable write: temp file in the same directory, flushed through, then swapped
                // over the destination, so a crash mid-save can never truncate the author's pet.
                if (!AtomicFile.TryWriteAllText(path, _editor.Text ?? "", null))
                    throw new IOException("The file could not be written.");
                _openedPath = path;
                _path.Text = path;
                _saveButton.IsEnabled = true;
                RememberOpenDir(path);
                SetStatus("Saved to " + path);
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't save: " + ex.Message);
            }
        }

        /// <summary>Where the Open dialog should start: the folder last browsed to, else the pet library,
        /// else Documents. The policy itself lives in PetStudioPaths so the self-test can pin it.</summary>
        private string InitialOpenDir()
        {
            string saved = _settings != null ? _settings.Get(PetStudioPaths.LastOpenDirKey, "") : "";
            string pets = _pets != null ? _pets.PetsDirectory : "";
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return PetStudioPaths.ResolveInitialDir(saved, pets, docs, Directory.Exists);
        }

        /// <summary>Remember the folder this file came from, so the next Open defaults back to it.</summary>
        private void RememberOpenDir(string path)
        {
            if (_settings == null) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(dir)) return;
                _settings.Set(PetStudioPaths.LastOpenDirKey, dir);
                _settings.Save();
            }
            catch { }
        }

        // ---- import a Shimeji skin ----

        /// <summary>Public entry so the host (e.g. the Pets pane, or a catalog hand-off) can open Pet Studio
        /// straight into the Shimeji import flow.</summary>
        internal void BeginImport() { ImportShimeji(); }

        private void ImportShimeji()
        {
            string root;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a Shimeji skin folder (or a folder that contains skins)";
                string start = InitialSkinDir();
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.SelectedPath = start;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                root = dlg.SelectedPath;
            }
            ImportSkinFromRoot(root);
        }

        private void ImportShimejiZip()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a Shimeji skin .zip",
                Filter = "Shimeji skin zip (*.zip)|*.zip|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = InitialSkinDir(),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                RememberSkinDir(Path.GetDirectoryName(dlg.FileName));
                CleanupExtracted();
                _extractedTemp = Path.Combine(Path.GetTempPath(), "petstudio-shimeji-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_extractedTemp);
                ZipFile.ExtractToDirectory(dlg.FileName, _extractedTemp);
                ImportSkinFromRoot(_extractedTemp);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read that .zip: " + ex.Message);
            }
        }

        private void CleanupExtracted()
        {
            if (string.IsNullOrEmpty(_extractedTemp)) return;
            try { if (Directory.Exists(_extractedTemp)) Directory.Delete(_extractedTemp, true); } catch { }
            _extractedTemp = null;
        }

        /// <summary>Convert the first skin under <paramref name="root"/> into the editor. Shared by the folder
        /// dialog and (later) a catalog hand-off that downloads a raw skin to a temp folder.</summary>
        internal void ImportSkinFromRoot(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { SetStatus("No such folder."); return; }
                RememberSkinDir(root);

                // Android JSON+WebP bundle (manifest.json + animation.json + sprites/*.webp)? Convert that path.
                // The bundle can sit one level down inside a zip, so search for it before the classic layout.
                string bundleRoot = FindBundleRoot(root);
                if (bundleRoot != null)
                {
                    string bundleName = ReadBundleName(bundleRoot);
                    string bundleError;
                    ConversionResult bundleResult = BundleConverter.ConvertBundle(bundleRoot, bundleName, out bundleError);
                    if (bundleResult == null) { HideImportLoss(); SetStatus("Bundle conversion failed: " + bundleError); return; }
                    LoadConvertedIntoEditor(bundleResult, string.IsNullOrWhiteSpace(bundleName) ? "Shimeji" : bundleName.Trim(), "");
                    return;
                }

                string note;
                var skins = SkinLayout.Detect(root, out note);
                if (skins == null || skins.Count == 0)
                {
                    HideImportLoss();
                    SetStatus("No convertible Shimeji skin found here." + (string.IsNullOrEmpty(note) ? "" : " " + note));
                    return;
                }

                DetectedSkin skin = skins[0];
                string extra = skins.Count > 1
                    ? " (found " + skins.Count + " skins; converted the first, '" + skin.Name + "')"
                    : "";

                string error;
                ConversionResult result = ShimejiEngine.ConvertSkin(skin.ConfDir, skin.ImgDir, skin.Name, out error);
                if (result == null)
                {
                    HideImportLoss();
                    SetStatus("Conversion failed: " + error);
                    return;
                }
                LoadConvertedIntoEditor(result, skin.Name, extra);
            }
            catch (Exception ex)
            {
                SetStatus("Import failed: " + ex.Message);
            }
        }

        /// <summary>Put a freshly converted skin (desktop or Android bundle) into the editor, analysis, and
        /// import-loss panel, and report acceptance. Shared by both import paths.</summary>
        private void LoadConvertedIntoEditor(ConversionResult result, string name, string extra)
        {
            _openedPath = null;                     // imported, not opened from a file: Save will prompt for a path
            _path.Text = "Imported: " + name;
            _installId.Text = SafeId(name);
            _saveButton.IsEnabled = true;
            SetEditorText(result.EmittedXml);
            Analyze();
            ShowImportLoss(result, name);
            SetStatus((result.Accepted
                ? "Imported '" + name + "'. Preview or install."
                : "Imported '" + name + "', but the host would reject it.") + extra);
        }

        /// <summary>Find the Android Shimeji bundle (manifest.json + animation.json) at or under
        /// <paramref name="root"/>, so a zip that wraps the bundle one level down still resolves. Null if none.</summary>
        private static string FindBundleRoot(string root)
        {
            try
            {
                if (BundleConverter.IsBundle(root)) return root;
                foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    if (BundleConverter.IsBundle(dir)) return dir;
            }
            catch { }
            return null;
        }

        /// <summary>Read the display name from an Android bundle's manifest.json, or null.</summary>
        private static string ReadBundleName(string bundleRoot)
        {
            try
            {
                BundleInfo info;
                BundleParser.Parse(bundleRoot, out info);
                return info != null ? info.Name : null;
            }
            catch { return null; }
        }

        private void ShowImportLoss(ConversionResult result, string skinName)
        {
            if (result == null || result.Residue == null) { HideImportLoss(); return; }
            _importLossText.Text = result.Residue.ToText(skinName);
            if (_importLossSection != null) _importLossSection.Visibility = Visibility.Visible;
        }

        private void HideImportLoss()
        {
            _importLossText.Text = "";
            if (_importLossSection != null) _importLossSection.Visibility = Visibility.Collapsed;
        }

        private string InitialSkinDir()
        {
            string saved = _settings != null ? _settings.Get(LastSkinDirKey, "") : "";
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved)) return saved;
            return InitialOpenDir();
        }

        private void RememberSkinDir(string dir)
        {
            if (_settings == null || string.IsNullOrWhiteSpace(dir)) return;
            try { _settings.Set(LastSkinDirKey, dir); _settings.Save(); }
            catch { }
        }

        private static string SafeId(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string id = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(id) ? "shimeji-pet" : id;
        }

        // ---- analysis + rendering ----

        private void Analyze()
        {
            _reanalyzeTimer.Stop();
            string xml = _editor.Text ?? "";
            PetReport report = PetAnalyzer.Analyze(xml);

            // Decode the sprite sheet only when the <image> actually changed. Editing re-analyzes every ~750ms,
            // and the sheet decode is by far the window's largest allocation, so re-decoding it on every
            // keystroke-settle would spike memory continuously for an image the edit never touched.
            string key = SpriteKey(report);
            if (!string.Equals(key, _spriteKey, StringComparison.Ordinal))
            {
                _sprite = PetSprite.TryDecode(report.SpritePngBase64, report.TilesX, report.TilesY, report.TransparencyColor);
                _spriteKey = key;
            }

            RenderReport(report);
            RenderMap(report);
            ResetDetail();

            // Preview and install are gated on the host ACCEPTING the pet, not on it being warning-free: an
            // unreachable animation is worth telling the author about, but it does not stop the pet running.
            _previewButton.IsEnabled = report.IsValid && _pets != null;
            _installButton.IsEnabled = report.IsValid && _pets != null;

            SetStatus(report.IsValid
                ? (report.UnreachableAnimations.Count == 0
                    ? "This pet is good to go."
                    : "This pet runs, but " + report.UnreachableAnimations.Count + " animation(s) will never play.")
                : "The host would reject this pet.");
        }

        /// <summary>A cheap fingerprint of the sprite inputs — tiles, transparency, and the base64 length plus
        /// its head/tail — so a re-analyze can tell whether the sheet changed without comparing megabytes.</summary>
        private static string SpriteKey(PetReport r)
        {
            string b = r.SpritePngBase64 ?? "";
            string ends = b.Length > 64 ? b.Substring(0, 32) + b.Substring(b.Length - 32) : b;
            return r.TilesX + "x" + r.TilesY + "|" + r.TransparencyColor + "|" + b.Length + "|" + ends;
        }

        private void RenderReport(PetReport report)
        {
            if (!report.IsValid)
            {
                _reportText.Text = "REJECTED — this pet would not load:\n" + report.Error;
                return;
            }
            string who = "Valid pet" +
                (report.PetName.Length > 0 ? " — " + report.PetName : "") +
                (report.Author.Length > 0 ? " by " + report.Author : "");
            _reportText.Text = who + "\n" +
                report.AnimationCount + " animations · " + report.SpawnCount + " spawns · " +
                report.ChildCount + " children · " + report.UnreachableAnimations.Count + " never play";
        }

        private void RenderMap(PetReport report)
        {
            _map.Children.Clear();
            _nodesById.Clear();
            _chipsById.Clear();
            _selectedChip = null;
            foreach (AnimNode node in report.Nodes)
            {
                _nodesById[node.Id] = node;
                Border chip = MakeChip(node);
                _chipsById[node.Id] = chip;
                _map.Children.Add(chip);
            }
            ApplyMapFilter();   // honour any active legend filters for the newly built chips
        }

        private Border MakeChip(AnimNode node)
        {
            Brush fill, stroke;
            if (!node.IsReachable) { fill = _theme.DeadFill; stroke = _theme.DeadStroke; }
            else if (node.IsRoot) { fill = _theme.RootFill; stroke = _theme.RootStroke; }
            else { fill = _theme.LiveFill; stroke = _theme.LiveStroke; }

            string label = "#" + node.Id;
            if (!string.IsNullOrEmpty(node.Name))
                label += " " + (node.Name.Length > 14 ? node.Name.Substring(0, 13) + "…" : node.Name);

            var chip = new Border
            {
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = stroke,   // remembered so the selection highlight can be undone
                ToolTip = "#" + node.Id + (string.IsNullOrEmpty(node.Name) ? "" : " " + node.Name),
                Child = new TextBlock { Text = label, Foreground = _theme.ChipText },
            };
            chip.MouseLeftButtonUp += delegate { SelectNode(node.Id); };
            return chip;
        }

        // ---- selection detail ----

        private void SelectNode(int id)
        {
            AnimNode node;
            if (!_nodesById.TryGetValue(id, out node)) return;

            HighlightChip(id);

            _detailTitle.Text = "#" + node.Id + (string.IsNullOrEmpty(node.Name) ? "" : "  \"" + node.Name + "\"");

            string status;
            if (!node.IsReachable)
            {
                // Explain WHY it never plays — the common case is an animation that is fully built (has frames
                // and its own exits) but that nothing transitions INTO, i.e. it was authored but never wired up.
                status = "Never played — no transition, spawn, or entry point (drag/fall/kill/sync) leads into it.";
                if (node.Frames.Length > 0 || node.Edges.Count > 0)
                    status += " It has " + node.Frames.Length + " frame(s) and " + node.Edges.Count +
                        " exit(s), so it looks complete but was never hooked up.";
            }
            else
            {
                status = (node.IsRoot ? "Entry animation (the engine can start here). " : "") + "Reachable.";
            }
            if (!string.IsNullOrEmpty(node.Action) && node.Action != "none") status += "  Action: " + node.Action + ".";
            _detailStatus.Text = status;

            RenderFrames(node);
            RenderTransitions(node);
        }

        private void HighlightChip(int id)
        {
            if (_selectedChip != null)
            {
                _selectedChip.BorderBrush = (Brush)_selectedChip.Tag;
                _selectedChip.BorderThickness = new Thickness(1);
            }
            Border chip;
            if (_chipsById.TryGetValue(id, out chip))
            {
                chip.BorderBrush = _theme.Text;
                chip.BorderThickness = new Thickness(2);
                chip.BringIntoView();
                _selectedChip = chip;
            }
        }

        private void RenderFrames(AnimNode node)
        {
            StopPlay();
            _playFrames.Clear();
            _frameStrip.Children.Clear();
            _playIndex = 0;

            bool anyDecoded = false, allBlank = true;
            if (_sprite != null && node.Frames != null)
                foreach (int frameIndex in node.Frames)
                {
                    BitmapSource bmp = _sprite.Frame(frameIndex);
                    if (bmp == null) continue;
                    anyDecoded = true;
                    if (!_sprite.IsBlank(frameIndex)) allBlank = false;
                    _playFrames.Add(bmp);
                    _frameStrip.Children.Add(MakeStripThumb(bmp, _playFrames.Count - 1));
                }

            // Say which tiles play, and — the answer to "why does this show nothing?" — flag a frame that is a
            // fully transparent tile, which the sheet uses to make the pet invisible during a state.
            if (node.Frames == null || node.Frames.Length == 0)
                _framesInfo.Text = "No sprite frames — this animation draws nothing on screen.";
            else
            {
                string list = string.Join(", ", System.Linq.Enumerable.Take(node.Frames, 24));
                if (node.Frames.Length > 24) list += ", …";
                _framesInfo.Text = "Frames: " + list +
                    (anyDecoded && allBlank ? "  — a blank (transparent) tile: the pet is invisible here, so nothing shows." : "");
            }

            if (_playFrames.Count > 0)
            {
                _frameImage.Source = _playFrames[0];
                _framePreviewBox.Visibility = Visibility.Visible;
                _playButton.IsEnabled = _playFrames.Count > 1;
            }
            else
            {
                _frameImage.Source = null;
                _framePreviewBox.Visibility = Visibility.Collapsed;
                _playButton.IsEnabled = false;
            }
        }

        private UIElement MakeStripThumb(BitmapSource bmp, int frameSlot)
        {
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Height = 44 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            var border = new Border
            {
                Background = _theme.PreviewBg,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = img,
            };
            border.MouseLeftButtonUp += delegate { StopPlay(); _playIndex = frameSlot; _frameImage.Source = bmp; };
            return border;
        }

        private void RenderTransitions(AnimNode node)
        {
            _transitions.Children.Clear();
            if (node.Edges.Count == 0)
            {
                _transitions.Children.Add(new TextBlock { Text = "No outgoing transitions.", Foreground = _theme.Muted });
                return;
            }
            _transitions.Children.Add(new TextBlock { Text = "Goes to:", Foreground = _theme.Muted, Margin = new Thickness(0, 0, 0, 2) });
            foreach (AnimEdge edge in node.Edges)
                _transitions.Children.Add(MakeTransitionRow(edge));
        }

        private UIElement MakeTransitionRow(AnimEdge edge)
        {
            AnimNode target;
            bool known = _nodesById.TryGetValue(edge.To, out target);
            string name = known && !string.IsNullOrEmpty(target.Name) ? " " + target.Name : "";
            string kind = edge.Kind == "sequence" ? "" : "  [" + edge.Kind + "]";
            string prob = edge.Probability <= 0 ? "  never" : "  " + edge.Probability + "%";
            string text = "→ #" + edge.To + name + prob + kind;

            var row = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = edge.Probability <= 0 ? _theme.Muted : _theme.Text,
                Margin = new Thickness(8, 1, 0, 1),
            };
            if (known)
            {
                row.Cursor = System.Windows.Input.Cursors.Hand;
                int to = edge.To;
                row.MouseLeftButtonUp += delegate { SelectNode(to); };
            }
            return row;
        }

        private void ResetDetail()
        {
            StopPlay();
            _selectedChip = null;
            _detailTitle.Text = "Nothing selected";
            _detailStatus.Text = "Click a node in the map to inspect it.";
            _framesInfo.Text = "";
            _frameStrip.Children.Clear();
            _transitions.Children.Clear();
            _frameImage.Source = null;
            _framePreviewBox.Visibility = Visibility.Collapsed;
            _playButton.IsEnabled = false;
        }

        // ---- frame playback ----

        private void TogglePlay()
        {
            if (_playTimer.IsEnabled) StopPlay();
            else if (_playFrames.Count > 1) { _playTimer.Start(); _playButton.Content = "⏸ Stop"; }
        }

        private void StepPlay()
        {
            if (_playFrames.Count == 0) { StopPlay(); return; }
            _playIndex = (_playIndex + 1) % _playFrames.Count;
            _frameImage.Source = _playFrames[_playIndex];
        }

        private void StopPlay()
        {
            _playTimer.Stop();
            _playButton.Content = "▶ Play";
        }

        // ---- preview / install ----

        private void Preview()
        {
            RemovePreview();
            string error;
            _preview = _pets.SpawnPreview(_editor.Text ?? "", out error);
            if (_preview == null)
            {
                SetStatus("Preview refused: " + error);
                return;
            }
            _removeButton.IsEnabled = true;
            SetStatus("Previewing on your desktop. It is temporary: not saved, not in your pet mix, and gone " +
                "when you close this window.");
        }

        private void RemovePreview()
        {
            IPetPreview preview = _preview;
            _preview = null;
            _removeButton.IsEnabled = false;
            if (preview == null) return;
            try { preview.Remove(); } catch { }
        }

        private void Install()
        {
            string id = (_installId.Text ?? "").Trim();
            if (id.Length == 0)
            {
                SetStatus("Give the pet an id to install it under (letters, digits, - and _).");
                return;
            }
            string error;
            if (_pets.InstallType(id, _editor.Text ?? "", out error))
            {
                SetStatus("Installed as '" + id + "'. It is now in Options, Pets.");
                return;
            }
            SetStatus("Couldn't install: " + error);
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? "";
        }

        /// <summary>A sensible default install id: the pet folder's name, which is how pets are laid out on
        /// disk (…\pets\&lt;id&gt;\animations.xml).</summary>
        private static string SuggestId(string path)
        {
            try
            {
                string folder = Path.GetFileName(Path.GetDirectoryName(path));
                return string.IsNullOrWhiteSpace(folder) ? "" : folder;
            }
            catch { return ""; }
        }
    }
}
