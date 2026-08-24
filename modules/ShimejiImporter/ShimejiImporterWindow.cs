using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Modules;
using DesktopPet.Tools.ShimejiConvert;
using DesktopPet.Tools.ShimejiConvert.Emit;
using DesktopPet.Tools.ShimejiConvert.Shimeji;

namespace DesktopPet.ShimejiImporterModule
{
    /// <summary>
    /// The importer window (built in code, no XAML, like Pet Studio). Browse to a skin folder or .zip, see
    /// the honest conversion report, preview the pet on the real desktop, and install it. Never downloads
    /// skins; a small links section points at where the user can find their own.
    /// </summary>
    internal sealed class ShimejiImporterWindow : Window
    {
        private readonly IHost _host;
        private readonly IPetManager _pets;

        private readonly ComboBox _skinCombo = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _pathLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBox _report = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 8, 0, 8),
        };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _previewBtn = new Button { Content = "Preview", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };
        private readonly Button _removeBtn = new Button { Content = "Remove preview", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };
        private readonly Button _installBtn = new Button { Content = "Install", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };
        private readonly Button _saveBtn = new Button { Content = "Save XML…", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };

        private List<DetectedSkin> _skins = new List<DetectedSkin>();
        private ConversionResult _result;
        private IPetPreview _preview;
        private string _extractedTemp;

        public ShimejiImporterWindow(IHost host)
        {
            _host = host;
            _pets = host.GetPetManager("shimejiimporter");

            Title = "Shimeji Importer";
            Width = 760;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(12) };

            // top: source pickers
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var openFolder = new Button { Content = "Open skin folder…", Padding = new Thickness(10, 4, 10, 4) };
            var openZip = new Button { Content = "Open .zip…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            openFolder.Click += delegate { OpenFolder(); };
            openZip.Click += delegate { OpenZip(); };
            top.Children.Add(openFolder);
            top.Children.Add(openZip);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var header = new StackPanel();
            header.Children.Add(_pathLabel);
            _skinCombo.SelectionChanged += delegate { ConvertSelected(); };
            _skinCombo.Visibility = Visibility.Collapsed;
            header.Children.Add(_skinCombo);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // bottom: status + actions + links
            var bottom = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(bottom, Dock.Bottom);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _previewBtn.Click += delegate { Preview(); };
            _removeBtn.Click += delegate { RemovePreview(); };
            _installBtn.Click += delegate { Install(); };
            _saveBtn.Click += delegate { SaveXml(); };
            actions.Children.Add(_previewBtn);
            actions.Children.Add(_removeBtn);
            actions.Children.Add(_installBtn);
            actions.Children.Add(_saveBtn);
            DockPanel.SetDock(actions, Dock.Right);
            DockPanel.SetDock(_status, Dock.Left);
            bottom.Children.Add(actions);
            bottom.Children.Add(_status);
            root.Children.Add(bottom);

            var links = BuildLinks();
            DockPanel.SetDock(links, Dock.Bottom);
            root.Children.Add(links);

            // center: report fills the rest
            root.Children.Add(_report);

            Content = root;
            ApplyTheme();

            _report.Text = "Open a Shimeji skin folder (or a .zip) to convert it.\r\n\r\n" +
                "Sprites-only skins work too: if a skin ships no behaviour config of its own, the bundled " +
                "Shimeji base behaviour is used (Shimeji-EE, BSD-licensed).";
            Closed += delegate { Cleanup(); };
        }

        private UIElement BuildLinks()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(new TextBlock { Text = "Where to find skins (opens in your browser; nothing is downloaded here):", Margin = new Thickness(0, 0, 0, 2) });
            foreach (var pair in new[]
            {
                new[] { "Shimeji-EE (the engine + how skins are packaged)", "https://github.com/gil/shimeji-ee" },
            })
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(pair[0])) { NavigateUri = new Uri(pair[1]) };
                string url = pair[1];
                link.RequestNavigate += delegate { OpenLink(url); };
                var tb = new TextBlock();
                tb.Inlines.Add(link);
                panel.Children.Add(tb);
            }
            return panel;
        }

        private void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { SetStatus("Could not open the link."); }
        }

        private void OpenFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a Shimeji skin folder";
                if (dlg.SelectedPath == "" && _pets != null && !string.IsNullOrEmpty(_pets.PetsDirectory))
                    dlg.SelectedPath = _pets.PetsDirectory;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    LoadSkinRoot(dlg.SelectedPath);
            }
        }

        private void OpenZip()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Shimeji skin zip (*.zip)|*.zip", Title = "Choose a Shimeji skin .zip" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                CleanupExtracted();
                _extractedTemp = Path.Combine(Path.GetTempPath(), "shimeji-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_extractedTemp);
                ZipFile.ExtractToDirectory(dlg.FileName, _extractedTemp);
                LoadSkinRoot(_extractedTemp);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read the zip: " + ex.Message);
            }
        }

        private void LoadSkinRoot(string root)
        {
            RemovePreview();
            _pathLabel.Text = "Skin source: " + root;
            string note;
            _skins = SkinLayout.Detect(root, out note);
            if (_skins.Count == 0)
            {
                _skinCombo.Visibility = Visibility.Collapsed;
                _result = null;
                UpdateButtons();
                _report.Text = "No convertible skin found.\r\n\r\n" + (note ?? "");
                SetStatus("Nothing to convert.");
                return;
            }

            _skinCombo.Items.Clear();
            foreach (DetectedSkin s in _skins) _skinCombo.Items.Add(s.Name);
            _skinCombo.SelectedIndex = 0;
            _skinCombo.Visibility = _skins.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ConvertSelected();
        }

        private void ConvertSelected()
        {
            if (_skins.Count == 0) return;
            int idx = _skinCombo.SelectedIndex;
            if (idx < 0 || idx >= _skins.Count) idx = 0;
            DetectedSkin skin = _skins[idx];

            RemovePreview();
            string error;
            _result = ShimejiEngine.ConvertSkin(skin.ConfDir, skin.ImgDir, skin.Name, out error);
            if (_result == null)
            {
                _report.Text = "Conversion failed:\r\n\r\n" + error;
                SetStatus("Conversion failed.");
                UpdateButtons();
                return;
            }

            var sb = new StringBuilder();
            int anims = _result.Root != null && _result.Root.Animations != null && _result.Root.Animations.Animation != null
                ? _result.Root.Animations.Animation.Length : 0;
            sb.AppendLine(_result.Accepted ? "READY TO INSTALL" : "NOT INSTALLABLE");
            sb.AppendLine(string.Format("{0} animations · valid: {1} · reachable: {2}",
                anims, _result.Valid, _result.Graph != null && _result.Graph.Unreachable.Count == 0));
            if (!_result.Valid && !string.IsNullOrEmpty(_result.Error)) sb.AppendLine("validator: " + _result.Error);
            sb.AppendLine();
            sb.Append(_result.Residue.ToText(skin.Name));
            _report.Text = sb.ToString();

            SetStatus(_result.Accepted ? "Converted. Preview or install." : "Converted, but not installable.");
            UpdateButtons();
        }

        private void Preview()
        {
            if (_result == null || !_result.Accepted || _pets == null) return;
            RemovePreview();
            string error;
            _preview = _pets.SpawnPreview(_result.EmittedXml, out error);
            if (_preview == null) SetStatus("Preview failed: " + error);
            else SetStatus("Previewing on the desktop (temporary — not installed).");
            UpdateButtons();
        }

        private void RemovePreview()
        {
            if (_preview == null) return;
            try { _preview.Remove(); } catch { }
            _preview = null;
            UpdateButtons();
        }

        private void Install()
        {
            if (_result == null || !_result.Accepted || _pets == null) return;
            int idx = _skinCombo.SelectedIndex < 0 ? 0 : _skinCombo.SelectedIndex;
            string id = SafeId(_skins.Count > idx ? _skins[idx].Name : "shimeji");
            string error;
            bool ok = _pets.InstallType(id, _result.EmittedXml, out error);
            SetStatus(ok
                ? "Installed as '" + id + "'. It is now in Options, Pets."
                : "Install failed: " + error);
        }

        private void SaveXml()
        {
            if (_result == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Pet (*.xml)|*.xml", FileName = "animations.xml" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, _result.EmittedXml, new UTF8Encoding(false));
                int idx = _skinCombo.SelectedIndex < 0 ? 0 : _skinCombo.SelectedIndex;
                string name = _skins.Count > idx ? _skins[idx].Name : "skin";
                File.WriteAllText(dlg.FileName + ".residue.txt", _result.Residue.ToText(name), new UTF8Encoding(false));
                SetStatus("Saved " + dlg.FileName + " (+ .residue.txt).");
            }
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message); }
        }

        private void UpdateButtons()
        {
            bool ok = _result != null && _result.Accepted && _pets != null;
            _previewBtn.IsEnabled = ok && _preview == null;
            _removeBtn.IsEnabled = _preview != null;
            _installBtn.IsEnabled = ok;
            _saveBtn.IsEnabled = _result != null;
        }

        private void SetStatus(string s) { _status.Text = s; }

        private static string SafeId(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string id = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(id) ? "shimeji-pet" : id;
        }

        private void ApplyTheme()
        {
            bool dark = false;
            try { dark = _host != null && _host.IsDarkTheme; } catch { dark = false; }
            if (dark)
            {
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
                _report.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
                _report.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            }
        }

        private void Cleanup()
        {
            RemovePreview();
            CleanupExtracted();
        }

        private void CleanupExtracted()
        {
            if (string.IsNullOrEmpty(_extractedTemp)) return;
            try { if (Directory.Exists(_extractedTemp)) Directory.Delete(_extractedTemp, true); } catch { }
            _extractedTemp = null;
        }
    }
}
