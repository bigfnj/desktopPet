using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using DesktopPet.Modules;

namespace DesktopPet.ShimejiImporterModule
{
    /// <summary>
    /// The catalog browse window (built in code, no XAML, like Pet Studio). A grid of curated shimeji, each
    /// with its creator credit and a link to its source, and a Get button that installs the pre-converted pet.
    /// AI-generated entries are tagged and hidden unless the user opts in.
    /// </summary>
    internal sealed class ShimejiCatalogWindow : Window
    {
        private readonly IHost _host;
        private readonly IPetManager _pets;
        private readonly List<CatalogEntry> _entries;
        private readonly WrapPanel _grid = new WrapPanel { Margin = new Thickness(6) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _showAi = new CheckBox { Content = "Show AI-generated", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

        private readonly bool _dark;

        public ShimejiCatalogWindow(IHost host)
        {
            _host = host;
            _pets = host != null ? host.GetPetManager("shimejiimporter") : null;
            try { _dark = host != null && host.IsDarkTheme; } catch { _dark = false; }

            Title = "Shimeji Catalog";
            Width = 840; Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            try { _entries = ShimejiCatalog.LoadEntries(); } catch { _entries = new List<CatalogEntry>(); }

            var root = new DockPanel { Margin = new Thickness(10) };

            var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var header = new StackPanel { Orientation = Orientation.Vertical };
            header.Children.Add(new TextBlock { Text = "Shimeji Catalog", FontWeight = FontWeights.Bold, FontSize = 15 });
            header.Children.Add(new TextBlock
            {
                Text = "Curated shimeji you can install in one click. Each credits its creator.",
                Foreground = Muted(), TextWrapping = TextWrapping.Wrap, FontSize = 11,
            });
            DockPanel.SetDock(header, Dock.Left);
            top.Children.Add(header);
            var filter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _showAi.Checked += delegate { RenderGrid(); };
            _showAi.Unchecked += delegate { RenderGrid(); };
            filter.Children.Add(_showAi);
            DockPanel.SetDock(filter, Dock.Right);
            top.Children.Add(filter);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(_status);
            root.Children.Add(bottom);

            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid });
            Content = root;
            ApplyTheme();

            RenderGrid();
            SetStatus(_pets == null
                ? "No pet service available -- the catalog needs the Pets permission."
                : _entries.Count + " shimeji available.");
        }

        private void RenderGrid()
        {
            _grid.Children.Clear();
            int shown = 0;
            foreach (CatalogEntry e in _entries)
            {
                if (e.AiGenerated && _showAi.IsChecked != true) continue;
                _grid.Children.Add(BuildCard(e));
                shown++;
            }
            if (shown == 0)
                _grid.Children.Add(new TextBlock { Text = "Nothing to show.", Foreground = Muted(), Margin = new Thickness(8) });
        }

        private UIElement BuildCard(CatalogEntry e)
        {
            var card = new Border
            {
                BorderBrush = new SolidColorBrush(_dark ? Color.FromRgb(0x3A, 0x3A, 0x3D) : Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(6),
                Padding = new Thickness(8),
                Width = 180,
            };
            var sp = new StackPanel();

            var thumb = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
            ImageSource icon = TryDecodeIcon(e);
            if (icon != null) thumb.Source = icon;
            sp.Children.Add(thumb);

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(e.Title) ? e.Id : e.Title,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            };
            sp.Children.Add(title);

            string author = string.IsNullOrWhiteSpace(e.Author) ? "shimeji.org" : e.Author;
            sp.Children.Add(new TextBlock
            {
                Text = "by " + author, Foreground = Muted(), FontSize = 11,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });

            if (e.AiGenerated)
                sp.Children.Add(new TextBlock
                {
                    Text = "AI-generated", Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x00)),
                    FontSize = 10, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
                });

            var get = new Button { Content = "Get", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 8, 0, 0), IsEnabled = _pets != null };
            get.Click += delegate { Install(e, get); };
            var getWrap = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            getWrap.Children.Add(get);
            sp.Children.Add(getWrap);

            if (!string.IsNullOrWhiteSpace(e.SourceUrl))
            {
                var link = new Hyperlink(new Run("source")) { };
                string url = e.SourceUrl;
                link.RequestNavigate += delegate { OpenLink(url); };
                link.NavigateUri = SafeUri(url);
                var linkText = new TextBlock { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontSize = 11 };
                linkText.Inlines.Add(link);
                sp.Children.Add(linkText);
            }

            card.Child = sp;
            return card;
        }

        private void Install(CatalogEntry e, Button get)
        {
            if (_pets == null) { SetStatus("No pet service available."); return; }
            string xml = ShimejiCatalog.ReadPetXml(e);
            if (string.IsNullOrEmpty(xml)) { SetStatus("Couldn't read '" + e.Id + "'."); return; }
            string error;
            if (_pets.InstallType(e.Id, xml, out error))
            {
                get.Content = "Installed"; get.IsEnabled = false;
                SetStatus("Installed '" + (string.IsNullOrWhiteSpace(e.Title) ? e.Id : e.Title) + "'. Find it in Options, Pets.");
            }
            else SetStatus("Couldn't install: " + error);
        }

        // The pet's <header><icon> is a base64 ICO/PNG; decode it as a thumbnail without loading the whole
        // (multi-MB) sprite sheet by pulling just the icon element out of the xml text.
        private static ImageSource TryDecodeIcon(CatalogEntry e)
        {
            try
            {
                string xml = ShimejiCatalog.ReadPetXml(e);
                if (string.IsNullOrEmpty(xml)) return null;
                Match m = Regex.Match(xml, "<icon>\\s*(?:<!\\[CDATA\\[)?\\s*([A-Za-z0-9+/=\\s]+?)\\s*(?:\\]\\]>)?\\s*</icon>", RegexOptions.Singleline);
                if (!m.Success) return null;
                byte[] bytes = Convert.FromBase64String(Regex.Replace(m.Groups[1].Value, "\\s+", ""));
                using (var ms = new MemoryStream(bytes, false))
                {
                    BitmapDecoder dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame frame = dec.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch { return null; }
        }

        private void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { SetStatus("Couldn't open the link."); }
        }

        private static Uri SafeUri(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u : new Uri("https://example.invalid");
        }

        private Brush Muted() { return new SolidColorBrush(_dark ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0x70, 0x70, 0x70)); }

        private void ApplyTheme()
        {
            if (_dark)
            {
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            }
        }

        private void SetStatus(string s) { _status.Text = s ?? ""; }
    }
}
