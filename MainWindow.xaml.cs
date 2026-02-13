using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WinRT.Interop;
using WinRT;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;

namespace MicaPDF
{
    public sealed partial class MainWindow : Window
    {
        private enum ToolMode
        {
            Select,
            Pen,
            Eraser
        }

        private PdfDocument? _pdfDocument;
        private uint _currentPageIndex = 0;
        private double _currentZoom = 0.5;
        private MicaController? _micaController;
        private SystemBackdropConfiguration? _configurationSource;
        private StorageFile? _currentFile;
        private ToolMode _currentTool = ToolMode.Select;
        private bool _isEraserActive = false;
        private bool _isDoublePageMode = false;
        private bool _isCoverPageMode = false;
        private bool _isContinuousMode = false;
        private Polyline? _currentStroke;
        private bool _isDrawing = false;

        public MainWindow()
        {
            this.InitializeComponent();
            
            // Set window title
            this.Title = "MicaPDF";
            
            // Enable translucent Mica background
            SetupMicaBackground();
            
            // Configure titlebar with Mica style
            SetupCustomTitleBar();
            
            // Navigation handling
            NavView.ItemInvoked += NavView_ItemInvoked;
            
            // Handle arrow keys for navigation
            if (this.Content is UIElement rootContent)
            {
                rootContent.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Window_KeyDown), true);
            }

