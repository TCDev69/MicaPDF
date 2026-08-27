using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MicaPDF
{
    public sealed class PdfPageCache
    {
        public const long DefaultByteBudget = 80L * 1024 * 1024;
        public const int DefaultTrimDistance = 8;

        private readonly int _capacity;
        private readonly long _byteBudget;
        private readonly Dictionary<(uint Page, int ZoomKey), LinkedListNode<CacheEntry>> _map = new();
        private readonly LinkedList<CacheEntry> _lru = new();
        private long _currentBytes;

        public PdfPageCache(int capacity = 48, long byteBudget = DefaultByteBudget)
        {
            _capacity = capacity;
            _byteBudget = byteBudget;
        }

        public long CurrentBytes => _currentBytes;
        public long ByteBudget => _byteBudget;

        public bool IsNearByteBudget(double fraction = 0.8) =>
            _byteBudget > 0 && _currentBytes >= _byteBudget * fraction;

        public bool TryGet(uint page, int zoomKey, out BitmapImage image)
        {
            if (_map.TryGetValue((page, zoomKey), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }

            image = null!;
            return false;
        }

        public void Set(uint page, int zoomKey, BitmapImage image, long estimatedBytes)
        {
            var key = (page, zoomKey);
            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.EstimatedBytes;
                existing.Value.Image = image;
                existing.Value.EstimatedBytes = estimatedBytes;
                _currentBytes += estimatedBytes;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = _lru.AddFirst(new CacheEntry(page, zoomKey, image, estimatedBytes));
            _map[key] = node;
            _currentBytes += estimatedBytes;

            while ((_map.Count > _capacity || _currentBytes > _byteBudget) && _lru.Last != null)
                EvictLast();
        }

        /// <summary>Drops entries whose zoom key is farther than <paramref name="keepDistance"/> from <paramref name="keepZoomKey"/>.</summary>
        public void TrimDistantZoom(int keepZoomKey, int keepDistance = DefaultTrimDistance)
        {
            var toRemove = new List<(uint Page, int ZoomKey)>();
            foreach (var key in _map.Keys)
            {
                if (System.Math.Abs(key.ZoomKey - keepZoomKey) > keepDistance)
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                RemoveKey(key);
        }

        public void Clear()
        {
            foreach (var node in _lru)
                ReleaseImage(node.Image);
            _map.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }

        private void EvictLast()
        {
            if (_lru.Last == null) return;
            var last = _lru.Last.Value;
            _map.Remove((last.Page, last.ZoomKey));
            _lru.RemoveLast();
            _currentBytes -= last.EstimatedBytes;
            ReleaseImage(last.Image);
        }

        private void RemoveKey((uint Page, int ZoomKey) key)
        {
            if (!_map.TryGetValue(key, out var node)) return;
            _currentBytes -= node.Value.EstimatedBytes;
            ReleaseImage(node.Value.Image);
            _lru.Remove(node);
            _map.Remove(key);
        }

        private static void ReleaseImage(BitmapImage? image)
        {
            if (image == null) return;
            try { image.UriSource = null; } catch { /* stream-backed images */ }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(uint page, int zoomKey, BitmapImage image, long estimatedBytes)
            {
                Page = page;
                ZoomKey = zoomKey;
                Image = image;
                EstimatedBytes = estimatedBytes;
            }

            public uint Page { get; }
            public int ZoomKey { get; }
            public BitmapImage Image { get; set; }
            public long EstimatedBytes { get; set; }
        }
    }
}
