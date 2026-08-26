using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopPet.Modules;

namespace DesktopPet.RemembranceModule
{
    /// <summary>
    /// Records a meeting (a selectable microphone + the system output over WASAPI loopback), transcribes it
    /// offline with a local Whisper, names it from the calendar (the Reminder module's "meeting.current"
    /// shared-context publish, else a timestamp), snapshots the screen on a hotkey, and purges the audio +
    /// snapshots after 72 hours while keeping the transcript. Start/stop and snapshot are on the tray and a
    /// global hotkey each. A visible "recording" indicator (tray text + a spoken cue) shows while capturing.
    /// </summary>
    public sealed class RemembranceModule : IModule
    {
        internal const string Id = "remembrance";
        private const int PurgeIntervalMs = 60 * 60 * 1000;   // hourly

        private const string DefaultRecordHotkey = "Ctrl+Alt+R";
        private const string DefaultSnapshotHotkey = "Ctrl+Alt+S";

        private IHost _host;
        private IModuleSettings _settings;
        private SynchronizationContext _ui;
        private System.Windows.Forms.Timer _purgeTimer;
        private EventHandler _purgeHandler;
        private readonly List<IDisposable> _hotkeys = new List<IDisposable>();

        private AudioRecorder _recorder;
        private CapturePaths _current;
        private string _currentMeetingName = "";
        private IReadOnlyList<string> _currentAttendees;
        private volatile bool _recording;
        private string _currentBase = "";
        private string _lastStatus = "Idle.";

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = Id,
            Name = "Remembrance",
            Version = "1.0.0",
            // Publishing/reading shared context + the capture permission flags are host 1.9.0.
            MinHostVersion = "1.9.0",
            Permissions = ModulePermissions.Microphone | ModulePermissions.SystemAudio
                | ModulePermissions.ScreenContext | ModulePermissions.Hotkey | ModulePermissions.Storage,
        };

        public void Init(IHost host)
        {
            _host = host;
            _settings = host.GetSettings(Id);
            _ui = SynchronizationContext.Current;

            host.AddOptionsPane(BuildOptionsPane());
            host.AddTrayItems(new[] { BuildRecordTrayItem(), BuildSnapshotTrayItem() });
            RegisterHotkeys();

            _purgeTimer = new System.Windows.Forms.Timer { Interval = PurgeIntervalMs };
            _purgeHandler = delegate { RunPurge(); };
            _purgeTimer.Tick += _purgeHandler;
            _purgeTimer.Start();
            RunPurge();

            try { host.HostShutdown += OnHostShutdown; } catch { }
        }

        public void Shutdown()
        {
            try { if (_recording) StopRecording(); } catch { }
            DisposeHotkeys();
            if (_purgeTimer != null)
            {
                try { _purgeTimer.Stop(); if (_purgeHandler != null) _purgeTimer.Tick -= _purgeHandler; _purgeTimer.Dispose(); }
                catch { }
                _purgeTimer = null;
            }
        }

        private void OnHostShutdown() { try { if (_recording) StopRecording(); } catch { } }

        // --- hotkeys ---------------------------------------------------------------------------------

        private void RegisterHotkeys()
        {
            DisposeHotkeys();
            Add(_settings.Get("recordHotkey", DefaultRecordHotkey), ToggleRecording);
            Add(_settings.Get("snapshotHotkey", DefaultSnapshotHotkey), TakeSnapshot);
        }

