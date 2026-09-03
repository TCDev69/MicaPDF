using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace MicaPDF
{
    public sealed partial class SettingsPanel : UserControl, INotifyPropertyChanged
    {
        private readonly ObservableCollection<MenuItemSetting> _menuItems = new();
        private readonly ObservableCollection<MenuItemSetting> _filteredMenuItems = new();
        private AppSettings _settings = new();
        private bool _loading;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
        private string _menuShowLabel = "";
        private string _menuHideLabel = "";
        private double _dragHandleOpacity = 0.5;

        public event EventHandler? SettingsApplied;
        public event EventHandler? CheckUpdatesRequested;
        public event EventHandler? ExportSettingsRequested;
        public event EventHandler? ImportSettingsRequested;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string MenuShowLabel
        {
            get => _menuShowLabel;
            private set { _menuShowLabel = value; OnPropertyChanged(); }
        }

        public string MenuHideLabel
        {
            get => _menuHideLabel;
            private set { _menuHideLabel = value; OnPropertyChanged(); }
        }

        public double DragHandleOpacity
        {
            get => _dragHandleOpacity;
            private set { _dragHandleOpacity = value; OnPropertyChanged(); }
        }

        public SettingsPanel()
        {
            InitializeComponent();
            MenuList.ItemsSource = _menuItems;
            WireEvents();
        }

        private void WireEvents()
        {
            ThemeBox.SelectionChanged += (_, _) => AutoApply();
            LanguageBox.SelectionChanged += (_, _) => AutoApply();
            PaneBox.SelectionChanged += (_, _) => AutoApply();
            FloatingBarBox.SelectionChanged += (_, _) => AutoApply();
            AutoUpdateSwitch.Toggled += (_, _) => AutoApply();
            WheelCtrlSwitch.Toggled += (_, _) => AutoApply();
            MaxZoomBox.SelectionChanged += (_, _) => AutoApply();
            ConfirmClearSwitch.Toggled += (_, _) => AutoApply();
            RepoBox.LostFocus += (_, _) => AutoApply();
            _menuItems.CollectionChanged += MenuItems_CollectionChanged;
        }

        public void LoadSettings(AppSettings settings)
        {
            _loading = true;
            _settings = settings;
            RefreshLocalizedUi();
            SelectCombo(ThemeBox, settings.Theme);
            SelectCombo(LanguageBox, settings.Language);
            SelectCombo(PaneBox, settings.MenuPosition);
            SelectCombo(FloatingBarBox, settings.FloatingBarPosition);
            AutoUpdateSwitch.IsOn = settings.AutoUpdate;
            WheelCtrlSwitch.IsOn = settings.WheelZoomRequiresCtrl;
            SelectCombo(MaxZoomBox, settings.MaxZoomPercent);
            ConfirmClearSwitch.IsOn = settings.ConfirmClearAnnotations;
            RepoBox.Text = settings.GitHubRepository;
            VersionBadgeText.Text = UpdateChecker.GetCurrentVersion();

            foreach (var existing in _menuItems)
                existing.PropertyChanged -= MenuItem_PropertyChanged;
            _menuItems.Clear();

            foreach (var tag in settings.MenuOrder)
            {
                _menuItems.Add(new MenuItemSetting
                {
                    Tag = tag,
                    Title = Loc.MenuTitle(tag),
                    IconGlyph = MenuItemIcons.GetGlyph(tag),
                    IsVisible = !settings.HiddenMenuTags.Contains(tag)
                });
            }

            MenuSearchBox.Text = string.Empty;
            RefreshMenuFilter();
            _loading = false;
        }

        public void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            StatusInfoBar.Title = severity switch
            {
                InfoBarSeverity.Error => Loc.Get("settings.status.error"),
                InfoBarSeverity.Success => Loc.Get("settings.status.success"),
                _ => string.Empty
            };
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;

            _statusTimer?.Stop();
            if (severity == InfoBarSeverity.Informational || severity == InfoBarSeverity.Success)
            {
                _statusTimer = DispatcherQueue.CreateTimer();
                _statusTimer.Interval = TimeSpan.FromSeconds(3);
                _statusTimer.Tick += (_, _) =>
                {
                    _statusTimer.Stop();
                    StatusInfoBar.IsOpen = false;
                };
                _statusTimer.Start();
            }
        }

        public void RefreshLocalizedUi()
        {
            var prevLoading = _loading;
            _loading = true;

            TitleBlock.Text = Loc.Get("settings.title");
            SubtitleBlock.Text = Loc.Get("settings.subtitle");

            AppearanceExpander.Header = Loc.Get("settings.section.appearance");
            AppearanceExpander.Description = Loc.Get("settings.section.appearance.desc");
            ViewerExpander.Header = Loc.Get("settings.section.viewer");
            ViewerExpander.Description = Loc.Get("settings.section.viewer.desc");
            MenuExpander.Header = Loc.Get("settings.section.menu");
            MenuExpander.Description = Loc.Get("settings.section.menu.desc");
            UpdatesExpander.Header = Loc.Get("settings.section.updates");
            UpdatesExpander.Description = Loc.Get("settings.section.updates.desc");
            AdvancedExpander.Header = Loc.Get("settings.section.advanced");
            AdvancedExpander.Description = Loc.Get("settings.section.advanced.desc");
            AboutExpander.Header = Loc.Get("settings.section.about");
            AboutExpander.Description = Loc.Get("settings.section.about.desc");

            ThemeCard.Header = Loc.Get("settings.theme");
            ThemeCard.Description = Loc.Get("settings.theme.desc");
            LanguageCard.Header = Loc.Get("settings.language");
            LanguageCard.Description = Loc.Get("settings.language.desc");
            PaneCard.Header = Loc.Get("settings.menuPosition");
            PaneCard.Description = Loc.Get("settings.menuPosition.desc");
            FloatingBarCard.Header = Loc.Get("settings.floatingBar");
            FloatingBarCard.Description = Loc.Get("settings.floatingBar.desc");

            WheelCard.Header = Loc.Get("settings.wheelZoom");
            WheelCard.Description = Loc.Get("settings.wheelZoom.desc");
            MaxZoomCard.Header = Loc.Get("settings.maxZoom");
            MaxZoomCard.Description = Loc.Get("settings.maxZoom.desc");
            ConfirmClearCard.Header = Loc.Get("settings.confirmClear");
            ConfirmClearCard.Description = Loc.Get("settings.confirmClear.desc");

            MenuCard.Header = Loc.Get("settings.menu.list");
            MenuCard.Description = Loc.Get("settings.menuItems");

            AutoUpdateCard.Header = Loc.Get("settings.autoUpdate");
            AutoUpdateCard.Description = Loc.Get("settings.autoUpdate.desc");
            CheckUpdatesCard.Header = Loc.Get("settings.checkUpdates");
            CheckUpdatesCard.Description = Loc.Get("settings.checkUpdates.desc");

            RepoCard.Header = Loc.Get("settings.githubRepo");
            RepoCard.Description = Loc.Get("settings.githubRepo.desc");
            RepoBox.PlaceholderText = Loc.Get("settings.repoPlaceholder");
            ExportCard.Header = Loc.Get("settings.advanced.export");
            ExportCard.Description = Loc.Get("settings.advanced.exportDesc");
            ImportCard.Header = Loc.Get("settings.advanced.import");
            ImportCard.Description = Loc.Get("settings.advanced.importDesc");

            VersionCard.Header = Loc.Get("settings.about.version");
            VersionCard.Description = Loc.Get("settings.about.versionDesc");
            VersionBadgeText.Text = UpdateChecker.GetCurrentVersion();
            RepositoryLinkCard.Header = Loc.Get("settings.about.repository");
            RepositoryLinkCard.Description = $"github.com/{_settings.GitHubRepository}";
            LicenseCard.Header = Loc.Get("settings.about.license");
            LicenseCard.Description = Loc.Get("settings.about.licenseDesc");

            AutoUpdateSwitch.OnContent = Loc.Get("settings.yes");
            AutoUpdateSwitch.OffContent = Loc.Get("settings.no");
            WheelCtrlSwitch.OnContent = Loc.Get("settings.wheel.ctrl");
            WheelCtrlSwitch.OffContent = Loc.Get("settings.wheel.direct");
            ConfirmClearSwitch.OnContent = Loc.Get("settings.confirm.ask");
            ConfirmClearSwitch.OffContent = Loc.Get("settings.confirm.immediate");

            MenuShowLabel = Loc.Get("settings.show");
            MenuHideLabel = Loc.Get("settings.hide");
            ToolTipService.SetToolTip(UpButton, Loc.Get("settings.moveUp"));
            ToolTipService.SetToolTip(DownButton, Loc.Get("settings.moveDown"));
            ResetMenuButtonText.Text = Loc.Get("settings.menu.resetOrder");
            ToolTipService.SetToolTip(ResetMenuButton, Loc.Get("settings.menu.resetOrder"));
            MenuSearchBox.PlaceholderText = Loc.Get("settings.menu.searchPlaceholder");
            AutomationProperties.SetName(MenuSearchBox, Loc.Get("settings.menu.search"));
            AutomationProperties.SetName(MenuReorderPanel, Loc.Get("settings.menu.actions"));

            RefillThemeBox();
            RefillLanguageBox();
            RefillPaneBox();
            RefillFloatingBox();
            RefillMaxZoomBox();

            foreach (var item in _menuItems)
                item.Title = Loc.MenuTitle(item.Tag);

            _loading = prevLoading;
        }

        private void RefillThemeBox()
        {
            var selected = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag ?? ElementTheme.Default;
            ThemeBox.Items.Clear();
            ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.system"), Tag = ElementTheme.Default });
            ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.light"), Tag = ElementTheme.Light });
            ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.dark"), Tag = ElementTheme.Dark });
            SelectCombo(ThemeBox, selected);
        }

        private void RefillLanguageBox()
        {
            var selected = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? Loc.System;
            LanguageBox.Items.Clear();
            LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.system"), Tag = Loc.System });
            LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.en"), Tag = Loc.English });
            LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.it"), Tag = Loc.Italian });
            SelectCombo(LanguageBox, selected);
        }

        private void RefillPaneBox()
        {
            var selected = (PaneBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Left";
            PaneBox.Items.Clear();
            PaneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.left"), Tag = "Left" });
            PaneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.leftCompact"), Tag = "LeftCompact" });
            PaneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.right"), Tag = "Right" });
            PaneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.top"), Tag = "Top" });
            SelectCombo(PaneBox, selected);
        }

        private void RefillFloatingBox()
        {
            var selected = (FloatingBarBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Bottom";
            FloatingBarBox.Items.Clear();
            FloatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.bottom"), Tag = "Bottom" });
            FloatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.top"), Tag = "Top" });
            FloatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.left"), Tag = "Left" });
            FloatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.right"), Tag = "Right" });
            SelectCombo(FloatingBarBox, selected);
        }

        private void RefillMaxZoomBox()
        {
            var selected = (MaxZoomBox.SelectedItem as ComboBoxItem)?.Tag as int?
                ?? ZoomLimits.DefaultMaxZoomPercent;
            MaxZoomBox.Items.Clear();
            foreach (var percent in new[] { 100, 150, 200, 300, 500 })
            {
                MaxZoomBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{percent}%",
                    Tag = percent
                });
            }
            SelectCombo(MaxZoomBox, selected);
        }

        private void RefreshMenuFilter()
        {
            var query = MenuSearchBox?.Text?.Trim();
            var filterActive = !string.IsNullOrEmpty(query);

            if (!filterActive)
            {
                if (!ReferenceEquals(MenuList.ItemsSource, _menuItems))
                    MenuList.ItemsSource = _menuItems;
                UpdateMenuReorderState();
                return;
            }

            _filteredMenuItems.Clear();
            foreach (var item in _menuItems)
            {
                if (item.Title.Contains(query!, StringComparison.OrdinalIgnoreCase))
                    _filteredMenuItems.Add(item);
            }

            if (!ReferenceEquals(MenuList.ItemsSource, _filteredMenuItems))
                MenuList.ItemsSource = _filteredMenuItems;
            UpdateMenuReorderState();
        }

        private void MenuSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            RefreshMenuFilter();
        }

        private void UpdateMenuReorderState()
        {
            var filterActive = !string.IsNullOrWhiteSpace(MenuSearchBox.Text);
            MenuList.CanReorderItems = !filterActive;
            MenuList.CanDragItems = !filterActive;
            MenuList.AllowDrop = !filterActive;
            DragHandleOpacity = filterActive ? 0 : 0.5;
        }

        private void MenuItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (MenuItemSetting item in e.NewItems)
                    item.PropertyChanged += MenuItem_PropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (MenuItemSetting item in e.OldItems)
                    item.PropertyChanged -= MenuItem_PropertyChanged;
            }
            AutoApply();
        }

        private void MenuItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MenuItemSetting.IsVisible))
                AutoApply();
        }

        private void AutoApply()
        {
            if (_loading) return;
            ApplyFromUi();
        }

        private void ApplyFromUi(bool saveOnly = false)
        {
            if (ThemeBox.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is ElementTheme theme)
                _settings.Theme = theme;
            if (LanguageBox.SelectedItem is ComboBoxItem langItem && langItem.Tag is string lang)
                _settings.Language = lang;
            if (PaneBox.SelectedItem is ComboBoxItem paneItem && paneItem.Tag is string pane)
                _settings.MenuPosition = pane;
            if (FloatingBarBox.SelectedItem is ComboBoxItem barItem && barItem.Tag is string barPos)
                _settings.FloatingBarPosition = barPos;

            _settings.AutoUpdate = AutoUpdateSwitch.IsOn;
            _settings.WheelZoomRequiresCtrl = WheelCtrlSwitch.IsOn;
            if (MaxZoomBox.SelectedItem is ComboBoxItem maxItem && maxItem.Tag is int maxZoom)
                _settings.MaxZoomPercent = ZoomLimits.SanitizeMaxZoomPercent(maxZoom);
            _settings.ConfirmClearAnnotations = ConfirmClearSwitch.IsOn;
            _settings.GitHubRepository = string.IsNullOrWhiteSpace(RepoBox.Text)
                ? AppSettings.DefaultGitHubRepository
                : RepoBox.Text.Trim();

            _settings.HiddenMenuTags.Clear();
            _settings.MenuOrder.Clear();
            foreach (var item in _menuItems)
            {
                _settings.MenuOrder.Add(item.Tag);
                if (!item.IsVisible)
                    _settings.HiddenMenuTags.Add(item.Tag);
            }

            Loc.Apply(_settings.Language);
            _settings.Save();
            RepositoryLinkCard.Description = $"github.com/{_settings.GitHubRepository}";

            if (!saveOnly)
            {
                SettingsApplied?.Invoke(this, EventArgs.Empty);
                SetStatus(Loc.Get("settings.applied"), InfoBarSeverity.Success);
            }
        }

        private void MoveSelected(int delta)
        {
            if (!ReferenceEquals(MenuList.ItemsSource, _menuItems))
                return;

            var index = MenuList.SelectedIndex;
            if (index < 0) return;
            var newIndex = index + delta;
            if (newIndex < 0 || newIndex >= _menuItems.Count) return;
            _menuItems.Move(index, newIndex);
            MenuList.SelectedIndex = newIndex;
        }

        private void UpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void DownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void ResetMenuButton_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            foreach (var existing in _menuItems)
                existing.PropertyChanged -= MenuItem_PropertyChanged;
            _menuItems.Clear();

            foreach (var tag in AppSettings.DefaultMenuOrder)
            {
                _menuItems.Add(new MenuItemSetting
                {
                    Tag = tag,
                    Title = Loc.MenuTitle(tag),
                    IconGlyph = MenuItemIcons.GetGlyph(tag),
                    IsVisible = true
                });
            }

            _loading = false;
            AutoApply();
        }

        private void CheckUpdatesCard_Click(object sender, RoutedEventArgs e)
        {
            ApplyFromUi(saveOnly: true);
            CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ExportCard_Click(object sender, RoutedEventArgs e)
        {
            ApplyFromUi(saveOnly: true);
            ExportSettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ImportCard_Click(object sender, RoutedEventArgs e)
        {
            ImportSettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private async void RepositoryLinkCard_Click(object sender, RoutedEventArgs e)
        {
            var repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository)
                ? AppSettings.DefaultGitHubRepository
                : _settings.GitHubRepository.Trim();
            await Launcher.LaunchUriAsync(new Uri($"https://github.com/{repo}"));
        }

        private async void LicenseCard_Click(object sender, RoutedEventArgs e)
        {
            var repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository)
                ? AppSettings.DefaultGitHubRepository
                : _settings.GitHubRepository.Trim();
            await Launcher.LaunchUriAsync(new Uri($"https://github.com/{repo}/blob/main/LICENSE"));
        }

        private static void SelectCombo(ComboBox box, object tag)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (Equals(item.Tag, tag) ||
                    (item.Tag is string s && tag is string t &&
                     string.Equals(s, t, StringComparison.OrdinalIgnoreCase)))
                {
                    box.SelectedItem = item;
                    return;
                }
            }

            if (box.Items.Count > 0)
                box.SelectedIndex = 0;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
