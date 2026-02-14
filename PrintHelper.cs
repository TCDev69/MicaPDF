using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;

namespace MicaPDF
{
    public class PrintHelper
    {
        private Window _window;
        private IntPtr _hWnd;
        private PrintManager _printManager;
        private PrintDocument _printDocument;
        private IPrintDocumentSource _printDocumentSource;
        private PdfDocument _pdfDocument;
        private List<UIElement> _pages = new List<UIElement>();
        private List<InMemoryRandomAccessStream> _streams = new List<InMemoryRandomAccessStream>();

        public PrintHelper(Window window, PdfDocument pdfDocument)
        {
            _window = window;
            _pdfDocument = pdfDocument;
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            
            // Get PrintManager for current window
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
            
            // Cleanup streams
            foreach(var stream in _streams)
            {
                stream.Dispose();
            }
            _streams.Clear();
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var printTask = args.Request.CreatePrintTask("MicaPDF Print Job", sourceRequested =>
            {
                sourceRequested.SetSource(_printDocumentSource);
            });
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            _printDocument.SetPreviewPageCount((int)_pdfDocument.PageCount, PreviewPageCountType.Final);
        }

        private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            try
            {
                await PreparePageAsync((uint)e.PageNumber - 1);
                _printDocument.SetPreviewPage(e.PageNumber, _pages[e.PageNumber - 1]);
            }
            catch { }
        }

        private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
        {
            try 
            {
                for (uint i = 0; i < _pdfDocument.PageCount; i++)
                {
                    await PreparePageAsync(i);
                    _printDocument.AddPage(_pages[(int)i]);
                }
                
                _printDocument.AddPagesComplete();
            }
            catch { }
        }

        private async Task PreparePageAsync(uint pageIndex)
        {
            // Expand list if needed
            while (_pages.Count <= pageIndex) _pages.Add(null);
            
            if (_pages[(int)pageIndex] != null) return;

            using (var page = _pdfDocument.GetPage(pageIndex))
            {
                // Simple A4 ratio calculation for target size
                // or just use page size
                var width = page.Size.Width;
                var height = page.Size.Height;
                
                var renderOptions = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(width * 2), // Higher DPI for print
                    DestinationHeight = (uint)(height * 2)
                };

                var stream = new InMemoryRandomAccessStream();
                _streams.Add(stream); // Keep stream alive
                
                await page.RenderToStreamAsync(stream, renderOptions);
                
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                
                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                
                // Wrap in A4-ish container or let it fill?
                // Print framework handles pagination based on content size usually
                // But for fixed page PDF, we probably want 1 page per sheet
                var container = new Grid
                {
                    // No fixed size, let print layout handle it
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch 
                };
                container.Children.Add(image);
                
                _pages[(int)pageIndex] = container;
            }
        }
    }
}
