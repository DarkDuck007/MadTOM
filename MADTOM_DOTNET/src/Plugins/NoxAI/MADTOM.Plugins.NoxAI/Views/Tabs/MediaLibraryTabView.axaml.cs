using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class MediaLibraryTabView : UserControl
{
    public MediaLibraryTabView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MediaLibraryTabViewModel vm)
            {
                _ = vm.ExecuteSemanticSearch();
            }
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateColumns(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Bounds.Width > 100)
        {
            UpdateColumns(Bounds.Width);
        }
    }

    private void UpdateColumns(double width)
    {
        if (DataContext is MediaLibraryTabViewModel vm && width > 100)
        {
            // Margin = 14 on left, ~14 on right + scrollbar width (~20) = ~48 padding
            double availableWidth = width - 48;
            // Card width = 195, right margin = 12 => ~207 per column
            int cols = Math.Max(1, (int)(availableWidth / 207));
            vm.SetColumnCount(cols);
        }
    }

    private void OnCardAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: MediaItemModel item } && DataContext is MediaLibraryTabViewModel vm)
        {
            vm.OnItemEnteredViewport(item);
        }
    }

    private void OnCardDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: MediaItemModel item } && DataContext is MediaLibraryTabViewModel vm)
        {
            vm.OnItemExitedViewport(item);
        }
    }
}
