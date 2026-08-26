using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopPet.Modules;
using DesktopPet.ModuleKit;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// The pet reads a calendar feed and announces each event a few minutes before it starts. Sources are
    /// pluggable (see <see cref="ICalendarSource"/>); this slice wires the Local-file JSON source. A single
    /// UI-thread WinForms timer polls the source, fires any due reminders through <see cref="IHost.SayAll"/>
    /// (a reminder is an announcement to the user, not a per-pet reaction), and remembers which fired so a
    /// restart never re-nags.
    /// </summary>
    public sealed class ReminderModule : IModule
    {
        internal const string Id = "reminder";
        private const int DefaultLeadMinutes = 5;
        private const int TickMilliseconds = 20 * 1000;
        private const string SourceLocalFile = "Local file";
        private const string SourceCalendarUrl = "Calendar URL (ICS)";
        private const string SourceOutlook = "Local Outlook";

        private IHost _host;
        private IModuleSettings _settings;
        private ICalendarSource _source;
        private System.Windows.Forms.Timer _timer;
        private EventHandler _tickHandler;
        private readonly HashSet<string> _fired = new HashSet<string>(StringComparer.Ordinal);
        private CalendarSnapshot _lastSnapshot;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = Id,
            Name = "Reminder",
            Version = "1.2.0",   // 1.2.0: multiple lead times (e.g. 15 & 5), quiet hours, an optional chime,
                                 //        the event location in the announcement, and module-owned speech
                                 //        styling (font/size/colour/bold/italic/underline via ModuleKit)
                                 // 1.1.0: pluggable calendar sources -- Calendar URL (ICS: Google secret .ics /
                                 //        published Outlook/M365 / iCloud) + Local Outlook (COM) beside the
                                 //        local-file corporate feed; Network permission for the URL fetch
            // Styled speech calls IHost.Say/SayAll(text, SpeechStyle) (host 1.8.0); the chime calls PlaySound
            // (host 1.6.0). 1.8.0 is the floor.
            MinHostVersion = "1.8.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage | ModulePermissions.Network | ModulePermissions.Audio,
        };

        public void Init(IHost host)
        {
            _host = host;
            _settings = host.GetSettings(Id);
            _source = BuildSource();
            LoadFired();

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildTrayItem() });

            // WinForms timer: its Tick fires on the UI thread the host called Init on, so SayAll is on the
            // right thread with no marshaling. First tick soon so an imminent event isn't missed at startup.
            _timer = new System.Windows.Forms.Timer { Interval = TickMilliseconds };
            _tickHandler = delegate { CheckDue(); };
            _timer.Tick += _tickHandler;
            _timer.Start();
            CheckDue();
        }

        public void Shutdown()
        {
            if (_timer != null)
            {
                try
                {
                    _timer.Stop();
                    if (_tickHandler != null) _timer.Tick -= _tickHandler;
                    _timer.Dispose();
                }
                catch { }
                _timer = null;
                _tickHandler = null;
            }
        }

        // Build the calendar source from the saved "source" choice. Sources take live getters, so a URL/file
        // change needs no rebuild; only a source-TYPE change does, so this is re-run on Save.
        private ICalendarSource BuildSource()
        {
            string type = _settings.Get("source", SourceLocalFile);
            if (string.Equals(type, SourceCalendarUrl, StringComparison.Ordinal))
                return new IcsUrlSource(() => _settings.Get("url", ""));
            if (string.Equals(type, SourceOutlook, StringComparison.Ordinal))
                return new OutlookComSource();
            return new LocalJsonSource(() => _settings.Get("file", ""));
        }

        // --- the tick ---------------------------------------------------------------------------------

        private void CheckDue()
        {
            try
            {
                CalendarSnapshot snap = _source.Fetch();
                _lastSnapshot = snap;
                if (snap == null) return;
                if (snap.Error != null) { _host.Log(Id, "reminder feed: " + snap.Error); return; }

                IReadOnlyList<CalendarEvent> events = snap.Events ?? (IReadOnlyList<CalendarEvent>)Array.Empty<CalendarEvent>();

                // Keep the fired set bounded: drop composite ids whose EVENT no longer appears in the feed.
                // Fired ids are "<eventId>@<lead>", so compare on the event-id part.
                var feedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (CalendarEvent e in events) if (e != null && e.Id != null) feedIds.Add(e.Id);
                bool changed = _fired.RemoveWhere(id => !feedIds.Contains(ReminderScheduler.EventIdOf(id))) > 0;

                DateTimeOffset now = DateTimeOffset.Now;
                // Quiet hours: skip announcing WITHOUT marking fired, so an event still fires once the window
                // ends if it is still inside its lead time.
                if (!QuietHours.IsQuiet(now, _settings.Get("quietFrom", ""), _settings.Get("quietTo", "")))
                {
                    bool chime = _settings.GetBool("chime", true);
                    SpeechStyle style = SpeechStyleSettings.ToStyle(_settings);
                    foreach (DueReminder due in ReminderScheduler.DueNowMulti(events, now, Leads(), _fired))
                    {
                        if (chime) Chime.Play(_host);
                        _host.SayAll(FormatReminder(due.Event, now), style);
                        _fired.Add(due.FiredId);
                        changed = true;
                    }
                }
                if (changed) SaveFired();
            }
            catch (Exception ex)
            {
                try { _host.Log(Id, "reminder tick failed: " + ex.Message); } catch { }
            }
        }

        private static string FormatReminder(CalendarEvent e, DateTimeOffset now)
        {
            string title = string.IsNullOrWhiteSpace(e.Title) ? "an event" : e.Title.Trim();
            int mins = (int)Math.Round((e.Start - now).TotalMinutes);
            string line = mins <= 0 ? title + " is starting now."
                : (mins == 1 ? title + " starts in 1 minute." : title + " starts in " + mins + " minutes.");
            if (!string.IsNullOrWhiteSpace(e.Location))
                line += " (" + e.Location.Trim() + ")";
            return line;
        }

        // --- settings ---------------------------------------------------------------------------------

        private int LeadMinutes()
        {
            int lead = _settings.GetInt("lead", DefaultLeadMinutes);
            return lead < 0 ? 0 : (lead > 240 ? 240 : lead);
        }

        // The full options schema: the feed + timing + quiet-hours fields, then the standard speech-style
        // controls (ModuleKit) so a reminder can be styled distinctly from the pet's other chatter.
        private SettingField[] BuildSchema()
        {
            var fields = new List<SettingField>
            {
                new SettingField { Id = "source", Label = "Where to read the calendar from", Kind = SettingKind.Enum, Options = new[] { SourceLocalFile, SourceCalendarUrl, SourceOutlook }, Group = "Calendar feed" },
                new SettingField { Id = "url", Label = "Calendar URL (Google/Outlook secret .ics)", Kind = SettingKind.Text, Group = "Calendar feed" },
                new SettingField { Id = "file", Label = "Reminder feed file (JSON)", Kind = SettingKind.Text, Group = "Calendar feed" },
                new SettingField { Id = "status", Label = "Feed status", Kind = SettingKind.Info, Group = "Calendar feed" },
                new SettingField { Id = "leads", Label = "Remind me these many minutes before (comma-separated, e.g. 15,5)", Kind = SettingKind.Text, Group = "Timing" },
                new SettingField { Id = "chime", Label = "Play a chime with each reminder", Kind = SettingKind.Bool, Group = "Timing" },
                new SettingField { Id = "quietFrom", Label = "Quiet hours start (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" },
                new SettingField { Id = "quietTo", Label = "Quiet hours end (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" },
            };
            fields.AddRange(SpeechStyleSettings.Fields("Speech style"));
            return fields.ToArray();
        }

        // The lead times the tick uses: the parsed "leads" list, or the legacy single "lead" when it is empty.
        private IReadOnlyList<int> Leads()
        {
            List<int> list = ParseLeads(_settings.Get("leads", ""));
            if (list.Count == 0) list.Add(LeadMinutes());
            return list;
        }

        // What the pane shows in the "leads" box: the saved list, or the legacy single lead as a one-item list.
        private string LeadsText()
        {
            List<int> list = ParseLeads(_settings.Get("leads", ""));
            if (list.Count == 0) list.Add(LeadMinutes());
            return string.Join(",", list);
        }

        private static string NormalizeLeads(string raw)
        {
            return string.Join(",", ParseLeads(raw ?? ""));   // "" when nothing parses -> Leads() falls back
        }

        private static List<int> ParseLeads(string raw)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (string part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    int clamped = n < 0 ? 0 : (n > 240 ? 240 : n);
                    if (!list.Contains(clamped)) list.Add(clamped);
                }
            }
            return list;
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Reminders",
                Schema = BuildSchema(),
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Check now",
                        Group = "Calendar feed",
                        ReloadPaneAfter = true,
                        InvokeAsync = () =>
                        {
                            CheckDue();
                            return System.Threading.Tasks.Task.FromResult(StatusLine());
                        },
                    },
                },
                Load = () =>
                {
                    var values = new Dictionary<string, string>
                    {
                        ["source"] = _settings.Get("source", SourceLocalFile),
                        ["url"] = _settings.Get("url", ""),
                        ["file"] = _settings.Get("file", ""),
                        ["status"] = StatusLine(),
                        ["leads"] = LeadsText(),
                        ["chime"] = _settings.GetBool("chime", true) ? "true" : "false",
                        ["quietFrom"] = _settings.Get("quietFrom", ""),
                        ["quietTo"] = _settings.Get("quietTo", ""),
                    };
                    SpeechStyleSettings.AddLoadValues(values, _settings);
                    return values;
                },
                Save = values =>
                {
                    string v;
                    if (values.TryGetValue("source", out v) && !string.IsNullOrWhiteSpace(v)) _settings.Set("source", v.Trim());
                    if (values.TryGetValue("url", out v)) _settings.Set("url", (v ?? "").Trim());
                    if (values.TryGetValue("file", out v)) _settings.Set("file", (v ?? "").Trim());
                    if (values.TryGetValue("leads", out v)) _settings.Set("leads", NormalizeLeads(v));
                    if (values.TryGetValue("chime", out v)) { bool b; if (bool.TryParse(v, out b)) _settings.Set("chime", b ? "true" : "false"); }
                    if (values.TryGetValue("quietFrom", out v)) _settings.Set("quietFrom", (v ?? "").Trim());
                    if (values.TryGetValue("quietTo", out v)) _settings.Set("quietTo", (v ?? "").Trim());
                    SpeechStyleSettings.Save(_settings, values);
                    bool ok = _settings.Save();
                    _source = BuildSource();   // a source-type change takes effect on the next tick
                    return ok;
                },
            };
        }

        private TrayItem BuildTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 10,
                DynamicText = () =>
                {
                    CalendarEvent next = NextUpcoming();
                    if (next == null) return "Reminders: nothing upcoming";
                    return "Reminders: next is " + (string.IsNullOrWhiteSpace(next.Title) ? "an event" : next.Title.Trim())
                        + " at " + next.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                },
                Click = () => CheckDue(),
            };
        }

        private string StatusLine()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null) return "Not checked yet.";
            if (snap.Error != null) return "✗ " + snap.Error;
            int count = snap.Events != null ? snap.Events.Count : 0;
            string updated = snap.Updated != null ? ", updated " + snap.Updated.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
            CalendarEvent next = NextUpcoming();
            string nextText = next != null
                ? "; next: " + (string.IsNullOrWhiteSpace(next.Title) ? "an event" : next.Title.Trim())
                    + " at " + next.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : "; nothing upcoming";
            string src = _source != null ? _source.Name + ": " : "";
            return "✓ " + src + count + " event(s)" + updated + nextText;
        }

        private CalendarEvent NextUpcoming()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null || snap.Events == null) return null;
            DateTimeOffset now = DateTimeOffset.Now;
            return snap.Events.Where(e => e != null && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        }

        // --- fired-id persistence (bounded to the current feed in CheckDue) ---------------------------

        private void LoadFired()
        {
            string raw = _settings.Get("fired", "");
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string id in raw.Split('\n'))
                if (!string.IsNullOrWhiteSpace(id)) _fired.Add(id.Trim());
        }

        private void SaveFired()
        {
            _settings.Set("fired", string.Join("\n", _fired));
            _settings.Save();
        }
    }
}
