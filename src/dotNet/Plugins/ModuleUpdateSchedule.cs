using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopPet.Plugins
{
    /// <summary>
    /// "Check for module updates on the 1st of every month", made honest about a desktop pet's actual life
    /// cycle. A literal 1st-of-the-month alarm would silently skip any month the app happened not to be running
    /// on that date, so what is stored is the month a check LAST SUCCEEDED and a check becomes due as soon as
    /// the calendar month has moved on. A pet started on the 5th having missed the 1st still checks; one left
    /// running for a year checks twelve times, not once.
    ///
    /// The stamp is a two-line-free `yyyy-MM` marker file next to the other startup markers in the data root
    /// rather than a settings field: it is machine state with no user meaning, and it must not drag the settings
    /// schema (and its migrations, merges and tests) along for the ride.
    ///
    /// A fresh install is SEEDED rather than checked — the stamp is written without a fetch, so the first
    /// automatic check lands at the next month rollover. Someone who just installed the app is minutes away
    /// from having chosen their modules by hand; a surprise network call on first launch buys nothing.
    /// </summary>
    internal static class ModuleUpdateSchedule
    {
        private const string StampFormat = "yyyy-MM";
        private const string FileName = "module-update-check.txt";

        internal static string DefaultStampPath
        {
            get { return Path.Combine(AppPaths.DataRoot, FileName); }
        }

        /// <summary>
        /// True when <paramref name="nowLocal"/> falls in a later calendar month than the recorded stamp. An
        /// empty or unparseable stamp is NOT due (the caller seeds it instead), and a stamp in the FUTURE is not
        /// due either — a clock that jumped backwards should not turn into a check on every single tick.
        /// </summary>
        internal static bool IsDue(DateTime nowLocal, string stamp)
        {
            DateTime last;
            if (!DateTime.TryParseExact(
                    (stamp ?? "").Trim(),
                    StampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out last))
                return false;
            return (nowLocal.Year * 12 + nowLocal.Month) > (last.Year * 12 + last.Month);
        }

        internal static string ReadStamp(string path)
        {
            try { return File.Exists(path) ? (File.ReadAllText(path) ?? "").Trim() : ""; }
            catch { return ""; }
        }

        /// <summary>Record a month as checked. Best-effort: a read-only data root means the check simply
        /// re-evaluates next launch, which is a better failure than refusing to run.</summary>
        internal static void WriteStamp(string path, DateTime nowLocal)
        {
            try
            {
                File.WriteAllText(
                    path,
                    nowLocal.ToString(StampFormat, CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
