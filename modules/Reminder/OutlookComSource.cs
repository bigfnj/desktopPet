using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// Reads the locally-installed desktop Outlook's default calendar over late-bound COM (no PIA reference, so
    /// it works with whatever Outlook version is installed). This is the "local Outlook" path, distinct from a
    /// published Outlook/M365 URL (which the <see cref="IcsUrlSource"/> already covers).
    ///
    /// Deliberately non-intrusive: it only ATTACHES to an Outlook that is already running (Outlook is a
    /// single-instance COM server, so creating the Application object returns the live one) and never launches
    /// Outlook itself — a background reminder poller silently starting Outlook would be hostile. Runs on an STA
    /// thread off the UI thread (see <see cref="CachingCalendarSource"/>), releases every COM object it takes,
    /// and never quits the user's Outlook.
    /// </summary>
    public sealed class OutlookComSource : CachingCalendarSource
    {
        private const int OlFolderCalendar = 9;
        private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WindowForward = TimeSpan.FromHours(48);
        private const int MaximumOccurrences = 2000;

        public OutlookComSource() : base(TimeSpan.FromMinutes(2)) { }

        public override string Name { get { return "Local Outlook"; } }
        protected override string LoadingMessage { get { return "Reading Outlook…"; } }
        protected override bool RequiresSta { get { return true; } }   // COM to Outlook wants an STA thread
        protected override string RefreshKey() { return "outlook"; }   // no per-config key; refresh on interval

        protected override CalendarSnapshot FetchCore(DateTimeOffset now)
        {
            // Attach only to a running Outlook; do not start it.
            if (Process.GetProcessesByName("OUTLOOK").Length == 0)
                return new CalendarSnapshot { Events = LastGood ?? Array.Empty<CalendarEvent>(), Error = "Outlook isn't running — start Outlook to read the local calendar." };

            Type appType = Type.GetTypeFromProgID("Outlook.Application");
            if (appType == null)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "Outlook is not installed on this PC." };

            object app = null, ns = null, folder = null, items = null, restricted = null;
            try
            {
                app = Activator.CreateInstance(appType);            // the single running instance
                dynamic application = app;
                ns = application.GetNamespace("MAPI");
                dynamic mapi = ns;
                folder = mapi.GetDefaultFolder(OlFolderCalendar);
                dynamic calendar = folder;
                items = calendar.Items;
                dynamic itemList = items;

                // Standard Outlook idiom for expanding recurrences over a range: sort by Start ascending,
                // turn on IncludeRecurrences, THEN Restrict to the window. Dates in the current locale format.
                itemList.IncludeRecurrences = true;
                itemList.Sort("[Start]");
                DateTime fromLocal = now.LocalDateTime - WindowBack;
                DateTime toLocal = now.LocalDateTime + WindowForward;
                string filter = "[Start] >= '" + fromLocal.ToString("g", CultureInfo.CurrentCulture) +
                    "' AND [Start] <= '" + toLocal.ToString("g", CultureInfo.CurrentCulture) + "'";
                restricted = itemList.Restrict(filter);
                dynamic range = restricted;

                var events = new List<CalendarEvent>();
                int count = 0;
                foreach (dynamic appt in range)
                {
                    object apptObj = appt;
                    try
                    {
                        if (++count > MaximumOccurrences) break;
                        DateTime startLocal = appt.Start;               // AppointmentItem.Start (local)
                        var start = new DateTimeOffset(DateTime.SpecifyKind(startLocal, DateTimeKind.Local));
                        DateTimeOffset? end = null;
                        try { end = new DateTimeOffset(DateTime.SpecifyKind((DateTime)appt.End, DateTimeKind.Local)); }
                        catch { }
                        string entryId;
                        try { entryId = (string)appt.EntryID; } catch { entryId = null; }
                        string id = (string.IsNullOrEmpty(entryId) ? "outlook" : entryId) + "@" + start.UtcDateTime.ToString("o");

                        string title = null, location = null;
                        try { title = (string)appt.Subject; } catch { }
                        try { location = (string)appt.Location; } catch { }

                        events.Add(new CalendarEvent { Id = id, Title = title, Start = start, End = end, Location = location });
                    }
                    catch { /* skip a stray non-appointment item */ }
                    finally { Release(apptObj); }
                }

                return new CalendarSnapshot { Events = events, Updated = now, Error = null };
            }
            finally
            {
                // Release everything we took, in reverse. Never Quit(): that would close the user's Outlook.
                Release(restricted); Release(items); Release(folder); Release(ns); Release(app);
            }
        }

        private static void Release(object comObject)
        {
            try { if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject); }
            catch { }
        }
    }
}
