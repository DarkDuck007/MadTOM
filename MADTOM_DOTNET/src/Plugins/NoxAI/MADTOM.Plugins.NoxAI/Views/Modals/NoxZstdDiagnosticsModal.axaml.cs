using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Modals;

public partial class NoxZstdDiagnosticsModal : UserControl
{
    public NoxZstdDiagnosticsModal()
    {
        InitializeComponent();
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseZstdDiagnosticsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}
