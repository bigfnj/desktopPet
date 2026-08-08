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

        // The on-screen pet mix (which pet types, and how many of each, to restore next launch).
        // Returns a deep copy so callers can't mutate the stored snapshot.
        internal System.Collections.Generic.List<PetCountEntry> GetPetMix()
        {
            lock (_sync)
                return AppSettingsDocument.ClonePetMix(_settings.Pets)
                    ?? new System.Collections.Generic.List<PetCountEntry>();
        }

        internal bool SetPetMix(System.Collections.Generic.List<PetCountEntry> pets)
        {
            System.Collections.Generic.List<PetCountEntry> value =
                AppSettingsDocument.ClonePetMix(pets)
                ?? new System.Collections.Generic.List<PetCountEntry>();
            return Update(
                delegate { return !AppSettingsDocument.PetMixEquals(_settings.Pets, value); },
                delegate { _settings.Pets = value; });
        }

        // A pet type's size override level (1/2/3), or 0 when the pet follows the global size. id "" is
        // the active/default pet.
        internal int GetPetSizeLevel(string id)
        {
            lock (_sync) return GetPetSizeLevelNoLock(id);
        }

        private int GetPetSizeLevelNoLock(string id)
        {
            string key = id ?? "";
            if (_settings.PetSizes != null)
                foreach (PetSizeEntry entry in _settings.PetSizes)
                    if (entry != null &&
                        string.Equals(entry.Id ?? "", key, StringComparison.OrdinalIgnoreCase))
                        return ScalePolicy.ClampLevel(entry.Level);
            return 0;
        }

        /// <summary>
        /// The effective rendering/movement factor (1x/2x/4x) for a pet: its own size override when set,
        /// otherwise the global factor. Used when a pet type is staged.
        /// </summary>
        public int GetEffectivePetScaleFactor(string id)
        {
            lock (_sync)
            {
                int level = GetPetSizeLevelNoLock(id);
                return level >= ScalePolicy.MinimumLevel
                    ? ScalePolicy.FactorFromLevel(level)
                    : ScalePolicy.FactorFromLevel(ScalePolicy.ClampLevel(_settings.ScaleLevel));
            }
        }

        // Set (level 1/2/3) or clear (level 0 or out of range -> follow global) a pet's size override.
        internal bool SetPetSizeLevel(string id, int level)
        {
            string key = id ?? "";
            bool clear = level < ScalePolicy.MinimumLevel || level > ScalePolicy.MaximumLevel;
            return Update(
                delegate
                {
                    int current = GetPetSizeLevelNoLock(key);
                    return clear ? current != 0 : current != level;
                },
                delegate
                {
                    var list = _settings.PetSizes ?? new List<PetSizeEntry>();
                    list.RemoveAll(delegate (PetSizeEntry e)
                    {
                        return e == null ||
                            string.Equals(e.Id ?? "", key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (!clear) list.Add(new PetSizeEntry { Id = key, Level = level });
                    _settings.PetSizes = list;
                });
        }

        /// <summary>The settings-window theme mode: "system", "light", or "dark".</summary>
        public string GetThemeMode()
        {
            lock (_sync) return AppSettingsDocument.NormalizeThemeMode(_settings.ThemeMode);
        }

        public bool SetThemeMode(string mode)
        {
            string value = AppSettingsDocument.NormalizeThemeMode(mode);
            return Update(
                delegate { return !string.Equals(AppSettingsDocument.NormalizeThemeMode(_settings.ThemeMode), value, StringComparison.Ordinal); },
                delegate { _settings.ThemeMode = value; });
        }

        /// <summary>The chosen audio output device GUID (DirectSound); "" = the default device.</summary>
        public string GetAudioDeviceId()
        {
            lock (_sync) return AppSettingsDocument.NormalizeAudioDeviceId(_settings.AudioDeviceId);
        }

        public bool SetAudioDeviceId(string id)
        {
            string value = AppSettingsDocument.NormalizeAudioDeviceId(id);
            return Update(
                delegate { return !string.Equals(AppSettingsDocument.NormalizeAudioDeviceId(_settings.AudioDeviceId), value, StringComparison.Ordinal); },
                delegate { _settings.AudioDeviceId = value; });
        }

        /// <summary>True unless the pet type's animation sound is muted (per-pet sound toggle). id "" = active.</summary>
        public bool IsPetSoundEnabled(string id)
        {
            lock (_sync) return !IsPetMutedNoLock(id ?? "");
        }

        private bool IsPetMutedNoLock(string key)
        {
            if (_settings.MutedPets != null)
                foreach (string m in _settings.MutedPets)
                    if (string.Equals(m ?? "", key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal bool SetPetSoundEnabled(string id, bool enabled)
        {
            string key = id ?? "";
            return Update(
                delegate { return IsPetMutedNoLock(key) == enabled; },   // muted==enabled means the state must flip
                delegate
                {
                    var list = _settings.MutedPets ?? new List<string>();
                    list.RemoveAll(delegate (string m) { return string.Equals(m ?? "", key, StringComparison.OrdinalIgnoreCase); });
                    if (!enabled) list.Add(key);
                    _settings.MutedPets = list;
                });
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
