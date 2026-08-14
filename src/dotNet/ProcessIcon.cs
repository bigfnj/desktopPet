using System;
using System.Windows.Forms;
using System.Drawing;
using System.Reflection;

namespace DesktopPet
{
        /// <summary>
        /// System Tray Icon. Shows an icon on the Taskbar to allow a ContextMenu.
        /// </summary>
    public sealed class ProcessIcon : IDisposable
    {
            /// <summary>
            /// The NotifyIcon object.
            /// </summary>
        NotifyIcon ni;
        ContextMenus menus;

            /// <summary>
            /// Initializes a new instance of the <see cref="ProcessIcon"/> class.
            /// </summary>
        public ProcessIcon()
        {
            // Instantiate the NotifyIcon object.
            ni = new NotifyIcon();
        }

            /// <summary>
            /// Displays the icon in the system tray.
            /// </summary>
        public void Display()
        {
            // Put the icon in the system tray and allow it react to mouse clicks.			
            ni.MouseClick += new MouseEventHandler(Ni_MouseClick);
            ni.MouseDoubleClick += new MouseEventHandler(Ni_MouseDoubleClick);

            ni.Text = "eSheep Desktop Pet";
            ni.Visible = true;

            // Attach a context menu.
            menus = new ContextMenus();
            ni.ContextMenuStrip = menus.Create();
        }

            /// <summary>
            /// Displays the icon in the system tray.
            /// </summary>
        public void SetIcon(System.IO.MemoryStream icon, string petName, string aboutAuthor, string aboutTitle, string aboutVersion, string aboutInfo)
        {
            bool success = true;
			try
			{
                Icon replacement = new Icon(icon, 32, 32);
                Icon oldIcon = ni.Icon;
				ni.Icon = replacement;
                if (oldIcon != null) oldIcon.Dispose();
				ContextMenus.UpdateIcon(ni.Icon, petName, aboutAuthor, aboutTitle, aboutVersion, aboutInfo);
				ni.Text = petName + " Desktop Pet";
			}
			catch(Exception)
			{
                success = false;
			}
            if(!success)
            {
                try
                {
                    StartUp.AddDebugInfo(StartUp.DEBUG_TYPE.error, "Animation ICON is invalid (icon converter is on the webpage)");
                    Icon replacement;
                    using (Icon extracted = Icon.ExtractAssociatedIcon(
                        Assembly.GetExecutingAssembly().Location))
                        replacement = new Icon(extracted, 32, 32);
                    Icon oldIcon = ni.Icon;
                    ni.Icon = replacement;
                    if (oldIcon != null) oldIcon.Dispose();
                    ContextMenus.UpdateIcon(ni.Icon, petName, aboutAuthor, aboutTitle, aboutVersion, aboutInfo);
                }
                catch (Exception) { } // probably thread error.
            }
        }

            /// <summary>
            /// Show a tray notification (Windows renders it as a toast), with an optional one-shot action for
            /// when the user clicks it. Used by the monthly module-update check: the pet must not nag with a
            /// modal dialog for something as minor as "a module has a newer build", but a notification the user
            /// can click through to the Modules pane is the difference between an update they find and one they
            /// never learn about. Silent no-op when the icon is not visible, so it can be called blindly.
            /// </summary>
        public void ShowBalloon(string title, string text, Action onClicked)
        {
            try
            {
                if (ni == null || !ni.Visible) return;
                if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                if (onClicked != null)
                {
                    // One-shot: unhook on the first click, so a later notification never replays this action.
                    balloonClicked = delegate
                    {
                        if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                        try { onClicked(); } catch (Exception) { }
                    };
                    ni.BalloonTipClicked += balloonClicked;
                }
                ni.BalloonTipTitle = title ?? "";
                ni.BalloonTipText = text ?? "";
                ni.BalloonTipIcon = ToolTipIcon.Info;
                ni.ShowBalloonTip(10000);
            }
            catch (Exception) { }
        }

        EventHandler balloonClicked;

            /// <summary>
            /// Releases unmanaged and - optionally - managed resources
            /// </summary>
        public void Dispose()
        {
            // When the application closes, this will remove the icon from the system tray immediately.
            if (ni != null)
            {
                ni.MouseClick -= Ni_MouseClick;
                ni.MouseDoubleClick -= Ni_MouseDoubleClick;
                if (balloonClicked != null) { ni.BalloonTipClicked -= balloonClicked; balloonClicked = null; }
                ni.ContextMenuStrip = null;
                if (menus != null)
                {
                    menus.Dispose();
                    menus = null;
                }
                if (ni.Icon != null)
                {
                    ni.Icon.Dispose();
                    ni.Icon = null;
                }
                ni.Visible = false;
                ni.Dispose();
                ni = null;
            }
        }

            /// <summary>
            /// Handles the MouseClick event of the ni control.
            /// </summary>
            /// <param name="sender">The source of the event.</param>
            /// <param name="e">The <see cref="System.Windows.Forms.MouseEventArgs"/> instance containing the event data.</param>
        void Ni_MouseClick(object sender, MouseEventArgs e)
        {
            // Handle mouse button clicks.
            if (e.Button == MouseButtons.Left)
            {
                // Start Windows Explorer.
                Program.Mainthread.TopMostSheeps();
            }
        }

            /// <summary>
            /// A double click will automatically start a new pet.
            /// </summary>
            /// <param name="sender">Caller as object.</param>
            /// <param name="e">Mouse event values.</param>
        void Ni_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            // Handle mouse button clicks.
            if (e.Button == MouseButtons.Left)
            {
                // Start Windows Explorer.
                //Process.Start("explorer", null);
                Program.Mainthread.AddSheep();
            }
        }
    }
}
