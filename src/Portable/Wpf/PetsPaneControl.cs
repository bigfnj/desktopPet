using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Options;   // PetsController, PetRow, IPetRuntime

namespace DesktopPet.Wpf
{
    /// <summary>
    /// Host-built Pets gallery for the WPF settings window (S5b-2c): a card per installed pet (thumbnail +
    /// name + Use/Add + an Active marker), backed by the base <see cref="PetsController"/>. Local pets only
    /// for now; the online "get more pets" catalog is a follow-on (it needs an ICatalogService). Use/Add
    /// apply immediately through the runtime, so this pane has no separate Apply button.
    /// </summary>
    internal sealed class PetsPaneControl : ContentControl
    {
        private readonly PetsController _pets;
        private readonly WrapPanel _grid = new WrapPanel { Margin = new Thickness(4) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 4, 0, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

        public PetsPaneControl()
        {
            _pets = new PetsController(Program.Mainthread as IPetRuntime, null);

            var root = new DockPanel { LastChildFill = true };
            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock { Text = "Pets", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            header.Children.Add(new TextBlock { Text = "Pick a look for your pet. “Use” replaces the current pet; “Add” spawns one alongside.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid });
            Content = root;

            Reload();
        }

        private void Reload()
        {
            _grid.Children.Clear();
            try
            {
                _pets.Load();
                foreach (PetRow row in _pets.State.Installed)
                    _grid.Children.Add(BuildCard(row));
            }
            catch (Exception ex) { _status.Text = "Couldn't list pets: " + ex.Message; }
        }

        private FrameworkElement BuildCard(PetRow row)
        {
            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 210 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(row.IsBuiltIn ? PetCatalog.BuiltInPetId : row.Id);
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            top.Children.Add(new TextBlock { Text = row.DisplayName ?? row.Id, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(top);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            string petId = row.IsBuiltIn ? PetCatalog.BuiltInPetId : row.Id;
            if (row.IsActive)
            {
                btns.Children.Add(new TextBlock { Text = "✓ Active", Foreground = Brushes.ForestGreen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            }
            else
            {
                var use = new Button { Content = "Use", Width = 62, Margin = new Thickness(0, 0, 6, 0) };
                use.Click += delegate { _status.Text = _pets.UsePet(petId).Ok ? (row.DisplayName + " is now your pet.") : "Couldn't apply that pet."; Reload(); };
                btns.Children.Add(use);
            }
            var add = new Button { Content = "Add", Width = 62 };
            add.Click += delegate { _status.Text = _pets.AddPet(petId).Ok ? ("Added " + row.DisplayName + ".") : "Couldn't add (max pets reached?)."; };
            btns.Children.Add(add);
            sp.Children.Add(btns);

            card.Child = sp;
            return card;
        }

        private static ImageSource LoadThumb(string id)
        {
            try
            {
                byte[] png = PetThumbnails.GetPng(id);
                if (png == null) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(png, false);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
