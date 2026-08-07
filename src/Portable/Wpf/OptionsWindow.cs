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
        private readonly IReadOnlyList<ShellPane> _panes;
        private readonly ContentControl _content = new ContentControl();
        private readonly Button _apply;
        private ShellPane _current;

        public OptionsWindow(IReadOnlyList<ShellPane> panes)
        {
            _panes = panes ?? new List<ShellPane>();
            Title = "DesktopPet — Settings";
            // Default large enough for the Pets gallery to reflow to 3 cards across and ~4 rows down
            // (the gallery WrapPanel wraps to fewer columns as the window shrinks). Resizable, with a
            // floor that still fits ~2 columns + the nav.
            Width = 1050;
            Height = 820;
            MinWidth = 700;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfTheme.Apply(this);   // light/dark/system per the user's preference; installs implicit styles

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nav = new ListBox { Margin = new Thickness(6) };
            foreach (ShellPane p in _panes) nav.Items.Add(p != null ? (p.Title ?? "(untitled)") : "(null)");
            nav.SelectionChanged += (s, e) => ShowPane(nav.SelectedIndex);
            Grid.SetColumn(nav, 0);
            grid.Children.Add(nav);

            var right = new DockPanel { Margin = new Thickness(6), LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _apply = new Button { Content = "_Apply", Width = 84, Height = 26, Margin = new Thickness(4) };
            _apply.Click += (s, e) => ApplyCurrent();
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4) };
            close.Click += (s, e) => Close();
            buttons.Children.Add(_apply);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            right.Children.Add(buttons);
            // Each pane supplies its own ScrollViewer (schema panes via PaneView, Pets via its control),
            // so there is no OUTER ScrollViewer for an inner one to nest inside and swallow the mouse wheel.
            right.Children.Add(_content);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            Content = grid;
            if (_panes.Count > 0) nav.SelectedIndex = 0;
        }

        private void ShowPane(int index)
        {
            if (index < 0 || index >= _panes.Count) { _content.Content = null; _current = null; if (_apply != null) _apply.Visibility = Visibility.Collapsed; return; }
            _current = _panes[index];
            FrameworkElement content;
            try { content = _current.BuildContent(); }
            catch (Exception ex) { content = new TextBlock { Text = "This pane failed to load: " + ex.Message, Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap }; }
            _content.Content = content;
            // Schema panes have an Apply; custom panes (Pets/Fortunes) apply through their own controls.
            if (_apply != null) _apply.Visibility = _current.HasApply ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyCurrent()
        {
            if (_current == null || !_current.HasApply) return;
            bool ok;
            try { ok = _current.Apply(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ok)
                MessageBox.Show(this, "These settings could not be saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>A section in the settings window: either a schema-driven module pane (<see cref="SchemaShellPane"/>)
    /// or a host-built custom control (<see cref="CustomShellPane"/> — the Pets gallery, the Fortunes tree).
    /// Host-only; the plugin ABI stays schema-only + framework-agnostic (no WPF types leak into it).</summary>
    internal abstract class ShellPane
    {
        public abstract string Title { get; }
        public abstract FrameworkElement BuildContent();
        public virtual bool HasApply { get { return false; } }
        public virtual bool Apply() { return true; }
    }

    /// <summary>Wraps a plugin-ABI OptionsPane, rendered by the schema PaneView.</summary>
    internal sealed class SchemaShellPane : ShellPane
    {
        private readonly OptionsPane _pane;
        private PaneView _view;
        public SchemaShellPane(OptionsPane pane) { _pane = pane; }
        public override string Title { get { return _pane != null ? (_pane.Title ?? "(untitled)") : "(null)"; } }
        public override FrameworkElement BuildContent() { _view = new PaneView(_pane); return _view.Build(); }
        public override bool HasApply { get { return _pane != null && _pane.Save != null; } }
        public override bool Apply() { return _view != null && _view.Save(); }
    }

    /// <summary>A host-built pane that supplies its own WPF control (applies through its own buttons).</summary>
    internal sealed class CustomShellPane : ShellPane
    {
        private readonly string _title;
        private readonly Func<FrameworkElement> _build;
        public CustomShellPane(string title, Func<FrameworkElement> build) { _title = title; _build = build; }
        public override string Title { get { return _title ?? "(untitled)"; } }
        public override FrameworkElement BuildContent() { return _build != null ? _build() : new TextBlock(); }
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

            // Action buttons (S5b): the schema is data-only, so things a module DOES (test a connection,
            // clear history, ...) render here as async buttons with a status line.
            IReadOnlyList<PaneAction> actions = _pane != null ? _pane.Actions : null;
            if (actions != null && actions.Count > 0)
            {
                panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 6) });
                foreach (PaneAction a in actions)
                {
                    if (a == null || a.InvokeAsync == null) continue;
                    panel.Children.Add(BuildActionRow(a));
                }
            }
            // Own ScrollViewer so the pane scrolls (incl. the mouse wheel) without an outer one to nest in.
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel,
            };
        }

        private static FrameworkElement BuildActionRow(PaneAction action)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var btn = new Button { Content = action.Label ?? "Run", Width = 150, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(btn, Dock.Left);
            var status = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            btn.Click += async delegate
            {
                btn.IsEnabled = false;
                status.Text = "working…";
                string result;
                try { result = await action.InvokeAsync() ?? ""; }
                catch (Exception ex) { result = "failed: " + ex.Message; }
                status.Text = result;
                btn.IsEnabled = true;
            };
            row.Children.Add(btn);
            row.Children.Add(status);
            return row;
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
