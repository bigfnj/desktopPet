using System;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// One concrete calendar event instance, normalized so the scheduler is source-agnostic. Every
    /// <see cref="ICalendarSource"/> produces these: the Local-file source deserializes them (co-work already
    /// expanded recurrence + resolved timezones), and a future ICS/Outlook source would expand its own into
    /// the same shape. <see cref="Start"/> carries an offset so a time is never ambiguous.
    /// </summary>
    public sealed class CalendarEvent
    {
        public string Id { get; set; }              // stable per-instance key; how a reminder is fired exactly once
        public string Title { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset? End { get; set; }
        public string Location { get; set; }
        public string SourceId { get; set; }        // which calendar slot this came from (set by AggregateCalendarSource); picks the per-feed style/label
    }

    /// <summary>What a source returns from one fetch: the events, the feed's own "updated" stamp (for a
    /// staleness hint), and an error string (null on success). Sources never throw; they report here.</summary>
    public sealed class CalendarSnapshot
    {
        public System.Collections.Generic.IReadOnlyList<CalendarEvent> Events { get; set; }
        public DateTimeOffset? Updated { get; set; }
        public string Error { get; set; }
    }
}
