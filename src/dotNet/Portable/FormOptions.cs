using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopPet
{
        /// <summary>
        /// Application options. Need a redesign, so it is not documented.
        /// </summary>
        /// <preliminary/>
    public partial class FormOptions : Form
    {
        // Speech tab controls (created programmatically so Designer.cs is untouched)
        private CheckBox _chkSpeech;
        private TrackBar _trkDuration;
        private Label    _lblDurationVal;

            /// <summary>
            /// Constructor
            /// </summary>
        public FormOptions()
        {
            InitializeComponent();
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
            /// New page was loaded. Check if page starts with the -XML- key. If so, the page will be converted to an xml.
            /// </summary>
            /// <param name="sender">Caller as object.</param>
            /// <param name="e">Webpage event values.</param>
        private void webBrowser1_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            WebBrowser web = (WebBrowser)sender;
            string s = web.DocumentText;
            if(s.Substring(0, 5) == "-XML-")
            {
                Program.Mainthread.LoadNewXMLFromString(s.Substring(5));
                Close();
            }
        }

        private void tabControl1_DrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Brush _textBrush;
            
            // Get the item from the collection.
            TabPage _tabPage = tabControl1.TabPages[e.Index];

            // Use our own font.
            Font _tabFont;


            if (e.State == DrawItemState.Selected)
            {
                // Draw a different background colour, and don't paint a focus rectangle.
                _textBrush = new SolidBrush(Color.Black);
                g.FillRectangle(Brushes.White, e.Bounds);
                _tabFont = new Font(tabControl1.TabPages[e.Index].Font.FontFamily.ToString(), (float)11.0, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            else
            {
                _textBrush = new SolidBrush(Color.Black);
                g.FillRectangle(Brushes.LightGray, e.Bounds);
                _tabFont = new Font(tabControl1.TabPages[e.Index].Font.FontFamily.ToString(), (float)10.0, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            
            // Draw string. Center the text.
            StringFormat _stringFlags = new StringFormat();
            _stringFlags.Alignment = StringAlignment.Center;
            _stringFlags.LineAlignment = StringAlignment.Center;
            g.DrawString(_tabPage.Text, _tabFont, _textBrush, tabControl1.GetTabRect(e.Index), _stringFlags);
        }

        private void FormOptions_Load(object sender, EventArgs e)
        {
                // Set up audio values
            checkBox1.Checked = (Properties.Settings.Default.Volume > 0.0);
			trackBar1.Value = (int)(Properties.Settings.Default.Volume * 10);
            trackBar1.Enabled = checkBox1.Checked;
			label2.Text = Program.Mainthread.ErrorMessages.AudioErrorMessage;
            if (label2.Text.Length > 1)
            {
                trackBar1.Enabled = false;
                checkBox1.Enabled = false;
            }
			checkBox2.Checked = Properties.Settings.Default.WinForeground;
			trackBar2.Value = Properties.Settings.Default.AutostartPets;
            label5.Text = trackBar2.Value.ToString();
            label6.Text = trackBar1.Value.ToString();

            BuildSpeechTab();
        }

        private void BuildSpeechTab()
        {
            var tabPage5 = new TabPage { Text = "Speech" };

            var panel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding       = new Padding(10),
                WrapContents  = false,
            };

            // Enable toggle
            _chkSpeech = new CheckBox
            {
                AutoSize = true,
                Text     = "Enable speech bubbles",
                Checked  = Properties.Settings.Default.SpeechEnabled,
                Margin   = new Padding(0, 0, 0, 4),
            };
            _chkSpeech.CheckedChanged += ChkSpeech_CheckedChanged;

            var lblDesc = new Label
            {
                AutoSize  = true,
                Text      = "Show a speech bubble above the pet.\n" +
                            "Phase 1: use 'Test Speech' in the tray menu.\n" +
                            "Phase 2 will add AI-generated content.",
                ForeColor = Color.FromArgb(80, 80, 80),
                Margin    = new Padding(0, 0, 0, 12),
            };

            // Duration slider
            var lblDurTitle = new Label
            {
                AutoSize = true,
                Text     = "Bubble display duration:",
                Margin   = new Padding(0, 0, 0, 2),
            };

            _trkDuration = new TrackBar
            {
                Minimum       = 2,
                Maximum       = 30,
                TickFrequency = 4,
                Width         = 300,
                Value         = Math.Max(2, Math.Min(30,
                                    Properties.Settings.Default.SpeechDuration)),
                Enabled       = Properties.Settings.Default.SpeechEnabled,
                Margin        = new Padding(0, 0, 0, 2),
            };
            _trkDuration.Scroll += TrkDuration_Scroll;

            _lblDurationVal = new Label
            {
                AutoSize = true,
                Text     = _trkDuration.Value + " seconds",
            };

            panel.Controls.Add(_chkSpeech);
            panel.Controls.Add(lblDesc);
            panel.Controls.Add(lblDurTitle);
            panel.Controls.Add(_trkDuration);
            panel.Controls.Add(_lblDurationVal);

            tabPage5.Controls.Add(panel);
            tabControl1.TabPages.Add(tabPage5);
        }

        private void ChkSpeech_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpeechEnabled = _chkSpeech.Checked;
            Properties.Settings.Default.Save();
            _trkDuration.Enabled = _chkSpeech.Checked;
            ContextMenus.RefreshSpeechMenuItem();
        }

        private void TrkDuration_Scroll(object sender, EventArgs e)
        {
            Properties.Settings.Default.SpeechDuration = _trkDuration.Value;
            Properties.Settings.Default.Save();
            _lblDurationVal.Text = _trkDuration.Value + " seconds";
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            trackBar1.Enabled = checkBox1.Checked;
            if(!trackBar1.Enabled)
            {
                trackBar1.Value = 0;
                trackBar1_Scroll(sender, e);
            }
			else
			{
				Properties.Settings.Default.Save();
			}
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            Properties.Settings.Default.Volume = (float)(trackBar1.Value / 10.0);
            if(Properties.Settings.Default.Volume < 0.1f)
            {
                trackBar1.Enabled = false;
                checkBox1.Checked = false;
            }
			Properties.Settings.Default.Save();
            label6.Text = trackBar1.Value.ToString();
        }

		private void checkBox2_Click(object sender, EventArgs e)
		{
			Properties.Settings.Default.WinForeground = checkBox2.Checked;
			Properties.Settings.Default.Save();
		}

		private void trackBar2_Scroll(object sender, EventArgs e)
		{
			Properties.Settings.Default.AutostartPets = trackBar2.Value;
			Properties.Settings.Default.Save();
            label5.Text = trackBar2.Value.ToString();
		}
	}
}
