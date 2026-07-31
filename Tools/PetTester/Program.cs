using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DesktopPet
{
    class Program
    {
        // Shared animation sources expect the product's settings facade. The
        // tester never reads persisted settings or plays validation audio, so
        // this in-memory facade keeps those paths inert without constructing
        // the legacy persistence service or touching a filesystem directory.
        public static readonly PetTesterContext MyData =
            new PetTesterContext();

        public static StartUp Mainthread = new StartUp();

        /// <summary>
        /// Der Haupteinstiegspunkt für die Anwendung.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null &&
                args.Length > 0 &&
                string.Equals(
                    args[0],
                    "--self-test",
                    StringComparison.OrdinalIgnoreCase))
            {
                return PetTesterSelfTests.Run(
                    args.Length > 1 ? args[1] : null);
            }

            Application.Run(new Form1());
            return 0;
        }
    }

    sealed class PetTesterContext
    {
        public string GetXml()
        {
            return "";
        }

        public double GetVolume()
        {
            return 0.0;
        }
    }

    class StartUp
    {
        public sealed class ErrorState
        {
            public string AudioErrorMessage = "";
        }

        public ErrorState ErrorMessages = new ErrorState();

        public enum DEBUG_TYPE
        {
            /// <summary>
            /// Only info, to show what is happening.
            /// </summary>
            info = 1,
            /// <summary>
            /// Something important happened or something that was not expected.
            /// </summary>
            warning = 2,
            /// <summary>
            /// An error is occurred. The application need to do something that was not expected.
            /// </summary>
            error = 3,
        }

        public static void AddDebugInfo(DEBUG_TYPE type, string text)
        {

        }
    }

    static class Screen
    {
        public static System.Windows.Forms.Screen PrimaryScreen
        {
            get { return System.Windows.Forms.Screen.PrimaryScreen; }
        }

        public static System.Windows.Forms.Screen[] AllScreens
        {
            get { return System.Windows.Forms.Screen.AllScreens; }
        }
    }

    class Properties
    {
        public static class Resources
        {
            public static string animations
            {
                get { return ""; }
            }

            public static string animations1
            {
                get
                {
                    using (var stream = typeof(Properties).Assembly.GetManifestResourceStream(
                        "DesktopPet.PetTester.animations.xsd"))
                    using (var reader = new System.IO.StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }
        }
    }
}
