using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace MicaPDF
{
    public enum AnnotationTool
    {
        Select,
        Pen,
        Eraser,
        Text
    }

    public sealed class AnnotationOverlay : UserControl
    {
        private readonly Grid _root;
        private readonly CanvasControl _inkCanvas;
        private readonly Canvas _textLayer;
        private readonly InkStrokeBuilder _strokeBuilder = new();
        private readonly List<InkPoint> _wetPoints = new();
        private readonly List<PdfGlyph> _selectedGlyphs = new();

        private AnnotationStore? _store;
        private AnnotationHistory? _history;
        private PdfTextIndex? _textIndex;
        private uint _pageIndex;
        private double _pageWidth = 1;
        private double _pageHeight = 1;
        private AnnotationTool _tool = AnnotationTool.Select;
        private bool _isPointerDown;
        private uint _activePointerId;
        private InkStroke? _selectedStroke;
        private TextAnnotation? _selectedText;
        private TextAnnotation? _editingText;
        private Point _lastDragPos;
        private Point _moveOrigin;
        private TextBox? _editingBox;
        private string _editSnapshot = "";
        private bool _selectingPdfText;
        private Point _pdfSelectStart;
        private Rect? _pdfSelectRect;

        public double DefaultFontSize { get; set; } = 18;
        public bool DefaultBold { get; set; }
        public bool DefaultItalic { get; set; }
        public Color DefaultTextColor { get; set; } = Color.FromArgb(255, 20, 20, 20);

        public event EventHandler? AnnotationsChanged;
        public event EventHandler? SelectionChanged;

        public TextAnnotation? SelectedText => _selectedText;
        public string SelectedPdfText => PdfTextIndex.BuildText(_selectedGlyphs);

        public AnnotationOverlay()
        {
            _inkCanvas = new CanvasControl
            {
                ClearColor = Microsoft.UI.Colors.Transparent,
                IsHitTestVisible = false
            };
            _inkCanvas.Draw += InkCanvas_Draw;

            _textLayer = new Canvas
            {
                Background = null,
                IsHitTestVisible = false
            };

            _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            _root.Children.Add(_inkCanvas);
            _root.Children.Add(_textLayer);
            _root.PointerPressed += OnPointerPressed;
            _root.PointerMoved += OnPointerMoved;
            _root.PointerReleased += OnPointerReleased;
            _root.PointerCanceled += OnPointerReleased;
            _root.PointerCaptureLost += OnPointerReleased;
            _root.PointerExited += OnPointerExited;
            _root.DoubleTapped += OnDoubleTapped;
            SizeChanged += (_, _) => Refresh();
            Content = _root;

            var copyItem = new MenuFlyoutItem { Text = Loc.Get("overlay.copy") };
            copyItem.Click += (_, _) => CopyPdfSelection();
            var flyout = new MenuFlyout { Items = { copyItem } };
            flyout.Opening += (_, _) =>
            {
                copyItem.Text = Loc.Get("overlay.copy");
                copyItem.IsEnabled = !string.IsNullOrEmpty(SelectedPdfText);
            };
            ContextFlyout = flyout;
        }

        public void RefreshLocalizedUi()
        {
            if (ContextFlyout is MenuFlyout flyout && flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault() is { } copy)
                copy.Text = Loc.Get("overlay.copy");
        }

        public double OverlayScale
        {
            get
            {
                var w = ActualWidth > 1 ? ActualWidth : Width;
                if (double.IsNaN(w) || double.IsInfinity(w) || w <= 1 || _pageWidth <= 1)
                    return 1;
                var scale = w / _pageWidth;
                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                    return 1;
                return scale;
            }
        }

        public void Attach(AnnotationStore store, uint pageIndex, double pageWidth, double pageHeight)
        {
            _store = store;
            _pageIndex = pageIndex;
            _pageWidth = Math.Max(1, pageWidth);
            _pageHeight = Math.Max(1, pageHeight);
            Refresh();
        }

        public void SetHistory(AnnotationHistory history) => _history = history;

        public void SetTextIndex(PdfTextIndex? index)
        {
            _textIndex = index;
            _selectedGlyphs.Clear();
            _inkCanvas.Invalidate();
        }

        public void SetTool(AnnotationTool tool)
        {
            _tool = tool;
            _selectedStroke = null;
            _selectedText = null;
            _editingText = null;
            _wetPoints.Clear();
            _selectedGlyphs.Clear();
            _selectingPdfText = false;
            _pdfSelectRect = null;
            _isPointerDown = false;
            _inkCanvas.Invalidate();
            RebuildTextLayer();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetDrawingAttributes(InkDrawingAttributes attributes)
        {
            _strokeBuilder.SetDefaultDrawingAttributes(attributes);
        }

        public void Refresh()
        {
            if (double.IsNaN(Width) || double.IsNaN(Height) ||
                (ActualWidth <= 0 && (double.IsNaN(Width) || Width <= 0)))
            {
                return;
            }
            _inkCanvas.Invalidate();
            RebuildTextLayer();
        }

        public bool CopyPdfSelection()
        {
            var text = SelectedPdfText;
            if (string.IsNullOrEmpty(text)) return false;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }

        public bool DeleteSelection()
        {
            if (_store == null) return false;
            var deleted = false;
            if (_selectedStroke != null)
            {
                _history?.Push(new RemoveStrokeCommand(_store, _pageIndex, _selectedStroke));
                _store.RemoveStroke(_pageIndex, _selectedStroke);
                _selectedStroke = null;
                deleted = true;
                _inkCanvas.Invalidate();
            }

            if (_selectedText != null)
            {
                _history?.Push(new RemoveTextCommand(_store, _selectedText));
                _store.RemoveText(_selectedText);
                _selectedText = null;
                _editingText = null;
                deleted = true;
                RebuildTextLayer();
            }

            if (deleted)
            {
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            return deleted;
        }

        public void ApplyStyleToSelectedText(double? fontSize = null, bool? bold = null, bool? italic = null, Color? color = null)
        {
            if (_selectedText == null) return;
            var before = _selectedText.Snapshot();
            if (fontSize.HasValue) _selectedText.FontSize = fontSize.Value;
            if (bold.HasValue) _selectedText.IsBold = bold.Value;
            if (italic.HasValue) _selectedText.IsItalic = italic.Value;
            if (color.HasValue) _selectedText.Color = color.Value;
            FitTextHeight(_selectedText);
            _history?.Push(new StyleTextCommand(_selectedText, before));
            RebuildTextLayer();
            AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }

        private Point ToPage(Point overlay) => new(overlay.X / OverlayScale, overlay.Y / OverlayScale);

        private float PageInflate(double overlayPx) => (float)(overlayPx / OverlayScale);

        private void InkCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_store == null) return;
            var scale = (float)OverlayScale;
            args.DrawingSession.Transform = Matrix3x2.CreateScale(scale);

            foreach (var glyph in _selectedGlyphs)
                args.DrawingSession.FillRectangle(ToWin2d(glyph.Bounds), Color.FromArgb(90, 51, 153, 255));

            if (_pdfSelectRect is Rect r)
                args.DrawingSession.DrawRectangle(ToWin2d(r), Color.FromArgb(180, 51, 153, 255), 1f / scale);

            foreach (var stroke in _store.GetStrokes(_pageIndex).GetStrokes())
                DrawStroke(args.DrawingSession, stroke);

            if (_wetPoints.Count > 1)
            {
                try
                {
                    var wet = _strokeBuilder.CreateStrokeFromInkPoints(_wetPoints, Matrix3x2.Identity);
                    DrawStroke(args.DrawingSession, wet);
                }
                catch { }
            }

            if (_selectedStroke != null)
            {
                var bounds = _selectedStroke.BoundingRect;
                args.DrawingSession.DrawRectangle(bounds, Color.FromArgb(180, 0, 120, 215), 2f / scale);
            }

            args.DrawingSession.Transform = Matrix3x2.Identity;
        }

        private static Windows.Foundation.Rect ToWin2d(Rect r) => r;

        public static void DrawStroke(CanvasDrawingSession ds, InkStroke stroke)
        {
            var segments = stroke.GetRenderingSegments();
            if (segments.Count == 0) return;

            using var builder = new CanvasPathBuilder(ds);
            var started = false;
            foreach (var seg in segments)
            {
                var pos = new Vector2((float)seg.Position.X, (float)seg.Position.Y);
                if (!started)
                {
                    builder.BeginFigure(pos);
                    started = true;
                }
                else
                {
                    builder.AddCubicBezier(
                        new Vector2((float)seg.BezierControlPoint1.X, (float)seg.BezierControlPoint1.Y),
                        new Vector2((float)seg.BezierControlPoint2.X, (float)seg.BezierControlPoint2.Y),
                        pos);
                }
            }

            builder.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(builder);
            var attr = stroke.DrawingAttributes;
            var color = attr.Color;
            if (attr.DrawAsHighlighter)
                color = Color.FromArgb(90, color.R, color.G, color.B);
            ds.DrawGeometry(geometry, color, (float)Math.Max(0.5, attr.Size.Width));
        }

        private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_tool != AnnotationTool.Select || _store == null) return;
            var pos = ToPage(e.GetPosition(_root));
            var text = _store.HitTestText(_pageIndex, pos);
            if (text == null) return;
            _selectedText = text;
            _selectedStroke = null;
            _editingText = text;
            _editSnapshot = text.Text;
            RebuildTextLayer();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _editingBox?.Focus(FocusState.Programmatic);
            e.Handled = true;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_store == null) return;
            var point = e.GetCurrentPoint(_root);
            var overlayPos = point.Position;
            var pos = ToPage(overlayPos);
            var isEraser = point.Properties.IsEraser || _tool == AnnotationTool.Eraser;

            if (_tool == AnnotationTool.Select && !isEraser)
            {
                if (_editingText != null && _store.HitTestText(_pageIndex, pos)?.Id != _editingText.Id)
                    CommitTextEdit();

                var hitText = _store.HitTestText(_pageIndex, pos);
                var hitStroke = hitText == null ? _store.HitTestStroke(_pageIndex, pos, PageInflate(12)) : null;
                if (hitText != null || hitStroke != null)
                {
                    _selectedGlyphs.Clear();
                    _pdfSelectRect = null;
                    _isPointerDown = true;
                    _activePointerId = e.Pointer.PointerId;
                    _root.CapturePointer(e.Pointer);
                    BeginSelect(pos);
                    e.Handled = true;
                    return;
                }

                var glyph = _textIndex?.HitTest(_pageIndex, pos);
                if (glyph != null)
                {
                    _selectedText = null;
                    _selectedStroke = null;
                    _selectingPdfText = true;
                    _pdfSelectStart = pos;
                    _pdfSelectRect = new Rect(pos, new Size(0, 0));
                    _selectedGlyphs.Clear();
                    _selectedGlyphs.Add(glyph);
                    _isPointerDown = true;
                    _activePointerId = e.Pointer.PointerId;
                    _root.CapturePointer(e.Pointer);
                    _inkCanvas.Invalidate();
                    RebuildTextLayer();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                }

                _selectedText = null;
                _selectedStroke = null;
                _selectedGlyphs.Clear();
                _pdfSelectRect = null;
                _inkCanvas.Invalidate();
                RebuildTextLayer();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (isEraser)
            {
                _isPointerDown = true;
                _activePointerId = e.Pointer.PointerId;
                if (point.PointerDeviceType != PointerDeviceType.Pen)
                    _root.CapturePointer(e.Pointer);
                EraseAt(pos);
                e.Handled = true;
                return;
            }

            if (_tool == AnnotationTool.Text)
            {
                _isPointerDown = true;
                _activePointerId = e.Pointer.PointerId;
                CreateTextAt(pos);
                e.Handled = true;
                return;
            }

            if (_tool != AnnotationTool.Pen)
                return;

            if (point.PointerDeviceType != PointerDeviceType.Mouse && !point.IsInContact)
                return;

            _isPointerDown = true;
            _activePointerId = e.Pointer.PointerId;
            if (point.PointerDeviceType != PointerDeviceType.Pen)
                _root.CapturePointer(e.Pointer);

            _wetPoints.Clear();
            _wetPoints.Add(CreateInkPoint(point, pos));
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_root);
            var pos = ToPage(point.Position);
            var isEraser = point.Properties.IsEraser || (_tool == AnnotationTool.Eraser && _isPointerDown);

            if (_tool == AnnotationTool.Pen && _isPointerDown && !point.IsInContact && point.PointerDeviceType != PointerDeviceType.Mouse)
            {
                EndStroke(e.Pointer);
                return;
            }

            if (!_isPointerDown || _store == null) return;
            if (e.Pointer.PointerId != _activePointerId && _activePointerId != 0) return;

            if (isEraser)
            {
                EraseAt(pos);
                e.Handled = true;
                return;
            }

            switch (_tool)
            {
                case AnnotationTool.Pen:
                    if (point.PointerDeviceType != PointerDeviceType.Mouse && !point.IsInContact)
                        return;
                    _wetPoints.Add(CreateInkPoint(point, pos));
                    _inkCanvas.Invalidate();
                    break;
                case AnnotationTool.Select:
                    if (_selectingPdfText)
                    {
                        _pdfSelectRect = NormalizeRect(_pdfSelectStart, pos);
                        _selectedGlyphs.Clear();
                        _selectedGlyphs.AddRange(_textIndex?.GlyphsInRect(_pageIndex, _pdfSelectRect.Value) ?? new List<PdfGlyph>());
                        _inkCanvas.Invalidate();
                    }
                    else
                    {
                        DragSelection(pos);
                    }
                    break;
            }

            e.Handled = true;
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_tool == AnnotationTool.Pen && _isPointerDown)
                EndStroke(e.Pointer);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPointerDown) return;
            if (_activePointerId != 0 && e.Pointer.PointerId != _activePointerId) return;
            EndStroke(e.Pointer);
            e.Handled = true;
        }

        private void EndStroke(Pointer pointer)
        {
            if (!_isPointerDown) return;
            _isPointerDown = false;
            _activePointerId = 0;

            if (_selectingPdfText)
            {
                _selectingPdfText = false;
                _pdfSelectRect = null;
                _inkCanvas.Invalidate();
            }
            else if (_tool == AnnotationTool.Select)
            {
                CommitMove();
            }
            else if (_tool == AnnotationTool.Pen && _store != null && _wetPoints.Count > 1)
            {
                try
                {
                    var stroke = _strokeBuilder.CreateStrokeFromInkPoints(_wetPoints, Matrix3x2.Identity);
                    _store.GetStrokes(_pageIndex).AddStroke(stroke);
                    _history?.Push(new AddStrokeCommand(_store, _pageIndex, stroke));
                    AnnotationsChanged?.Invoke(this, EventArgs.Empty);
                }
                catch { }
            }

            _wetPoints.Clear();
            try { _root.ReleasePointerCapture(pointer); } catch { }
            _inkCanvas.Invalidate();
        }

        private static InkPoint CreateInkPoint(PointerPoint point, Point pagePos)
        {
            var pressure = point.Properties.Pressure;
            if (pressure <= 0) pressure = 0.5f;
            return new InkPoint(pagePos, pressure);
        }

        private void EraseAt(Point pos)
        {
            if (_store == null) return;

            var hit = _store.HitTestStroke(_pageIndex, pos, PageInflate(20));
            if (hit != null)
            {
                _history?.Push(new RemoveStrokeCommand(_store, _pageIndex, hit));
                _store.RemoveStroke(_pageIndex, hit);
                if (ReferenceEquals(_selectedStroke, hit))
                    _selectedStroke = null;
                _inkCanvas.Invalidate();
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var text = _store.HitTestText(_pageIndex, pos);
            if (text != null)
            {
                if (_selectedText?.Id == text.Id) _selectedText = null;
                if (_editingText?.Id == text.Id) _editingText = null;
                _history?.Push(new RemoveTextCommand(_store, text));
                _store.RemoveText(text);
                RebuildTextLayer();
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void BeginSelect(Point pos)
        {
            if (_store == null) return;
            _selectedText = _store.HitTestText(_pageIndex, pos);
            _selectedStroke = _selectedText == null ? _store.HitTestStroke(_pageIndex, pos, PageInflate(12)) : null;
            _lastDragPos = pos;
            _moveOrigin = pos;
            _inkCanvas.Invalidate();
            RebuildTextLayer();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DragSelection(Point pos)
        {
            var dx = pos.X - _lastDragPos.X;
            var dy = pos.Y - _lastDragPos.Y;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
            _lastDragPos = pos;

            if (_selectedStroke != null)
            {
                _selectedStroke.PointTransform =
                    _selectedStroke.PointTransform * Matrix3x2.CreateTranslation((float)dx, (float)dy);
                _inkCanvas.Invalidate();
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_selectedText != null && _editingText?.Id != _selectedText.Id)
            {
                _selectedText.X += dx;
                _selectedText.Y += dy;
                foreach (var child in _textLayer.Children)
                {
                    if (child is Border border && border.Tag is TextAnnotation ann && ann.Id == _selectedText.Id)
                    {
                        var s = OverlayScale;
                        Canvas.SetLeft(border, _selectedText.X * s);
                        Canvas.SetTop(border, _selectedText.Y * s);
                        break;
                    }
                }
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void CommitMove()
        {
            var dx = _lastDragPos.X - _moveOrigin.X;
            var dy = _lastDragPos.Y - _moveOrigin.Y;
            if (Math.Abs(dx) < 0.2 && Math.Abs(dy) < 0.2) return;
            if (_selectedStroke != null)
                _history?.Push(new TranslateStrokeCommand(_selectedStroke, (float)dx, (float)dy));
            if (_selectedText != null && _editingText?.Id != _selectedText.Id)
                _history?.Push(new TranslateTextCommand(_selectedText, dx, dy));
        }

        private void CreateTextAt(Point pos)
        {
            if (_store == null) return;
            var annotation = new TextAnnotation
            {
                PageIndex = _pageIndex,
                X = pos.X,
                Y = pos.Y,
                Text = "",
                FontSize = DefaultFontSize,
                IsBold = DefaultBold,
                IsItalic = DefaultItalic,
                Color = DefaultTextColor,
                Width = 180,
                Height = Math.Max(32, DefaultFontSize * 1.6)
            };
            _store.AddText(annotation);
            _selectedText = annotation;
            _editingText = annotation;
            _editSnapshot = "";
            _history?.Push(new AddTextCommand(_store, annotation));
            RebuildTextLayer();
            AnnotationsChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            if (_editingBox != null)
            {
                _editingBox.PlaceholderText = Loc.Get("overlay.textPlaceholder");
                _editingBox.Focus(FocusState.Programmatic);
            }
        }

        private void CommitTextEdit()
        {
            if (_editingText != null && _editingText.Text != _editSnapshot)
                _history?.Push(new EditTextCommand(_editingText, _editSnapshot, _editingText.Text));
            _editingText = null;
            RebuildTextLayer();
        }

        private static void FitTextHeight(TextAnnotation text)
        {
            var raw = text.Text ?? "";
            var hardLines = Math.Max(1, raw.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n').Length);
            // Estimate wrapped lines (TextWrapping) from content width.
            var avgChar = Math.Max(4.0, text.FontSize * 0.55);
            var charsPerLine = Math.Max(8, (int)(text.Width / avgChar));
            var wrapped = 0;
            foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                wrapped += Math.Max(1, (int)Math.Ceiling(Math.Max(1, line.Length) / (double)charsPerLine));
            var lines = Math.Max(hardLines, wrapped);
            text.Height = Math.Max(text.FontSize * 1.8, lines * text.FontSize * 1.5 + 16);
        }

        private static Rect NormalizeRect(Point a, Point b)
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private void RebuildTextLayer()
        {
            _textLayer.Children.Clear();
            _editingBox = null;
            _textLayer.IsHitTestVisible = _editingText != null;
            if (_store == null) return;
            var s = OverlayScale;
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0)
                s = 1;

            foreach (var text in _store.GetTexts(_pageIndex))
            {
                FitTextHeight(text);
                var isEditing = _editingText?.Id == text.Id;
                var isSelected = _selectedText?.Id == text.Id;
                var width = text.Width * s;
                var height = text.Height * s;
                if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
                    continue;

                var border = new Border
                {
                    Width = width,
                    Height = height,
                    MinHeight = Math.Max(1, text.FontSize * 1.8 * s),
                    Background = new SolidColorBrush(Color.FromArgb((byte)(isSelected ? 60 : 30), 255, 255, 0)),
                    BorderBrush = new SolidColorBrush(
                        isSelected
                            ? Color.FromArgb(255, 0, 120, 215)
                            : Color.FromArgb(120, 100, 100, 100)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4),
                    Tag = text,
                    IsHitTestVisible = isEditing
                };

                if (isEditing)
                {
                    var box = new TextBox
                    {
                        Text = text.Text,
                        FontSize = Math.Max(8, text.FontSize * s),
                        FontWeight = text.IsBold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = text.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                        Foreground = new SolidColorBrush(text.Color),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        MinHeight = text.FontSize * 1.4 * s,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        PlaceholderText = string.IsNullOrEmpty(text.Text) ? Loc.Get("overlay.textPlaceholder") : ""
                    };
                    box.LostFocus += (_, _) =>
                    {
                        if (_editingText?.Id == text.Id)
                            CommitTextEdit();
                    };
                    box.TextChanged += (_, _) =>
                    {
                        text.Text = box.Text;
                        FitTextHeight(text);
                        border.Height = Math.Max(border.MinHeight, text.Height * OverlayScale);
                        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
                    };
                    border.Child = box;
                    _editingBox = box;
                }
                else
                {
                    // TextBlock preserves multi-line display; read-only TextBox often clips to one line.
                    border.Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(text.Text) ? Loc.Get("overlay.textPlaceholder") : text.Text,
                        FontSize = Math.Max(8, text.FontSize * s),
                        FontWeight = text.IsBold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = text.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                        Foreground = new SolidColorBrush(
                            string.IsNullOrEmpty(text.Text)
                                ? Color.FromArgb(160, text.Color.R, text.Color.G, text.Color.B)
                                : text.Color),
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsHitTestVisible = false
                    };
                }

                Canvas.SetLeft(border, text.X * s);
                Canvas.SetTop(border, text.Y * s);
                _textLayer.Children.Add(border);
            }
        }
    }
}