        private void Add(string combo, Action onPressed)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            try { IDisposable h = _host.RegisterHotkey(combo.Trim(), onPressed); if (h != null) _hotkeys.Add(h); }
            catch { }
        }

        private void DisposeHotkeys()
        {
            foreach (IDisposable h in _hotkeys) { try { h.Dispose(); } catch { } }
            _hotkeys.Clear();
        }

        // --- record / stop / snapshot ----------------------------------------------------------------

        private void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            if (_recording) return;
            try
            {
                MeetingContext mc = MeetingContext.Parse(_host.ReadContext(MeetingContext.Key));
                _currentMeetingName = mc.Name;
                _currentAttendees = mc.Attendees;

                var store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()),
                    _settings.GetBool("folderPerCapture", true));
                _current = store.NewCapture(mc.Name, DateTimeOffset.Now);
                _currentBase = _current.BaseName;

                _recorder = new AudioRecorder();
                _recorder.Start(_current.Audio,
                    _settings.GetBool("sysEnabled", true), _settings.Get("sysDevice", ""),
                    _settings.GetBool("micEnabled", true), _settings.Get("micDevice", ""));
                _recording = true;
                _lastStatus = "Recording: " + _currentBase;
                Announce("Recording started: " + _currentBase);
            }
            catch (Exception ex)
            {
                _recording = false;
                _lastStatus = "Could not start: " + ex.Message;
                try { _host.Log(Id, "start failed: " + ex.Message); } catch { }
                Announce("Could not start recording. " + ex.Message);
                try { if (_recorder != null) { _recorder.Dispose(); _recorder = null; } } catch { }
                _current = null;
            }
        }

        private void StopRecording()
        {
            if (!_recording || _recorder == null) return;
            AudioRecorder recorder = _recorder;
            CapturePaths paths = _current;
            string meetingName = _currentMeetingName;
            IReadOnlyList<string> attendees = _currentAttendees;
            string whisperExe = _settings.Get("whisperExe", "");
            string model = _settings.Get("whisperModel", "");

            _recording = false;   // flips the tray indicator immediately
            _recorder = null;
            _current = null;
            _lastStatus = "Saving: " + (paths != null ? paths.BaseName : "");
            Announce("Recording stopped. Saving and transcribing…");

            Task.Run(() =>
            {
                try
                {
                    string wav = recorder.Stop();
                    recorder.Dispose();
                    bool did;
                    Transcriber.Transcribe(wav ?? paths.Audio, paths.Transcript, whisperExe, model, meetingName, attendees, out did);
                    _lastStatus = (did ? "Transcribed: " : "Saved (Whisper not set up): ") + paths.BaseName;
                    Announce(did ? "Transcript ready." : "Recording saved. Set up Whisper to transcribe it.");
                }
                catch (Exception ex)
                {
                    _lastStatus = "Stop failed: " + ex.Message;
                    try { _host.Log(Id, "stop/transcribe failed: " + ex.Message); } catch { }
                }
            });
        }

        private void TakeSnapshot()
        {
            try
            {
                string dir, prefix;
                if (_current != null) { dir = _current.Directory; prefix = _current.SnapshotPrefix; }
                else
                {
                    var store = new CaptureStore(_settings.Get("storageLocation", CaptureStore.DefaultRoot()),
                        _settings.GetBool("folderPerCapture", true));
                    dir = store.Root;
                    System.IO.Directory.CreateDirectory(dir);
                    prefix = "snap";
                }
                string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
                string png = System.IO.Path.Combine(dir, prefix + " " + stamp + ".png");
                Announce(ScreenSnapshot.Capture(png) ? "Snapshot saved." : "Snapshot failed.");
            }
            catch (Exception ex) { try { _host.Log(Id, "snapshot failed: " + ex.Message); } catch { } }
        }

        // Marshal a host call to the UI thread (transcription completes on a background task).
        private void Announce(string text)
        {
            if (_ui != null) _ui.Post(delegate { try { _host.SayAll(text); } catch { } }, null);
            else try { _host.SayAll(text); } catch { }
        }

        private void RunPurge()
        {
            string root = _settings.Get("storageLocation", CaptureStore.DefaultRoot());
            bool perCapture = _settings.GetBool("folderPerCapture", true);
            Task.Run(() => { try { new CaptureStore(root, perCapture).Purge(); } catch { } });
        }

        // --- options ---------------------------------------------------------------------------------

        private SettingField[] BuildOptionsPane_Schema()
        {
            string[] renderNames = AudioDevices.RenderDevices().Select(d => d.Name).ToArray();
            string[] micNames = AudioDevices.CaptureDevices().Select(d => d.Name).ToArray();
            return new[]
            {
                new SettingField { Id = "sysEnabled", Label = "Record system audio (what you hear)", Kind = SettingKind.Bool, Group = "Sources" },
                new SettingField { Id = "sysDevice", Label = "System output device", Kind = SettingKind.Enum, Options = renderNames, Group = "Sources" },
                new SettingField { Id = "micEnabled", Label = "Record microphone", Kind = SettingKind.Bool, Group = "Sources" },
                new SettingField { Id = "micDevice", Label = "Microphone device", Kind = SettingKind.Enum, Options = micNames, Group = "Sources" },

                new SettingField { Id = "recordHotkey", Label = "Start/stop hotkey (e.g. Ctrl+Alt+R)", Kind = SettingKind.Text, Group = "Hotkeys" },
                new SettingField { Id = "snapshotHotkey", Label = "Snapshot hotkey (e.g. Ctrl+Alt+S)", Kind = SettingKind.Text, Group = "Hotkeys" },

                new SettingField { Id = "storageLocation", Label = "Where recordings are stored (blank = Documents\\Remembrance)", Kind = SettingKind.Text, Group = "Storage" },
                new SettingField { Id = "folderPerCapture", Label = "Create a folder per capture", Kind = SettingKind.Bool, Group = "Storage" },

                new SettingField { Id = "whisperExe", Label = "whisper-cli path (offline transcription)", Kind = SettingKind.Text, Group = "Transcription" },
                new SettingField { Id = "whisperModel", Label = "Whisper model file (e.g. ggml-base.en.bin)", Kind = SettingKind.Text, Group = "Transcription" },

                new SettingField { Id = "status", Label = "Status", Kind = SettingKind.Info, Group = "Status" },
            };
        }

        private OptionsPane BuildOptionsPane()
        {
            return new OptionsPane
            {
                Title = "Remembrance",
                Schema = BuildOptionsPane_Schema(),
                Actions = new[]
                {
                    new PaneAction { Label = "Browse for a storage folder…", Group = "Storage", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFolder("storageLocation")) },
                    new PaneAction { Label = "Browse for whisper-cli…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperExe", "whisper-cli", new[] { "exe" })) },
                    new PaneAction { Label = "Browse for a model…", Group = "Transcription", ReloadPaneAfter = true,
                        InvokeAsync = () => Task.FromResult(BrowseFile("whisperModel", "Whisper model", new[] { "bin" })) },
                    new PaneAction { Label = "Transcribe a WAV file…", Group = "Transcription", ReloadPaneAfter = false,
                        InvokeAsync = () => Task.FromResult(TranscribeExisting()) },
                },
                Load = () => new Dictionary<string, string>
                {
                    ["sysEnabled"] = _settings.GetBool("sysEnabled", true) ? "true" : "false",
                    ["sysDevice"] = _settings.Get("sysDevice", ""),
                    ["micEnabled"] = _settings.GetBool("micEnabled", true) ? "true" : "false",
                    ["micDevice"] = _settings.Get("micDevice", ""),
                    ["recordHotkey"] = _settings.Get("recordHotkey", DefaultRecordHotkey),
                    ["snapshotHotkey"] = _settings.Get("snapshotHotkey", DefaultSnapshotHotkey),
                    ["storageLocation"] = _settings.Get("storageLocation", ""),
                    ["folderPerCapture"] = _settings.GetBool("folderPerCapture", true) ? "true" : "false",
                    ["whisperExe"] = _settings.Get("whisperExe", ""),
                    ["whisperModel"] = _settings.Get("whisperModel", ""),
                    ["status"] = StatusLine(),
                },
                Save = values =>
                {
                    string v;
                    SaveBool(values, "sysEnabled");
                    if (values.TryGetValue("sysDevice", out v)) _settings.Set("sysDevice", (v ?? "").Trim());
                    SaveBool(values, "micEnabled");
                    if (values.TryGetValue("micDevice", out v)) _settings.Set("micDevice", (v ?? "").Trim());
                    if (values.TryGetValue("recordHotkey", out v)) _settings.Set("recordHotkey", (v ?? "").Trim());
                    if (values.TryGetValue("snapshotHotkey", out v)) _settings.Set("snapshotHotkey", (v ?? "").Trim());
                    if (values.TryGetValue("storageLocation", out v)) _settings.Set("storageLocation", (v ?? "").Trim());
                    SaveBool(values, "folderPerCapture");
                    if (values.TryGetValue("whisperExe", out v)) _settings.Set("whisperExe", (v ?? "").Trim());
                    if (values.TryGetValue("whisperModel", out v)) _settings.Set("whisperModel", (v ?? "").Trim());
                    bool ok = _settings.Save();
                    RegisterHotkeys();   // a changed combo takes effect without a restart
                    return ok;
                },
            };
        }

        private void SaveBool(IReadOnlyDictionary<string, string> values, string key)
        {
            string v; bool b;
            if (values.TryGetValue(key, out v) && bool.TryParse(v, out b)) _settings.Set(key, b ? "true" : "false");
        }

        private string StatusLine()
        {
            string root = _settings.Get("storageLocation", "");
            if (string.IsNullOrWhiteSpace(root)) root = CaptureStore.DefaultRoot();
            bool whisper = System.IO.File.Exists(_settings.Get("whisperExe", "")) && System.IO.File.Exists(_settings.Get("whisperModel", ""));
            int outs = Math.Max(0, AudioDevices.RenderDevices().Count - 1);   // minus the "System default" entry
            int mics = Math.Max(0, AudioDevices.CaptureDevices().Count - 1);
            string s = _lastStatus + "  |  devices: " + outs + " output, " + mics + " mic"
                + "  |  storage: " + root + "  |  Whisper: " + (whisper ? "configured" : "not set up");
            if (System.Windows.Forms.SystemInformation.TerminalServerSession)
                s += "  |  ⚠ Remote Desktop session: the machine's real mic and speakers are not presented here, so recording won't work. Run on the machine's own console. (Device dropdowns are read at startup; restart there to populate them.)";
            else if (mics == 0)
                s += "  |  ⚠ no microphone detected.";
            return s;
        }

        private string BrowseFolder(string settingKey)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Choose where recordings are stored";
                    string cur = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(cur)) { try { dlg.SelectedPath = cur; } catch { } }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    _settings.Set(settingKey, dlg.SelectedPath);
                    _settings.Save();
                    return "✓ storage: " + dlg.SelectedPath;
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        private string BrowseFile(string settingKey, string label, string[] extensions)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "Choose " + label;
                    dlg.CheckFileExists = true;
                    string filter = string.Join(";", extensions.Select(e => "*." + e));
                    dlg.Filter = label + " (" + filter + ")|" + filter + "|All files (*.*)|*.*";
                    string cur = _settings.Get(settingKey, "");
                    if (!string.IsNullOrWhiteSpace(cur)) { try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(cur); dlg.FileName = System.IO.Path.GetFileName(cur); } catch { } }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    _settings.Set(settingKey, (dlg.FileName ?? "").Trim());
                    _settings.Save();
                    return "✓ " + label + ": " + System.IO.Path.GetFileName(dlg.FileName);
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        // Transcribe an existing WAV the user picks (e.g. a kept recording, or one made before Whisper was set
        // up). Writes <name>.transcript.txt beside it. Runs Whisper on a background task so the pane stays live.
        private string TranscribeExisting()
        {
            string whisperExe = _settings.Get("whisperExe", "");
            string model = _settings.Get("whisperModel", "");
            if (!System.IO.File.Exists(whisperExe) || !System.IO.File.Exists(model))
                return "✗ Set the whisper-cli path and a model first.";
            try
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "Choose a WAV recording to transcribe";
                    dlg.Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return "Unchanged.";
                    string wav = dlg.FileName;
                    string transcript = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(wav),
                        System.IO.Path.GetFileNameWithoutExtension(wav) + ".transcript.txt");
                    string name = System.IO.Path.GetFileNameWithoutExtension(wav);
                    Task.Run(() =>
                    {
                        try
                        {
                            bool did;
                            Transcriber.Transcribe(wav, transcript, whisperExe, model, name, null, out did);
                            _lastStatus = did ? "Transcribed: " + name : "Transcription failed: " + name;
                            Announce(did ? "Transcript ready." : "Transcription failed.");
                        }
                        catch (Exception ex) { try { _host.Log(Id, "manual transcribe failed: " + ex.Message); } catch { } }
                    });
                    return "Transcribing " + name + "…";
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
        }

        // --- tray ------------------------------------------------------------------------------------

        private TrayItem BuildRecordTrayItem()
        {
            return new TrayItem
            {
                Group = 45,
                Order = 10,
                DynamicText = () => _recording ? ("● Recording: " + _currentBase + " (click to stop)") : "Start recording a meeting",
                Click = ToggleRecording,
            };
        }

        private TrayItem BuildSnapshotTrayItem()
        {
            return new TrayItem
            {
                Group = 45,
                Order = 20,
                DynamicText = () => "Snapshot the screen",
                Click = TakeSnapshot,
            };
        }
    }
}
