using System;
using System.Collections.Generic;
using System.Net.Http;
using Ical.Net;
using Ical.Net.DataTypes;
// This module has its own CalendarEvent DTO; alias iCal.Net's so the source-component cast is unambiguous.
using IcsEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// Reads a public/secret iCalendar (.ics) URL: Google Calendar's "Secret address in iCal format", an
    /// Outlook.com / Microsoft 365 published calendar, or iCloud. iCal.Net does the hard parts (RFC 5545
    /// recurrence + VTIMEZONE), so this expands occurrences in a bounded near-term window into the same
    /// <see cref="CalendarEvent"/> shape every other source produces. The fetch runs on a background thread
    /// via <see cref="CachingCalendarSource"/>, so a slow feed never freezes the pet.
    /// </summary>
    public sealed class IcsUrlSource : CachingCalendarSource
    {
        private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WindowForward = TimeSpan.FromHours(48);
        private const int MaximumBytes = 8 * 1024 * 1024;
        private const int MaximumOccurrences = 2000;   // guard a runaway (e.g. every-minute) recurrence

        private static readonly HttpClient Http = CreateClient();
        private readonly Func<string> _urlGetter;

        public IcsUrlSource(Func<string> urlGetter) : base(TimeSpan.FromMinutes(5))
        {
            _urlGetter = urlGetter ?? throw new ArgumentNullException(nameof(urlGetter));
        }

        public override string Name { get { return "Calendar URL"; } }
        protected override string LoadingMessage { get { return "Fetching the calendar…"; } }
        protected override string RefreshKey() { return (_urlGetter() ?? "").Trim(); }

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPet-Reminder/1.0");
            return http;
        }

        protected override CalendarSnapshot FetchCore(DateTimeOffset now)
        {
            string url = RefreshKey();
            if (url.Length == 0)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "No calendar URL is configured." };
            string ics = Download(url);
            return ParseIcs(ics, now);
        }

        private static string Download(string url)
        {
            // webcal:// is just http(s) by another name; normalize it, and refuse anything that isn't http(s).
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring("webcal://".Length);
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new InvalidOperationException("The calendar URL must be an http(s) or webcal address.");

            using (HttpResponseMessage response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                       .GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaximumBytes)
                    throw new InvalidOperationException("The calendar feed is too large.");
                byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length > MaximumBytes) throw new InvalidOperationException("The calendar feed is too large.");
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>Parse ICS text and expand occurrences in the near-term window. Testable without a network:
        /// give it the raw ICS and "now". Never throws; a parse failure returns an error snapshot.</summary>
        internal static CalendarSnapshot ParseIcs(string ics, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(ics))
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed was empty." };

            Calendar calendar;
            try { calendar = Calendar.Load(ics); }
            catch (Exception ex)
            {
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed is not valid iCalendar: " + Short(ex.Message) };
            }
            if (calendar == null)
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "The calendar feed is not valid iCalendar." };

            DateTime fromUtc = now.UtcDateTime - WindowBack;
            DateTime toUtc = now.UtcDateTime + WindowForward;

            var events = new List<CalendarEvent>();
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;
                // GetOccurrences yields ascending occurrences (recurrence + timezones already resolved); stop
                // once we pass the window end, and cap the count so a pathological recurrence can't run away.
                foreach (Occurrence occ in calendar.GetOccurrences(new CalDateTime(fromUtc, "UTC")))
                {
                    if (++count > MaximumOccurrences) break;
                    DateTime startUtc;
                    try { startUtc = occ.Period.StartTime.AsUtc; }
                    catch { continue; }
                    if (startUtc >= toUtc) break;

                    var source = occ.Source as IcsEvent;
                    string uid = source != null && !string.IsNullOrEmpty(source.Uid) ? source.Uid : "ics";
                    string id = uid + "@" + startUtc.ToString("o");
                    if (!seen.Add(id)) continue;

                    DateTimeOffset? endOffset = null;
                    try
                    {
                        CalDateTime end = occ.Period.EffectiveEndTime;
                        if (end != null) endOffset = new DateTimeOffset(end.AsUtc, TimeSpan.Zero);
                    }
                    catch { }

                    events.Add(new CalendarEvent
                    {
                        Id = id,
                        Title = source != null ? source.Summary : null,
                        Start = new DateTimeOffset(startUtc, TimeSpan.Zero),
                        End = endOffset,
                        Location = source != null ? source.Location : null,
                        Description = source != null ? source.Description : null,
                        AllDay = source != null && source.IsAllDay,
                        // ResponseStatus left null: an .ics feed carries per-attendee PARTSTAT, not "my" status.
                    });
                }
            }
            catch (Exception ex)
            {
                return new CalendarSnapshot { Events = Array.Empty<CalendarEvent>(), Error = "Could not expand the calendar's events: " + Short(ex.Message) };
            }

            return new CalendarSnapshot { Events = events, Updated = now, Error = null };
        }
    }
}
