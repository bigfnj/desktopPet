using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPet.Ai
{
    /// <summary>
    /// Best-effort read of the foreground window's title (backlog 5.1: context awareness).
    /// Lets the brain say things like "busy in Visual Studio?" instead of reacting to raw
    /// screen text alone. Purely observational; never throws.
    /// </summary>
    internal static class ActiveWindow
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>Title of the current foreground window, or "" if none/unavailable.</summary>
        public static string Title()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";

                int len = GetWindowTextLength(h);
                if (len <= 0) return "";

                StringBuilder sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                return sb.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }
    }
}
