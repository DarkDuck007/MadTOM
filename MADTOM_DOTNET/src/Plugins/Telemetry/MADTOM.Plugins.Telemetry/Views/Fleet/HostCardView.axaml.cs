using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
        if (e.Source is Visual visual)
        {
            var current = visual;
            while (current != null && current != this)
            {
                if (current is Button) return;
                current = current.GetVisualParent();
            }
        }

        if (DataContext is FleetNodeCardViewModel vm)
        {
            vm.OpenDetailCommand.Execute(null);
            e.Handled = true;
        }
    }
}

