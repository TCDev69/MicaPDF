using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Input.Inking;
using WinRT;
using WinRT.Interop;

namespace MicaPDF
{
    public sealed partial class MainWindow : Window
    {
        private sealed class ContinuousPageHost
        {
            public uint Index;
            public Grid Root = null!;
            public Image Image = null!;
            public AnnotationOverlay? Overlay;
            public double DisplayHeight;
            public bool Rendered;
            public double PageWidthDip;
            public double PageHeightDip;
        }

        private sealed class OutlineTreeItem
        {
            public string Title { get; init; } = "";
            public int? PageIndex { get; init; }
            public override string ToString() => Title;
        }

        private PdfDocument? _pdfDocument;
        private PdfPageLabels? _pageLabels;
        private PdfOutline? _pdfOutline;
        private uint _currentPageIndex;
        private double _currentZoom = 0.5;
        private MicaController? _micaController;
        private SystemBackdropConfiguration? _configurationSource;
        private StorageFile? _currentFile;
        private AnnotationTool _currentTool = AnnotationTool.Select;
        private bool _isDoublePageMode;
        private bool _isCoverPageMode;
        private bool _isContinuousMode;
        private PrintHelper? _printHelper;
        private readonly PdfPageCache _pageCache = new(8, PdfPageCache.DefaultByteBudget);
        private readonly AnnotationStore _annotations = new();
        private readonly AnnotationHistory _history = new();
        private AppSettings _settings = AppSettings.Load();
        private PdfTextIndex? _textIndex;
        private bool _forceClose;
        private bool _isGoToPageOpen;
        private int _loadingDepth;
        private float _lastScrollZoom = 1f;
        private bool _pinchZoomQueued;
        private bool _continuousRenderQueued;
        private double _textFontSize = 18;
        private bool _textBold;
        private bool _textItalic;
        private Windows.UI.Color _textColor = Windows.UI.Color.FromArgb(255, 20, 20, 20);
        private PenSlot _activePenSlot = PenSlot.Black;
        private static readonly string[][] MenuSectionTags =
        {
            new[] { "open", "recentfiles", "print", "savewithannotations" },
            new[] { "zoomin", "zoomout", "zoomreset", "zoomfit", "find" },
            new[] { "outline", "gotopage", "nextpage", "prevpage", "doublepagemode", "coverpagemode", "continuousmode" },
            new[] { "edit", "clearink" }
        };

        private readonly Dictionary<string, NavigationViewItem> _menuItemsByTag = new();
        private RecentFilesStore _recentFiles = RecentFilesStore.Load();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sessionSaveTimer;

