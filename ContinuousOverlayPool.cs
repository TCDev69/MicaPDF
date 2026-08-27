using System.Collections.Generic;

namespace MicaPDF
{
    /// <summary>Recycles <see cref="AnnotationOverlay"/> instances for virtualized continuous scroll.</summary>
    public sealed class ContinuousOverlayPool
    {
        private const int MaxPoolSize = 6;
        private readonly Stack<AnnotationOverlay> _available = new();

        public AnnotationOverlay Rent()
        {
            if (_available.Count > 0)
                return _available.Pop();
            return new AnnotationOverlay();
        }

        public void Return(AnnotationOverlay? overlay)
        {
            if (overlay == null) return;
            if (_available.Count >= MaxPoolSize)
                return;
            overlay.SetSearchHighlight(null);
            _available.Push(overlay);
        }

        public void Clear()
        {
            _available.Clear();
        }
    }
}
