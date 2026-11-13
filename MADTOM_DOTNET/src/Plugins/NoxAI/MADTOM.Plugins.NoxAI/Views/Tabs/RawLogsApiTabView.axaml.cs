using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class RawLogsApiTabView : UserControl
{
    private RawLogsApiTabViewModel? _currentVm;

    public RawLogsApiTabView()
    {
        InitializeComponent();
        LogViewer.NeedChunk += (_, chunkId) =>
        {
            if (DataContext is RawLogsApiTabViewModel vm)
            {
                vm.RequestChunk(chunkId);
            }
        };

        ChatHistoryScroll.ScrollChanged += OnChatScrollChanged;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            _currentVm.ScrollToTopRequested -= OnScrollToTopRequested;
            _currentVm.LogNavigationRequested -= OnLogNavigationRequested;
            _currentVm.ChatHistoryReset -= OnChatHistoryReset;
            _currentVm.ChatOlderPrepending -= OnChatOlderPrepending;
        }

        if (DataContext is RawLogsApiTabViewModel vm)
        {
            _currentVm = vm;
            _currentVm.ScrollToTopRequested += OnScrollToTopRequested;
            _currentVm.LogNavigationRequested += OnLogNavigationRequested;
            _currentVm.ChatHistoryReset += OnChatHistoryReset;
            _currentVm.ChatOlderPrepending += OnChatOlderPrepending;
        }
        else
        {
            _currentVm = null;
        }
    }

    // ------------------------------------------------------------------ Chats: bottom-anchored history

    private bool _chatStickToBottom = true;
    private double? _chatOlderAnchorExtent; // extent before older calls were prepended

    private void OnChatHistoryReset()
    {
        _chatStickToBottom = true;
        _chatOlderAnchorExtent = null;
        ChatJumpToNewest.IsVisible = false;
        ScrollChatToEnd();
    }

    private void OnChatOlderPrepending()
    {
        _chatStickToBottom = false;
        _chatOlderAnchorExtent = ChatHistoryScroll.Extent.Height;
    }

    private void OnChatJumpToNewest(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OnChatHistoryReset();

    // Heights are estimated until items are realized, so re-pin after layout settles.
    private void ScrollChatToEnd() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ChatHistoryScroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = ChatHistoryScroll;
        double maxOffset = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        bool atBottom = sv.Offset.Y >= maxOffset - 40;
        bool extentChanged = Math.Abs(e.ExtentDelta.Y) > 0.5;

        if (_chatOlderAnchorExtent is double before && extentChanged)
        {
            // Older calls were inserted above: shift by the added height so the view doesn't jump.
            _chatOlderAnchorExtent = null;
            sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + (sv.Extent.Height - before));
            return;
        }
        // A scroll upward always wins (even if heights were re-estimated in the same pass); only
        // ScrollToEnd moves the offset down by itself.
        if (e.OffsetDelta.Y < -0.5) _chatStickToBottom = atBottom;
        else if (atBottom) _chatStickToBottom = true;
        // Payloads finishing / live calls arriving: keep following the newest message.
        if (extentChanged && _chatStickToBottom && !atBottom) ScrollChatToEnd();
        ChatJumpToNewest.IsVisible = !_chatStickToBottom && !atBottom;

        // Near the top: extend the window upwards (one step at a time).
        if (!_chatStickToBottom && _chatOlderAnchorExtent == null && sv.Offset.Y < sv.Viewport.Height &&
            DataContext is RawLogsApiTabViewModel vm && vm.ChatHasOlder)
            vm.LoadOlderChatHistory(); // anchors via ChatOlderPrepending
    }

    private void OnLogNavigationRequested(ulong seq) => Avalonia.Threading.Dispatcher.UIThread.Post(() => LogViewer.ScrollToSequence(seq));

    private async void OnLogSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not RawLogsApiTabViewModel vm) return;
        if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await vm.SearchLogsAsync(); }
        else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; vm.ClearLogSearch(); }
    }

    private async void OnApiSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not RawLogsApiTabViewModel vm) return;
        if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await vm.SearchApiContentAsync(); }
        else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; vm.ApiFilter = string.Empty; }
    }

    private ScrollViewer? _attachedScrollViewer;

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentVm != null) _currentVm.IsTabActive = true;
        DetachScrollViewer();
        _attachedScrollViewer = ApiCallsListBox.FindDescendantOfType<ScrollViewer>();
        if (_attachedScrollViewer != null)
        {
            _attachedScrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentVm != null) _currentVm.IsTabActive = false;
        DetachScrollViewer();
    }

    private void DetachScrollViewer()
    {
        if (_attachedScrollViewer != null)
        {
            _attachedScrollViewer.ScrollChanged -= OnScrollChanged;
            _attachedScrollViewer = null;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_attachedScrollViewer != null && DataContext is RawLogsApiTabViewModel vm)
        {
            bool isDown = _attachedScrollViewer.Offset.Y > 30;
            vm.IsScrolledDown = isDown;
            vm.ShowGoToNewestButton = isDown;
        }
    }

    private void OnScrollToTopRequested()
    {
        var scrollViewer = ApiCallsListBox.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer != null)
        {
            scrollViewer.Offset = new Vector(0, 0);
        }
        else if (ApiCallsListBox.ItemCount > 0)
        {
            ApiCallsListBox.ScrollIntoView(0);
        }
    }
}
