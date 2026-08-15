using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Modules;

namespace DesktopPet.PetStudioModule
{
    /// <summary>
    /// The studio window. Built in code rather than XAML to match the host's own WPF panes, and because a
    /// module with a .xaml would need its own build/resource plumbing for one window.
    ///
    /// The flow is deliberately linear, because it mirrors what a pet author actually does: open an XML,
    /// read what is wrong with it, watch it move, then install it. Nothing here decides anything — analysis
    /// is PetAnalyzer's job and every pet operation goes through IPetManager, so this file is only layout
    /// plus wiring.
    /// </summary>
    internal sealed class PetStudioWindow : Window
    {
        private readonly IHost _host;
        private readonly IPetManager _pets;

        private readonly TextBlock _path = new TextBlock { Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBox _report = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            MinHeight = 220,
        };
        private readonly TextBlock _status = new TextBlock { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        private readonly Button _previewButton = new Button { Content = "Preview on my desktop", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
        private readonly Button _removeButton = new Button { Content = "Remove preview", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _installButton = new Button { Content = "Install this pet…", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBox _installId = new TextBox { Width = 150, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        private string _xml;
        private IPetPreview _preview;

        internal PetStudioWindow(IHost host)
        {
            _host = host;
            _pets = host != null ? host.GetPetManager("petstudio") : null;

            Title = "Pet Studio";
            Width = 720;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };

            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(new TextBlock
            {
                Text = "Open a pet's animations.xml to check it before you use it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var openRow = new StackPanel { Orientation = Orientation.Horizontal };
            var openButton = new Button { Content = "Open animations.xml…", Padding = new Thickness(10, 3, 10, 3) };
            openButton.Click += delegate { OpenFile(); };
            openRow.Children.Add(openButton);
            openRow.Children.Add(new TextBlock { Text = "  ", Width = 8 });
            _path.VerticalAlignment = VerticalAlignment.Center;
            openRow.Children.Add(_path);
            top.Children.Add(openRow);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            _previewButton.Click += delegate { Preview(); };
            _removeButton.Click += delegate { RemovePreview(); };
            _installButton.Click += delegate { Install(); };
            buttons.Children.Add(_previewButton);
            buttons.Children.Add(_removeButton);
            buttons.Children.Add(new TextBlock
            {
                Text = "install as:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                Foreground = Brushes.Gray,
            });
            buttons.Children.Add(_installId);
            buttons.Children.Add(_installButton);
            bottom.Children.Add(buttons);
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            root.Children.Add(_report);
            Content = root;

            if (_pets == null)
                SetStatus("No pet service available — Pet Studio needs the Pets permission.");
            else
                SetStatus("Nothing loaded yet.");

            // A preview is owned by this window, so it must not outlive it on the user's desktop.
            Closed += delegate { RemovePreview(); };
        }

        private void OpenFile()
        {
            try
            {
                IReadOnlyList<string> picked = _host.PickFilesToOpen(
                    "Open a pet's animations.xml", "Pet XML", new[] { "xml" });
                if (picked == null || picked.Count == 0) return;

                string path = picked[0];
                _xml = File.ReadAllText(path);
                _path.Text = path;
                _installId.Text = SuggestId(path);
                Analyze();
            }
            catch (Exception ex)
            {
                _report.Text = "";
                SetStatus("Couldn't read that file: " + ex.Message);
            }
        }

        private void Analyze()
        {
            PetReport report = PetAnalyzer.Analyze(_xml);
            _report.Text = report.Describe();

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

        private void Preview()
        {
            RemovePreview();
            string error;
            _preview = _pets.SpawnPreview(_xml, out error);
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
            if (_pets.InstallType(id, _xml, out error))
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
