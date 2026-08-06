using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopPet
{
    /// <summary>
    /// Per-user "run at startup" registration via the HKCU Run key. Best-effort: registration must
    /// never throw into the UI. Extracted from FormOptions so the renderer-agnostic Options controller
    /// can drive it too. For self-tests, the DESKTOPPET_STARTUP_TEST_KEY environment variable redirects
    /// to a throwaway subkey, so a test never rewrites or deletes the user's real startup entry.
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RealKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopPet AI Edition";

        private static string KeyPath
        {
            get
            {
                string redirect = Environment.GetEnvironmentVariable("DESKTOPPET_STARTUP_TEST_KEY");
                return string.IsNullOrWhiteSpace(redirect) ? RealKeyPath : redirect;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                                         ?? Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return;
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, false);
                }
            }
            catch { /* startup registration is best-effort */ }
        }
    }
}
