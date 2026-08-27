using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MicaPDF
{
    /// <summary>
    /// Loads string catalogs from Strings/{lang}.json next to the exe.
    /// Add a language by dropping another JSON file and registering it in AvailableLanguages / Settings.
    /// </summary>
    public static class Loc
    {
        public const string System = "System";
        public const string English = "en";
        public const string Italian = "it";

        private static readonly Dictionary<string, Dictionary<string, string>> Catalogs =
            new(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);
        private static string _active = English;

        public static string ActiveLanguage => _active;

        public static event EventHandler? Changed;

        public static IReadOnlyList<(string Id, string DisplayName)> AvailableLanguages { get; } =
        [
            (System, "System"),
            (English, "English"),
            (Italian, "Italiano")
        ];

        static Loc()
        {
            LoadCatalog(English);
            LoadCatalog(Italian);
            if (Catalogs.TryGetValue(English, out var en))
                _fallback = en;
        }

        private static string StringsDirectory
        {
            get
            {
                var baseDir = AppContext.BaseDirectory;
                var beside = Path.Combine(baseDir, "Strings");
                if (Directory.Exists(beside))
                    return beside;
                // Dev fallback when running from project tree
                var project = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Strings"));
                return Directory.Exists(project) ? project : beside;
            }
        }

        private static void LoadCatalog(string languageId)
        {
            try
            {
                var path = Path.Combine(StringsDirectory, languageId + ".json");
                if (!File.Exists(path))
                    return;

                var json = File.ReadAllText(path, Encoding.UTF8);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map == null || map.Count == 0)
                    return;

                Catalogs[languageId] = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Missing/invalid catalog: fall back to English keys or key names.
            }
        }

        public static string ResolveLanguageId(string? preference)
        {
            if (string.Equals(preference, English, StringComparison.OrdinalIgnoreCase))
                return English;
            if (string.Equals(preference, Italian, StringComparison.OrdinalIgnoreCase))
                return Italian;

            try
            {
                var langs = Windows.System.UserProfile.GlobalizationPreferences.Languages;
                var primary = langs?.FirstOrDefault() ?? CultureInfo.CurrentUICulture.Name;
                if (!string.IsNullOrEmpty(primary) &&
                    primary.StartsWith("it", StringComparison.OrdinalIgnoreCase))
                    return Italian;
            }
            catch
            {
                if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                    .Equals("it", StringComparison.OrdinalIgnoreCase))
                    return Italian;
            }

            return English;
        }

        public static void Apply(string? preference)
        {
            var resolved = ResolveLanguageId(preference);
            if (string.Equals(_active, resolved, StringComparison.OrdinalIgnoreCase))
                return;

            _active = resolved;
            try
            {
                var culture = CultureInfo.GetCultureInfo(resolved);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
            }
            catch
            {
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static string Get(string key)
        {
            if (Catalogs.TryGetValue(_active, out var map) && map.TryGetValue(key, out var value))
                return value;
            if (_fallback.TryGetValue(key, out var fallback))
                return fallback;
            return key;
        }

        public static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentUICulture, Get(key), args);

        public static string MenuTitle(string tag) => Get("menu." + tag);

        public static string? MenuHint(string tag)
        {
            var key = "menu." + tag + ".hint";
            var value = Get(key);
            return string.Equals(value, key, StringComparison.Ordinal) ? null : value;
        }
    }
}
