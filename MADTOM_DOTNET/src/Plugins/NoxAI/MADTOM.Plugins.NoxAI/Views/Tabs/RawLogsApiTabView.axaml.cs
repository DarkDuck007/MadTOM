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

        ChatHistoryListBox.TemplateApplied += OnChatHistoryListBoxTemplateApplied;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnChatHistoryListBoxTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        AttachChatScrollViewer(e.NameScope.Find<ScrollViewer>("PART_ScrollViewer"));
    }

    private void AttachChatScrollViewer(ScrollViewer? scrollViewer)
    {
        scrollViewer ??= ChatHistoryListBox.FindDescendantOfType<ScrollViewer>();
        if (ReferenceEquals(_chatHistoryScrollViewer, scrollViewer)) return;
        DetachChatScrollViewer();
        _chatHistoryScrollViewer = scrollViewer;
        if (_chatHistoryScrollViewer != null)
        {
            _chatHistoryScrollViewer.ScrollChanged += OnChatScrollChanged;
            if (_chatStickToBottom)
            {
                ScrollChatToEnd();
            }
        }
    }

    private void DetachChatScrollViewer()
    {
        if (_chatHistoryScrollViewer != null)
        {
            _chatHistoryScrollViewer.ScrollChanged -= OnChatScrollChanged;
            _chatHistoryScrollViewer = null;
        }
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

    private ScrollViewer? _chatHistoryScrollViewer;
    private bool _chatStickToBottom = true;
    private bool _chatScrollToEndQueued;
    private bool _isAdjustingChatScroll;
    private double? _chatOlderAnchorExtent; // extent before older calls were prepended
    private double _chatOlderAnchorOffset;  // offset before older calls were prepended

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
        if (_chatHistoryScrollViewer is { } sv)
        {
            _chatOlderAnchorExtent = sv.Extent.Height;
            _chatOlderAnchorOffset = sv.Offset.Y;
        }
        else
        {
            _chatOlderAnchorExtent = null;
        }
    }

    private void OnChatJumpToNewest(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OnChatHistoryReset();

    private void ScrollChatToEnd()
    {
        if (_chatScrollToEndQueued) return;
        _chatScrollToEndQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _chatScrollToEndQueued = false;
            if (!_chatStickToBottom) return;

            if (_currentVm is { ChatHistory.Count: > 0 } vm)
            {
                ChatHistoryListBox.ScrollIntoView(vm.ChatHistory[^1]);
            }

            if (_chatHistoryScrollViewer is { } sv)
            {
                double maxOffset = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                _isAdjustingChatScroll = true;
                try
                {
                    sv.Offset = new Vector(sv.Offset.X, maxOffset);
                }
                finally
                {
                    _isAdjustingChatScroll = false;
                }
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isAdjustingChatScroll || _chatHistoryScrollViewer is not { } sv) return;

        double maxOffset = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        bool atBottom = maxOffset <= 1 || sv.Offset.Y >= maxOffset - 40;
        bool extentChanged = Math.Abs(e.ExtentDelta.Y) > 0.5;

        if (_chatOlderAnchorExtent is double before && extentChanged)
        {
            // Older calls were prepended above: shift synchronously by the added height so the view stays anchored.
            _chatOlderAnchorExtent = null;
            double targetY = Math.Max(0, _chatOlderAnchorOffset + (sv.Extent.Height - before));
            _isAdjustingChatScroll = true;
            try
            {
                sv.Offset = new Vector(sv.Offset.X, targetY);
            }
            finally
            {
                _isAdjustingChatScroll = false;
            }
            return;
        }

        if (extentChanged)
        {
            // Layout expansion from visible payload batches: pin synchronously to bottom if stick-to-bottom is active;
            // if the user has scrolled up, do not fight their scroll position.
            if (_chatStickToBottom && !atBottom)
            {
                _isAdjustingChatScroll = true;
                try
                {
                    sv.Offset = new Vector(sv.Offset.X, maxOffset);
                }
                finally
                {
                    _isAdjustingChatScroll = false;
                }
                atBottom = true;
            }
        }
        else
        {
            // Pure user scroll (extent did not change in this pass)
            if (e.OffsetDelta.Y < -0.5) _chatStickToBottom = atBottom;
            else if (atBottom) _chatStickToBottom = true;
        }

        ChatJumpToNewest.IsVisible = !_chatStickToBottom && !atBottom;

        // Near the top (on user scroll): extend the window upwards (one step at a time).
        if (!extentChanged && !_chatStickToBottom && _chatOlderAnchorExtent == null &&
            sv.Offset.Y < Math.Max(120, sv.Viewport.Height * 0.5) &&
            DataContext is RawLogsApiTabViewModel vm && vm.ChatHasOlder)
        {
            vm.LoadOlderChatHistory(); // anchors via ChatOlderPrepending
        }
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

    private async void OnChatContentSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not RawLogsApiTabViewModel vm) return;
        if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await vm.SearchChatHistoryAsync(); }
        else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; vm.ClearChatHistorySearch(); }
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
        AttachChatScrollViewer(null);
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentVm != null) _currentVm.IsTabActive = false;
        DetachScrollViewer();
        DetachChatScrollViewer();
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
