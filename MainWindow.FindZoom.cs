using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MicaPDF
{
    public sealed partial class MainWindow
    {
        private ZoomFitMode _zoomFitMode = ZoomFitMode.Height;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomSettleTimer;
        private bool _zoomSettling;
        private string? _decryptedTempPath;
        private List<TextSearchHit> _findHits = new();
        private int _findIndex = -1;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _annotationSaveTimer;
        /// <summary>Last zoom level that was rasterized (reload when |displayed − this| ≥ 25%).</summary>
        private double _rasterZoom = 0.5;

        private void UpdateZoomUi(double absoluteZoom)
        {
            StatusZoomText.Text = ViewerSession.FormatZoomPercent(absoluteZoom);
        }

        private void SetStatusMessage(string message)
        {
            StatusMessageText.Text = message;
            if (!string.IsNullOrEmpty(message))
                AppLog.Info(message);
        }

        private double GetDisplayedZoom() =>
            _currentZoom * Math.Max(0.01f, PdfScrollViewer.ZoomFactor);

        private double GetDisplayMultiplier(double pageDip, double zoom, double actualSize)
        {
            if (pageDip > 1 && zoom > 0.01 && actualSize > 1)
                return actualSize / (pageDip * zoom);
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale < 0.5) scale = 1.0;
            return 2.0 * scale;
        }

        private double GetMaxZoom() => ZoomLimits.MaxZoomFromPercent(_settings.MaxZoomPercent);

        private void UpdateScrollViewerZoomLimits()
        {
            var maxZoom = GetMaxZoom();
            var headroom = maxZoom / Math.Max(0.25, _currentZoom);
            PdfScrollViewer.MaxZoomFactor = (float)Math.Clamp(headroom, 1.5, 3.0);
        }

        private void ApplyInteractiveZoom(double targetAbsoluteZoom)
        {
            if (_pdfDocument == null) return;
            var maxZoom = GetMaxZoom();
            targetAbsoluteZoom = Math.Clamp(targetAbsoluteZoom, 0.25, maxZoom);
            var baseZoom = Math.Max(0.01, _currentZoom);
            var oldFactor = Math.Max(0.01f, PdfScrollViewer.ZoomFactor);
            var newFactor = (float)(targetAbsoluteZoom / baseZoom);
            UpdateScrollViewerZoomLimits();
            newFactor = Math.Clamp(newFactor, PdfScrollViewer.MinZoomFactor, PdfScrollViewer.MaxZoomFactor);

            var projected = baseZoom * newFactor;
            if (Math.Abs(projected - targetAbsoluteZoom) > 0.05 &&
                (newFactor >= PdfScrollViewer.MaxZoomFactor - 0.01 || newFactor <= PdfScrollViewer.MinZoomFactor + 0.01))
            {
                _ = SettleThenZoomAsync(targetAbsoluteZoom);
                return;
            }

            var (h, v) = ZoomOffsetsPreservingCenter(oldFactor, newFactor);
            PdfScrollViewer.ChangeView(h, v, newFactor, false);
            UpdateZoomUi(targetAbsoluteZoom);
            ScheduleZoomSettle();
        }

        /// <summary>Keep the viewport-center content point stable when ZoomFactor changes.</summary>
        private (double h, double v) ZoomOffsetsPreservingCenter(float oldFactor, float newFactor)
        {
            oldFactor = Math.Max(0.01f, oldFactor);
            newFactor = Math.Max(0.01f, newFactor);
            var vw = PdfScrollViewer.ViewportWidth;
            var vh = PdfScrollViewer.ViewportHeight;
            var cx = PdfScrollViewer.HorizontalOffset + vw / 2;
            var cy = PdfScrollViewer.VerticalOffset + vh / 2;
            var scale = newFactor / oldFactor;
            return (Math.Max(0, cx * scale - vw / 2), Math.Max(0, cy * scale - vh / 2));
        }

        private async Task SettleThenZoomAsync(double targetAbsoluteZoom)
        {
            await SettleZoomAsync();
            ApplyInteractiveZoom(targetAbsoluteZoom);
        }

        private void ScheduleZoomSettle()
        {
            _zoomSettleTimer ??= DispatcherQueue.CreateTimer();
            _zoomSettleTimer.Interval = TimeSpan.FromMilliseconds(200);
            _zoomSettleTimer.Tick -= ZoomSettleTimer_Tick;
            _zoomSettleTimer.Tick += ZoomSettleTimer_Tick;
            _zoomSettleTimer.Stop();
            _zoomSettleTimer.Start();
        }

        private async void ZoomSettleTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            await SettleZoomAsync();
        }

        private async Task SettleZoomAsync()
        {
            if (_zoomSettling || _pdfDocument == null) return;
            _zoomSettling = true;
            try
            {
                var factor = PdfScrollViewer.ZoomFactor;
                var maxZoom = GetMaxZoom();
                var displayed = Math.Clamp(_currentZoom * factor, 0.25, maxZoom);
                var delta = Math.Abs(displayed - _rasterZoom);
                var needsReload = delta >= ZoomFitCalculator.ZoomStep;

                var oldRaster = _rasterZoom;
                var vw = PdfScrollViewer.ViewportWidth;
                var vh = PdfScrollViewer.ViewportHeight;
                // Content coords under viewport center (strip current ZoomFactor).
                var centerX = (PdfScrollViewer.HorizontalOffset + vw / 2) / Math.Max(0.01f, factor);
                var centerY = (PdfScrollViewer.VerticalOffset + vh / 2) / Math.Max(0.01f, factor);

                if (!needsReload)
                {
                    UpdateZoomUi(displayed);
                    return;
                }

                _currentZoom = displayed;
                _rasterZoom = displayed;
                UpdateScrollViewerZoomLimits();
                var sizeRatio = displayed / Math.Max(0.01, oldRaster);
                var newH = Math.Max(0, centerX * sizeRatio - vw / 2);
                var newV = Math.Max(0, centerY * sizeRatio - vh / 2);

                UpdateZoomUi(_currentZoom);
                _pageCache.TrimDistantZoom(ZoomKey, PdfPageCache.DefaultTrimDistance);

                // Warm cache while the old view stays stable (no factor change yet).
                await PrefetchCurrentPageBitmapsAsync();

                // Hide during swap so the intermediate factor→1 on the old bitmap is not visible.
                PdfContainer.Opacity = 0;
                try
                {
                    PdfScrollViewer.ChangeView(newH, newV, 1f, true);
                    _lastScrollZoom = 1f;
                    await RefreshAfterZoomAsync(showLoading: false);
                    PdfScrollViewer.UpdateLayout();
                    PdfScrollViewer.ChangeView(newH, newV, 1f, true);
                }
                finally
                {
                    PdfContainer.Opacity = 1;
                }

                PersistCurrentSession();
            }
            catch (Exception ex)
            {
                AppLog.Error("Zoom settle failed", ex);
                try { PdfContainer.Opacity = 1; } catch { /* ignore */ }
            }
            finally
            {
                _zoomSettling = false;
            }
        }

        private async Task PrefetchCurrentPageBitmapsAsync()
        {
            if (_pdfDocument == null || _isContinuousMode) return;

            var pages = new List<uint>();
            if (_isDoublePageMode)
            {
                uint leftIndex, rightIndex;
                bool showLeft = true, showRight = true;
                if (_isCoverPageMode && _currentPageIndex == 0)
                {
                    leftIndex = 0;
                    showLeft = false;
                    rightIndex = 0;
                }
                else if (_isCoverPageMode)
                {
                    var baseIndex = _currentPageIndex % 2 == 0 ? _currentPageIndex - 1 : _currentPageIndex;
                    leftIndex = baseIndex;
                    rightIndex = baseIndex + 1;
                }
                else
                {
                    var baseIndex = _currentPageIndex % 2 != 0 ? _currentPageIndex - 1 : _currentPageIndex;
                    leftIndex = baseIndex;
                    rightIndex = baseIndex + 1;
                }

                if (showLeft && leftIndex < _pdfDocument.PageCount)
                    pages.Add(leftIndex);
                if (showRight && rightIndex < _pdfDocument.PageCount)
                    pages.Add(rightIndex);
            }
            else
            {
                pages.Add(_currentPageIndex);
            }

            await PrefetchPageBitmapsAsync(pages);
        }

        private async Task ZoomIn()
        {
            ApplyInteractiveZoom(GetDisplayedZoom() + 0.25);
            await Task.CompletedTask;
        }

        private async Task ZoomOut()
        {
            ApplyInteractiveZoom(GetDisplayedZoom() - 0.25);
            await Task.CompletedTask;
        }

        private async Task ZoomReset()
        {
            ApplyInteractiveZoom(0.5);
            await Task.CompletedTask;
        }

        private async Task ZoomFit()
        {
            if (_pdfDocument == null || _pdfDocument.PageCount == 0) return;
            if (_currentPageIndex >= _pdfDocument.PageCount) _currentPageIndex = 0;

            var mode = _zoomFitMode;
            _zoomFitMode = ZoomFitCalculator.NextMode(_zoomFitMode);

            using var page = _pdfDocument.GetPage(_currentPageIndex);
            double pageWidth = page.Size.Width;
            double pageHeight = page.Size.Height;
            double viewportWidth = PdfScrollViewer.ViewportWidth > 0 ? PdfScrollViewer.ViewportWidth : PdfScrollViewer.ActualWidth;
            double viewportHeight = PdfScrollViewer.ViewportHeight > 0 ? PdfScrollViewer.ViewportHeight : PdfScrollViewer.ActualHeight;
            if (viewportWidth == 0 || viewportHeight == 0) return;

            // Small inset only; status/title already outside the ScrollViewer.
            double availableWidth = Math.Max(100, viewportWidth - 16);
            double availableHeight = Math.Max(100, viewportHeight - 16);

            double imgW = PdfImage.ActualWidth > 1 ? PdfImage.ActualWidth : pageWidth * _currentZoom * 2;
            double imgH = PdfImage.ActualHeight > 1 ? PdfImage.ActualHeight : pageHeight * _currentZoom * 2;
            var mul = GetDisplayMultiplier(pageHeight, _currentZoom, imgH);
            if (_isDoublePageMode)
                mul = GetDisplayMultiplier(pageWidth, _currentZoom, imgW / 2);

            var zoom = ZoomFitCalculator.Compute(
                pageWidth, pageHeight, availableWidth, availableHeight,
                mode, mul, _isDoublePageMode, _settings.MaxZoomPercent);

            RefreshZoomFitLabel();

            // Apply directly — avoid ChangeView race that left the wrong zoom.
            _currentZoom = zoom;
            _rasterZoom = zoom;
            PdfScrollViewer.ChangeView(null, null, 1f, true);
            _lastScrollZoom = 1f;
            UpdateZoomUi(_currentZoom);
            _pageCache.TrimDistantZoom(ZoomKey, PdfPageCache.DefaultTrimDistance);
            await RefreshAfterZoomAsync(showLoading: false);

            PersistCurrentSession();
        }

        private void RefreshZoomFitLabel()
        {
            var key = _zoomFitMode == ZoomFitMode.Height ? "menu.zoomfit.height" : "menu.zoomfit.width";
            SetNavContent(ZoomFitItem, Loc.Get(key));
        }

        private async Task RefreshAfterZoomAsync(bool showLoading = false)
        {
            if (_isContinuousMode)
            {
                if (_pdfDocument != null && _continuousPageItems.Count == (int)_pdfDocument.PageCount)
                {
                    ResizeContinuousHosts();
                    await RenderVisibleContinuousPagesAsync();
                }
                else
                {
                    double relativeV = PdfScrollViewer.ExtentHeight > 0
                        ? PdfScrollViewer.VerticalOffset / PdfScrollViewer.ExtentHeight
                        : 0;
                    await BuildContinuousLayoutAsync();
                    PdfScrollViewer.ChangeView(null, relativeV * PdfScrollViewer.ExtentHeight, null, true);
                }
            }
            else
            {
                await RenderCurrentPage(showLoadingOnMiss: showLoading);
            }
        }

        private void ResizeContinuousHosts()
        {
            if (_pdfDocument == null) return;

            double top = PdfScrollViewer.VerticalOffset - PdfScrollViewer.ViewportHeight;
            double bottom = PdfScrollViewer.VerticalOffset + PdfScrollViewer.ViewportHeight * 2;
            var updated = new List<ContinuousPageItem>();
            double y = 0;

            for (uint i = 0; i < _pdfDocument.PageCount; i++)
            {
                if (!_pageSizes.ContainsKey(i))
                    CachePageSize(i);

                var size = GetPageSize(i);
                var (displayW, displayH) = GetDisplayDimensions(i);
                var layoutHeight = displayH + 16;
                var pageTop = y;
                var pageBottom = y + layoutHeight;
                var visible = pageBottom >= top && pageTop <= bottom;

                updated.Add(new ContinuousPageItem
                {
                    PageIndex = i,
                    DisplayWidth = displayW,
                    DisplayHeight = displayH,
                    PageWidthDip = size.Width,
                    PageHeightDip = size.Height
                });

                if (_realizedContinuousHosts.TryGetValue(i, out var host))
                {
                    host.Root.Width = displayW;
                    host.Root.Height = displayH;
                    host.Image.Width = displayW;
                    host.Image.Height = displayH;
                    host.DisplayHeight = layoutHeight;
                    if (host.Overlay != null)
                    {
                        host.Overlay.Width = displayW;
                        host.Overlay.Height = displayH;
                    }

                    if (!visible)
                    {
                        host.Image.Source = null;
                        host.Rendered = false;
                    }
                }

                y = pageBottom;
            }

            _continuousPageItems.Clear();
            foreach (var item in updated)
                _continuousPageItems.Add(item);
        }

        private void ShowFindBar()
        {
            FindBar.Visibility = Visibility.Visible;
            FindTextBox.PlaceholderText = Loc.Get("find.placeholder");
            FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        }

        private void CloseFindBar()
        {
            FindBar.Visibility = Visibility.Collapsed;
            ClearFindHighlights();
            _findHits.Clear();
            _findIndex = -1;
            FindStatusText.Text = "";
        }

        private void ClearFindHighlights()
        {
            foreach (var overlay in EnumerateOverlays())
                overlay.ClearSearchHighlight();
        }

        private async void RunFind(bool forward)
        {
            if (_textIndex == null || _pdfDocument == null)
            {
                FindStatusText.Text = Loc.Get("find.noText");
                return;
            }

            var query = FindTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(query))
            {
                FindStatusText.Text = "";
                ClearFindHighlights();
                return;
            }

            if (_textIndex.IndexedPageCount < _pdfDocument.PageCount)
            {
                FindStatusText.Text = Loc.Get("find.indexing");
                try
                {
                    await _textIndex.EnsureAllPagesIndexedAsync(_pdfDocument.PageCount);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Find indexing failed: {ex.Message}");
                }
            }

            _findHits = PdfTextSearch.Find(_textIndex, query);
            if (_findHits.Count == 0)
            {
                FindStatusText.Text = Loc.Get("find.none");
                ClearFindHighlights();
                _findIndex = -1;
                return;
            }

            if (_findIndex < 0)
                _findIndex = forward ? 0 : _findHits.Count - 1;
            else
                _findIndex = forward
                    ? (_findIndex + 1) % _findHits.Count
                    : (_findIndex - 1 + _findHits.Count) % _findHits.Count;

            _ = NavigateToFindHitAsync(_findHits[_findIndex]);
            FindStatusText.Text = Loc.Format("find.status", _findIndex + 1, _findHits.Count);
        }

        private async Task NavigateToFindHitAsync(TextSearchHit hit)
        {
            ClearFindHighlights();
            _currentPageIndex = hit.PageIndex;

            if (_isContinuousMode)
            {
                ScrollToCurrentPage();
                await RenderVisibleContinuousPagesAsync();
            }
            else
            {
                await RenderCurrentPage();
            }

            ApplyFindHighlightToVisibleOverlays(hit);
            UpdateStatusPageText();
            PersistCurrentSession();
        }

        private void ApplyFindHighlightToVisibleOverlays(TextSearchHit hit)
        {
            foreach (var overlay in EnumerateOverlays())
            {
                if (overlay.PageIndex == hit.PageIndex)
                    overlay.SetSearchHighlight(hit.Bounds);
                else
                    overlay.ClearSearchHighlight();
            }
        }

        private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                RunFind(forward: true);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                CloseFindBar();
                e.Handled = true;
            }
        }

        private void FindNextButton_Click(object sender, RoutedEventArgs e) => RunFind(true);

        private void FindPrevButton_Click(object sender, RoutedEventArgs e) => RunFind(false);

        private void FindCloseButton_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void ScheduleAnnotationAutosave()
        {
            if (_currentFile == null) return;
            _annotationSaveTimer ??= DispatcherQueue.CreateTimer();
            _annotationSaveTimer.Interval = TimeSpan.FromMilliseconds(800);
            _annotationSaveTimer.Tick -= AnnotationSaveTimer_Tick;
            _annotationSaveTimer.Tick += AnnotationSaveTimer_Tick;
            _annotationSaveTimer.Stop();
            _annotationSaveTimer.Start();
        }

        private async void AnnotationSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (_currentFile?.Path is { } path)
                await AnnotationSidecarStore.SaveAsync(path, _annotations);
        }

        private async Task<string?> PromptPasswordAsync()
        {
            var box = new PasswordBox { PlaceholderText = Loc.Get("dialog.password.placeholder") };
            var dialog = new ContentDialog
            {
                Title = Loc.Get("dialog.password.title"),
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = Loc.Get("dialog.password.content"), TextWrapping = TextWrapping.Wrap },
                        box
                    }
                },
                PrimaryButtonText = Loc.Get("dialog.password.unlock"),
                CloseButtonText = Loc.Get("dialog.password.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Password : null;
        }

        private void RegisterKeyboardAccelerators()
        {
            if (Content is not UIElement root) return;

            // Ctrl+O/P/F/G/E and zoom keys are handled in Window_KeyDown to avoid duplicate dialogs.
            AddMenuAccelerator(root, VirtualKey.S, VirtualKeyModifiers.Control, "savewithannotations");
        }

        private void AddMenuAccelerator(
            UIElement root,
            VirtualKey key,
            VirtualKeyModifiers modifiers,
            string tag,
            bool requiresPdf = false)
        {
            var accel = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accel.Invoked += (sender, e) =>
            {
                if (requiresPdf && _pdfDocument == null)
                    return;
                e.Handled = true;
                ShowViewerContent();
                _ = InvokeMenuActionAsync(tag);
            };
            root.KeyboardAccelerators.Add(accel);
        }
    }
}
