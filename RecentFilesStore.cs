using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MicaPDF
{
    public sealed class RecentFileEntry
    {
        public string Path { get; set; } = "";
        public uint PageIndex { get; set; }
        public double Zoom { get; set; } = 0.5;
        public DateTime LastOpenedUtc { get; set; }
        public string? CoverFileName { get; set; }
    }

    public sealed class RecentFilesStore
    {
        public const int MaxEntries = 6;
        private const uint CoverWidth = 240;

        private readonly List<RecentFileEntry> _entries = new();
        private readonly object _saveLock = new();

        private static string StoreDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicaPDF");

        private static string StorePath => Path.Combine(StoreDirectory, "recent.json");

        public static string CoversDirectory => Path.Combine(StoreDirectory, "covers");

        public static RecentFilesStore Load()
        {
            var store = new RecentFilesStore();
            try
            {
                if (!File.Exists(StorePath))
                    return store;

                var dto = JsonSerializer.Deserialize<RecentFilesDto>(File.ReadAllText(StorePath));
                if (dto?.Entries == null)
                    return store;

                foreach (var entry in dto.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Path))
                        continue;
                    entry.Path = NormalizePath(entry.Path);
                    entry.CoverFileName = GetCoverFileName(entry.Path);
                    store._entries.Add(entry);
                }
            }
            catch
            {
                // Keep empty list on failure.
            }

            return store;
        }

        public IReadOnlyList<RecentFileEntry> GetEntries() => _entries;

        public RecentFileEntry? Find(string path)
        {
            var normalized = NormalizePath(path);
            return _entries.FirstOrDefault(e =>
                string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public void UpdateSession(string path, uint pageIndex, double zoom, int maxZoomPercent = ZoomLimits.DefaultMaxZoomPercent)
        {
            var normalized = NormalizePath(path);
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;

            entry.PageIndex = pageIndex;
            entry.Zoom = ZoomLimits.ClampZoom(zoom, maxZoomPercent);
            entry.LastOpenedUtc = DateTime.UtcNow;
        }

        public void RecordOpened(string path, uint pageIndex, double zoom, int maxZoomPercent = ZoomLimits.DefaultMaxZoomPercent)
        {
            var normalized = NormalizePath(path);
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                _entries.Remove(existing);

            var coverFileName = GetCoverFileName(normalized);
            if (existing?.CoverFileName is { } oldCover && !string.Equals(oldCover, coverFileName, StringComparison.OrdinalIgnoreCase))
                DeleteCoverFile(oldCover);

            var entry = new RecentFileEntry
            {
                Path = normalized,
                PageIndex = pageIndex,
                Zoom = ZoomLimits.ClampZoom(zoom, maxZoomPercent),
                LastOpenedUtc = DateTime.UtcNow,
                CoverFileName = coverFileName
            };
            _entries.Insert(0, entry);

            while (_entries.Count > MaxEntries)
            {
                var removed = _entries[^1];
                _entries.RemoveAt(_entries.Count - 1);
                DeleteCoverFile(removed.CoverFileName);
            }
        }

        public bool Remove(string path)
        {
            var normalized = NormalizePath(path);
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return false;

            _entries.Remove(entry);
            DeleteCoverFile(entry.CoverFileName);
            return true;
        }

        public void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    Directory.CreateDirectory(StoreDirectory);
                    var dto = new RecentFilesDto
                    {
                        Entries = _entries.Select(e => new RecentFileEntry
                        {
                            Path = e.Path,
                            PageIndex = e.PageIndex,
                            Zoom = e.Zoom,
                            LastOpenedUtc = e.LastOpenedUtc,
                            CoverFileName = e.CoverFileName
                        }).ToArray()
                    };
                    File.WriteAllText(StorePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // Ignore persistence failures.
                }
            }
        }

        public string? GetCoverPath(RecentFileEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Path))
                return null;
            var path = Path.Combine(CoversDirectory, GetCoverFileName(entry.Path));
            return File.Exists(path) ? path : null;
        }

        public static async Task EnsureCoverAsync(PdfDocument document, string filePath)
        {
            var normalized = NormalizePath(filePath);
            var coverFileName = GetCoverFileName(normalized);
            Directory.CreateDirectory(CoversDirectory);
            var coverPath = Path.Combine(CoversDirectory, coverFileName);
            if (File.Exists(coverPath))
                return;

            try
            {
                using var page = document.GetPage(0);
                var scale = CoverWidth / page.Size.Width;
                var renderOptions = new PdfPageRenderOptions
                {
                    DestinationWidth = CoverWidth,
                    DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
                };

                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, renderOptions);
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                await using var fileStream = new FileStream(coverPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var outputStream = fileStream.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
                encoder.SetSoftwareBitmap(await decoder.GetSoftwareBitmapAsync());
                await encoder.FlushAsync();
            }
            catch
            {
                try { File.Delete(coverPath); } catch { }
            }
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path.Trim());

        private static string GetCoverFileName(string normalizedPath)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedPath.ToUpperInvariant()}|w{CoverWidth}"));
            return Convert.ToHexString(hash)[..16] + ".png";
        }

        private static void DeleteCoverFile(string? coverFileName)
        {
            if (string.IsNullOrEmpty(coverFileName))
                return;
            try
            {
                var path = Path.Combine(CoversDirectory, coverFileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class RecentFilesDto
        {
            public RecentFileEntry[] Entries { get; set; } = Array.Empty<RecentFileEntry>();
        }
    }
}
