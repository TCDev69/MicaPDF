using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;
using Windows.Foundation;
using Windows.Storage;

namespace MicaPDF
{
    /// <summary>
    /// Single PdfPig open for text index + outline to avoid duplicate full-document parsing.
    /// </summary>
    internal static class PdfPigServices
    {
        public static async Task<(PdfTextIndex TextIndex, PdfOutline? Outline)> LoadTextAndOutlineAsync(
            StorageFile file,
            IReadOnlyDictionary<uint, Size> pageSizes)
        {
            var bytes = await ReadBytesAsync(file);
            return LoadTextAndOutline(bytes, pageSizes);
        }

        public static (PdfTextIndex TextIndex, PdfOutline? Outline) LoadTextAndOutline(
            byte[] bytes,
            IReadOnlyDictionary<uint, Size> pageSizes)
        {
            PdfOutline? outline = null;
            try
            {
                using var document = PdfDocument.Open(bytes);
                if (document.TryGetBookmarks(out Bookmarks? bookmarks) && bookmarks.Roots.Count > 0)
                    outline = PdfOutline.FromBookmarks(bookmarks);
            }
            catch
            {
                // Best-effort.
            }

            var index = PdfTextIndex.LoadFromBytes(bytes, pageSizes);
            return (index, outline);
        }

        public static PdfOutline? LoadOutlineFromPath(string path)
        {
            try
            {
                using var document = PdfDocument.Open(path);
                if (document.TryGetBookmarks(out Bookmarks? bookmarks) && bookmarks.Roots.Count > 0)
                    return PdfOutline.FromBookmarks(bookmarks);
            }
            catch
            {
                // Best-effort.
            }

            return null;
        }

        public static async Task<byte[]> ReadBytesAsync(StorageFile file)
        {
            using var stream = await file.OpenStreamForReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
    }
}
