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
                    _startPoint = e.GetPosition(this);
                    _startTranslateX = _transform.X;
                    _startTranslateY = _transform.Y;
                    e.Pointer.Capture(header);
                    e.Handled = true;
                }
            };

            header.PointerMoved += (s, e) =>
            {
                if (!_isDragging) return;
                var current = e.GetPosition(this);
                double targetX = _startTranslateX + (current.X - _startPoint.X);
                double targetY = _startTranslateY + (current.Y - _startPoint.Y);

                if (card != null && Bounds.Width > 0 && Bounds.Height > 0)
                {
                    double cardW = card.Bounds.Width > 0 ? card.Bounds.Width : 660.0;
                    double cardH = card.Bounds.Height > 0 ? card.Bounds.Height : 240.0;
                    double defaultLeft = (Bounds.Width - cardW) / 2.0;
                    double defaultTop = Bounds.Height - cardH - 16.0;

                    double minX = -defaultLeft;
                    double maxX = Bounds.Width - defaultLeft - cardW;
                    double minY = -defaultTop;
                    double maxY = 16.0;

                    targetX = System.Math.Clamp(targetX, minX, maxX);
                    targetY = System.Math.Clamp(targetY, minY, maxY);
                }

                _transform.X = targetX;
                _transform.Y = targetY;
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

