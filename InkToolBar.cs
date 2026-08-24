using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace MicaPDF
{
    public enum PenSlot
    {
        Black,
        Red,
        Green,
        Highlighter
    }

    public sealed class InkToolBar : UserControl
    {
        private readonly Border _host;
        private readonly StackPanel _tools;
        private readonly Button _undoButton;
        private readonly Button _redoButton;
        private readonly Button _selectButton;
        private readonly Button _textButton;
        private readonly Button _eraserButton;
        private readonly Button _blackPen;
        private readonly Button _redPen;
        private readonly Button _greenPen;
        private readonly Button _highlighter;
        private readonly Shape _blackTop;
        private readonly Shape _blackTip;
        private readonly Shape _redTop;
        private readonly Shape _redTip;
        private readonly Shape _greenTop;
        private readonly Shape _greenTip;
        private readonly Shape _highlighterTop;
        private readonly Shape _highlighterTip;

        private readonly Button _closeButton;

        private AnnotationTool _tool = AnnotationTool.Select;
        private PenSlot _activeSlot = PenSlot.Black;
        private Color _blackColor = Colors.Black;
        private Color _redColor = Color.FromArgb(255, 220, 38, 38);
        private Color _greenColor = Color.FromArgb(255, 22, 163, 74);
        private Color _highlighterColor = Color.FromArgb(255, 250, 204, 21);

        private Button? _lastClickButton;
        private long _lastClickTicks;
        private const long DoubleClickTicks = TimeSpan.TicksPerMillisecond * 400;

        public event EventHandler<AnnotationTool>? ToolSelected;
        public event EventHandler<(PenSlot Slot, Color Color)>? PenSelected;
        public event EventHandler<(PenSlot Slot, Color Color)>? PenColorChanged;
        public event EventHandler? UndoRequested;
        public event EventHandler? RedoRequested;
        public event EventHandler? CloseRequested;
        public event EventHandler? TextSizeUp;
        public event EventHandler? TextSizeDown;
        public event EventHandler<bool>? BoldToggled;
        public event EventHandler<bool>? ItalicToggled;
        public event EventHandler<Color>? TextColorSelected;

        public InkToolBar()
        {
            _undoButton = IconButton("\uE7A7", Loc.Get("toolbar.undo"));
            _undoButton.Click += (_, _) => UndoRequested?.Invoke(this, EventArgs.Empty);

            _redoButton = IconButton("\uE7A6", Loc.Get("toolbar.redo"));
            _redoButton.Click += (_, _) => RedoRequested?.Invoke(this, EventArgs.Empty);

            _selectButton = IconButton("\uE8B0", Loc.Get("toolbar.select"));
            _selectButton.Click += (_, _) => ToolSelected?.Invoke(this, AnnotationTool.Select);

            _textButton = CreateTextToolButton();
            _eraserButton = IconButton("\uE75C", Loc.Get("toolbar.eraser"));
            _eraserButton.Click += (_, _) => ToolSelected?.Invoke(this, AnnotationTool.Eraser);
            (_blackPen, _blackTop, _blackTip) = CreateNibButton(PenSlot.Black, _blackColor, false, Loc.Get("toolbar.blackPen"));
            (_redPen, _redTop, _redTip) = CreateNibButton(PenSlot.Red, _redColor, false, Loc.Get("toolbar.redPen"));
            (_greenPen, _greenTop, _greenTip) = CreateNibButton(PenSlot.Green, _greenColor, false, Loc.Get("toolbar.greenPen"));
            (_highlighter, _highlighterTop, _highlighterTip) = CreateNibButton(PenSlot.Highlighter, _highlighterColor, true, Loc.Get("toolbar.highlighter"));

            _closeButton = IconButton("\uE711", Loc.Get("toolbar.close"));
            _closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

            _tools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _tools.Children.Add(_undoButton);
            _tools.Children.Add(_redoButton);
            _tools.Children.Add(_selectButton);
            _tools.Children.Add(_textButton);
            _tools.Children.Add(_eraserButton);
            _tools.Children.Add(_blackPen);
            _tools.Children.Add(_redPen);
            _tools.Children.Add(_greenPen);
            _tools.Children.Add(_highlighter);
            _tools.Children.Add(_closeButton);

            _host = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 36, 36, 36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 110, 110, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = _tools,
                RequestedTheme = ElementTheme.Dark
            };

            Content = _host;
            Margin = new Thickness(16);
            RequestedTheme = ElementTheme.Dark;
            ApplyDock("Bottom");
            UpdateVisualState();
        }

        public void ApplyDock(string position)
        {
            switch (position)
            {
                case "Top":
                    HorizontalAlignment = HorizontalAlignment.Center;
                    VerticalAlignment = VerticalAlignment.Top;
                    _tools.Orientation = Orientation.Horizontal;
                    break;
                case "Left":
                    HorizontalAlignment = HorizontalAlignment.Left;
                    VerticalAlignment = VerticalAlignment.Center;
                    _tools.Orientation = Orientation.Vertical;
                    break;
                case "Right":
                    HorizontalAlignment = HorizontalAlignment.Right;
                    VerticalAlignment = VerticalAlignment.Center;
                    _tools.Orientation = Orientation.Vertical;
                    break;
                default:
                    HorizontalAlignment = HorizontalAlignment.Center;
                    VerticalAlignment = VerticalAlignment.Bottom;
                    _tools.Orientation = Orientation.Horizontal;
                    break;
            }
        }

        public void SetHistoryState(bool canUndo, bool canRedo)
        {
            _undoButton.IsEnabled = canUndo;
            _redoButton.IsEnabled = canRedo;
        }

        public void RefreshLocalizedUi()
        {
            ToolTipService.SetToolTip(_undoButton, Loc.Get("toolbar.undo"));
            ToolTipService.SetToolTip(_redoButton, Loc.Get("toolbar.redo"));
            ToolTipService.SetToolTip(_selectButton, Loc.Get("toolbar.select"));
            ToolTipService.SetToolTip(_textButton, Loc.Get("toolbar.text"));
            ToolTipService.SetToolTip(_eraserButton, Loc.Get("toolbar.eraser"));
            ToolTipService.SetToolTip(_blackPen, Loc.Get("toolbar.blackPen"));
            ToolTipService.SetToolTip(_redPen, Loc.Get("toolbar.redPen"));
            ToolTipService.SetToolTip(_greenPen, Loc.Get("toolbar.greenPen"));
            ToolTipService.SetToolTip(_highlighter, Loc.Get("toolbar.highlighter"));
            ToolTipService.SetToolTip(_closeButton, Loc.Get("toolbar.close"));
        }

        public void SetPenColors(Color black, Color red, Color green, Color highlighter)
        {
            _blackColor = black;
            _redColor = red;
            _greenColor = green;
            _highlighterColor = highlighter;
            ApplyFill(_blackTop, _blackTip, black);
            ApplyFill(_redTop, _redTip, red);
            ApplyFill(_greenTop, _greenTip, green);
            ApplyFill(_highlighterTop, _highlighterTip, highlighter);
            UpdateVisualState();
        }

        public void SetActive(AnnotationTool tool, PenSlot slot)
        {
            _tool = tool;
            _activeSlot = slot;
            UpdateVisualState();
        }

        private Button CreateTextToolButton()
        {
            var button = IconButton("\uE8D2", Loc.Get("toolbar.text"));
            button.Click += (_, _) =>
            {
                var doubleClick = IsDoubleClick(button);
                if (doubleClick)
                {
                    FlyoutBase.SetAttachedFlyout(button, BuildTextFlyout());
                    FlyoutBase.ShowAttachedFlyout(button);
                    return;
                }

                ToolSelected?.Invoke(this, AnnotationTool.Text);
            };
            return button;
        }

        private Flyout BuildTextFlyout()
        {
            // Single vertical column of style actions (not a 2x2 grid).
            var styleCol = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Width = 110
            };
            styleCol.Children.Add(new TextBlock
            {
                Text = Loc.Get("toolbar.textStyles"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            styleCol.Children.Add(MakeStyleButton("A+", () => TextSizeUp?.Invoke(this, EventArgs.Empty)));
            styleCol.Children.Add(MakeStyleButton("A-", () => TextSizeDown?.Invoke(this, EventArgs.Empty)));

            var bold = new ToggleButton
            {
                Content = Loc.Get("toolbar.bold"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 36
            };
            bold.Click += (_, _) => BoldToggled?.Invoke(this, bold.IsChecked == true);
            var italic = new ToggleButton
            {
                Content = Loc.Get("toolbar.italic"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 36
            };
            italic.Click += (_, _) => ItalicToggled?.Invoke(this, italic.IsChecked == true);
            styleCol.Children.Add(bold);
            styleCol.Children.Add(italic);

            var colorGrid = new VariableSizedWrapGrid
            {
                MaximumRowsOrColumns = 4,
                Orientation = Orientation.Horizontal,
                ItemWidth = 36,
                ItemHeight = 36
            };
            foreach (var color in SharedPalette())
                colorGrid.Children.Add(CreateColorSwatch(color, c => TextColorSelected?.Invoke(this, c)));

            var colorCol = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = Loc.Get("toolbar.colors"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                    colorGrid
                }
            };

            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(styleCol, 0);
            Grid.SetColumn(colorCol, 1);
            row.Children.Add(styleCol);
            row.Children.Add(colorCol);

            return new Flyout { Content = row };
        }

        private static Button MakeStyleButton(string label, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 36
            };
            button.Click += (_, _) => onClick();
            return button;
        }

        private static Border CreateColorSwatch(Color color, Action<Color> onPick)
        {
            // Border keeps fill color stable; hover only thickens the white ring.
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Tag = color
            };
            swatch.PointerEntered += (_, _) =>
            {
                swatch.BorderThickness = new Thickness(2.5);
                swatch.RenderTransform = new ScaleTransform { ScaleX = 1.08, ScaleY = 1.08, CenterX = 14, CenterY = 14 };
            };
            swatch.PointerExited += (_, _) =>
            {
                swatch.BorderThickness = new Thickness(1);
                swatch.RenderTransform = null;
            };
            swatch.PointerPressed += (_, e) =>
            {
                onPick(color);
                e.Handled = true;
            };
            return swatch;
        }

        private (Button Button, Shape Top, Shape Tip) CreateNibButton(PenSlot slot, Color color, bool highlighter, string tooltip)
        {
            var canvas = new Canvas { Width = 28, Height = 32, IsHitTestVisible = false };
            Shape top;
            Shape tip;
            Shape outline;

            if (highlighter)
            {
                top = new Rectangle
                {
                    Width = 16, Height = 7,
                    Fill = new SolidColorBrush(color),
                    RadiusX = 1, RadiusY = 1
                };
                Canvas.SetLeft(top, 6);
                Canvas.SetTop(top, 2);

                tip = new Polygon
                {
                    Points = new PointCollection { new Point(6, 22), new Point(22, 22), new Point(20, 30), new Point(8, 30) },
                    Fill = new SolidColorBrush(color)
                };

                outline = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(5, 1), new Point(23, 1), new Point(23, 10),
                        new Point(21, 22), new Point(20, 31), new Point(8, 31),
                        new Point(7, 22), new Point(5, 10)
                    },
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Colors.Transparent)
                };

                var divider = new Line
                {
                    X1 = 6, Y1 = 10, X2 = 22, Y2 = 10,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.2
                };
                canvas.Children.Add(top);
                canvas.Children.Add(tip);
                canvas.Children.Add(outline);
                canvas.Children.Add(divider);
            }
            else
            {
                // Nib: wide top, taper to tip — matches attached icon style.
                top = new Rectangle
                {
                    Width = 14, Height = 6,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(top, 7);
                Canvas.SetTop(top, 2);

                tip = new Polygon
                {
                    Points = new PointCollection { new Point(11, 24), new Point(17, 24), new Point(14, 30) },
                    Fill = new SolidColorBrush(color)
                };

                outline = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(6, 1), new Point(22, 1), new Point(22, 9),
                        new Point(18, 24), new Point(14, 31), new Point(10, 24), new Point(6, 9)
                    },
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Colors.Transparent)
                };

                var divider = new Line
                {
                    X1 = 7, Y1 = 9, X2 = 21, Y2 = 9,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1.2
                };
                canvas.Children.Add(top);
                canvas.Children.Add(tip);
                canvas.Children.Add(outline);
                canvas.Children.Add(divider);
            }

            var button = new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = canvas,
                CornerRadius = new CornerRadius(6),
                Tag = slot
            };
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) => OnPenClicked(button, slot);
            return (button, top, tip);
        }

        private void OnPenClicked(Button button, PenSlot slot)
        {
            var color = GetSlotColor(slot);
            var doubleClick = IsDoubleClick(button);
            if (doubleClick)
            {
                ShowColorFlyout(button, slot);
                return;
            }

            _activeSlot = slot;
            PenSelected?.Invoke(this, (slot, color));
            UpdateVisualState();
        }

        private void ShowColorFlyout(Button button, PenSlot slot)
        {
            var grid = new VariableSizedWrapGrid
            {
                MaximumRowsOrColumns = 4,
                Orientation = Orientation.Horizontal,
                ItemWidth = 36,
                ItemHeight = 36,
                Margin = new Thickness(8)
            };
            foreach (var color in SharedPalette())
            {
                grid.Children.Add(CreateColorSwatch(color, picked =>
                {
                    SetSlotColor(slot, picked);
                    PenColorChanged?.Invoke(this, (slot, picked));
                    PenSelected?.Invoke(this, (slot, picked));
                    UpdateVisualState();
                    if (FlyoutBase.GetAttachedFlyout(button) is Flyout f)
                        f.Hide();
                }));
            }

            var flyout = new Flyout { Content = grid };
            FlyoutBase.SetAttachedFlyout(button, flyout);
            FlyoutBase.ShowAttachedFlyout(button);
        }

        private static Color[] SharedPalette() => new[]
        {
            Colors.Black,
            Colors.White,
            Color.FromArgb(255, 220, 38, 38),   // red
            Color.FromArgb(255, 249, 115, 22),  // orange
            Color.FromArgb(255, 250, 204, 21),  // yellow
            Color.FromArgb(255, 22, 163, 74),   // green
            Color.FromArgb(255, 6, 182, 212),   // cyan
            Color.FromArgb(255, 37, 99, 235),   // blue
            Color.FromArgb(255, 79, 70, 229),   // indigo
            Color.FromArgb(255, 147, 51, 234),  // purple
            Color.FromArgb(255, 236, 72, 153),  // pink
            Color.FromArgb(255, 120, 113, 108)  // gray
        };

        private Color GetSlotColor(PenSlot slot) => slot switch
        {
            PenSlot.Black => _blackColor,
            PenSlot.Red => _redColor,
            PenSlot.Green => _greenColor,
            _ => _highlighterColor
        };

        private void SetSlotColor(PenSlot slot, Color color)
        {
            switch (slot)
            {
                case PenSlot.Black:
                    _blackColor = color;
                    ApplyFill(_blackTop, _blackTip, color);
                    break;
                case PenSlot.Red:
                    _redColor = color;
                    ApplyFill(_redTop, _redTip, color);
                    break;
                case PenSlot.Green:
                    _greenColor = color;
                    ApplyFill(_greenTop, _greenTip, color);
                    break;
                default:
                    _highlighterColor = color;
                    ApplyFill(_highlighterTop, _highlighterTip, color);
                    break;
            }
        }

        private static void ApplyFill(Shape top, Shape tip, Color color)
        {
            top.Fill = new SolidColorBrush(color);
            tip.Fill = new SolidColorBrush(color);
        }

        private bool IsDoubleClick(Button button)
        {
            var now = DateTime.UtcNow.Ticks;
            var isDouble = ReferenceEquals(_lastClickButton, button) && now - _lastClickTicks <= DoubleClickTicks;
            _lastClickButton = button;
            _lastClickTicks = now;
            return isDouble;
        }

        private static Button IconButton(string glyph, string tooltip)
        {
            var button = new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Content = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(Colors.White) }
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        private void UpdateVisualState()
        {
            Mark(_selectButton, _tool == AnnotationTool.Select);
            Mark(_textButton, _tool == AnnotationTool.Text);
            Mark(_eraserButton, _tool == AnnotationTool.Eraser);
            Mark(_blackPen, _tool == AnnotationTool.Pen && _activeSlot == PenSlot.Black);
            Mark(_redPen, _tool == AnnotationTool.Pen && _activeSlot == PenSlot.Red);
            Mark(_greenPen, _tool == AnnotationTool.Pen && _activeSlot == PenSlot.Green);
            Mark(_highlighter, _tool == AnnotationTool.Pen && _activeSlot == PenSlot.Highlighter);
        }

        private static void Mark(Button button, bool active)
        {
            button.Background = new SolidColorBrush(
                active ? Color.FromArgb(55, 255, 255, 255) : Colors.Transparent);
        }
    }
}
