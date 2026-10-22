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

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            _currentVm.ScrollToTopRequested -= OnScrollToTopRequested;
        }

        if (DataContext is RawLogsApiTabViewModel vm)
        {
            _currentVm = vm;
            _currentVm.ScrollToTopRequested += OnScrollToTopRequested;
        }
        else
        {
            _currentVm = null;
        }
    }

    private ScrollViewer? _attachedScrollViewer;

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DetachScrollViewer();
        _attachedScrollViewer = ApiCallsListBox.FindDescendantOfType<ScrollViewer>();
        if (_attachedScrollViewer != null)
        {
            _attachedScrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
