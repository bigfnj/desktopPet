using System;
using System.Collections.Generic;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// The pure due-selection rule, kept separate from the module so it can be unit-tested without a host.
    /// An event fires once, in the window [start - lead, start + grace]: the lead is the user's "remind me N
    /// minutes before", and the small grace both lets a coarse tick catch the moment and lets a just-launched
    /// app still fire a reminder for something imminent -- while NOT nagging about events that already came
    /// and went while the app was closed.
    /// </summary>
    public static class ReminderScheduler
    {
        /// <summary>Minutes after an event's start that a reminder may still fire (past this it is missed, not nagged).</summary>
        public const int GraceMinutes = 1;

        public static IReadOnlyList<CalendarEvent> DueNow(
            IReadOnlyList<CalendarEvent> events, DateTimeOffset now, int leadMinutes, ISet<string> firedIds)
        {
            var due = new List<CalendarEvent>();
            if (events == null) return due;
            if (leadMinutes < 0) leadMinutes = 0;
            foreach (CalendarEvent e in events)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                if (firedIds != null && firedIds.Contains(e.Id)) continue;
                DateTimeOffset fireAt = e.Start.AddMinutes(-leadMinutes);
                DateTimeOffset expireAt = e.Start.AddMinutes(GraceMinutes);
                if (now >= fireAt && now < expireAt) due.Add(e);
            }
            return due;
        }
    }
}
