using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Pig = UglyToad.PdfPig;
using PigCore = UglyToad.PdfPig.Core;

namespace MicaPDF
{
    public sealed class PdfGlyph
    {
        public uint PageIndex { get; init; }
        public string Value { get; init; } = "";
        public Rect Bounds { get; init; }
        public double AdvanceWidth { get; init; }
        public double BaselineY { get; init; }
        public int TextSequence { get; init; }
    }

    public sealed class PdfTextIndex : IDisposable
    {
        private readonly Dictionary<uint, List<PdfGlyph>> _glyphs = new();
        private readonly object _lock = new();
        private readonly string? _sourcePath;
        private readonly IReadOnlyDictionary<uint, Size>? _pageSizes;
        private Pig.PdfDocument? _document;
        private int _indexedPageCount;

        private PdfTextIndex(string? sourcePath, IReadOnlyDictionary<uint, Size>? pageSizes)
        {
            _sourcePath = sourcePath;
            _pageSizes = pageSizes;
        }

        public static PdfTextIndex CreateLazy(string sourcePath, IReadOnlyDictionary<uint, Size> pageSizes) =>
            new(sourcePath, pageSizes);

        public static async Task<PdfTextIndex> LoadAsync(StorageFile file, IReadOnlyDictionary<uint, Size> pageSizes)
        {
            var bytes = await PdfPigServices.ReadBytesAsync(file);
            return LoadFromBytes(bytes, pageSizes);
        }

        public static PdfTextIndex LoadFromBytes(byte[] bytes, IReadOnlyDictionary<uint, Size> pageSizes)
        {
            var index = new PdfTextIndex(null, pageSizes);
            try
            {
                using var document = Pig.PdfDocument.Open(bytes);
                index.LoadFromDocument(document, pageSizes);
            }
            catch
            {
                // Scanned PDFs, encryption, or parse failures: no selectable text.
            }

            return index;
        }

        public bool IsPageIndexed(uint pageIndex) => _glyphs.ContainsKey(pageIndex);

        public int IndexedPageCount
        {
            get
            {
                lock (_lock) return _indexedPageCount;
            }
        }

        public void EnsurePageIndexed(uint pageIndex)
        {
            if (IsPageIndexed(pageIndex)) return;

            lock (_lock)
            {
                if (_glyphs.ContainsKey(pageIndex)) return;
                EnsureDocument();
                if (_document == null) return;

                var page = _document.GetPage((int)pageIndex + 1);
                if (!(_pageSizes?.TryGetValue(pageIndex, out var winSize) ?? false))
                    winSize = new Size(page.Width * 96.0 / 72.0, page.Height * 96.0 / 72.0);

                _glyphs[pageIndex] = IndexPageLetters(page, pageIndex, winSize);
                _indexedPageCount = _glyphs.Count;
            }
        }

        public Task EnsurePageIndexedAsync(uint pageIndex, CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePageIndexed(pageIndex);
            }, cancellationToken);

