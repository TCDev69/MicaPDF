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
        private readonly PdfPageCache _pageCache = new(48);
        private readonly AnnotationStore _annotations = new();
        private readonly AnnotationHistory _history = new();
        private readonly List<ContinuousPageHost> _continuousPages = new();
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
                RefreshAllOverlays();
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
            if (_pdfDocument == null || pageIndex >= _pdfDocument.PageCount)
                return new Size(1, 1);
            using var page = _pdfDocument.GetPage(pageIndex);
            return page.Size;
        }

        private void RefreshAllOverlays()
        {
            foreach (var overlay in EnumerateOverlays())
                overlay.Refresh();
        }

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
        }

        private void RefreshLocalizedUi()
        {
            SetNavContent(OpenFileItem, Loc.MenuTitle("open"));
            SetNavContent(RecentFilesItem, Loc.MenuTitle("recentfiles"));
            RecentFilesItemText.Text = Loc.MenuTitle("recentfiles");
            SetNavContent(PrintItem, Loc.MenuTitle("print"));
            SetNavContent(SaveItem, Loc.MenuTitle("savewithannotations"));
            SetNavContent(ZoomInItem, Loc.MenuTitle("zoomin"));
            SetNavContent(ZoomOutItem, Loc.MenuTitle("zoomout"));
            SetNavContent(ZoomResetItem, Loc.MenuTitle("zoomreset"));
            SetNavContent(ZoomFitItem, Loc.Get(_zoomFitMode == ZoomFitMode.Height ? "menu.zoomfit.height" : "menu.zoomfit.width"));
            SetNavContent(FindItem, Loc.Get("menu.find"));
            FindTextBox.PlaceholderText = Loc.Get("find.placeholder");
            SetNavContent(GoToPageItem, Loc.MenuTitle("gotopage"));
            SetNavContent(NextPageItem, Loc.MenuTitle("nextpage"));
            SetNavContent(PrevPageItem, Loc.MenuTitle("prevpage"));
            SetNavContent(ContinuousItem, Loc.MenuTitle("continuousmode"));
            SetNavContent(OutlineItem, Loc.MenuTitle("outline"));
            SetNavContent(EditItem, Loc.MenuTitle("edit"));
            SetNavContent(ClearInkItem, Loc.MenuTitle("clearink"));
            SetNavContent(DoublePageItem, Loc.MenuTitle("doublepagemode"));
            SetNavContent(CoverPageItem, Loc.MenuTitle("coverpagemode"));

            RefreshModeLabels();
            UpdatePageHeaderText();

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
        }

        private void UpdatePageHeaderText()
        {
            if (_pdfDocument == null)
            {
                PageHeaderTextBlock.Content = Loc.Format("nav.page", 1, 1);
                SyncStatusPageFromHeader();
                return;
            }

            SetPageHeaderForIndices(_currentPageIndex, null, showLeft: true, showRight: false);
            SyncStatusPageFromHeader();
        }

        private void SetPageHeaderForIndices(uint leftIndex, uint? rightIndex, bool showLeft, bool showRight)
        {
            var total = _pdfDocument?.PageCount ?? 0u;
            if (_pdfDocument == null || total == 0)
            {
                PageHeaderTextBlock.Content = Loc.Format("nav.page", 1, 1);
                SyncStatusPageFromHeader();
                return;
            }

            var useLabels = _pageLabels is { IsIdentity: false };

            if (rightIndex is uint right && showLeft && showRight && right < total)
            {
                var phys = $"{leftIndex + 1}-{right + 1}";
                if (useLabels)
                {
                    var labels = $"{_pageLabels!.GetLabel(leftIndex)}-{_pageLabels.GetLabel(right)}";
                    PageHeaderTextBlock.Content = Loc.Format("nav.pageLabeled", labels, phys, total);
                }
                else
                {
                    PageHeaderTextBlock.Content = Loc.Format("nav.page", phys, total);
                }
                SyncStatusPageFromHeader();
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
                PageHeaderTextBlock.Content = Loc.Format(
                    "nav.pageLabeled",
                    _pageLabels!.GetLabel(index),
                    physical,
                    total);
            }
            else
            {
                PageHeaderTextBlock.Content = Loc.Format("nav.page", physical, total);
            }
            SyncStatusPageFromHeader();
        }

        private static void SetNavContent(NavigationViewItem item, string text)
        {
            if (item.Content is TextBlock tb)
                tb.Text = text;
            else
                item.Content = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
        }

        private void ApplyMenuCustomization()
        {
            foreach (var kv in _menuItemsByTag)
            {
                kv.Value.Visibility = _settings.HiddenMenuTags.Contains(kv.Key)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            // Reorder known items while keeping separators/headers in place as much as possible.
            var tagged = NavView.MenuItems.OfType<NavigationViewItem>()
                .Where(i => i.Tag is string)
                .ToDictionary(i => (string)i.Tag!);

            var insertIndex = 0;
            foreach (var tag in _settings.MenuOrder)
            {
                if (!tagged.TryGetValue(tag, out var item)) continue;
                var current = NavView.MenuItems.IndexOf(item);
                if (current < 0) continue;
                while (insertIndex < NavView.MenuItems.Count &&
                       NavView.MenuItems[insertIndex] is not NavigationViewItem)
                {
                    insertIndex++;
                }

                if (current != insertIndex)
                {
                    NavView.MenuItems.RemoveAt(current);
                    if (insertIndex > current) insertIndex--;
                    NavView.MenuItems.Insert(Math.Min(insertIndex, NavView.MenuItems.Count), item);
                }

                insertIndex++;
            }

            if (_isDoublePageMode)
                CoverPageItem.Visibility = _settings.HiddenMenuTags.Contains("coverpagemode")
                    ? Visibility.Collapsed
                    : Visibility.Visible;
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
                UpdatePageHeaderText();
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
                    ShowFindBar();
                    e.Handled = true;
                }
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

            // Any other menu action leaves settings and returns to the document.
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

        private void ApplyThemeToWindow()
        {
            if (Content is FrameworkElement root)
                root.RequestedTheme = _settings.Theme;
            RootGrid.RequestedTheme = _settings.Theme;
            NavView.RequestedTheme = _settings.Theme;
            TitleBarHost.RequestedTheme = _settings.Theme;

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
                    TitleBarHost.ColumnDefinitions[0].Width = new GridLength(0);
                    break;
                case "Right":
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                    NavView.FlowDirection = FlowDirection.RightToLeft;
                    NavContentRoot.FlowDirection = FlowDirection.LeftToRight;
                    TitleBarHost.ColumnDefinitions[0].Width = new GridLength(0);
                    break;
                case "LeftCompact":
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
                    TitleBarHost.ColumnDefinitions[0].Width = new GridLength(48);
                    break;
                default:
                    NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                    TitleBarHost.ColumnDefinitions[0].Width = new GridLength(48);
                    break;
            }

            foreach (var item in NavView.MenuItems.OfType<FrameworkElement>())
                item.FlowDirection = FlowDirection.LeftToRight;
        }

        private void ShowSettingsContent()
        {
            ViewerPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Collapsed;
            SettingsHost.Visibility = Visibility.Visible;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            NavView.IsBackEnabled = true;
            AppSettingsPanel.LoadSettings(_settings);
        }

        private void ShowViewerContent()
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            ViewerPanel.Visibility = Visibility.Visible;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
            NavView.IsBackEnabled = false;
            if (_pdfDocument == null)
                WelcomePanel.Visibility = Visibility.Visible;
        }

        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
        {
            PersistCurrentSession();
            FlushRecentSave();
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

            // Leave the left 48px free for the NavigationView pane toggle.
            SetTitleBar(TitleBarDragElement);
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

            _recentFiles.UpdateSession(path, _currentPageIndex, _currentZoom);
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
                    cover = new BitmapImage(new Uri(coverPath)) { DecodePixelWidth = 1120 };
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
            try
            {
                var pdfBytes = await PdfPigServices.ReadBytesAsync(file);

                PdfDocument? loaded = null;
                StorageFile workingFile = file;
                try
                {
                    loaded = await PdfDocument.LoadFromFileAsync(file);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"PDF open requires password or failed: {ex.Message}");
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

                _pdfDocument = loaded;
                if (_pdfDocument == null)
                {
                    SetStatusMessage(Loc.Get("status.unableOpen"));
                    return;
                }

                _currentFile = file;
                if (recentEntry != null)
                {
                    _currentPageIndex = ViewerSession.ClampPageIndex(recentEntry.PageIndex, _pdfDocument.PageCount);
                    _currentZoom = ViewerSession.ClampZoom(recentEntry.Zoom);
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
                _pageCache.Clear();
                _annotations.Clear();
                _history.Clear();
                await AnnotationSidecarStore.TryLoadAsync(file.Path, _annotations);
                _pdfOutline = null;
                OutlineTreeView.RootNodes.Clear();
                RefreshOutlineEmptyState();
                _pageLabels = PdfPageLabels.LoadFromBytes(pdfBytes, (int)_pdfDocument.PageCount);

                var sizes = new Dictionary<uint, Size>();
                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                    sizes[i] = GetPageSize(i);

                var bytesCopy = pdfBytes;
                var sizesCopy = sizes;
                var pigResult = await Task.Run(() => PdfPigServices.LoadTextAndOutline(bytesCopy, sizesCopy));
                (_textIndex, _pdfOutline) = pigResult;

                PopulateOutlineTree();

                ApplyOutlinePaneState();

                BindAnnotationOverlays();
                SetToolMode(_currentTool);
                CloseFindBar();
                _rasterZoom = _currentZoom;

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

                _recentFiles.RecordOpened(file.Path, _currentPageIndex, _currentZoom);
                FlushRecentSave();
                RefreshRecentFilesUi();
                _ = EnsureCoverAndRefreshAsync();

                SetStatusMessage(Loc.Format("status.fileLoaded", file.Name));
                AppLog.Info($"Opened PDF: {file.Name} ({_pdfDocument.PageCount} pages)");
            }
            catch (Exception ex)
            {
                SetStatusMessage(Loc.Format("status.error", ex.Message));
                AppLog.Error("LoadPdfFile failed", ex);
            }
            finally
            {
                HideLoading();
            }
        }

        private int ZoomKey => (int)Math.Round(_currentZoom * 100);

        private async Task<BitmapImage> RenderPageBitmapAsync(uint pageIndex, bool showOverlayOnMiss = false)
        {
            if (_pdfDocument == null)
                throw new InvalidOperationException("No document");

            if (_pageCache.TryGet(pageIndex, ZoomKey, out var cached))
                return cached;

            if (showOverlayOnMiss)
                ShowLoading(Loc.Format("loading.rendering", pageIndex + 1));

            try
            {
                using var page = _pdfDocument.GetPage(pageIndex);
                var renderOptions = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, page.Size.Width * _currentZoom * 2),
                    DestinationHeight = (uint)Math.Max(1, page.Size.Height * _currentZoom * 2)
                };

                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, renderOptions);
                var bitmapImage = new BitmapImage();
                await bitmapImage.SetSourceAsync(stream);
                _pageCache.Set(pageIndex, ZoomKey, bitmapImage);
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
                    SetPageHeaderForIndices(
                        leftIndex,
                        rightIndex < _pdfDocument.PageCount ? rightIndex : null,
                        showLeft,
                        showRight && rightIndex < _pdfDocument.PageCount);
                }
                else
                {
                    PdfImage.Source = await RenderPageBitmapAsync(_currentPageIndex, showLoadingOnMiss);
                    ConfigureOverlay(PdfAnnotationOverlay, _currentPageIndex);
                    UpdatePageHeaderText();
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
                UpdatePageHeaderText();
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
                UpdatePageHeaderText();
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
            if (!_isContinuousMode || _currentPageIndex >= _continuousPages.Count) return;
            _continuousPages[(int)_currentPageIndex].Root.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0
            });
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
                UpdatePageHeaderText();
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
            foreach (var host in _continuousPages)
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
                ContinuousPageContainer.Visibility = Visibility.Collapsed;
                DoublePageItem.IsEnabled = true;
            }

            _isDoublePageMode = !_isDoublePageMode;
            if (_isDoublePageMode)
            {
                SinglePageContainer.Visibility = Visibility.Collapsed;
                DoublePageContainer.Visibility = Visibility.Visible;
                CoverPageItem.Visibility = _settings.HiddenMenuTags.Contains("coverpagemode")
                    ? Visibility.Collapsed
                    : Visibility.Visible;

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
                CoverPageItem.Visibility = Visibility.Collapsed;
            }

            RefreshModeLabels();
            await RenderCurrentPage();
        }

        private async Task ToggleCoverPageMode()
        {
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
                ContinuousPageContainer.Visibility = Visibility.Visible;
                CoverPageItem.Visibility = Visibility.Collapsed;
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
                ContinuousPageContainer.Visibility = Visibility.Collapsed;
                _continuousPages.Clear();
                ContinuousPageContainer.Children.Clear();
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
                ContinuousPageContainer.Children.Clear();
                _continuousPages.Clear();

                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                {
                    using var page = _pdfDocument.GetPage(i);
                    double width = Math.Max(1, page.Size.Width * _currentZoom * 2);
                    double height = Math.Max(1, page.Size.Height * _currentZoom * 2);

                    var image = new Image { Stretch = Stretch.Fill, Width = width, Height = height };
                    var root = new Grid
                    {
                        Width = width,
                        Height = height,
                        Margin = new Thickness(0, 0, 0, 16),
                        Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(20, 128, 128, 128))
                    };
                    root.Children.Add(image);

                    ContinuousPageContainer.Children.Add(root);
                    _continuousPages.Add(new ContinuousPageHost
                    {
                        Index = i,
                        Root = root,
                        Image = image,
                        Overlay = null,
                        DisplayHeight = height + 16,
                        PageWidthDip = page.Size.Width,
                        PageHeightDip = page.Size.Height
                    });
                }

                UpdatePageHeaderText();
                await RenderVisibleContinuousPagesAsync();
                ScrollToCurrentPage();
            }
            finally
            {
                HideLoading();
            }
        }

        private AnnotationOverlay EnsureContinuousOverlay(ContinuousPageHost host)
        {
            if (host.Overlay != null) return host.Overlay;

            var overlay = new AnnotationOverlay
            {
                Width = host.Root.Width,
                Height = host.Root.Height,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            overlay.Attach(_annotations, host.Index, host.PageWidthDip, host.PageHeightDip);
            overlay.SetHistory(_history);
            overlay.SetTextIndex(_textIndex);
            overlay.SetTool(_currentTool);
            overlay.SelectionChanged += OnAnnotationSelectionChanged;
            overlay.AnnotationsChanged += (_, _) => ScheduleAnnotationAutosave();
            ApplyPenAttributesToOverlay(overlay);
            overlay.DefaultFontSize = _textFontSize;
            overlay.DefaultBold = _textBold;
            overlay.DefaultItalic = _textItalic;
            overlay.DefaultTextColor = _textColor;
            host.Root.Children.Add(overlay);
            host.Overlay = overlay;
            return overlay;
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
                UpdateContinuousPageHeaderFromScroll();
            }
        }

        private void UpdateContinuousPageHeaderFromScroll()
        {
            if (_pdfDocument == null || _continuousPages.Count == 0) return;
            double y = 0;
            double center = PdfScrollViewer.VerticalOffset + PdfScrollViewer.ViewportHeight / 3;
            for (var i = 0; i < _continuousPages.Count; i++)
            {
                var next = y + _continuousPages[i].DisplayHeight;
                if (center >= y && center < next)
                {
                    _currentPageIndex = (uint)i;
                    UpdatePageHeaderText();
                    PersistCurrentSession();
                    break;
                }
                y = next;
            }
        }

        private async Task RenderVisibleContinuousPagesAsync()
        {
            if (_pdfDocument == null || _continuousPages.Count == 0 || _continuousRenderQueued) return;
            _continuousRenderQueued = true;
            try
            {
                await Task.Delay(16);
                double top = PdfScrollViewer.VerticalOffset - PdfScrollViewer.ViewportHeight;
                double bottom = PdfScrollViewer.VerticalOffset + PdfScrollViewer.ViewportHeight * 2;
                double y = 0;

                for (var i = 0; i < _continuousPages.Count; i++)
                {
                    var host = _continuousPages[i];
                    var pageTop = y;
                    var pageBottom = y + host.DisplayHeight;
                    var visible = pageBottom >= top && pageTop <= bottom;

                    if (visible)
                    {
                        var overlay = EnsureContinuousOverlay(host);
                        if (!host.Rendered)
                        {
                            host.Image.Source = await RenderPageBitmapAsync(host.Index);
                            overlay.Refresh();
                            host.Rendered = true;
                        }
                    }
                    else if (host.Rendered &&
                             (pageBottom < top - PdfScrollViewer.ViewportHeight * 2 ||
                              pageTop > bottom + PdfScrollViewer.ViewportHeight * 2))
                    {
                        host.Image.Source = null;
                        host.Rendered = false;
                        if (host.Overlay != null)
                        {
                            host.Root.Children.Remove(host.Overlay);
                            host.Overlay = null;
                        }
                    }

                    y = pageBottom;
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
            foreach (var host in _continuousPages)
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
            foreach (var host in _continuousPages)
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

        private async Task CheckForUpdatesAsync(bool forcePrompt)
        {
            var result = await UpdateChecker.CheckAsync(_settings.GitHubRepository);
            if (!string.IsNullOrEmpty(result.Error))
            {
                if (forcePrompt)
                {
                    AppSettingsPanel.SetStatus(result.Error);
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
