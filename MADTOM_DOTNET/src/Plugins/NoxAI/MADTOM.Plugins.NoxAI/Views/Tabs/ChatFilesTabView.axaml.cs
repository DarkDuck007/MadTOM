using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class ChatFilesTabView : UserControl
{
    // Left chat column (270) + spacing (12) + outer margins (32) + inner margins/scrollbar (~34)
    private const double ReservedWidth = 348;
    // Card width 210 + right margin 12
    private const double CardSlotWidth = 222;

    public ChatFilesTabView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateColumns(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Bounds.Width > 100) UpdateColumns(Bounds.Width);
    }

    private void UpdateColumns(double width)
    {
        if (DataContext is ChatFilesTabViewModel vm && width > 100)
        {
            vm.SetColumnCount(Math.Max(1, (int)((width - ReservedWidth) / CardSlotWidth)));
        }
    }

    private void OnCardAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ChatFileItemModel item } && DataContext is ChatFilesTabViewModel vm)
        {
            vm.OnItemEnteredViewport(item);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is ChatFilesTabViewModel vm && vm.IsPreviewOpen && vm.IsMultiPage)
        {
            if (e.Key == Key.Left && vm.CanPrevPage)
            {
                if (vm.PrevPageCommand.CanExecute(null))
                {
                    _ = vm.PrevPageCommand.ExecuteAsync(null);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Right && vm.CanNextPage)
            {
                if (vm.NextPageCommand.CanExecute(null))
                {
                    _ = vm.NextPageCommand.ExecuteAsync(null);
                }
                e.Handled = true;
            }
        }
    }
}