        public async Task PrefetchPagesAsync(IEnumerable<uint> pageIndices, CancellationToken cancellationToken = default)
        {
            foreach (var page in pageIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsurePageIndexedAsync(page, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EnsureAllPagesIndexedAsync(uint pageCount, CancellationToken cancellationToken = default)
        {
            for (uint i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsurePageIndexedAsync(i, cancellationToken).ConfigureAwait(false);
            }
        }

        internal void LoadFromDocument(Pig.PdfDocument document, IReadOnlyDictionary<uint, Size> pageSizes)
        {
            foreach (var page in document.GetPages())
            {
                var pageIndex = (uint)(page.Number - 1);
                if (!pageSizes.TryGetValue(pageIndex, out var winSize))
                    winSize = new Size(page.Width * 96.0 / 72.0, page.Height * 96.0 / 72.0);

                _glyphs[pageIndex] = IndexPageLetters(page, pageIndex, winSize);
            }

            _indexedPageCount = _glyphs.Count;
        }

        private void EnsureDocument()
        {
            if (_document != null) return;
            if (string.IsNullOrEmpty(_sourcePath) || !File.Exists(_sourcePath)) return;
            try
            {
                _document = Pig.PdfDocument.Open(_sourcePath);
            }
            catch
            {
                _document = null;
            }
        }

        private static List<PdfGlyph> IndexPageLetters(Pig.Content.Page page, uint pageIndex, Size winSize)
        {
            var crop = page.CropBox.Bounds;
            var rotate = ((page.Rotation.Value % 360) + 360) % 360;
            var visW = rotate is 90 or 270 ? crop.Height : crop.Width;
            var visH = rotate is 90 or 270 ? crop.Width : crop.Height;
            if (visW <= 0) visW = Math.Max(1, page.Width);
            if (visH <= 0) visH = Math.Max(1, page.Height);

            var list = new List<PdfGlyph>();
            var seq = 0;
            foreach (var letter in page.Letters)
            {
                if (string.IsNullOrEmpty(letter.Value)) continue;

                var advance = letter.Width;
                if (advance <= 0)
                {
                    var dx = letter.EndBaseLine.X - letter.StartBaseLine.X;
                    var dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
                    advance = Math.Sqrt(dx * dx + dy * dy);
                }

                var height = letter.BoundingBox.Height;
                if (height <= 0)
                    height = letter.PointSize > 0 ? letter.PointSize : Math.Max(1, letter.FontSize);

                var pdfLeft = letter.StartBaseLine.X;
                var pdfBottom = Math.Min(letter.StartBaseLine.Y, letter.BoundingBox.Bottom);
                var pdfRight = pdfLeft + Math.Max(advance, letter.BoundingBox.Width);
                var pdfTop = Math.Max(letter.BoundingBox.Top, pdfBottom + height);
                if (pdfRight <= pdfLeft || pdfTop <= pdfBottom)
                {
                    var g = letter.BoundingBox;
                    pdfLeft = g.Left;
                    pdfBottom = g.Bottom;
                    pdfRight = g.Right;
                    pdfTop = g.Top;
                }

                MapPdfRectToWin(
                    pdfLeft, pdfBottom, pdfRight, pdfTop,
                    crop, rotate, visW, visH, winSize,
                    out var winX, out var winY, out var winW, out var winH);

                if (winW <= 0 || winH <= 0) continue;

                MapPdfPointToWin(
                    letter.StartBaseLine.X, letter.StartBaseLine.Y,
                    crop, rotate, visW, visH, winSize,
                    out _, out var baselineY);

                var textSeq = letter.TextSequence;
                if (textSeq == 0)
                    textSeq = seq;

                list.Add(new PdfGlyph
                {
                    PageIndex = pageIndex,
                    Value = letter.Value,
                    Bounds = new Rect(winX, winY, winW, winH),
                    AdvanceWidth = advance / visW * winSize.Width,
                    BaselineY = baselineY,
                    TextSequence = textSeq
                });
                seq++;
            }

            return list;
        }

        /// <summary>
        /// PDF user space (bottom-left) → Windows.Data.Pdf visual DIPs (top-left, upright after /Rotate).
        /// Mirrors AnnotatedPdfExporter mapping inverted.
        /// </summary>
        private static void MapPdfPointToWin(
            double pdfX,
            double pdfY,
            PigCore.PdfRectangle crop,
            int rotate,
            double visW,
            double visH,
            Size winSize,
            out double winX,
            out double winY)
        {
            double vx, vy;
            switch (rotate)
            {
                case 90:
                    vx = pdfY - crop.Bottom;
                    vy = crop.Right - pdfX;
                    break;
                case 180:
                    vx = crop.Right - pdfX;
                    vy = crop.Top - pdfY;
                    break;
                case 270:
                    vx = crop.Top - pdfY;
                    vy = pdfX - crop.Left;
                    break;
                default:
                    vx = pdfX - crop.Left;
                    vy = crop.Top - pdfY;
                    break;
            }

            winX = vx / visW * winSize.Width;
            winY = vy / visH * winSize.Height;
        }

        private static void MapPdfRectToWin(
            double pdfLeft,
            double pdfBottom,
            double pdfRight,
            double pdfTop,
            PigCore.PdfRectangle crop,
            int rotate,
            double visW,
            double visH,
            Size winSize,
            out double winX,
            out double winY,
            out double winW,
            out double winH)
        {
            MapPdfPointToWin(pdfLeft, pdfBottom, crop, rotate, visW, visH, winSize, out var x0, out var y0);
            MapPdfPointToWin(pdfRight, pdfTop, crop, rotate, visW, visH, winSize, out var x1, out var y1);
            MapPdfPointToWin(pdfLeft, pdfTop, crop, rotate, visW, visH, winSize, out var x2, out var y2);
            MapPdfPointToWin(pdfRight, pdfBottom, crop, rotate, visW, visH, winSize, out var x3, out var y3);

            var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            winX = minX;
            winY = minY;
            winW = Math.Max(0.5, maxX - minX);
            winH = Math.Max(0.5, maxY - minY);
        }

        public IReadOnlyCollection<uint> PageIndices
        {
            get
            {
                lock (_lock) return _glyphs.Keys.ToList();
            }
        }

        public IReadOnlyList<PdfGlyph> GetGlyphs(uint pageIndex)
        {
            EnsurePageIndexed(pageIndex);
            return _glyphs.TryGetValue(pageIndex, out var list) ? list : Array.Empty<PdfGlyph>();
        }

        public PdfGlyph? HitTest(uint pageIndex, Point pagePoint)
        {
            if (!_glyphs.TryGetValue(pageIndex, out var list))
            {
                EnsurePageIndexed(pageIndex);
                if (!_glyphs.TryGetValue(pageIndex, out list)) return null;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (Contains(list[i].Bounds, pagePoint))
                    return list[i];
            }

            return null;
        }

        public List<PdfGlyph> GlyphsInRect(uint pageIndex, Rect pageRect)
        {
            if (!_glyphs.TryGetValue(pageIndex, out var list))
            {
                EnsurePageIndexed(pageIndex);
                if (!_glyphs.TryGetValue(pageIndex, out list))
                    return new List<PdfGlyph>();
            }

            return list.Where(g => Intersects(g.Bounds, pageRect)).ToList();
        }

        /// <summary>
        /// Line-oriented selection: glyphs from start→end by reading order (baseline rows).
        /// </summary>
        public List<PdfGlyph> GlyphsInLineRange(uint pageIndex, Point start, Point end)
        {
            if (!_glyphs.TryGetValue(pageIndex, out var list) || list.Count == 0)
            {
                EnsurePageIndexed(pageIndex);
                if (!_glyphs.TryGetValue(pageIndex, out list) || list.Count == 0)
                    return new List<PdfGlyph>();
            }

            var startG = HitTest(pageIndex, start) ?? NearestGlyph(list, start);
            var endG = HitTest(pageIndex, end) ?? NearestGlyph(list, end);
            if (startG == null || endG == null)
                return new List<PdfGlyph>();

            var ordered = list
                .OrderBy(g => Math.Round(g.BaselineY / 2) * 2)
                .ThenBy(g => g.Bounds.X)
                .ThenBy(g => g.TextSequence)
                .ToList();

            var i0 = ordered.IndexOf(startG);
            var i1 = ordered.IndexOf(endG);
            if (i0 < 0 || i1 < 0)
                return new List<PdfGlyph>();
            if (i1 < i0) (i0, i1) = (i1, i0);

            return ordered.GetRange(i0, i1 - i0 + 1);
        }

        private static PdfGlyph? NearestGlyph(List<PdfGlyph> list, Point p)
        {
            PdfGlyph? best = null;
            var bestDist = double.MaxValue;
            foreach (var g in list)
            {
                var cx = g.Bounds.X + g.Bounds.Width / 2;
                var cy = g.BaselineY;
                var d = (cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = g;
                }
            }
            return best;
        }

        public static string BuildText(IReadOnlyList<PdfGlyph> glyphs)
        {
            if (glyphs.Count == 0) return "";

            var ordered = glyphs
                .OrderBy(g => g.TextSequence)
                .ThenBy(g => Math.Round(g.BaselineY / 2) * 2)
                .ThenBy(g => g.Bounds.X)
                .ToList();

            var sb = new StringBuilder();
            double? lastBaseline = null;
            double lastRight = 0;
            foreach (var g in ordered)
            {
                var advance = Math.Max(g.AdvanceWidth, g.Bounds.Width);
                var lineGap = Math.Max(4, g.Bounds.Height * 0.55);
                var spaceGap = Math.Max(1.2, advance * 0.35);

                if (lastBaseline.HasValue && Math.Abs(g.BaselineY - lastBaseline.Value) > lineGap)
                {
                    sb.AppendLine();
                    lastRight = 0;
                }
                else if (lastBaseline.HasValue && g.Bounds.X - lastRight > spaceGap)
                {
                    sb.Append(' ');
                }

                sb.Append(g.Value);
                lastBaseline = g.BaselineY;
                lastRight = g.Bounds.X + advance;
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _document?.Dispose();
                _document = null;
                _glyphs.Clear();
            }
        }

        private static bool Contains(Rect r, Point p) =>
            p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;

        private static bool Intersects(Rect a, Rect b) =>
            a.X < b.X + b.Width && a.X + a.Width > b.X &&
            a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    }
}
