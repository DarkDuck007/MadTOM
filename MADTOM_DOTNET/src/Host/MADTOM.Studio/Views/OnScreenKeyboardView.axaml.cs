using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MADTOM.Console.Views;

public partial class OnScreenKeyboardView : UserControl
{
    private Point _startPoint;
    private double _startTranslateX;
    private double _startTranslateY;
    private bool _isDragging;
    private readonly TranslateTransform _transform = new();

    public OnScreenKeyboardView()
    {
        InitializeComponent();

        var card = this.FindControl<Border>("KeyboardCard");
        if (card != null)
        {
            card.RenderTransform = _transform;
        }

        var header = this.FindControl<Grid>("HeaderBar");
        if (header != null)
        {
            header.PointerPressed += (s, e) =>
            {
                var point = e.GetCurrentPoint(header);
                if (point.Properties.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen)
                {
                    _isDragging = true;
                    _startPoint = e.GetPosition(null);
                    _startTranslateX = _transform.X;
                    _startTranslateY = _transform.Y;
                    e.Pointer.Capture(header);
                    e.Handled = true;
                }
            };

            header.PointerMoved += (s, e) =>
            {
                if (!_isDragging) return;
                var current = e.GetPosition(null);
                _transform.X = _startTranslateX + (current.X - _startPoint.X);
                _transform.Y = _startTranslateY + (current.Y - _startPoint.Y);
                e.Handled = true;
            };

            void EndDrag(PointerEventArgs e)
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                }
            }

            header.PointerReleased += (s, e) => EndDrag(e);
            header.PointerCaptureLost += (s, e) => { _isDragging = false; };
        }
    }
}

