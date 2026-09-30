using Avalonia.Controls;
using Avalonia.Input;
using MadTOM.ViewModels;

namespace MadTOM.Views.Modals;

public partial class CompressionDiagnosticsModal : UserControl
{
    public CompressionDiagnosticsModal()
    {
        InitializeComponent();
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SidebarViewModel vm)
        {
            vm.CloseCompressionModal();
            e.Handled = true;
        }
    }

    private void OnContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}

