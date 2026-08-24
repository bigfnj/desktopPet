using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DesktopPet.Modules;
using DesktopPet.Tools.ShimejiConvert;
using DesktopPet.Tools.ShimejiConvert.Emit;
using DesktopPet.Tools.ShimejiConvert.Shimeji;

namespace DesktopPet.ShimejiImporterModule
{
    /// <summary>
    /// Shimeji Importer: turn a Shimeji skin the user already has into a desktopPet pet. It parses the skin's
    /// conf, composites its sprites into one sheet, emits an animations.xml, shows an honest report of what
    /// the conversion could not carry, previews the pet on the real desktop (IPetManager.SpawnPreview), and
    /// installs it (IPetManager.InstallType). All the conversion logic is in the shared ShimejiConvert.Engine;
    /// this module is the window and the host wiring.
    /// </summary>
    public sealed class ShimejiImporterModule : IModule
    {
        private IHost _host;
        private ShimejiImporterWindow _window;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "shimejiimporter",
            Name = "Shimeji Importer",
            Version = "1.0.0",
            // 1.4.7 is the host that added IHost.IsDarkTheme (window theming); it also carries the
            // IPetManager verbs this uses -- PetsDirectory (1.4.6), SpawnPreview and InstallType. Declaring it
            // means an older host refuses the module with a legible reason instead of failing at a call site.
            MinHostVersion = "1.4.7",
            Permissions = ModulePermissions.Pets | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;

            // No tray item: the importer is an occasional tool, opened from Options -> Modules rather than
            // cluttering every tray right-click.
            host.AddOptionsPane(new OptionsPane
            {
                Title = "Shimeji Importer",
                Schema = new List<SettingField>
                {
                    new SettingField
                    {
                        Id = "about",
                        Label = "What this is",
                        Kind = SettingKind.Info,
                        Group = "Shimeji Importer",
                    },
                },
                Actions = new[]
                {
                    new PaneAction { Label = "Open Shimeji Importer…", InvokeAsync = OpenAsync, Group = "Shimeji Importer" },
                },
                Load = delegate
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        {
                            "about",
                            "Convert a Shimeji skin you already have (a folder or a .zip) into a desktopPet pet. " +
                            "The importer shows exactly what the conversion cannot carry before you install, " +
                            "previews the pet on your desktop, and never downloads skins for you."
                        },
                    };
                },
            });
        }

        private System.Threading.Tasks.Task<string> OpenAsync()
        {
            Open();
            return System.Threading.Tasks.Task.FromResult("Shimeji Importer is open.");
        }

        /// <summary>Show the importer, or bring the existing one forward. One window: a second would let two
        /// preview pets fight over the same slots.</summary>
        private void Open()
        {
            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return;
                }
                _window = new ShimejiImporterWindow(_host);
                _window.Closed += delegate { _window = null; };
                _window.Show();
            }
            catch (Exception ex)
            {
                if (_host != null) _host.SayAll("Shimeji Importer could not open: " + ex.Message);
            }
        }

        public void Shutdown()
        {
            ShimejiImporterWindow window = _window;
            _window = null;
            if (window == null) return;
            try { window.Close(); } catch { }
        }

        /// <summary>
        /// Convention self-test (run by --module-selftest=shimejiimporter through the real loader). Builds a
        /// tiny synthetic skin on disk, runs the full folder -> detect -> convert pipeline, and asserts the
        /// result is accepted. Host-free: install/preview need an IPetManager, which the engine's own
        /// EmitterSelfTest already exercises the emit half of.
        /// </summary>
        public static bool SelfTest(out string detail)
        {
            string root = Path.Combine(Path.GetTempPath(), "shimejiimporter-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string conf = Path.Combine(root, "conf");
                string img = Path.Combine(root, "img", "TestSkin");
                Directory.CreateDirectory(conf);
                Directory.CreateDirectory(img);
                WritePng(Path.Combine(img, "s.png"), Color.FromArgb(255, 200, 200, 200));
                WritePng(Path.Combine(img, "w.png"), Color.FromArgb(255, 160, 160, 160));
                WritePng(Path.Combine(img, "f.png"), Color.FromArgb(255, 120, 120, 255));
                File.WriteAllText(Path.Combine(conf, "actions.xml"), SyntheticActionsXml);

                string note;
                List<DetectedSkin> skins = SkinLayout.Detect(root, out note);
                if (skins.Count == 0) { detail = "shimeji importer self-test: layout detect found no skin -- " + note; return false; }

                DetectedSkin skin = skins[0];
                string error;
                ConversionResult r = ShimejiEngine.ConvertSkin(skin.ConfDir, skin.ImgDir, skin.Name, out error);
                if (r == null) { detail = "shimeji importer self-test: convert failed -- " + error; return false; }
                if (!r.Accepted)
                {
                    detail = "shimeji importer self-test: pet not accepted (valid=" + r.Valid +
                             ", roundtrip=" + r.RoundTrips +
                             ", unreachable=" + (r.Graph != null ? r.Graph.Unreachable.Count : -1) + "): " + r.Error;
                    return false;
                }

                detail = "shimeji importer self-test: folder -> detect -> convert produced an accepted pet (" +
                         (r.Root != null && r.Root.Animations != null && r.Root.Animations.Animation != null
                             ? r.Root.Animations.Animation.Length : 0) + " animations)";
                return true;
            }
            catch (Exception ex)
            {
                detail = "shimeji importer self-test: threw -- " + ex.Message;
                return false;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static void WritePng(string path, Color c)
        {
            using (var bmp = new Bitmap(40, 60, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(c);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private const string SyntheticActionsXml =
@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<Mascot xmlns=""http://www.group-finity.com/Mascot"">
  <ActionList>
    <Action Name=""Stand"" Type=""Stay"" BorderType=""Floor"">
      <Animation><Pose Image=""/s.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
    <Action Name=""Walk"" Type=""Move"" BorderType=""Floor"">
      <Animation><Pose Image=""/w.png"" ImageAnchor=""20,60"" Velocity=""-2,0"" Duration=""6"" /></Animation>
    </Action>
    <Action Name=""Falling"" Type=""Embedded"" Class=""com.group_finity.mascot.action.Fall"" Gravity=""2"">
      <Animation><Pose Image=""/f.png"" ImageAnchor=""20,60"" Velocity=""0,0"" Duration=""250"" /></Animation>
    </Action>
  </ActionList>
</Mascot>";
    }
}
