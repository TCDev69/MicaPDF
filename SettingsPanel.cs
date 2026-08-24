using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MicaPDF
{
    public sealed class SettingsPanel : UserControl
    {
        private readonly ComboBox _themeBox;
        private readonly ComboBox _languageBox;
        private readonly ComboBox _paneBox;
        private readonly ComboBox _floatingBarBox;
        private readonly ToggleSwitch _autoUpdateSwitch;
        private readonly ToggleSwitch _wheelCtrlSwitch;
        private readonly ToggleSwitch _confirmClearSwitch;
        private readonly TextBox _repoBox;
        private readonly ListView _menuList;
        private readonly TextBlock _statusText;
        private readonly TextBlock _titleBlock;
        private readonly TextBlock _themeLabel;
        private readonly TextBlock _languageLabel;
        private readonly TextBlock _paneLabel;
        private readonly TextBlock _floatingLabel;
        private readonly TextBlock _autoUpdateLabel;
        private readonly TextBlock _wheelLabel;
        private readonly TextBlock _confirmLabel;
        private readonly TextBlock _repoLabel;
        private readonly TextBlock _menuItemsLabel;
        private readonly Button _checkButton;
        private readonly Button _upButton;
        private readonly Button _downButton;
        private readonly ObservableCollection<MenuItemSetting> _menuItems = new();
        private AppSettings _settings = new();
        private bool _loading;

        public event EventHandler? SettingsApplied;
        public event EventHandler? CheckUpdatesRequested;
        public event EventHandler? BackToPdfRequested;

        public SettingsPanel()
        {
            _themeBox = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            _languageBox = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            _paneBox = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            _floatingBarBox = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };

            _autoUpdateSwitch = new ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Left };
            _wheelCtrlSwitch = new ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Left };
            _confirmClearSwitch = new ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Left };
            _repoBox = new TextBox
            {
                Width = 280,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _statusText = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left };

            _titleBlock = new TextBlock { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
            _themeLabel = new TextBlock();
            _languageLabel = new TextBlock();
            _paneLabel = new TextBlock();
            _floatingLabel = new TextBlock();
            _autoUpdateLabel = new TextBlock();
            _wheelLabel = new TextBlock();
            _confirmLabel = new TextBlock();
            _repoLabel = new TextBlock();
            _menuItemsLabel = new TextBlock();

            _menuList = new ListView
            {
                Height = 280,
                SelectionMode = ListViewSelectionMode.Single,
                CanReorderItems = true,
                AllowDrop = true,
                CanDragItems = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 420
            };
            _menuList.ItemTemplate = CreateMenuTemplate();
            _menuList.ItemsSource = _menuItems;

            _checkButton = new Button();
            _checkButton.Click += (_, _) =>
            {
                ApplyFromUi(saveOnly: true);
                CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
            };

            _upButton = new Button { Margin = new Thickness(0, 0, 8, 0) };
            _upButton.Click += (_, _) => MoveSelected(-1);
            _downButton = new Button();
            _downButton.Click += (_, _) => MoveSelected(1);

            _themeBox.SelectionChanged += (_, _) => AutoApply();
            _languageBox.SelectionChanged += (_, _) => AutoApply();
            _paneBox.SelectionChanged += (_, _) => AutoApply();
            _floatingBarBox.SelectionChanged += (_, _) => AutoApply();
            _autoUpdateSwitch.Toggled += (_, _) => AutoApply();
            _wheelCtrlSwitch.Toggled += (_, _) => AutoApply();
            _confirmClearSwitch.Toggled += (_, _) => AutoApply();
            _repoBox.LostFocus += (_, _) => AutoApply();
            _menuItems.CollectionChanged += MenuItems_CollectionChanged;

            Content = new ScrollViewer
            {
                Padding = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Children =
                    {
                        _titleBlock,
                        _themeLabel,
                        _themeBox,
                        _languageLabel,
                        _languageBox,
                        _paneLabel,
                        _paneBox,
                        _floatingLabel,
                        _floatingBarBox,
                        _autoUpdateLabel,
                        _autoUpdateSwitch,
                        _wheelLabel,
                        _wheelCtrlSwitch,
                        _confirmLabel,
                        _confirmClearSwitch,
                        _repoLabel,
                        _repoBox,
                        _checkButton,
                        _menuItemsLabel,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children = { _upButton, _downButton }
                        },
                        _menuList,
                        _statusText
                    }
                }
            };

            RefreshLocalizedUi();
        }

        public void LoadSettings(AppSettings settings)
        {
            _loading = true;
            _settings = settings;
            RefreshLocalizedUi();
            SelectCombo(_themeBox, settings.Theme);
            SelectCombo(_languageBox, settings.Language);
            SelectCombo(_paneBox, settings.MenuPosition);
            SelectCombo(_floatingBarBox, settings.FloatingBarPosition);
            _autoUpdateSwitch.IsOn = settings.AutoUpdate;
            _wheelCtrlSwitch.IsOn = settings.WheelZoomRequiresCtrl;
            _confirmClearSwitch.IsOn = settings.ConfirmClearAnnotations;
            _repoBox.Text = settings.GitHubRepository;

            foreach (var existing in _menuItems)
                existing.PropertyChanged -= MenuItem_PropertyChanged;
            _menuItems.Clear();

            foreach (var tag in settings.MenuOrder)
            {
                var item = new MenuItemSetting
                {
                    Tag = tag,
                    Title = Loc.MenuTitle(tag),
                    IsVisible = !settings.HiddenMenuTags.Contains(tag)
                };
                item.PropertyChanged += MenuItem_PropertyChanged;
                _menuItems.Add(item);
            }
            _loading = false;
        }

        public void SetStatus(string message) => _statusText.Text = message;

        public void RefreshLocalizedUi()
        {
            var prevLoading = _loading;
            _loading = true;

            _titleBlock.Text = Loc.Get("settings.title");
            _themeLabel.Text = Loc.Get("settings.theme");
            _languageLabel.Text = Loc.Get("settings.language");
            _paneLabel.Text = Loc.Get("settings.menuPosition");
            _floatingLabel.Text = Loc.Get("settings.floatingBar");
            _autoUpdateLabel.Text = Loc.Get("settings.autoUpdate");
            _wheelLabel.Text = Loc.Get("settings.wheelZoom");
            _confirmLabel.Text = Loc.Get("settings.confirmClear");
            _repoLabel.Text = Loc.Get("settings.githubRepo");
            _menuItemsLabel.Text = Loc.Get("settings.menuItems");
            _checkButton.Content = Loc.Get("settings.checkUpdates");
            _upButton.Content = Loc.Get("settings.moveUp");
            _downButton.Content = Loc.Get("settings.moveDown");
            _repoBox.PlaceholderText = Loc.Get("settings.repoPlaceholder");

            _autoUpdateSwitch.OnContent = Loc.Get("settings.yes");
            _autoUpdateSwitch.OffContent = Loc.Get("settings.no");
            _wheelCtrlSwitch.OnContent = Loc.Get("settings.wheel.ctrl");
            _wheelCtrlSwitch.OffContent = Loc.Get("settings.wheel.direct");
            _confirmClearSwitch.OnContent = Loc.Get("settings.confirm.ask");
            _confirmClearSwitch.OffContent = Loc.Get("settings.confirm.immediate");

            RefillThemeBox();
            RefillLanguageBox();
            RefillPaneBox();
            RefillFloatingBox();
            _menuList.ItemTemplate = CreateMenuTemplate();

            foreach (var item in _menuItems)
                item.Title = Loc.MenuTitle(item.Tag);

            _loading = prevLoading;
        }

        private void RefillThemeBox()
        {
            var selected = (_themeBox.SelectedItem as ComboBoxItem)?.Tag ?? ElementTheme.Default;
            _themeBox.Items.Clear();
            _themeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.system"), Tag = ElementTheme.Default });
            _themeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.light"), Tag = ElementTheme.Light });
            _themeBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.theme.dark"), Tag = ElementTheme.Dark });
            SelectCombo(_themeBox, selected);
        }

        private void RefillLanguageBox()
        {
            var selected = (_languageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? Loc.System;
            _languageBox.Items.Clear();
            _languageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.system"), Tag = Loc.System });
            _languageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.en"), Tag = Loc.English });
            _languageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.lang.it"), Tag = Loc.Italian });
            SelectCombo(_languageBox, selected);
        }

        private void RefillPaneBox()
        {
            var selected = (_paneBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Left";
            _paneBox.Items.Clear();
            _paneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.left"), Tag = "Left" });
            _paneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.leftCompact"), Tag = "LeftCompact" });
            _paneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.right"), Tag = "Right" });
            _paneBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.top"), Tag = "Top" });
            SelectCombo(_paneBox, selected);
        }

        private void RefillFloatingBox()
        {
            var selected = (_floatingBarBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Bottom";
            _floatingBarBox.Items.Clear();
            _floatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.bottom"), Tag = "Bottom" });
            _floatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.top"), Tag = "Top" });
            _floatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.left"), Tag = "Left" });
            _floatingBarBox.Items.Add(new ComboBoxItem { Content = Loc.Get("settings.pos.right"), Tag = "Right" });
            SelectCombo(_floatingBarBox, selected);
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
            if (_themeBox.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is ElementTheme theme)
                _settings.Theme = theme;
            if (_languageBox.SelectedItem is ComboBoxItem langItem && langItem.Tag is string lang)
                _settings.Language = lang;
            if (_paneBox.SelectedItem is ComboBoxItem paneItem && paneItem.Tag is string pane)
                _settings.MenuPosition = pane;
            if (_floatingBarBox.SelectedItem is ComboBoxItem barItem && barItem.Tag is string barPos)
                _settings.FloatingBarPosition = barPos;

            _settings.AutoUpdate = _autoUpdateSwitch.IsOn;
            _settings.WheelZoomRequiresCtrl = _wheelCtrlSwitch.IsOn;
            _settings.ConfirmClearAnnotations = _confirmClearSwitch.IsOn;
            _settings.GitHubRepository = string.IsNullOrWhiteSpace(_repoBox.Text)
                ? AppSettings.DefaultGitHubRepository
                : _repoBox.Text.Trim();

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
            if (!saveOnly)
            {
                SettingsApplied?.Invoke(this, EventArgs.Empty);
                _statusText.Text = Loc.Get("settings.applied");
            }
        }

        private void MoveSelected(int delta)
        {
            var index = _menuList.SelectedIndex;
            if (index < 0) return;
            var newIndex = index + delta;
            if (newIndex < 0 || newIndex >= _menuItems.Count) return;
            _menuItems.Move(index, newIndex);
            _menuList.SelectedIndex = newIndex;
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

        private static DataTemplate CreateMenuTemplate()
        {
            var show = Loc.Get("settings.show").Replace("\"", "&quot;");
            var hide = Loc.Get("settings.hide").Replace("\"", "&quot;");
            var xaml =
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<Grid ColumnSpacing=\"12\">" +
                "<Grid.ColumnDefinitions>" +
                "<ColumnDefinition Width=\"*\"/>" +
                "<ColumnDefinition Width=\"Auto\"/>" +
                "</Grid.ColumnDefinitions>" +
                "<TextBlock Text=\"{Binding Title}\" VerticalAlignment=\"Center\"/>" +
                "<ToggleSwitch Grid.Column=\"1\" IsOn=\"{Binding IsVisible, Mode=TwoWay}\" OnContent=\"" + show +
                "\" OffContent=\"" + hide + "\"/>" +
                "</Grid></DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }
    }
}
