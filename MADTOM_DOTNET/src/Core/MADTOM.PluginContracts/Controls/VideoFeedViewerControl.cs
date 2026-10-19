using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MADTOM.PluginContracts.Controls;

/// <summary>
/// Reusable high-performance video feed viewer control.
/// Renders raw video frames with aspect ratio preservation, theme integration,
/// status badges (LIVE, RECORDING), and HUD overlays.
/// </summary>
public class VideoFeedViewerControl : Control
{
    public static readonly StyledProperty<string> FeedTitleProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, string>(nameof(FeedTitle), string.Empty);

    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, string>(nameof(StatusText), "WAITING");

    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, bool>(nameof(IsLive), false);

    public static readonly StyledProperty<bool> IsRecordingProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, bool>(nameof(IsRecording), false);

    public static readonly StyledProperty<string> FpsTextProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, string>(nameof(FpsText), string.Empty);

    public static readonly StyledProperty<string> ResolutionTextProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, string>(nameof(ResolutionText), string.Empty);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoFeedViewerControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    public string FeedTitle
    {
        get => GetValue(FeedTitleProperty);
        set => SetValue(FeedTitleProperty, value);
    }

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public string FpsText
    {
        get => GetValue(FpsTextProperty);
        set => SetValue(FpsTextProperty, value);
    }

