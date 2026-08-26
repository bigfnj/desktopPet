using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopPet.Modules;

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
            Version = "1.0.0",
            // Uses only long-frozen ABI (SayAll, GetSettings, AddOptionsPane, AddTrayItems, Log); a recent
            // floor keeps it off hosts predating the grouped-settings pane it renders into.
            MinHostVersion = "1.6.0",
            Permissions = ModulePermissions.Speech | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _settings = host.GetSettings(Id);
            _source = new LocalJsonSource(() => _settings.Get("file", ""));
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

                // Keep the fired set bounded: drop ids no longer in the feed (a past event that rolled off).
                var feedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (CalendarEvent e in events) if (e != null && e.Id != null) feedIds.Add(e.Id);
                bool changed = _fired.RemoveWhere(id => !feedIds.Contains(id)) > 0;

                DateTimeOffset now = DateTimeOffset.Now;
                foreach (CalendarEvent e in ReminderScheduler.DueNow(events, now, LeadMinutes(), _fired))
                {
                    _host.SayAll(FormatReminder(e, now));
                    _fired.Add(e.Id);
                    changed = true;
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
            if (mins <= 0) return title + " is starting now.";
            if (mins == 1) return title + " starts in 1 minute.";
            return title + " starts in " + mins + " minutes.";
        }

        // --- settings ---------------------------------------------------------------------------------

        private int LeadMinutes()
        {
            int lead = _settings.GetInt("lead", DefaultLeadMinutes);
            return lead < 0 ? 0 : (lead > 240 ? 240 : lead);
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Reminders",
                Schema = new[]
                {
                    new SettingField { Id = "file", Label = "Reminder feed file (JSON)", Kind = SettingKind.Text, Group = "Calendar feed" },
                    new SettingField { Id = "status", Label = "Feed status", Kind = SettingKind.Info, Group = "Calendar feed" },
                    new SettingField { Id = "lead", Label = "Remind me this many minutes before", Kind = SettingKind.Int, Min = 0, Max = 240, Group = "Timing" },
                },
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
                Load = () => new Dictionary<string, string>
                {
                    ["file"] = _settings.Get("file", ""),
                    ["lead"] = LeadMinutes().ToString(CultureInfo.InvariantCulture),
                    ["status"] = StatusLine(),
                },
                Save = values =>
                {
                    string file;
                    if (values.TryGetValue("file", out file)) _settings.Set("file", (file ?? "").Trim());
                    string leadRaw;
                    if (values.TryGetValue("lead", out leadRaw))
                    {
                        int lead;
                        if (int.TryParse(leadRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out lead))
                            _settings.Set("lead", (lead < 0 ? 0 : (lead > 240 ? 240 : lead)).ToString(CultureInfo.InvariantCulture));
                    }
                    return _settings.Save();
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
            return "✓ " + count + " event(s)" + updated + nextText;
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
