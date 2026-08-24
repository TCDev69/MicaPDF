using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Input.Inking;

namespace MicaPDF
{
    public sealed class TextAnnotation
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public uint PageIndex { get; set; }
        public string Text { get; set; } = "Text";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 180;
        public double Height { get; set; } = 48;
        public double FontSize { get; set; } = 18;
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public Windows.UI.Color Color { get; set; } = Windows.UI.Color.FromArgb(255, 20, 20, 20);

        public TextAnnotation Snapshot() => new()
        {
            Id = Id,
            PageIndex = PageIndex,
            Text = Text,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            FontSize = FontSize,
            IsBold = IsBold,
            IsItalic = IsItalic,
            Color = Color
        };
    }

    public sealed class AnnotationStore
    {
        private readonly Dictionary<uint, InkStrokeContainer> _strokes = new();
        private readonly Dictionary<uint, List<TextAnnotation>> _texts = new();

        public InkStrokeContainer GetStrokes(uint pageIndex)
        {
            if (!_strokes.TryGetValue(pageIndex, out var container))
            {
                container = new InkStrokeContainer();
                _strokes[pageIndex] = container;
            }

            return container;
        }

        public List<TextAnnotation> GetTexts(uint pageIndex)
        {
            if (!_texts.TryGetValue(pageIndex, out var list))
            {
                list = new List<TextAnnotation>();
                _texts[pageIndex] = list;
            }

            return list;
        }

        public void AddText(TextAnnotation annotation)
        {
            GetTexts(annotation.PageIndex).Add(annotation);
        }

        public void RemoveText(TextAnnotation annotation)
        {
            if (_texts.TryGetValue(annotation.PageIndex, out var list))
                list.Remove(annotation);
        }

        public void RemoveStroke(uint pageIndex, InkStroke stroke)
        {
            var container = GetStrokes(pageIndex);
            foreach (var item in container.GetStrokes())
                item.Selected = false;
            stroke.Selected = true;
            container.DeleteSelected();
        }

        public void Clear()
        {
            _strokes.Clear();
            _texts.Clear();
        }

        public bool HasAny()
        {
            foreach (var container in _strokes.Values)
            {
                if (container.GetStrokes().Count > 0)
                    return true;
            }

            foreach (var list in _texts.Values)
            {
                if (list.Count > 0)
                    return true;
            }

            return false;
        }

        public InkStroke? HitTestStroke(uint pageIndex, Point point, double inflate = 8)
        {
            var rect = new Rect(point.X - inflate, point.Y - inflate, inflate * 2, inflate * 2);
            foreach (var stroke in GetStrokes(pageIndex).GetStrokes())
            {
                var bounds = stroke.BoundingRect;
                bounds.X -= inflate;
                bounds.Y -= inflate;
                bounds.Width += inflate * 2;
                bounds.Height += inflate * 2;
                if (bounds.X <= point.X && point.X <= bounds.X + bounds.Width &&
                    bounds.Y <= point.Y && point.Y <= bounds.Y + bounds.Height)
                {
                    return stroke;
                }
            }

            _ = rect;
            return null;
        }

        public TextAnnotation? HitTestText(uint pageIndex, Point point)
        {
            var list = GetTexts(pageIndex);
            // Topmost (last created) first.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var text = list[i];
                if (point.X >= text.X && point.X <= text.X + text.Width &&
                    point.Y >= text.Y && point.Y <= text.Y + text.Height)
                {
                    return text;
                }
            }

            return null;
        }
    }
}
