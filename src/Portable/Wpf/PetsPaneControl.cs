using System;
using System.Collections.Generic;
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
                Dictionary<string, int> mix = BuildMixDict();
                foreach (PetRow row in _pets.State.Installed)
                    _grid.Children.Add(BuildCard(row, mix));
            }
            catch (Exception ex) { _status.Text = "Couldn't list pets: " + ex.Message; }
        }

        // The live on-screen mix (id -> count). The active/default type's pets are keyed "" (see StartUp.OnScreenMix).
        private static Dictionary<string, int> BuildMixDict()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mix = Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
                if (mix != null)
                    foreach (PetCountEntry e in mix)
                    {
                        string id = e.Id ?? "";
                        int c; d.TryGetValue(id, out c); d[id] = c + e.Count;
                    }
            }
            catch { }
            return d;
        }

        private FrameworkElement BuildCard(PetRow row, Dictionary<string, int> mix)
        {
            string addId = row.IsBuiltIn ? PetCatalog.BuiltInPetId : (row.Id ?? "");
            int onScreen = 0, c;
            if (mix.TryGetValue(addId, out c)) onScreen += c;
            int defaultCount = 0;                      // the active type's pets are keyed "" in the mix
            if (row.IsActive && mix.TryGetValue("", out c)) defaultCount = c;
            onScreen += defaultCount;

            var card = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4), Padding = new Thickness(6), Width = 224 };
            var sp = new StackPanel();

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            ImageSource img = LoadThumb(addId);
            if (img == null && row.IsBuiltIn) img = LoadAppIcon();   // the default eSheep isn't in the thumbnail zip
            if (img != null) top.Children.Add(new Image { Source = img, Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 0) });
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock { Text = row.DisplayName ?? row.Id, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (onScreen > 0)
                nameStack.Children.Add(new TextBlock { Text = (row.IsActive ? "active · " : "") + "on screen: " + onScreen, FontSize = 11, Foreground = Brushes.ForestGreen });
            top.Children.Add(nameStack);
            sp.Children.Add(top);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (!row.IsActive)
            {
                var use = new Button { Content = "Use", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
                use.Click += delegate { _status.Text = _pets.UsePet(addId).Ok ? (row.DisplayName + " is now your pet.") : "Couldn't apply that pet."; Reload(); };
                btns.Children.Add(use);
            }
            var add = new Button { Content = "Add", Width = 48, Margin = new Thickness(0, 0, 5, 0) };
            add.Click += delegate { _status.Text = _pets.AddPet(addId).Ok ? ("Added " + row.DisplayName + ".") : "Couldn't add (max pets reached?)."; Reload(); };
            btns.Children.Add(add);
            if (onScreen > 0)
            {
                string removeId = defaultCount > 0 ? "" : addId;   // remove one of this type (active default = "")
                var remove = new Button { Content = "Remove", Width = 66 };
                remove.Click += delegate
                {
                    try { if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(removeId); } catch { }
                    _status.Text = "Removed one " + row.DisplayName + ".";
                    Reload();
                };
                btns.Children.Add(remove);
            }
            sp.Children.Add(btns);

            card.Child = sp;
            return card;
        }

        private static ImageSource LoadThumb(string id)
        {
            try
            {
                return FromPng(PetThumbnails.GetPng(id));
            }
            catch { return null; }
        }

        /// <summary>Fallback thumbnail for the built-in eSheep (not present in the thumbnail zip): the app icon.</summary>
        private static ImageSource LoadAppIcon()
        {
            try
            {
                using (var bmp = DesktopPet.Properties.Resources.icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return FromPng(ms.ToArray());
                }
            }
            catch { return null; }
        }

        private static ImageSource FromPng(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png, false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
