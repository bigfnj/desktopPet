using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Programmatic (no-XAML) WPF About window — the themed replacement for the retired WinForms
    /// <c>AboutBox</c>. Built the same way as <see cref="OptionsWindow"/> (Grid/DockPanel/StackPanel/TextBlock,
    /// explicit sizing, <see cref="WpfTheme.Apply"/> for the dark title bar + implicit control styles), shown
    /// modally from the WinForms UI thread. Shows the running build version (in the title), fixed project /
    /// upstream links, and the active pet's author/title/version/info. The pet's <c>info</c> supports the same
    /// markup the old AboutBox did — <c>[br]</c> → line break, <c>[link:https://…]</c> → clickable link — parsed
    /// here into WPF inline runs + hyperlinks that route through <see cref="WebLinks.TryOpen"/>. Read-only + a
    /// plain Close button (the old "Cancel = SyncSheeps()" behaviour is intentionally dropped).
    /// </summary>
    internal sealed class AboutWindow : Window
    {
        // Match the AboutBox's info cap so a pathological pet payload can't build an unbounded inline run.
        private const int InfoMaxChars = 8192;

        private readonly Brush _linkBrush = WpfHyperlinks.LinkBrush();

        public AboutWindow(string author, string title, string version, string info)
        {
            Title = "About DesktopPet AI Edition — Version " +
                System.Windows.Forms.Application.ProductVersion;
            Width = 480;
            Height = 420;
            MinWidth = 380;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfTheme.Apply(this);   // light/dark/system per preference; installs implicit styles + dark title bar

            var root = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

            // Bottom bar: a single Close button on the right (mirrors OptionsWindow's chrome).
            var bottomBar = new DockPanel { LastChildFill = false };
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4), IsCancel = true };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            bottomBar.Children.Add(close);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(bottomBar);

            var stack = new StackPanel { Margin = new Thickness(2, 2, 2, 8) };

            stack.Children.Add(Line("Original concept and artwork by Tatsutoshi Nomura"));
            stack.Children.Add(Line("C# Application code by Adriano Petrucci"));
            stack.Children.Add(Line("System Tray implementation by Sergi Fumanya Grunwaldt"));
            stack.Children.Add(LinkLine("Sounds played through NAudio (Open Source): ", "https://github.com/naudio/NAudio", false));
            stack.Children.Add(new TextBlock { Height = 8 });
            stack.Children.Add(LinkLine("For more information, please visit: ", "https://esheep.petrucci.ch", false));
            stack.Children.Add(LinkLine("Open Source project is hosted on: ", "https://github.com/bigfnj/desktopPet", false));

            stack.Children.Add(new TextBlock
            {
                Text = "Information about the current pet:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 12, 0, 6),
            });
            stack.Children.Add(BuildPetInfoCard(author, title, version, info));

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            });

            Content = root;
        }

        private static TextBlock Line(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        }

        // A line of prose ending in a single clickable link.
        private TextBlock LinkLine(string prefix, string url, bool projectDoc)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            if (!string.IsNullOrEmpty(prefix)) tb.Inlines.Add(new Run(prefix));
            tb.Inlines.Add(WpfHyperlinks.Link(url, url, projectDoc, _linkBrush));
            return tb;
        }

        // The active pet's author/title/version + its info text (with [br]/[link:] markup parsed to inlines),
        // wrapped in the same bordered card chrome OptionsWindow uses (theme-neutral in light + dark).
        private Border BuildPetInfoCard(string author, string title, string version, string info)
        {
            var inner = new StackPanel();
            inner.Children.Add(LabeledRow("Author:", author));
            inner.Children.Add(LabeledRow("Title:", title));
            inner.Children.Add(LabeledRow("Version:", version));
            inner.Children.Add(new TextBlock { Text = "Info:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) });

            var infoBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AppendInfoInlines(infoBlock, info);
            inner.Children.Add(infoBlock);

            return new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(8),
                Child = inner,
            };
        }

        private static DockPanel LabeledRow(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
            var l = new TextBlock { Text = label, FontWeight = FontWeights.Bold, Width = 60, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            row.Children.Add(new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap });
            return row;
        }

        /// <summary>
        /// Parse the pet's <c>info</c> into WPF inlines, honouring the AboutBox markup: <c>[br]</c> becomes a
        /// line break and <c>[link:https://…]</c> becomes a clickable hyperlink (the link text is the URL).
        /// Truncated at a code-point boundary to the same 8192-char cap the old AboutBox used.
        /// </summary>
        private void AppendInfoInlines(TextBlock target, string info)
        {
            info = UnicodeTextProgress.TruncateAtCodePointBoundary(info ?? "", InfoMaxChars);

            var pending = new StringBuilder();
            int i = 0;
            while (i < info.Length)
            {
                if (MatchAt(info, i, "[br]"))
                {
                    FlushRun(target, pending);
                    target.Inlines.Add(new LineBreak());
                    i += 4;
                    continue;
                }
                if (MatchAt(info, i, "[link:"))
                {
                    int close = info.IndexOf(']', i + 6);
                    if (close >= 0)
                    {
                        string url = info.Substring(i + 6, close - (i + 6));
                        FlushRun(target, pending);
                        target.Inlines.Add(WpfHyperlinks.Link(url, url, false, _linkBrush));
                        i = close + 1;
                        continue;
                    }
                }
                pending.Append(info[i]);
                i++;
            }
            FlushRun(target, pending);
        }

        private static void FlushRun(TextBlock target, StringBuilder pending)
        {
            if (pending.Length == 0) return;
            target.Inlines.Add(new Run(pending.ToString()));
            pending.Clear();
        }

        private static bool MatchAt(string text, int index, string token)
        {
            return index + token.Length <= text.Length &&
                string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }

    /// <summary>
    /// Shared hyperlink factory for the WPF About/Help windows: a theme-aware link brush and an inline
    /// <see cref="Hyperlink"/> whose navigation is routed through the security-reviewed <see cref="WebLinks"/>
    /// (arbitrary HTTPS, or the project-doc allowlist). A URL that isn't a well-formed absolute URI renders as
    /// plain text (no live link) so a malformed pet payload can't throw while the window builds.
    /// </summary>
    internal static class WpfHyperlinks
    {
        public static Brush LinkBrush()
        {
            string mode = "system";
            try { if (Program.MyData != null) mode = Program.MyData.GetThemeMode(); } catch { }
            bool dark = WpfTheme.EffectiveDark(mode);
            // Readable link colour on each background: a bright blue on the dark surface, a deeper blue on light.
            Color c = dark ? Color.FromRgb(0x6C, 0xB6, 0xFF) : Color.FromRgb(0x0A, 0x4A, 0xA6);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Inline Link(string text, string url, bool projectDoc, Brush brush)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return new Run(text);   // not a live link, but still shown verbatim

            var link = new Hyperlink(new Run(text)) { NavigateUri = uri };
            if (brush != null) link.Foreground = brush;
            string captured = url;
            link.RequestNavigate += delegate(object sender, RequestNavigateEventArgs e)
            {
                if (projectDoc) WebLinks.TryOpenProjectDoc(captured);
                else WebLinks.TryOpen(captured);
                e.Handled = true;
            };
            return link;
        }
    }
}
