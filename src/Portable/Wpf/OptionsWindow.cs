using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DesktopPet.Modules;

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Programmatic (no-XAML) WPF settings / module-manager window (S5b). Renders one left-nav section per
    /// <see cref="OptionsPane"/> — the core Preferences pane plus each module's schema-driven pane — with an
    /// Apply that round-trips values through the pane's own Load/Save (so a module persists to its own store).
    /// The pet stays WinForms; this window is shown modally from the WinForms UI thread. No XAML keeps the
    /// build/packaging unchanged (no BAML/resource wiring); the renderer lives in <see cref="PaneView"/>,
    /// which is constructable headlessly (STA) so the schema-render + Load/Save round-trip is self-testable.
    /// </summary>
    internal sealed class OptionsWindow : Window
    {
        private readonly IReadOnlyList<OptionsPane> _panes;
        private readonly ContentControl _content = new ContentControl();
        private PaneView _current;

        public OptionsWindow(IReadOnlyList<OptionsPane> panes)
        {
            _panes = panes ?? new List<OptionsPane>();
            Title = "DesktopPet — Settings";
            Width = 660;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nav = new ListBox { Margin = new Thickness(6) };
            foreach (OptionsPane p in _panes) nav.Items.Add(p != null ? (p.Title ?? "(untitled)") : "(null)");
            nav.SelectionChanged += (s, e) => ShowPane(nav.SelectedIndex);
            Grid.SetColumn(nav, 0);
            grid.Children.Add(nav);

            var right = new DockPanel { Margin = new Thickness(6), LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = new Button { Content = "_Apply", Width = 84, Height = 26, Margin = new Thickness(4) };
            apply.Click += (s, e) => ApplyCurrent();
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4) };
            close.Click += (s, e) => Close();
            buttons.Children.Add(apply);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            right.Children.Add(buttons);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content };
            right.Children.Add(scroll);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            Content = grid;
            if (_panes.Count > 0) nav.SelectedIndex = 0;
        }

        private void ShowPane(int index)
        {
            if (index < 0 || index >= _panes.Count) { _content.Content = null; _current = null; return; }
            _current = new PaneView(_panes[index]);
            _content.Content = _current.Build();
        }

        private void ApplyCurrent()
        {
            if (_current == null) return;
            bool ok;
            try { ok = _current.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ok)
                MessageBox.Show(this, "These settings could not be saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Renders one <see cref="OptionsPane"/>'s schema into WPF controls and collects/persists edited values
    /// via the pane's Load/Save. Kept separate + headless-constructable (STA) so the render + Load/Save
    /// round-trip is unit-testable without showing a window. Secret fields are write-only: the box starts
    /// empty (a "leave blank to keep the current one" hint when a secret is already set) and only a
    /// non-empty entry is sent back on Save.
    /// </summary>
    internal sealed class PaneView
    {
        private readonly OptionsPane _pane;
        private readonly Dictionary<string, Func<string>> _readers = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _secretIds = new HashSet<string>(StringComparer.Ordinal);

        public PaneView(OptionsPane pane) { _pane = pane; }

        public FrameworkElement Build()
        {
            _readers.Clear();
            _secretIds.Clear();

            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new TextBlock
            {
                Text = _pane != null ? (_pane.Title ?? "") : "",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            IReadOnlyDictionary<string, string> values = null;
            try { if (_pane != null && _pane.Load != null) values = _pane.Load(); } catch { values = null; }
            if (values == null) values = new Dictionary<string, string>();

            IReadOnlyList<SettingField> schema = _pane != null ? _pane.Schema : null;
            if (schema != null)
            {
                foreach (SettingField f in schema)
                {
                    if (f == null || string.IsNullOrEmpty(f.Id)) continue;
                    string cur;
                    if (!values.TryGetValue(f.Id, out cur)) cur = "";
                    panel.Children.Add(BuildRow(f, cur ?? ""));
                }
            }
            return panel;
        }

        private FrameworkElement BuildRow(SettingField f, string cur)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var label = new TextBlock { Text = f.Label ?? f.Id, Width = 210, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            switch (f.Kind)
            {
                case SettingKind.Bool:
                {
                    var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsChecked = ParseBool(cur) };
                    _readers[f.Id] = () => (cb.IsChecked == true) ? "true" : "false";
                    row.Children.Add(cb);
                    break;
                }
                case SettingKind.Enum:
                {
                    var combo = new ComboBox();
                    if (f.Options != null) foreach (string o in f.Options) combo.Items.Add(o);
                    combo.SelectedItem = cur;
                    _readers[f.Id] = () => combo.SelectedItem as string ?? "";
                    row.Children.Add(combo);
                    break;
                }
                case SettingKind.Secret:
                {
                    var pw = new PasswordBox();
                    bool alreadySet = !string.IsNullOrEmpty(cur);
                    if (alreadySet) pw.ToolTip = "A value is saved. Leave blank to keep it.";
                    _secretIds.Add(f.Id);
                    _readers[f.Id] = () => pw.Password ?? "";
                    row.Children.Add(pw);
                    break;
                }
                default: // Int + Text both edit as text (Int is validated by the module on Save).
                {
                    var tb = new TextBox { Text = cur };
                    _readers[f.Id] = () => tb.Text ?? "";
                    row.Children.Add(tb);
                    break;
                }
            }
            return row;
        }

        /// <summary>Collect the edited values and hand them to the pane's Save. A Secret is included only when
        /// the user typed something (blank keeps the stored value). Returns true if there's nothing to save.</summary>
        public bool Save()
        {
            if (_pane == null || _pane.Save == null) return true;
            return _pane.Save(Collect());
        }

        /// <summary>The values that would be sent to Save (also the self-test hook).</summary>
        internal Dictionary<string, string> Collect()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Func<string>> reader in _readers)
            {
                string v;
                try { v = reader.Value() ?? ""; } catch { v = ""; }
                if (_secretIds.Contains(reader.Key) && string.IsNullOrEmpty(v)) continue; // blank secret => keep stored
                values[reader.Key] = v;
            }
            return values;
        }

        private static bool ParseBool(string s) { bool b; return bool.TryParse(s, out b) && b; }
    }
}