            // Center the window
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                
                // Set window icon
                try
                {
                    appWindow.SetIcon("MicaPDF.ico");
                }
                catch
                {
                    // Ignore if icon not found
                }
            }
            
            // Restore side menu state (with try-catch for safety)
            try
            {
                LoadNavigationPaneState();
                NavView.PaneClosing += (s, e) => SaveNavigationPaneState(false);
                NavView.PaneOpening += (s, e) => SaveNavigationPaneState(true);
            }
            catch
            {
                // Ignore state loading errors
            }

            // Load file from command line if specified
            _ = LoadFileFromCommandLine();
        }

        private async System.Threading.Tasks.Task LoadFileFromCommandLine()
        {
            // Wait a bit for the window to be fully loaded
            await System.Threading.Tasks.Task.Delay(100);

            if (!string.IsNullOrEmpty(App.FileToOpen))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(App.FileToOpen);
                    if (file != null)
                    {
                        LoadingProgressBar.Visibility = Visibility.Visible;
                        await LoadPdfFile(file);
                        LoadingProgressBar.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = $"Error opening file: {ex.Message}";
                }
            }

            // Check if MicaPDF is the default PDF reader
            await CheckDefaultPdfReader();
        }

        private async System.Threading.Tasks.Task CheckDefaultPdfReader()
        {
            try
            {
                // Check if this is the first launch
                var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                bool hasShownPrompt = false;
                
                if (localSettings.Values.ContainsKey("HasShownDefaultReaderPrompt"))
                {
                    hasShownPrompt = (bool)localSettings.Values["HasShownDefaultReaderPrompt"];
                }

                // Only show the prompt if it hasn't been shown before
                if (hasShownPrompt)
                {
                    return;
                }

                // Wait a bit more to ensure window is visible
                await System.Threading.Tasks.Task.Delay(500);

                // Check if this app is set as default PDF reader
                bool isDefault = await IsDefaultPdfReader();

                if (!isDefault)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Set as Default PDF Reader",
                        Content = "MicaPDF is not your default PDF reader.\n\nWould you like to set it as default?",
                        PrimaryButtonText = "Yes, Open Settings",
                        CloseButtonText = "Not Now",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        OpenDefaultAppsSettings();
                    }
                }

                // Mark that we've shown the prompt
                localSettings.Values["HasShownDefaultReaderPrompt"] = true;
            }
            catch
            {
                // Ignore errors in checking default app
            }
        }

        private async Task<bool> IsDefaultPdfReader()
        {
            try
            {
                // Try to get the default app for .pdf extension
                var launcher = await Windows.System.Launcher.QueryUriSupportAsync(
                    new Uri("ms-settings:defaultapps"),
                    Windows.System.LaunchQuerySupportType.Uri);

                // For now, we can't reliably detect if we're the default
                // So we'll show the prompt once per session
                // You could add a setting to remember user's choice
                return false;
            }
            catch
            {
                return false;
            }
        }

        private async void OpenDefaultAppsSettings()
        {
            try
            {
                // Open Windows Settings to Default Apps > PDF
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri("ms-settings:defaultapps"));

                // Show instruction dialog
                var instructionDialog = new ContentDialog
                {
                    Title = "Set MicaPDF as Default",
                    Content = "In the Settings window that just opened:\n\n" +
                             "1. Scroll down to 'Default apps'\n" +
                             "2. Search for '.pdf' or 'PDF'\n" +
                             "3. Click on the current default app\n" +
                             "4. Select 'MicaPDF' from the list",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await instructionDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error opening settings: {ex.Message}";
            }
        }

        private void LoadNavigationPaneState()
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (localSettings.Values.TryGetValue("NavPaneIsOpen", out var isOpen))
            {
                NavView.IsPaneOpen = (bool)isOpen;
            }
        }

        private void SaveNavigationPaneState(bool isOpen)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            localSettings.Values["NavPaneIsOpen"] = isOpen;
        }

        private async void Window_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Don't navigate if the user is typing in a text input
            if (e.OriginalSource is TextBox || 
                e.OriginalSource is NumberBox || 
                e.OriginalSource is PasswordBox || 
                e.OriginalSource is RichEditBox)
            {
                return;
            }

            if (e.Key == VirtualKey.Left)
            {
                await PreviousPage();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Right)
            {
                await NextPage();
                e.Handled = true;
            }
        }

        private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            var tag = args.InvokedItemContainer?.Tag?.ToString();
            
            switch (tag)
            {
                case "open":
                    await OpenFileDialog();
                    break;
                case "viewer":
                    ShowViewer();
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
                case "gotopage":
                    await ShowGoToPageDialog();
                    break;
                case "selectmode":
                    SetToolMode(ToolMode.Select);
                    break;
                case "penmode":
                    SetToolMode(ToolMode.Pen);
                    break;
                case "eraser":
                    SetToolMode(ToolMode.Eraser);
                    break;
                case "clearink":
                    ClearInkAnnotations();
                    break;
                case "savewithannotations":
                    await SavePdfWithAnnotations();
                    break;
            }
        }

        private void ShowViewer()
        {
            ViewerPanel.Visibility = Visibility.Visible;
        }

        private void SetupCustomTitleBar()
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                // Extend content into titlebar
                var titleBar = appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                
                // Set titlebar colors to match Mica
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(30, 255, 255, 255);
            }
        }

        private void SetupMicaBackground()
        {
            if (MicaController.IsSupported())
            {
                _micaController = new MicaController();
                _micaController.Kind = MicaKind.BaseAlt; // Use BaseAlt for more translucent effect
                
                _configurationSource = new SystemBackdropConfiguration();
                this.Activated += OnWindowActivated;
                this.Closed += OnWindowClosed;
                
                // Get ICompositionSupportsSystemBackdrop interface
                var backdropTarget = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
                _micaController.AddSystemBackdropTarget(backdropTarget);
                _micaController.SetSystemBackdropConfiguration(_configurationSource);
            }
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_configurationSource != null)
            {
                _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_micaController != null)
            {
                _micaController.Dispose();
                _micaController = null;
            }
            
            if (_configurationSource != null)
            {
                _configurationSource = null;
            }
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenFileDialog();
        }

        private async System.Threading.Tasks.Task OpenFileDialog()
        {
            try
            {
                StatusTextBlock.Text = "Opening file picker...";
                
                var picker = new FileOpenPicker();
                var hWnd = WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                
                picker.FileTypeFilter.Add(".pdf");
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                
                StatusTextBlock.Text = "Select a PDF file...";
                var file = await picker.PickSingleFileAsync();
                
                if (file != null)
                {
                    // Show loading bar
                    LoadingProgressBar.Visibility = Visibility.Visible;
                    
                    await LoadPdfFile(file);
                    
                    // Hide loading bar
                    LoadingProgressBar.Visibility = Visibility.Collapsed;
                }
                else
                {
                    StatusTextBlock.Text = "No file selected";
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"File opening error: {ex.Message}";
            }
        }

        private async System.Threading.Tasks.Task LoadPdfFile(StorageFile file)
        {
            try
            {
                StatusTextBlock.Text = "Loading...";
                
                _pdfDocument = await PdfDocument.LoadFromFileAsync(file);
                
                if (_pdfDocument == null)
                {
                    StatusTextBlock.Text = "Error: unable to open PDF file";
                    return;
                }

                _currentFile = file;
                _currentPageIndex = 0;
                _currentZoom = 0.5;
                ZoomLevelTextBlock.Text = "50%";
                ZoomHeaderTextBlock.Content = "Zoom: 50%";
                FileNameTextBlock.Text = file.Name;
                TitleBarFileName.Text = file.Name;
                WelcomePanel.Visibility = Visibility.Collapsed;
                ViewerPanel.Visibility = Visibility.Visible;
                
                await RenderCurrentPage();
                
                UpdateNavigationButtons();
                StatusTextBlock.Text = $"File loaded: {file.Name}";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
            }
        }

        private async System.Threading.Tasks.Task RenderCurrentPage()
        {
            if (_pdfDocument == null) return;

            try
            {
                if (_isContinuousMode)
                {
                    // Continuous mode logic handled separately
                    DoublePageModeTextBlock.Text = "Enable Double Page"; // Reset double page text
                    return;
                }

                if (_isDoublePageMode)
                {
                    // Calculate left and right page indices based on mode
                    uint leftIndex, rightIndex;
                    bool showLeft = true, showRight = true;

                    if (_isCoverPageMode)
                    {
                        if (_currentPageIndex == 0)
                        {
                            // Cover page mode: Page 0 is alone on the right
                            leftIndex = 999999; // Invalid
                            showLeft = false;
                            rightIndex = 0;
                        }
                        else
                        {
                            // Even-Odd pairing: 1-2, 3-4
                            // Ensure we stick to the odd starting page for the spread
                            // If _currentPageIndex is even (2), take (1,2)
                            uint baseIndex = _currentPageIndex % 2 == 0 ? _currentPageIndex - 1 : _currentPageIndex;
                            leftIndex = baseIndex;
                            rightIndex = baseIndex + 1;
                        }
                    }
                    else
                    {
                        // Standard Odd-Even pairing: 0-1, 2-3
                        // Ensure we stick to even starting page
                        uint baseIndex = _currentPageIndex % 2 != 0 ? _currentPageIndex - 1 : _currentPageIndex;
                        leftIndex = baseIndex;
                        rightIndex = baseIndex + 1;
                    }

                    // Render Left page
                    if (showLeft && leftIndex < _pdfDocument.PageCount)
                    {
                        using (var page = _pdfDocument.GetPage(leftIndex))
                        {
                            var renderOptions = new PdfPageRenderOptions
                            {
                                DestinationWidth = (uint)(page.Size.Width * _currentZoom * 2),
                                DestinationHeight = (uint)(page.Size.Height * _currentZoom * 2)
                            };

                            using (var stream = new InMemoryRandomAccessStream())
                            {
                                await page.RenderToStreamAsync(stream, renderOptions);
                                var bitmapImage = new BitmapImage();
                                await bitmapImage.SetSourceAsync(stream);
                                PdfImageLeft.Source = bitmapImage;
                            }
                        }
                    }
                    else
                    {
                        PdfImageLeft.Source = null;
                    }
                    
                    // Render Right page
                    if (showRight && rightIndex < _pdfDocument.PageCount)
                    {
                        using (var page = _pdfDocument.GetPage(rightIndex))
                        {
                            var renderOptions = new PdfPageRenderOptions
                            {
                                DestinationWidth = (uint)(page.Size.Width * _currentZoom * 2),
                                DestinationHeight = (uint)(page.Size.Height * _currentZoom * 2)
                            };

                            using (var stream = new InMemoryRandomAccessStream())
                            {
                                await page.RenderToStreamAsync(stream, renderOptions);
                                var bitmapImage = new BitmapImage();
                                await bitmapImage.SetSourceAsync(stream);
                                PdfImageRight.Source = bitmapImage;
                            }
                        }
                    }
                    else
                    {
                        PdfImageRight.Source = null;
                    } 
                        
                    // Update Text Info
                    string text = "";
                    if (!showLeft) text = $"{rightIndex + 1}";
                    else if (rightIndex >= _pdfDocument.PageCount) text = $"{leftIndex + 1}";
                    else text = $"{leftIndex + 1}-{rightIndex + 1}";
                    
                    PageInfoTextBlock.Text = $"{text} / {_pdfDocument.PageCount}";
                    PageHeaderTextBlock.Content = $"Page {text} / {_pdfDocument.PageCount}";
                    
                    // Update current index to be the start of the visible spread so navigation works
                    _currentPageIndex = showLeft ? leftIndex : rightIndex;
                }
                else
                {
                    // Single page mode
                    using (var page = _pdfDocument.GetPage(_currentPageIndex))
                    {
                        var renderOptions = new PdfPageRenderOptions
                        {
                            DestinationWidth = (uint)(page.Size.Width * _currentZoom * 2), // 2x for high quality
                            DestinationHeight = (uint)(page.Size.Height * _currentZoom * 2)
                        };

                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            await page.RenderToStreamAsync(stream, renderOptions);
                            
                            var bitmapImage = new BitmapImage();
                            await bitmapImage.SetSourceAsync(stream);
                            PdfImage.Source = bitmapImage;
                        }
                    }

                    PageInfoTextBlock.Text = $"{_currentPageIndex + 1} / {_pdfDocument.PageCount}";
                    PageHeaderTextBlock.Content = $"Page {_currentPageIndex + 1} / {_pdfDocument.PageCount}";
                }
                
                UpdateNavigationButtons();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Rendering error: {ex.Message}";
            }
        }

        private void UpdateNavigationButtons()
        {
            // Buttons are now in side menu, no need to update them anymore
        }

        private async void PreviousPageButton_Click(object sender, RoutedEventArgs e)
        {
            await PreviousPage();
        }

        private async System.Threading.Tasks.Task PreviousPage()
        {
            if (_currentPageIndex > 0)
            {
                if (_isContinuousMode)
                {
                    _currentPageIndex--;
                    ScrollToCurrentPage();
                    return; 
                }

                if (_isDoublePageMode)
                {
                    // In double page mode, move back by 2 pages
                    if (_isCoverPageMode && _currentPageIndex == 0)
                    { 
                         // Already at start
                         return;
                    }

                    if (_isCoverPageMode)
                    {
                        // Current logic: 1-2 (index 1), 3-4 (index 3)
                        // If at 1, goes to 0
                        // If at 3, goes to 1
                        if (_currentPageIndex == 1) _currentPageIndex = 0;
                        else _currentPageIndex = _currentPageIndex >= 2 ? _currentPageIndex - 2 : 0;
                    }
                    else
                    {
                        // Standard: 0-1, 2-3
                         _currentPageIndex = _currentPageIndex >= 2 ? _currentPageIndex - 2 : 0;
                    }
                }
                else
                {
                    _currentPageIndex--;
                }
                await RenderCurrentPage();
            }
        }

        private async void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            await NextPage();
        }

        private async System.Threading.Tasks.Task NextPage()
        {
            if (_pdfDocument != null && _currentPageIndex < _pdfDocument.PageCount - 1)
            {
                if (_isContinuousMode)
                {
                    _currentPageIndex++;
                    ScrollToCurrentPage();
                    return;
                }

                if (_isDoublePageMode)
                {
                    // In double page mode, move forward by 2 pages
                    if (_isCoverPageMode)
                    {
                         // If at 0, go to 1 (pair 1-2)
                         // If at 1, go to 3 (pair 3-4)
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
            }
        }

        private async void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            await ZoomIn();
        }

        private async System.Threading.Tasks.Task ZoomIn()
        {
            double oldZoom = _currentZoom;
            _currentZoom = Math.Min(_currentZoom + 0.25, 5.0);
            
            if (oldZoom == _currentZoom) return;

            ZoomLevelTextBlock.Text = $"{(_currentZoom * 100):F0}%";
            ZoomHeaderTextBlock.Content = $"Zoom: {(_currentZoom * 100):F0}%";
            
            if (_isContinuousMode)
            {
                // Capture relative scroll position
                double relativeV = PdfScrollViewer.VerticalOffset / PdfScrollViewer.ExtentHeight;
                
                await RenderAllPages();
                
                // Restore relative scroll position
                // We need to wait for layout update, but a simple approximation is to set it after rendering
                PdfScrollViewer.ChangeView(null, relativeV * PdfScrollViewer.ExtentHeight, null, true);
            }
            else
            {
                await RenderCurrentPage();
            }
        }

        private async void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            await ZoomOut();
        }

        private async System.Threading.Tasks.Task ZoomOut()
        {
            double oldZoom = _currentZoom;
            _currentZoom = Math.Max(_currentZoom - 0.25, 0.5);

            if (oldZoom == _currentZoom) return;

            ZoomLevelTextBlock.Text = $"{(_currentZoom * 100):F0}%";
            ZoomHeaderTextBlock.Content = $"Zoom: {(_currentZoom * 100):F0}%";
            
            if (_isContinuousMode)
            {
                // Capture relative scroll position
                double relativeV = PdfScrollViewer.VerticalOffset / PdfScrollViewer.ExtentHeight;
                
                await RenderAllPages();

                // Restore relative scroll position
                PdfScrollViewer.ChangeView(null, relativeV * PdfScrollViewer.ExtentHeight, null, true);
            }
            else
            {
                await RenderCurrentPage();
            }
        }

        private async void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            await ZoomReset();
        }

        private async System.Threading.Tasks.Task ZoomReset()
        {
            double oldZoom = _currentZoom;
            _currentZoom = 0.5;

            if (oldZoom == _currentZoom) return;
            
            ZoomLevelTextBlock.Text = "50%";
            ZoomHeaderTextBlock.Content = "Zoom: 50%";
            
            if (_isContinuousMode)
            {
                // Capture relative scroll position
                double relativeV = PdfScrollViewer.VerticalOffset / PdfScrollViewer.ExtentHeight;
                
                await RenderAllPages();
                
                // Restore relative scroll position
                PdfScrollViewer.ChangeView(null, relativeV * PdfScrollViewer.ExtentHeight, null, true);
            }
            else
            {
                await RenderCurrentPage();
            }
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFile != null)
            {
                await LoadPdfFile(_currentFile);
            }
        }

        private void ViewerPanel_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            
            if (e.DragUIOverride != null)
            {
                e.DragUIOverride.Caption = "Rilascia per aprire il PDF";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
        }

        private async void ViewerPanel_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.FirstOrDefault() as StorageFile;
                
                if (file != null && file.FileType.ToLower() == ".pdf")
                {
                    // Show loading bar
                    LoadingProgressBar.Visibility = Visibility.Visible;
                    
                    await LoadPdfFile(file);
                    
                    // Hide loading bar
                    LoadingProgressBar.Visibility = Visibility.Collapsed;
                }
                else
                {
                    StatusTextBlock.Text = "File must be a PDF";
                }
            }
        }

        private async void GoToPageButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowGoToPageDialog();
        }

        private async void GoToPageItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await ShowGoToPageDialog();
        }

        private async System.Threading.Tasks.Task ShowGoToPageDialog()
        {
            if (_pdfDocument == null)
            {
                // Don't show dialog if no PDF is open
                return;
            }

            // Set maximum and current value
            PageNumberBox.Maximum = _pdfDocument.PageCount;
            PageNumberBox.Value = _currentPageIndex + 1;
            
            // Show dialog
            GoToPageDialog.XamlRoot = this.Content.XamlRoot;
            await GoToPageDialog.ShowAsync();
        }

        private async void GoToPageDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            await NavigateToPage();
        }

        private async void PageNumberBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                // Prevent dialog closing
                e.Handled = true;
                
                // Navigate to page
                await NavigateToPage();
                
                // Close dialog
                GoToPageDialog.Hide();
            }
        }

        private void ScrollToCurrentPage()
        {
            if (_isContinuousMode && (int)_currentPageIndex < ContinuousPageContainer.Children.Count)
            {
                var targetElement = ContinuousPageContainer.Children[(int)_currentPageIndex] as FrameworkElement;
                if (targetElement != null)
                {
                    targetElement.StartBringIntoView(new BringIntoViewOptions 
                    { 
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0 // Scroll to top of the element
                    });
                }
            }
        }

        private async System.Threading.Tasks.Task NavigateToPage()
        {
            if (_pdfDocument == null)
            {
                return;
            }

            double requestedValue = PageNumberBox.Value;

            if (double.TryParse(PageNumberBox.Text, out double parsedValue))
            {
                requestedValue = parsedValue;
                PageNumberBox.Value = parsedValue; // keep UI in sync
            }

            // Check for offset in filename (e.g. filename_+2.pdf)
            int offset = 0;
            if (_currentFile != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(_currentFile.Name, @"_\+(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int extractedOffset))
                {
                    offset = extractedOffset;
                }
            }

            double targetPage = requestedValue + offset;

            if (targetPage >= 1 && targetPage <= _pdfDocument.PageCount)
            {
                _currentPageIndex = (uint)(targetPage - 1);

                if (_isContinuousMode)
                {
                    ScrollToCurrentPage();
                }
                else
                {
                    await RenderCurrentPage();
                }

                UpdateNavigationButtons();
            }
        }

        private void SetToolMode(ToolMode mode)
        {
            _currentTool = mode;
            
            switch (mode)
            {
                case ToolMode.Select:
                    // Re-enable ScrollViewer manipulation
                    PdfScrollViewer.HorizontalScrollMode = ScrollMode.Enabled;
                    PdfScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
                    PdfScrollViewer.ZoomMode = ZoomMode.Enabled;
                    break;
                    
                case ToolMode.Pen:
                case ToolMode.Eraser:
                    // Disable ScrollViewer manipulation when in drawing/erasing mode
                    PdfScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
                    PdfScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                    PdfScrollViewer.ZoomMode = ZoomMode.Disabled;
                    break;
            }
        }

        private void ClearInkAnnotations()
        {
            PdfInkCanvas.Children.Clear();
            if (_isDoublePageMode)
            {
                PdfInkCanvasLeft.Children.Clear();
                PdfInkCanvasRight.Children.Clear();
            }
        }

        private async Task ToggleDoublePageMode()
        {
            _isDoublePageMode = !_isDoublePageMode;
            
            if (_isDoublePageMode)
            {
                // Switch to double page view
                SinglePageContainer.Visibility = Visibility.Collapsed;
                DoublePageContainer.Visibility = Visibility.Visible;
                DoublePageModeTextBlock.Text = "Disable Double Page";
                CoverPageItem.Visibility = Visibility.Visible;

                // Adjust page index for the new mode
                if (_isCoverPageMode)
                {
                    if (_currentPageIndex > 0 && _currentPageIndex % 2 == 0) _currentPageIndex--;
                }
                else
                {
                    if (_currentPageIndex % 2 != 0) _currentPageIndex--;
                }
            }
            else
            {
                // Switch back to single page view
                SinglePageContainer.Visibility = Visibility.Visible;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                DoublePageModeTextBlock.Text = "Enable Double Page";
                CoverPageItem.Visibility = Visibility.Collapsed;
            }
            
            await RenderCurrentPage();
        }

        private async Task ToggleCoverPageMode()
        {
            _isCoverPageMode = !_isCoverPageMode;
            CoverPageModeTextBlock.Text = _isCoverPageMode ? "Cover Page: On" : "Cover Page: Off";
            
            // Re-align page index
            if (_isDoublePageMode)
            {
                 if (_isCoverPageMode)
                {
                    // If switching TO cover mode
                    // Even pages (0, 2, 4) should become the Right page of previous spread or single cover
                    // 0 -> 0 (Cover)
                    // 2 -> 1-2
                    if (_currentPageIndex > 0 && _currentPageIndex % 2 == 0)
                         _currentPageIndex--;
                }
                else
                {
                    // If switching FROM cover mode
                    // Odd pages (1, 3, 5) should align to even start (0-1, 2-3)
                    if (_currentPageIndex % 2 != 0)
                        _currentPageIndex--;
                }
            }

            await RenderCurrentPage();
        }

        private async Task ToggleContinuousMode()
        {
            _isContinuousMode = !_isContinuousMode;
            
            if (_isContinuousMode)
            {
                // Disable double page mode if active
                _isDoublePageMode = false;
                
                // Force zoom to 100% for continuous mode
                _currentZoom = 1.0;
                ZoomLevelTextBlock.Text = "100%";
                ZoomHeaderTextBlock.Content = "Zoom: 100%";
                
                // Disable zoom controls and Double Page button
                ZoomInItem.IsEnabled = false;
                ZoomOutItem.IsEnabled = false;
                ZoomResetItem.IsEnabled = false;
                DoublePageItem.IsEnabled = false;

                // Switch to continuous view
                SinglePageContainer.Visibility = Visibility.Collapsed;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                ContinuousPageContainer.Visibility = Visibility.Visible;
                ContinuousModeTextBlock.Text = "Disable Continuous Scroll";
                DoublePageModeTextBlock.Text = "Enable Double Page";

                // Hide page navigation controls since we show all pages
                PageHeaderTextBlock.Content = $"Total Pages: {_pdfDocument?.PageCount ?? 0}";
                
                await RenderAllPages();
            }
            else
            {
                // Re-enable zoom controls and Double Page button
                ZoomInItem.IsEnabled = true;
                ZoomOutItem.IsEnabled = true;
                ZoomResetItem.IsEnabled = true;
                DoublePageItem.IsEnabled = true;

                // Switch back to single page view
                SinglePageContainer.Visibility = Visibility.Visible;
                DoublePageContainer.Visibility = Visibility.Collapsed;
                ContinuousPageContainer.Visibility = Visibility.Collapsed;
                ContinuousModeTextBlock.Text = "Enable Continuous Scroll";
                
                // Restore page view
                await RenderCurrentPage();
            }
        }

        private async Task RenderAllPages()
        {
            if (_pdfDocument == null) return;
            
            ContinuousPageContainer.Children.Clear();
            
            // Show loading indicator
            StatusTextBlock.Text = "Rendering all pages...";
            LoadingProgressBar.Visibility = Visibility.Visible;

            try 
            {
                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                {
                    using (var page = _pdfDocument.GetPage(i))
                    {
                        var renderOptions = new PdfPageRenderOptions
                        {
                            DestinationWidth = (uint)(page.Size.Width * _currentZoom * 2),
                            DestinationHeight = (uint)(page.Size.Height * _currentZoom * 2)
                        };

                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            await page.RenderToStreamAsync(stream, renderOptions);
                            
                            var bitmapImage = new BitmapImage();
                            await bitmapImage.SetSourceAsync(stream);
                            
                            var image = new Image
                            {
                                Source = bitmapImage,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Thickness(0, 0, 0, 16)
                            };
                            
                            ContinuousPageContainer.Children.Add(image);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error rendering pages: {ex.Message}";
            }
            finally
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = $"Loaded {_pdfDocument.PageCount} pages";
            }
        }

        private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Canvas currentCanvas) return;

            if (_currentTool == ToolMode.Pen)
            {
                _isDrawing = true;
                _currentStroke = new Polyline
                {
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };

                var point = e.GetCurrentPoint(currentCanvas).Position;
                _currentStroke.Points.Add(point);
                currentCanvas.Children.Add(_currentStroke);
                
                currentCanvas.CapturePointer(e.Pointer);
            }
            else if (_currentTool == ToolMode.Eraser)
            {
                // Start erasing when pointer is pressed
                _isEraserActive = true;
                var point = e.GetCurrentPoint(currentCanvas).Position;
                EraseStrokesAtPoint(point, currentCanvas);
                currentCanvas.CapturePointer(e.Pointer);
            }
        }

        private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Canvas currentCanvas) return;

            if (_currentTool == ToolMode.Pen && _isDrawing && _currentStroke != null)
            {
                var point = e.GetCurrentPoint(currentCanvas).Position;
                _currentStroke.Points.Add(point);
            }
            else if (_currentTool == ToolMode.Eraser && _isEraserActive)
            {
                // Continue erasing only while pointer is pressed
                var point = e.GetCurrentPoint(currentCanvas).Position;
                EraseStrokesAtPoint(point, currentCanvas);
            }
        }

        private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Canvas currentCanvas) return;

            if (_currentTool == ToolMode.Pen)
            {
                _isDrawing = false;
                _currentStroke = null;
                currentCanvas.ReleasePointerCapture(e.Pointer);
            }
            else if (_currentTool == ToolMode.Eraser)
            {
                _isEraserActive = false;
                currentCanvas.ReleasePointerCapture(e.Pointer);
            }
        }

        private void EraseStrokesAtPoint(Windows.Foundation.Point point, Canvas targetCanvas)
        {
            const double eraserRadius = 20; // Size of eraser
            var strokesToRemove = new List<UIElement>();

            foreach (var child in targetCanvas.Children)
            {
                if (child is Polyline polyline)
                {
                    // Check if any point in the polyline is within eraser radius
                    foreach (var strokePoint in polyline.Points)
                    {
                        double distance = Math.Sqrt(
                            Math.Pow(strokePoint.X - point.X, 2) + 
                            Math.Pow(strokePoint.Y - point.Y, 2)
                        );

                        if (distance <= eraserRadius)
                        {
                            strokesToRemove.Add(polyline);
                            break; // Remove entire stroke if any point is hit
                        }
                    }
                }
            }

            // Remove all strokes that were hit
            foreach (var stroke in strokesToRemove)
            {
                targetCanvas.Children.Remove(stroke);
            }
        }

        private async Task SavePdfWithAnnotations()
        {
            if (_pdfDocument == null || _currentFile == null)
            {
                StatusTextBlock.Text = "No PDF opened";
                return;
            }

            try
            {
                // Create file picker for saving
                var savePicker = new FileSavePicker();
                var hWnd = WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

                savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("PNG Image", new List<string>() { ".png" });
                savePicker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentFile.Name) + $"_{_currentPageIndex + 1}";

                var file = await savePicker.PickSaveFileAsync();
                if (file == null)
                {
                    StatusTextBlock.Text = "Save cancelled";
                    return;
                }

                StatusTextBlock.Text = "Saving annotated PDF...";
                LoadingProgressBar.Visibility = Visibility.Visible;

                // Render the current view (PDF + annotations) to an image
                var renderTargetBitmap = new RenderTargetBitmap();
                await renderTargetBitmap.RenderAsync(PdfContainer);

                // Get pixel data
                var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
                var pixels = pixelBuffer.ToArray();

                // Create a new file with the rendered image
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)renderTargetBitmap.PixelWidth,
                        (uint)renderTargetBitmap.PixelHeight,
                        96, 96,
                        pixels);

                    await encoder.FlushAsync();
                }

                LoadingProgressBar.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = $"Saved as PNG: {file.Name}";
                
                // Show success message
                var dialog = new ContentDialog
                {
                    Title = "Saved Successfully",
                    Content = $"The annotated PDF page has been saved as a PNG image:\n{file.Name}\n\nNote: The file is saved as an image to preserve annotations.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = $"Error saving: {ex.Message}";
                
                var errorDialog = new ContentDialog
                {
                    Title = "Save Error",
                    Content = $"Failed to save the file:\n{ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }
    }

    // Converter for null-based visibility
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
