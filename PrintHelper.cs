using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Text;

namespace MicaPDF
{
    public class PrintHelper
    {
        private readonly IntPtr _hWnd;
        private PrintManager? _printManager;
        private readonly PrintDocument _printDocument;
        private readonly IPrintDocumentSource _printDocumentSource;
        private readonly PdfDocument _pdfDocument;
        private readonly AnnotationStore _annotations;
        private readonly List<UIElement?> _pages = new();
        private readonly List<InMemoryRandomAccessStream> _streams = new();
        private PrintTaskOptions? _lastOptions;

        public PrintHelper(Window window, PdfDocument pdfDocument, AnnotationStore annotations)
        {
            _pdfDocument = pdfDocument;
            _annotations = annotations;
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            _printManager = PrintManagerInterop.GetForWindow(_hWnd);
            _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;

            _printDocument = new PrintDocument();
            _printDocumentSource = _printDocument.DocumentSource;
            _printDocument.Paginate += PrintDocument_Paginate;
            _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
            _printDocument.AddPages += PrintDocument_AddPages;
        }

        public async Task ShowPrintUIAsync()
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(_hWnd);
        }

        public void Unregister()
        {
            if (_printManager != null)
            {
                _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested;
                _printManager = null;
            }

            foreach (var stream in _streams)
                stream.Dispose();
            _streams.Clear();
            _pages.Clear();
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var printTask = args.Request.CreatePrintTask(Loc.Get("print.jobName"), sourceRequested =>
            {
                sourceRequested.SetSource(_printDocumentSource);
            });

            printTask.Options.DisplayedOptions.Clear();
            printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.Copies);
            printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.Orientation);
            printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.MediaSize);
            printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.ColorMode);
            printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);

            printTask.Options.PageRangeOptions.AllowAllPages = true;
            printTask.Options.PageRangeOptions.AllowCurrentPage = true;
            printTask.Options.PageRangeOptions.AllowCustomSetOfPages = true;
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            _lastOptions = e.PrintTaskOptions;
            _pages.Clear();
            var pages = GetPagesToPrint(e.PrintTaskOptions);
            _printDocument.SetPreviewPageCount(Math.Max(1, pages.Count), PreviewPageCountType.Final);
        }

        private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            try
            {
                var pages = GetPagesToPrint(_lastOptions);
                if (e.PageNumber < 1 || e.PageNumber > pages.Count)
                    return;
                var pdfIndex = pages[e.PageNumber - 1];
                await PreparePageAsync(pdfIndex, e.PageNumber);
                _printDocument.SetPreviewPage(e.PageNumber, _pages[(int)pdfIndex]!);
            }
            catch { }
        }

        private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
        {
            try
            {
                _lastOptions = e.PrintTaskOptions;
                var pages = GetPagesToPrint(e.PrintTaskOptions);
                var printPageNumber = 1;
                foreach (var pdfIndex in pages)
                {
                    await PreparePageAsync(pdfIndex, printPageNumber);
                    _printDocument.AddPage(_pages[(int)pdfIndex]!);
                    printPageNumber++;
                }

                _printDocument.AddPagesComplete();
            }
            catch { }
        }

        private List<uint> GetPagesToPrint(PrintTaskOptions? options)
        {
            var result = new List<uint>();
            uint total = _pdfDocument.PageCount;
            if (total == 0) return result;

            try
            {
                if (options?.CustomPageRanges is { Count: > 0 } ranges)
                {
                    foreach (var range in ranges)
                    {
                        var from = Math.Max(1u, Math.Min(GetRangeStart(range), total));
                        var to = Math.Max(from, Math.Min(GetRangeEnd(range), total));
                        for (uint p = from; p <= to; p++)
                            result.Add(p - 1);
                    }
                }
            }
            catch { }

            if (result.Count == 0)
            {
                for (uint i = 0; i < total; i++)
                    result.Add(i);
            }

            return result;
        }

        private static uint GetRangeStart(PrintPageRange range)
        {
            var type = range.GetType();
            foreach (var name in new[] { "FirstPageNumber", "PageFrom", "FromPage", "StartPage" })
            {
                var prop = type.GetProperty(name);
                if (prop?.GetValue(range) is uint u) return u;
                if (prop?.GetValue(range) is int i && i > 0) return (uint)i;
            }
            return 1;
        }

        private static uint GetRangeEnd(PrintPageRange range)
        {
            var type = range.GetType();
            foreach (var name in new[] { "LastPageNumber", "PageTo", "ToPage", "EndPage" })
            {
                var prop = type.GetProperty(name);
                if (prop?.GetValue(range) is uint u) return u;
                if (prop?.GetValue(range) is int i && i > 0) return (uint)i;
            }
            return GetRangeStart(range);
        }

        private async Task PreparePageAsync(uint pageIndex, int printPageNumber)
        {
            while (_pages.Count <= pageIndex)
                _pages.Add(null);

            if (_pages[(int)pageIndex] != null) return;

            using var page = _pdfDocument.GetPage(pageIndex);
            var pageWidth = Math.Max(1, page.Size.Width);
            var pageHeight = Math.Max(1, page.Size.Height);
            var printSize = TryGetPrintPageSize(printPageNumber);

            var scale = 2.0;
            var destW = (float)Math.Max(1, pageWidth * scale);
            var destH = (float)Math.Max(1, pageHeight * scale);
            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)destW,
                DestinationHeight = (uint)destH
            };

            using var pdfStream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(pdfStream, renderOptions);

            var device = CanvasDevice.GetSharedDevice();
            using var pdfBitmap = await CanvasBitmap.LoadAsync(device, pdfStream);
            using var target = new CanvasRenderTarget(device, destW, destH, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(255, 255, 255, 255));
                ds.DrawImage(pdfBitmap, new Windows.Foundation.Rect(0, 0, destW, destH));
                ds.Transform = Matrix3x2.CreateScale((float)scale);
                foreach (var stroke in _annotations.GetStrokes(pageIndex).GetStrokes())
                    AnnotationOverlay.DrawStroke(ds, stroke);
                foreach (var text in _annotations.GetTexts(pageIndex))
                    DrawTextAnnotation(ds, text);
            }

            var outStream = new InMemoryRandomAccessStream();
            _streams.Add(outStream);
            await target.SaveAsync(outStream, CanvasBitmapFileFormat.Png);
            outStream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(outStream);
            var image = new Image
            {
                Source = bitmap,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var container = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            if (printSize is { } size && size.Width > 1 && size.Height > 1)
            {
                container.Width = size.Width;
                container.Height = size.Height;
            }

            container.Children.Add(image);
            _pages[(int)pageIndex] = container;
        }

        private Windows.Foundation.Size? TryGetPrintPageSize(int printPageNumber)
        {
            try
            {
                if (_lastOptions == null) return null;
                var desc = _lastOptions.GetPageDescription((uint)Math.Max(1, printPageNumber));
                return desc.PageSize;
            }
            catch
            {
                return null;
            }
        }

        private static void DrawTextAnnotation(CanvasDrawingSession ds, TextAnnotation text)
        {
            if (string.IsNullOrEmpty(text.Text)) return;

            var weight = text.IsBold ? FontWeights.Bold : FontWeights.Normal;
            var style = text.IsItalic ? FontStyle.Italic : FontStyle.Normal;
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = (float)Math.Max(6, text.FontSize),
                FontWeight = weight,
                FontStyle = style,
                WordWrapping = CanvasWordWrapping.Wrap
            };
            using var layout = new CanvasTextLayout(
                ds,
                text.Text,
                format,
                (float)Math.Max(8, text.Width),
                (float)Math.Max(8, text.Height));
            ds.DrawTextLayout(
                layout,
                new Vector2((float)text.X, (float)text.Y),
                text.Color);
        }
    }
}
