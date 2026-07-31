using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopPet
{
    /// <summary>
    /// Runtime facade over the versioned settings store. Values are held in one normalized snapshot;
    /// every durable update replaces the JSON file atomically.
    /// </summary>
    public class LocalData
    {
        private readonly object _sync = new object();
        private readonly AppSettingsStore _store;
        private AppSettingsDocument _settings;

        public string SettingsWarning { get; private set; }

        public LocalData()
            : this(new AppSettingsStore(AppPaths.SettingsFile, BuildLegacyCandidates()))
        {
        }

        internal LocalData(AppSettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException("store");
            LoadSettings();
        }

        public void LoadSettings()
        {
            lock (_sync)
            {
                _settings = _store.Load() ?? AppSettingsDocument.CreateDefault();
                SettingsWarning = _store.LastLoadWarning;
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
            }
        }

        public bool SetVolume(double volume)
        {
            volume = NormalizeVolume(volume);
            return Update(
                delegate { return Math.Abs(_settings.Volume - volume) > 0.000001; },
                delegate { _settings.Volume = volume; });
        }

        public float GetVolume()
        {
            lock (_sync)
                return (float)NormalizeVolume(_settings.Volume);
        }

        public bool SetScale(int level)
        {
            level = ScalePolicy.ClampLevel(level);
            return Update(
                delegate { return _settings.ScaleLevel != level; },
                delegate { _settings.ScaleLevel = level; });
        }

        /// <summary>The persisted UI level: 1, 2, or 3.</summary>
        public int GetScale()
        {
            lock (_sync)
                return ScalePolicy.ClampLevel(_settings.ScaleLevel);
        }

        /// <summary>The effective rendering/movement factor: 1x, 2x, or 4x.</summary>
        public int GetScaleFactor()
        {
            return ScalePolicy.FactorFromLevel(GetScale());
        }

        public bool GetMultiscreen()
        {
            lock (_sync) return _settings.MultiScreen;
        }

        public bool SetMultiscreen(bool multi)
        {
            return Update(
                delegate { return _settings.MultiScreen != multi; },
                delegate { _settings.MultiScreen = multi; });
        }

        public bool GetWindowForeground()
        {
            lock (_sync) return _settings.WindowForeground;
        }

        public bool SetWindowForeground(bool foreground)
        {
            return Update(
                delegate { return _settings.WindowForeground != foreground; },
                delegate { _settings.WindowForeground = foreground; });
        }

        public bool SetStealTaskbarFocus(bool steal)
        {
            return Update(
                delegate { return _settings.StealTaskbarFocus != steal; },
                delegate { _settings.StealTaskbarFocus = steal; });
        }

        public bool GetStealTaskbarFocus()
        {
            lock (_sync) return _settings.StealTaskbarFocus;
        }

        public int GetAutoStartPets()
        {
            lock (_sync)
                return Math.Max(
                    1,
                    Math.Min(AppSettingsDocument.MaximumAutoStartPets, _settings.AutoStartPets));
        }

        public bool SetAutoStartPets(int autostart)
        {
            autostart = Math.Max(
                1,
                Math.Min(AppSettingsDocument.MaximumAutoStartPets, autostart));
            return Update(
                delegate { return _settings.AutoStartPets != autostart; },
                delegate { _settings.AutoStartPets = autostart; });
        }

        public bool GetSpeechEnabled()
        {
            lock (_sync) return _settings.SpeechEnabled;
        }

        public bool SetSpeechEnabled(bool enabled)
        {
            return Update(
                delegate { return _settings.SpeechEnabled != enabled; },
                delegate { _settings.SpeechEnabled = enabled; });
        }

        public int GetSpeechDuration()
        {
            lock (_sync)
                return Math.Max(2, Math.Min(30, _settings.SpeechDurationSeconds));
        }

        public bool SetSpeechDuration(int seconds)
        {
            seconds = Math.Max(2, Math.Min(30, seconds));
            return Update(
                delegate { return _settings.SpeechDurationSeconds != seconds; },
                delegate { _settings.SpeechDurationSeconds = seconds; });
        }

        public bool SetXml(string xml, string folder)
        {
            string value = xml ?? "";
            return Update(
                delegate { return !string.Equals(_settings.Xml, value, StringComparison.Ordinal); },
                delegate { _settings.Xml = value; });
        }

        /// <summary>
        /// Atomically commit all persisted assets for a staged pet. The in-memory snapshot is
        /// restored if durable persistence is blocked or fails.
        /// </summary>
        public bool TrySetPetAssets(string xml, string images, string icon)
        {
            lock (_sync)
            {
                AppSettingsDocument before = CloneSettings(_settings);
                _settings.Xml = xml ?? "";
                _settings.Images = images ?? "";
                _settings.Icon = icon ?? "";
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
                if (_store.Save(_settings)) return true;
                _settings = before;
                SynchronizeLegacySettingsObject();
                return false;
            }
        }

        public string GetXml()
        {
            lock (_sync) return _settings.Xml ?? "";
        }

        public string LoadXML()
        {
            // Runtime pet replacement is validated and committed by StartUp. Legacy installpet.xml,
            // arbitrary URL, and direct file side channels are intentionally no longer consumed here.
            return GetXml();
        }

        public string GetImages()
        {
            lock (_sync) return _settings.Images ?? "";
        }

        public bool SetImages(string images)
        {
            string value = images ?? "";
            return Update(
                delegate { return !string.Equals(_settings.Images, value, StringComparison.Ordinal); },
                delegate { _settings.Images = value; });
        }

        public string GetIcon()
        {
            lock (_sync) return _settings.Icon ?? "";
        }

        public bool SetIcon(string icon)
        {
            string value = icon ?? "";
            return Update(
                delegate { return !string.Equals(_settings.Icon, value, StringComparison.Ordinal); },
                delegate { _settings.Icon = value; });
        }

        public bool IsFirstBoot()
        {
            return false;
        }

        public delegate void MyFunction(object source, FileSystemEventArgs e);

        public void ListenOnXMLChanged(MyFunction f)
        {
            // Not implemented in the portable build.
        }

        public void ListenOnOptionsChanged(MyFunction f)
        {
            // Not implemented in the portable build.
        }

        private bool Update(Func<bool> changed, Action apply)
        {
            lock (_sync)
            {
                if (!changed()) return true;
                AppSettingsDocument before = CloneSettings(_settings);
                apply();
                _settings.Normalize();
                SynchronizeLegacySettingsObject();
                if (_store.Save(_settings)) return true;
                _settings = before;
                SynchronizeLegacySettingsObject();
                return false;
            }
        }

        private static AppSettingsDocument CloneSettings(AppSettingsDocument source)
        {
            return AppSettingsStore.Clone(source);
        }

        private static double NormalizeVolume(double volume)
        {
            if (double.IsNaN(volume) || double.IsInfinity(volume)) return 0.3;
            return Math.Max(0.0, Math.Min(1.0, volume));
        }

        /// <summary>
        /// Keep the generated settings object coherent for old extension code, but never call its
        /// Save method. The canonical durable file is AppPaths.SettingsFile.
        /// </summary>
        private void SynchronizeLegacySettingsObject()
        {
            try
            {
                Properties.Settings.Default.Volume = (float)_settings.Volume;
                Properties.Settings.Default.Scale = _settings.ScaleLevel;
                Properties.Settings.Default.AutostartPets = _settings.AutoStartPets;
                Properties.Settings.Default.Multiscreen = _settings.MultiScreen;
                Properties.Settings.Default.WinForeground = _settings.WindowForeground;
                Properties.Settings.Default.StealTaskbarFocus = _settings.StealTaskbarFocus;
                Properties.Settings.Default.SpeechEnabled = _settings.SpeechEnabled;
                Properties.Settings.Default.SpeechDuration = _settings.SpeechDurationSeconds;
                Properties.Settings.Default.xml = _settings.Xml;
                Properties.Settings.Default.Images = _settings.Images;
                Properties.Settings.Default.Icon = _settings.Icon;
            }
            catch
            {
                // A corrupt legacy user.config cannot make the canonical settings unusable.
            }
        }

        private static IEnumerable<string> BuildLegacyCandidates()
        {
            var candidates = new List<string>(AppPaths.LegacySettingsFiles);
            try
            {
                string userConfig = System.Configuration.ConfigurationManager
                    .OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.PerUserRoamingAndLocal)
                    .FilePath;
                if (!string.IsNullOrWhiteSpace(userConfig))
                    candidates.Add(Path.GetFullPath(userConfig));
            }
            catch
            {
            }
            return candidates;
        }
    }
}
