using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopPet.Modules;
using DesktopPet.ModuleKit;

namespace DesktopPet.ReminderModule
{
    /// <summary>
    /// The pet reads one or more calendar feeds and announces each event a few minutes before it starts. Up to
    /// <see cref="MaxSlots"/> feeds are configured independently (each a Local file, a Calendar URL / ICS, or the
    /// running desktop Outlook), and each carries its OWN name and speech style, so a Home event and a Work event
    /// can look different in the bubble. A single UI-thread WinForms timer polls the aggregated feed, fires any
    /// due reminders through <see cref="IHost.SayAll"/> in that feed's style, and remembers which fired so a
    /// restart never re-nags.
    /// </summary>
    public sealed class ReminderModule : IModule
    {
        internal const string Id = "reminder";
        private const int DefaultLeadMinutes = 5;
        private const int TickMilliseconds = 20 * 1000;
        private const int MaxSlots = 5;

        // Per-slot feed types. "Off" is a real choice so a slot can be parked without deleting its settings.
        private const string SourceOff = "Off";
        private const string SourceLocalFile = "Local file";
        private const string SourceCalendarUrl = "Calendar URL (ICS)";
        private const string SourceOutlook = "Local Outlook";

        private static readonly string[] LegacyStyleKeys =
        {
            SpeechStyleSettings.FontKey, SpeechStyleSettings.SizeKey, SpeechStyleSettings.BoldKey,
            SpeechStyleSettings.ItalicKey, SpeechStyleSettings.UnderlineKey, SpeechStyleSettings.ColorKey,
        };

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
            Version = "1.4.1",   // 1.4.1: a per-calendar "play a chime" checkbox, so one calendar can be silent
                                 //        while another sounds; the global chime switch is the master over all.
                                 // 1.4.0: per-calendar chime -- each slot can Browse for its own WAV/MP3 sound
                                 //        (blank = the built-in chime); the global chime switch stays the master.
                                 // 1.3.0: up to 5 independent calendar feeds, each with its OWN name and speech
                                 //        style (font/size/colour/bold/italic/underline); the single "source" a
                                 //        1.2.x user had is migrated into slot 1.
                                 // 1.2.0: multiple lead times (e.g. 15 & 5), quiet hours, an optional chime,
                                 //        the event location in the announcement, and module-owned speech styling
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
            MigrateLegacy();
            _source = BuildSource();
            LoadFired();

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildTrayItem(), BuildJoinTrayItem(), BuildAgendaTrayItem() });

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

        // Carry a 1.2.x single-source config into slot 1 exactly once, so an existing user's feed + style survive
        // the upgrade. Keyed on a marker so it never re-runs and never stomps a slot the user has since edited.
        private void MigrateLegacy()
        {
            if (string.Equals(_settings.Get("migratedSlots", ""), "1", StringComparison.Ordinal)) return;
            string legacySource = _settings.Get("source", "");
            if (!string.IsNullOrEmpty(legacySource))
            {
                _settings.Set(SlotKey(1, "type"), legacySource);
                _settings.Set(SlotKey(1, "url"), _settings.Get("url", ""));
                _settings.Set(SlotKey(1, "file"), _settings.Get("file", ""));
                foreach (string k in LegacyStyleKeys)
                {
                    string val = _settings.Get(k, "");
                    if (!string.IsNullOrEmpty(val)) _settings.Set(SlotId(1) + "." + k, val);
                }
            }
            _settings.Set("migratedSlots", "1");
            _settings.Save();
        }

        // Build the aggregated source from the saved slots. Sources take live getters, so a URL/file change needs
        // no rebuild; only a feed-TYPE change does, which is why this is re-run on Save.
        private ICalendarSource BuildSource()
        {
            var slots = new List<AggregateCalendarSource.Slot>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                ICalendarSource src = BuildSlotSource(i, _settings.Get(SlotKey(i, "type"), SourceOff));
                if (src == null) continue;
                slots.Add(new AggregateCalendarSource.Slot
                {
                    Id = SlotId(i),
                    Label = _settings.Get(SlotKey(i, "label"), ""),
                    Source = src,
                });
            }
            return new AggregateCalendarSource(slots);
        }

        // `i` is a method parameter (fresh per call), so capturing it in the getter is safe -- no for-loop capture trap.
        private ICalendarSource BuildSlotSource(int i, string type)
        {
            if (string.Equals(type, SourceCalendarUrl, StringComparison.Ordinal))
                return new IcsUrlSource(() => _settings.Get(SlotKey(i, "url"), ""));
            if (string.Equals(type, SourceOutlook, StringComparison.Ordinal))
                return new OutlookComSource();
            if (string.Equals(type, SourceLocalFile, StringComparison.Ordinal))
                return new LocalJsonSource(() => _settings.Get(SlotKey(i, "file"), ""));
            return null;   // Off / unknown -> not polled
        }

        // --- the tick ---------------------------------------------------------------------------------

        private void CheckDue()
        {
            try
            {
                CalendarSnapshot snap = _source.Fetch();
                _lastSnapshot = snap;
                if (snap == null) return;
                // A combined error means one or more slots failed; log it but keep going -- the healthy slots'
                // events are still in snap.Events and must still fire.
                if (!string.IsNullOrEmpty(snap.Error)) _host.Log(Id, "reminder feed: " + snap.Error);

                IReadOnlyList<CalendarEvent> events = snap.Events ?? (IReadOnlyList<CalendarEvent>)Array.Empty<CalendarEvent>();

                // Keep the fired set bounded: drop composite ids whose EVENT no longer appears in the feed.
                // Fired ids are "<eventId>@<lead>", so compare on the event-id part.
                var feedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (CalendarEvent e in events) if (e != null && e.Id != null) feedIds.Add(e.Id);
                bool changed = _fired.RemoveWhere(id => !feedIds.Contains(ReminderScheduler.EventIdOf(id))) > 0;

                DateTimeOffset now = DateTimeOffset.Now;
                // Quiet hours: skip announcing WITHOUT marking fired, so an event still fires once the window
                // ends if it is still inside its lead time.
                bool quiet = QuietHours.IsQuiet(now, _settings.Get("quietFrom", ""), _settings.Get("quietTo", ""));
                if (!quiet)
                {
                    bool chime = _settings.GetBool("chime", true);
                    Dictionary<string, SpeechStyle> styleBySlot = StyleBySlot();
                    foreach (DueReminder due in ReminderScheduler.DueNowMulti(Schedulable(events), now, Leads(), _fired))
                    {
                        if (chime && SlotChimeOn(due.Event.SourceId)) Chime.Play(_host, SlotChimePath(due.Event.SourceId));
                        SpeechStyle style;
                        styleBySlot.TryGetValue(due.Event.SourceId ?? "", out style);
                        _host.SayAll(FormatReminder(due.Event, now, SlotLabel(due.Event.SourceId)), style);
                        _fired.Add(due.FiredId);
                        changed = true;
                    }
                    CheckPersonal(now);
                }
                MaybeBriefing(now, quiet);
                if (changed) SaveFired();
            }
            catch (Exception ex)
            {
                try { _host.Log(Id, "reminder tick failed: " + ex.Message); } catch { }
            }
        }

        private static string FormatReminder(CalendarEvent e, DateTimeOffset now, string sourceLabel)
        {
            string title = string.IsNullOrWhiteSpace(e.Title) ? "an event" : e.Title.Trim();
            int mins = (int)Math.Round((e.Start - now).TotalMinutes);
            string line = mins <= 0 ? title + " is starting now."
                : (mins == 1 ? title + " starts in 1 minute." : title + " starts in " + mins + " minutes.");
            if (!string.IsNullOrWhiteSpace(e.Location))
                line += " (" + e.Location.Trim() + ")";
            string ju, jk;
            if (MeetingLinkDetector.TryFind(e, out ju, out jk))
                line += " Join link is in the tray.";
            if (!string.IsNullOrWhiteSpace(sourceLabel))
                line = sourceLabel.Trim() + ": " + line;
            return line;
        }

        // Every slot's SpeechStyle, keyed by slot id, computed once per tick.
        private Dictionary<string, SpeechStyle> StyleBySlot()
        {
            var map = new Dictionary<string, SpeechStyle>(StringComparer.Ordinal);
            for (int i = 1; i <= MaxSlots; i++)
                map[SlotId(i)] = SpeechStyleSettings.ToStyle(_settings, SlotId(i) + ".");
            return map;
        }

        private string SlotLabel(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return "";
            for (int i = 1; i <= MaxSlots; i++)
                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal))
                    return _settings.Get(SlotKey(i, "label"), "");
            return "";
        }

        private string SlotChimePath(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return "";
            for (int i = 1; i <= MaxSlots; i++)
                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal))
                    return _settings.Get(SlotKey(i, "chime"), "");
            return "";
        }

        private bool SlotChimeOn(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return true;
            for (int i = 1; i <= MaxSlots; i++)
                if (string.Equals(SlotId(i), sourceId, StringComparison.Ordinal))
                    return _settings.GetBool(SlotKey(i, "chimeOn"), true);
            return true;
        }

        // --- settings ---------------------------------------------------------------------------------

        private static string SlotId(int i) { return "cal" + i.ToString(CultureInfo.InvariantCulture); }
        private static string SlotKey(int i, string key) { return SlotId(i) + "." + key; }

        private int LeadMinutes()
        {
            int lead = _settings.GetInt("lead", DefaultLeadMinutes);
            return lead < 0 ? 0 : (lead > 240 ? 240 : lead);
        }

        // The full options schema: a group per calendar slot (feed type + name + url/file + its own speech style),
        // then the shared timing + quiet-hours + status fields.
        private SettingField[] BuildSchema()
        {
            var fields = new List<SettingField>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                string g = "Calendar " + i.ToString(CultureInfo.InvariantCulture);
                fields.Add(new SettingField { Id = SlotKey(i, "type"), Label = "Feed type", Kind = SettingKind.Enum, Options = new[] { SourceOff, SourceLocalFile, SourceCalendarUrl, SourceOutlook }, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "label"), Label = "Name (spoken with the reminder, e.g. Home / Work)", Kind = SettingKind.Text, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "url"), Label = "Calendar URL (Google/Outlook secret .ics)", Kind = SettingKind.Text, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "file"), Label = "Reminder feed file (JSON)", Kind = SettingKind.Text, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "chimeOn"), Label = "Play a chime for this calendar", Kind = SettingKind.Bool, Group = g });
                fields.Add(new SettingField { Id = SlotKey(i, "chime"), Label = "Chime sound file (blank = built-in; use Browse below)", Kind = SettingKind.Text, Group = g });
                fields.AddRange(SpeechStyleSettings.Fields(g, SlotId(i) + "."));
            }
            fields.Add(new SettingField { Id = "personalChimeOn", Label = "Play a chime for personal reminders", Kind = SettingKind.Bool, Group = "Personal reminder style & chime" });
            fields.Add(new SettingField { Id = "personalChime", Label = "Chime sound file (blank = built-in; use Browse below)", Kind = SettingKind.Text, Group = "Personal reminder style & chime" });
            fields.AddRange(SpeechStyleSettings.Fields("Personal reminder style & chime", "personal."));
            fields.Add(new SettingField { Id = "leads", Label = "Remind me these many minutes before (comma-separated, e.g. 15,5)", Kind = SettingKind.Text, Group = "Timing" });
            fields.Add(new SettingField { Id = "chime", Label = "Play chimes with reminders (master switch for all calendars)", Kind = SettingKind.Bool, Group = "Timing" });
            fields.Add(new SettingField { Id = "skipDeclined", Label = "Skip meetings I've declined (Outlook only)", Kind = SettingKind.Bool, Group = "Filtering" });
            fields.Add(new SettingField { Id = "skipAllDay", Label = "Skip all-day events", Kind = SettingKind.Bool, Group = "Filtering" });
            fields.Add(new SettingField { Id = "quietFrom", Label = "Quiet hours start (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" });
            fields.Add(new SettingField { Id = "quietTo", Label = "Quiet hours end (HH:mm, 24h; blank = off)", Kind = SettingKind.Text, Group = "Quiet hours" });
            fields.Add(new SettingField { Id = "briefingOn", Label = "Read me the day's agenda each morning", Kind = SettingKind.Bool, Group = "Daily briefing" });
            fields.Add(new SettingField { Id = "briefingTime", Label = "Briefing time (HH:mm, 24h)", Kind = SettingKind.Text, Group = "Daily briefing" });
            fields.Add(new SettingField { Id = "status", Label = "Feed status", Kind = SettingKind.Info, Group = "Status" });
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
                Actions = BuildActions(),
                Lists = new[] { BuildPersonalListCard() },
                Load = () =>
                {
                    var values = new Dictionary<string, string>();
                    for (int i = 1; i <= MaxSlots; i++)
                    {
                        values[SlotKey(i, "type")] = _settings.Get(SlotKey(i, "type"), SourceOff);
                        values[SlotKey(i, "label")] = _settings.Get(SlotKey(i, "label"), "");
                        values[SlotKey(i, "url")] = _settings.Get(SlotKey(i, "url"), "");
                        values[SlotKey(i, "file")] = _settings.Get(SlotKey(i, "file"), "");
                        values[SlotKey(i, "chimeOn")] = _settings.GetBool(SlotKey(i, "chimeOn"), true) ? "true" : "false";
                        values[SlotKey(i, "chime")] = _settings.Get(SlotKey(i, "chime"), "");
                        SpeechStyleSettings.AddLoadValues(values, _settings, SlotId(i) + ".");
                    }
                    values["personalChimeOn"] = _settings.GetBool("personalChimeOn", true) ? "true" : "false";
                    values["personalChime"] = _settings.Get("personalChime", "");
                    SpeechStyleSettings.AddLoadValues(values, _settings, "personal.");
                    values["leads"] = LeadsText();
                    values["chime"] = _settings.GetBool("chime", true) ? "true" : "false";
                    values["skipDeclined"] = _settings.GetBool("skipDeclined", true) ? "true" : "false";
                    values["skipAllDay"] = _settings.GetBool("skipAllDay", false) ? "true" : "false";
                    values["quietFrom"] = _settings.Get("quietFrom", "");
                    values["quietTo"] = _settings.Get("quietTo", "");
                    values["briefingOn"] = _settings.GetBool("briefingOn", false) ? "true" : "false";
                    values["briefingTime"] = _settings.Get("briefingTime", "08:00");
                    values["status"] = StatusLine();
                    return values;
                },
                Save = values =>
                {
                    string v;
                    for (int i = 1; i <= MaxSlots; i++)
                    {
                        if (values.TryGetValue(SlotKey(i, "type"), out v) && !string.IsNullOrWhiteSpace(v)) _settings.Set(SlotKey(i, "type"), v.Trim());
                        if (values.TryGetValue(SlotKey(i, "label"), out v)) _settings.Set(SlotKey(i, "label"), (v ?? "").Trim());
                        if (values.TryGetValue(SlotKey(i, "url"), out v)) _settings.Set(SlotKey(i, "url"), (v ?? "").Trim());
                        if (values.TryGetValue(SlotKey(i, "file"), out v)) _settings.Set(SlotKey(i, "file"), (v ?? "").Trim());
                        if (values.TryGetValue(SlotKey(i, "chimeOn"), out v)) { bool cb; if (bool.TryParse(v, out cb)) _settings.Set(SlotKey(i, "chimeOn"), cb ? "true" : "false"); }
                        if (values.TryGetValue(SlotKey(i, "chime"), out v)) _settings.Set(SlotKey(i, "chime"), (v ?? "").Trim());
                        SpeechStyleSettings.Save(_settings, values, SlotId(i) + ".");
                    }
                    if (values.TryGetValue("personalChimeOn", out v)) { bool pb; if (bool.TryParse(v, out pb)) _settings.Set("personalChimeOn", pb ? "true" : "false"); }
                    if (values.TryGetValue("personalChime", out v)) _settings.Set("personalChime", (v ?? "").Trim());
                    SpeechStyleSettings.Save(_settings, values, "personal.");
                    if (values.TryGetValue("leads", out v)) _settings.Set("leads", NormalizeLeads(v));
                    if (values.TryGetValue("chime", out v)) { bool b; if (bool.TryParse(v, out b)) _settings.Set("chime", b ? "true" : "false"); }
                    if (values.TryGetValue("skipDeclined", out v)) { bool sb; if (bool.TryParse(v, out sb)) _settings.Set("skipDeclined", sb ? "true" : "false"); }
                    if (values.TryGetValue("skipAllDay", out v)) { bool sb; if (bool.TryParse(v, out sb)) _settings.Set("skipAllDay", sb ? "true" : "false"); }
                    if (values.TryGetValue("quietFrom", out v)) _settings.Set("quietFrom", (v ?? "").Trim());
                    if (values.TryGetValue("quietTo", out v)) _settings.Set("quietTo", (v ?? "").Trim());
                    if (values.TryGetValue("briefingOn", out v)) { bool bb; if (bool.TryParse(v, out bb)) _settings.Set("briefingOn", bb ? "true" : "false"); }
                    if (values.TryGetValue("briefingTime", out v)) _settings.Set("briefingTime", (v ?? "").Trim());
                    bool ok = _settings.Save();
                    _source = BuildSource();   // a feed-type change takes effect on the next tick
                    return ok;
                },
            };
        }

        // A "Browse for a chime" button per calendar card, plus the shared "Check now". A PaneAction runs on the
        // UI thread (per the ABI), so the file dialog is safe here with no host change; ReloadPaneAfter refreshes
        // the chime text box to the chosen path so a later Apply reads it back rather than clobbering it.
        private PaneAction[] BuildActions()
        {
            var actions = new List<PaneAction>();
            for (int i = 1; i <= MaxSlots; i++)
            {
                int slot = i;   // capture a per-iteration copy, not the shared loop variable
                actions.Add(new PaneAction
                {
                    Label = "Browse for a chime…",
                    Group = "Calendar " + slot.ToString(CultureInfo.InvariantCulture),
                    ReloadPaneAfter = true,
                    InvokeAsync = () => System.Threading.Tasks.Task.FromResult(BrowseChime(slot)),
                });
                actions.Add(new PaneAction
                {
                    Label = "Test this reminder",
                    Group = "Calendar " + slot.ToString(CultureInfo.InvariantCulture),
                    ReloadPaneAfter = false,   // no settings change: don't reload and lose other unsaved edits
                    InvokeAsync = () => System.Threading.Tasks.Task.FromResult(TestReminder(slot)),
                });
            }
            actions.Add(new PaneAction
            {
                Label = "Browse for a chime…",
                Group = "Personal reminder style & chime",
                ReloadPaneAfter = true,
                InvokeAsync = () => System.Threading.Tasks.Task.FromResult(BrowsePersonalChime()),
            });
            actions.Add(new PaneAction
            {
                Label = "Check now",
                Group = "Status",
                ReloadPaneAfter = true,
                InvokeAsync = () => { CheckDue(); return System.Threading.Tasks.Task.FromResult(StatusLine()); },
            });
            return actions.ToArray();
        }

        // Fire a sample announcement in this slot's name, style, and chime so the user can see and hear it while
        // configuring, instead of waiting for a real event. Uses the SAVED settings (a PaneAction can't read the
        // pane's unsaved edits), so the status reminds the user to Apply first to preview pending changes.
        private string TestReminder(int slot)
        {
            try
            {
                string label = _settings.Get(SlotKey(slot, "label"), "");
                string name = string.IsNullOrWhiteSpace(label) ? ("Calendar " + slot.ToString(CultureInfo.InvariantCulture)) : label.Trim();
                SpeechStyle style = SpeechStyleSettings.ToStyle(_settings, SlotId(slot) + ".");
                if (_settings.GetBool(SlotKey(slot, "chimeOn"), true))
                    Chime.Play(_host, _settings.Get(SlotKey(slot, "chime"), ""));
                _host.SayAll(name + ": this is a test reminder in this calendar's style.", style);
                return "✓ test sent. It uses saved settings, so Apply first to preview pending edits.";
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        // Open a file picker and, on OK, persist the chosen sound as a chime. Best-effort: a cancel or any error
        // just leaves the current setting. The host accepts WAV or MP3 up to 16 MiB; reject a larger pick here
        // with a clear message rather than a silent no-sound at reminder time.
        private string BrowseChime(int slot)
        {
            return BrowseChimeInto(SlotKey(slot, "chime"), "Choose a chime sound (Calendar " + slot.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private string BrowsePersonalChime()
        {
            return BrowseChimeInto("personalChime", "Choose a chime sound (personal reminders)");
        }

        private string BrowseChimeInto(string settingKey, string title)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = title;
                    dlg.Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    string current = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        try
                        {
                            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(current);
                            dlg.FileName = System.IO.Path.GetFileName(current);
                        }
                        catch { }
                    }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return "Chime unchanged.";
                    string path = (dlg.FileName ?? "").Trim();
                    long len;
                    try { len = new System.IO.FileInfo(path).Length; } catch { len = 0; }
                    if (len > 8 * 1024 * 1024)
                        return "✗ that file is over 8 MiB; pick a short chime.";
                    _settings.Set(settingKey, path);
                    _settings.Save();
                    return "✓ chime set: " + System.IO.Path.GetFileName(path);
                }
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        // Speak the rest of today's events on demand.
        private TrayItem BuildAgendaTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 5,
                DynamicText = () => "Read today's agenda",
                Click = () =>
                {
                    try { _lastSnapshot = _source.Fetch(); } catch { }
                    _host.SayAll(AgendaText(DateTimeOffset.Now), null);
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

        // A one-click "Join" for a video meeting that is happening now or about to. Only shows a live label when
        // an ongoing/imminent event actually carries a Teams/Zoom/Meet/Webex link; otherwise it is a no-op hint.
        private TrayItem BuildJoinTrayItem()
        {
            return new TrayItem
            {
                Group = 40,
                Order = 20,
                DynamicText = () =>
                {
                    CalendarEvent m = BestJoinable();
                    return m == null
                        ? "No meeting to join right now"
                        : "Join: " + (string.IsNullOrWhiteSpace(m.Title) ? "meeting" : m.Title.Trim());
                },
                Click = () =>
                {
                    CalendarEvent m = BestJoinable();
                    string url, kind;
                    if (m != null && MeetingLinkDetector.TryFind(m, out url, out kind)) OpenUrl(url);
                },
            };
        }

        // The best meeting to "Join" now: among events from ~10 min before their start until their end, the one
        // nearest to now that actually has a join link.
        private CalendarEvent BestJoinable()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null || snap.Events == null) return null;
            DateTimeOffset now = DateTimeOffset.Now;
            CalendarEvent best = null;
            double bestDist = double.MaxValue;
            foreach (CalendarEvent e in snap.Events)
            {
                if (e == null) continue;
                DateTimeOffset end = e.End ?? e.Start.AddMinutes(60);
                if (now < e.Start.AddMinutes(-10) || now > end) continue;   // not ongoing/imminent
                string url, kind;
                if (!MeetingLinkDetector.TryFind(e, out url, out kind)) continue;
                double dist = Math.Abs((e.Start - now).TotalMinutes);
                if (dist < bestDist) { bestDist = dist; best = e; }
            }
            return best;
        }

        private void OpenUrl(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return;
                if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;   // never launch a non-web scheme
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) { try { _host.Log(Id, "join link failed: " + ex.Message); } catch { } }
        }

        // --- agenda + daily briefing -----------------------------------------------------------------

        // The events that may schedule/announce, after the announce filter (feature: skip declined / all-day).
        private IReadOnlyList<CalendarEvent> Schedulable(IReadOnlyList<CalendarEvent> events)
        {
            if (events == null) return Array.Empty<CalendarEvent>();
            var kept = new List<CalendarEvent>(events.Count);
            foreach (CalendarEvent e in events) if (e != null && PassesFilter(e)) kept.Add(e);
            return kept;
        }

        // Whether an event is announced at all: a single gate used by scheduling, the agenda, and "next
        // upcoming" so all three agree. Response status is only known from Outlook (an .ics feed doesn't carry
        // "my" status), so skip-declined is a no-op for ICS feeds, which is the correct, safe default.
        private bool PassesFilter(CalendarEvent e)
        {
            if (e == null) return false;
            if (_settings.GetBool("skipDeclined", true) &&
                string.Equals(e.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase)) return false;
            if (_settings.GetBool("skipAllDay", false) && e.AllDay) return false;
            return true;
        }

        // A spoken summary of what's left today across every calendar.
        private string AgendaText(DateTimeOffset now)
        {
            CalendarSnapshot snap = _lastSnapshot;
            var today = new List<CalendarEvent>();
            if (snap != null && snap.Events != null)
            {
                DateTimeOffset endOfDay = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 59, 59, now.Offset);
                foreach (CalendarEvent e in snap.Events)
                {
                    if (e == null || !PassesFilter(e)) continue;
                    if (e.Start < now || e.Start > endOfDay) continue;
                    today.Add(e);
                }
            }
            today.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (today.Count == 0) return "Nothing left on your calendar today.";

            int shown = Math.Min(today.Count, 6);
            var parts = new List<string>();
            for (int i = 0; i < shown; i++)
            {
                CalendarEvent e = today[i];
                string t = string.IsNullOrWhiteSpace(e.Title) ? "an event" : e.Title.Trim();
                parts.Add(t + " at " + e.Start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            }
            string more = today.Count > shown ? ", and " + (today.Count - shown) + " more" : "";
            string count = today.Count == 1 ? "1 event left today" : today.Count + " events left today";
            return "You have " + count + ": " + string.Join("; ", parts) + more + ".";
        }

        // Once a day, at the configured time, read the agenda. Skipped during quiet hours; marks the date so it
        // fires exactly once even across restarts. A late start after the time still gets the briefing that day.
        private void MaybeBriefing(DateTimeOffset now, bool quiet)
        {
            if (quiet) return;
            if (!_settings.GetBool("briefingOn", false)) return;
            int mins;
            if (!TryParseHhmm(_settings.Get("briefingTime", "08:00"), out mins)) return;
            DateTimeOffset todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddMinutes(mins);
            if (now < todayAt) return;
            string todayKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(_settings.Get("briefingLast", ""), todayKey, StringComparison.Ordinal)) return;
            _settings.Set("briefingLast", todayKey);
            _settings.Save();
            _host.SayAll(AgendaText(now), null);
        }

        private static bool TryParseHhmm(string s, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] parts = s.Trim().Split(':');
            int h, m;
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) &&
                h >= 0 && h < 24 && m >= 0 && m < 60)
            {
                minutes = h * 60 + m;
                return true;
            }
            return false;
        }

        // --- typed personal reminders (independent of any calendar) -----------------------------------

        private List<PersonalReminder> LoadPersonal()
        {
            var list = new List<PersonalReminder>();
            string raw = _settings.Get("personal", "");
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string line in raw.Split('\n'))
            {
                PersonalReminder r = PersonalReminder.Decode(line.Trim());
                if (r != null) list.Add(r);
            }
            return list;
        }

        private void SavePersonal(List<PersonalReminder> list)
        {
            _settings.Set("personal", string.Join("\n", list.Select(PersonalReminder.Encode)));
            _settings.Save();
        }

        // Announce any personal reminder that just came due, in the personal style + chime. Dedup is the
        // reminder's own LastFired stamp (bounded, unlike a global fired set); a one-off disables itself.
        private void CheckPersonal(DateTimeOffset now)
        {
            List<PersonalReminder> list = LoadPersonal();
            if (list.Count == 0) return;
            SpeechStyle style = SpeechStyleSettings.ToStyle(_settings, "personal.");
            bool chime = _settings.GetBool("chime", true) && _settings.GetBool("personalChimeOn", true);
            string chimePath = _settings.Get("personalChime", "");
            bool changed = false;
            foreach (PersonalReminder r in list)
            {
                if (r == null || !r.Enabled) continue;
                string firedKey;
                if (!IsPersonalDue(r, now, out firedKey)) continue;
                if (string.Equals(r.LastFired, firedKey, StringComparison.Ordinal)) continue;
                r.LastFired = firedKey;
                if (r.Kind == PersonalReminder.KindOnce) r.Enabled = false;
                changed = true;
                if (chime) Chime.Play(_host, chimePath);
                _host.SayAll(FormatPersonal(r), style);
            }
            if (changed) SavePersonal(list);
        }

        // The occurrence key a reminder would fire under right now, or false if it is not due. once: any time at
        // or after its moment (so a late start still delivers it). everyN: within 2 min of an interval boundary.
        // daily/weekdays: within 15 min of the time (forgives a slightly late tick or start, not hours).
        private static bool IsPersonalDue(PersonalReminder r, DateTimeOffset now, out string firedKey)
        {
            firedKey = null;
            switch (r.Kind)
            {
                case PersonalReminder.KindOnce:
                    if (now >= r.When) { firedKey = "once"; return true; }
                    return false;
                case PersonalReminder.KindEveryN:
                    if (r.IntervalMinutes <= 0) return false;
                    double since = (now - r.Anchor).TotalMinutes;
                    if (since < r.IntervalMinutes) return false;
                    long index = (long)(since / r.IntervalMinutes);
                    DateTimeOffset occ = r.Anchor.AddMinutes(index * (double)r.IntervalMinutes);
                    double d = (now - occ).TotalMinutes;
                    if (d >= 0 && d <= 2.0) { firedKey = "i" + index.ToString(CultureInfo.InvariantCulture); return true; }
                    return false;
                case PersonalReminder.KindDaily:
                case PersonalReminder.KindWeekdays:
                    if (r.Kind == PersonalReminder.KindWeekdays &&
                        (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)) return false;
                    DateTimeOffset todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddMinutes(r.TimeOfDayMinutes);
                    double dd = (now - todayAt).TotalMinutes;
                    if (dd >= 0 && dd <= 15.0) { firedKey = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); return true; }
                    return false;
            }
            return false;
        }

        private static string FormatPersonal(PersonalReminder r)
        {
            string t = string.IsNullOrWhiteSpace(r.Text) ? "reminder" : r.Text.Trim();
            return "Reminder: " + t;
        }

        private ListCard BuildPersonalListCard()
        {
            return new ListCard
            {
                Title = "Personal reminders",
                EmptyHint = "No personal reminders yet. Use “Add a reminder…” below.",
                LoadItems = () =>
                {
                    var items = new List<ListItem>();
                    foreach (PersonalReminder r in LoadPersonal())
                        items.Add(new ListItem { Id = r.Id, Label = r.Text, Detail = r.ScheduleSummary(), Checked = r.Enabled });
                    return items;
                },
                SetChecked = (id, on) => TogglePersonal(id, on),
                Actions = new[]
                {
                    new PaneAction
                    {
                        Label = "Add a reminder…",
                        ReloadPaneAfter = true,
                        InvokeAsync = () => System.Threading.Tasks.Task.FromResult(AddPersonalReminder()),
                    },
                    new PaneAction
                    {
                        Label = "Remove disabled",
                        ReloadPaneAfter = true,
                        InvokeAsync = () => System.Threading.Tasks.Task.FromResult(RemoveDisabledPersonal()),
                    },
                },
            };
        }

        private void TogglePersonal(string id, bool on)
        {
            List<PersonalReminder> list = LoadPersonal();
            bool changed = false;
            foreach (PersonalReminder r in list)
                if (r != null && r.Id == id && r.Enabled != on) { r.Enabled = on; changed = true; }
            if (changed) SavePersonal(list);
        }

        private string AddPersonalReminder()
        {
            try
            {
                string input;
                if (!PromptDialog.Show("Add a reminder",
                        "Type a schedule then the text.\r\nExamples:  daily 09:00 Standup   |   every 60m Stretch   |   in 30m Pizza   |   weekdays 17:00 Log off   |   2026-09-01 14:00 Dentist",
                        "", out input))
                    return "No reminder added.";
                PersonalReminder r;
                string err;
                if (!PersonalReminderParser.TryParse(input, DateTimeOffset.Now, out r, out err))
                    return "✗ " + err;
                List<PersonalReminder> list = LoadPersonal();
                list.Add(r);
                SavePersonal(list);
                return "✓ added: " + r.Text + " (" + r.ScheduleSummary() + ")";
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
        }

        private string RemoveDisabledPersonal()
        {
            List<PersonalReminder> list = LoadPersonal();
            int before = list.Count;
            List<PersonalReminder> kept = list.Where(r => r != null && r.Enabled).ToList();
            if (kept.Count == before) return "Nothing to remove (no disabled reminders).";
            SavePersonal(kept);
            return "✓ removed " + (before - kept.Count) + " disabled reminder(s).";
        }

        private string StatusLine()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null) return "Not checked yet.";
            int count = snap.Events != null ? snap.Events.Count : 0;
            string updated = snap.Updated != null ? ", updated " + snap.Updated.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
            CalendarEvent next = NextUpcoming();
            string nextText = next != null
                ? "; next: " + (string.IsNullOrWhiteSpace(next.Title) ? "an event" : next.Title.Trim())
                    + " at " + next.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : "; nothing upcoming";
            string src = _source != null ? _source.Name + ": " : "";
            string prefix = string.IsNullOrEmpty(snap.Error) ? "✓ " : "⚠ ";
            string err = string.IsNullOrEmpty(snap.Error) ? "" : " [" + snap.Error + "]";
            return prefix + src + count + " event(s)" + updated + nextText + err;
        }

        private CalendarEvent NextUpcoming()
        {
            CalendarSnapshot snap = _lastSnapshot;
            if (snap == null || snap.Events == null) return null;
            DateTimeOffset now = DateTimeOffset.Now;
            return snap.Events.Where(e => e != null && PassesFilter(e) && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
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
