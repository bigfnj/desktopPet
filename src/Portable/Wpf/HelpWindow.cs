using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Programmatic (no-XAML) WPF Help window — the themed replacement for the retired WinForms <c>FormHelp</c>.
    /// Built like <see cref="OptionsWindow"/> (DockPanel/StackPanel/TextBlock, explicit sizing,
    /// <see cref="WpfTheme.Apply"/>), shown modally from the WinForms UI thread. Reproduces the offline help
    /// text verbatim; the embedded HTTPS documentation URLs are detected and rendered as clickable hyperlinks
    /// that route through <see cref="WebLinks.TryOpenProjectDoc"/>, which enforces the same
    /// github.com/bigfnj/desktopPet allowlist FormHelp did (a link only opens on an explicit click). Read-only
    /// + a plain Close button.
    /// </summary>
    internal sealed class HelpWindow : Window
    {
        // The offline help text, copied verbatim from the retired FormHelp. The embedded https URLs are turned
        // into clickable, allowlisted hyperlinks below; everything else is plain text.
        private const string HelpText =
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

        private readonly Brush _linkBrush = WpfHyperlinks.LinkBrush();

        public HelpWindow()
        {
            Title = "DesktopPet Help";
            Width = 640;
            Height = 560;
            MinWidth = 460;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfTheme.Apply(this);   // light/dark/system per preference; installs implicit styles + dark title bar

            var root = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

            var bottomBar = new DockPanel { LastChildFill = false };
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4), IsCancel = true };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            bottomBar.Children.Add(close);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(bottomBar);

            var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) };
            AppendHelpInlines(body, HelpText);

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            });

            Content = root;
        }

        // Render the help text line by line (line breaks become WPF LineBreaks), turning each embedded
        // https:// token into an allowlisted, clickable hyperlink and leaving all other text plain.
        private void AppendHelpInlines(TextBlock target, string text)
        {
            string[] lines = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) target.Inlines.Add(new LineBreak());
                AppendLineWithLinks(target, lines[li]);
            }
        }

        private void AppendLineWithLinks(TextBlock target, string line)
        {
            var pending = new StringBuilder();
            int i = 0;
            while (i < line.Length)
            {
                int h = line.IndexOf("https://", i, StringComparison.OrdinalIgnoreCase);
                if (h < 0)
                {
                    pending.Append(line, i, line.Length - i);
                    break;
                }
                if (h > i) pending.Append(line, i, h - i);
                Flush(target, pending);

                int end = h;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                string url = line.Substring(h, end - h);
                target.Inlines.Add(WpfHyperlinks.Link(url, url, true, _linkBrush));   // project-doc allowlist
                i = end;
            }
            Flush(target, pending);
        }

        private static void Flush(TextBlock target, StringBuilder pending)
        {
            if (pending.Length == 0) return;
            target.Inlines.Add(new Run(pending.ToString()));
            pending.Clear();
        }
    }
}
