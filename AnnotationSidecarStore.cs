using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Input.Inking;

namespace MicaPDF
{
    /// <summary>
    /// Persists annotations under %LocalAppData%\MicaPDF\annotations keyed by path+size hash.
    /// </summary>
    public static class AnnotationSidecarStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        public static string StoreDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MicaPDF",
                    "annotations");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string KeyFor(string path, long fileLength) =>
            AnnotationKeys.KeyFor(path, fileLength);

        public static async Task SaveAsync(string pdfPath, AnnotationStore store)
        {
            try
            {
                if (!File.Exists(pdfPath) || !store.HasAny())
                {
                    if (File.Exists(pdfPath) && !store.HasAny())
                        Delete(pdfPath);
                    return;
                }

                var length = new FileInfo(pdfPath).Length;
                var key = KeyFor(pdfPath, length);
                var dto = await BuildDtoFromStore(store, pdfPath, length);
                var json = JsonSerializer.Serialize(dto, JsonOptions);
                var path = Path.Combine(StoreDirectory, key + ".json");
                await File.WriteAllTextAsync(path, json);
                AppLog.Info($"Annotations saved for {Path.GetFileName(pdfPath)}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to save annotation sidecar", ex);
            }
        }

        public static async Task<bool> TryLoadAsync(string pdfPath, AnnotationStore store)
        {
            try
            {
                if (!File.Exists(pdfPath)) return false;
                var length = new FileInfo(pdfPath).Length;
                var key = KeyFor(pdfPath, length);
                var path = Path.Combine(StoreDirectory, key + ".json");
                if (!File.Exists(path)) return false;

                var json = await File.ReadAllTextAsync(path);
                var dto = JsonSerializer.Deserialize<SidecarDto>(json, JsonOptions);
                if (dto == null) return false;
                if (!string.Equals(dto.Path, pdfPath, StringComparison.OrdinalIgnoreCase) || dto.Length != length)
                    return false;

                store.Clear();
                await ApplyAsync(dto, store);
                AppLog.Info($"Annotations restored for {Path.GetFileName(pdfPath)}");
                return store.HasAny();
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to load annotation sidecar", ex);
                return false;
            }
        }

        public static void Delete(string pdfPath)
        {
            try
            {
                if (!File.Exists(pdfPath)) return;
                var length = new FileInfo(pdfPath).Length;
                var key = KeyFor(pdfPath, length);
                var path = Path.Combine(StoreDirectory, key + ".json");
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to delete annotation sidecar", ex);
            }
        }

        private static async Task<SidecarDto> BuildDtoFromStore(AnnotationStore store, string pdfPath, long length)
        {
            var dto = new SidecarDto { Path = pdfPath, Length = length, SavedUtc = DateTime.UtcNow };

            foreach (var page in store.EnumeratePageIndices())
            {
                var pageDto = new PageDto { PageIndex = page };
                foreach (var text in store.GetTexts(page))
                {
                    pageDto.Texts.Add(new TextDto
                    {
                        Id = text.Id,
                        Text = text.Text,
                        X = text.X,
                        Y = text.Y,
                        Width = text.Width,
                        Height = text.Height,
                        FontSize = text.FontSize,
                        IsBold = text.IsBold,
                        IsItalic = text.IsItalic,
                        A = text.Color.A,
                        R = text.Color.R,
                        G = text.Color.G,
                        B = text.Color.B
                    });
                }

                var container = store.GetStrokes(page);
                if (container.GetStrokes().Count > 0)
                {
                    using var ms = new InMemoryRandomAccessStream();
                    await container.SaveAsync(ms).AsTask();
                    ms.Seek(0);
                    var bytes = new byte[ms.Size];
                    var reader = new DataReader(ms.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)ms.Size);
                    reader.ReadBytes(bytes);
                    reader.Dispose();
                    pageDto.InkBase64 = Convert.ToBase64String(bytes);
                }

                if (pageDto.Texts.Count > 0 || !string.IsNullOrEmpty(pageDto.InkBase64))
                    dto.Pages.Add(pageDto);
            }

            return dto;
        }

        private static async Task ApplyAsync(SidecarDto dto, AnnotationStore store)
        {
            foreach (var page in dto.Pages)
            {
                foreach (var t in page.Texts)
                {
                    store.AddText(new TextAnnotation
                    {
                        Id = string.IsNullOrEmpty(t.Id) ? Guid.NewGuid().ToString() : t.Id,
                        PageIndex = page.PageIndex,
                        Text = t.Text ?? "",
                        X = t.X,
                        Y = t.Y,
                        Width = t.Width,
                        Height = t.Height,
                        FontSize = t.FontSize,
                        IsBold = t.IsBold,
                        IsItalic = t.IsItalic,
                        Color = Windows.UI.Color.FromArgb(t.A, t.R, t.G, t.B)
                    });
                }

                if (!string.IsNullOrEmpty(page.InkBase64))
                {
                    var bytes = Convert.FromBase64String(page.InkBase64);
                    using var ms = new InMemoryRandomAccessStream();
                    await ms.WriteAsync(bytes.AsBuffer());
                    ms.Seek(0);
                    await store.GetStrokes(page.PageIndex).LoadAsync(ms).AsTask();
                }
            }
        }

        private sealed class SidecarDto
        {
            public string Path { get; set; } = "";
            public long Length { get; set; }
            public DateTime SavedUtc { get; set; }
            public List<PageDto> Pages { get; set; } = new();
        }

        private sealed class PageDto
        {
            public uint PageIndex { get; set; }
            public string? InkBase64 { get; set; }
            public List<TextDto> Texts { get; set; } = new();
        }

        private sealed class TextDto
        {
            public string Id { get; set; } = "";
            public string? Text { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double FontSize { get; set; }
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
            public byte A { get; set; }
            public byte R { get; set; }
            public byte G { get; set; }
            public byte B { get; set; }
        }
    }
}