    public string ResolutionText
    {
        get => GetValue(ResolutionTextProperty);
        set => SetValue(ResolutionTextProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private readonly object _bitmapLock = new();
    private bool _hasFrame;

    static VideoFeedViewerControl()
    {
        AffectsRender<VideoFeedViewerControl>(
            FeedTitleProperty, StatusTextProperty, IsLiveProperty,
            IsRecordingProperty, FpsTextProperty, ResolutionTextProperty, StretchProperty);
    }

    private (uint Width, uint Height, byte[] Data)? _pendingFrame;
    private bool _renderQueued;

    public void UpdateFrame(uint width, uint height, ReadOnlySpan<byte> bgraData)
    {
        if (width == 0 || height == 0 || (ulong)width * height * 4 != (ulong)bgraData.Length) return;
        lock (_bitmapLock)
        {
            _pendingFrame = (width, height, bgraData.ToArray());
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.UIThread.Post(ApplyPendingFrame, DispatcherPriority.Render);
    }

    private void ApplyPendingFrame()
    {
        (uint Width, uint Height, byte[] Data)? pending;
        lock (_bitmapLock)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
            _renderQueued = false;
        }
        if (pending is not { } frame) return;
        int width = checked((int)frame.Width), height = checked((int)frame.Height);
        if (_bitmap == null || _bitmap.PixelSize != new PixelSize(width, height))
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _bitmap.Lock())
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(frame.Data, y * width * 4, IntPtr.Add(fb.Address, y * fb.RowBytes), width * 4);
        }
        _hasFrame = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Background
        var bgBrush = new SolidColorBrush(Color.FromRgb(15, 17, 23));
        context.FillRectangle(bgBrush, bounds);

        // Draw Video Frame
        lock (_bitmapLock)
        {
            if (_bitmap != null && _hasFrame)
            {
                var srcSize = _bitmap.Size;
                var destRect = CalculateDestRect(bounds, srcSize, Stretch);
                context.DrawImage(_bitmap, new Rect(0, 0, srcSize.Width, srcSize.Height), destRect);
            }
            else
            {
                // No frame placeholder text
                var placeholderText = new FormattedText(
                    string.IsNullOrEmpty(FeedTitle) ? "No Video Signal" : $"{FeedTitle}\nWaiting for stream...",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                    14,
                    new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)));

                var textPos = new Point(
                    Math.Max(0, (bounds.Width - placeholderText.Width) / 2),
                    Math.Max(0, (bounds.Height - placeholderText.Height) / 2));
                context.DrawText(placeholderText, textPos);
            }
        }

        // Top Overlay Bar (Title & Status Pills)
        DrawTopOverlay(context, bounds);

        // Bottom HUD (Resolution & FPS)
        DrawBottomHud(context, bounds);
    }

    private void DrawTopOverlay(DrawingContext context, Rect bounds)
    {
        // Dark gradient scrim at the top
        var scrimBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(180, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        context.FillRectangle(scrimBrush, new Rect(0, 0, bounds.Width, Math.Min(40, bounds.Height)));

        // Title
        if (!string.IsNullOrEmpty(FeedTitle))
        {
            var titleText = new FormattedText(
                FeedTitle,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                12,
                Brushes.White);
            context.DrawText(titleText, new Point(10, 8));
        }

        // Status Badge (LIVE / RECORDING / WAITING)
        double rightOffset = bounds.Width - 10;

        if (IsRecording)
        {
            var recBadgeText = new FormattedText(
                "● REC",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                10,
                Brushes.White);

            double badgeWidth = recBadgeText.Width + 12;
            rightOffset -= badgeWidth;
            var recRect = new Rect(rightOffset, 6, badgeWidth, 18);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(220, 38, 38)), recRect, 4);
            context.DrawText(recBadgeText, new Point(rightOffset + 6, 8));
            rightOffset -= 6;
        }

        if (IsLive)
        {
            var liveBadgeText = new FormattedText(
                "● LIVE",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                10,
                Brushes.White);

            double badgeWidth = liveBadgeText.Width + 12;
            rightOffset -= badgeWidth;
            var liveRect = new Rect(rightOffset, 6, badgeWidth, 18);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(22, 163, 74)), liveRect, 4);
            context.DrawText(liveBadgeText, new Point(rightOffset + 6, 8));
        }
        else if (!IsRecording)
        {
            var status = string.IsNullOrEmpty(StatusText) ? "WAITING" : StatusText.ToUpperInvariant();
            var statusBadgeText = new FormattedText(
                status,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                10,
                new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));

            double badgeWidth = statusBadgeText.Width + 12;
            rightOffset -= badgeWidth;
            var statusRect = new Rect(rightOffset, 6, badgeWidth, 18);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(120, 75, 85, 99)), statusRect, 4);
            context.DrawText(statusBadgeText, new Point(rightOffset + 6, 8));
        }
    }

    private void DrawBottomHud(DrawingContext context, Rect bounds)
    {
        var hudString = string.Empty;
        if (!string.IsNullOrEmpty(ResolutionText)) hudString += ResolutionText;
        if (!string.IsNullOrEmpty(FpsText))
        {
            if (!string.IsNullOrEmpty(hudString)) hudString += " • ";
            hudString += FpsText;
        }

        if (!string.IsNullOrEmpty(hudString))
        {
            var hudText = new FormattedText(
                hudString,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium),
                10,
                new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));

            var pos = new Point(bounds.Width - hudText.Width - 10, bounds.Height - hudText.Height - 6);
            var bgRect = new Rect(pos.X - 4, pos.Y - 2, hudText.Width + 8, hudText.Height + 4);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), bgRect, 3);
            context.DrawText(hudText, pos);
        }
    }

    private static Rect CalculateDestRect(Rect container, Size content, Stretch stretch)
    {
        if (content.Width <= 0 || content.Height <= 0) return container;

        switch (stretch)
        {
            case Stretch.Fill:
                return container;

            case Stretch.Uniform:
                double scaleX = container.Width / content.Width;
                double scaleY = container.Height / content.Height;
                double scale = Math.Min(scaleX, scaleY);

                double width = content.Width * scale;
                double height = content.Height * scale;
                double x = container.X + (container.Width - width) / 2.0;
                double y = container.Y + (container.Height - height) / 2.0;
                return new Rect(x, y, width, height);

            case Stretch.UniformToFill:
                double fillScaleX = container.Width / content.Width;
                double fillScaleY = container.Height / content.Height;
                double fillScale = Math.Max(fillScaleX, fillScaleY);

                double fWidth = content.Width * fillScale;
                double fHeight = content.Height * fillScale;
                double fx = container.X + (container.Width - fWidth) / 2.0;
                double fy = container.Y + (container.Height - fHeight) / 2.0;
                return new Rect(fx, fy, fWidth, fHeight);

            default:
                return container;
        }
    }
}
