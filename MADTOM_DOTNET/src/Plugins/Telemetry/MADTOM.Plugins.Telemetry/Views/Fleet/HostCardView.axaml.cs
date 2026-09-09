using Avalonia.Controls;
using Avalonia.Input;
using MadTOM.ViewModels;

namespace MadTOM.Views.Fleet;

public partial class HostCardView : UserControl
{
    public HostCardView()
    {
        InitializeComponent();
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is FleetNodeCardViewModel vm)
        {
            vm.OpenDetailCommand.Execute(null);
            e.Handled = true;
        }
    }
}

