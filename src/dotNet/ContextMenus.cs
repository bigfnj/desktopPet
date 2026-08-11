using System;
using System.Collections.Generic;
using System.Windows.Forms;
using DesktopPet.Properties;
using DesktopPet.Modules;   // TrayItem (module tray contributions)
using System.Drawing;
using System.IO;
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
        private ContextMenuStrip ownedMenu;
        // Module-contributed tray items (S5a), (re)built from the plugin host's collected TrayItems each time
        // the menu opens, so Visible/DynamicText/BuildChildren are re-evaluated live and late-loaded modules
        // still appear. Tracked here so the prior batch can be removed before the next rebuild.
        private readonly List<ToolStripItem> moduleTrayItems = new List<ToolStripItem>();

        // The AI-brain tray items (Ask / Enable-Disable) moved out with the AI-brain module (S4b); the AI
        // brain now controls itself via its own setting + hotkey. The module contributes its own tray items
        // once the tray is assembled from module contributions (S5).

        /// <summary>
        /// Called by FormOptions when SpeechEnabled is toggled so the menu items show/hide live.
        /// </summary>
        public static void RefreshSpeechMenuItem()
        {
            if (testSpeechMenuItem != null)
                testSpeechMenuItem.Visible = Program.MyData.GetSpeechEnabled();
        }

        /// <summary>
        /// Rebuild the module-contributed tray section each time the menu opens: remove the previous batch,
        /// then merge the plugin host's collected <see cref="TrayItem"/>s (sorted by Group then Order, with a
        /// separator between groups) just after Test Speech. Visible/DynamicText are evaluated live here and
        /// BuildChildren submenus are built lazily on open. Fully defensive — a throwing module item can never
        /// break the core tray.
        /// </summary>
        private void ModuleTray_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ContextMenuStrip menu = ownedMenu;
            if (menu == null) return;
            try
            {
                foreach (ToolStripItem prior in moduleTrayItems)
                {
                    try { menu.Items.Remove(prior); prior.Dispose(); } catch { }
                }
                moduleTrayItems.Clear();

                DesktopPet.Plugins.PetHost host = Program.Mainthread != null ? Program.Mainthread.Host : null;
                if (host == null || host.TrayItems == null || host.TrayItems.Count == 0) return;

                var ordered = new List<TrayItem>(host.TrayItems);
                ordered.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Order.CompareTo(b.Order));

                int anchor = testSpeechMenuItem != null ? menu.Items.IndexOf(testSpeechMenuItem) + 1 : menu.Items.Count;
                if (anchor < 0 || anchor > menu.Items.Count) anchor = menu.Items.Count;

                int insertAt = anchor;
                int lastGroup = int.MinValue;
                bool any = false;
                foreach (TrayItem ti in ordered)
                {
                    if (ti == null) continue;
                    bool visible = true;
                    try { if (ti.Visible != null) visible = ti.Visible(); } catch { visible = false; }
                    if (!visible) continue;

                    if (any && ti.Group != lastGroup)
                    {
                        var groupSep = new ToolStripSeparator();
                        menu.Items.Insert(insertAt++, groupSep);
                        moduleTrayItems.Add(groupSep);
                    }
                    ToolStripMenuItem mi = BuildModuleMenuItem(ti);
                    menu.Items.Insert(insertAt++, mi);
                    moduleTrayItems.Add(mi);
                    lastGroup = ti.Group;
                    any = true;
                }
                if (any)
                {
                    var tailSep = new ToolStripSeparator();
                    menu.Items.Insert(insertAt++, tailSep);
                    moduleTrayItems.Add(tailSep);
                }
            }
            catch { /* a bad module must never break the tray */ }
        }

        private static ToolStripMenuItem BuildModuleMenuItem(TrayItem ti)
        {
            string text = ti.Label ?? "";
            try { if (ti.DynamicText != null) { string dt = ti.DynamicText(); if (!string.IsNullOrEmpty(dt)) text = dt; } } catch { }
            var mi = new ToolStripMenuItem { Text = text };
            if (ti.BuildChildren != null)
            {
                mi.DropDownItems.Add(new ToolStripMenuItem { Text = "…", Enabled = false });
                TrayItem captured = ti;
                mi.DropDownOpening += (s, e) => RebuildModuleSubmenu(mi, captured);
            }
            if (ti.Click != null)
            {
                Action click = ti.Click;
                mi.Click += (s, e) => { try { click(); } catch { } };
            }
            return mi;
        }

        private static void RebuildModuleSubmenu(ToolStripMenuItem parent, TrayItem ti)
        {
            try
            {
                parent.DropDownItems.Clear();
                IEnumerable<TrayItem> children = null;
                try { if (ti.BuildChildren != null) children = ti.BuildChildren(); } catch { children = null; }
                if (children != null)
                {
                    foreach (TrayItem c in children)
                    {
                        if (c == null) continue;
                        bool vis = true;
                        try { if (c.Visible != null) vis = c.Visible(); } catch { vis = false; }
                        if (!vis) continue;
                        parent.DropDownItems.Add(BuildModuleMenuItem(c));
                    }
                }
                if (parent.DropDownItems.Count == 0)
                    parent.DropDownItems.Add(new ToolStripMenuItem { Text = "(none)", Enabled = false });
            }
            catch { }
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

        bool isAboutLoaded = false;
        bool isOptionLoaded = false;

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

            // Module tray contributions (S5a) are merged in here (just after Test Speech, before Options),
            // rebuilt on each open so their Visible/DynamicText re-evaluate and late-loaded modules appear.
            menu.Opening += ModuleTray_Opening;

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

			// Item: About (also hosts the usage/help content — the separate Help dialog was folded in).
			item = new ToolStripMenuItem
			{
				Text = "A&bout / Help"
			};
			item.Click += new EventHandler(About_Click);
            item.Image = Resources.about;
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

            return menu;
        }

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
                // Add the specific pet the entry names — the built-in adds the default eSheep, not the
                // active pet (id "" means "active", which would add the wrong pet after "Use this pet").
                string id = info.IsBuiltIn ? PetCatalog.BuiltInPetId : (info.Id ?? "");
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
            if (isOptionLoaded) return;
            if (isAboutLoaded) return;
            isAboutLoaded = true;
            try
            {
                // Themed WPF About window (the WinForms AboutBox + Help dialog were retired and folded into
                // this one window); ShowDialog is modal, but the re-entry guard stays for parity/safety.
                DesktopPet.Wpf.OptionsShell.OpenAbout(author, title, version, info);
            }
            finally
            {
                isAboutLoaded = false;
            }
        }

        /// <summary>
        /// Handles the Click event of the Option control. Open a dialog if no other dialog is still opened.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        void Options_Click(object sender, EventArgs e)
        {
            if (isAboutLoaded) return;
            isOptionLoaded = true;
            try
            {
                DesktopPet.Wpf.OptionsShell.Open();   // WPF settings (the classic FormOptions dialog was retired in S5b-3)
            }
            finally
            {
                isOptionLoaded = false;
            }
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
            if (menu != null) { menu.Opening -= ModuleTray_Opening; menu.Dispose(); }
            moduleTrayItems.Clear();
            addPetMenuItem = null;
            removePetMenuItem = null;
            closeSheepMenuItem = null;
            testSpeechMenuItem = null;
        }
    }
}
