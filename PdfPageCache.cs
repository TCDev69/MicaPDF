using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace MicaPDF
{
    /// <summary>
    /// Two-tier page cache: compressed PNG bytes (main budget) + a small LRU of
    /// decoded BitmapImage for currently visible pages.
    /// PdfPage.RenderToStreamAsync already emits PNG, so we store those bytes as-is.
    /// </summary>
    public sealed class PdfPageCache
    {
        public const long DefaultByteBudget = 80L * 1024 * 1024;
        public const int DefaultTrimDistance = 8;
        public const int DefaultDecodedCapacity = 6;

        private readonly int _capacity;
        private readonly long _byteBudget;
        private readonly int _decodedCapacity;
        private readonly Dictionary<(uint Page, int ZoomKey), LinkedListNode<CacheEntry>> _map = new();
        private readonly LinkedList<CacheEntry> _lru = new();
        private readonly Dictionary<(uint Page, int ZoomKey), LinkedListNode<DecodedEntry>> _decodedMap = new();
        private readonly LinkedList<DecodedEntry> _decodedLru = new();
        private long _currentBytes;

        public PdfPageCache(
            int capacity = 48,
            long byteBudget = DefaultByteBudget,
            int decodedCapacity = DefaultDecodedCapacity)
        {
            _capacity = capacity;
            _byteBudget = byteBudget;
            _decodedCapacity = Math.Max(1, decodedCapacity);
        }

        public long CurrentBytes => _currentBytes;
        public long ByteBudget => _byteBudget;

        public bool IsNearByteBudget(double fraction = 0.8) =>
            _byteBudget > 0 && _currentBytes >= _byteBudget * fraction;

        /// <summary>Returns a decoded BitmapImage, decoding from compressed PNG on miss of the hot tier.</summary>
        public async Task<BitmapImage?> TryGetBitmapAsync(uint page, int zoomKey)
        {
            var key = (page, zoomKey);
            if (_decodedMap.TryGetValue(key, out var decodedNode))
            {
                _decodedLru.Remove(decodedNode);
                _decodedLru.AddFirst(decodedNode);
                TouchCompressed(key);
                return decodedNode.Value.Image;
            }

            if (!_map.TryGetValue(key, out var compressedNode))
                return null;

            _lru.Remove(compressedNode);
            _lru.AddFirst(compressedNode);
            var image = await DecodeToBitmapAsync(compressedNode.Value.PngData);
            PutDecoded(key, image);
            return image;
        }

        public void Set(uint page, int zoomKey, byte[] pngData, BitmapImage? decoded = null)
        {
            var key = (page, zoomKey);
            long size = pngData.LongLength;

            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.CompressedBytes;
                existing.Value.PngData = pngData;
                existing.Value.CompressedBytes = size;
                _currentBytes += size;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
            }
            else
            {
                var node = _lru.AddFirst(new CacheEntry(page, zoomKey, pngData, size));
                _map[key] = node;
                _currentBytes += size;
            }

            if (decoded != null)
                PutDecoded(key, decoded);

            while ((_map.Count > _capacity || _currentBytes > _byteBudget) && _lru.Last != null)
                EvictLastCompressed();
        }

        /// <summary>Drops entries whose zoom key is farther than <paramref name="keepDistance"/> from <paramref name="keepZoomKey"/>.</summary>
        public void TrimDistantZoom(int keepZoomKey, int keepDistance = DefaultTrimDistance)
        {
            var toRemove = new List<(uint Page, int ZoomKey)>();
            foreach (var key in _map.Keys)
            {
                if (Math.Abs(key.ZoomKey - keepZoomKey) > keepDistance)
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                RemoveKey(key);
        }

        public void Clear()
        {
            foreach (var node in _decodedLru)
                ReleaseImage(node.Image);
            _decodedMap.Clear();
            _decodedLru.Clear();
            _map.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }

        public static async Task<byte[]> CopyStreamToBytesAsync(IRandomAccessStream stream)
        {
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            return bytes;
        }

        public static async Task<BitmapImage> DecodeToBitmapAsync(byte[] pngData)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngData.AsBuffer());
            stream.Seek(0);
            var bitmapImage = new BitmapImage();
            await bitmapImage.SetSourceAsync(stream);
            return bitmapImage;
        }

        private void TouchCompressed((uint Page, int ZoomKey) key)
        {
            if (!_map.TryGetValue(key, out var node)) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        private void PutDecoded((uint Page, int ZoomKey) key, BitmapImage image)
        {
            if (_decodedMap.TryGetValue(key, out var existing))
            {
                ReleaseImage(existing.Value.Image);
                existing.Value.Image = image;
                _decodedLru.Remove(existing);
                _decodedLru.AddFirst(existing);
            }
            else
            {
                var node = _decodedLru.AddFirst(new DecodedEntry(key.Page, key.ZoomKey, image));
                _decodedMap[key] = node;
            }

            while (_decodedMap.Count > _decodedCapacity && _decodedLru.Last != null)
                EvictLastDecoded();
        }

        private void EvictLastCompressed()
        {
            if (_lru.Last == null) return;
            var last = _lru.Last.Value;
            var key = (last.Page, last.ZoomKey);
            _map.Remove(key);
            _lru.RemoveLast();
            _currentBytes -= last.CompressedBytes;
            RemoveDecoded(key);
        }

        private void EvictLastDecoded()
        {
            if (_decodedLru.Last == null) return;
            var last = _decodedLru.Last.Value;
            _decodedMap.Remove((last.Page, last.ZoomKey));
            _decodedLru.RemoveLast();
            ReleaseImage(last.Image);
        }

        private void RemoveKey((uint Page, int ZoomKey) key)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _currentBytes -= node.Value.CompressedBytes;
                _lru.Remove(node);
                _map.Remove(key);
            }

            RemoveDecoded(key);
        }

        private void RemoveDecoded((uint Page, int ZoomKey) key)
        {
            if (!_decodedMap.TryGetValue(key, out var node)) return;
            ReleaseImage(node.Value.Image);
            _decodedLru.Remove(node);
            _decodedMap.Remove(key);
        }

        private static void ReleaseImage(BitmapImage? image)
        {
            if (image == null) return;
            try { image.UriSource = null; } catch { /* stream-backed images */ }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(uint page, int zoomKey, byte[] pngData, long compressedBytes)
            {
                Page = page;
                ZoomKey = zoomKey;
                PngData = pngData;
                CompressedBytes = compressedBytes;
            }

            public uint Page { get; }
            public int ZoomKey { get; }
            public byte[] PngData { get; set; }
            public long CompressedBytes { get; set; }
        }

        private sealed class DecodedEntry
        {
            public DecodedEntry(uint page, int zoomKey, BitmapImage image)
            {
                Page = page;
                ZoomKey = zoomKey;
                Image = image;
            }

            public uint Page { get; }
            public int ZoomKey { get; }
            public BitmapImage Image { get; set; }
        }
    }
}