        public MainWindow()
        {
            InitializeComponent();
            Title = "MicaPDF";
            Loc.Apply(_settings.Language);

            SetupMicaBackground();
            SetupCustomTitleBar();
            CacheMenuItems();
            ApplySettingsToUi();

            NavView.ItemInvoked += NavView_ItemInvoked;
            AppSettingsPanel.SettingsApplied += (_, _) =>
            {
                ApplySettingsToUi();
                ShowSettingsContent();
            };
            AppSettingsPanel.CheckUpdatesRequested += async (_, _) => await CheckForUpdatesAsync(forcePrompt: true);
            AppSettingsPanel.ExportSettingsRequested += async (_, _) => await ExportSettingsAsync();
            AppSettingsPanel.ImportSettingsRequested += async (_, _) => await ImportSettingsAsync();
            NavView.BackRequested += (_, _) => ShowViewerContent();

            if (Content is UIElement rootContent)
                rootContent.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Window_KeyDown), true);
            PageNumberBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(PageNumberBox_AnyKeyDown), true);
            RegisterKeyboardAccelerators();

            PdfAnnotationOverlay.AnnotationsChanged += (_, _) => ScheduleAnnotationAutosave();
            PdfAnnotationOverlayLeft.AnnotationsChanged += (_, _) => ScheduleAnnotationAutosave();
            PdfAnnotationOverlayRight.AnnotationsChanged += (_, _) => ScheduleAnnotationAutosave();

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                try { appWindow.SetIcon("MicaPDF.ico"); } catch { }
                appWindow.Closing += AppWindow_Closing;
            }

            try
            {
                LoadNavigationPaneState();
                ApplyOutlinePaneState();
                NavView.PaneClosing += (_, _) => SaveNavigationPaneState(false);
                NavView.PaneOpening += (_, _) => SaveNavigationPaneState(true);
            }
            catch { }

            BindAnnotationOverlays();
            ApplyPenAttributesToOverlays();
            ApplyTextDefaultsToOverlays();
            WireInkToolbar();
            _history.Changed += (_, _) =>
            {
                RefreshVisibleOverlays();
                AnnotationToolbar.SetHistoryState(_history.CanUndo, _history.CanRedo);
                ScheduleAnnotationAutosave();
            };
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            RefreshRecentFilesUi();
            await LoadFileFromCommandLine();
            if (_settings.AutoUpdate)
                await CheckForUpdatesAsync(forcePrompt: false);
        }

        private void CacheMenuItems()
        {
            foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag is string tag)
                    _menuItemsByTag[tag] = item;
            }
        }

        private void BindAnnotationOverlays()
        {
            ConfigureOverlay(PdfAnnotationOverlay, 0);
            ConfigureOverlay(PdfAnnotationOverlayLeft, 0);
            ConfigureOverlay(PdfAnnotationOverlayRight, 0);
        }

        private void ConfigureOverlay(AnnotationOverlay overlay, uint pageIndex)
        {
            var size = GetPageSize(pageIndex);
            overlay.Attach(_annotations, pageIndex, size.Width, size.Height);
            overlay.SetHistory(_history);
            overlay.SetTextIndex(_textIndex);
            overlay.SetTool(_currentTool);
            overlay.SelectionChanged -= OnAnnotationSelectionChanged;
            overlay.SelectionChanged += OnAnnotationSelectionChanged;
            ApplyPenAttributesToOverlay(overlay);
            overlay.DefaultFontSize = _textFontSize;
            overlay.DefaultBold = _textBold;
            overlay.DefaultItalic = _textItalic;
            overlay.DefaultTextColor = _textColor;
        }

        private Size GetPageSize(uint pageIndex)
        {
            if (_pageSizes.TryGetValue(pageIndex, out var cached))
                return cached;

            if (_pdfDocument == null || pageIndex >= _pdfDocument.PageCount)
                return new Size(1, 1);

            using var page = _pdfDocument.GetPage(pageIndex);
            var size = page.Size;
            _pageSizes[pageIndex] = size;
            return size;
        }

        private void RefreshAllOverlays() => RefreshVisibleOverlays();

        private void WireInkToolbar()
        {
            if (Enum.TryParse<PenSlot>(_settings.ActivePenSlot, out var slot))
                _activePenSlot = slot;

            AnnotationToolbar.SetPenColors(
                _settings.PenBlackColor,
                _settings.PenRedColor,
                _settings.PenGreenColor,
                _settings.HighlighterColor);

            AnnotationToolbar.ToolSelected += (_, tool) => SetToolMode(tool);
            AnnotationToolbar.PenSelected += (_, preset) => ApplyPenSlot(preset.Slot, preset.Color, saveSlot: true);
            AnnotationToolbar.PenColorChanged += (_, preset) =>
            {
                switch (preset.Slot)
                {
                    case PenSlot.Black: _settings.PenBlackColor = preset.Color; break;
                    case PenSlot.Red: _settings.PenRedColor = preset.Color; break;
                    case PenSlot.Green: _settings.PenGreenColor = preset.Color; break;
                    case PenSlot.Highlighter: _settings.HighlighterColor = preset.Color; break;
                }
                _settings.Save();
                ApplyPenSlot(preset.Slot, preset.Color, saveSlot: true);
            };
            AnnotationToolbar.UndoRequested += (_, _) => _history.Undo();
            AnnotationToolbar.RedoRequested += (_, _) => _history.Redo();
            AnnotationToolbar.CloseRequested += (_, _) =>
            {
                AnnotationToolbar.Visibility = Visibility.Collapsed;
                SetToolMode(AnnotationTool.Select);
            };
            AnnotationToolbar.TextSizeUp += (_, _) => AdjustTextSize(2);
            AnnotationToolbar.TextSizeDown += (_, _) => AdjustTextSize(-2);
            AnnotationToolbar.BoldToggled += (_, value) =>
            {
                _textBold = value;
                ApplyTextDefaultsToOverlays();
                ApplyStyleToSelectedText(bold: value);
            };
            AnnotationToolbar.ItalicToggled += (_, value) =>
            {
                _textItalic = value;
                ApplyTextDefaultsToOverlays();
                ApplyStyleToSelectedText(italic: value);
            };
            AnnotationToolbar.TextColorSelected += (_, color) =>
            {
                _textColor = color;
                ApplyTextDefaultsToOverlays();
                ApplyStyleToSelectedText(color: color);
            };
            AnnotationToolbar.SetHistoryState(false, false);
            AnnotationToolbar.ApplyDock(_settings.FloatingBarPosition);
            SyncActivePenFromSettings();
        }

        private void ShowEditToolbar()
        {
            AnnotationToolbar.ApplyDock(_settings.FloatingBarPosition);
            AnnotationToolbar.Visibility = Visibility.Visible;
            AnnotationToolbar.SetPenColors(
                _settings.PenBlackColor,
                _settings.PenRedColor,
                _settings.PenGreenColor,
                _settings.HighlighterColor);
            AnnotationToolbar.SetHistoryState(_history.CanUndo, _history.CanRedo);
            if (_currentTool is AnnotationTool.Select or AnnotationTool.Text or AnnotationTool.Pen or AnnotationTool.Eraser)
                SetToolMode(_currentTool);
            else
                SetToolMode(AnnotationTool.Select);
        }

        private void ApplyPenSlot(PenSlot slot, Windows.UI.Color color, bool saveSlot)
        {
            _activePenSlot = slot;
            _settings.PenColor = color;
            _settings.PenIsHighlighter = slot == PenSlot.Highlighter;
            _settings.PenSize = slot == PenSlot.Highlighter ? 8f : 3f;
            if (saveSlot)
            {
                _settings.ActivePenSlot = slot.ToString();
                _settings.Save();
            }
            ApplyPenAttributesToOverlays();
            SetToolMode(AnnotationTool.Pen);
        }

        private void SyncActivePenFromSettings()
        {
            var color = _activePenSlot switch
            {
                PenSlot.Black => _settings.PenBlackColor,
                PenSlot.Red => _settings.PenRedColor,
                PenSlot.Green => _settings.PenGreenColor,
                _ => _settings.HighlighterColor
            };
            _settings.PenColor = color;
            _settings.PenIsHighlighter = _activePenSlot == PenSlot.Highlighter;
            _settings.PenSize = _activePenSlot == PenSlot.Highlighter ? 8f : 3f;
            ApplyPenAttributesToOverlays();
        }

        private void ApplySettingsToUi()
        {
            Loc.Apply(_settings.Language);
            ApplyThemeToWindow();
            ApplyMenuPosition();
            ApplyMenuCustomization();
            RefreshLocalizedUi();
            if (Enum.TryParse<PenSlot>(_settings.ActivePenSlot, out var slot))
                _activePenSlot = slot;
            SyncActivePenFromSettings();
            AnnotationToolbar.SetPenColors(
                _settings.PenBlackColor,
                _settings.PenRedColor,
                _settings.PenGreenColor,
                _settings.HighlighterColor);
            AnnotationToolbar.ApplyDock(_settings.FloatingBarPosition);
            ApplyPenAttributesToOverlays();
            EnforceMaxZoomFromSettings();
        }

        private void EnforceMaxZoomFromSettings()
        {
            UpdateScrollViewerZoomLimits();
            if (_pdfDocument == null) return;
            var maxZoom = ZoomLimits.MaxZoomFromPercent(_settings.MaxZoomPercent);
            if (_currentZoom > maxZoom + 0.001)
                ApplyInteractiveZoom(maxZoom);
        }

        private void RefreshLocalizedUi()
        {
            SetNavItem(OpenFileItem, "open");
            SetNavItem(RecentFilesItem, "recentfiles");
            RecentFilesItemText.Text = Loc.MenuTitle("recentfiles");
            SetNavItem(PrintItem, "print");
            SetNavItem(SaveItem, "savewithannotations");
            SetNavItem(ZoomInItem, "zoomin");
            SetNavItem(ZoomOutItem, "zoomout");
            SetNavItem(ZoomResetItem, "zoomreset");
            SetNavItem(ZoomFitItem, "zoomfit", Loc.Get(_zoomFitMode == ZoomFitMode.Height ? "menu.zoomfit.height" : "menu.zoomfit.width"));
            SetNavItem(FindItem, "find");
            FindTextBox.PlaceholderText = Loc.Get("find.placeholder");
            SetNavItem(GoToPageItem, "gotopage");
            SetNavItem(NextPageItem, "nextpage");
            SetNavItem(PrevPageItem, "prevpage");
            SetNavItem(ContinuousItem, "continuousmode");
            SetNavItem(OutlineItem, "outline");
            SetNavItem(EditItem, "edit");
            SetNavItem(ClearInkItem, "clearink");
            SetNavItem(DoublePageItem, "doublepagemode");
            SetNavItem(CoverPageItem, "coverpagemode");

            FileSectionHeader.Content = Loc.Get("nav.section.file");
            ZoomSectionHeader.Content = Loc.Get("nav.section.zoom");
            PagesSectionHeader.Content = Loc.Get("nav.section.pages");
            AnnotationsSectionHeader.Content = Loc.Get("nav.section.annotations");

            RefreshModeLabels();
            UpdateStatusPageText();

            GoToPageDialog.Title = Loc.Get("goto.title");
            GoToPageDialog.PrimaryButtonText = Loc.Get("goto.go");
            GoToPageDialog.CloseButtonText = Loc.Get("goto.cancel");
            if (GoToPageDialog.Content is StackPanel sp && sp.Children.OfType<TextBlock>().FirstOrDefault() is { } prompt)
                prompt.Text = Loc.Get("goto.prompt");
            PageNumberBox.PlaceholderText = Loc.Get("goto.placeholder");

            WelcomeTitleText.Text = Loc.Get("welcome.title");
            WelcomeSubtitleText.Text = Loc.Get("welcome.subtitle");
            WelcomeBrowseText.Text = Loc.Get("welcome.browse");
            WelcomeCompactTitleText.Text = Loc.Get("welcome.title");
            WelcomeCompactSubtitleText.Text = Loc.Get("welcome.subtitle");
            WelcomeCompactBrowseText.Text = Loc.Get("welcome.browse");
            WelcomeRecentTitleText.Text = Loc.Get("welcome.recentTitle");
            OutlineHeaderText.Text = Loc.Get("outline.header");
            RefreshOutlineEmptyState();
            if (LoadingOverlay.Visibility != Visibility.Visible)
                LoadingOverlayText.Text = Loc.Get("loading.pleaseWait");

            AnnotationToolbar.RefreshLocalizedUi();
            foreach (var overlay in EnumerateOverlays())
                overlay.RefreshLocalizedUi();
            if (SettingsHost.Visibility == Visibility.Visible)
                AppSettingsPanel.LoadSettings(_settings);
            else
                AppSettingsPanel.RefreshLocalizedUi();

            RefreshRecentFilesUi();
        }

        private void RefreshModeLabels()
        {
            DoublePageItem.IsSelected = _isDoublePageMode;
            CoverPageItem.IsSelected = _isCoverPageMode;
            ContinuousItem.IsSelected = _isContinuousMode;
            OutlineItem.IsSelected = OutlineSplitView.IsPaneOpen;
            UpdateCoverPageMenuVisibility();
        }

        private void UpdateCoverPageMenuVisibility()
        {
            CoverPageItem.Visibility = _isDoublePageMode &&
                !_settings.HiddenMenuTags.Contains("coverpagemode")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateStatusPageText()
        {
            if (_pdfDocument == null)
            {
                StatusPageText.Text = Loc.Format("nav.page", 1, 1);
                return;
            }

            SetStatusPageForIndices(_currentPageIndex, null, showLeft: true, showRight: false);
        }

        private void SetStatusPageForIndices(uint leftIndex, uint? rightIndex, bool showLeft, bool showRight)
        {
            var total = _pdfDocument?.PageCount ?? 0u;
            if (_pdfDocument == null || total == 0)
            {
                StatusPageText.Text = Loc.Format("nav.page", 1, 1);
                return;
            }

            var useLabels = _pageLabels is { IsIdentity: false };

            if (rightIndex is uint right && showLeft && showRight && right < total)
            {
                var phys = $"{leftIndex + 1}-{right + 1}";
                if (useLabels)
                {
                    var labels = $"{_pageLabels!.GetLabel(leftIndex)}-{_pageLabels.GetLabel(right)}";
                    StatusPageText.Text = Loc.Format("nav.pageLabeled", labels, phys, total);
                }
                else
                {
                    StatusPageText.Text = Loc.Format("nav.page", phys, total);
                }
                return;
            }

            uint index;
            if (!showLeft && showRight && rightIndex is uint r && r < total)
                index = r;
            else if (showLeft && leftIndex < total)
                index = leftIndex;
            else
                index = _currentPageIndex;

            if (index >= total)
                index = 0;

            var physical = index + 1;
            if (useLabels)
            {
                StatusPageText.Text = Loc.Format(
                    "nav.pageLabeled",
                    _pageLabels!.GetLabel(index),
                    physical,
                    total);
            }
            else
            {
                StatusPageText.Text = Loc.Format("nav.page", physical, total);
            }
        }

        private static void SetNavContent(NavigationViewItem item, string text)
        {
            if (item.Content is TextBlock tb)
                tb.Text = text;
            else
                item.Content = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
        }

        private static void SetNavItem(NavigationViewItem item, string tag, string? titleOverride = null)
        {
            var title = titleOverride ?? Loc.MenuTitle(tag);
            SetNavContent(item, title);
            var hint = Loc.MenuHint(tag);
            ToolTipService.SetToolTip(item, hint != null ? $"{title} ({hint})" : title);
        }

        private void ApplyMenuCustomization()
        {
            foreach (var kv in _menuItemsByTag)
            {
                kv.Value.Visibility = _settings.HiddenMenuTags.Contains(kv.Key)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            var tagged = NavView.MenuItems.OfType<NavigationViewItem>()
                .Where(i => i.Tag is string)
                .ToDictionary(i => (string)i.Tag!);

            var headers = new[]
            {
                FileSectionHeader,
                ZoomSectionHeader,
                PagesSectionHeader,
                AnnotationsSectionHeader
            };

            for (var section = 0; section < MenuSectionTags.Length; section++)
            {
                var sectionTags = MenuSectionTags[section];
                var header = headers[section];
                var headerIndex = NavView.MenuItems.IndexOf(header);
                if (headerIndex < 0) continue;

                var orderedTags = _settings.MenuOrder
                    .Where(sectionTags.Contains)
                    .ToList();
                foreach (var tag in sectionTags)
                {
                    if (!orderedTags.Contains(tag))
                        orderedTags.Add(tag);
                }

                var sectionItems = orderedTags
                    .Where(tagged.ContainsKey)
                    .Select(tag => tagged[tag])
                    .ToList();

                foreach (var item in sectionItems)
                    NavView.MenuItems.Remove(item);

                var insertAt = headerIndex + 1;
                foreach (var item in sectionItems)
                    NavView.MenuItems.Insert(insertAt++, item);
            }

            UpdateCoverPageMenuVisibility();
        }

        private async Task LoadFileFromCommandLine()
        {
            await Task.Delay(100);
            if (!string.IsNullOrEmpty(App.FileToOpen))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(App.FileToOpen);
                    if (file != null)
                        await LoadPdfFile(file);
                }
                catch (Exception ex)
                {
                    StatusMessageText.Text = Loc.Format("status.errorOpening", ex.Message);
                }
            }

            await CheckDefaultPdfReader();
        }

        private async Task CheckDefaultPdfReader()
        {
            try
            {
                if (_settings.HasShownDefaultReaderPrompt)
                    return;

                await Task.Delay(500);
                var dialog = new ContentDialog
                {
                    Title = Loc.Get("dialog.defaultReader.title"),
                    Content = Loc.Get("dialog.defaultReader.content"),
                    PrimaryButtonText = Loc.Get("dialog.defaultReader.yes"),
                    CloseButtonText = Loc.Get("dialog.defaultReader.notNow"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    OpenDefaultAppsSettings();

                _settings.HasShownDefaultReaderPrompt = true;
                _settings.Save();
            }
            catch { }
        }

        private async void OpenDefaultAppsSettings()
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
                var instructionDialog = new ContentDialog
                {
                    Title = Loc.Get("dialog.defaultReader.howtoTitle"),
                    Content = Loc.Get("dialog.defaultReader.howto"),
                    CloseButtonText = Loc.Get("dialog.ok"),
                    XamlRoot = Content.XamlRoot
                };
                await instructionDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = Loc.Format("status.errorOpeningSettings", ex.Message);
            }
        }

        private void LoadNavigationPaneState()
        {
            NavView.IsPaneOpen = _settings.NavPaneIsOpen;
        }

        private void SaveNavigationPaneState(bool isOpen)
        {
            _settings.NavPaneIsOpen = isOpen;
            try { _settings.Save(); } catch { }
        }

        private void ApplyOutlinePaneState()
        {
            OutlineSplitView.IsPaneOpen = _settings.OutlinePaneIsOpen;
            OutlineItem.IsSelected = _settings.OutlinePaneIsOpen;
        }

        private void SaveOutlinePaneState(bool isOpen)
        {
            _settings.OutlinePaneIsOpen = isOpen;
            try { _settings.Save(); } catch { }
        }

        private async Task ToggleOutlinePane()
        {
            if (_pdfDocument == null)
            {
                StatusMessageText.Text = Loc.Get("status.noPdf");
                return;
            }

            var open = !OutlineSplitView.IsPaneOpen;
            OutlineSplitView.IsPaneOpen = open;
            OutlineItem.IsSelected = open;
            SaveOutlinePaneState(open);
            if (open)
                await EnsureOutlineLoadedAsync();
            else
                RefreshOutlineEmptyState();
            await Task.CompletedTask;
        }

        private void RefreshOutlineEmptyState()
        {
            var hasEntries = _pdfOutline?.HasEntries == true;
            OutlineTreeView.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
            OutlineEmptyText.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
            OutlineEmptyText.Text = Loc.Get("outline.empty");
        }

        private void PopulateOutlineTree()
        {
            OutlineTreeView.RootNodes.Clear();
            if (_pdfOutline?.HasEntries == true)
            {
                foreach (var entry in _pdfOutline.Roots)
                    OutlineTreeView.RootNodes.Add(CreateOutlineNode(entry));
            }

            RefreshOutlineEmptyState();
        }

        private static TreeViewNode CreateOutlineNode(PdfOutlineEntry entry)
        {
            var node = new TreeViewNode
            {
                Content = new OutlineTreeItem
                {
                    Title = entry.Title,
                    PageIndex = entry.PageIndex
                }
            };

            foreach (var child in entry.Children)
                node.Children.Add(CreateOutlineNode(child));

            return node;
        }

        private async void OutlineTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not TreeViewNode node ||
                node.Content is not OutlineTreeItem item ||
                item.PageIndex is not int pageIndex ||
                pageIndex < 0)
                return;

            await GoToPageIndex((uint)pageIndex);
        }

        private async Task GoToPageIndex(uint targetIndex)
        {
            if (_pdfDocument == null || targetIndex >= _pdfDocument.PageCount)
                return;

            _currentPageIndex = targetIndex;
            if (_isContinuousMode)
            {
                ScrollToCurrentPage();
                UpdateStatusPageText();
            }
            else
            {
                await RenderCurrentPage();
            }

            PersistCurrentSession();
        }

        private void ShowLoading(string message)
        {
            _loadingDepth++;
            LoadingOverlayText.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
            NavView.IsEnabled = false;
        }

        private void HideLoading()
        {
            _loadingDepth = Math.Max(0, _loadingDepth - 1);
            if (_loadingDepth == 0)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                NavView.IsEnabled = true;
            }
        }

        private async void Window_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.OriginalSource is TextBox or NumberBox or PasswordBox or RichEditBox)
                return;

            if (_isGoToPageOpen)
                return;

            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrl && e.Key == VirtualKey.F)
            {
                if (_pdfDocument != null)
                {
                    ShowViewerContent();
                    ShowFindBar();
                    e.Handled = true;
                }
                return;
            }

            if (ctrl && !shift && e.Key == VirtualKey.O)
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("open");
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && e.Key == VirtualKey.P)
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("print");
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && e.Key == VirtualKey.G)
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("gotopage");
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && e.Key == VirtualKey.E)
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("edit");
                e.Handled = true;
                return;
            }

            if (ctrl && (e.Key == VirtualKey.Add || e.Key == (VirtualKey)187))
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("zoomin");
                e.Handled = true;
                return;
            }

            if (ctrl && (e.Key == VirtualKey.Subtract || e.Key == (VirtualKey)189))
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("zoomout");
                e.Handled = true;
                return;
            }

            if (ctrl && !shift && e.Key == VirtualKey.Number0)
            {
                ShowViewerContent();
                await InvokeMenuActionAsync("zoomreset");
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape && FindBar.Visibility == Visibility.Visible)
            {
                CloseFindBar();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == VirtualKey.C)
            {
                foreach (var overlay in EnumerateOverlays())
                {
                    if (overlay.CopyPdfSelection())
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (ctrl && e.Key == VirtualKey.Z)
            {
                if (shift) _history.Redo();
                else _history.Undo();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == VirtualKey.Y)
            {
                _history.Redo();
                e.Handled = true;
                return;
            }

            if (e.Key is VirtualKey.Delete or VirtualKey.Back)
            {
                if (DeleteSelectedAnnotations())
                    e.Handled = true;
                return;
            }

            if (_isContinuousMode)
            {
                if (e.Key is VirtualKey.Up or VirtualKey.PageUp)
                {
                    await PreviousPage();
                    e.Handled = true;
                    return;
                }

                if (e.Key is VirtualKey.Down or VirtualKey.PageDown)
                {
                    await NextPage();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key is VirtualKey.Left or VirtualKey.Up)
            {
                await PreviousPage();
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Right or VirtualKey.Down)
            {
                await NextPage();
                e.Handled = true;
            }
        }

        private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ShowSettingsContent();
                return;
            }

            ShowViewerContent();
            var tag = args.InvokedItemContainer?.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag) &&
                tag != "recentfiles" &&
                !_menuItemsByTag.ContainsKey(tag) &&
                Path.IsPathRooted(tag))
            {
                await OpenRecentFileAsync(tag);
                RefreshModeLabels();
                return;
            }

            await InvokeMenuActionAsync(tag);
        }

        private async Task InvokeMenuActionAsync(string? tag)
        {
            if (string.IsNullOrEmpty(tag))
                return;

            switch (tag)
            {
                case "open":
                    await OpenFileDialog();
                    break;
                case "zoomin":
                    await ZoomIn();
                    break;
                case "zoomout":
                    await ZoomOut();
                    break;
                case "zoomreset":
                    await ZoomReset();
                    break;
                case "zoomfit":
                    await ZoomFit();
                    break;
                case "find":
                    ShowFindBar();
                    break;
                case "prevpage":
                    await PreviousPage();
                    break;
                case "nextpage":
                    await NextPage();
                    break;
                case "doublepagemode":
                    await ToggleDoublePageMode();
                    break;
                case "coverpagemode":
                    await ToggleCoverPageMode();
                    break;
                case "continuousmode":
                    await ToggleContinuousMode();
                    break;
                case "outline":
                    await ToggleOutlinePane();
                    break;
                case "gotopage":
                    await ShowGoToPageDialog();
                    break;
                case "edit":
                    ShowEditToolbar();
                    break;
                case "clearink":
                    await ClearInkAnnotationsAsync();
                    break;
                case "savewithannotations":
                    await SavePdfWithAnnotations();
                    break;
                case "print":
                    await PrintPdf();
                    break;
            }

            RefreshModeLabels();
        }

        private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
        }

        private void AppTitleBar_BackRequested(TitleBar sender, object args)
        {
            ShowViewerContent();
        }

        private void ApplyThemeToWindow()
        {
            if (Content is FrameworkElement root)
                root.RequestedTheme = _settings.Theme;
            RootGrid.RequestedTheme = _settings.Theme;
            NavView.RequestedTheme = _settings.Theme;
            AppTitleBar.RequestedTheme = _settings.Theme;

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            var titleBar = appWindow.TitleBar;
            var useLight = _settings.Theme switch
            {
                ElementTheme.Light => true,
                ElementTheme.Dark => false,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Light
            };

            if (_configurationSource != null)
                _configurationSource.Theme = useLight
                    ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light
                    : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark;

            if (useLight)
            {
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(160, 0, 0, 0);
                titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(20, 0, 0, 0);
                titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(30, 0, 0, 0);
            }
            else
            {
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(160, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(30, 255, 255, 255);
            }
        }

        private void ApplyMenuPosition()
        {
            NavView.FlowDirection = FlowDirection.LeftToRight;
            NavContentRoot.FlowDirection = FlowDirection.LeftToRight;
            switch (_settings.MenuPosition)
            {
                case "Top":
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Top;
                    break;
                case "Right":
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                    NavView.FlowDirection = FlowDirection.RightToLeft;
                    NavContentRoot.FlowDirection = FlowDirection.LeftToRight;
                    break;
                case "LeftCompact":
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
                    break;
                default:
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                    break;
            }

            AppTitleBar.IsPaneToggleButtonVisible = _settings.MenuPosition != "Top";

            foreach (var item in NavView.MenuItems.OfType<FrameworkElement>())
                item.FlowDirection = FlowDirection.LeftToRight;
        }

        private void ShowSettingsContent()
        {
            ViewerPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Collapsed;
            SettingsHost.Visibility = Visibility.Visible;
            AppTitleBar.IsBackButtonVisible = true;
            AppTitleBar.IsBackButtonEnabled = true;
            AppSettingsPanel.LoadSettings(_settings);
        }

        private void ShowViewerContent()
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            ViewerPanel.Visibility = Visibility.Visible;
            AppTitleBar.IsBackButtonVisible = false;
            AppTitleBar.IsBackButtonEnabled = false;
            if (_pdfDocument == null)
                WelcomePanel.Visibility = Visibility.Visible;
        }

        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
        {
            PersistCurrentSession();
            FlushRecentSave();
            _indexLoadCts?.Cancel();
            if (_currentFile?.Path is { } path)
                await AnnotationSidecarStore.SaveAsync(path, _annotations);

            if (_forceClose || !_annotations.HasAny())
            {
                PasswordPdfOpener.TryDeleteTemp(_decryptedTempPath);
                AppLog.Shutdown();
                return;
            }

            e.Cancel = true;
            var dialog = new ContentDialog
            {
                Title = Loc.Get("dialog.unsaved.title"),
                Content = Loc.Get("dialog.unsaved.content"),
                PrimaryButtonText = Loc.Get("dialog.unsaved.save"),
                SecondaryButtonText = Loc.Get("dialog.unsaved.discard"),
                CloseButtonText = Loc.Get("dialog.unsaved.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await SavePdfWithAnnotations();
                _forceClose = true;
                PasswordPdfOpener.TryDeleteTemp(_decryptedTempPath);
                AppLog.Shutdown();
                Close();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _forceClose = true;
                PasswordPdfOpener.TryDeleteTemp(_decryptedTempPath);
                AppLog.Shutdown();
                Close();
            }
        }

        private bool DeleteSelectedAnnotations()
        {
            var deleted = false;
            foreach (var overlay in EnumerateOverlays())
                deleted |= overlay.DeleteSelection();
            return deleted;
        }

        private async void PageNumberBox_AnyKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || !_isGoToPageOpen) return;
            if (e.OriginalSource is TextBox inner && double.TryParse(inner.Text, out var typed))
                PageNumberBox.Value = typed;
            e.Handled = true;
            await NavigateToPage();
            GoToPageDialog.Hide();
        }

        private async void PageNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
                return;
            e.Handled = true;
            await NavigateToPage();
            GoToPageDialog.Hide();
        }

        private void SetupCustomTitleBar()
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            var titleBar = appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(30, 255, 255, 255);

            SetTitleBar(AppTitleBar);
        }

        private void SetupMicaBackground()
        {
            if (!MicaController.IsSupported()) return;
            _micaController = new MicaController { Kind = MicaKind.BaseAlt };
            _configurationSource = new SystemBackdropConfiguration();
            Activated += OnWindowActivated;
            Closed += OnWindowClosed;
            var backdropTarget = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
            _micaController.AddSystemBackdropTarget(backdropTarget);
            _micaController.SetSystemBackdropConfiguration(_configurationSource);
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_configurationSource != null)
                _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _micaController?.Dispose();
            _micaController = null;
            _configurationSource = null;
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e) => await OpenFileDialog();

        private async void RecentFilesGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is RecentFileDisplayItem item)
                await OpenRecentFileAsync(item.Path);
        }

        private async Task OpenRecentFileAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await LoadPdfFile(file, restoreSession: true);
            }
            catch
            {
                _recentFiles.Remove(path);
                FlushRecentSave();
                RefreshRecentFilesUi();
                StatusMessageText.Text = Loc.Format("status.recentMissing", Path.GetFileName(path));
            }
        }

        private void PersistCurrentSession()
        {
            if (_currentFile?.Path is not { } path)
                return;

            _recentFiles.UpdateSession(path, _currentPageIndex, _currentZoom, _settings.MaxZoomPercent);
            ScheduleRecentSave();
        }

        private void ScheduleRecentSave()
        {
            _sessionSaveTimer ??= DispatcherQueue.CreateTimer();
            _sessionSaveTimer.Interval = TimeSpan.FromSeconds(1);
            _sessionSaveTimer.IsRepeating = false;
            if (_sessionSaveTimer.IsRunning)
                return;
            _sessionSaveTimer.Tick -= SessionSaveTimer_Tick;
            _sessionSaveTimer.Tick += SessionSaveTimer_Tick;
            _sessionSaveTimer.Start();
        }

        private void SessionSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            FlushRecentSave();
        }

        private void FlushRecentSave() => _recentFiles.Save();

        private void RefreshRecentFilesUi()
        {
            RefreshRecentFilesMenu();

            var items = new List<RecentFileDisplayItem>();
            foreach (var entry in _recentFiles.GetEntries())
            {
                if (!File.Exists(entry.Path))
                    continue;

                var date = entry.LastOpenedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);
                var page = Loc.Format("welcome.recentPage", entry.PageIndex + 1);
                var zoom = $"{(entry.Zoom * 100):F0}%";
                BitmapImage? cover = null;
                var coverPath = _recentFiles.GetCoverPath(entry);
                if (coverPath != null)
                {
                    cover = GetCachedCoverImage(coverPath);
                }

                items.Add(new RecentFileDisplayItem
                {
                    Path = entry.Path,
                    FileName = Path.GetFileName(entry.Path),
                    Subtitle = $"{date} · {page} · {zoom}",
                    CoverImage = cover
                });
            }

            var hasRecents = items.Count > 0;
            WelcomeHeroPanel.Visibility = hasRecents ? Visibility.Collapsed : Visibility.Visible;
            WelcomeCompactHeader.Visibility = hasRecents ? Visibility.Visible : Visibility.Collapsed;
            RecentFilesSection.Visibility = hasRecents ? Visibility.Visible : Visibility.Collapsed;
            RecentFilesGrid.ItemsSource = items;
        }

        private void RefreshRecentFilesMenu()
        {
            RecentFilesItem.MenuItems.Clear();
            var entries = _recentFiles.GetEntries().Where(e => File.Exists(e.Path)).Take(RecentFilesStore.MaxEntries).ToList();
            if (entries.Count == 0)
            {
                RecentFilesItem.MenuItems.Add(new NavigationViewItem
                {
                    Content = Loc.Get("welcome.recentEmpty"),
                    IsEnabled = false
                });
                return;
            }

            foreach (var entry in entries)
            {
                RecentFilesItem.MenuItems.Add(new NavigationViewItem
                {
                    Content = Path.GetFileName(entry.Path),
                    Tag = entry.Path
                });
            }
        }

        private async Task EnsureCoverAndRefreshAsync()
        {
            if (_pdfDocument == null || _currentFile?.Path is not { } path)
                return;

            await RecentFilesStore.EnsureCoverAsync(_pdfDocument, path);
            RefreshRecentFilesUi();
        }

        private async Task OpenFileDialog()
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                picker.FileTypeFilter.Add(".pdf");
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                    await LoadPdfFile(file);
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = Loc.Format("status.fileOpenError", ex.Message);
            }
        }

        private async Task LoadPdfFile(StorageFile file, bool restoreSession = true)
        {
            PersistCurrentSession();
            FlushRecentSave();
            PasswordPdfOpener.TryDeleteTemp(_decryptedTempPath);
            _decryptedTempPath = null;

            var recentEntry = restoreSession ? _recentFiles.Find(file.Path) : null;

            ShowLoading(Loc.Get("loading.opening"));
            LoadDiagnostics? loadDiag = null;
            try
            {
                PdfDocument? loaded = null;
                StorageFile workingFile = file;
                byte[]? pdfBytes = null;

                using (LoadDiagnostics.Step("nativeOpen"))
                {
                    try
                    {
                        loaded = await PdfDocument.LoadFromFileAsync(file);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"PDF open requires password or failed: {ex.Message}");
                    }
                }

                if (loaded == null)
                {
                    HideLoading();
                    var password = await PromptPasswordAsync();
                    if (password == null)
                    {
                        SetStatusMessage(Loc.Get("status.unableOpen"));
                        return;
                    }

                    ShowLoading(Loc.Get("loading.opening"));
                    using (LoadDiagnostics.Step("passwordOpen"))
                    {
                        var (doc, work, temp) = await PasswordPdfOpener.TryOpenAsync(file, password);
                        if (doc == null)
                        {
                            SetStatusMessage(Loc.Get("dialog.password.failed"));
                            AppLog.Warn("Password rejected or decrypt failed");
                            return;
                        }

                        loaded = doc;
                        workingFile = work ?? file;
                        _decryptedTempPath = temp;
                        pdfBytes = await PdfPigServices.ReadBytesAsync(workingFile);
                    }
                }

                _pdfDocument = loaded;
                if (_pdfDocument == null)
                {
                    SetStatusMessage(Loc.Get("status.unableOpen"));
                    return;
                }

                loadDiag = LoadDiagnostics.BeginLoad(
                    file.Name,
                    pdfBytes?.LongLength ?? new FileInfo(workingFile.Path).Length,
                    _pdfDocument.PageCount);

                _currentFile = file;
                if (recentEntry != null)
                {
                    _currentPageIndex = ViewerSession.ClampPageIndex(recentEntry.PageIndex, _pdfDocument.PageCount);
                    _currentZoom = ViewerSession.ClampZoom(recentEntry.Zoom, _settings.MaxZoomPercent);
                }
                else
                {
                    _currentPageIndex = 0;
                    _currentZoom = 0.5;
                }

                PdfScrollViewer.ChangeView(null, null, 1f, true);
                _lastScrollZoom = 1f;
                UpdateZoomUi(_currentZoom);
                TitleBarFileName.Text = file.Name;
                WelcomePanel.Visibility = Visibility.Collapsed;
                SettingsHost.Visibility = Visibility.Collapsed;
                ViewerPanel.Visibility = Visibility.Visible;
                ResetDocumentCaches();
                _pageCache.Clear();
                _annotations.Clear();
                _history.Clear();

                using (LoadDiagnostics.Step("sidecar"))
                {
                    await AnnotationSidecarStore.TryLoadAsync(file.Path, _annotations);
                }

                _pdfOutline = null;
                OutlineTreeView.RootNodes.Clear();
                RefreshOutlineEmptyState();

                using (LoadDiagnostics.Step("labels"))
                {
                    _pageLabels = PdfPageLabels.LoadFromPath(workingFile.Path, (int)_pdfDocument.PageCount);
                }

                CachePageSize(_currentPageIndex);
                _textIndexSourcePath = workingFile.Path;
                _textIndex = PdfTextIndex.CreateLazy(workingFile.Path, _pageSizes);

                BindAnnotationOverlays();
                SetToolMode(_currentTool);
                CloseFindBar();
                _rasterZoom = _currentZoom;
                UpdateScrollViewerZoomLimits();

                using (LoadDiagnostics.Step("firstRender"))
                {
                    if (_isContinuousMode)
                    {
                        await BuildContinuousLayoutAsync();
                        if (recentEntry != null)
                            ScrollToCurrentPage();
                    }
                    else
                    {
                        await RenderCurrentPage();
                    }
                }

                _recentFiles.RecordOpened(file.Path, _currentPageIndex, _currentZoom, _settings.MaxZoomPercent);
                FlushRecentSave();
                RefreshRecentFilesUi();
                _ = EnsureCoverAndRefreshAsync();

                SetStatusMessage(Loc.Format("status.fileLoaded", file.Name));
                AppLog.Info($"Opened PDF: {file.Name} ({_pdfDocument.PageCount} pages)");

                _indexLoadCts = new CancellationTokenSource();
                _ = LoadDocumentIndexInBackgroundAsync(workingFile.Path, _indexLoadCts.Token);
            }
            catch (Exception ex)
            {
                SetStatusMessage(Loc.Format("status.error", ex.Message));
                AppLog.Error("LoadPdfFile failed", ex);
            }
            finally
            {
                HideLoading();
                loadDiag?.Dispose();
                var wsMb = LoadDiagnostics.GetWorkingSetMb();
                LoadDiagnostics.Complete(wsMb);
            }
        }

        private int ZoomKey => (int)Math.Round(_currentZoom * 100);

        private async Task<BitmapImage> RenderPageBitmapAsync(uint pageIndex, bool showOverlayOnMiss = false)
        {
            if (_pdfDocument == null)
                throw new InvalidOperationException("No document");

            var cached = await _pageCache.TryGetBitmapAsync(pageIndex, ZoomKey);
            if (cached != null)
                return cached;

            if (showOverlayOnMiss)
                ShowLoading(Loc.Format("loading.rendering", pageIndex + 1));

            try
            {
                var (destW, destH) = GetRasterDestinationSize(pageIndex);

                using var page = _pdfDocument.GetPage(pageIndex);
                var renderOptions = new PdfPageRenderOptions
                {
                    DestinationWidth = destW,
                    DestinationHeight = destH
                };

                // RenderToStreamAsync emits PNG; store those bytes and keep only a few decoded images hot.
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, renderOptions);
                var pngBytes = await PdfPageCache.CopyStreamToBytesAsync(stream);
                var bitmapImage = await PdfPageCache.DecodeToBitmapAsync(pngBytes);
                _pageCache.Set(pageIndex, ZoomKey, pngBytes, bitmapImage);
                return bitmapImage;
            }
            finally
            {
                if (showOverlayOnMiss)
                    HideLoading();
            }
        }

        private async Task RenderCurrentPage(bool showLoadingOnMiss = true)
        {
            if (_pdfDocument == null) return;

            try
            {
                if (_isContinuousMode)
                {
                    await BuildContinuousLayoutAsync();
                    return;
                }

                if (_isDoublePageMode)
                {
                    uint leftIndex, rightIndex;
                    bool showLeft = true, showRight = true;

                    if (_isCoverPageMode)
                    {
                        if (_currentPageIndex == 0)
                        {
                            leftIndex = 0;
                            showLeft = false;
                            rightIndex = 0;
                        }
                        else
                        {
                            var baseIndex = _currentPageIndex % 2 == 0 ? _currentPageIndex - 1 : _currentPageIndex;
                            leftIndex = baseIndex;
                            rightIndex = baseIndex + 1;
                        }
                    }
                    else
                    {
                        var baseIndex = _currentPageIndex % 2 != 0 ? _currentPageIndex - 1 : _currentPageIndex;
                        leftIndex = baseIndex;
                        rightIndex = baseIndex + 1;
                    }

                    if (showLeft && leftIndex < _pdfDocument.PageCount)
                    {
                        PdfImageLeft.Source = await RenderPageBitmapAsync(leftIndex, showLoadingOnMiss);
                        ConfigureOverlay(PdfAnnotationOverlayLeft, leftIndex);
                    }
                    else
                    {
                        PdfImageLeft.Source = null;
                    }

                    if (showRight && rightIndex < _pdfDocument.PageCount)
                    {
                        PdfImageRight.Source = await RenderPageBitmapAsync(rightIndex, showLoadingOnMiss);
                        ConfigureOverlay(PdfAnnotationOverlayRight, rightIndex);
                    }
                    else
                    {
                        PdfImageRight.Source = null;
                    }

                    _currentPageIndex = showLeft ? leftIndex : rightIndex;
                    SetStatusPageForIndices(
                        leftIndex,
                        rightIndex < _pdfDocument.PageCount ? rightIndex : null,
                        showLeft,
                        showRight && rightIndex < _pdfDocument.PageCount);
                }
                else
                {
                    PdfImage.Source = await RenderPageBitmapAsync(_currentPageIndex, showLoadingOnMiss);
                    ConfigureOverlay(PdfAnnotationOverlay, _currentPageIndex);
                    UpdateStatusPageText();
                }
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = Loc.Format("status.renderError", ex.Message);
            }
        }

        private void PageImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Image image) return;
            AnnotationOverlay? overlay = null;
            if (image == PdfImage) overlay = PdfAnnotationOverlay;
            else if (image == PdfImageLeft) overlay = PdfAnnotationOverlayLeft;
            else if (image == PdfImageRight) overlay = PdfAnnotationOverlayRight;

            if (overlay == null) return;
            overlay.Width = image.ActualWidth;
            overlay.Height = image.ActualHeight;
            overlay.HorizontalAlignment = HorizontalAlignment.Center;
            overlay.VerticalAlignment = VerticalAlignment.Center;
        }

        private async Task PreviousPage()
        {
            if (_currentPageIndex == 0) return;

            if (_isContinuousMode)
            {
                _currentPageIndex--;
                ScrollToCurrentPage();
                UpdateStatusPageText();
                PersistCurrentSession();
                return;
            }

            if (_isDoublePageMode)
            {
                if (_isCoverPageMode)
                {
                    if (_currentPageIndex == 0) return;
                    if (_currentPageIndex == 1) _currentPageIndex = 0;
                    else _currentPageIndex = _currentPageIndex >= 2 ? _currentPageIndex - 2 : 0;
                }
                else
                {
                    _currentPageIndex = _currentPageIndex >= 2 ? _currentPageIndex - 2 : 0;
                }
            }
            else
            {
                _currentPageIndex--;
            }

            await RenderCurrentPage();
            PersistCurrentSession();
        }

        private async Task NextPage()
        {
            if (_pdfDocument == null || _currentPageIndex >= _pdfDocument.PageCount - 1) return;

            if (_isContinuousMode)
            {
                _currentPageIndex++;
                ScrollToCurrentPage();
                UpdateStatusPageText();
                PersistCurrentSession();
                return;
            }

            if (_isDoublePageMode)
            {
                if (_isCoverPageMode)
                {
                    if (_currentPageIndex == 0) _currentPageIndex = 1;
                    else _currentPageIndex = Math.Min(_currentPageIndex + 2, _pdfDocument.PageCount - 1);
                }
                else
                {
                    _currentPageIndex = Math.Min(_currentPageIndex + 2, _pdfDocument.PageCount - 1);
                }
            }
            else
            {
                _currentPageIndex++;
            }

            await RenderCurrentPage();
            PersistCurrentSession();
        }

        private void ViewerPanel_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride != null)
            {
                e.DragUIOverride.Caption = Loc.Get("status.dropPdf");
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }

        private async void ViewerPanel_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                await LoadPdfFile(file);
            else
                StatusMessageText.Text = Loc.Get("status.notPdf");
        }

        private async Task ShowGoToPageDialog()
        {
            if (_pdfDocument == null || _isGoToPageOpen) return;
            _isGoToPageOpen = true;
            try
            {
                PageNumberBox.Maximum = _pdfDocument.PageCount;
                if (_pageLabels is { IsIdentity: false } labels &&
                    uint.TryParse(labels.GetLabel(_currentPageIndex), out var logical) &&
                    logical >= 1)
                {
                    PageNumberBox.Value = logical;
                }
                else
                {
                    PageNumberBox.Value = _currentPageIndex + 1;
                }
                GoToPageDialog.XamlRoot = Content.XamlRoot;
                await GoToPageDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = Loc.Format("status.gotoError", ex.Message);
            }
            finally
            {
                _isGoToPageOpen = false;
            }
        }

        private async void GoToPageDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                await NavigateToPage();
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void ScrollToCurrentPage()
        {
            if (!_isContinuousMode || _currentPageIndex >= _continuousPageItems.Count) return;

            double y = 0;
            for (var i = 0; i < _currentPageIndex; i++)
                y += _continuousPageItems[(int)i].LayoutHeight;

            PdfScrollViewer.ChangeView(null, y, null, false);
            _ = RenderVisibleContinuousPagesAsync();
        }

        private async Task NavigateToPage()
        {
            if (_pdfDocument == null) return;

            double requestedValue = PageNumberBox.Value;
            if (double.TryParse(PageNumberBox.Text, out var parsedValue))
            {
                requestedValue = parsedValue;
                PageNumberBox.Value = parsedValue;
            }

            if (requestedValue < 1)
                return;

            var requestedInt = (int)Math.Round(requestedValue);
            var requestedLabel = requestedInt.ToString();

            uint targetIndex;
            if (_pageLabels is { IsIdentity: false } &&
                _pageLabels.TryFindPageIndex(requestedLabel, out var labeledIndex))
            {
                targetIndex = labeledIndex;
            }
            else
            {
                int offset = 0;
                if (_pageLabels == null && _currentFile != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(_currentFile.Name, @"_\+(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var extractedOffset))
                        offset = extractedOffset;
                }

                var targetPage = requestedInt + offset;
                if (targetPage < 1 || targetPage > _pdfDocument.PageCount)
                    return;

                targetIndex = (uint)(targetPage - 1);
            }

            if (targetIndex >= _pdfDocument.PageCount)
                return;

            _currentPageIndex = targetIndex;
            if (_isContinuousMode)
            {
                ScrollToCurrentPage();
                UpdateStatusPageText();
            }
            else
            {
                await RenderCurrentPage();
            }

            PersistCurrentSession();
        }

        private void SetToolMode(AnnotationTool mode)
        {
            _currentTool = mode;
            // Do not auto-show: toolbar opens only via Edit.
            AnnotationToolbar.SetActive(mode, _activePenSlot);
            AnnotationToolbar.SetHistoryState(_history.CanUndo, _history.CanRedo);
            ApplyTextDefaultsToOverlays();

            if (mode == AnnotationTool.Select)
            {
                PdfScrollViewer.HorizontalScrollMode = ScrollMode.Enabled;
                PdfScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
                PdfScrollViewer.ZoomMode = ZoomMode.Enabled;
            }
            else
            {
                // Keep wheel scrolling via handler; disable pan/pinch so pen and touch can ink.
                PdfScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
                PdfScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                PdfScrollViewer.ZoomMode = ZoomMode.Disabled;
            }

            PdfAnnotationOverlay.SetTool(mode);
            PdfAnnotationOverlayLeft.SetTool(mode);
            PdfAnnotationOverlayRight.SetTool(mode);
            foreach (var host in _realizedContinuousHosts.Values)
                host.Overlay?.SetTool(mode);
        }

        private void OnAnnotationSelectionChanged(object? sender, EventArgs e)
        {
            var selected = EnumerateOverlays()
                .Select(o => o.SelectedText)
                .FirstOrDefault(t => t != null);
            if (selected == null) return;
            _textFontSize = selected.FontSize;
            _textBold = selected.IsBold;
            _textItalic = selected.IsItalic;
            _textColor = selected.Color;
        }

        private async Task ClearInkAnnotationsAsync()
        {
            if (_settings.ConfirmClearAnnotations)
            {
                var dialog = new ContentDialog
                {
                    Title = Loc.Get("dialog.clear.title"),
                    Content = Loc.Get("dialog.clear.content"),
                    PrimaryButtonText = Loc.Get("dialog.clear.confirm"),
                    CloseButtonText = Loc.Get("dialog.clear.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            _annotations.Clear();
            _history.Clear();
            RefreshAllOverlays();
        }

        private async Task ToggleDoublePageMode()
        {
            if (_isContinuousMode)
            {
                _isContinuousMode = false;
                ContinuousPagesRepeater.Visibility = Visibility.Collapsed;
                _continuousPageItems.Clear();
                _realizedContinuousHosts.Clear();
                ContinuousPagesRepeater.ItemsSource = null;
                DoublePageItem.IsEnabled = true;
            }

            _isDoublePageMode = !_isDoublePageMode;
            if (_isDoublePageMode)
            {
                SinglePageContainer.Visibility = Visibility.Collapsed;
                DoublePageContainer.Visibility = Visibility.Visible;

                if (_isCoverPageMode)
                {
                    if (_currentPageIndex > 0 && _currentPageIndex % 2 == 0) _currentPageIndex--;
                }
                else if (_currentPageIndex % 2 != 0)
                {
                    _currentPageIndex--;
                }
            }
            else
            {
                SinglePageContainer.Visibility = Visibility.Visible;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                _isCoverPageMode = false;
            }

            RefreshModeLabels();
            await RenderCurrentPage();
        }

        private async Task ToggleCoverPageMode()
        {
            if (!_isDoublePageMode)
                return;

            _isCoverPageMode = !_isCoverPageMode;
            if (_isDoublePageMode)
            {
                if (_isCoverPageMode)
                {
                    if (_currentPageIndex > 0 && _currentPageIndex % 2 == 0)
                        _currentPageIndex--;
                }
                else if (_currentPageIndex % 2 != 0)
                {
                    _currentPageIndex--;
                }
            }

            RefreshModeLabels();
            await RenderCurrentPage();
        }

        private async Task ToggleContinuousMode()
        {
            _isContinuousMode = !_isContinuousMode;
            if (_isContinuousMode)
            {
                _isDoublePageMode = false;
                SinglePageContainer.Visibility = Visibility.Collapsed;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                ContinuousPagesRepeater.Visibility = Visibility.Visible;
                _isCoverPageMode = false;
                DoublePageItem.IsEnabled = false;
                ZoomInItem.IsEnabled = true;
                ZoomOutItem.IsEnabled = true;
                ZoomResetItem.IsEnabled = true;
                ZoomFitItem.IsEnabled = true;
                RefreshModeLabels();
                await BuildContinuousLayoutAsync();
            }
            else
            {
                DoublePageItem.IsEnabled = true;
                SinglePageContainer.Visibility = Visibility.Visible;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                ContinuousPagesRepeater.Visibility = Visibility.Collapsed;
                _continuousPageItems.Clear();
                _realizedContinuousHosts.Clear();
                ContinuousPagesRepeater.ItemsSource = null;
                RefreshModeLabels();
                await RenderCurrentPage();
            }
        }

        private async Task BuildContinuousLayoutAsync()
        {
            if (_pdfDocument == null) return;
            ShowLoading(Loc.Get("loading.continuous"));
            try
            {
                _continuousPageItems.Clear();
                _realizedContinuousHosts.Clear();

                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                {
                    if (!_pageSizes.ContainsKey(i))
                        CachePageSize(i);

                    var size = GetPageSize(i);
                    var (displayW, displayH) = GetDisplayDimensions(i);
                    _continuousPageItems.Add(new ContinuousPageItem
                    {
                        PageIndex = i,
                        DisplayWidth = displayW,
                        DisplayHeight = displayH,
                        PageWidthDip = size.Width,
                        PageHeightDip = size.Height
                    });
                }

                ContinuousPagesRepeater.ItemsSource = _continuousPageItems;
                UpdateStatusPageText();
                await RenderVisibleContinuousPagesAsync();
                ScrollToCurrentPage();
            }
            finally
            {
                HideLoading();
            }
        }

        private async void PdfScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate && Math.Abs(PdfScrollViewer.ZoomFactor - _lastScrollZoom) > 0.02 && !_pinchZoomQueued && !_zoomSettling)
            {
                _pinchZoomQueued = true;
                try
                {
                    UpdateZoomUi(_currentZoom * PdfScrollViewer.ZoomFactor);
                    ScheduleZoomSettle();
                }
                finally
                {
                    _pinchZoomQueued = false;
                }
            }

            if (_isContinuousMode && !e.IsIntermediate)
            {
                await RenderVisibleContinuousPagesAsync();
                UpdateContinuousStatusPageFromScroll();
            }
        }

        private void UpdateContinuousStatusPageFromScroll()
        {
            if (_pdfDocument == null || _continuousPageItems.Count == 0) return;
            double y = 0;
            double center = PdfScrollViewer.VerticalOffset + PdfScrollViewer.ViewportHeight / 3;
            for (var i = 0; i < _continuousPageItems.Count; i++)
            {
                var next = y + _continuousPageItems[i].LayoutHeight;
                if (center >= y && center < next)
                {
                    var newIndex = _continuousPageItems[i].PageIndex;
                    if (_currentPageIndex == newIndex) return;
                    _currentPageIndex = newIndex;
                    UpdateStatusPageText();
                    PersistCurrentSession();
                    break;
                }
                y = next;
            }
        }

        private async Task RenderVisibleContinuousPagesAsync()
        {
            if (_pdfDocument == null || _continuousPageItems.Count == 0 || _continuousRenderQueued) return;
            _continuousRenderQueued = true;
            try
            {
                await Task.Delay(16);
                double top = PdfScrollViewer.VerticalOffset - PdfScrollViewer.ViewportHeight;
                double bottom = PdfScrollViewer.VerticalOffset + PdfScrollViewer.ViewportHeight * 2;
                double y = 0;
                var toRender = new List<uint>();

                foreach (var item in _continuousPageItems)
                {
                    var pageTop = y;
                    var pageBottom = y + item.LayoutHeight;
                    var visible = pageBottom >= top && pageTop <= bottom;

                    if (visible &&
                        _realizedContinuousHosts.TryGetValue(item.PageIndex, out var host) &&
                        !host.Rendered)
                    {
                        toRender.Add(item.PageIndex);
                    }

                    y = pageBottom;
                }

                await PrefetchPageBitmapsAsync(toRender);
                foreach (var pageIndex in toRender)
                {
                    if (!_realizedContinuousHosts.TryGetValue(pageIndex, out var host)) continue;
                    var overlay = EnsureContinuousOverlay(host);
                    host.Image.Source = await RenderPageBitmapAsync(host.Index);
                    overlay.Refresh();
                    host.Rendered = true;
                }
            }
            finally
            {
                _continuousRenderQueued = false;
            }
        }

        private async void PdfScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(PdfScrollViewer).Properties;
            var delta = props.MouseWheelDelta;
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if ((_settings.WheelZoomRequiresCtrl && ctrl) || (!_settings.WheelZoomRequiresCtrl && !ctrl))
            {
                if (delta > 0) await ZoomIn();
                else await ZoomOut();
                e.Handled = true;
                return;
            }

            if (PdfScrollViewer.VerticalScrollMode == ScrollMode.Disabled)
            {
                PdfScrollViewer.ChangeView(null, PdfScrollViewer.VerticalOffset - delta, null, true);
                e.Handled = true;
            }
        }

        private void ApplyTextDefaultsToOverlays()
        {
            foreach (var overlay in EnumerateOverlays())
            {
                overlay.DefaultFontSize = _textFontSize;
                overlay.DefaultBold = _textBold;
                overlay.DefaultItalic = _textItalic;
                overlay.DefaultTextColor = _textColor;
            }
        }

        private IEnumerable<AnnotationOverlay> EnumerateOverlays()
        {
            yield return PdfAnnotationOverlay;
            yield return PdfAnnotationOverlayLeft;
            yield return PdfAnnotationOverlayRight;
            foreach (var host in _realizedContinuousHosts.Values)
            {
                if (host.Overlay != null)
                    yield return host.Overlay;
            }
        }

        private void ApplyPenAttributesToOverlays()
        {
            ApplyPenAttributesToOverlay(PdfAnnotationOverlay);
            ApplyPenAttributesToOverlay(PdfAnnotationOverlayLeft);
            ApplyPenAttributesToOverlay(PdfAnnotationOverlayRight);
            foreach (var host in _realizedContinuousHosts.Values)
            {
                if (host.Overlay != null)
                    ApplyPenAttributesToOverlay(host.Overlay);
            }
        }

        private void ApplyPenAttributesToOverlay(AnnotationOverlay overlay)
        {
            var attrs = new InkDrawingAttributes
            {
                Color = _settings.PenColor,
                Size = new Size(_settings.PenSize, _settings.PenSize * (_settings.PenIsHighlighter ? 3 : 1)),
                IgnorePressure = false,
                FitToCurve = true,
                DrawAsHighlighter = _settings.PenIsHighlighter
            };
            overlay.SetDrawingAttributes(attrs);
        }

        private void AdjustTextSize(int delta)
        {
            _textFontSize = Math.Max(10, Math.Min(48, _textFontSize + delta));
            ApplyTextDefaultsToOverlays();
            ApplyStyleToSelectedText(fontSize: _textFontSize);
        }

        private void ApplyStyleToSelectedText(double? fontSize = null, bool? bold = null, bool? italic = null, Windows.UI.Color? color = null)
        {
            foreach (var overlay in EnumerateOverlays())
            {
                if (overlay.SelectedText != null)
                    overlay.ApplyStyleToSelectedText(fontSize, bold, italic, color);
            }
        }

        private async Task PrintPdf()
        {
            if (_pdfDocument == null) return;
            _printHelper?.Unregister();
            _printHelper = null;
            try
            {
                _printHelper = new PrintHelper(this, _pdfDocument, _annotations);
                await _printHelper.ShowPrintUIAsync();
            }
            catch (Exception ex)
            {
                StatusMessageText.Text = Loc.Format("status.printError", ex.Message);
            }
        }

        private async Task SavePdfWithAnnotations()
        {
            if (_pdfDocument == null || _currentFile == null)
            {
                StatusMessageText.Text = Loc.Get("status.noPdf");
                return;
            }

            try
            {
                var savePicker = new FileSavePicker();
                InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));
                savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                savePicker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentFile.Name) + "_annotated";
                var file = await savePicker.PickSaveFileAsync();
                if (file == null) return;

                ShowLoading(Loc.Get("loading.saving"));
                var pageSizes = new Dictionary<uint, Size>();
                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                    pageSizes[i] = GetPageSize(i);
                await AnnotatedPdfExporter.ExportAsync(_currentFile, file, _annotations, pageSizes);

                var dialog = new ContentDialog
                {
                    Title = Loc.Get("dialog.saved.title"),
                    Content = Loc.Format("dialog.saved.content", file.Name),
                    CloseButtonText = Loc.Get("dialog.ok"),
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = Loc.Get("dialog.saveError.title"),
                    Content = ex.Message,
                    CloseButtonText = Loc.Get("dialog.ok"),
                    XamlRoot = Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                HideLoading();
            }
        }

        private async Task ExportSettingsAsync()
        {
            try
            {
                var picker = new FileSavePicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("JSON", new[] { ".json" });
                picker.SuggestedFileName = "micapdf-settings.json";
                var file = await picker.PickSaveFileAsync();
                if (file == null)
                    return;

                _settings.ExportTo(file.Path);
                AppSettingsPanel.SetStatus(Loc.Get("settings.export.success"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                AppSettingsPanel.SetStatus(Loc.Format("settings.export.error", ex.Message), InfoBarSeverity.Error);
            }
        }

        private async Task ImportSettingsAsync()
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                picker.FileTypeFilter.Add(".json");
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                var dialog = new ContentDialog
                {
                    Title = Loc.Get("settings.import.confirmTitle"),
                    Content = Loc.Get("settings.import.confirmContent"),
                    PrimaryButtonText = Loc.Get("settings.import.confirm"),
                    CloseButtonText = Loc.Get("settings.import.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                var imported = AppSettings.ImportFrom(file.Path);
                CopySettings(imported, _settings);
                _settings.Save();
                ApplySettingsToUi();
                AppSettingsPanel.LoadSettings(_settings);
                ShowSettingsContent();
                AppSettingsPanel.SetStatus(Loc.Get("settings.import.success"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                AppSettingsPanel.SetStatus(
                    ex is InvalidOperationException ? ex.Message : Loc.Get("settings.import.error"),
                    InfoBarSeverity.Error);
            }
        }

        private static void CopySettings(AppSettings source, AppSettings target)
        {
            target.Theme = source.Theme;
            target.Language = source.Language;
            target.MenuPosition = source.MenuPosition;
            target.FloatingBarPosition = source.FloatingBarPosition;
            target.AutoUpdate = source.AutoUpdate;
            target.WheelZoomRequiresCtrl = source.WheelZoomRequiresCtrl;
            target.MaxZoomPercent = source.MaxZoomPercent;
            target.ConfirmClearAnnotations = source.ConfirmClearAnnotations;
            target.GitHubRepository = source.GitHubRepository;
            target.PenSize = source.PenSize;
            target.PenIsHighlighter = source.PenIsHighlighter;
            target.PenColor = source.PenColor;
            target.PenBlackColor = source.PenBlackColor;
            target.PenRedColor = source.PenRedColor;
            target.PenGreenColor = source.PenGreenColor;
            target.HighlighterColor = source.HighlighterColor;
            target.ActivePenSlot = source.ActivePenSlot;
            target.NavPaneIsOpen = source.NavPaneIsOpen;
            target.OutlinePaneIsOpen = source.OutlinePaneIsOpen;
            target.HasShownDefaultReaderPrompt = source.HasShownDefaultReaderPrompt;
            target.HiddenMenuTags.Clear();
            foreach (var tag in source.HiddenMenuTags)
                target.HiddenMenuTags.Add(tag);
            target.MenuOrder.Clear();
            target.MenuOrder.AddRange(source.MenuOrder);
        }

        private async Task CheckForUpdatesAsync(bool forcePrompt)
        {
            var result = await UpdateChecker.CheckAsync(_settings.GitHubRepository);
            if (!string.IsNullOrEmpty(result.Error))
            {
                if (forcePrompt)
                {
                    AppSettingsPanel.SetStatus(result.Error, InfoBarSeverity.Error);
                    var err = new ContentDialog
                    {
                        Title = Loc.Get("dialog.updateCheck.title"),
                        Content = result.Error,
                        CloseButtonText = Loc.Get("dialog.ok"),
                        XamlRoot = Content.XamlRoot
                    };
                    await err.ShowAsync();
                }
                return;
            }

            if (!result.IsUpdateAvailable)
            {
                if (forcePrompt)
                {
                    var ok = new ContentDialog
                    {
                        Title = Loc.Get("dialog.upToDate.title"),
                        Content = Loc.Format("dialog.upToDate.content", result.CurrentVersion),
                        CloseButtonText = Loc.Get("dialog.ok"),
                        XamlRoot = Content.XamlRoot
                    };
                    await ok.ShowAsync();
                }
                return;
            }

            var dialog = new ContentDialog
            {
                Title = Loc.Get("dialog.updateAvailable.title"),
                Content = Loc.Format("dialog.updateAvailable.content", result.LatestVersion, result.CurrentVersion),
                PrimaryButtonText = Loc.Get("dialog.updateAvailable.open"),
                CloseButtonText = Loc.Get("dialog.updateAvailable.later"),
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                await Launcher.LaunchUriAsync(new Uri(result.ReleaseUrl));
        }
    }

}
