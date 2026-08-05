using System;
using System.Windows.Forms;
using DesktopPet.Properties;
using System.Drawing;
using System.IO;
#if !PORTABLE
using Windows.System;
using Windows.Foundation.Collections;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet
{
        /// <summary>
        /// The only way to interact with the application (not the pet itself) is over the context menu.<br />
        /// This menu is available once you press on the tray icon (near the windows clock).
        /// </summary>
    class ContextMenus : IDisposable
    {
            /// <summary>
            /// "Add a pet" submenu: lists the built-in default plus every local pet type; its icon
            /// tracks the active pet. Children are (re)built each time the submenu opens.
            /// </summary>
        static ToolStripMenuItem addPetMenuItem;
            /// <summary>
            /// "Remove a pet" submenu: lists the pet types currently on screen with their counts.
            /// </summary>
        static ToolStripMenuItem removePetMenuItem;
            /// <summary>
            /// Close Menu Item: removes all pets and closes the app.
            /// </summary>
        static ToolStripMenuItem closeSheepMenuItem;
            /// <summary>Display name of the active/default pet, for the Remove submenu's "" entry.</summary>
        static string activePetName = "Sheep";
            /// <summary>
            /// Test Speech item — visibility tracks the SpeechEnabled setting.
            /// </summary>
        static ToolStripMenuItem testSpeechMenuItem;
            /// <summary>
            /// Ask-AI item — visibility also tracks the SpeechEnabled setting.
            /// </summary>
        static ToolStripMenuItem askAiMenuItem;
            /// <summary>
            /// Enable/Disable-AI item — text reflects the AI-brain state, click toggles it.
            /// </summary>
        static ToolStripMenuItem aiBrainMenuItem;
        private ContextMenuStrip ownedMenu;

        private static bool BrainOn { get { return Program.Mainthread != null && Program.Mainthread.AiBrainEnabled; } }

        /// <summary>
        /// Called by FormOptions when SpeechEnabled is toggled so the menu items show/hide live.
        /// </summary>
        public static void RefreshSpeechMenuItem()
        {
            if (testSpeechMenuItem != null)
                testSpeechMenuItem.Visible = Program.MyData.GetSpeechEnabled();
            if (askAiMenuItem != null)
                askAiMenuItem.Visible = Program.MyData.GetSpeechEnabled() && BrainOn;
        }

        /// <summary>
        /// Update the "Enable AI" / "Disable AI" tray item to reflect the current brain state, and
        /// show/hide the "Ask about my screen" item accordingly.
        /// </summary>
        public static void RefreshAiBrainMenuItem(bool enabled)
        {
            if (aiBrainMenuItem != null)
                aiBrainMenuItem.Text = GetAiBrainMenuText(enabled);
            if (askAiMenuItem != null)
                askAiMenuItem.Visible = Program.MyData.GetSpeechEnabled() && enabled;
        }

        internal static string GetAiBrainMenuText(bool enabled)
        {
            return enabled ? "&Disable AI" : "&Enable AI";
        }

        /// <summary>
        /// A value to set in the About dialog: author.
        /// </summary>
        static string author;
            /// <summary>
            /// A value to set in the About dialog: animation version.
            /// </summary>
        static string version;
            /// <summary>
            /// A value to set in the About dialog: animation title.
            /// </summary>
        static string title;
            /// <summary>
            /// A value to set in the About dialog: description and information about the animation.
            /// </summary>
        static string info;

#if PORTABLE
        bool isAboutLoaded = false;
        bool isOptionLoaded = false;
#else
        LocalData.LocalData MyData = new LocalData.LocalData(Windows.Storage.ApplicationData.Current.LocalFolder.Path, Windows.Storage.ApplicationData.Current.LocalFolder.Path + "\\eSheep.exe");
#endif

        /// <summary>
        /// Creates this instance for the tray icon.
        /// </summary>
        /// <returns>ContextMenuStrip to add in the tray icon.</returns>
        public ContextMenuStrip Create()
        {
                // Add the default menu options.
            ContextMenuStrip menu = new ContextMenuStrip();
            ownedMenu = menu;
            ToolStripMenuItem item;
            ToolStripSeparator sep;

			// Item: Add a pet (submenu of pet types; built on open). Different types can coexist.
			addPetMenuItem = new ToolStripMenuItem { Text = "&Add a pet" };
            addPetMenuItem.Image = Resources.icon.ToBitmap();
            addPetMenuItem.Font = new Font(addPetMenuItem.Font, addPetMenuItem.Font.Style | FontStyle.Bold);
            addPetMenuItem.DropDownOpening += AddPetMenu_Opening;
            // A placeholder child so the arrow shows before the first open.
            addPetMenuItem.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
            menu.Items.Add(addPetMenuItem);

            // Item: Remove a pet (submenu of on-screen types with counts; built on open).
            removePetMenuItem = new ToolStripMenuItem { Text = "&Remove a pet" };
            removePetMenuItem.DropDownOpening += RemovePetMenu_Opening;
            removePetMenuItem.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
            menu.Items.Add(removePetMenuItem);

            // Item: Test Speech (optional — hidden when speech disabled)
            item = new ToolStripMenuItem { Text = "&Test Speech" };
            item.Click += (s, ev) =>
                Program.Mainthread.SayAll(
                    "Hello! I'm your desktop companion. Right-click the tray icon for options.");
            item.Visible = Program.MyData.GetSpeechEnabled();
            testSpeechMenuItem = item;
            menu.Items.Add(item);

            // Item: Ask about my screen (AI). Captures the screen, asks the selected provider,
            // and lets the pet speak the response.
            item = new ToolStripMenuItem { Text = "As&k about my screen" };
            item.Click += (s, ev) => Program.Mainthread.AskAboutScreen();
            item.Visible = Program.MyData.GetSpeechEnabled() && BrainOn;
            askAiMenuItem = item;
            menu.Items.Add(item);

            // Item: Enable/Disable AI. Ollama can additionally warm or unload model memory;
            // OpenAI-compatible backends simply enable or disable provider requests.
            aiBrainMenuItem = new ToolStripMenuItem { Text = GetAiBrainMenuText(BrainOn) };
            aiBrainMenuItem.Click += (s, ev) => { if (Program.Mainthread != null) Program.Mainthread.SetAiBrainEnabled(!Program.Mainthread.AiBrainEnabled); };
            menu.Items.Add(aiBrainMenuItem);

			// Item: Options.
			item = new ToolStripMenuItem
			{
				Text = "&Options"
			};
			item.Click += new EventHandler(Options_Click);
            item.Image = Resources.option;
            menu.Items.Add(item);

                // Item: Separator.
            sep = new ToolStripSeparator();
            menu.Items.Add(sep);

			// Item: About.
			item = new ToolStripMenuItem
			{
				Text = "A&bout"
			};
			item.Click += new EventHandler(About_Click);
            item.Image = Resources.about;
            menu.Items.Add(item);

			// Item: Help.
			item = new ToolStripMenuItem
			{
				Text = "&Help"
			};
			item.Click += new EventHandler(Help_Click);
            item.Image = Resources.help;
            menu.Items.Add(item);

            // Item: Separator.
            sep = new ToolStripSeparator();
            menu.Items.Add(sep);

			// Item: Close application.
			closeSheepMenuItem = new ToolStripMenuItem
			{
				Text = "&Remove all pets and Close"
			};
			closeSheepMenuItem.Click += new EventHandler(Exit_Click);
            closeSheepMenuItem.Image = Resources.exit;
            menu.Items.Add(closeSheepMenuItem);

#if PORTABLE
            if(Program.MyData.IsFirstBoot())
#else
            if(MyData.IsFirstBoot())
#endif
            {
                OpenOptionWindow("xamlesheep://options");
            }
            
            return menu;
        }
#if !PORTABLE
        private async void OpenOptionWindow(string url)
        {

            Uri uri = new Uri(url);
            await Launcher.LaunchUriAsync(uri);

        }
#else
        private void OpenOptionWindow(string url) { }
#endif

        /// <summary>
        /// Set a new icon in the context menu with the new pet and updated the info to show in the about dialog.<br />
        /// This function is called every time a new pet is loaded.
        /// </summary>
        /// <param name="newIcon">Icon with the new image.</param>
        /// <param name="petName">Name of the pet, to show in the context menu.</param>
        /// <param name="aboutAuthor">Name of the author.</param>
        /// <param name="aboutTitle">Title of the animation.</param>
        /// <param name="aboutVersion">Version of the animation.</param>
        /// <param name="aboutInfo">About the animation (copyright and author information)</param>
        static public void UpdateIcon(Icon newIcon, string petName, string aboutAuthor, string aboutTitle, string aboutVersion, string aboutInfo)
        {
            activePetName = string.IsNullOrWhiteSpace(petName) ? "Sheep" : petName;
            if (addPetMenuItem != null && newIcon != null)
            {
                Image oldImage = addPetMenuItem.Image;
                addPetMenuItem.Image = newIcon.ToBitmap();
                if (oldImage != null) oldImage.Dispose();
            }

            author = aboutAuthor;
            title = aboutTitle;
            version = aboutVersion;
            info = aboutInfo;
        }

        // Display name for a tray menu entry: the active/default pet ("") shows the running pet's name;
        // a folder id shows its curated catalog name (Pearl, Rick, ...).
        private static string TrayPetName(string id)
        {
            return string.IsNullOrEmpty(id) ? activePetName : PetCatalog.DisplayName(id, null);
        }

        // Rebuild the "Add a pet" submenu each time it opens so freshly downloaded pets appear. The
        // built-in default is first; each entry spawns one of that type alongside any existing pets.
        void AddPetMenu_Opening(object sender, EventArgs e)
        {
            addPetMenuItem.DropDownItems.Clear();
            bool full = Program.Mainthread != null && Program.Mainthread.IsAtMaxPets;
            foreach (PetCatalog.PetInfo info in PetCatalog.EnumerateLocal())
            {
                string id = info.Id ?? "";   // "" == the active/default pet
                var child = new ToolStripMenuItem { Text = info.DisplayName, Enabled = !full };
                child.Click += delegate
                {
                    if (Program.Mainthread != null) Program.Mainthread.AddPetFromTray(id);
                };
                addPetMenuItem.DropDownItems.Add(child);
            }
            if (full)
            {
                addPetMenuItem.DropDownItems.Add(new ToolStripSeparator());
                addPetMenuItem.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(maximum pets reached)", Enabled = false });
            }
        }

        // Rebuild the "Remove a pet" submenu each time it opens from the current on-screen mix, e.g.
        // "Pearl x2" / "Rick x1". Each entry removes one pet of that type.
        void RemovePetMenu_Opening(object sender, EventArgs e)
        {
            removePetMenuItem.DropDownItems.Clear();
            System.Collections.Generic.List<PetCountEntry> mix =
                Program.Mainthread != null ? Program.Mainthread.OnScreenMix() : null;
            if (mix != null)
            {
                foreach (PetCountEntry entry in mix)
                {
                    string id = entry.Id ?? "";
                    string text = TrayPetName(id) + " ×" + entry.Count;
                    var child = new ToolStripMenuItem { Text = text };
                    child.Click += delegate
                    {
                        if (Program.Mainthread != null) Program.Mainthread.RemoveOnePet(id);
                    };
                    removePetMenuItem.DropDownItems.Add(child);
                }
            }
            if (removePetMenuItem.DropDownItems.Count == 0)
                removePetMenuItem.DropDownItems.Add(
                    new ToolStripMenuItem { Text = "(no pets on screen)", Enabled = false });
        }


        /// <summary>
        /// Handles the Click event of the About control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void About_Click(object sender, EventArgs e)
        {
#if PORTABLE
            if (isOptionLoaded)
            {

            }
            else if (!isAboutLoaded)
            {
                isAboutLoaded = true;
                try
                {
                    using (AboutBox box = new AboutBox())
                    {
                        box.FillData(author, title, version, info);
                        box.ShowDialog();
                    }
                }
                finally
                {
                    isAboutLoaded = false;
                }
            }
#else
            OpenOptionWindow("xamlesheep://about");
#endif
        }

        /// <summary>
        /// Handles the Click event of the Help control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Help_Click(object sender, EventArgs e)
        {
#if PORTABLE
            FormHelp help = new FormHelp();
            help.Show();
#else
            OpenOptionWindow("xamlesheep://help");
#endif
        }

        /// <summary>
        /// Handles the Click event of the Option control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Options_Click(object sender, EventArgs e)
        {
#if PORTABLE
            if (isAboutLoaded)
            {

            }
            else if (!isOptionLoaded)
            {
                isOptionLoaded = true;
                try
                {
                    Program.OpenOptionDialog();
                }
                finally
                {
                    isOptionLoaded = false;
                }
            }
#else
            OpenOptionWindow("xamlesheep://options");
#endif
        }

            /// <summary>
            /// Processes a menu item. Will close the application after closing all pets.
            /// </summary>
            /// <param name="sender">The sender.</param>
            /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Exit_Click(object sender, EventArgs e)
        {
            // Quit without further ado.
            //Application.Exit();
            Program.Mainthread.KillSheeps(true);
        }


        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        public void Dispose()
        {
            ContextMenuStrip menu = ownedMenu;
            ownedMenu = null;
            if (menu != null) menu.Dispose();
            addPetMenuItem = null;
            removePetMenuItem = null;
            closeSheepMenuItem = null;
            testSpeechMenuItem = null;
            askAiMenuItem = null;
            aiBrainMenuItem = null;
        }
    }
}
