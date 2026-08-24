using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;
using Windows.Storage;

namespace MicaPDF
{
    public sealed class PdfOutlineEntry
    {
        public string Title { get; init; } = "";
        /// <summary>Zero-based page index, or null when the bookmark has no in-document destination.</summary>
        public int? PageIndex { get; init; }
        public List<PdfOutlineEntry> Children { get; } = new();
    }

    public sealed class PdfOutline
    {
        public IReadOnlyList<PdfOutlineEntry> Roots { get; }

        private PdfOutline(IReadOnlyList<PdfOutlineEntry> roots) => Roots = roots;

        public bool HasEntries => Roots.Count > 0;

        public static async Task<PdfOutline?> LoadAsync(StorageFile file)
        {
            try
            {
                byte[] bytes;
                using (var stream = await file.OpenStreamForReadAsync())
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }

                using var document = PdfDocument.Open(bytes);
                if (!document.TryGetBookmarks(out Bookmarks? bookmarks) || bookmarks.Roots.Count == 0)
                    return null;

                var roots = new List<PdfOutlineEntry>();
                foreach (var node in bookmarks.Roots)
                {
                    var entry = MapNode(node);
                    if (entry != null)
                        roots.Add(entry);
                }

                return roots.Count == 0 ? null : new PdfOutline(roots);
            }
            catch
            {
                return null;
            }
        }

        private static PdfOutlineEntry? MapNode(BookmarkNode node)
        {
            int? pageIndex = null;
            if (node is DocumentBookmarkNode doc && doc.PageNumber > 0)
                pageIndex = doc.PageNumber - 1;

            var title = string.IsNullOrWhiteSpace(node.Title) ? "…" : node.Title.Trim();
            var entry = new PdfOutlineEntry { Title = title, PageIndex = pageIndex };

            foreach (var child in node.Children)
            {
                var mapped = MapNode(child);
                if (mapped != null)
                    entry.Children.Add(mapped);
            }

            return entry;
        }
    }
}
