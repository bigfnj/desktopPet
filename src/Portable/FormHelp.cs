using System.Windows.Forms;

namespace DesktopPet
{
        /// <summary>
        /// Offline help form. Online documentation opens only after an explicit user action.
        /// </summary>
    public partial class FormHelp : Form
    {
            /// <summary>
            /// Constructor. Initialize components.
            /// </summary>
        public FormHelp()
        {
            InitializeComponent();
            helpText.Text =
                "DesktopPet AI Edition\r\n\r\n" +
                "Move and dismiss the pet\r\n" +
                "• Drag the pet with the mouse to reposition it.\r\n" +
                "• Right-click the pet to poke it; right-click the tray icon for actions, " +
                "Options, and Exit.\r\n\r\n" +
                "Fortunes and AI\r\n" +
                "• Fortunes and smart matching work locally.\r\n" +
                "• The optional AI brain is off until you configure and enable it.\r\n" +
                "• Review the Privacy notice before sending screen context to a provider.\r\n\r\n" +
                "Portable and installed data\r\n" +
                "• Portable ZIP copies keep data beside DesktopPet.exe in the data folder.\r\n" +
                "• MSI installs keep mutable data under %LOCALAPPDATA%\\DesktopPet.\r\n\r\n" +
                "Current HTTPS documentation (opens only when clicked)\r\n" +
                "Privacy: https://github.com/bigfnj/desktopPet/blob/master/PRIVACY.md\r\n" +
                "Support: https://github.com/bigfnj/desktopPet/blob/master/SUPPORT.md\r\n" +
                "Security: https://github.com/bigfnj/desktopPet/blob/master/SECURITY.md\r\n" +
                "Pet authoring: https://github.com/bigfnj/desktopPet/blob/master/grimoire/03-pet-xml-format.md\r\n" +
                "Fortune packs: https://github.com/bigfnj/desktopPet/blob/master/packs/README.md\r\n" +
                "Release status: https://github.com/bigfnj/desktopPet/blob/master/docs/RELEASE-CHECKLIST.md";
        }

        private void OnlineDocumentationLink_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e)
        {
            OpenDocumentationUrl("https://github.com/bigfnj/desktopPet#readme");
        }

        private void HelpText_LinkClicked(
            object sender,
            LinkClickedEventArgs e)
        {
            OpenDocumentationUrl(e.LinkText);
        }

        private void OpenDocumentationUrl(string documentationUrl)
        {
            try
            {
                System.Uri uri;
                if (!System.Uri.TryCreate(
                        documentationUrl,
                        System.UriKind.Absolute,
                        out uri) ||
                    !string.Equals(
                        uri.Scheme,
                        System.Uri.UriSchemeHttps,
                        System.StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        uri.Host,
                        "github.com",
                        System.StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith(
                        "/bigfnj/desktopPet",
                        System.StringComparison.OrdinalIgnoreCase))
                    throw new System.InvalidOperationException(
                        "Only this project's HTTPS documentation can be opened.");

                using (System.Diagnostics.Process process =
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                        {
                            UseShellExecute = true
                        }))
                {
                    if (process == null)
                        throw new System.InvalidOperationException(
                            "Windows did not start a browser process.");
                }
            }
            catch (System.Exception exception)
            {
                MessageBox.Show(
                    this,
                    "The default browser could not be opened.\r\n\r\n" +
                    documentationUrl + "\r\n\r\n" + exception.Message,
                    "DesktopPet Help",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
