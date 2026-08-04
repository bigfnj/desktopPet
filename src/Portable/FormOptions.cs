using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.Ai;
using Newtonsoft.Json.Linq;

namespace DesktopPet
{
    /// <summary>
    /// Application options. Need a redesign, so it is not documented.
    /// </summary>
    /// <preliminary/>
    public partial class FormOptions : Form
    {
        // Speech tab controls (Phase 1) — backed by Properties.Settings (SpeechEnabled/SpeechDuration).
        private CheckBox      _chkSpeech;
        private NumericUpDown _prefDuration;

        // AI tab controls (built programmatically in BuildAiTab so the Designer stays untouched).
        // Edits update _ai in memory; the file is saved and applied live to the running pet when
        // the dialog closes (FormOptions_ApplyAi -> StartUp.ReloadAiSettings).
        private AiSettings    _ai;
        private TextBox       _aiPetName;
        private TextBox       _aiUserName;
        private ComboBox      _aiPersona;
        private TextBox       _aiPersonality;
        private ComboBox      _aiSpeech;
        private bool          _personaSyncGuard;   // suppress preset<->blurb echo
        private CheckBox      _aiMemory;
        private CheckBox      _aiBrainEnabled;
        private ComboBox      _aiProvider;
        private TextBox       _aiEndpoint;
        private Label         _aiEndpointStatus;
        private Label         _aiApiKeyLabel;
        private TextBox       _aiApiKey;
        private Label         _aiApiKeyStatus;
        private string        _aiApiKeyAdmissionError = "";
        private bool          _updatingAiApiKeyUi;
        private CheckBox      _aiCloudConsent;
        private Label         _aiCloudDisclosure;
        private Button        _aiRefreshModelsBtn;
        private Button        _aiClearHistoryBtn;
        private Label         _aiHistoryStatus;

        // Fortunes tab controls (built in BuildFortunesTab).
        private CheckBox        _fSmart;
        private Label           _fSmartStatus;
        private System.Windows.Forms.Timer _fSmartTimer;
        private Button          _fRebuildBtn;
        private CheckBox        _fSpicy;
        private ComboBox        _fTier;
        private CheckBox        _fSpicyOnly;
        private CheckBox        _fNoProfanity;
        private TreeView        _fSourcesTree;
        private TextBox         _fSourceFilter;   // live filter for the Sources tree
        private Label           _fSourceCount;    // "N of M sources - L lines"
        private bool            _treeSyncGuard;   // suppresses AfterCheck cascade during bulk updates
        private Label           _petStatus;       // Pets tab gallery status (built in BuildPetGallery)
        private Button          _petOnlineButton; // "Check for pets online"
        private RemoteCatalog   _catalog;         // last successfully fetched runtime catalog (pets + packs)
        private CancellationTokenSource _catalogCancellation;
        private CheckBox        _prefRunAtStartup;
        private NumericUpDown   _prefVolume;
        private NumericUpDown   _prefAutoStart;
        private NumericUpDown   _prefScale;
        private CheckBox        _prefRandomDrop;
        private NumericUpDown   _prefDropMinutes;
        private NumericUpDown   _prefDropJitter;
        private CheckedListBox  _fGenres;
        private Label           _fStatus;
        private Button          _fAddFortunesButton;
        private Label           _fImportStatus;   // feedback for the "Add your own fortunes" section
        private Label           _fPacksStatus;
        private Button          _fPacksOnlineButton;     // "Check online for packs"
        private Button          _fPacksDownloadButton;   // "Download checked"
        private TreeView        _fPacksTree;             // grouped downloadable packs (collection -> per-source)
        private ComboBox      _aiTextModel;
        private ComboBox      _aiVisionModel;
        private Label         _aiVisionCapWarning;
        private CheckBox      _aiUseVision;
        private Button        _aiTestBtn;
        private Label         _aiTestStatus;
        private CheckBox      _aiHotkeyEnabled;
        private TextBox       _aiHotkey;
        private Label         _aiHotkeyStatus;
        private CheckBox      _aiIdleEnabled;
        private NumericUpDown _aiIdleMin;
        private NumericUpDown _aiIdleMax;
        private CheckBox      _aiAutoStart;
        private CheckBox      _aiWarmUp;

        private readonly object _asyncOperationsLock = new object();
        private readonly HashSet<Task> _asyncOperations = new HashSet<Task>();
        private readonly CancellationTokenSource _lifetimeCancellation =
            new CancellationTokenSource();
        private readonly SemaphoreSlim _fortuneDirectoryOperationGate =
            new SemaphoreSlim(1, 1);
        private CancellationTokenSource _fortuneImportCancellation;
        private Task _fortuneImportTask = Task.FromResult(0);
        private CancellationTokenSource _modelRefreshCancellation;
        private CancellationTokenSource _aiTestCancellation;
        private bool _isClosing;
        private bool _resourceChurnDiagnostic;
        private const int MaximumModelListBytes = 2 * 1024 * 1024;
        private const int MaximumListedModels = 512;
        private const int MaximumReturnedModelRecords = 4096;
        private const int UiSettingsSaveTimeoutMilliseconds = 250;

            /// <summary>
            /// Constructor
            /// </summary>
        public FormOptions()
        {
            InitializeComponent();
            FormClosing += FormOptions_ApplyAi;
        }

            /// <summary>
            /// Restore default animation. Will restore the animation delivered with the app.
            /// </summary>
            /// <param name="sender">Caller object.</param>
            /// <param name="e">Click event values.</param>
        private void button1_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Retry;
            Close();
        }
        
            /// <summary>
            /// The legacy browser callback is retained for designer compatibility. It now just
            /// rebuilds the local pet gallery so any stale WebBrowser content is replaced.
            /// </summary>
        private void tabControl1_DrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            TabPage tabPage = tabControl1.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool dark = WindowTheme.IsDark();
            Color tabFill = dark
                ? (selected ? WindowTheme.Surface : WindowTheme.Bg)
                : (selected ? Color.White : Color.LightGray);
            using (var tabBack = new SolidBrush(tabFill))
                g.FillRectangle(tabBack, e.Bounds);

