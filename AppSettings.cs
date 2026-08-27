using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MicaPDF
{
    public sealed class MenuItemSetting : INotifyPropertyChanged
    {
        private bool _isVisible = true;

        public string Tag { get; set; } = "";
        public string Title { get; set; } = "";

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class AppSettings
    {
        public const string DefaultGitHubRepository = "TCDev69/MicaPDF";

        public static readonly string[] DefaultMenuOrder =
        {
            "open", "recentfiles", "print", "savewithannotations",
            "zoomin", "zoomout", "zoomreset", "zoomfit", "find",
            "outline", "gotopage", "nextpage", "prevpage", "doublepagemode", "coverpagemode", "continuousmode",
            "edit", "clearink"
        };

        public ElementTheme Theme { get; set; } = ElementTheme.Default;
        /// <summary>System, en, it</summary>
        public string Language { get; set; } = Loc.System;
        /// <summary>Left, LeftCompact, Right, Top</summary>
        public string MenuPosition { get; set; } = "Left";
        /// <summary>Bottom, Top, Left, Right</summary>
        public string FloatingBarPosition { get; set; } = "Bottom";
        public bool AutoUpdate { get; set; } = true;
        public bool WheelZoomRequiresCtrl { get; set; } = true;
        public bool ConfirmClearAnnotations { get; set; } = true;
        public string GitHubRepository { get; set; } = DefaultGitHubRepository;
        public Color PenColor { get; set; } = Color.FromArgb(255, 0, 0, 0);
        public float PenSize { get; set; } = 3f;
        public bool PenIsHighlighter { get; set; }
        public Color PenBlackColor { get; set; } = Color.FromArgb(255, 0, 0, 0);
        public Color PenRedColor { get; set; } = Color.FromArgb(255, 220, 38, 38);
        public Color PenGreenColor { get; set; } = Color.FromArgb(255, 22, 163, 74);
        public Color HighlighterColor { get; set; } = Color.FromArgb(255, 250, 204, 21);
        public string ActivePenSlot { get; set; } = "Black";
        public HashSet<string> HiddenMenuTags { get; } = new();
        public List<string> MenuOrder { get; } = new(DefaultMenuOrder);
        public bool NavPaneIsOpen { get; set; } = true;
        public bool OutlinePaneIsOpen { get; set; }
        public bool HasShownDefaultReaderPrompt { get; set; }

        private static string StoreDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicaPDF");

        private static string StorePath => Path.Combine(StoreDirectory, "settings.json");

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                if (!File.Exists(StorePath))
                    return settings;

                var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(StorePath));
                if (dto == null)
                    return settings;

                if (Enum.TryParse<ElementTheme>(dto.Theme, out var parsedTheme))
                    settings.Theme = parsedTheme;
                if (!string.IsNullOrWhiteSpace(dto.Language))
                {
                    var lang = dto.Language.Trim();
                    if (lang.Equals(Loc.System, StringComparison.OrdinalIgnoreCase) ||
                        lang.Equals("System", StringComparison.OrdinalIgnoreCase))
                        settings.Language = Loc.System;
                    else if (lang.Equals(Loc.English, StringComparison.OrdinalIgnoreCase))
                        settings.Language = Loc.English;
                    else if (lang.Equals(Loc.Italian, StringComparison.OrdinalIgnoreCase))
                        settings.Language = Loc.Italian;
                }
                if (!string.IsNullOrWhiteSpace(dto.MenuPosition))
                    settings.MenuPosition = dto.MenuPosition;
                else if (!string.IsNullOrWhiteSpace(dto.PaneDisplayMode))
                {
                    settings.MenuPosition = dto.PaneDisplayMode switch
                    {
                        "LeftCompact" => "LeftCompact",
                        "Top" => "Top",
                        _ => "Left"
                    };
                }
                if (!string.IsNullOrWhiteSpace(dto.FloatingBarPosition))
                    settings.FloatingBarPosition = dto.FloatingBarPosition;
                settings.AutoUpdate = dto.AutoUpdate ?? true;
                settings.WheelZoomRequiresCtrl = dto.WheelZoomRequiresCtrl;
                settings.ConfirmClearAnnotations = dto.ConfirmClearAnnotations;
                if (!string.IsNullOrWhiteSpace(dto.GitHubRepository))
                    settings.GitHubRepository = dto.GitHubRepository;
                settings.PenSize = dto.PenSize;
                settings.PenIsHighlighter = dto.PenIsHighlighter;
                settings.PenColor = ParseColor(dto.PenColor, settings.PenColor);
                settings.PenBlackColor = ParseColor(dto.PenBlackColor, settings.PenBlackColor);
                settings.PenRedColor = ParseColor(dto.PenRedColor, settings.PenRedColor);
                settings.PenGreenColor = ParseColor(dto.PenGreenColor, settings.PenGreenColor);
                settings.HighlighterColor = ParseColor(dto.HighlighterColor, settings.HighlighterColor);
                if (!string.IsNullOrWhiteSpace(dto.ActivePenSlot))
                    settings.ActivePenSlot = dto.ActivePenSlot;
                settings.NavPaneIsOpen = dto.NavPaneIsOpen;
                settings.OutlinePaneIsOpen = dto.OutlinePaneIsOpen;
                settings.HasShownDefaultReaderPrompt = dto.HasShownDefaultReaderPrompt;
                settings.HiddenMenuTags.Clear();
                foreach (var tag in dto.HiddenMenuTags ?? Array.Empty<string>())
                {
                    if (tag is "selectmode" or "penmode" or "textmode" or "eraser")
                        continue;
                    settings.HiddenMenuTags.Add(tag);
                }

                if (dto.MenuOrder is { Length: > 0 })
                {
                    var legacyMap = new Dictionary<string, string>
                    {
                        ["selectmode"] = "edit",
                        ["penmode"] = "edit",
                        ["textmode"] = "edit",
                        ["eraser"] = "edit"
                    };
                    var parsed = new List<string>();
                    foreach (var tag in dto.MenuOrder)
                    {
                        var mapped = legacyMap.TryGetValue(tag, out var next) ? next : tag;
                        if (DefaultMenuOrder.Contains(mapped) && !parsed.Contains(mapped))
                            parsed.Add(mapped);
                    }
                    foreach (var tag in DefaultMenuOrder)
                    {
                        if (!parsed.Contains(tag))
                            parsed.Add(tag);
                    }

                    parsed.Remove("recentfiles");
                    var recentInsertIdx = parsed.IndexOf("open");
                    parsed.Insert(recentInsertIdx >= 0 ? recentInsertIdx + 1 : 0, "recentfiles");

                    settings.MenuOrder.Clear();
                    settings.MenuOrder.AddRange(parsed);
                    EnsureOutlineBeforeGoToPage(settings.MenuOrder);
                }
            }
            catch
            {
                // Keep defaults if settings storage is unavailable.
            }

            return settings;
        }

        private static void EnsureOutlineBeforeGoToPage(List<string> order)
        {
            var outlineIdx = order.IndexOf("outline");
            var gotoIdx = order.IndexOf("gotopage");
            if (outlineIdx < 0 || gotoIdx < 0 || outlineIdx < gotoIdx)
                return;
            order.RemoveAt(outlineIdx);
            order.Insert(gotoIdx, "outline");
        }

        public void Save()
        {
            Directory.CreateDirectory(StoreDirectory);
            var dto = new SettingsDto
            {
                Theme = Theme.ToString(),
                Language = Language,
                MenuPosition = MenuPosition,
                FloatingBarPosition = FloatingBarPosition,
                AutoUpdate = AutoUpdate,
                WheelZoomRequiresCtrl = WheelZoomRequiresCtrl,
                ConfirmClearAnnotations = ConfirmClearAnnotations,
                GitHubRepository = GitHubRepository,
                PenSize = PenSize,
                PenIsHighlighter = PenIsHighlighter,
                PenColor = FormatColor(PenColor),
                PenBlackColor = FormatColor(PenBlackColor),
                PenRedColor = FormatColor(PenRedColor),
                PenGreenColor = FormatColor(PenGreenColor),
                HighlighterColor = FormatColor(HighlighterColor),
                ActivePenSlot = ActivePenSlot,
                HiddenMenuTags = HiddenMenuTags.ToArray(),
                MenuOrder = MenuOrder.ToArray(),
                NavPaneIsOpen = NavPaneIsOpen,
                OutlinePaneIsOpen = OutlinePaneIsOpen,
                HasShownDefaultReaderPrompt = HasShownDefaultReaderPrompt
            };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }

        private sealed class SettingsDto
        {
            public string Theme { get; set; } = nameof(ElementTheme.Default);
            public string Language { get; set; } = Loc.System;
            public string MenuPosition { get; set; } = "Left";
            public string FloatingBarPosition { get; set; } = "Bottom";
            public string? PaneDisplayMode { get; set; }
            public bool? AutoUpdate { get; set; }
            public bool WheelZoomRequiresCtrl { get; set; } = true;
            public bool ConfirmClearAnnotations { get; set; } = true;
            public string GitHubRepository { get; set; } = DefaultGitHubRepository;
            public float PenSize { get; set; } = 3f;
            public bool PenIsHighlighter { get; set; }
            public string PenColor { get; set; } = "FF000000";
            public string PenBlackColor { get; set; } = "FF000000";
            public string PenRedColor { get; set; } = "FFDC2626";
            public string PenGreenColor { get; set; } = "FF16A34A";
            public string HighlighterColor { get; set; } = "FFFACC15";
            public string ActivePenSlot { get; set; } = "Black";
            public string[] HiddenMenuTags { get; set; } = Array.Empty<string>();
            public string[] MenuOrder { get; set; } = Array.Empty<string>();
            public bool NavPaneIsOpen { get; set; } = true;
            public bool OutlinePaneIsOpen { get; set; }
            public bool HasShownDefaultReaderPrompt { get; set; }
        }

        public static string FormatColor(Color color) =>
            $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        public static Color ParseColor(string value, Color fallback)
        {
            try
            {
                value = value.Trim().TrimStart('#');
                if (value.Length == 6)
                    value = "FF" + value;
                if (value.Length == 8)
                {
                    return Color.FromArgb(
                        Convert.ToByte(value[..2], 16),
                        Convert.ToByte(value[2..4], 16),
                        Convert.ToByte(value[4..6], 16),
                        Convert.ToByte(value[6..8], 16));
                }
            }
            catch
            {
            }

            return fallback;
        }
    }
}
