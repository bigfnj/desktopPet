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
        private CheckBox _chkSpeech;
        private TrackBar _trkDuration;
        private Label    _lblDurationVal;

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
        private bool            _treeSyncGuard;   // suppresses AfterCheck cascade during bulk updates
        private CheckBox        _prefRunAtStartup;
        private CheckBox        _prefRandomDrop;
        private NumericUpDown   _prefDropMinutes;
        private TrackBar        _prefDropJitter;
        private Label           _prefDropJitterVal;
        private CheckedListBox  _fGenres;
        private Label           _fStatus;
        private Button          _fAddFortunesButton;
        private CheckedListBox  _fPacks;
        private Label           _fPacksStatus;
        private Button          _fPacksRefreshButton;
        private Button          _fPacksDownloadButton;
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
        private CancellationTokenSource _packDownloadCancellation;
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
            /// The legacy browser callback is retained for designer compatibility, but online pet
            /// content is fail-closed until a pinned and redistribution-approved catalog exists.
            /// </summary>
        private void webBrowser1_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            ShowOnlinePetsUnavailable();
        }

        private void tabControl1_DrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            TabPage tabPage = tabControl1.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            g.FillRectangle(selected ? Brushes.White : Brushes.LightGray, e.Bounds);

            using (var textBrush = new SolidBrush(Color.Black))
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
            int requestedFactor = ScalePolicy.FactorFromLevel(trackBar3.Value);
            int effectiveFactor = requestedFactor;
            StartUp main = Program.Mainthread;
            Animations activeAnimations = main == null ? null : main.GetAnimations();
            if (activeAnimations != null)
                effectiveFactor = activeAnimations.ScaleFactor;
            label9.Text = ScalePolicy.StatusText(trackBar3.Value, effectiveFactor);
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
            ShowOnlinePetsUnavailable();
        }

        private void ShowOnlinePetsUnavailable()
        {
            while (flowLayoutPanel1.Controls.Count > 0)
            {
                Control control = flowLayoutPanel1.Controls[0];
                flowLayoutPanel1.Controls.RemoveAt(0);
                DisposeOwnedImages(control);
                control.Dispose();
            }

            flowLayoutPanel1.FlowDirection = FlowDirection.TopDown;
            flowLayoutPanel1.WrapContents = false;
            flowLayoutPanel1.Padding = new Padding(14);
            flowLayoutPanel1.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.Firebrick,
                Text = "Online pet downloads are unavailable."
            });
            flowLayoutPanel1.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Margin = new Padding(0, 10, 0, 0),
                Text = "DesktopPet does not yet have a commit-pinned, hash-verified, " +
                       "redistribution-approved pet catalog. For your safety, this screen " +
                       "will not contact the old mutable download source."
            });
            flowLayoutPanel2.Visible = false;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            trackBar1.Enabled = checkBox1.Checked;
            if(!trackBar1.Enabled)
            {
                trackBar1.Value = 0;
                trackBar1_Scroll(sender, e);
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
            AddPrefRow(panel, button1, label1.Text);

            _prefRunAtStartup = new CheckBox
            {
                AutoSize = true, Text = "Run at Windows startup",
                Checked = IsRunAtStartupEnabled(), Margin = new Padding(0),
            };
            _prefRunAtStartup.CheckedChanged += delegate { SetRunAtStartup(_prefRunAtStartup.Checked); };
            AddPrefRow(panel, _prefRunAtStartup,
                "Launch DesktopPet automatically when you sign in to Windows.");

            SizeSlider(trackBar1);
            AddPrefRow(panel, StackControls(checkBox1, trackBar1, label2), "Play sounds, and set the volume.");
            AddPrefRow(panel, checkBox2, label3.Text);
            AddPrefRow(panel, checkBox4, label6.Text);
            AddPrefRow(panel, checkBox3, label7.Text);
            SizeSlider(trackBar2);
            AddPrefRow(panel, StackControls(trackBar2, label5), label4.Text);
            SizeSlider(trackBar3);
            AddPrefRow(panel, StackControls(trackBar3, label9), label8.Text);

            // Speech settings, merged from the removed "Speech" tab.
            CreateSpeechControls();
            AddPrefRow(panel, _chkSpeech,
                "Show speech bubbles for greetings, fortunes, and AI remarks. Turning it off silences the pet.");
            AddPrefRow(panel, StackControls(_trkDuration, _lblDurationVal),
                "How long a speech bubble stays on screen.");

            // Randomly drop a fortune or insight (new).
            AddPrefRow(panel, BuildRandomDropControls(),
                "Every N ± J minutes the sheep speaks on its own — a fortune, or an AI insight when the " +
                "brain is on. Example: 15 ± 3 makes it speak every 12–18 minutes. Respects your source/genre filters.");

            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });
            tab.Controls.Add(panel);
            tabControl1.TabPages.Insert(0, tab);   // Preferences first; Online pets follows
        }

        private static void SizeSlider(TrackBar bar)
        {
            bar.Dock = DockStyle.None; bar.AutoSize = false; bar.Width = 180;
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
            _trkDuration = new TrackBar
            {
                Minimum = 2, Maximum = 30, TickFrequency = 4, Width = 180, AutoSize = false,
                Value = Program.MyData.GetSpeechDuration(), Enabled = Program.MyData.GetSpeechEnabled(),
            };
            _trkDuration.Scroll += TrkDuration_Scroll;
            _lblDurationVal = new Label { AutoSize = true, Text = _trkDuration.Value + " seconds" };
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
            _prefDropMinutes = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = minutes, Width = 60 };
            intRow.Controls.Add(_prefDropMinutes);
            intRow.Controls.Add(new Label { AutoSize = true, Text = "minutes", Margin = new Padding(4, 5, 0, 0) });

            int maxJit = Math.Max(1, minutes - 1);
            var jitRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            jitRow.Controls.Add(new Label { AutoSize = true, Text = "plus or minus", Margin = new Padding(0, 12, 4, 0) });
            _prefDropJitter = new TrackBar
            {
                Minimum = 0, Maximum = maxJit, TickFrequency = 1, Width = 150, AutoSize = false,
                Value = Math.Min(maxJit, Math.Max(0, _ai.RandomDropJitterMinutes)),
            };
            jitRow.Controls.Add(_prefDropJitter);
            _prefDropJitterVal = new Label { AutoSize = true, Text = _prefDropJitter.Value + " min", Margin = new Padding(4, 12, 0, 0) };
            jitRow.Controls.Add(_prefDropJitterVal);

            box.Controls.Add(_prefRandomDrop);
            box.Controls.Add(intRow);
            box.Controls.Add(jitRow);

            _prefRandomDrop.CheckedChanged += RandomDropChanged;
            _prefDropMinutes.ValueChanged += RandomDropChanged;
            _prefDropJitter.Scroll += RandomDropChanged;
            return box;
        }

        // Random-drop settings live in _ai and persist + apply on Options close (FormOptions_ApplyAi
        // -> save + ReloadAiSettings), matching how the other AI settings behave.
        private void RandomDropChanged(object sender, EventArgs e)
        {
            if (_prefRandomDrop == null || _ai == null) return;
            int minutes = (int)_prefDropMinutes.Value;
            int maxJit = Math.Max(1, minutes - 1);
            if (_prefDropJitter.Maximum != maxJit) _prefDropJitter.Maximum = maxJit;
            int jitter = Math.Min(_prefDropJitter.Value, minutes - 1);
            if (_prefDropJitter.Value != jitter) _prefDropJitter.Value = jitter;
            _prefDropJitterVal.Text = jitter + " min";
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
            _trkDuration.Enabled = _chkSpeech.Checked;
            ContextMenus.RefreshSpeechMenuItem();
        }

        private void TrkDuration_Scroll(object sender, EventArgs e)
        {
            if (!Program.MyData.SetSpeechDuration(_trkDuration.Value))
            {
                _trkDuration.Value = Program.MyData.GetSpeechDuration();
                ShowMainSettingsSaveFailure();
                return;
            }
            _lblDurationVal.Text = _trkDuration.Value + " seconds";
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
                Text = "Collections the sheep may draw from, grouped by theme. Toggle a whole group, or expand it to pick individual sources. (Spicy lines still obey the settings above.)",
            });

            var pickRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var btnAll  = new Button { Text = "Select all",  AutoSize = true, Margin = new Padding(0) };
            var btnNone = new Button { Text = "Select none", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            btnAll.Click  += delegate { SetAllSources(true); };
            btnNone.Click += delegate { SetAllSources(false); };
            pickRow.Controls.Add(btnAll);
            pickRow.Controls.Add(btnNone);
            panel.Controls.Add(pickRow);

            _fSourcesTree = new TreeView
            {
                Width = 340, Height = 210, CheckBoxes = true, HideSelection = false,
                ShowLines = true, ShowRootLines = true, ShowPlusMinus = true,
                Margin = new Padding(0, 0, 0, 6),
            };
            _fSourcesTree.AfterCheck += SourcesTree_AfterCheck;
            panel.Controls.Add(_fSourcesTree);

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
            _fGenres = new CheckedListBox { Width = 340, Height = 130, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(0, 0, 0, 6) };
            panel.Controls.Add(_fGenres);

            var fileRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            _fAddFortunesButton =
                new Button { Text = "Add fortunes…", AutoSize = true, Margin = new Padding(0) };
            var btnOpen = new Button { Text = "Open folder",       AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            _fAddFortunesButton.Click += AddFortunes_Click;
            btnOpen.Click += delegate
            {
                OpenCustomFortunesFolder();
            };
            fileRow.Controls.Add(_fAddFortunesButton);
            fileRow.Controls.Add(btnOpen);
            panel.Controls.Add(fileRow);

            // Apply ------------------------------------------------------------
            var applyRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            var btnApply = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(0), Font = new Font(Font, FontStyle.Bold) };
            btnApply.Click += delegate { ApplyFortunes(); };
            _fStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(0, 120, 0), Margin = new Padding(10, 6, 0, 0), MaximumSize = new Size(200, 0) };
            applyRow.Controls.Add(btnApply);
            applyRow.Controls.Add(_fStatus);
            panel.Controls.Add(applyRow);

            // Packs (trusted embedded catalog) --------------------------------
            panel.Controls.Add(new Label { AutoSize = true, Text = "Fortune packs", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) });
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, 0, 0, 4),
                Text = "This list comes from the catalog embedded in this build. Held entries remain visible, " +
                       "but only commit-pinned, hash-verified packs with recorded redistribution approval can be installed.",
            });
            _fPacks = new CheckedListBox { Width = 340, Height = 150, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(0, 0, 0, 6) };
            _fPacks.ItemCheck += FortunePack_ItemCheck;
            panel.Controls.Add(_fPacks);

            var packBtnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            _fPacksRefreshButton = new Button { Text = "Reload embedded list", AutoSize = true, Margin = new Padding(0) };
            _fPacksDownloadButton = new Button { Text = "Install checked", AutoSize = true, Margin = new Padding(6, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) };
            _fPacksRefreshButton.Click += delegate { LoadTrustedPacks(); };
            _fPacksDownloadButton.Click += DownloadPacks_Click;
            _fPacksStatus = new Label { AutoSize = true, Text = "", ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(10, 6, 0, 0), MaximumSize = new Size(170, 0) };
            packBtnRow.Controls.Add(_fPacksRefreshButton);
            packBtnRow.Controls.Add(_fPacksDownloadButton);
            packBtnRow.Controls.Add(_fPacksStatus);
            panel.Controls.Add(packBtnRow);

            // Trailing spacer: AutoScroll otherwise clips the final control's bottom at small window
            // sizes. This guarantees scrollable room past the last real control.
            panel.Controls.Add(new Label { Text = "", AutoSize = false, Width = 1, Height = 16, Margin = new Padding(0) });

            tab.Controls.Add(panel);
            tabControl1.TabPages.Add(tab);

            PopulateSources();
            UpdateSpicyEnabled();
            LoadTrustedPacks();
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
            public bool Installed;

            public override string ToString()
            {
                string text = Pack.Name + "  (" + Pack.Count.ToString("N0") + ")"
                    + (!string.Equals(Pack.Vibe, "clean", StringComparison.OrdinalIgnoreCase)
                        ? "  [" + Pack.Vibe + "]"
                        : "");
                if (!Pack.RedistributionApproved)
                    text += "  [HELD: redistribution approval pending]";
                if (Installed) text += "  verified local copy";
                return text;
            }
        }

        private sealed class DownloadedPack
        {
            public PackItem Item;
            public byte[] Bytes;
        }

        private void LoadTrustedPacks()
        {
            if (_fPacks == null) return;
            List<TrustedPack> catalog;
            string error;
            if (!TrustedPackCatalog.TryLoad(out catalog, out error))
            {
                _fPacks.Items.Clear();
                _fPacks.Enabled = false;
                _fPacksDownloadButton.Enabled = false;
                _fPacksStatus.ForeColor = Color.Firebrick;
                _fPacksStatus.Text = "Embedded catalog rejected: " + Short(error);
                return;
            }

            var items = new List<PackItem>(catalog.Count);
            foreach (TrustedPack pack in catalog)
                items.Add(new PackItem { Pack = pack, Installed = IsVerifiedLocalPack(pack) });
            FillPacks(items);
        }

        private void FillPacks(List<PackItem> items)
        {
            _fPacks.BeginUpdate();
            try
            {
                _fPacks.Items.Clear();
                foreach (PackItem item in items)
                    _fPacks.Items.Add(
                        item,
                        item.Pack.RedistributionApproved && item.Installed);
            }
            finally
            {
                _fPacks.EndUpdate();
            }

            int approved = 0;
            int installable = 0;
            foreach (PackItem item in items)
                if (item.Pack.RedistributionApproved)
                {
                    approved++;
                    if (!item.Installed) installable++;
                }
            int held = items.Count - approved;
            _fPacks.Enabled = true;
            _fPacksDownloadButton.Enabled = installable > 0;
            _fPacksStatus.ForeColor = Color.FromArgb(80, 80, 80);
            _fPacksStatus.Text = approved + " approved; " + held + " held";
        }

        private void FortunePack_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.NewValue != CheckState.Checked || e.Index < 0 ||
                e.Index >= _fPacks.Items.Count) return;
            var item = _fPacks.Items[e.Index] as PackItem;
            if (item == null || item.Pack.RedistributionApproved) return;

            e.NewValue = CheckState.Unchecked;
            _fPacksStatus.ForeColor = Color.Firebrick;
            _fPacksStatus.Text = "Held packs cannot be downloaded.";
        }

        /// <summary>Download every checked-but-not-installed pack into the fortunes folder, then reload.</summary>
        private void DownloadPacks_Click(object sender, EventArgs e)
        {
            var todo = new List<PackItem>();
            for (int i = 0; i < _fPacks.Items.Count; i++)
                if (_fPacks.GetItemChecked(i))
                {
                    var item = (PackItem)_fPacks.Items[i];
                    if (item.Pack.RedistributionApproved && !item.Installed)
                        todo.Add(item);
                }

            if (todo.Count == 0)
            {
                _fPacksStatus.Text = "No approved, uninstalled packs checked.";
                return;
            }

            var approvedOverwrites = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var authorized = new List<PackItem>(todo.Count);
            foreach (PackItem item in todo)
            {
                string destinationPath = SecureDownload.ResolveContainedFile(
                    FortuneProvider.CustomDir,
                    item.Pack.Id,
                    ".txt");
                if (File.Exists(destinationPath))
                {
                    string fileName = Path.GetFileName(destinationPath);
                    DialogResult overwrite = MessageBox.Show(
                        this,
                        "'" + fileName + "' already exists and does not match the trusted " +
                        "catalog pack. Installing this pack will permanently replace those " +
                        "existing bytes.\r\n\r\nReplace the existing file?",
                        "Trusted pack name conflict",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (overwrite != DialogResult.Yes)
                        continue;
                    approvedOverwrites.Add(fileName);
                }
                authorized.Add(item);
            }
            if (authorized.Count == 0)
            {
                _fPacksStatus.Text = "No trusted-pack replacement was authorized.";
                return;
            }

            if (_packDownloadCancellation != null)
                _packDownloadCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _packDownloadCancellation = cancellation;
            TrackOperation(DownloadPacksAsync(
                authorized,
                approvedOverwrites,
                cancellation));
        }

        private async Task DownloadPacksAsync(
            IList<PackItem> items,
            ISet<string> approvedOverwrites,
            CancellationTokenSource cancellation)
        {
            int installed = 0;
            int failures = 0;
            var downloaded = new List<DownloadedPack>();
            _fPacksDownloadButton.Enabled = false;
            _fPacksRefreshButton.Enabled = false;
            _fPacksStatus.ForeColor = Color.FromArgb(80, 80, 80);
            _fPacksStatus.Text = "Downloading verified content…";
            try
            {
                foreach (PackItem item in items)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    TrustedPack pack = item.Pack;
                    if (!pack.RedistributionApproved)
                        continue;
                    string limitsError;
                    if (!FortunePackLoadPolicy.TryValidatePackMetadata(
                            pack.Bytes, pack.Count, out limitsError))
                    {
                        Debug.WriteLine(
                            "Trusted pack cannot be loaded by the runtime: " +
                            limitsError + ".");
                        failures++;
                        continue;
                    }

                    Uri uri;
                    string urlError;
                    if (!SecureDownload.TryValidatePinnedRawGitHubUrl(
                            pack.Url, "bigfnj", "desktopPet", out uri, out urlError))
                    {
                        Debug.WriteLine(urlError);
                        failures++;
                        continue;
                    }

                    try
                    {
                        byte[] bytes = await SecureDownload.DownloadBytesAsync(
                            uri, pack.Bytes, cancellation.Token);
                        string validationError;
                        if (!TryValidatePackBytes(pack, bytes, out validationError))
                            throw new InvalidDataException(validationError);

                        downloaded.Add(new DownloadedPack {
                            Item = item,
                            Bytes = bytes
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        failures++;
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                if (downloaded.Count > 0)
                {
                    FortuneImportBatchResult importResult = null;
                    bool gateHeld = false;
                    try
                    {
                        await _fortuneDirectoryOperationGate.WaitAsync(
                            cancellation.Token);
                        gateHeld = true;
                        string destinationDirectory = FortuneProvider.CustomDir;
                        importResult = await Task.Run(
                            delegate
                            {
                                return InstallDownloadedPacks(
                                    downloaded,
                                    destinationDirectory,
                                    approvedOverwrites,
                                    cancellation.Token);
                            },
                            cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures += downloaded.Count;
                        Debug.WriteLine(
                            "Trusted pack batch admission failed: " + ex.Message);
                    }
                    finally
                    {
                        if (gateHeld)
                            _fortuneDirectoryOperationGate.Release();
                    }

                    if (importResult != null)
                    {
                        installed += importResult.ImportedCount;
                        failures += importResult.RejectedCount;
                        foreach (FortuneImportItemResult item in importResult.Items)
                            if (!item.Imported)
                                Debug.WriteLine(
                                    "Trusted pack was not admitted: " +
                                    Path.GetFileName(item.SourcePath) + ": " +
                                    (item.Error ?? "Rejected."));
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                PopulateSources();
                if (Program.Mainthread != null)
                    Program.Mainthread.ReloadAiSettings();
                LoadTrustedPacks();
                _fPacksStatus.ForeColor = failures == 0
                    ? Color.FromArgb(0, 120, 0)
                    : Color.Firebrick;
                _fPacksStatus.Text = "Installed " + installed + " pack(s)"
                    + (failures == 0 ? "." : "; " + failures + " failed.");
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                    _fPacksStatus.Text = "Pack download canceled.";
            }
            finally
            {
                if (ReferenceEquals(_packDownloadCancellation, cancellation))
                    _packDownloadCancellation = null;
                cancellation.Dispose();
                if (!_isClosing && !IsDisposed)
                {
                    _fPacksRefreshButton.Enabled = true;
                    _fPacksDownloadButton.Enabled = HasApprovedCatalogEntry();
                }
            }
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

        private bool HasApprovedCatalogEntry()
        {
            if (_fPacks == null) return false;
            foreach (object value in _fPacks.Items)
            {
                var item = value as PackItem;
                if (item != null && item.Pack.RedistributionApproved && !item.Installed)
                    return true;
            }
            return false;
        }

        private static bool IsVerifiedLocalPack(TrustedPack pack)
        {
            try
            {
                string path = SecureDownload.ResolveContainedFile(
                    FortuneProvider.CustomDir, pack.Id, ".txt");
                if (!File.Exists(path) || new FileInfo(path).Length != pack.Bytes)
                    return false;
                string error;
                return TryValidatePackBytes(pack, File.ReadAllBytes(path), out error);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryValidatePackBytes(
            TrustedPack pack,
            byte[] bytes,
            out string error)
        {
            error = null;
            try
            {
                if (pack == null)
                    throw new InvalidDataException("Trusted pack metadata is unavailable.");
                string limitsError;
                if (!FortunePackLoadPolicy.TryValidatePackMetadata(
                        pack.Bytes, pack.Count, out limitsError))
                    throw new InvalidDataException(
                        "Trusted pack cannot be loaded by the runtime: " + limitsError + ".");
                if (bytes == null || bytes.Length != pack.Bytes)
                    throw new InvalidDataException(
                        "Downloaded size does not match the embedded catalog.");
                SecureDownload.RequireSha256(bytes, pack.Sha256);
                string content = SecureDownload.DecodeUtf8(bytes);
                int rowCount;
                int schemaVersion;
                if (!FortuneProvider.TryValidateTaggedPack(
                        content,
                        pack.Count,
                        out rowCount,
                        out schemaVersion,
                        out error))
                    return false;
                if (rowCount != pack.Count)
                {
                    error = "Pack row count does not match the embedded catalog.";
                    return false;
                }
                if (schemaVersion != pack.DataSchema)
                {
                    error = "Pack schema does not match the embedded catalog.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
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
            _fSourcesTree.BeginUpdate();
            _treeSyncGuard = true;
            try
            {
                _fSourcesTree.Nodes.Clear();
                // Sources() returns theme-grouped, contiguous order (custom last), so consecutive
                // runs of one category title become one parent node.
                TreeNode group = null;
                string groupTitle = null;
                foreach (SourceStat s in FortuneProvider.Sources())
                {
                    string cat = s.Custom ? "Custom" : TopicTitle(s.Topic);
                    if (group == null || !string.Equals(groupTitle, cat, StringComparison.Ordinal))
                    {
                        groupTitle = cat;
                        group = _fSourcesTree.Nodes.Add(cat);
                    }
                    var leaf = group.Nodes.Add(FriendlyName(s.Id) + "  (" + s.Count + ")");
                    leaf.Tag = s.Id;
                    leaf.Checked = !disabled.Contains(s.Id);
                }
                foreach (TreeNode g in _fSourcesTree.Nodes)
                    g.Checked = AllChildrenChecked(g);
                _fSourcesTree.CollapseAll();
            }
            finally
            {
                _treeSyncGuard = false;
                _fSourcesTree.EndUpdate();
            }
            PopulateGenres();
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
            var disabled = new List<string>();
            foreach (TreeNode g in _fSourcesTree.Nodes)
                foreach (TreeNode c in g.Nodes)
                {
                    string id = c.Tag as string;
                    if (id != null && !c.Checked) disabled.Add(id);
                }
            _ai.DisabledSources = disabled;
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
                    if (_fStatus != null)
                        _fStatus.Text = "Validating and importing fortune files…";
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
                if (_fStatus != null)
                    _fStatus.Text = "Could not start import: " + Short(ex.Message);
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
                if (_fStatus != null)
                {
                    _fStatus.Text =
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
                if (!_isClosing && !IsDisposed && _fStatus != null)
                    _fStatus.Text = "Fortune import cancelled.";
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed && _fStatus != null)
                    _fStatus.Text = "Fortune import failed: " + Short(ex.Message);
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

        private static string TopicTitle(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return "Other";
            if (string.Equals(topic, "work-money", StringComparison.Ordinal)) return "Work & Money";
            return char.ToUpperInvariant(topic[0]) + topic.Substring(1);
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
            _aiIdleMin = new NumericUpDown { Width = 60, Minimum = 15, Maximum = 3600, Value = Clamp(_ai.IdleMinSeconds, 15, 3600) };
            _aiIdleMin.ValueChanged += delegate
            {
                _ai.IdleMinSeconds = (int)_aiIdleMin.Value;
                if (_aiIdleMax.Value < _aiIdleMin.Value) _aiIdleMax.Value = _aiIdleMin.Value;
            };
            idleRow.Controls.Add(_aiIdleMin);
            idleRow.Controls.Add(new Label { AutoSize = true, Text = "to", Margin = new Padding(4, 5, 4, 0) });
            _aiIdleMax = new NumericUpDown { Width = 60, Minimum = 15, Maximum = 3600, Value = Clamp(_ai.IdleMaxSeconds, 15, 3600) };
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
                if (_packDownloadCancellation != null)
                    _packDownloadCancellation.Cancel();
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
