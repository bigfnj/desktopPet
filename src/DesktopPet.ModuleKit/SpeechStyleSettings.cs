using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopPet.Modules;

namespace DesktopPet.ModuleKit
{
    /// <summary>
    /// Reusable speech-bubble style controls for a module's options pane, plus the reader that turns the saved
    /// values into a <see cref="SpeechStyle"/> to pass to IHost.Say/SayAll. Every speaking module gets the SAME
    /// font / size / weight / colour controls in a couple of lines, each stored in that module's OWN settings,
    /// so speech styling is module-owned yet consistent across modules. The host renders whatever style it is
    /// handed; nothing here is central. "Default" choices map to unset SpeechStyle fields, so the bubble keeps
    /// its own look for those.
    /// </summary>
    public static class SpeechStyleSettings
    {
        // Namespaced setting ids so they cannot collide with a module's own keys.
        public const string FontKey = "speechFont";
        public const string SizeKey = "speechSize";
        public const string BoldKey = "speechBold";
        public const string ItalicKey = "speechItalic";
        public const string UnderlineKey = "speechUnderline";
        public const string ColorKey = "speechColor";

        private const string DefaultChoice = "Default";
        private const int DefaultSize = 9;
        private const int MinSize = 6;
        private const int MaxSize = 24;

        // A short, safe menu of families present on essentially every Windows box (plus "Default" = the
        // bubble's own font). A dropdown beats a free-text box: a typo would silently fall back, and 300
        // installed families is not a menu. Arbitrary font entry can come later if anyone asks.
        private static readonly string[] Fonts =
            { DefaultChoice, "Segoe UI", "Arial", "Calibri", "Verdana", "Tahoma", "Georgia",
              "Times New Roman", "Consolas", "Comic Sans MS" };

        private static readonly string[] Colors =
            { DefaultChoice, "Black", "Blue", "Green", "Red", "Orange", "Purple", "Teal", "Gray" };

        private static readonly Dictionary<string, string> ColorHex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Black", "#000000" }, { "Blue", "#1E66F5" }, { "Green", "#2E8B57" },
                { "Red", "#CC3333" }, { "Orange", "#E08000" }, { "Purple", "#8A2BE2" },
                { "Teal", "#008080" }, { "Gray", "#606060" },
            };

        /// <summary>The style controls, to splice into an OptionsPane.Schema (put them in your own group).</summary>
        public static SettingField[] Fields(string group)
        {
            return new[]
            {
                new SettingField { Id = FontKey, Label = "Speech font", Kind = SettingKind.Enum, Options = Fonts, Group = group },
                new SettingField { Id = SizeKey, Label = "Speech size (pt)", Kind = SettingKind.Int, Min = MinSize, Max = MaxSize, Group = group },
                new SettingField { Id = BoldKey, Label = "Bold", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = ItalicKey, Label = "Italic", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = UnderlineKey, Label = "Underline", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = ColorKey, Label = "Text colour", Kind = SettingKind.Enum, Options = Colors, Group = group },
            };
        }

        /// <summary>Add the saved values of the style fields to a pane Load() dictionary.</summary>
        public static void AddLoadValues(IDictionary<string, string> values, IModuleSettings settings)
        {
            if (values == null || settings == null) return;
            values[FontKey] = settings.Get(FontKey, DefaultChoice);
            values[SizeKey] = ClampSize(settings.GetInt(SizeKey, DefaultSize)).ToString(CultureInfo.InvariantCulture);
            values[BoldKey] = settings.GetBool(BoldKey, false) ? "true" : "false";
            values[ItalicKey] = settings.GetBool(ItalicKey, false) ? "true" : "false";
            values[UnderlineKey] = settings.GetBool(UnderlineKey, false) ? "true" : "false";
            values[ColorKey] = settings.Get(ColorKey, DefaultChoice);
        }

        /// <summary>Persist the style fields from a pane Save() values dictionary into the module settings.
        /// The caller still owns calling settings.Save().</summary>
        public static void Save(IModuleSettings settings, IReadOnlyDictionary<string, string> values)
        {
            if (settings == null || values == null) return;
            string v;
            if (values.TryGetValue(FontKey, out v) && !string.IsNullOrWhiteSpace(v)) settings.Set(FontKey, v.Trim());
            if (values.TryGetValue(SizeKey, out v))
            {
                int n;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    settings.Set(SizeKey, ClampSize(n).ToString(CultureInfo.InvariantCulture));
            }
            SaveBool(settings, values, BoldKey);
            SaveBool(settings, values, ItalicKey);
            SaveBool(settings, values, UnderlineKey);
            if (values.TryGetValue(ColorKey, out v) && !string.IsNullOrWhiteSpace(v)) settings.Set(ColorKey, v.Trim());
        }

        /// <summary>Build the SpeechStyle to hand to IHost.Say/SayAll from the saved settings.</summary>
        public static SpeechStyle ToStyle(IModuleSettings settings)
        {
            if (settings == null) return null;
            string font = settings.Get(FontKey, DefaultChoice);
            string colorName = settings.Get(ColorKey, DefaultChoice);
            string colorHex = null;
            if (!IsDefault(colorName))
            {
                string hex;
                if (ColorHex.TryGetValue(colorName.Trim(), out hex)) colorHex = hex;
            }
            return new SpeechStyle
            {
                FontFamily = IsDefault(font) ? null : font.Trim(),
                FontSize = ClampSize(settings.GetInt(SizeKey, DefaultSize)),
                Bold = settings.GetBool(BoldKey, false),
                Italic = settings.GetBool(ItalicKey, false),
                Underline = settings.GetBool(UnderlineKey, false),
                TextColor = colorHex,
            };
        }

        private static void SaveBool(IModuleSettings s, IReadOnlyDictionary<string, string> values, string key)
        {
            string v; bool b;
            if (values.TryGetValue(key, out v) && bool.TryParse(v, out b)) s.Set(key, b ? "true" : "false");
        }

        private static bool IsDefault(string choice)
        {
            return string.IsNullOrWhiteSpace(choice) ||
                string.Equals(choice.Trim(), DefaultChoice, StringComparison.OrdinalIgnoreCase);
        }

        private static int ClampSize(int n) { return n < MinSize ? MinSize : (n > MaxSize ? MaxSize : n); }
    }
}
