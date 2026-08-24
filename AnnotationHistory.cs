using System;
using System.Collections.Generic;
using Windows.UI.Input.Inking;

namespace MicaPDF
{
    public interface IAnnotationCommand
    {
        void Undo();
        void Redo();
    }

    public sealed class AnnotationHistory
    {
        public const int Limit = 50;

        private readonly List<IAnnotationCommand> _undo = new();
        private readonly List<IAnnotationCommand> _redo = new();

        public bool IsApplying { get; private set; }
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public event EventHandler? Changed;

        public void Push(IAnnotationCommand command)
        {
            if (IsApplying || command == null) return;
            _undo.Add(command);
            if (_undo.Count > Limit)
                _undo.RemoveAt(0);
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var command = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            IsApplying = true;
            try { command.Undo(); }
            finally { IsApplying = false; }
            _redo.Add(command);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var command = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            IsApplying = true;
            try { command.Redo(); }
            finally { IsApplying = false; }
            _undo.Add(command);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class AddStrokeCommand : IAnnotationCommand
    {
        private readonly AnnotationStore _store;
        private readonly uint _page;
        private InkStroke _stroke;

        public AddStrokeCommand(AnnotationStore store, uint page, InkStroke stroke)
        {
            _store = store;
            _page = page;
            _stroke = stroke;
        }

        public void Undo()
        {
            _store.RemoveStroke(_page, _stroke);
        }

        public void Redo()
        {
            _stroke = _stroke.Clone();
            _store.GetStrokes(_page).AddStroke(_stroke);
        }
    }

    public sealed class RemoveStrokeCommand : IAnnotationCommand
    {
        private readonly AnnotationStore _store;
        private readonly uint _page;
        private readonly InkStroke _prototype;
        private InkStroke _live;

        public RemoveStrokeCommand(AnnotationStore store, uint page, InkStroke stroke)
        {
            _store = store;
            _page = page;
            _prototype = stroke.Clone();
            _live = stroke;
        }

        public void Undo()
        {
            _live = _prototype.Clone();
            _store.GetStrokes(_page).AddStroke(_live);
        }

        public void Redo()
        {
            _store.RemoveStroke(_page, _live);
        }
    }

    public sealed class TranslateStrokeCommand : IAnnotationCommand
    {
        private readonly InkStroke _stroke;
        private readonly float _dx;
        private readonly float _dy;

        public TranslateStrokeCommand(InkStroke stroke, float dx, float dy)
        {
            _stroke = stroke;
            _dx = dx;
            _dy = dy;
        }

        public void Undo() => Translate(-_dx, -_dy);

        public void Redo() => Translate(_dx, _dy);

        private void Translate(float dx, float dy)
        {
            _stroke.PointTransform *= System.Numerics.Matrix3x2.CreateTranslation(dx, dy);
        }
    }

    public sealed class AddTextCommand : IAnnotationCommand
    {
        private readonly AnnotationStore _store;
        private readonly TextAnnotation _text;

        public AddTextCommand(AnnotationStore store, TextAnnotation text)
        {
            _store = store;
            _text = text;
        }

        public void Undo() => _store.RemoveText(_text);

        public void Redo()
        {
            if (!_store.GetTexts(_text.PageIndex).Contains(_text))
                _store.AddText(_text);
        }
    }

    public sealed class RemoveTextCommand : IAnnotationCommand
    {
        private readonly AnnotationStore _store;
        private readonly TextAnnotation _text;

        public RemoveTextCommand(AnnotationStore store, TextAnnotation text)
        {
            _store = store;
            _text = text;
        }

        public void Undo()
        {
            if (!_store.GetTexts(_text.PageIndex).Contains(_text))
                _store.AddText(_text);
        }

        public void Redo() => _store.RemoveText(_text);
    }

    public sealed class TranslateTextCommand : IAnnotationCommand
    {
        private readonly TextAnnotation _text;
        private readonly double _dx;
        private readonly double _dy;

        public TranslateTextCommand(TextAnnotation text, double dx, double dy)
        {
            _text = text;
            _dx = dx;
            _dy = dy;
        }

        public void Undo()
        {
            _text.X -= _dx;
            _text.Y -= _dy;
        }

        public void Redo()
        {
            _text.X += _dx;
            _text.Y += _dy;
        }
    }

    public sealed class EditTextCommand : IAnnotationCommand
    {
        private readonly TextAnnotation _text;
        private readonly string _oldText;
        private readonly string _newText;

        public EditTextCommand(TextAnnotation text, string oldText, string newText)
        {
            _text = text;
            _oldText = oldText;
            _newText = newText;
        }

        public void Undo() => _text.Text = _oldText;

        public void Redo() => _text.Text = _newText;
    }

    public sealed class StyleTextCommand : IAnnotationCommand
    {
        private readonly TextAnnotation _text;
        private readonly double _oldSize, _newSize;
        private readonly bool _oldBold, _newBold, _oldItalic, _newItalic;
        private readonly Windows.UI.Color _oldColor, _newColor;

        public StyleTextCommand(TextAnnotation text, TextAnnotation before)
        {
            _text = text;
            _oldSize = before.FontSize;
            _newSize = text.FontSize;
            _oldBold = before.IsBold;
            _newBold = text.IsBold;
            _oldItalic = before.IsItalic;
            _newItalic = text.IsItalic;
            _oldColor = before.Color;
            _newColor = text.Color;
        }

        public void Undo() => Apply(_oldSize, _oldBold, _oldItalic, _oldColor);

        public void Redo() => Apply(_newSize, _newBold, _newItalic, _newColor);

        private void Apply(double size, bool bold, bool italic, Windows.UI.Color color)
        {
            _text.FontSize = size;
            _text.IsBold = bold;
            _text.IsItalic = italic;
            _text.Color = color;
        }
    }
}
