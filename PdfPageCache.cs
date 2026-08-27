using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MicaPDF
{
    public sealed class PdfPageCache
    {
        private readonly int _capacity;
        private readonly Dictionary<(uint Page, int ZoomKey), LinkedListNode<CacheEntry>> _map = new();
        private readonly LinkedList<CacheEntry> _lru = new();

        public PdfPageCache(int capacity = 48)
        {
            _capacity = capacity;
        }

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

        public void Set(uint page, int zoomKey, BitmapImage image)
        {
            var key = (page, zoomKey);
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Image = image;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = _lru.AddFirst(new CacheEntry(page, zoomKey, image));
            _map[key] = node;

            while (_map.Count > _capacity && _lru.Last != null)
            {
                var last = _lru.Last.Value;
                _map.Remove((last.Page, last.ZoomKey));
                _lru.RemoveLast();
            }
        }

        /// <summary>Drops entries whose zoom key is farther than <paramref name="keepDistance"/> from <paramref name="keepZoomKey"/>.</summary>
        public void TrimDistantZoom(int keepZoomKey, int keepDistance = 50)
        {
            var toRemove = new List<(uint Page, int ZoomKey)>();
            foreach (var key in _map.Keys)
            {
                if (System.Math.Abs(key.ZoomKey - keepZoomKey) > keepDistance)
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _map.Remove(key);
                }
            }
        }

        public void Clear()
        {
            _map.Clear();
            _lru.Clear();
        }

        private sealed class CacheEntry
        {
            public CacheEntry(uint page, int zoomKey, BitmapImage image)
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
