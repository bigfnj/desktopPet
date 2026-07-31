using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DesktopPet.Ai
{
    internal sealed class ScreenCaptureContext
    {
        public string ActiveWindowTitle { get; private set; }
        public Rectangle MonitorBounds { get; private set; }

        public ScreenCaptureContext(string activeWindowTitle, Rectangle monitorBounds)
        {
            ActiveWindowTitle = activeWindowTitle ?? "";
            MonitorBounds = monitorBounds;
        }
    }

    /// <summary>
    /// Best-effort read of the foreground window's title (backlog 5.1: context awareness).
    /// Lets the brain say things like "busy in Visual Studio?" instead of reacting to raw
    /// screen text alone. Purely observational; never throws.
    /// </summary>
    internal static class ActiveWindow
    {
        private const int MaximumTitleCharacters = 512;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rectangle);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>Process name of the current foreground window (e.g. "chrome"), or "" if unavailable.</summary>
        public static string ProcessName()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid <= 0) return "";
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    return p.ProcessName ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Title of the current foreground window, or "" if none/unavailable.</summary>
        public static string Title()
        {
            try
            {
                return Title(GetForegroundWindow());
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Snapshot the foreground title and the monitor to capture from the same window handle.
        /// If no usable foreground window exists, fall back to the monitor containing the pet.
        /// </summary>
        public static ScreenCaptureContext CaptureContext(
            Rectangle fallbackMonitorBounds)
        {
            try
            {
                Rectangle fallback = fallbackMonitorBounds;
                if (fallback.Width <= 0 || fallback.Height <= 0)
                    fallback = Screen.PrimaryScreen != null
                        ? Screen.PrimaryScreen.Bounds
                        : new Rectangle(0, 0, 1, 1);

                IntPtr foreground = GetForegroundWindow();
                string title = Title(foreground);
                Rectangle foregroundBounds = Rectangle.Empty;
                if (foreground != IntPtr.Zero &&
                    GetWindowRect(foreground, out WindowRect rectangle))
                {
                    long width = (long)rectangle.Right - rectangle.Left;
                    long height = (long)rectangle.Bottom - rectangle.Top;
                    if (width > 0 && width <= int.MaxValue &&
                        height > 0 && height <= int.MaxValue)
                    {
                        foregroundBounds = new Rectangle(
                            rectangle.Left,
                            rectangle.Top,
                            (int)width,
                            (int)height);
                    }
                }

                Screen[] screens = Screen.AllScreens;
                Rectangle[] monitors = new Rectangle[screens.Length];
                for (int index = 0; index < screens.Length; index++)
                    monitors[index] = screens[index].Bounds;
                Rectangle selected = DesktopGeometry.SelectCaptureMonitor(
                    foregroundBounds,
                    fallback,
                    monitors);
                return new ScreenCaptureContext(title, selected);
            }
            catch
            {
                Rectangle fallback = fallbackMonitorBounds;
                if (fallback.Width <= 0 || fallback.Height <= 0)
                    fallback = new Rectangle(0, 0, 1, 1);
                return new ScreenCaptureContext("", fallback);
            }
        }

        private static string Title(IntPtr window)
        {
            if (window == IntPtr.Zero) return "";
            int length = GetWindowTextLength(window);
            if (length <= 0) return "";

            // Read one code unit beyond the retained limit so the shared truncator
            // can see and remove a surrogate pair that straddles the boundary.
            length = Math.Min(length, MaximumTitleCharacters + 1);
            StringBuilder text = new StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            string title = text.ToString().Trim();
            return UnicodeTextProgress.TruncateAtCodePointBoundary(
                title,
                MaximumTitleCharacters);
        }
    }
}
