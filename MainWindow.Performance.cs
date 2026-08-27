using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MicaPDF
{
    public sealed partial class MainWindow
    {
        private readonly Dictionary<uint, Size> _pageSizes = new();
        private readonly Dictionary<string, BitmapImage> _coverImageCache = new();
        private readonly ContinuousOverlayPool _overlayPool = new();
        private readonly Dictionary<uint, ContinuousPageHost> _realizedContinuousHosts = new();
        private readonly ObservableCollection<ContinuousPageItem> _continuousPageItems = new();
        private readonly SemaphoreSlim _renderSemaphore = new(1, 1);
        private CancellationTokenSource? _indexLoadCts;
        private CancellationTokenSource? _outlineLoadCts;
        private string? _textIndexSourcePath;

        private double GetRasterizationScale()
        {
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            return scale < 0.5 ? 1.0 : scale;
        }

        private (uint Width, uint Height) GetRasterDestinationSize(uint pageIndex)
        {
            var size = GetPageSize(pageIndex);
            var (vw, vh) = GetViewportDips();
            return RasterSizeCalculator.ComputeDestinationSize(
                size.Width, size.Height, _currentZoom, GetRasterizationScale(), vw, vh);
        }

        private (double Width, double Height) GetDisplayDimensions(uint pageIndex)
        {
            var size = GetPageSize(pageIndex);
            var (vw, vh) = GetViewportDips();
            var (w, h) = RasterSizeCalculator.ComputeDisplaySize(
                size.Width, size.Height, _currentZoom, GetRasterizationScale(), vw, vh);
            return (w, h);
        }

        private (double Width, double Height) GetViewportDips()
        {
            var vw = PdfScrollViewer.ViewportWidth > 0 ? PdfScrollViewer.ViewportWidth : PdfScrollViewer.ActualWidth;
            var vh = PdfScrollViewer.ViewportHeight > 0 ? PdfScrollViewer.ViewportHeight : PdfScrollViewer.ActualHeight;
            if (vw < 64) vw = 1200;
            if (vh < 64) vh = 800;
            return (vw, vh);
        }

        private void ResetDocumentCaches()
        {
            _indexLoadCts?.Cancel();
            _indexLoadCts?.Dispose();
            _indexLoadCts = null;
            _outlineLoadCts?.Cancel();
            _outlineLoadCts?.Dispose();
            _outlineLoadCts = null;
            _textIndex?.Dispose();
            _textIndex = null;
            _textIndexSourcePath = null;
            _pageSizes.Clear();
            _realizedContinuousHosts.Clear();
            _continuousPageItems.Clear();
            _overlayPool.Clear();
        }

        private async Task LoadDocumentIndexInBackgroundAsync(string sourcePath, CancellationToken cancellationToken)
        {
            try
            {
                // Outline loads on demand when the pane opens (saves PdfPig memory at open).
                // Text index stays lazy until Find/search needs it.
                using (LoadDiagnostics.Step("pageSizes"))
                {
                    await PopulateRemainingPageSizesAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Document closed or replaced.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Background index load failed: {ex.Message}");
            }
        }

        private IEnumerable<uint> GetTextIndexPrefetchPages()
        {
            if (_pdfDocument == null) yield break;
            var center = _currentPageIndex;
            yield return center;
            if (center > 0) yield return center - 1;
            if (center + 1 < _pdfDocument.PageCount) yield return center + 1;
        }

        private async Task PopulateRemainingPageSizesAsync(CancellationToken cancellationToken)
        {
            if (_pdfDocument == null) return;
            for (uint i = 0; i < _pdfDocument.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_pageSizes.ContainsKey(i))
                    CachePageSize(i);
                if (i % 32 == 0)
                    await Task.Yield();
            }
        }

        private void CachePageSize(uint pageIndex)
        {
            if (_pdfDocument == null || pageIndex >= _pdfDocument.PageCount) return;
            if (_pageSizes.ContainsKey(pageIndex)) return;
            using var page = _pdfDocument.GetPage(pageIndex);
            _pageSizes[pageIndex] = page.Size;
        }

        private void RefreshTextIndexOnOverlays()
        {
            foreach (var overlay in EnumerateOverlays())
                overlay.SetTextIndex(_textIndex);
        }

        private async Task PrefetchPageBitmapsAsync(IReadOnlyList<uint> pageIndices, bool showOverlayOnMiss = false)
        {
            if (pageIndices.Count == 0) return;

            if (pageIndices.Count == 1 || _pageCache.IsNearByteBudget(0.8))
            {
                foreach (var page in pageIndices)
                    await RenderPageBitmapAsync(page, showOverlayOnMiss);
                return;
            }

            var tasks = new List<Task>();
            foreach (var page in pageIndices)
            {
                tasks.Add(RenderPageBitmapWithSemaphoreAsync(page, showOverlayOnMiss));
            }

            await Task.WhenAll(tasks);
        }

        private async Task RenderPageBitmapWithSemaphoreAsync(uint pageIndex, bool showOverlayOnMiss)
        {
            await _renderSemaphore.WaitAsync();
            try
            {
                await RenderPageBitmapAsync(pageIndex, showOverlayOnMiss);
            }
            finally
            {
                _renderSemaphore.Release();
            }
        }

        private void RefreshVisibleOverlays()
        {
            foreach (var overlay in EnumerateVisibleOverlays())
                overlay.Refresh();
        }

        private IEnumerable<AnnotationOverlay> EnumerateVisibleOverlays()
        {
            if (!_isContinuousMode)
            {
                foreach (var overlay in EnumerateOverlays())
                    yield return overlay;
                yield break;
            }

            foreach (var host in _realizedContinuousHosts.Values)
            {
                if (host.Overlay != null)
                    yield return host.Overlay;
            }
        }

        private BitmapImage? GetCachedCoverImage(string coverPath)
        {
            if (_coverImageCache.TryGetValue(coverPath, out var cached))
                return cached;

            var image = new BitmapImage(new Uri(coverPath)) { DecodePixelWidth = 400 };
            _coverImageCache[coverPath] = image;
            return image;
        }

        private void ContinuousPagesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Index < 0 || args.Index >= _continuousPageItems.Count) return;
            var item = _continuousPageItems[args.Index];
            var root = (Grid)args.Element;
            PrepareContinuousHost(root, item);
            _ = RenderVisibleContinuousPagesAsync();
        }

        private void ContinuousPagesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is not Grid root) return;
            if (root.Tag is not ContinuousPageHost host) return;

            host.Image.Source = null;
            host.Rendered = false;
            if (host.Overlay != null)
            {
                root.Children.Remove(host.Overlay);
                _overlayPool.Return(host.Overlay);
                host.Overlay = null;
            }

            _realizedContinuousHosts.Remove(host.Index);
        }

        private void PrepareContinuousHost(Grid root, ContinuousPageItem item)
        {
            if (root.Tag is ContinuousPageHost existing && existing.Index == item.PageIndex)
            {
                existing.Root.Width = item.DisplayWidth;
                existing.Root.Height = item.DisplayHeight;
                existing.Image.Width = item.DisplayWidth;
                existing.Image.Height = item.DisplayHeight;
                if (existing.Overlay != null)
                {
                    existing.Overlay.Width = item.DisplayWidth;
                    existing.Overlay.Height = item.DisplayHeight;
                }

                existing.DisplayHeight = item.LayoutHeight;
                _realizedContinuousHosts[item.PageIndex] = existing;
                return;
            }

            root.Children.Clear();
            root.Width = item.DisplayWidth;
            root.Height = item.DisplayHeight;

            var image = new Image
            {
                Stretch = Stretch.Fill,
                Width = item.DisplayWidth,
                Height = item.DisplayHeight
            };

            root.Children.Add(image);
            var host = new ContinuousPageHost
            {
                Index = item.PageIndex,
                Root = root,
                Image = image,
                Overlay = null,
                DisplayHeight = item.LayoutHeight,
                PageWidthDip = item.PageWidthDip,
                PageHeightDip = item.PageHeightDip
            };
            root.Tag = host;
            _realizedContinuousHosts[item.PageIndex] = host;
        }

        private AnnotationOverlay EnsureContinuousOverlay(ContinuousPageHost host)
        {
            if (host.Overlay != null) return host.Overlay;

            var overlay = _overlayPool.Rent();
            overlay.Width = host.Root.Width;
            overlay.Height = host.Root.Height;
            overlay.HorizontalAlignment = HorizontalAlignment.Center;
            overlay.VerticalAlignment = VerticalAlignment.Center;
            overlay.Attach(_annotations, host.Index, host.PageWidthDip, host.PageHeightDip);
            overlay.SetHistory(_history);
            overlay.SetTextIndex(_textIndex);
            overlay.SetTool(_currentTool);
            overlay.SelectionChanged -= OnAnnotationSelectionChanged;
            overlay.SelectionChanged += OnAnnotationSelectionChanged;
            overlay.AnnotationsChanged -= OnContinuousOverlayAnnotationsChanged;
            overlay.AnnotationsChanged += OnContinuousOverlayAnnotationsChanged;
            ApplyPenAttributesToOverlay(overlay);
            overlay.DefaultFontSize = _textFontSize;
            overlay.DefaultBold = _textBold;
            overlay.DefaultItalic = _textItalic;
            overlay.DefaultTextColor = _textColor;
            host.Root.Children.Add(overlay);
            host.Overlay = overlay;
            return overlay;
        }

        private void OnContinuousOverlayAnnotationsChanged(object? sender, EventArgs e) =>
            ScheduleAnnotationAutosave();
    }
}
