using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace DesktopPet
{
        /// <summary>
        /// Information about application and current XML animation file
        /// </summary>
    public partial class AboutBox : Form
    {
            /// <summary>
            /// Initialize form and get application version
            /// </summary>
        public AboutBox()
        {
            InitializeComponent();

            string version = Application.ProductVersion;
            Text = Text.Replace("XXX", version);
        }

            /// <summary>
            /// Called from parent to fill all labels on the form
            /// </summary>
            /// <param name="author">Author of the XML animation</param>
            /// <param name="title">Title of the animation (got from XML file)</param>
            /// <param name="version">Animation version (got from XML file)</param>
            /// <param name="info">Animation infos (got from XML file). Contains author and copyright information.</param>
            /// <remarks>In the info, you can't use HTML tags. But you can use:
            /// [br] to add a line break 
            /// [link:https://...] to add a clickable HTTPS link
            /// </remarks>
        public void FillData(string author, string title, string version, string info)
        {
            info = (info ?? "").Replace("[br]", "\n");
            int replacements = 0;
            while (replacements++ < 64)
            {
                int iPos = info.IndexOf("[link:", StringComparison.OrdinalIgnoreCase);
                if (iPos < 0) break;
                int close = info.IndexOf("]", iPos + 6, StringComparison.Ordinal);
                if (close < 0) break;
                string link = info.Substring(iPos + 6, close - iPos - 6);
                info = info.Substring(0, iPos) + link + info.Substring(close + 1);
            }

            label_author.Text = author ?? "";
            label_title.Text = title ?? "";
            label_version.Text = version ?? "";
            richTextBox1.Text =
                UnicodeTextProgress.TruncateAtCodePointBoundary(info, 8192);
        }

            /// <summary>
            /// OK was pressed. Close About dialog.
            /// </summary>
            /// <param name="sender">Caller object</param>
            /// <param name="e">Events</param>
        private void Button_ok_Click(object sender, EventArgs e)
        {
            Close();
        }

            /// <summary>
            /// https://esheep.petrucci.ch was pressed, a webpage with this link will be opened
            /// </summary>
            /// <param name="sender">Caller object</param>
            /// <param name="e">Information about the link click event</param>
        private void LinkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            TryOpenWebLink("https://esheep.petrucci.ch");
        }

            /// <summary>
            /// Cancel was pressed. Synchronize pets and close about dialog.
            /// </summary>
            /// <param name="sender">Caller object</param>
            /// <param name="e">Click events</param>
        private void Button2_Click(object sender, EventArgs e)
        {
            Program.Mainthread.SyncSheeps();
            Close();
        }

            /// <summary>
            /// The DesktopPet AI Edition repository link was pressed.
            /// </summary>
            /// <param name="sender">Caller object</param>
            /// <param name="e">Information about the link click event</param>
        private void LinkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            TryOpenWebLink("https://github.com/bigfnj/desktopPet");
        }

            /// <summary>
            /// Link on the richTextbox was pressed. Open it in the browser.
            /// </summary>
            /// <param name="sender">Caller as object</param>
            /// <param name="e">Information about the link click event</param>
        private void RichTextBox1_LinkClicked(object sender, LinkClickedEventArgs e)
        {
            TryOpenWebLink(e.LinkText);
        }

        private static void TryOpenWebLink(string value)
        {
            try
            {
                string normalized;
                if (!TryNormalizeHttpsLink(
                        value,
                        out normalized))
                    return;

                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = normalized,
                    UseShellExecute = true
                }))
                {
                }
            }
            catch
            {
                // An unavailable browser or rejected URL must not affect the pet runtime.
            }
        }

        internal static bool TryNormalizeHttpsLink(
            string value,
            out string normalized)
        {
            normalized = null;
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 2048 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                return false;
            normalized = uri.AbsoluteUri;
            return true;
        }
    }
}
