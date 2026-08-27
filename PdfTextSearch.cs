using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;

namespace MicaPDF
{
    public readonly struct TextSearchHit
    {
        public TextSearchHit(uint pageIndex, int startGlyph, int length, Rect bounds, string preview)
        {
            PageIndex = pageIndex;
            StartGlyph = startGlyph;
            Length = length;
            Bounds = bounds;
            Preview = preview;
        }

        public uint PageIndex { get; }
        public int StartGlyph { get; }
        public int Length { get; }
        public Rect Bounds { get; }
        public string Preview { get; }
    }

    public static class PdfTextSearch
    {
        public static List<TextSearchHit> Find(PdfTextIndex index, string query, bool caseSensitive = false)
        {
            var results = new List<TextSearchHit>();
            if (index == null || string.IsNullOrWhiteSpace(query))
                return results;

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (var pageIndex in index.PageIndices)
            {
                var glyphs = index.GetGlyphs(pageIndex);
                if (glyphs.Count == 0) continue;

                var sb = new StringBuilder(glyphs.Count);
                foreach (var g in glyphs)
                    sb.Append(g.Value);
                var hay = sb.ToString();

                var start = 0;
                while (start < hay.Length)
                {
                    var idx = hay.IndexOf(query, start, comparison);
                    if (idx < 0) break;

                    var end = Math.Min(glyphs.Count, idx + query.Length);
                    var startG = glyphs[idx];
                    double minX = startG.Bounds.X, minY = startG.Bounds.Y;
                    double maxX = startG.Bounds.X + startG.Bounds.Width;
                    double maxY = startG.Bounds.Y + startG.Bounds.Height;
                    for (var i = idx; i < end; i++)
                    {
                        var b = glyphs[i].Bounds;
                        minX = Math.Min(minX, b.X);
                        minY = Math.Min(minY, b.Y);
                        maxX = Math.Max(maxX, b.X + b.Width);
                        maxY = Math.Max(maxY, b.Y + b.Height);
                    }

                    var previewStart = Math.Max(0, idx - 12);
                    var previewLen = Math.Min(hay.Length - previewStart, query.Length + 24);
                    var preview = hay.Substring(previewStart, previewLen);

                    results.Add(new TextSearchHit(
                        pageIndex,
                        idx,
                        end - idx,
                        new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)),
                        preview));

                    start = idx + Math.Max(1, query.Length);
                }
            }

            return results;
        }
    }
}