            using (var textBrush = new SolidBrush(dark ? WindowTheme.Text : Color.Black))
            using (var tabFont = new Font(
                tabPage.Font.FontFamily,
                selected ? 11.0f : 10.0f,
                selected ? FontStyle.Bold : FontStyle.Regular,
                GraphicsUnit.Pixel))
            using (var stringFlags = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                g.DrawString(
                    tabPage.Text,
                    tabFont,
                    textBrush,
                    tabControl1.GetTabRect(e.Index),
                    stringFlags);
            }
        }

        private void FormOptions_Load(object sender, EventArgs e)
        {
                // Set up audio values
            checkBox1.Checked = (Program.MyData.GetVolume() > 0.0);
			trackBar1.Value = (int)(Program.MyData.GetVolume() * 10);
            trackBar1.Enabled = checkBox1.Checked;
            string audioError = TSound.CurrentErrorMessage();
            label2.Text = AudioStatusText(audioError, trackBar1.Value);
            if (!string.IsNullOrWhiteSpace(audioError))
            {
                trackBar1.Enabled = false;
                checkBox1.Enabled = false;
            }
			checkBox2.Checked = Program.MyData.GetWindowForeground();
            checkBox4.Checked = Program.MyData.GetStealTaskbarFocus();
            trackBar2.Value = Program.MyData.GetAutoStartPets();
            trackBar3.Tag = Program.MyData.GetScale();
            trackBar3.Value = Program.MyData.GetScale();
            label5.Text = trackBar2.Value.ToString();
            RefreshScaleStatus();
            checkBox3.Checked = Program.MyData.GetMultiscreen();

            flowLayoutPanel2.Visible = false;

            _ai = AiSettings.Load();
            BuildPreferencesTab();   // also hosts the Speech settings (Speech tab removed)
            BuildFortunesTab();
            BuildAiTab();
        }

        private static string AudioStatusText(string audioError, int volumeLevel)
        {
            return string.IsNullOrWhiteSpace(audioError)
                ? volumeLevel.ToString()
                : audioError.Trim();
        }

        private void RefreshScaleStatus()
        {
            int level = _prefScale != null ? (int)_prefScale.Value : trackBar3.Value;
            int requestedFactor = ScalePolicy.FactorFromLevel(level);
            int effectiveFactor = requestedFactor;
            StartUp main = Program.Mainthread;
            Animations activeAnimations = main == null ? null : main.GetAnimations();
            if (activeAnimations != null)
                effectiveFactor = activeAnimations.ScaleFactor;
            label9.Text = ScalePolicy.StatusText(level, effectiveFactor);
        }

        internal static bool RunAudioStatusSelfTest(StringBuilder output)
        {
            bool ok =
                string.Equals(AudioStatusText("", 7), "7", StringComparison.Ordinal) &&
                string.Equals(
                    AudioStatusText("  Audio decoder unavailable.  ", 7),
                    "Audio decoder unavailable.",
                    StringComparison.Ordinal);
            output.AppendLine("audio_status_message=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private void FormOptions_Shown(object sender, EventArgs e)
        {
            BuildPetGallery();
            WindowTheme.Apply(this);   // system-following dark title bar + dark control colours
            // The tab-strip background is darkened by DarkTabControl (WM_ERASEBKGND); the owner-drawn
            // tab_control1_DrawItem paints the tab buttons on top.
        }

        // ---- Pets gallery (local/offline + online catalog downloads) ----

        private sealed class PetGalleryItem
        {
            public string Id;         // folder / catalog id; null for the built-in default
            public string DisplayName;
            public string Author;
            public string IconPath;   // null for the built-in default or a freshly downloaded pet
            public string XmlPath;    // null for the built-in default
            public bool IsBuiltIn;
        }

        /// <summary>
        /// Populate the Pets tab with a local gallery: the built-in default plus every pet bundled
        /// beside the executable (portable zip). Each choice is validated before it is applied, so a
        /// bundled file is never trusted blindly. Absent bundled content simply yields the default only.
        /// </summary>
        private void BuildPetGallery()
        {
            tabPage1.Text = "Pets";
            FlowLayoutPanel panel = flowLayoutPanel1;
            while (panel.Controls.Count > 0)
            {
                Control control = panel.Controls[0];
                panel.Controls.RemoveAt(0);
                DisposeOwnedImages(control);
                control.Dispose();
            }

            panel.FlowDirection = FlowDirection.TopDown;
            panel.WrapContents = false;
            panel.AutoScroll = true;
            panel.Padding = new Padding(12);

            panel.Controls.Add(new Label { AutoSize = true, Text = "Pets", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 8),
                Text = "Pick a look for your pet. These ship with the app and work fully offline. " +
                       "Choosing one replaces the current pet right away."
            });

            _petStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(0, 120, 0), MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 8) };
            panel.Controls.Add(_petStatus);

            foreach (PetGalleryItem item in EnumerateLocalPets())
                panel.Controls.Add(BuildPetCard(item));

            // Online catalog: fetched on demand, then offers any pet not already present locally.
            panel.Controls.Add(new Label { AutoSize = true, Text = "Get more pets", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 4),
                Text = "Check the project's online catalog for more pets. Each download is verified against " +
                       "a published checksum and validated before it is added to your pets.",
            });
            _petOnlineButton = new Button { Text = "Check for pets online", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            _petOnlineButton.Click += CheckOnlinePets_Click;
            panel.Controls.Add(_petOnlineButton);

            if (_catalog != null)
            {
                HashSet<string> localIds = LocalPetIds();
                var downloadable = new List<CatalogPet>();
                foreach (CatalogPet pet in _catalog.Pets)
                    if (!localIds.Contains(pet.Id)) downloadable.Add(pet);
                if (downloadable.Count == 0)
                    panel.Controls.Add(new Label { AutoSize = true, ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 8), Text = "You already have every pet in the catalog." });
                else
                {
                    // A wrapping grid of preview tiles (three across) instead of a single tall column.
                    var grid = new FlowLayoutPanel
                    {
                        FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        MinimumSize = new Size(PetGridWidth, 0), MaximumSize = new Size(PetGridWidth, 0),
                        Margin = new Padding(0, 0, 0, 8),
                    };
                    foreach (CatalogPet pet in downloadable)
                        grid.Controls.Add(BuildDownloadablePetCard(pet));
                    panel.Controls.Add(grid);
                }
            }

            // Trailing spacer so AutoScroll can fully reveal the last card at small window sizes.
            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });
            flowLayoutPanel2.Visible = false;
            WindowTheme.ThemeControlTree(panel);   // re-theme after any rebuild (dark mode only)
        }

        private Control BuildPetCard(PetGalleryItem item)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 8),
            };
            var pic = new PictureBox { Width = 48, Height = 48, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 8, 0) };
            Image thumbnail = LoadPetThumbnail(item);
            if (thumbnail != null) pic.Image = thumbnail;
            row.Controls.Add(pic);

            var stack = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 8, 0),
            };
            stack.Controls.Add(new Label { AutoSize = true, Text = item.DisplayName, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0) });
            if (!string.IsNullOrWhiteSpace(item.Author))
                stack.Controls.Add(new Label { AutoSize = true, Text = "by " + item.Author, ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 2, 0, 0) });
            row.Controls.Add(stack);

            var apply = new Button { Text = item.IsBuiltIn ? "Use default" : "Use this pet", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            apply.Click += delegate { ApplyPet(item); };
            row.Controls.Add(apply);
            return row;
        }

        private static List<PetGalleryItem> EnumerateLocalPets()
        {
            var items = new List<PetGalleryItem>
            {
                new PetGalleryItem { DisplayName = "eSheep (default)", Author = "Adriano", IsBuiltIn = true }
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPetsFrom(AppPaths.BundledPetsDirectory, items, seen);   // read-only, beside the exe
            AddPetsFrom(AppPaths.LibraryPetsDirectory, items, seen);   // writable, downloaded pets
            return items;
        }

        private static void AddPetsFrom(string root, List<PetGalleryItem> items, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            Dictionary<string, string> authors = LoadBundledPetAuthors(root);
            var directories = new List<string>();
            try { directories.AddRange(Directory.EnumerateDirectories(root)); }
            catch { return; }
            directories.Sort(StringComparer.OrdinalIgnoreCase);

            const int maxPets = 256;
            foreach (string directory in directories)
            {
                if (items.Count > maxPets) break;
                string folder = Path.GetFileName(directory);
                if (!SecureDownload.IsSafeId(folder) || !seen.Add(folder)) continue;
                string xmlPath = Path.Combine(directory, "animations.xml");
                if (!File.Exists(xmlPath)) continue;
                string iconPath = Path.Combine(directory, "icon.png");
                string author;
                authors.TryGetValue(folder, out author);
                items.Add(new PetGalleryItem
                {
                    Id = folder,
                    DisplayName = PrettyPetName(folder),
                    Author = author,
                    IconPath = File.Exists(iconPath) ? iconPath : null,
                    XmlPath = xmlPath,
                });
            }
        }

        private static Dictionary<string, string> LoadBundledPetAuthors(string root)
        {
            var authors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string manifest = Path.Combine(root, "pets.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 64 * 1024)
                    return authors;
                Newtonsoft.Json.Linq.JObject parsed =
                    Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifest));
                var array = parsed["pets"] as Newtonsoft.Json.Linq.JArray;
                if (array == null) return authors;
                foreach (Newtonsoft.Json.Linq.JToken token in array)
                {
                    string folder = ((string)token["folder"] ?? "").Trim();
                    string author = ((string)token["author"] ?? "").Trim();
                    if (folder.Length > 0 && author.Length > 0 && author.Length <= 128)
                        authors[folder] = author;
                }
            }
            catch { }
            return authors;
        }

        private static string PrettyPetName(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Pet";
            string spaced = folder.Replace('_', ' ').Replace('-', ' ');
            string[] words = spaced.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (string word in words)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1) builder.Append(word.Substring(1));
            }
            return builder.Length > 0 ? builder.ToString() : "Pet";
        }

        private static Image LoadPetThumbnail(PetGalleryItem item)
        {
            try
            {
                if (item.IsBuiltIn) return new Bitmap(Properties.Resources.esheep);
                if (string.IsNullOrEmpty(item.IconPath) || !File.Exists(item.IconPath)) return null;
                long length = new FileInfo(item.IconPath).Length;
                if (length < 1 || length > PetXmlValidator.MaximumIconBytes) return null;
                byte[] bytes = File.ReadAllBytes(item.IconPath);
                using (var stream = new MemoryStream(bytes, false))
                using (var decoded = Image.FromStream(stream, false, true))
                    return new Bitmap(decoded);
            }
            catch { return null; }
        }

        private void ApplyPet(PetGalleryItem item)
        {
            if (item == null) return;
            try
            {
                if (Program.Mainthread == null)
                {
                    SetPetStatus("The pet host is not running.", true);
                    return;
                }

                string xml;
                if (item.IsBuiltIn)
                {
                    xml = Properties.Resources.animations;
                }
                else
                {
                    if (string.IsNullOrEmpty(item.XmlPath) || !File.Exists(item.XmlPath))
                    {
                        SetPetStatus("That pet's file is missing.", true);
                        return;
                    }
                    long length = new FileInfo(item.XmlPath).Length;
                    if (length < 1 || length > PetXmlValidator.MaximumXmlBytes)
                    {
                        SetPetStatus("That pet's file is too large.", true);
                        return;
                    }
                    xml = File.ReadAllText(item.XmlPath);
                }

                // Validate here for a precise message; LoadNewXMLFromString validates again before it
                // swaps the running pet, so an invalid file can never actually take effect.
                XmlData.RootNode parsed;
                string validationError;
                if (!PetXmlValidator.TryParse(xml, out parsed, out validationError))
                {
                    SetPetStatus(item.DisplayName + " failed validation: " + Short(validationError), true);
                    return;
                }

                if (Program.Mainthread.LoadNewXMLFromString(xml))
                    SetPetStatus("Now showing " + item.DisplayName + ".", false);
                else
                    SetPetStatus("Could not switch to " + item.DisplayName + ".", true);
            }
            catch (Exception ex)
            {
                SetPetStatus("Could not apply that pet: " + Short(ex.Message), true);
            }
        }

        private void SetPetStatus(string text, bool isError)
        {
            if (_petStatus == null) return;
            _petStatus.ForeColor = isError ? Color.Firebrick : Color.FromArgb(0, 120, 0);
            _petStatus.Text = text ?? "";
        }

        // ---- Online pets (runtime catalog) ----------------------------------

        private HashSet<string> LocalPetIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PetGalleryItem item in EnumerateLocalPets())
                if (!item.IsBuiltIn && !string.IsNullOrEmpty(item.Id)) ids.Add(item.Id);
            return ids;
        }

        // Grid geometry for the "Get more pets" tiles: a fixed tile width lets the wrapping panel
        // (locked to PetGridWidth) settle at exactly three tiles per row.
        private const int PetTileWidth = 100;
        private const int PetThumbSize = 72;
        private const int PetGridWidth = 3 * (PetTileWidth + 8) + 6;   // tile + L/R margin, plus slack

        private Control BuildDownloadablePetCard(CatalogPet pet)
        {
            // Width is pinned (Min == Max) so AutoSize only grows the height; a plain AutoSize tile
            // would shrink to its content and break the three-across wrap.
            var tile = new TableLayoutPanel
            {
                ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(PetTileWidth, 0), MaximumSize = new Size(PetTileWidth, 0),
                Margin = new Padding(4), Padding = new Padding(2, 6, 2, 8),
            };

            var pic = new PictureBox
            {
                Width = PetThumbSize, Height = PetThumbSize, SizeMode = PictureBoxSizeMode.Zoom,
                Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 4),
            };
            Image thumb = PetThumbnails.Get(pet.Id);
            if (thumb != null) pic.Image = thumb;
            tile.Controls.Add(pic);

            tile.Controls.Add(new Label
            {
                Text = pet.Name, Font = new Font(Font, FontStyle.Bold),
                AutoSize = false, Dock = DockStyle.Fill, Height = 30,
                TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0),
            });
            if (!string.IsNullOrWhiteSpace(pet.Author))
                tile.Controls.Add(new Label
                {
                    Text = "by " + pet.Author, ForeColor = Color.FromArgb(80, 80, 80),
                    AutoSize = false, Dock = DockStyle.Fill, Height = 16,
                    TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 0, 4),
                });

            var download = new Button { Text = "Download", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 2, 0, 0) };
            download.Click += delegate { DownloadPet_Click(pet); };
            tile.Controls.Add(download);
            return tile;
        }

        private async void CheckOnlinePets_Click(object sender, EventArgs e)
        {
            if (_petOnlineButton != null) _petOnlineButton.Enabled = false;
            SetPetStatus("Checking for pets online…", false);
            try
            {
                _catalog = await FetchCatalogAsync();
                if (_isClosing || IsDisposed) return;
                HashSet<string> localIds = LocalPetIds();
                int available = 0;
                foreach (CatalogPet pet in _catalog.Pets)
                    if (!localIds.Contains(pet.Id)) available++;
                SetPetStatus(available > 0
                    ? "Found " + available + " pet(s) available to download."
                    : "You already have every available pet.", false);
                BuildPetGallery();
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed)
                    SetPetStatus("Could not reach the catalog: " + Short(ex.Message), true);
            }
            finally
            {
                if (!_isClosing && !IsDisposed && _petOnlineButton != null)
                    _petOnlineButton.Enabled = true;
            }
        }

        private async Task<RemoteCatalog> FetchCatalogAsync()
        {
            if (_catalogCancellation != null) _catalogCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _catalogCancellation = cancellation;
            try
            {
                return await RemoteCatalogClient.FetchAsync(cancellation.Token);
            }
            finally
            {
                if (ReferenceEquals(_catalogCancellation, cancellation))
                    _catalogCancellation = null;
                cancellation.Dispose();
            }
        }

        private async void DownloadPet_Click(CatalogPet pet)
        {
            if (pet == null) return;
            SetPetStatus("Downloading " + pet.Name + "…", false);
            try
            {
                byte[] bytes;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
                try
                {
                    bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                        pet.Url, pet.Sha256, PetXmlValidator.MaximumXmlBytes, cancellation.Token);
                }
                finally { cancellation.Dispose(); }
                if (_isClosing || IsDisposed) return;

                // Verify structure before install; a bundled/downloaded file is never trusted blindly.
                string xml = SecureDownload.DecodeUtf8(bytes);
                XmlData.RootNode parsed;
                string validationError;
                if (!PetXmlValidator.TryParse(xml, out parsed, out validationError))
                {
                    SetPetStatus(pet.Name + " failed validation: " + Short(validationError), true);
                    return;
                }

                string directory = SafePetLibraryDirectory(pet.Id);
                Directory.CreateDirectory(directory);
                SecureDownload.WriteAllBytesAtomic(
                    Path.Combine(directory, "animations.xml"), bytes);
                SetPetStatus("Added " + pet.Name + " to your pets.", false);
                BuildPetGallery();
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed)
                    SetPetStatus("Could not download " + pet.Name + ": " + Short(ex.Message), true);
            }
        }

        private static string SafePetLibraryDirectory(string id)
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

        // ---- Online packs (runtime catalog) ---------------------------------

        private HashSet<string> PresentPackIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[]
                { FortuneProvider.CustomDir, AppPaths.BundledFortunesDirectory })
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;
                try
                {
                    foreach (string path in Directory.EnumerateFiles(
                        directory, "*.txt", SearchOption.TopDirectoryOnly))
                        ids.Add(Path.GetFileNameWithoutExtension(path));
                }
                catch { }
            }
            return ids;
        }

        private void SetPacksStatus(string text, bool isError)
        {
            if (_fPacksStatus == null) return;
            _fPacksStatus.ForeColor = isError ? Color.Firebrick : Color.FromArgb(0, 120, 0);
            _fPacksStatus.Text = text ?? "";
        }

        private async void CheckOnlinePacks_Click(object sender, EventArgs e)
        {
            if (_fPacksOnlineButton != null) _fPacksOnlineButton.Enabled = false;
            SetPacksStatus("Checking online…", false);
            try
            {
                _catalog = await FetchCatalogAsync();
                if (_isClosing || IsDisposed) return;
                PopulatePacksTree();
                HashSet<string> present = PresentPackIds();
                int available = 0;
                foreach (CatalogPack pack in _catalog.Packs)
                    if (!present.Contains(pack.Id)) available++;
                SetPacksStatus(available > 0
                    ? available + " pack(s) available — check the ones you want, then Download checked."
                    : "You already have every available pack.", false);
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed)
                    SetPacksStatus("Could not reach the catalog: " + Short(ex.Message), true);
            }
            finally
            {
                if (!_isClosing && !IsDisposed && _fPacksOnlineButton != null)
                    _fPacksOnlineButton.Enabled = true;
            }
        }

        private void PopulatePacksTree()
        {
            if (_fPacksTree == null) return;
            HashSet<string> present = PresentPackIds();
            _treeSyncGuard = true;
            _fPacksTree.BeginUpdate();
            try
            {
                _fPacksTree.Nodes.Clear();
                if (_catalog == null) return;
                var groups = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (CatalogPack pack in _catalog.Packs)
                {
                    string cat = string.IsNullOrEmpty(pack.Group) ? "Other" : pack.Group;
                    TreeNode group;
                    if (!groups.TryGetValue(cat, out group))
                    {
                        group = new TreeNode(cat);
                        groups[cat] = group;
                        order.Add(cat);
                    }
                    bool installed = present.Contains(pack.Id);
                    var leaf = group.Nodes.Add(
                        FriendlyName(pack.Id) + "  (" + pack.Count.ToString("N0") +
                        (installed ? " · installed)" : ")"));
                    leaf.Tag = pack;
                    leaf.Checked = installed;
                    if (installed) leaf.ForeColor = Color.FromArgb(0, 120, 0);
                }
                order.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string cat in order)
                {
                    TreeNode group = groups[cat];
                    group.Text = cat + "  (" + group.Nodes.Count + ")";
                    _fPacksTree.Nodes.Add(group);
                    group.Checked = AllChildrenChecked(group);
                }
                _fPacksTree.CollapseAll();
            }
            finally
            {
                _treeSyncGuard = false;
                _fPacksTree.EndUpdate();
            }
            UpdatePacksDownloadEnabled();
        }

        // Group checkbox cascades to its packs; a leaf toggle recomputes its group. Installed leaves
        // stay checked (they're a no-op on download). The guard blocks programmatic-set recursion.
        private void PacksTree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_treeSyncGuard || e.Node == null) return;
            _treeSyncGuard = true;
            try
            {
                if (e.Node.Nodes.Count > 0)
                    foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked;
                else if (e.Node.Parent != null)
                    e.Node.Parent.Checked = AllChildrenChecked(e.Node.Parent);
            }
            finally { _treeSyncGuard = false; }
            UpdatePacksDownloadEnabled();
        }

        private List<CatalogPack> CheckedDownloadablePacks()
        {
            var result = new List<CatalogPack>();
            if (_fPacksTree == null) return result;
            HashSet<string> present = PresentPackIds();
            foreach (TreeNode g in _fPacksTree.Nodes)
                foreach (TreeNode c in g.Nodes)
                {
                    var pack = c.Tag as CatalogPack;
                    if (pack != null && c.Checked && !present.Contains(pack.Id))
                        result.Add(pack);
                }
            return result;
        }

        private void UpdatePacksDownloadEnabled()
        {
            if (_fPacksDownloadButton == null) return;
            _fPacksDownloadButton.Enabled = CheckedDownloadablePacks().Count > 0;
        }

        private async void DownloadCheckedPacks_Click(object sender, EventArgs e)
        {
            List<CatalogPack> selected = CheckedDownloadablePacks();
            if (selected.Count == 0)
            {
                SetPacksStatus("Check one or more packs to download first.", true);
                return;
            }
            if (_fPacksDownloadButton != null) _fPacksDownloadButton.Enabled = false;
            try
            {
                await DownloadCatalogPacksAsync(selected);
                if (_isClosing || IsDisposed) return;
                PopulatePacksTree();   // reflect the newly-installed packs
            }
            finally
            {
                if (!_isClosing && !IsDisposed) UpdatePacksDownloadEnabled();
            }
        }

        private async Task DownloadCatalogPacksAsync(List<CatalogPack> newPacks)
        {
            SetPacksStatus("Downloading " + newPacks.Count + " pack(s)…", false);
            var downloaded = new List<DownloadedPack>();
            int failures = 0;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            try
            {
                foreach (CatalogPack pack in newPacks)
                {
                    try
                    {
                        byte[] bytes = await RemoteCatalogClient.DownloadVerifiedAsync(
                            pack.Url, pack.Sha256,
                            FortunePackLoadPolicy.MaximumFileBytes, cancellation.Token);
                        downloaded.Add(new DownloadedPack
                        {
                            Item = new PackItem
                            {
                                Pack = new TrustedPack
                                {
                                    Id = pack.Id, Name = pack.Name, Description = pack.Description,
                                    License = pack.License, Sha256 = pack.Sha256, Bytes = pack.Bytes,
                                    Count = pack.Count, DataSchema = pack.DataSchema,
                                    RedistributionApproved = true,
                                }
                            },
                            Bytes = bytes,
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { failures++; }
                }

                int installed = 0;
                if (downloaded.Count > 0)
                {
                    bool gateHeld = false;
                    FortuneImportBatchResult importResult = null;
                    try
                    {
                        await _fortuneDirectoryOperationGate.WaitAsync(cancellation.Token);
                        gateHeld = true;
                        string destination = FortuneProvider.CustomDir;
                        importResult = await Task.Run(delegate
                        {
                            return InstallDownloadedPacks(
                                downloaded, destination,
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                cancellation.Token);
                        }, cancellation.Token);
                    }
                    finally { if (gateHeld) _fortuneDirectoryOperationGate.Release(); }
                    if (importResult != null)
                    {
                        installed = importResult.ImportedCount;
                        failures += importResult.RejectedCount;
                    }
                }

                if (_isClosing || IsDisposed) return;
                PopulateSources();
                if (Program.Mainthread != null) Program.Mainthread.ReloadAiSettings();
                SetPacksStatus(
                    "Added " + installed + " pack(s)" +
                    (failures == 0 ? "." : "; " + failures + " failed."),
                    failures != 0);
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing && !IsDisposed) SetPacksStatus("Pack download canceled.", true);
            }
            finally { cancellation.Dispose(); }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (_prefVolume == null) return;   // designer may raise this before the Preferences tab is built
            _prefVolume.Enabled = checkBox1.Checked;
            if (!checkBox1.Checked)
            {
                if (_prefVolume.Value != 0) _prefVolume.Value = 0;   // fires VolumeNud_Changed -> persists 0
                else VolumeNud_Changed(sender, e);                   // already 0: persist the mute explicitly
            }
        }

        // Volume as an editable 0-10 number. Mirrors the retired trackBar1_Scroll persistence logic.
        private void VolumeNud_Changed(object sender, EventArgs e)
        {
            if (_prefVolume == null) return;
            if (!Program.MyData.SetVolume((float)(_prefVolume.Value / 10.0m)))
            {
                _prefVolume.Value = Math.Max(0, Math.Min(10, (int)Math.Round(Program.MyData.GetVolume() * 10.0)));
                bool persistedEnabled = Program.MyData.GetVolume() >= 0.1f;
                if (checkBox1.Checked != persistedEnabled) checkBox1.Checked = persistedEnabled;
                _prefVolume.Enabled = persistedEnabled;
                ShowMainSettingsSaveFailure();
                return;
            }
            if (Program.MyData.GetVolume() < 0.1f)
            {
                _prefVolume.Enabled = false;
                if (checkBox1.Checked) checkBox1.Checked = false;
            }
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            if (!Program.MyData.SetVolume((float)(trackBar1.Value / 10.0)))
            {
                trackBar1.Value = Math.Max(
                    trackBar1.Minimum,
                    Math.Min(
                        trackBar1.Maximum,
                        (int)Math.Round(Program.MyData.GetVolume() * 10.0)));
                label2.Text = trackBar1.Value.ToString();
                bool persistedEnabled = Program.MyData.GetVolume() >= 0.1f;
                if (checkBox1.Checked != persistedEnabled)
                    checkBox1.Checked = persistedEnabled;
                trackBar1.Enabled = persistedEnabled;
                ShowMainSettingsSaveFailure();
                return;
            }
            if(Program.MyData.GetVolume() < 0.1f)
            {
                trackBar1.Enabled = false;
                checkBox1.Checked = false;
            }
            label2.Text = trackBar1.Value.ToString();
        }

		private void checkBox2_Click(object sender, EventArgs e)
		{
            if (!Program.MyData.SetWindowForeground(checkBox2.Checked))
            {
                checkBox2.Checked = Program.MyData.GetWindowForeground();
                ShowMainSettingsSaveFailure();
            }
		}

        private void checkBox4_CheckedChanged(object sender, EventArgs e)
        {
            if (!Program.MyData.SetStealTaskbarFocus(checkBox4.Checked))
            {
                checkBox4.Checked = Program.MyData.GetStealTaskbarFocus();
                ShowMainSettingsSaveFailure();
            }
        }

        private void trackBar2_Scroll(object sender, EventArgs e)
		{
            if (!Program.MyData.SetAutoStartPets(trackBar2.Value))
            {
                trackBar2.Value = Program.MyData.GetAutoStartPets();
                ShowMainSettingsSaveFailure();
            }
            label5.Text = trackBar2.Value.ToString();
		}

        // Pets-at-startup as an editable number (replaces trackBar2).
        private void AutoStartNud_Changed(object sender, EventArgs e)
        {
            if (_prefAutoStart == null) return;
            if (!Program.MyData.SetAutoStartPets((int)_prefAutoStart.Value))
            {
                _prefAutoStart.Value = Math.Max(_prefAutoStart.Minimum,
                    Math.Min(_prefAutoStart.Maximum, Program.MyData.GetAutoStartPets()));
                ShowMainSettingsSaveFailure();
            }
        }

        private void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            if (!Program.MyData.SetMultiscreen(checkBox3.Checked))
            {
                checkBox3.Checked = Program.MyData.GetMultiscreen();
                ShowMainSettingsSaveFailure();
            }
        }

        private void trackBar3_Scroll(object sender, EventArgs e)
        {
            if (!Program.TryRequestRestartAfterSave(
                delegate { return Program.MyData.SetScale(trackBar3.Value); },
                Program.RequestRestart))
            {
                trackBar3.Value = Program.MyData.GetScale();
                RefreshScaleStatus();
                ShowMainSettingsSaveFailure();
                return;
            }
            RefreshScaleStatus();

            MessageBox.Show("Scale changed. Application will be restarted", "New scale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Hide();
            Application.Exit();
        }

        // Size as an editable number (replaces trackBar3). Applied on commit (Validated) because a scale
        // change restarts the app; validating on each keystroke would restart mid-edit.
        private void ScaleNud_Validated(object sender, EventArgs e)
        {
            if (_prefScale == null) return;
            int requested = (int)_prefScale.Value;
            if (requested == Program.MyData.GetScale()) return;   // no change to apply
            if (!Program.TryRequestRestartAfterSave(
                delegate { return Program.MyData.SetScale(requested); },
                Program.RequestRestart))
            {
                _prefScale.Value = Math.Max(_prefScale.Minimum,
                    Math.Min(_prefScale.Maximum, Program.MyData.GetScale()));
                RefreshScaleStatus();
                ShowMainSettingsSaveFailure();
                return;
            }
            RefreshScaleStatus();
            MessageBox.Show("Scale changed. Application will be restarted", "New scale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Hide();
            Application.Exit();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            flowLayoutPanel2.Visible = false;
        }

        // ---- Preferences tab (merged Animation options + Application) ----------

        /// <summary>
        /// Merge the two designer tabs ("Animation options" + "Application") into a single scrollable
        /// "Preferences" tab placed first, and add a "Run at Windows startup" toggle. The original
        /// TableLayoutPanels are reparented intact (so all existing wiring keeps working).
        /// </summary>
        private void BuildPreferencesTab()
        {
            tabControl1.TabPages.Remove(tabPage2);   // "Animation options"
            tabControl1.TabPages.Remove(tabPage4);   // "Application"

            var tab = new TabPage { Text = "Preferences" };
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(12),
            };

            // Reuse the existing (already-wired) designer controls, reparented into consistent
            // "control + description" rows. Status/value labels travel with their control; static
            // description labels supply the row text via their .Text so wording is preserved.
            button1.AutoSize = true;
            var restoreRow = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0),
            };
            restoreRow.Controls.Add(button1);
            restoreRow.Controls.Add(new PictureBox
            {
                Width = 44, Height = 44, SizeMode = PictureBoxSizeMode.Zoom,
                Image = new Bitmap(Properties.Resources.esheep), Margin = new Padding(10, 0, 0, 0),
            });
            AddPrefRow(panel, restoreRow, label1.Text);

            _prefRunAtStartup = new CheckBox
            {
                AutoSize = true, Text = "Run at Windows startup",
                Checked = IsRunAtStartupEnabled(), Margin = new Padding(0),
            };
            _prefRunAtStartup.CheckedChanged += delegate { SetRunAtStartup(_prefRunAtStartup.Checked); };
            AddPrefRow(panel, _prefRunAtStartup,
                "Launch DesktopPet automatically when you sign in to Windows.");

            // Audio: enable + volume as an editable 0-10 number (no slider).
            string audioError = TSound.CurrentErrorMessage();
            bool audioOk = string.IsNullOrWhiteSpace(audioError);
            _prefVolume = MakeNud(0, 10, (int)Math.Round(Program.MyData.GetVolume() * 10.0));
            _prefVolume.Enabled = audioOk && checkBox1.Checked;
            _prefVolume.ValueChanged += VolumeNud_Changed;
            var audioStack = StackControls(checkBox1, LabeledNud("Volume (0-10):", _prefVolume));
            if (!audioOk)
                audioStack.Controls.Add(new Label
                {
                    AutoSize = true, ForeColor = Color.Firebrick,
                    Text = audioError.Trim(), Margin = new Padding(0, 2, 0, 0),
                });
            AddPrefRow(panel, audioStack, "Play sounds, and set the volume.");

            AddPrefRow(panel, checkBox2, label3.Text);
            AddPrefRow(panel, checkBox4, label6.Text);
            AddPrefRow(panel, checkBox3, label7.Text);

            _prefAutoStart = MakeNud(trackBar2.Minimum, trackBar2.Maximum, Program.MyData.GetAutoStartPets());
            _prefAutoStart.ValueChanged += AutoStartNud_Changed;
            AddPrefRow(panel, LabeledNud("Pets at startup:", _prefAutoStart), label4.Text);

            _prefScale = MakeNud(trackBar3.Minimum, trackBar3.Maximum, Program.MyData.GetScale());
            _prefScale.Validated += ScaleNud_Validated;   // apply on commit (scale change restarts the app)
            AddPrefRow(panel, StackControls(LabeledNud("Size (1-3):", _prefScale), label9), label8.Text);

            // Speech settings, merged from the removed "Speech" tab.
            CreateSpeechControls();
            AddPrefRow(panel, _chkSpeech,
                "Show speech bubbles for greetings, fortunes, and AI remarks. Turning it off silences the pet.");
            AddPrefRow(panel, LabeledNud("Seconds:", _prefDuration),
                "How long a speech bubble stays on screen.");

            // Randomly drop a fortune or insight (new).
            AddPrefRow(panel, BuildRandomDropControls(),
                "Every N ± J minutes the sheep speaks on its own — a fortune, or an AI insight when the " +
                "brain is on. Example: 15 ± 3 makes it speak every 12–18 minutes. Respects your source/genre filters.");

            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });
            tab.Controls.Add(panel);
            tabControl1.TabPages.Insert(0, tab);   // Preferences first; Online pets follows
        }

        private static NumericUpDown MakeNud(int min, int max, int value)
        {
            return new DarkNumericUpDown
            {
                Minimum = min, Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Width = 58, Margin = new Padding(0),
            };
        }

        private static FlowLayoutPanel LabeledNud(string caption, NumericUpDown nud)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0),
            };
            p.Controls.Add(new Label { AutoSize = true, Text = caption, Margin = new Padding(0, 5, 4, 0) });
            p.Controls.Add(nud);
            return p;
        }

        private static FlowLayoutPanel StackControls(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0),
            };
            foreach (Control c in controls) { c.Dock = DockStyle.None; c.Margin = new Padding(0, 0, 0, 2); p.Controls.Add(c); }
            return p;
        }

        private static void AddPrefRow(FlowLayoutPanel parent, Control control, string description)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12),
            };
            control.Margin = new Padding(0, 0, 12, 0);
            row.Controls.Add(control);
            row.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(250, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Text = description ?? "", Margin = new Padding(0, 2, 0, 0),
            });
            parent.Controls.Add(row);
        }

        private void CreateSpeechControls()
        {
            _chkSpeech = new CheckBox
            {
                AutoSize = true, Text = "Enable speech bubbles",
                Checked = Program.MyData.GetSpeechEnabled(), Margin = new Padding(0),
            };
            _chkSpeech.CheckedChanged += ChkSpeech_CheckedChanged;
            _prefDuration = MakeNud(2, 30, Program.MyData.GetSpeechDuration());
            _prefDuration.Enabled = Program.MyData.GetSpeechEnabled();
            _prefDuration.ValueChanged += DurationNud_Changed;
        }

        private FlowLayoutPanel BuildRandomDropControls()
        {
            var box = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0),
            };
            _prefRandomDrop = new CheckBox
            {
                AutoSize = true, Text = "Randomly drop a fortune or insight",
                Checked = _ai.RandomDropEnabled, Margin = new Padding(0, 0, 0, 4),
            };

            int minutes = Math.Min(9999, Math.Max(1, _ai.RandomDropMinutes));
            var intRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            intRow.Controls.Add(new Label { AutoSize = true, Text = "every", Margin = new Padding(0, 5, 4, 0) });
            _prefDropMinutes = new DarkNumericUpDown { Minimum = 1, Maximum = 9999, Value = minutes, Width = 60 };
            intRow.Controls.Add(_prefDropMinutes);
            intRow.Controls.Add(new Label { AutoSize = true, Text = "minutes", Margin = new Padding(4, 5, 0, 0) });

            int maxJit = Math.Max(0, minutes - 1);
            var jitRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            jitRow.Controls.Add(new Label { AutoSize = true, Text = "plus or minus", Margin = new Padding(0, 5, 4, 0) });
            _prefDropJitter = new DarkNumericUpDown
            {
                Minimum = 0, Maximum = maxJit, Width = 58, Margin = new Padding(0),
                Value = Math.Min(maxJit, Math.Max(0, _ai.RandomDropJitterMinutes)),
            };
            jitRow.Controls.Add(_prefDropJitter);
            jitRow.Controls.Add(new Label { AutoSize = true, Text = "minutes", Margin = new Padding(4, 5, 0, 0) });

            box.Controls.Add(_prefRandomDrop);
            box.Controls.Add(intRow);
            box.Controls.Add(jitRow);

            _prefRandomDrop.CheckedChanged += RandomDropChanged;
            _prefDropMinutes.ValueChanged += RandomDropChanged;
            _prefDropJitter.ValueChanged += RandomDropChanged;
            return box;
        }

        // Random-drop settings live in _ai and persist + apply on Options close (FormOptions_ApplyAi
        // -> save + ReloadAiSettings), matching how the other AI settings behave.
        private void RandomDropChanged(object sender, EventArgs e)
        {
            if (_prefRandomDrop == null || _ai == null) return;
            int minutes = (int)_prefDropMinutes.Value;
            int maxJit = Math.Max(0, minutes - 1);
            if ((int)_prefDropJitter.Maximum != maxJit) _prefDropJitter.Maximum = maxJit;
            int jitter = Math.Min((int)_prefDropJitter.Value, maxJit);
            if ((int)_prefDropJitter.Value != jitter) _prefDropJitter.Value = jitter;
            _ai.RandomDropEnabled = _prefRandomDrop.Checked;
            _ai.RandomDropMinutes = minutes;
            _ai.RandomDropJitterMinutes = jitter;
        }

        private const string RunAtStartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunAtStartupValueName = "DesktopPet AI Edition";

        private static bool IsRunAtStartupEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunAtStartupKeyPath, false))
                    return key != null && key.GetValue(RunAtStartupValueName) != null;
            }
            catch { return false; }
        }

        private static void SetRunAtStartup(bool enabled)
        {
            // Per-user HKCU Run key; no admin. Best-effort: startup registration must never crash Options.
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunAtStartupKeyPath, true)
                                 ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunAtStartupKeyPath))
                {
                    if (key == null) return;
                    if (enabled)
                        key.SetValue(RunAtStartupValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(RunAtStartupValueName) != null)
                        key.DeleteValue(RunAtStartupValueName, false);
                }
            }
            catch { /* startup registration is best-effort */ }
        }

        // ---- Speech settings handlers (controls built in the Preferences tab) ------------------

        private void ChkSpeech_CheckedChanged(object sender, EventArgs e)
        {
            if (!Program.MyData.SetSpeechEnabled(_chkSpeech.Checked))
            {
                _chkSpeech.Checked = Program.MyData.GetSpeechEnabled();
                ShowMainSettingsSaveFailure();
                return;
            }
            if (_prefDuration != null) _prefDuration.Enabled = _chkSpeech.Checked;
            ContextMenus.RefreshSpeechMenuItem();
        }

        private void DurationNud_Changed(object sender, EventArgs e)
        {
            if (_prefDuration == null) return;
            if (!Program.MyData.SetSpeechDuration((int)_prefDuration.Value))
            {
                _prefDuration.Value = Math.Max(2, Math.Min(30, Program.MyData.GetSpeechDuration()));
                ShowMainSettingsSaveFailure();
            }
        }

        private void ShowMainSettingsSaveFailure()
        {
            MessageBox.Show(
                this,
                "DesktopPet could not save the setting. The previous value was restored.",
                "Setting not saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        // ---- Fortunes tab ------------------------------------------------------

        /// <summary>An entry in the source picker. ToString drives the checkbox label.</summary>
        private sealed class SourceItem
        {
            public string Id;
            public string Label;
            public override string ToString() { return Label; }
        }

        private static readonly Dictionary<string, string> SourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "classic_philosophy", "Classic Philosophy" }, { "modern_philosophy", "Modern Philosophy" },
            { "authors", "Authors & Writers" }, { "artists", "Artists" }, { "tao", "Tao Te Ching" },
            { "montaigne", "Montaigne" }, { "HeraclitusFragments", "Heraclitus" }, { "SimoneWeil", "Simone Weil" },
            { "jung", "Carl Jung" }, { "Gurdjieff", "Gurdjieff" }, { "mencken", "H. L. Mencken" },
            { "wblake", "William Blake" }, { "ogden_nash", "Ogden Nash" }, { "stevenson", "R. L. Stevenson" },
            { "korzybski", "Korzybski" }, { "Paine", "Thomas Paine" }, { "Rousseau", "Rousseau" },
            { "Bakunin", "Bakunin" }, { "Kerouac-Modern-Prose", "Jack Kerouac" }, { "brecht_dances-events-puzzles", "Bertolt Brecht" },
            { "haraway", "Donna Haraway" }, { "bruno-latour", "Bruno Latour" }, { "immortal_consciousness", "Immortal Consciousness" },
            { "existentialriddles", "Existential Riddles" }, { "Twenty_Lessons_On_Tyranny", "On Tyranny (Snyder)" },
            { "friedman_12-structures", "Friedman: 12 Structures" }, { "Schlesinger", "Schlesinger" },
            { "invisiblestates", "Invisible States" }, { "predictions", "Predictions" }, { "MrRogers", "Mister Rogers" },
            { "ObliqueStrategies", "Oblique Strategies" }, { "epigrams_in_programming", "Epigrams in Programming" },
            { "lwall-quotes", "Larry Wall" }, { "hackers", "Hacker Wisdom" }, { "hacker-questions", "Hacker Questions" },
            { "ComputerDictionary", "Computer Dictionary" }, { "rfc1925", "RFC 1925" },
            { "enkiv2s-glossary-of-tech-industry-terms", "Tech Industry Glossary" }, { "rhetorical-devices", "Rhetorical Devices" },
            { "anathem-glossary", "Anathem Glossary" }, { "ObscureSorrows", "Dictionary of Obscure Sorrows" },
            { "EnglishAsSheIsSpoke", "English As She Is Spoke" }, { "SimpsonsChalkboard", "The Simpsons (chalkboard)" },
            { "FerengiRulesOfAcquisition", "Ferengi Rules of Acquisition" }, { "redgreen", "The Red Green Show" },
            { "handey", "Deep Thoughts (Jack Handey)" }, { "groucho", "Groucho Marx" }, { "pirate", "Pirate Sayings" },
            { "SeventyMaximsOfMaximallyEffectiveMercenaries", "70 Maxims of Mercenaries" }, { "actualcookies", "Fortune Cookies" },
            { "realfacts", "Real Facts" }, { "godin", "Seth Godin" }, { "entertainers", "Entertainers" },
            { "AClaude", "Claude" }, { "racter", "Racter" }, { "critics", "Critics" }, { "Jenny_Holzer", "Jenny Holzer" },
            { "activists", "Activists" }, { "Andromeda", "Andromeda" }, { "PA-historical-markers", "PA Historical Markers" },
            { "yo-mama", "Yo Mama Jokes" }, { "carlin", "George Carlin" }, { "chuckfacts", "Chuck Norris Facts" },
            { "subgenius", "Church of the SubGenius" }, { "RAW", "Robert Anton Wilson" }, { "showerthoughts", "Reddit Showerthoughts" },
            { "BibleAbridged", "Bible (Abridged)" }, { "conalnet", "Conal.net" }, { "higgins_metadramas", "Higgins Metadramas" },
            { "bofh", "BOFH Excuses" }, { "dadjokes", "Dad Jokes" }, { "goedel", "Kurt Gödel" },
            { "pratchett", "Terry Pratchett" }, { "songs-poems", "Songs & Poems" },
            { "off-songs-poems", "Songs & Poems (adult)" }, { "off-zippy", "Zippy the Pinhead (adult)" },
            { "off-black-humor", "Black Humor (adult)" }, { "off-knghtbrd", "KnghtBrd (adult)" },
        };

        /// <summary>
        /// Build the "Fortunes" tab: pick how spicy the sheep's offline chatter is (content tier +
        /// remove-profanity) and which source collections it draws from, plus load your own. An
        /// explicit Apply button writes ai-settings.json and reloads the running pet — closing the
        /// dialog also applies (via FormOptions_ApplyAi), but Apply gives immediate feedback.
        /// </summary>
        private void BuildFortunesTab()
        {
            var tab = new TabPage { Text = "Fortunes" };
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(10), WrapContents = false, AutoScroll = true,
            };

            panel.Controls.Add(new Label { AutoSize = true, Text = "Fortunes", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(0, 0, 0, 10),
                Text = "The offline lines the sheep speaks on landing and when poked. Tune how edgy they get and which collections they come from.",
            });

            // Smart (contextual) fortunes -------------------------------------
            _fSmart = new CheckBox
            {
                AutoSize = true,
                Text     = "Smart fortunes (pick lines that fit what's on screen)",
                Checked  = _ai.SmartFortunes,
                Margin   = new Padding(0, 0, 0, 2),
            };
            _fSmart.CheckedChanged += delegate { _ai.SmartFortunes = _fSmart.Checked; };
            panel.Controls.Add(_fSmart);
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(18, 0, 0, 2),
                Text = "Uses a tiny bundled model — fully offline, no keys. Falls back to random when nothing fits.",
            });
            _fSmartStatus = new Label { AutoSize = true, ForeColor = Color.FromArgb(0, 120, 0), Margin = new Padding(18, 0, 0, 4), Text = "" };
            panel.Controls.Add(_fSmartStatus);
            _fRebuildBtn = new Button { Text = "Rebuild smart weights", AutoSize = true, Margin = new Padding(18, 0, 0, 12) };
            _fRebuildBtn.Click += RebuildWeights_Click;
            panel.Controls.Add(_fRebuildBtn);
            UpdateSmartStatus();
            _fSmartTimer = new System.Windows.Forms.Timer { Interval = 1500 };   // live-update while the dialog is open
            _fSmartTimer.Tick += delegate { UpdateSmartStatus(); };
            _fSmartTimer.Start();
            // Content level ----------------------------------------------------
            _fSpicy = new CheckBox { AutoSize = true, Text = "Enable spicy content (crude / adult humor)", Checked = _ai.SpicyFortunes, Margin = new Padding(0, 0, 0, 4) };
            _fSpicy.CheckedChanged += delegate { _ai.SpicyFortunes = _fSpicy.Checked; UpdateSpicyEnabled(); };
            panel.Controls.Add(_fSpicy);

            var tierRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(18, 0, 0, 4) };
            tierRow.Controls.Add(new Label { AutoSize = true, Text = "Level:", Margin = new Padding(0, 6, 6, 0) });
            _fTier = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            _fTier.Items.AddRange(new object[] { "Edgy + NSFW (everything)", "True NSFW only" });
            _fTier.SelectedIndex = string.Equals(_ai.SpicyTier, "nsfw", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _fTier.SelectedIndexChanged += delegate { _ai.SpicyTier = _fTier.SelectedIndex == 1 ? "nsfw" : "edgy"; };
            tierRow.Controls.Add(_fTier);
            panel.Controls.Add(tierRow);

            _fSpicyOnly = new CheckBox { AutoSize = true, Text = "Skip the tame ones (spicy only)", Checked = _ai.SpicyOnly, Margin = new Padding(18, 0, 0, 4) };
            _fSpicyOnly.CheckedChanged += delegate { _ai.SpicyOnly = _fSpicyOnly.Checked; };
            panel.Controls.Add(_fSpicyOnly);

            _fNoProfanity = new CheckBox { AutoSize = true, Text = "Remove fortunes with recognized profanity or explicit sexual content", Checked = _ai.NoProfanity, Margin = new Padding(0, 4, 0, 12) };
            _fNoProfanity.CheckedChanged += delegate { _ai.NoProfanity = _fNoProfanity.Checked; };
            panel.Controls.Add(_fNoProfanity);

            // Sources ----------------------------------------------------------
            panel.Controls.Add(new Label { AutoSize = true, Text = "Sources", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(0, 0, 0, 4),
                Text = "Collections the sheep may draw from, grouped by collection. Toggle a whole group, or expand it " +
                       "to pick individual shows/authors. Type to filter. (Spicy lines still obey the settings above.)",
            });

            var pickRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var btnAll  = new Button { Text = "Select all",  AutoSize = true, Margin = new Padding(0) };
            var btnNone = new Button { Text = "Select none", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            btnAll.Click  += delegate { SetAllSources(true); };
            btnNone.Click += delegate { SetAllSources(false); };
            _fSourceFilter = new TextBox { Width = 140, Margin = new Padding(10, 1, 0, 0) };
            _fSourceFilter.TextChanged += delegate { PopulateSources(); };
            pickRow.Controls.Add(btnAll);
            pickRow.Controls.Add(btnNone);
            pickRow.Controls.Add(new Label { AutoSize = true, Text = "Filter:", Margin = new Padding(10, 5, 2, 0) });
            pickRow.Controls.Add(_fSourceFilter);
            panel.Controls.Add(pickRow);

            _fSourcesTree = new TreeView
            {
                Width = 340, Height = 210, CheckBoxes = true, HideSelection = false,
                ShowLines = true, ShowRootLines = true, ShowPlusMinus = true,
                Margin = new Padding(0, 0, 0, 2),
            };
            _fSourcesTree.AfterCheck += SourcesTree_AfterCheck;
            panel.Controls.Add(_fSourcesTree);
            _fSourceCount = new Label { AutoSize = true, ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 6), Text = "" };
            panel.Controls.Add(_fSourceCount);

            // Genres (delivery style) -----------------------------------------
            panel.Controls.Add(new Label { AutoSize = true, Text = "Genres", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 4),
                Text = "Check the delivery styles the sheep may use (jokes, wisdom, TV quotes, …). Uncheck one to mute that style.",
            });
            var genreBtnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var btnGAll  = new Button { Text = "Select all",  AutoSize = true, Margin = new Padding(0) };
            var btnGNone = new Button { Text = "Select none", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            btnGAll.Click  += delegate { SetAllGenres(true); };
            btnGNone.Click += delegate { SetAllGenres(false); };
            genreBtnRow.Controls.Add(btnGAll);
            genreBtnRow.Controls.Add(btnGNone);
            panel.Controls.Add(genreBtnRow);
            _fGenres = new CheckedListBox { Width = 340, Height = 130, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(0, 0, 0, 12) };
            panel.Controls.Add(_fGenres);

            // Apply ------------------------------------------------------------
            var applyRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var btnApply = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(0), Font = new Font(Font, FontStyle.Bold) };
            btnApply.Click += delegate { ApplyFortunes(); };
            _fStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(0, 120, 0), Margin = new Padding(10, 6, 0, 0), MaximumSize = new Size(200, 0) };
            applyRow.Controls.Add(btnApply);
            applyRow.Controls.Add(_fStatus);
            panel.Controls.Add(applyRow);

            // Fortune packs (downloaded from the runtime catalog) -------------
            panel.Controls.Add(new Label { AutoSize = true, Text = "Fortune packs", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 4),
                Text = "Curated collections you can add on demand. Check online to browse them by collection, " +
                       "expand one to pick individual shows/authors, then download the checked ones. Each " +
                       "download is checksum-verified and validated; its sources then appear in the picker above.",
            });

            var packBtnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            _fPacksOnlineButton = new Button { Text = "Check online for packs", AutoSize = true, Margin = new Padding(0) };
            _fPacksOnlineButton.Click += CheckOnlinePacks_Click;
            _fPacksDownloadButton = new Button { Text = "Download checked", AutoSize = true, Enabled = false, Margin = new Padding(6, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) };
            _fPacksDownloadButton.Click += DownloadCheckedPacks_Click;
            packBtnRow.Controls.Add(_fPacksOnlineButton);
            packBtnRow.Controls.Add(_fPacksDownloadButton);
            panel.Controls.Add(packBtnRow);

            _fPacksTree = new TreeView
            {
                Width = 340, Height = 170, CheckBoxes = true, HideSelection = false,
                ShowLines = true, ShowRootLines = true, ShowPlusMinus = true,
                Margin = new Padding(0, 0, 0, 2),
            };
            _fPacksTree.AfterCheck += PacksTree_AfterCheck;
            panel.Controls.Add(_fPacksTree);

            _fPacksStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size(340, 0) };
            panel.Controls.Add(_fPacksStatus);

            // Add your own fortunes (user-supplied files) ---------------------
            panel.Controls.Add(new Label { AutoSize = true, Text = "Add your own fortunes", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(0, 0, 0, 6),
                Text = "Bring your own collections. Use 'Add fortunes…' to pick one or more .txt files, or " +
                       "'Open folder' to drop files straight into your fortunes folder. Each file can be one " +
                       "fortune per line, or a classic %-separated fortune file. After importing, press Apply " +
                       "(above) to fold the new lines into the mix.",
            });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(0, 0, 0, 6),
                Text = "On import, each file is checked for valid text and a sensible size, split into individual " +
                       "fortunes, and screened against the content settings above (spicy / profanity filters). " +
                       "If Smart fortunes is on, the new lines are also embedded by the small bundled model so they " +
                       "can be matched to what's on screen — that indexing runs in the background and can take a " +
                       "little while for a large library. Everything stays on your PC; nothing is uploaded.",
            });

            var fileRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            _fAddFortunesButton = new Button { Text = "Add fortunes…", AutoSize = true, Margin = new Padding(0) };
            var btnOpen = new Button { Text = "Open folder", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            _fAddFortunesButton.Click += AddFortunes_Click;
            btnOpen.Click += delegate { OpenCustomFortunesFolder(); };
            _fImportStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(0, 120, 0), Margin = new Padding(10, 6, 0, 0), MaximumSize = new Size(180, 0) };
            fileRow.Controls.Add(_fAddFortunesButton);
            fileRow.Controls.Add(btnOpen);
            fileRow.Controls.Add(_fImportStatus);
            panel.Controls.Add(fileRow);

            // Trailing spacer: AutoScroll otherwise clips the final control's bottom at small window
            // sizes. This guarantees scrollable room past the last real control.
            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });

            tab.Controls.Add(panel);
            tabControl1.TabPages.Add(tab);

            PopulateSources();
            UpdateSpicyEnabled();
        }

        private static void OpenCustomFortunesFolder()
        {
            try
            {
                string directory = Path.GetFullPath(FortuneProvider.CustomDir);
                Directory.CreateDirectory(directory);

                string systemDirectory = Environment.SystemDirectory;
                string explorer = Path.Combine(systemDirectory, "explorer.exe");
                if (!Path.IsPathRooted(explorer) ||
                    !File.Exists(explorer))
                    return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = explorer,
                    Arguments = QuoteWindowsProcessArgument(directory),
                    WorkingDirectory = systemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    // The application does not own Explorer's lifetime; release our handle.
                }
            }
            catch
            {
                // Opening the convenience folder must not take down the options window.
            }
        }

        internal static string QuoteWindowsProcessArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");

            var quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        // ---- Fortune packs downloader ------------------------------------------

        private sealed class PackItem
        {
            public TrustedPack Pack;
        }

        private sealed class DownloadedPack
        {
            public PackItem Item;
            public byte[] Bytes;
        }

        private static FortuneImportBatchResult InstallDownloadedPacks(
            IList<DownloadedPack> downloaded,
            string destinationDirectory,
            ISet<string> approvedOverwrites,
            CancellationToken cancellationToken)
        {
            if (downloaded == null) throw new ArgumentNullException("downloaded");
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new ArgumentException(
                    "A destination directory is required.",
                    "destinationDirectory");

            string stagingRoot = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-trusted-pack-import-" +
                Guid.NewGuid().ToString("N"));
            var sourcePaths = new List<string>(downloaded.Count);
            try
            {
                Directory.CreateDirectory(stagingRoot);
                foreach (DownloadedPack downloadedPack in downloaded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (downloadedPack == null ||
                        downloadedPack.Item == null ||
                        downloadedPack.Item.Pack == null ||
                        downloadedPack.Bytes == null)
                        throw new InvalidDataException(
                            "Downloaded trusted-pack staging metadata is incomplete.");

                    string sourcePath = SecureDownload.ResolveContainedFile(
                        stagingRoot,
                        downloadedPack.Item.Pack.Id,
                        ".txt");
                    sourcePaths.Add(sourcePath);
                    WriteStagedPack(
                        sourcePath,
                        downloadedPack.Bytes,
                        cancellationToken);
                }

                // The shared importer holds the cross-process directory lock, snapshots every
                // existing custom file, validates the complete selected replacement-aware batch
                // against all runtime bounds, and commits only admitted files atomically.
                return FortuneFileImporter.Import(
                    sourcePaths,
                    destinationDirectory,
                    approvedOverwrites,
                    cancellationToken);
            }
            finally
            {
                foreach (string sourcePath in sourcePaths)
                    TryDeletePackStagingFile(sourcePath);
                try
                {
                    if (Directory.Exists(stagingRoot))
                        Directory.Delete(stagingRoot, false);
                }
                catch { }
            }
        }

        internal static bool RunTrustedPackInstallSelfTest(StringBuilder report)
        {
            report = report ?? new StringBuilder();
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopPet-trusted-conflict-selftest-" +
                Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(root);
                const string trustedText =
                    "collision\tlife\tquip\tgeneral\t0\tA trusted collision fixture.";
                byte[] trustedBytes = new UTF8Encoding(false, true).GetBytes(
                    trustedText);
                string sha256;
                using (var hash = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] digest = hash.ComputeHash(trustedBytes);
                    var encoded = new StringBuilder(digest.Length * 2);
                    foreach (byte value in digest)
                        encoded.Append(value.ToString("x2"));
                    sha256 = encoded.ToString();
                }
                var pack = new TrustedPack {
                    Id = "collision",
                    Name = "Collision fixture",
                    Bytes = trustedBytes.Length,
                    Count = 1,
                    DataSchema = FortuneTaxonomy.CurrentSchemaVersion,
                    Sha256 = sha256,
                    RedistributionApproved = true
                };
                var downloaded = new List<DownloadedPack> {
                    new DownloadedPack {
                        Item = new PackItem { Pack = pack },
                        Bytes = trustedBytes
                    }
                };

                string destination = Path.Combine(root, "collision.txt");
                byte[] userBytes = new UTF8Encoding(false).GetBytes(
                    "User-owned bytes must survive a declined replacement.");
                File.WriteAllBytes(destination, userBytes);
                FortuneImportBatchResult declined = InstallDownloadedPacks(
                    downloaded,
                    root,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    CancellationToken.None);
                bool preserved = declined.ImportedCount == 0 &&
                    declined.RejectedCount == 1 &&
                    ByteArraysEqualForSelfTest(
                        userBytes,
                        File.ReadAllBytes(destination));
                if (!preserved)
                {
                    ok = false;
                    report.AppendLine(
                        "TRUSTED PACK FAIL declined conflict changed user bytes");
                }

                FortuneImportBatchResult approved = InstallDownloadedPacks(
                    downloaded,
                    root,
                    new HashSet<string>(
                        new[] { "collision.txt" },
                        StringComparer.OrdinalIgnoreCase),
                    CancellationToken.None);
                bool replaced = approved.ImportedCount == 1 &&
                    ByteArraysEqualForSelfTest(
                        trustedBytes,
                        File.ReadAllBytes(destination));
                if (!replaced)
                {
                    ok = false;
                    report.AppendLine(
                        "TRUSTED PACK FAIL explicit replacement was not applied");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine(
                    "TRUSTED PACK EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    ok = false;
                    report.AppendLine(
                        "TRUSTED PACK CLEANUP EXC: " + ex.Message);
                }
            }
            report.AppendLine(
                "trusted_pack_conflict=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool ByteArraysEqualForSelfTest(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static void WriteStagedPack(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(64 * 1024, bytes.Length - offset);
                    stream.Write(bytes, offset, count);
                    offset += count;
                }
                stream.Flush(true);
            }
        }

        private static void TryDeletePackStagingFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private void UpdateSpicyEnabled()
        {
            if (_fTier != null)      _fTier.Enabled = _fSpicy.Checked;
            if (_fSpicyOnly != null) _fSpicyOnly.Enabled = _fSpicy.Checked;
        }

        private void PopulateSources()
        {
            if (_fSourcesTree == null) return;
            // A rebuild follows file import and trusted-pack installation. Capture the live
            // checklist first so unsaved user choices survive the clear; newly discovered
            // sources remain enabled by default because they are absent from this disabled set.
            if (_fSourcesTree.Nodes.Count > 0)
                SyncFortuneSources();
            var disabled = new HashSet<string>(_ai.DisabledSources ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            string filter = _fSourceFilter != null ? _fSourceFilter.Text.Trim() : "";
            int shown = 0, total = 0;
            long shownLines = 0;
            _fSourcesTree.BeginUpdate();
            _treeSyncGuard = true;
            try
            {
                _fSourcesTree.Nodes.Clear();
                // Group by collection (from the embedded collection map). Embedded/built-in sources
                // fall under "Built-in", user files under "Custom". A group appears only if a source
                // matches the current filter, so empty groups never show.
                var groups = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (SourceStat s in FortuneProvider.Sources())
                {
                    total++;
                    string name = FriendlyName(s.Id);
                    if (filter.Length > 0 &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        s.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string cat = s.Custom ? "Custom" : PackCollections.CollectionName(s.Id);
                    if (string.IsNullOrEmpty(cat)) cat = s.Custom ? "Custom" : "Built-in";
                    TreeNode group;
                    if (!groups.TryGetValue(cat, out group))
                    {
                        group = new TreeNode(cat);
                        groups[cat] = group;
                        order.Add(cat);
                    }
                    var leaf = group.Nodes.Add(name + "  (" + s.Count + ")");
                    leaf.Tag = s.Id;
                    leaf.Checked = !disabled.Contains(s.Id);
                    shown++;
                    shownLines += s.Count;
                }
                order.Sort(delegate(string a, string b)
                {
                    int ra = SourceGroupRank(a), rb = SourceGroupRank(b);
                    return ra != rb ? ra - rb : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });
                foreach (string cat in order)
                {
                    TreeNode group = groups[cat];
                    group.Text = cat + "  (" + group.Nodes.Count + ")";
                    _fSourcesTree.Nodes.Add(group);
                    group.Checked = AllChildrenChecked(group);
                }
                _fSourcesTree.CollapseAll();
            }
            finally
            {
                _treeSyncGuard = false;
                _fSourcesTree.EndUpdate();
            }
            if (_fSourceCount != null)
                _fSourceCount.Text =
                    (filter.Length > 0 ? shown + " of " + total : total.ToString()) +
                    " sources · " + shownLines.ToString("N0") + " lines";
            PopulateGenres();
        }

        // Built-in default corpus first, downloadable collections in the middle, user Custom last.
        private static int SourceGroupRank(string cat)
        {
            if (string.Equals(cat, "Built-in", StringComparison.Ordinal)) return 0;
            if (string.Equals(cat, "Custom", StringComparison.Ordinal)) return 2;
            return 1;
        }

        private void SetAllSources(bool on)
        {
            if (_fSourcesTree == null) return;
            _treeSyncGuard = true;
            try
            {
                foreach (TreeNode g in _fSourcesTree.Nodes)
                {
                    g.Checked = on;
                    foreach (TreeNode c in g.Nodes) c.Checked = on;
                }
            }
            finally { _treeSyncGuard = false; }
        }

        // Group checkbox = "all sources in this theme". Cascade down on a group toggle; recompute
        // the group when a leaf changes. The guard prevents these programmatic sets from recursing.
        private void SourcesTree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_treeSyncGuard || e.Node == null) return;
            _treeSyncGuard = true;
            try
            {
                if (e.Node.Nodes.Count > 0)
                    foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked;
                else if (e.Node.Parent != null)
                    e.Node.Parent.Checked = AllChildrenChecked(e.Node.Parent);
            }
            finally { _treeSyncGuard = false; }
        }

        private static bool AllChildrenChecked(TreeNode group)
        {
            if (group.Nodes.Count == 0) return false;
            foreach (TreeNode c in group.Nodes) if (!c.Checked) return false;
            return true;
        }

        private void SyncFortuneSources()
        {
            if (_fSourcesTree == null) return;
            // Merge with the existing disabled set so sources hidden by the filter keep their state:
            // only sources currently shown in the tree update, everything else is left untouched.
            var disabled = new HashSet<string>(
                _ai.DisabledSources ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode g in _fSourcesTree.Nodes)
                foreach (TreeNode c in g.Nodes)
                {
                    string id = c.Tag as string;
                    if (id == null) continue;
                    if (c.Checked) disabled.Remove(id);
                    else disabled.Add(id);
                }
            _ai.DisabledSources = new List<string>(disabled);
            SyncFortuneGenres();
        }

        private void PopulateGenres()
        {
            if (_fGenres == null) return;
            // Capture live choices before Items.Clear() so unsaved toggles survive; newly-seen
            // genres default to enabled (absent from the disabled set).
            if (_fGenres.Items.Count > 0) SyncFortuneGenres();
            var disabled = new HashSet<string>(_ai.DisabledGenres ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            _fGenres.BeginUpdate();
            try
            {
                _fGenres.Items.Clear();
                foreach (GenreStat g in FortuneProvider.Genres())
                {
                    // Genres are delivery-style toggles, not a library count: no total (it would also
                    // misleadingly include not-downloaded packs).
                    var item = new SourceItem { Id = g.Id, Label = GenreTitle(g.Id) };
                    _fGenres.Items.Add(item, !disabled.Contains(g.Id));
                }
            }
            finally { _fGenres.EndUpdate(); }
        }

        private void SetAllGenres(bool on)
        {
            if (_fGenres == null) return;
            for (int i = 0; i < _fGenres.Items.Count; i++) _fGenres.SetItemChecked(i, on);
        }

        private void SyncFortuneGenres()
        {
            if (_fGenres == null) return;
            var disabled = new List<string>();
            for (int i = 0; i < _fGenres.Items.Count; i++)
                if (!_fGenres.GetItemChecked(i)) disabled.Add(((SourceItem)_fGenres.Items[i]).Id);
            _ai.DisabledGenres = disabled;
        }

        private static string GenreTitle(string genre)
        {
            if (string.IsNullOrEmpty(genre)) return "Other";
            if (string.Equals(genre, "tv-quote", StringComparison.Ordinal)) return "TV quotes";
            return char.ToUpperInvariant(genre[0]) + genre.Substring(1);
        }

        private const string CustomPersonaLabel = "Custom…";

        /// <summary>Index of the preset whose blurb matches the current personality, else the
        /// trailing "Custom…" entry (Presets.Length).</summary>
        private static int MatchPersonaIndex(string blurb)
        {
            string b = (blurb ?? "").Trim();
            for (int i = 0; i < Personas.Presets.Length; i++)
                if (string.Equals(Personas.Presets[i].Blurb, b, StringComparison.OrdinalIgnoreCase))
                    return i;
            return Personas.Presets.Length;   // "Custom…"
        }

        private void ApplyFortunes()
        {
            try
            {
                SyncFortuneSources();
                if (!TrySaveAiSettings())
                {
                    if (_fStatus != null)
                        _fStatus.Text =
                            "Settings are busy; try Apply again. Changes were not applied.";
                    return;
                }
                if (Program.Mainthread != null) Program.Mainthread.ReloadAiSettings();
                int n = new FortuneProvider(_ai).Count;
                if (_fStatus != null) _fStatus.Text = n == 0
                    ? "No fortunes match these filters."
                    : "Applied — " + n.ToString("N0") + " fortunes active";
            }
            catch { if (_fStatus != null) _fStatus.Text = "Could not apply."; }
        }

        private void AddFortunes_Click(object sender, EventArgs e)
        {
            try
            {
                using (var ofd = new OpenFileDialog {
                    Title = "Add fortune files",
                    Filter = "Text files (*.txt)|*.txt",
                    CheckFileExists = true,
                    Multiselect = true
                })
                {
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    string destinationDirectory = FortuneProvider.CustomDir;
                    Directory.CreateDirectory(destinationDirectory);
                    var approvedOverwrites = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var promptedOverwriteNames = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string src in ofd.FileNames)
                    {
                        string fileName = Path.GetFileName(src);
                        if (!string.Equals(
                                Path.GetExtension(fileName),
                                 ".txt",
                                 StringComparison.OrdinalIgnoreCase))
                            continue;
                        // The importer resolves duplicate destination names first-wins. Prompt only
                        // for that first source so a later duplicate cannot accidentally broaden
                        // or reverse the user's overwrite decision for the file that is processed.
                        if (!promptedOverwriteNames.Add(fileName))
                            continue;
                        string destination = Path.Combine(
                            destinationDirectory,
                            fileName);
                        if (File.Exists(destination))
                        {
                            DialogResult overwrite = MessageBox.Show(
                                this,
                                "'" + fileName + "' already exists. Replace it?",
                                "Replace custom fortune file?",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2);
                            if (overwrite == DialogResult.Yes)
                                approvedOverwrites.Add(fileName);
                        }
                    }

                    Task predecessor = _fortuneImportTask;
                    if (_fortuneImportCancellation != null)
                        _fortuneImportCancellation.Cancel();
                    var cancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            _lifetimeCancellation.Token);
                    _fortuneImportCancellation = cancellation;
                    if (_fAddFortunesButton != null)
                        _fAddFortunesButton.Enabled = false;
                    if (_fImportStatus != null)
                        _fImportStatus.Text = "Validating and importing fortune files…";
                    Task operation = ImportFortunesAsync(
                        predecessor,
                        (string[])ofd.FileNames.Clone(),
                        destinationDirectory,
                        approvedOverwrites,
                        cancellation);
                    _fortuneImportTask = operation;
                    TrackOperation(operation);
                }
            }
            catch (Exception ex)
            {
                if (_fImportStatus != null)
                    _fImportStatus.Text = "Could not start import: " + Short(ex.Message);
            }
        }

        private async Task ImportFortunesAsync(
            Task predecessor,
            string[] sourcePaths,
            string destinationDirectory,
            ISet<string> approvedOverwrites,
            CancellationTokenSource cancellation)
        {
            try
            {
                if (predecessor != null)
                {
                    try
                    {
                        await predecessor;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        // The predecessor has still retired and released its importer lock.
                        // Observe the failure, then let the replacement operation proceed.
                        Debug.WriteLine(
                            "Prior fortune import retired with an error: " + ex.Message);
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                FortuneImportBatchResult result;
                bool gateHeld = false;
                try
                {
                    await _fortuneDirectoryOperationGate.WaitAsync(
                        cancellation.Token);
                    gateHeld = true;
                    result = await Task.Run(
                        delegate
                        {
                            return FortuneFileImporter.Import(
                                sourcePaths,
                                destinationDirectory,
                                approvedOverwrites,
                                cancellation.Token);
                        },
                        cancellation.Token);
                }
                finally
                {
                    if (gateHeld)
                        _fortuneDirectoryOperationGate.Release();
                }
                if (_isClosing || IsDisposed ||
                    cancellation.Token.IsCancellationRequested)
                    return;

                PopulateSources();
                if (_fImportStatus != null)
                {
                    _fImportStatus.Text =
                        "Imported " + result.ImportedCount +
                        " file(s); rejected " + result.RejectedCount +
                        " — press Apply";
                }
                if (result.RejectedCount > 0)
                {
                    var details = new StringBuilder();
                    details.AppendLine(
                        "Some files were not imported:");
                    foreach (FortuneImportItemResult item in result.Items)
                    {
                        if (item.Imported) continue;
                        details.AppendLine();
                        details.Append(Path.GetFileName(item.SourcePath));
                        details.Append(": ");
                        details.Append(item.Error ?? "Rejected.");
                    }
                    MessageBox.Show(
                        this,
                        details.ToString(),
                        "Fortune import results",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing && !IsDisposed && _fImportStatus != null)
                    _fImportStatus.Text = "Fortune import cancelled.";
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed && _fImportStatus != null)
                    _fImportStatus.Text = "Fortune import failed: " + Short(ex.Message);
            }
            finally
            {
                bool current = ReferenceEquals(
                    _fortuneImportCancellation,
                    cancellation);
                if (current)
                {
                    _fortuneImportCancellation = null;
                    if (!_isClosing && !IsDisposed &&
                        _fAddFortunesButton != null)
                        _fAddFortunesButton.Enabled = true;
                }
                cancellation.Dispose();
            }
        }

        private static readonly Dictionary<string, string> TvNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "tv-mst3k","MST3K" }, { "tv-tpb","Trailer Park Boys" }, { "tv-koth","King of the Hill" },
            { "tv-office-us","The Office (US)" }, { "tv-3rdrock","3rd Rock from the Sun" }, { "tv-x-files","The X-Files" },
            { "tv-alwayssunny","It's Always Sunny" }, { "tv-venturebros","The Venture Bros." }, { "tv-parksrec","Parks & Rec" },
            { "tv-30rock","30 Rock" }, { "tv-batman-tas","Batman: TAS" }, { "tv-harveybirdman","Harvey Birdman" },
            { "tv-bobsburgers","Bob's Burgers" }, { "tv-metalocalypse","Metalocalypse" }, { "tv-drawntogether","Drawn Together" },
            { "tv-friskydingo","Frisky Dingo" }, { "tv-sealab2021","Sealab 2021" }, { "tv-moralorel","Moral Orel" },
            { "tv-lookaroundyou","Look Around You" }, { "tv-genkill","Generation Kill" }, { "tv-lucydevil","Lucy, Daughter of the Devil" },
            { "tv-mrshow","Mr. Show" }, { "tv-newsradio","NewsRadio" }, { "tv-youngones","The Young Ones" },
            { "tv-a-team","The A-Team" }, { "tv-thewire","The Wire" }, { "tv-southpark","South Park" },
            { "tv-simpsons","The Simpsons" }, { "tv-futurama","Futurama" }, { "tv-firefly","Firefly" },
            { "tv-seinfeld","Seinfeld" }, { "tv-sopranos","The Sopranos" }, { "tv-madmen","Mad Men" },
            { "tv-arrested","Arrested Development" }, { "tv-curb","Curb Your Enthusiasm" }, { "tv-boondocks","The Boondocks" },
            { "tv-peepshow","Peep Show" }, { "tv-beavisbutthead","Beavis and Butt-Head" }, { "tv-robotchicken","Robot Chicken" },
            { "tv-twilightzone","The Twilight Zone" }, { "tv-montypython","Monty Python" }, { "tv-homemovies","Home Movies" },
            { "tv-malcolm","Malcolm in the Middle" }, { "tv-rockos","Rocko's Modern Life" }, { "tv-squidbillies","Squidbillies" },
            { "tv-scrubs","Scrubs" }, { "tv-archer","Archer" }, { "tv-batman","Batman" }, { "tv-qi","QI" }, { "tv-snl","SNL" },
            { "tv-dilbert","Dilbert" }, { "startrek","Star Trek" },
        };

        private static string FriendlyName(string id)
        {
            string name;
            if (SourceNames.TryGetValue(id, out name)) return name;
            if (TvNames.TryGetValue(id, out name)) return name;
            if (id.StartsWith("tv-", StringComparison.OrdinalIgnoreCase)) return Pretty(id.Substring(3));
            if (id.StartsWith("off-", StringComparison.OrdinalIgnoreCase)) return Pretty(id.Substring(4)) + " (adult)";
            return Pretty(id);
        }

        private static string Pretty(string s)
        {
            s = s.Replace('_', ' ').Replace('-', ' ').Trim();
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
        }

        private void UpdateSmartStatus()
        {
            try { if (_fSmartStatus != null && Program.Mainthread != null) _fSmartStatus.Text = "Status: " + Program.Mainthread.SmartFortunesStatus(); }
            catch { }
        }

        /// <summary>Apply the current source/tone selection and re-embed the pool (recompute weights).</summary>
        private void RebuildWeights_Click(object sender, EventArgs e)
        {
            try
            {
                SyncFortuneSources();                       // capture the source checklist into _ai
                if (_ai != null && !TrySaveAiSettings())
                {
                    if (_fSmartStatus != null)
                    {
                        _fSmartStatus.ForeColor = Color.Firebrick;
                        _fSmartStatus.Text =
                            "Status: settings busy; try Rebuild again";
                    }
                    return;
                }
                if (Program.Mainthread != null) Program.Mainthread.RebuildSmartFortunes();
                if (_fSmartStatus != null) { _fSmartStatus.ForeColor = Color.FromArgb(0, 120, 0); _fSmartStatus.Text = "Status: rebuilding…"; }
            }
            catch { }
        }

        // ---- AI tab (Phase 4) --------------------------------------------------

        /// <summary>
        /// Build the "AI" tab: expose the ai-settings.json fields so the AI layer is
        /// configurable without hand-editing JSON. Controls update <see cref="_ai"/> in memory;
        /// the file is written and re-applied to the running pet when the dialog closes
        /// (<see cref="FormOptions_ApplyAi"/> -> <see cref="StartUp.ReloadAiSettings"/>).
        /// </summary>
        private void BuildAiTab()
        {
            var tab = new TabPage { Text = "AI" };
            var panel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding       = new Padding(10),
                WrapContents  = false,
                AutoScroll    = true,
            };

            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text     = "AI brain",
                Font     = new Font(Font, FontStyle.Bold),
                Margin   = new Padding(0, 0, 0, 2),
            });
            panel.Controls.Add(new Label
            {
                AutoSize    = true,
                MaximumSize = new Size(320, 0),
                Text        = "The pet can comment on OCR-derived screen text or screenshots through " +
                              "a local or OpenAI-compatible provider. Changes apply when you close this window.",
                ForeColor   = Color.FromArgb(80, 80, 80),
                Margin      = new Padding(0, 0, 0, 8),
            });

            _aiBrainEnabled = new CheckBox
            {
                AutoSize = true,
                Text     = "Enable AI commentary through the selected provider",
                Checked  = _ai.AiBrainEnabled,
                Margin   = new Padding(0, 0, 0, 12),
            };
            _aiBrainEnabled.CheckedChanged += delegate { _ai.AiBrainEnabled = _aiBrainEnabled.Checked; };
            panel.Controls.Add(_aiBrainEnabled);

            // Persona (backlog 5.5) — name, your name, and a personality blurb steer the pet's voice.
            panel.Controls.Add(MakeLabel("Pet name:"));
            _aiPetName = new TextBox { Width = 300, Text = _ai.PetName, Margin = new Padding(0, 0, 0, 8) };
            _aiPetName.TextChanged += delegate { _ai.PetName = _aiPetName.Text.Trim(); };
            panel.Controls.Add(_aiPetName);

            panel.Controls.Add(MakeLabel("Your name (optional):"));
            _aiUserName = new TextBox { Width = 300, Text = _ai.UserName, Margin = new Padding(0, 0, 0, 8) };
            _aiUserName.TextChanged += delegate { _ai.UserName = _aiUserName.Text.Trim(); };
            panel.Controls.Add(_aiUserName);

            // Persona preset (backlog 2) fills the personality blurb; Speech style (backlog 3) layers
            // an optional voice on top. Both round-trip through _ai.Personality / _ai.SpeechPattern.
            _aiPersonality = new TextBox { Width = 300, Text = _ai.Personality, Margin = new Padding(0, 0, 0, 8) };

            panel.Controls.Add(MakeLabel("Persona preset:"));
            _aiPersona = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 0, 8) };
            foreach (var p in Personas.Presets) _aiPersona.Items.Add(p.Name);
            _aiPersona.Items.Add(CustomPersonaLabel);
            _aiPersona.SelectedIndex = MatchPersonaIndex(_ai.Personality);
            _aiPersona.SelectedIndexChanged += delegate
            {
                int i = _aiPersona.SelectedIndex;
                if (i < 0 || i >= Personas.Presets.Length) return;   // "Custom…" chosen: keep the text
                _personaSyncGuard = true;
                try { _aiPersonality.Text = Personas.Presets[i].Blurb; }
                finally { _personaSyncGuard = false; }
            };
            panel.Controls.Add(_aiPersona);

            panel.Controls.Add(MakeLabel("Personality:"));
            _aiPersonality.TextChanged += delegate
            {
                _ai.Personality = _aiPersonality.Text.Trim();
                if (!_personaSyncGuard && _aiPersona != null)
                    _aiPersona.SelectedIndex = MatchPersonaIndex(_ai.Personality);
            };
            panel.Controls.Add(_aiPersonality);

            panel.Controls.Add(MakeLabel("Speech style:"));
            _aiSpeech = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 0, 8) };
            foreach (var s in Personas.SpeechPatterns) _aiSpeech.Items.Add(s.Name);
            int speechSel = 0;
            for (int i = 0; i < Personas.SpeechPatterns.Length; i++)
                if (string.Equals(Personas.SpeechPatterns[i].Id, _ai.SpeechPattern, StringComparison.OrdinalIgnoreCase))
                    speechSel = i;
            _aiSpeech.SelectedIndex = speechSel;
            _aiSpeech.SelectedIndexChanged += delegate
            {
                int i = _aiSpeech.SelectedIndex;
                if (i >= 0 && i < Personas.SpeechPatterns.Length) _ai.SpeechPattern = Personas.SpeechPatterns[i].Id;
            };
            panel.Controls.Add(_aiSpeech);

            _aiMemory = new CheckBox
            {
                AutoSize = true,
                Text     = "Remember recent remarks (continuity across reactions)",
                Checked  = _ai.MemoryEnabled,
                Margin   = new Padding(0, 0, 0, 12),
            };
            _aiMemory.CheckedChanged += delegate
            {
                _ai.MemoryEnabled = _aiMemory.Checked;
                if (!_ai.MemoryEnabled)
                {
                    if (_aiHistoryStatus != null)
                        _aiHistoryStatus.Text = "Saved history will be deleted when settings apply.";
                }
            };
            panel.Controls.Add(_aiMemory);

            var historyRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(18, 0, 0, 12)
            };
            _aiClearHistoryBtn = new Button { Text = "Clear saved history", AutoSize = true };
            _aiClearHistoryBtn.Click += delegate
            {
                TrackOperation(ClearAiHistoryAsync());
            };
            _aiHistoryStatus = new Label
            {
                AutoSize = true,
                Margin = new Padding(8, 6, 0, 0),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            historyRow.Controls.Add(_aiClearHistoryBtn);
            historyRow.Controls.Add(_aiHistoryStatus);
            panel.Controls.Add(historyRow);

            // Provider ("One Interface": Ollama / LM Studio / llama.cpp / OpenRouter / OpenAI / custom)
            panel.Controls.Add(MakeLabel("Provider:"));
            _aiProvider = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 0, 0, 8) };
            foreach (var p in AiProviders.All) _aiProvider.Items.Add(p.Name);
            int sel = 0; for (int i = 0; i < AiProviders.All.Length; i++) if (string.Equals(AiProviders.All[i].Id, _ai.Provider, StringComparison.OrdinalIgnoreCase)) sel = i;
            _aiProvider.SelectedIndex = sel;
            _aiProvider.SelectedIndexChanged += delegate
            {
                CancelAiNetworkOperations();
                ApplyProviderToUi(true);
            };
            panel.Controls.Add(_aiProvider);

            // Endpoint / base URL (means the Ollama host, or the OpenAI-compatible /v1 base)
            panel.Controls.Add(MakeLabel("Endpoint / base URL:"));
            _aiEndpoint = new TextBox { Width = 300, Margin = new Padding(0, 0, 0, 8) };
            _aiEndpoint.TextChanged += delegate
            {
                _ai.UpdateSelectedProviderEndpoint(_aiEndpoint.Text);
                RefreshSelectedApiKey();
                UpdateCloudConsentUi();
                CancelAiNetworkOperations();
            };
            panel.Controls.Add(_aiEndpoint);
            _aiEndpointStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            panel.Controls.Add(_aiEndpointStatus);

            // API key (cloud providers). Stored DPAPI-encrypted.
            _aiApiKeyLabel = MakeLabel("API key:");
            panel.Controls.Add(_aiApiKeyLabel);
            _aiApiKey = new TextBox { Width = 300, UseSystemPasswordChar = true, Text = _ai.ApiKey, Margin = new Padding(0, 0, 0, 2) };
            _aiApiKey.TextChanged += delegate
            {
                if (_updatingAiApiKeyUi) return;
                string error;
                if (_ai.TrySetApiKey(_aiApiKey.Text, out error))
                {
                    SetAiApiKeyAdmissionError("");
                    CancelAiNetworkOperations();
                }
                else
                {
                    SetAiApiKeyAdmissionError(error);
                }
            };
            panel.Controls.Add(_aiApiKey);
            _aiApiKeyStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                ForeColor = Color.Firebrick,
                Margin = new Padding(0, 0, 0, 8)
            };
            panel.Controls.Add(_aiApiKeyStatus);

            _aiCloudDisclosure = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(0, 2, 0, 4),
                Text = "A non-loopback provider receives your prompts and either OCR-derived screen " +
                       "text or screenshots. DesktopPet will not contact it until you explicitly consent."
            };
            panel.Controls.Add(_aiCloudDisclosure);
            _aiCloudConsent = new CheckBox
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                Text = "I consent to send screen context and prompts to non-loopback endpoints",
                Checked = _ai.CloudDataConsent,
                Margin = new Padding(0, 0, 0, 10)
            };
            _aiCloudConsent.CheckedChanged += delegate
            {
                _ai.CloudDataConsent = _aiCloudConsent.Checked;
                UpdateCloudConsentUi();
                CancelAiNetworkOperations();
            };
            panel.Controls.Add(_aiCloudConsent);
            ApplyProviderToUi(false);

            // Text model
            panel.Controls.Add(MakeLabel("Text model (OCR commentary):"));
            _aiTextModel = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, Text = _ai.TextModel, Margin = new Padding(0, 0, 0, 8) };
            _aiTextModel.TextChanged += delegate { _ai.TextModel = _aiTextModel.Text.Trim(); };
            panel.Controls.Add(_aiTextModel);

            // Vision model
            panel.Controls.Add(MakeLabel("Vision model (screenshot):"));
            _aiVisionModel = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, Text = _ai.VisionModel, Margin = new Padding(0, 0, 0, 2) };
            _aiVisionModel.TextChanged += delegate
            {
                _ai.VisionModel = _aiVisionModel.Text.Trim();
                UpdateVisionCapabilityWarning();
            };
            panel.Controls.Add(_aiVisionModel);
            _aiVisionCapWarning = new Label
            {
                AutoSize = true, MaximumSize = new Size(320, 0), Visible = false,
                ForeColor = Color.FromArgb(176, 96, 0), Margin = new Padding(0, 0, 0, 8),
                Text = "This model may not accept images. Vision needs a multimodal model " +
                       "(e.g. llava, gemma3, llama3.2-vision, moondream, qwen2-vl, minicpm-v, " +
                       "gpt-4o, claude-3…).",
            };
            panel.Controls.Add(_aiVisionCapWarning);

            _aiRefreshModelsBtn = new Button
            {
                Text = "Refresh model list",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            _aiRefreshModelsBtn.Click += delegate { StartModelRefresh(); };
            panel.Controls.Add(_aiRefreshModelsBtn);

            // Use vision
            _aiUseVision = new CheckBox
            {
                AutoSize = true,
                Text     = "Use vision model (send a screenshot instead of OCR text)",
                Checked  = _ai.UseVision,
                Margin   = new Padding(0, 0, 0, 12),
            };
            _aiUseVision.CheckedChanged += delegate
            {
                _ai.UseVision = _aiUseVision.Checked;
                UpdateVisionCapabilityWarning();
            };
            panel.Controls.Add(_aiUseVision);
            UpdateVisionCapabilityWarning();

            // Test: reach the endpoint and request a reply from the chosen model(s). Ollama receives
            // a best-effort unload afterward; generic OpenAI-compatible providers do not.
            var testRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            _aiTestBtn = new Button { Text = "Test connection", AutoSize = true };
            _aiTestBtn.Click += TestAiConnection_Click;
            _aiTestStatus = new Label { AutoSize = true, Text = "", Margin = new Padding(10, 6, 0, 0), MaximumSize = new Size(320, 0) };
            testRow.Controls.Add(_aiTestBtn);
            testRow.Controls.Add(_aiTestStatus);
            panel.Controls.Add(testRow);

            // Hotkey
            _aiHotkeyEnabled = new CheckBox
            {
                AutoSize = true,
                Text     = "Global hotkey to ask about the screen",
                Checked  = _ai.HotkeyEnabled,
                Margin   = new Padding(0, 0, 0, 2),
            };
            _aiHotkeyEnabled.CheckedChanged += delegate
            {
                _ai.HotkeyEnabled = _aiHotkeyEnabled.Checked;
                _aiHotkey.Enabled = _aiHotkeyEnabled.Checked;
            };
            panel.Controls.Add(_aiHotkeyEnabled);

            var hkRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            _aiHotkey = new TextBox { Width = 150, Text = _ai.Hotkey, Enabled = _ai.HotkeyEnabled };
            _aiHotkeyStatus = new Label { AutoSize = true, Text = "", Margin = new Padding(8, 4, 0, 0), MaximumSize = new Size(150, 0) };
            _aiHotkey.TextChanged += delegate
            {
                uint mods, vk;
                if (HotkeyListener.TryParse(_aiHotkey.Text, out mods, out vk))
                {
                    _ai.Hotkey = _aiHotkey.Text.Trim();
                    _aiHotkeyStatus.Text = "OK";
                    _aiHotkeyStatus.ForeColor = Color.Green;
                }
                else
                {
                    _aiHotkeyStatus.Text = "e.g. Ctrl+Alt+P (needs a modifier)";
                    _aiHotkeyStatus.ForeColor = Color.Firebrick;
                }
            };
            hkRow.Controls.Add(_aiHotkey);
            hkRow.Controls.Add(_aiHotkeyStatus);
            panel.Controls.Add(hkRow);

            // Idle commentary
            _aiIdleEnabled = new CheckBox
            {
                AutoSize = true,
                Text     = "Idle commentary (occasional unprompted remarks)",
                Checked  = _ai.IdleCommentaryEnabled,
                Margin   = new Padding(0, 0, 0, 2),
            };
            _aiIdleEnabled.CheckedChanged += delegate
            {
                _ai.IdleCommentaryEnabled = _aiIdleEnabled.Checked;
                UpdateIdleEnabled();
            };
            panel.Controls.Add(_aiIdleEnabled);

            var idleRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            idleRow.Controls.Add(new Label { AutoSize = true, Text = "every", Margin = new Padding(0, 5, 4, 0) });
            _aiIdleMin = new DarkNumericUpDown { Width = 60, Minimum = 15, Maximum = 3600, Value = Clamp(_ai.IdleMinSeconds, 15, 3600) };
            _aiIdleMin.ValueChanged += delegate
            {
                _ai.IdleMinSeconds = (int)_aiIdleMin.Value;
                if (_aiIdleMax.Value < _aiIdleMin.Value) _aiIdleMax.Value = _aiIdleMin.Value;
            };
            idleRow.Controls.Add(_aiIdleMin);
            idleRow.Controls.Add(new Label { AutoSize = true, Text = "to", Margin = new Padding(4, 5, 4, 0) });
            _aiIdleMax = new DarkNumericUpDown { Width = 60, Minimum = 15, Maximum = 3600, Value = Clamp(_ai.IdleMaxSeconds, 15, 3600) };
            _aiIdleMax.ValueChanged += delegate
            {
                _ai.IdleMaxSeconds = (int)_aiIdleMax.Value;
                if (_aiIdleMax.Value < _aiIdleMin.Value) _aiIdleMin.Value = _aiIdleMax.Value;
            };
            idleRow.Controls.Add(_aiIdleMax);
            idleRow.Controls.Add(new Label { AutoSize = true, Text = "seconds", Margin = new Padding(4, 5, 0, 0) });
            panel.Controls.Add(idleRow);

            // Launch preparation
            _aiAutoStart = new CheckBox { AutoSize = true, Text = "Start Ollama automatically if it isn't running", Checked = _ai.AutoStartServer, Margin = new Padding(0, 0, 0, 2) };
            _aiAutoStart.CheckedChanged += delegate { _ai.AutoStartServer = _aiAutoStart.Checked; };
            panel.Controls.Add(_aiAutoStart);

            _aiWarmUp = new CheckBox { AutoSize = true, Text = "Preload the model on launch (faster first reply)", Checked = _ai.WarmUpOnLaunch, Margin = new Padding(0, 0, 0, 2) };
            _aiWarmUp.CheckedChanged += delegate { _ai.WarmUpOnLaunch = _aiWarmUp.Checked; };
            panel.Controls.Add(_aiWarmUp);

            // Trailing spacer: AutoScroll otherwise clips the final control's bottom at small window
            // sizes. This guarantees scrollable room past the last real control.
            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });

            tab.Controls.Add(panel);
            tabControl1.TabPages.Add(tab);

            UpdateIdleEnabled();
            UpdateCloudConsentUi();
            // Opening Options and changing consent remain network-silent. Model discovery is
            // started only by the explicit Refresh model list action wired above.
        }

        private async Task ClearAiHistoryAsync()
        {
            if (_isClosing || _aiClearHistoryBtn == null ||
                _aiHistoryStatus == null)
                return;

            _aiClearHistoryBtn.Enabled = false;
            _aiHistoryStatus.ForeColor = Color.FromArgb(80, 80, 80);
            _aiHistoryStatus.Text = "Clearing saved history…";
            ChatHistoryDeleteResult result;
            try
            {
                if (Program.Mainthread != null)
                    result = await Program.Mainthread.ClearAiHistory();
                else
                    result = await Task.Run(
                        (Func<ChatHistoryDeleteResult>)
                        ChatHistory.DeletePersisted);
            }
            catch (Exception ex)
            {
                result = ChatHistoryDeleteResult.Failure(ex.Message);
            }

            if (_isClosing || IsDisposed ||
                _aiHistoryStatus == null ||
                _aiHistoryStatus.IsDisposed)
                return;
            if (result != null && result.Succeeded)
            {
                _aiHistoryStatus.ForeColor = Color.FromArgb(0, 120, 0);
                _aiHistoryStatus.Text = "Saved history deleted.";
            }
            else if (result != null && result.Pending)
            {
                _aiHistoryStatus.ForeColor = Color.DarkOrange;
                _aiHistoryStatus.Text =
                    "Deletion pending; DesktopPet will retry. " +
                    Short(result.Error);
            }
            else
            {
                _aiHistoryStatus.ForeColor = Color.Firebrick;
                _aiHistoryStatus.Text =
                    "History was not deleted: " +
                    Short(result == null ? "No deletion result." : result.Error);
            }
            if (_aiClearHistoryBtn != null && !_aiClearHistoryBtn.IsDisposed)
                _aiClearHistoryBtn.Enabled = true;
        }

        private static Label MakeLabel(string text)
        {
            return new Label { AutoSize = true, Text = text, Margin = new Padding(0, 0, 0, 2) };
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void UpdateIdleEnabled()
        {
            bool on = _aiIdleEnabled.Checked;
            _aiIdleMin.Enabled = on;
            _aiIdleMax.Enabled = on;
        }

        private string SelectedAiEndpoint()
        {
            return string.Equals(_ai.Provider, "ollama", StringComparison.OrdinalIgnoreCase)
                ? _ai.Endpoint
                : _ai.OpenAiBaseUrl;
        }

        private void CancelAiNetworkOperations()
        {
            if (_modelRefreshCancellation != null)
                _modelRefreshCancellation.Cancel();
            if (_aiTestCancellation != null)
                _aiTestCancellation.Cancel();
        }

        private bool TryAuthorizeSelectedEndpoint(
            out string normalized,
            out string error)
        {
            if (!AiEndpointPolicy.TryNormalize(
                    SelectedAiEndpoint(),
                    out normalized,
                    out error))
                return false;
            if (!AiEndpointPolicy.IsLoopbackEndpoint(normalized) &&
                !_ai.CloudDataConsent)
            {
                error = "Consent is required before contacting a non-loopback endpoint.";
                return false;
            }
            return true;
        }

        private void UpdateCloudConsentUi()
        {
            if (_ai == null || _aiEndpointStatus == null) return;
            string normalized;
            string error;
            if (!AiEndpointPolicy.TryNormalize(
                    SelectedAiEndpoint(),
                    out normalized,
                    out error))
            {
                _aiEndpointStatus.ForeColor = Color.Firebrick;
                _aiEndpointStatus.Text = error;
                return;
            }

            bool remote = !AiEndpointPolicy.IsLoopbackEndpoint(normalized);
            if (!remote)
            {
                _aiEndpointStatus.ForeColor = Color.FromArgb(0, 120, 0);
                _aiEndpointStatus.Text = "Loopback endpoint; screen context stays on this computer.";
            }
            else if (!_ai.CloudDataConsent)
            {
                _aiEndpointStatus.ForeColor = Color.Firebrick;
                _aiEndpointStatus.Text =
                    "Remote endpoint blocked until explicit consent is checked.";
            }
            else
            {
                _aiEndpointStatus.ForeColor = Color.DarkOrange;
                _aiEndpointStatus.Text =
                    "Remote endpoint allowed: screen context may leave this computer.";
            }
        }

        /// <summary>Reflect the selected provider in the UI: prefill the URL and show/hide the key field.</summary>
        private void ApplyProviderToUi(bool prefillUrl)
        {
            string provider = _ai.Provider;
            if (_aiProvider != null &&
                _aiProvider.SelectedIndex >= 0 &&
                _aiProvider.SelectedIndex < AiProviders.All.Length)
                provider = AiProviders.All[_aiProvider.SelectedIndex].Id;
            string url = _ai.SelectProviderEndpoint(provider, prefillUrl);
            bool ollama = string.Equals(
                _ai.Provider,
                "ollama",
                StringComparison.OrdinalIgnoreCase);
            if (_aiEndpoint != null) _aiEndpoint.Text = url;
            bool showKey = !ollama;
            if (_aiApiKeyLabel != null) _aiApiKeyLabel.Visible = showKey;
            if (_aiApiKey != null) _aiApiKey.Visible = showKey;
            if (_aiApiKeyStatus != null) _aiApiKeyStatus.Visible = showKey;
            RefreshSelectedApiKey();
            UpdateCloudConsentUi();
        }

        private void SetAiApiKeyAdmissionError(string error)
        {
            _aiApiKeyAdmissionError = error ?? "";
            if (_aiApiKeyStatus != null)
                _aiApiKeyStatus.Text = _aiApiKeyAdmissionError;
        }

        private void RefreshSelectedApiKey()
        {
            if (_ai == null || _aiApiKey == null) return;
            SetAiApiKeyAdmissionError("");
            string selected = _ai.ApiKey;
            if (string.Equals(
                    _aiApiKey.Text,
                    selected,
                    StringComparison.Ordinal))
                return;
            _updatingAiApiKeyUi = true;
            try { _aiApiKey.Text = selected; }
            finally { _updatingAiApiKeyUi = false; }
        }

        private void StartModelRefresh()
        {
            if (_isClosing || _ai == null || _aiRefreshModelsBtn == null) return;
            if (_modelRefreshCancellation != null)
                _modelRefreshCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _modelRefreshCancellation = cancellation;
            TrackOperation(PopulateModelsAsync(cancellation));
        }

        private async Task PopulateModelsAsync(CancellationTokenSource cancellation)
        {
            if (!ShouldPublishModelRefresh(cancellation))
            {
                CompleteModelRefresh(cancellation);
                return;
            }
            bool ollama = string.Equals(_ai.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
            string endpoint;
            string policyError;
            if (!TryAuthorizeSelectedEndpoint(out endpoint, out policyError))
            {
                if (ShouldPublishModelRefresh(cancellation))
                {
                    _aiEndpointStatus.ForeColor = Color.Firebrick;
                    _aiEndpointStatus.Text = policyError;
                }
                CompleteModelRefresh(cancellation);
                return;
            }

            string key = _ai.ApiKey;
            if (ShouldPublishModelRefresh(cancellation))
            {
                _aiRefreshModelsBtn.Enabled = false;
                _aiEndpointStatus.ForeColor = Color.FromArgb(80, 80, 80);
                _aiEndpointStatus.Text = "Loading model list…";
            }
            var names = new List<string>();
            try
            {
                using (HttpClientHandler handler = AiEndpointPolicy.CreateNoRedirectHandler())
                {
                    names = await FetchModelNamesAsync(
                        handler,
                        endpoint,
                        ollama,
                        key,
                        TimeSpan.FromSeconds(4),
                        cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ShouldPublishModelRefresh(cancellation))
                    return;
                names.Sort(StringComparer.OrdinalIgnoreCase);
                FillModelCombos(names.ToArray());
                _aiEndpointStatus.ForeColor = Color.FromArgb(0, 120, 0);
                _aiEndpointStatus.Text = names.Count + " model(s) found.";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ShouldPublishModelRefresh(cancellation))
                {
                    _aiEndpointStatus.ForeColor = Color.Firebrick;
                    _aiEndpointStatus.Text = "Model list failed: " + Short(ex.Message);
                }
            }
            finally
            {
                CompleteModelRefresh(cancellation);
            }
        }

        internal static async Task<List<string>> FetchModelNamesAsync(
            HttpMessageHandler handler,
            string endpoint,
            bool ollama,
            string apiKey,
            TimeSpan deadline,
            CancellationToken cancellation)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("The model endpoint is required.", "endpoint");

            var names = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var http = new HttpClient(handler, false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            })
            using (var request = new HttpRequestMessage(
                HttpMethod.Get,
                endpoint + (ollama ? "/api/tags" : "/models")))
            {
                request.Headers.Add("User-Agent", "DesktopPet");
                if (!ollama && !string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKey);
                string json = await AiEndpointPolicy.SendAndReadResponseStringAsync(
                    http,
                    request,
                    deadline,
                    cancellation,
                    MaximumModelListBytes).ConfigureAwait(false);
                JArray values =
                    JObject.Parse(json)[ollama ? "models" : "data"] as JArray;
                if (values != null)
                {
                    int inspected = 0;
                    foreach (JToken model in values)
                    {
                        inspected++;
                        if (inspected > MaximumReturnedModelRecords)
                            throw new InvalidDataException(
                                "The endpoint returned too many models.");
                        if (names.Count >= MaximumListedModels) break;
                        string rawName = (string)model[ollama ? "name" : "id"];
                        string name;
                        if (AiModelPolicy.TryNormalize(rawName, out name) &&
                            seenNames.Add(name))
                            names.Add(name);
                    }
                }
            }
            return names;
        }

        private void CompleteModelRefresh(CancellationTokenSource cancellation)
        {
            bool current = ReferenceEquals(_modelRefreshCancellation, cancellation);
            if (current)
                _modelRefreshCancellation = null;
            cancellation.Dispose();
            if (current && !_isClosing && !IsDisposed && _aiRefreshModelsBtn != null)
                _aiRefreshModelsBtn.Enabled = true;
        }

        private bool ShouldPublishModelRefresh(
            CancellationTokenSource cancellation)
        {
            return ShouldPublishOperationResult(
                _modelRefreshCancellation,
                cancellation,
                _isClosing,
                IsDisposed);
        }

        private void FillModelCombos(string[] names)
        {
            FillCombo(_aiTextModel, names);
            FillCombo(_aiVisionModel, names);
            UpdateVisionCapabilityWarning();
        }

        // Backlog 4: quick, name-based capability check. When the vision feature is on but the chosen
        // vision model does not look multimodal, show a non-blocking advisory. Never a hard gate,
        // since the heuristic can be wrong for new model families.
        private void UpdateVisionCapabilityWarning()
        {
            if (_aiVisionCapWarning == null || _aiUseVision == null || _aiVisionModel == null) return;
            _aiVisionCapWarning.Visible =
                _aiUseVision.Checked && !AiModelPolicy.LooksVisionCapable(_aiVisionModel.Text);
        }

        private static void FillCombo(ComboBox combo, string[] names)
        {
            string current = combo.Text;   // keep the configured value even if the server doesn't list it
            combo.Items.Clear();
            combo.Items.AddRange(names);
            combo.Text = current;
        }

        /// <summary>
        /// Verify the AI brain end-to-end: reach the endpoint, then request a reply from the chosen
        /// text model and, if vision is on, the vision model. Ollama unloads those models afterward;
        /// generic OpenAI-compatible providers have no remote-memory control operation.
        /// </summary>
        private void TestAiConnection_Click(object sender, EventArgs e)
        {
            string endpoint;
            string policyError;
            if (!TryAuthorizeSelectedEndpoint(out endpoint, out policyError))
            {
                _aiTestStatus.ForeColor = Color.Firebrick;
                _aiTestStatus.Text = policyError;
                return;
            }

            if (_aiTestCancellation != null)
                _aiTestCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _aiTestCancellation = cancellation;
            TrackOperation(TestAiConnectionAsync(endpoint, cancellation));
        }

        private async Task TestAiConnectionAsync(
            string endpoint,
            CancellationTokenSource cancellation)
        {
            IPetBrainBackend backend = null;
            try
            {
                if (!ShouldPublishAiTest(cancellation))
                    return;
                _aiTestBtn.Enabled = false;
                _aiTestStatus.ForeColor = Color.FromArgb(80, 80, 80);
                _aiTestStatus.Text = "Testing… (may load a model)";
                bool ollama = string.IsNullOrEmpty(_ai.Provider) || string.Equals(_ai.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
                string key = _ai.ApiKey, ollamaPath = _ai.OllamaPath, textModel = _ai.TextModel, visionModel = _ai.VisionModel;
                bool testVision = _ai.UseVision && !string.IsNullOrWhiteSpace(visionModel) && !string.Equals(visionModel, textModel, StringComparison.OrdinalIgnoreCase);
                TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(10, _ai.TimeoutSeconds));

                string result;
                Color color;
                backend = ollama ? (IPetBrainBackend)new OllamaClient(endpoint, timeout, ollamaPath)
                                 : new OpenAiCompatBackend(endpoint, key, timeout);
                bool local = AiEndpointPolicy.IsLoopbackEndpoint(endpoint);
                bool up = ollama && local
                    ? await backend.EnsureServerAsync(cancellation.Token)
                    : await backend.IsAvailableAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!up)
                {
                    result = "✗ can't reach " + endpoint;
                    color = Color.Firebrick;
                }
                else
                {
                    string textResult = await TestModel(
                        backend, textModel, "text", cancellation.Token);
                    string visionResult = testVision
                        ? await TestModel(backend, visionModel, "vision", cancellation.Token)
                        : "";
                    result = "✓ connected" + textResult + visionResult;
                    color = result.IndexOf('✗') >= 0
                        ? Color.Firebrick
                        : Color.FromArgb(0, 120, 0);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (ShouldPublishAiTest(cancellation))
                {
                    _aiTestStatus.ForeColor = color;
                    _aiTestStatus.Text = result;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ShouldPublishAiTest(cancellation))
                {
                    _aiTestStatus.ForeColor = Color.Firebrick;
                    _aiTestStatus.Text = "✗ " + Short(ex.Message);
                }
            }
            finally
            {
                try { if (backend != null) backend.Dispose(); } catch { }
                bool current = ReferenceEquals(
                    _aiTestCancellation,
                    cancellation);
                if (current)
                    _aiTestCancellation = null;
                cancellation.Dispose();
                if (current && !_isClosing && !IsDisposed &&
                    _aiTestBtn != null)
                    _aiTestBtn.Enabled = true;
            }
        }

        private bool ShouldPublishAiTest(CancellationTokenSource cancellation)
        {
            return ShouldPublishOperationResult(
                _aiTestCancellation,
                cancellation,
                _isClosing,
                IsDisposed);
        }

        private static bool ShouldPublishOperationResult(
            CancellationTokenSource current,
            CancellationTokenSource candidate,
            bool isClosing,
            bool isDisposed)
        {
            return !isClosing &&
                !isDisposed &&
                candidate != null &&
                ReferenceEquals(current, candidate) &&
                !candidate.IsCancellationRequested;
        }

        internal static bool RunAsyncPublicationSelfTest(StringBuilder report)
        {
            report = report ?? new StringBuilder();
            bool ok = true;
            var old = new CancellationTokenSource();
            var replacement = new CancellationTokenSource();
            try
            {
                if (!ShouldPublishOperationResult(
                        old,
                        old,
                        false,
                        false))
                {
                    ok = false;
                    report.AppendLine(
                        "ASYNC UI FAIL current live operation was rejected");
                }
                if (ShouldPublishOperationResult(
                        replacement,
                        old,
                        false,
                        false))
                {
                    ok = false;
                    report.AppendLine(
                        "ASYNC UI FAIL stale operation was allowed to publish");
                }
                old.Cancel();
                if (ShouldPublishOperationResult(
                        old,
                        old,
                        false,
                        false))
                {
                    ok = false;
                    report.AppendLine(
                        "ASYNC UI FAIL canceled operation was allowed to publish");
                }
                if (ShouldPublishOperationResult(
                        replacement,
                        replacement,
                        true,
                        false) ||
                    ShouldPublishOperationResult(
                        replacement,
                        replacement,
                        false,
                        true))
                {
                    ok = false;
                    report.AppendLine(
                        "ASYNC UI FAIL closing/disposed form was allowed to publish");
                }
            }
            finally
            {
                old.Dispose();
                replacement.Dispose();
            }
            report.AppendLine(
                "async_ui_generation=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        internal static async Task<string> TestModel(
            IPetBrainBackend backend,
            string model,
            string label,
            CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(model)) return "  ·  no " + label + " model set";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var msgs = new List<ChatMessage> { ChatMessage.User("Reply with OK.", null) };
                string r = await backend.ChatAsync(model, msgs, false, cancellation);
                sw.Stop();
                bool ok = !string.IsNullOrWhiteSpace(r);
                return "  ·  " + label + " '" + model + "' " + (ok ? ("OK " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s") : "✗ no reply");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return "  ·  " + label + " '" + model + "' ✗ " + Short(ex.Message); }
            finally
            {
                await UnloadTestModelBoundedAsync(backend, model);
            }
        }

        private static async Task UnloadTestModelBoundedAsync(
            IPetBrainBackend backend,
            string model)
        {
            if (backend == null || string.IsNullOrWhiteSpace(model)) return;
            using (var cleanupCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                Task unload;
                try
                {
                    unload = backend.UnloadAsync(model, cleanupCancellation.Token);
                }
                catch
                {
                    return;
                }
                if (unload == null) return;

                Task completed = await Task.WhenAny(
                    unload,
                    Task.Delay(TimeSpan.FromSeconds(2)));
                if (completed == unload)
                {
                    try { await unload; } catch { }
                    return;
                }

                try { cleanupCancellation.Cancel(); } catch { }
                ObserveTestModelUnloadFailure(unload);
            }
        }

        private static void ObserveTestModelUnloadFailure(Task unload)
        {
            if (unload == null) return;
            unload.ContinueWith(
                    task =>
                    {
                        var ignored = task.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private void TrackOperation(Task operation)
        {
            if (operation == null) return;
            lock (_asyncOperationsLock)
                _asyncOperations.Add(operation);
            operation.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        AggregateException observed = completed.Exception;
                        Debug.WriteLine(observed);
                    }
                    lock (_asyncOperationsLock)
                        _asyncOperations.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void ExerciseTabsForResourceChurn()
        {
            if (!Program.ResourceChurnSelfTestActive)
                throw new InvalidOperationException(
                    "Resource churn diagnostics are not active.");
            for (int index = 0;
                index < tabControl1.TabPages.Count;
                index++)
            {
                tabControl1.SelectedIndex = index;
                tabControl1.Refresh();
                Application.DoEvents();
            }
        }

        internal Task BeginResourceChurnCloseForDiagnostics()
        {
            if (!Program.ResourceChurnSelfTestActive)
                throw new InvalidOperationException(
                    "Resource churn diagnostics are not active.");
            _resourceChurnDiagnostic = true;
            Task pending = Task.Delay(
                TimeSpan.FromMinutes(1),
                _lifetimeCancellation.Token);
            TrackOperation(pending);
            return pending;
        }

        private void CancelAndObserveOperations()
        {
            try { _lifetimeCancellation.Cancel(); } catch { }
            try
            {
                if (_fortuneImportCancellation != null)
                    _fortuneImportCancellation.Cancel();
                if (_modelRefreshCancellation != null)
                    _modelRefreshCancellation.Cancel();
                if (_aiTestCancellation != null)
                    _aiTestCancellation.Cancel();
            }
            catch { }

            Task[] pending;
            lock (_asyncOperationsLock)
            {
                pending = new Task[_asyncOperations.Count];
                _asyncOperations.CopyTo(pending);
            }
            if (pending.Length == 0)
            {
                _lifetimeCancellation.Dispose();
                return;
            }

            Task.WhenAll(pending).ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        AggregateException observed = completed.Exception;
                        Debug.WriteLine(observed);
                    }
                    _lifetimeCancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void DisposeOwnedImages(Control control)
        {
            if (control == null) return;
            foreach (Control child in control.Controls)
                DisposeOwnedImages(child);

            var pictureBox = control as PictureBox;
            if (pictureBox != null && pictureBox.Image != null)
            {
                Image image = pictureBox.Image;
                pictureBox.Image = null;
                image.Dispose();
            }

            var button = control as Button;
            if (button != null && button.Image != null)
            {
                Image image = button.Image;
                button.Image = null;
                image.Dispose();
            }
        }

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 70
                ? UnicodeTextProgress.TruncateAtCodePointBoundary(s, 70) + "…"
                : s;
        }

        private bool TrySaveAiSettings()
        {
            return _ai != null &&
                _ai.SaveWithin(UiSettingsSaveTimeoutMilliseconds);
        }

        /// <summary>Persist the AI settings and apply them to the running pet when the dialog closes.</summary>
        private void FormOptions_ApplyAi(object sender, FormClosingEventArgs e)
        {
            if (_isClosing) return;
            if (_resourceChurnDiagnostic)
            {
                BeginOptionsClose();
                return;
            }
            if (!string.IsNullOrEmpty(_aiApiKeyAdmissionError))
            {
                DialogResult choice = MessageBox.Show(
                    this,
                    "The API key was not accepted: " +
                    Short(_aiApiKeyAdmissionError) +
                    "\r\n\r\nChoose Retry to keep this window open and correct it, " +
                    "or Cancel to close without applying the changes.",
                    "API key not accepted",
                    MessageBoxButtons.RetryCancel,
                    MessageBoxIcon.Error);
                e.Cancel = choice == DialogResult.Retry;
                if (!e.Cancel) BeginOptionsClose();
                return;
            }
            try
            {
                if (_fSourcesTree != null)
                    SyncFortuneSources();
                if (_ai != null && !TrySaveAiSettings())
                {
                    DialogResult choice = MessageBox.Show(
                        this,
                        "DesktopPet could not save the AI settings promptly. Another instance or " +
                        "operation may be using the settings file.\r\n\r\nChoose Retry to keep " +
                        "this window open and try again, or Cancel to close it and discard the " +
                        "unapplied changes.",
                        "Settings not saved",
                        MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Error);
                    e.Cancel = choice == DialogResult.Retry;
                    if (!e.Cancel) BeginOptionsClose();
                    return;
                }
            }
            catch (Exception ex)
            {
                DialogResult choice = MessageBox.Show(
                    this,
                    "DesktopPet could not save the settings: " + Short(ex.Message) +
                    "\r\n\r\nChoose Retry to keep this window open, or Cancel to close " +
                    "without applying the changes.",
                    "Settings not saved",
                    MessageBoxButtons.RetryCancel,
                    MessageBoxIcon.Error);
                e.Cancel = choice == DialogResult.Retry;
                if (!e.Cancel) BeginOptionsClose();
                return;
            }

            BeginOptionsClose();
            try
            {
                if (Program.Mainthread != null) Program.Mainthread.ReloadAiSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The settings were saved, but the running pet could not reload them: " +
                    Short(ex.Message),
                    "Restart required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void BeginOptionsClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            CancelAndObserveOperations();
            if (_fSmartTimer != null)
            {
                _fSmartTimer.Stop();
                _fSmartTimer.Dispose();
                _fSmartTimer = null;
            }
        }
    }
}
