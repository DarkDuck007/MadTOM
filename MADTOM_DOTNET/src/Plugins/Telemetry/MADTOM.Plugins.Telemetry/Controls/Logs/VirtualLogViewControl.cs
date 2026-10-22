using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Controls.Logs;

/// <summary>
/// High-performance virtualized system log viewer.
/// Renders visible rows on demand using Monospace FormattedText, calculating
/// scrollbar range, thumb size, and virtual offsets directly from monotonic sequence numbers.
/// Offscreen rows remain compressed in zstd client cache and are never decoded.
/// Supports touch drag scrolling, mouse click-drag selection, touch hold-drag selection, and clipboard copy.
/// </summary>
public sealed class VirtualLogViewControl : Control
{
    public static readonly StyledProperty<ulong> TotalRecordsProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, ulong>(nameof(TotalRecords), 0);

    public static readonly StyledProperty<ulong> FirstSeqProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, ulong>(nameof(FirstSeq), 0);

    public static readonly StyledProperty<ulong> LastSeqProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, ulong>(nameof(LastSeq), 0);

    public static readonly StyledProperty<bool> IsFollowModeProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, bool>(nameof(IsFollowMode), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> NodeIdProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, string>(nameof(NodeId), string.Empty);

    public static readonly StyledProperty<string> CollectorEndpointProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, string>(nameof(CollectorEndpoint), string.Empty);

    public static readonly StyledProperty<LogChunkCache?> LogCacheProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, LogChunkCache?>(nameof(LogCache), null);

    public static readonly StyledProperty<TelemetryOptInMode> LogModeProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, TelemetryOptInMode>(nameof(LogMode), TelemetryOptInMode.OptInOff);

    public static readonly StyledProperty<int> CacheRevisionProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, int>(nameof(CacheRevision), 0);

    public static readonly StyledProperty<Func<ulong, bool>?> ChunkUnavailablePredicateProperty =
        AvaloniaProperty.Register<VirtualLogViewControl, Func<ulong, bool>?>(nameof(ChunkUnavailablePredicate), null);

    private const double RowHeight = 18.0;
    private const double ScrollBarWidth = 14.0;
    private static readonly Typeface MonoTypeface = new("Consolas, DejaVu Sans Mono, Liberation Mono, Courier New, monospace");

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, out var res) && res is IBrush brush)
            return brush;
        return fallback;
    }

    private readonly ScrollBar _scrollBar;

    public event EventHandler<ulong>? NeedChunk;

    // Selection tracking
    private ulong? _selectionStartSeq;
    private ulong? _selectionEndSeq;
    private bool _isMouseSelecting;

    public bool HasSelection => _selectionStartSeq.HasValue && _selectionEndSeq.HasValue;
    public ulong MinSelectionSeq => HasSelection ? Math.Min(_selectionStartSeq!.Value, _selectionEndSeq!.Value) : 0;
    public ulong MaxSelectionSeq => HasSelection ? Math.Max(_selectionStartSeq!.Value, _selectionEndSeq!.Value) : 0;

    // Touch drag scrolling & long-press tracking
    private bool _isTouchScrolling;
    private Point _touchStartPoint;
    private double _touchStartScrollVal;
    private DispatcherTimer? _touchHoldTimer;
    private bool _touchHeld;

    public ulong TotalRecords
    {
        get => GetValue(TotalRecordsProperty);
        set => SetValue(TotalRecordsProperty, value);
    }

    public ulong FirstSeq
    {
        get => GetValue(FirstSeqProperty);
        set => SetValue(FirstSeqProperty, value);
    }

    public ulong LastSeq
    {
        get => GetValue(LastSeqProperty);
        set => SetValue(LastSeqProperty, value);
    }

    public bool IsFollowMode
    {
        get => GetValue(IsFollowModeProperty);
        set => SetValue(IsFollowModeProperty, value);
    }

    public string NodeId
    {
        get => GetValue(NodeIdProperty);
        set => SetValue(NodeIdProperty, value);
    }

    public string CollectorEndpoint
    {
        get => GetValue(CollectorEndpointProperty);
        set => SetValue(CollectorEndpointProperty, value);
    }

    public LogChunkCache? LogCache
    {
        get => GetValue(LogCacheProperty);
        set => SetValue(LogCacheProperty, value);
    }

    public TelemetryOptInMode LogMode
    {
        get => GetValue(LogModeProperty);
        set => SetValue(LogModeProperty, value);
    }

    public int CacheRevision
    {
        get => GetValue(CacheRevisionProperty);
        set => SetValue(CacheRevisionProperty, value);
    }

    public Func<ulong, bool>? ChunkUnavailablePredicate
    {
        get => GetValue(ChunkUnavailablePredicateProperty);
        set => SetValue(ChunkUnavailablePredicateProperty, value);
    }

    public VirtualLogViewControl()
    {
        Focusable = true;

        _scrollBar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        _scrollBar.Scroll += (_, _) =>
        {
            if (_scrollBar.Maximum > 0 && _scrollBar.Value < _scrollBar.Maximum - 1.0)
            {
                if (IsFollowMode) IsFollowMode = false;
            }
            else if (_scrollBar.Value >= _scrollBar.Maximum - 1.0)
            {
                if (!IsFollowMode) IsFollowMode = true;
            }
            InvalidateVisual();
        };

        VisualChildren.Add(_scrollBar);
        LogicalChildren.Add(_scrollBar);

        SetupContextMenu();
    }

    private void SetupContextMenu()
    {
        var copyItem = new MenuItem { Header = "Copy Selected (Ctrl+C)" };
        copyItem.Click += async (_, _) => await CopySelectedToClipboardAsync();

        var selectAllItem = new MenuItem { Header = "Select All (Ctrl+A)" };
        selectAllItem.Click += (_, _) => SelectAllVisible();

        var clearItem = new MenuItem { Header = "Clear Selection" };
        clearItem.Click += (_, _) => ClearSelection();

        ContextMenu = new ContextMenu
        {
            ItemsSource = new List<MenuItem> { copyItem, selectAllItem, clearItem }
        };
    }

    static VirtualLogViewControl()
    {
        AffectsRender<VirtualLogViewControl>(
            TotalRecordsProperty, FirstSeqProperty, LastSeqProperty,
            IsFollowModeProperty, NodeIdProperty, LogModeProperty, CacheRevisionProperty,
            ChunkUnavailablePredicateProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TotalRecordsProperty || change.Property == LastSeqProperty)
        {
            UpdateScrollBarRange();
            if (IsFollowMode)
            {
                _scrollBar.Value = _scrollBar.Maximum;
            }
            InvalidateVisual();
        }
        else if (change.Property == IsFollowModeProperty && change.GetNewValue<bool>())
        {
            _scrollBar.Value = _scrollBar.Maximum;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _scrollBar.Measure(new Size(ScrollBarWidth, availableSize.Height));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _scrollBar.Arrange(new Rect(finalSize.Width - ScrollBarWidth, 0, ScrollBarWidth, finalSize.Height));
        UpdateScrollBarRange();
        if (IsFollowMode)
        {
            _scrollBar.Value = _scrollBar.Maximum;
        }
        return finalSize;
    }

    private void UpdateScrollBarRange()
    {
        double visibleRows = Math.Max(1, Math.Floor(Bounds.Height / RowHeight));
        double total = TotalRecords;
        _scrollBar.ViewportSize = visibleRows;
        _scrollBar.Minimum = 0;
        _scrollBar.Maximum = Math.Max(0, total - visibleRows);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y * 3.0;
        double nextVal = Math.Clamp(_scrollBar.Value - delta, _scrollBar.Minimum, _scrollBar.Maximum);
        _scrollBar.Value = nextVal;
        if (nextVal < _scrollBar.Maximum - 1.0)
        {
            IsFollowMode = false;
        }
        else
        {
            IsFollowMode = true;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);

        if (p.Pointer.Type == PointerType.Touch)
        {
            _isTouchScrolling = true;
            _touchStartPoint = p.Position;
            _touchStartScrollVal = _scrollBar.Value;
            _touchHeld = false;
            StartTouchHoldTimer(p.Position);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (p.Properties.IsLeftButtonPressed)
        {
            int rowIndex = (int)Math.Floor(p.Position.Y / RowHeight);
            ulong topRowIndex = (ulong)Math.Floor(_scrollBar.Value);
            ulong seq = FirstSeq + topRowIndex + (ulong)Math.Max(0, rowIndex);
            if (seq <= LastSeq && seq >= FirstSeq)
            {
                _selectionStartSeq = seq;
                _selectionEndSeq = seq;
                _isMouseSelecting = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
            }
        }
        else if (p.Properties.IsRightButtonPressed)
        {
            int rowIndex = (int)Math.Floor(p.Position.Y / RowHeight);
            ulong topRowIndex = (ulong)Math.Floor(_scrollBar.Value);
            ulong seq = FirstSeq + topRowIndex + (ulong)Math.Max(0, rowIndex);
            if (!HasSelection || seq < MinSelectionSeq || seq > MaxSelectionSeq)
            {
                if (seq <= LastSeq && seq >= FirstSeq)
                {
                    _selectionStartSeq = seq;
                    _selectionEndSeq = seq;
                    InvalidateVisual();
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetCurrentPoint(this);

        if (_isTouchScrolling && !_touchHeld)
        {
            double deltaY = p.Position.Y - _touchStartPoint.Y;
            if (Math.Abs(deltaY) > 5.0)
            {
                CancelTouchHoldTimer();
            }
            double rowDelta = deltaY / RowHeight;
            double nextVal = Math.Clamp(_touchStartScrollVal - rowDelta, _scrollBar.Minimum, _scrollBar.Maximum);
            _scrollBar.Value = nextVal;
            if (nextVal < _scrollBar.Maximum - 1.0) IsFollowMode = false; else IsFollowMode = true;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isMouseSelecting || (_isTouchScrolling && _touchHeld))
        {
            int visibleRowCount = (int)Math.Ceiling(Bounds.Height / RowHeight) + 1;
            int rowIndex = (int)Math.Floor(p.Position.Y / RowHeight);
            ulong topRowIndex = (ulong)Math.Floor(_scrollBar.Value);
            ulong seq = FirstSeq + topRowIndex + (ulong)Math.Clamp(rowIndex, 0, visibleRowCount - 1);
            seq = Math.Clamp(seq, FirstSeq, LastSeq);
            _selectionEndSeq = seq;

            // Auto-scroll near edges while dragging
            if (p.Position.Y < RowHeight * 1.5 && _scrollBar.Value > _scrollBar.Minimum)
            {
                _scrollBar.Value = Math.Max(_scrollBar.Minimum, _scrollBar.Value - 1.0);
                IsFollowMode = false;
            }
            else if (p.Position.Y > Bounds.Height - RowHeight * 1.5 && _scrollBar.Value < _scrollBar.Maximum)
            {
                _scrollBar.Value = Math.Min(_scrollBar.Maximum, _scrollBar.Value + 1.0);
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        CancelTouchHoldTimer();
        _isTouchScrolling = false;
        _isMouseSelecting = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelTouchHoldTimer();
        _isTouchScrolling = false;
        _isMouseSelecting = false;
    }

    private void StartTouchHoldTimer(Point pos)
    {
        CancelTouchHoldTimer();
        _touchHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _touchHoldTimer.Tick += (_, _) =>
        {
            CancelTouchHoldTimer();
            _touchHeld = true;
            int rowIndex = (int)Math.Floor(pos.Y / RowHeight);
            ulong topRowIndex = (ulong)Math.Floor(_scrollBar.Value);
            ulong seq = FirstSeq + topRowIndex + (ulong)Math.Max(0, rowIndex);
            if (seq <= LastSeq && seq >= FirstSeq)
            {
                _selectionStartSeq = seq;
                _selectionEndSeq = seq;
                InvalidateVisual();
            }
        };
        _touchHoldTimer.Start();
    }

    private void CancelTouchHoldTimer()
    {
        _touchHoldTimer?.Stop();
        _touchHoldTimer = null;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.C)
        {
            await CopySelectedToClipboardAsync();
            e.Handled = true;
        }
        else if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.A)
        {
            SelectAllVisible();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    public void ClearSelection()
    {
        _selectionStartSeq = null;
        _selectionEndSeq = null;
        InvalidateVisual();
    }

    public void SelectAllVisible()
    {
        if (TotalRecords == 0) return;
        _selectionStartSeq = FirstSeq;
        _selectionEndSeq = LastSeq;
        InvalidateVisual();
    }

    public async Task CopySelectedToClipboardAsync()
    {
        if (!HasSelection || LogCache == null || string.IsNullOrEmpty(NodeId)) return;
        ulong min = MinSelectionSeq;
        ulong max = MaxSelectionSeq;
        var sb = new StringBuilder();
        for (ulong s = min; s <= max; s++)
        {
            var rec = LogCache.GetRecord(CollectorEndpoint, NodeId, s);
            if (rec != null)
            {
                DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampUnixNano / 1_000_000).ToLocalTime().DateTime;
                string timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string unitStr = string.IsNullOrEmpty(rec.Unit) ? "-" : rec.Unit;
                sb.AppendLine($"[{timeStr}] [{unitStr}] {rec.Message}");
            }
        }
        if (sb.Length > 0 && TopLevel.GetTopLevel(this) is { } topLevel && topLevel.Clipboard is { } cb)
        {
            await cb.SetTextAsync(sb.ToString());
        }
    }

    public void ScrollToLatest()
    {
        IsFollowMode = true;
        _scrollBar.Value = _scrollBar.Maximum;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var bgBrush = ResolveBrush("DarkBaseBrush", new ImmutableSolidColorBrush(Color.FromRgb(15, 23, 42)));
        var textPrimary = ResolveBrush("TextPrimaryBrush", new ImmutableSolidColorBrush(Color.FromRgb(241, 245, 249)));
        var textMuted = ResolveBrush("TextMutedBrush", new ImmutableSolidColorBrush(Color.FromRgb(100, 116, 139)));
        var unitBrush = ResolveBrush("AccentTextBrush", ResolveBrush("AccentBrush", new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248))));
        var errBrush = ResolveBrush("CriticalBrush", new ImmutableSolidColorBrush(Color.FromRgb(239, 68, 68)));
        var warnBrush = ResolveBrush("WarningBrush", new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11)));
        var noticeBrush = ResolveBrush("HealthyBrush", new ImmutableSolidColorBrush(Color.FromRgb(34, 197, 94)));
        var selBrush = ResolveBrush("AccentSubtleBrush", new ImmutableSolidColorBrush(Color.FromArgb(70, 56, 189, 248)));

        context.FillRectangle(bgBrush, bounds);

        if (LogMode == TelemetryOptInMode.OptInOff)
        {
            DrawEmptyMessage(context, "System log streaming is disabled on this node (Opt-in: Off).\nEnable Monitor Only or Store in Node Settings.", textMuted);
            return;
        }

        if (TotalRecords == 0)
        {
            DrawEmptyMessage(context, "No log records available.\nWaiting for journald stream or historical chunks...", textMuted);
            return;
        }

        int visibleRowCount = (int)Math.Ceiling(bounds.Height / RowHeight) + 1;
        ulong topRowIndex = (ulong)Math.Floor(_scrollBar.Value);
        ulong firstVisibleSeq = FirstSeq + topRowIndex;
        ulong lastVisibleSeq = Math.Min(LastSeq, firstVisibleSeq + (ulong)visibleRowCount);

        // Prefetch any missing chunks for the visible viewport + overscan (only when collector stores chunks)
        if (LogMode == TelemetryOptInMode.OptInMonitorAndStore && LogCache != null && !string.IsNullOrEmpty(NodeId))
        {
            ulong firstChunkId = firstVisibleSeq > 0 ? (firstVisibleSeq - 1) / 256 : 0;
            if (LogCache.TryGetChunkIdForSeq(NodeId, firstVisibleSeq, out ulong rFirst)) firstChunkId = rFirst;
            ulong lastChunkId = lastVisibleSeq > 0 ? (lastVisibleSeq - 1) / 256 : 0;
            if (LogCache.TryGetChunkIdForSeq(NodeId, lastVisibleSeq, out ulong rLast)) lastChunkId = rLast;

            for (ulong c = firstChunkId; c <= lastChunkId; c++)
            {
                if (!LogCache.TryGetChunk(CollectorEndpoint, NodeId, c, out var chunk) || (chunk != null && lastVisibleSeq > chunk.LastSeq && !chunk.Sealed))
                {
                    NeedChunk?.Invoke(this, c);
                }
            }
        }

        double textWidth = bounds.Width - ScrollBarWidth - 8.0;

        for (int i = 0; i < visibleRowCount; i++)
        {
            ulong seq = firstVisibleSeq + (ulong)i;
            if (seq > LastSeq) break;

            double y = i * RowHeight;

            // Highlight selected rows
            if (HasSelection && seq >= MinSelectionSeq && seq <= MaxSelectionSeq)
            {
                context.FillRectangle(selBrush, new Rect(0, y, textWidth + 8.0, RowHeight));
            }

            var record = LogCache?.GetRecord(CollectorEndpoint, NodeId, seq);

            if (record != null)
            {
                DrawLogRecord(context, record, y, textWidth, textPrimary, textMuted, unitBrush, errBrush, warnBrush, noticeBrush);
            }
            else
            {
                if (LogMode == TelemetryOptInMode.OptInMonitorAndStore && !string.IsNullOrEmpty(NodeId))
                {
                    ulong reqChunkId = seq > 0 ? (seq - 1) / 256 : 0;
                    if (LogCache != null && LogCache.TryGetChunkIdForSeq(NodeId, seq, out ulong mId)) reqChunkId = mId;
                    NeedChunk?.Invoke(this, reqChunkId);
                }
                DrawPlaceholder(context, seq, y, textMuted);
            }
        }
    }

    private void DrawLogRecord(DrawingContext context, LogRecord rec, double y, double maxWidth,
        IBrush textPrimary, IBrush textMuted, IBrush unitBrush, IBrush errBrush, IBrush warnBrush, IBrush noticeBrush)
    {
        // Format: [yyyy-MM-dd HH:mm:ss.fff] [unit] message
        DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(rec.TimestampUnixNano / 1_000_000).ToLocalTime().DateTime;
        string timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string unitStr = string.IsNullOrEmpty(rec.Unit) ? "-" : rec.Unit;
        string msg = rec.Message ?? string.Empty;

        string line = $"[{timeStr}] [{unitStr}] {msg}";
        bool isError = rec.Priority <= 3 || unitStr.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase);
        var baseBrush = isError ? errBrush : textPrimary;
        var formatted = new FormattedText(
            line,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            11.0,
            baseBrush
        )
        {
            MaxTextWidth = Math.Max(100, maxWidth - 8),
            MaxLineCount = 1
        };

        IBrush prioBrush = isError ? errBrush : rec.Priority switch
        {
            <= 3 => errBrush,
            4 => warnBrush,
            5 => noticeBrush,
            _ => unitBrush
        };

        int timeLen = timeStr.Length + 2; // "[HH:mm:ss.fff]"
        int unitStart = timeLen + 1;       // space after time
        int unitLen = unitStr.Length + 2; // "[unit]"

        formatted.SetForegroundBrush(textMuted, 0, timeLen);
        if (unitStart + unitLen <= line.Length)
        {
            formatted.SetForegroundBrush(prioBrush, unitStart, unitLen);
        }

        context.DrawText(formatted, new Point(8, y + 1));
    }

    private void DrawPlaceholder(DrawingContext context, ulong seq, double y, IBrush textMuted)
    {
        ulong chunkId = seq > 0 ? (seq - 1) / 256 : 0;
        if (LogCache != null && !string.IsNullOrEmpty(NodeId) && LogCache.TryGetChunkIdForSeq(NodeId, seq, out ulong resolvedChunkId))
        {
            chunkId = resolvedChunkId;
        }

        var errBrush = ResolveBrush("CriticalBrush", new ImmutableSolidColorBrush(Color.FromRgb(239, 68, 68)));

        if (LogCache != null && !string.IsNullOrEmpty(NodeId) && LogCache.TryGetChunkError(NodeId, chunkId, out var chunkErr) && chunkErr != null)
        {
            string errLine = $"    [ERROR:{chunkErr.ErrorType}] ... record #{seq} failed: {chunkErr.Message}";
            var errText = new FormattedText(
                errLine,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                11.0,
                errBrush
            );
            context.DrawText(errText, new Point(8, y + 1));
            return;
        }

        bool isUnavailable = ChunkUnavailablePredicate?.Invoke(chunkId) == true;
        string text = isUnavailable ? $"    [ERROR:NOT_FOUND] ... record #{seq} unavailable in collector TSDB" : $"    ... loading record #{seq}";
        var placeholder = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            11.0,
            isUnavailable ? errBrush : textMuted
        );
        context.DrawText(placeholder, new Point(8, y + 1));
    }

    private void DrawEmptyMessage(DrawingContext context, string msg, IBrush textMuted)
    {
        var text = new FormattedText(
            msg,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            12.0,
            textMuted
        )
        {
            TextAlignment = TextAlignment.Center
        };
        double x = (Bounds.Width - text.Width) / 2.0;
        double y = (Bounds.Height - text.Height) / 2.0;
        context.DrawText(text, new Point(Math.Max(0, x), Math.Max(0, y)));
    }
}
