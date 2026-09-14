using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MadTOM.Common;
using MadTOM.ViewModels;

namespace MadTOM.Views.Modals;

public partial class CustomizeGraphsModal : UserControl
{
    public CustomizeGraphsModal()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape && DataContext is HostMetricsTabViewModel vm)
            {
                if (vm.IsCustomProcessModalOpen)
                {
                    vm.CancelCustomProcess();
                    e.Handled = true;
                }
                else if (vm.IsSavePresetModalOpen)
                {
                    vm.CloseSavePresetModal();
                    e.Handled = true;
                }
                else
                {
                    vm.CloseCustomizationModal();
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        var dialog = this.FindControl<Border>("DialogContainer");
        var resizeLeft = this.FindControl<Border>("ResizeLeft");
        var resizeRight = this.FindControl<Border>("ResizeRight");
        var resizeTop = this.FindControl<Border>("ResizeTop");
        var resizeBottom = this.FindControl<Border>("ResizeBottom");
        var resizeTopLeft = this.FindControl<Border>("ResizeTopLeft");
        var resizeTopRight = this.FindControl<Border>("ResizeTopRight");
        var resizeBottomLeft = this.FindControl<Border>("ResizeBottomLeft");
        var resizeBottomRight = this.FindControl<Border>("ResizeBottomRight");

        if (dialog != null && resizeLeft != null && resizeRight != null &&
            resizeTop != null && resizeBottom != null &&
            resizeTopLeft != null && resizeTopRight != null &&
            resizeBottomLeft != null && resizeBottomRight != null)
        {
            CenteredDialogResizer.Attach(
                dialog,
                resizeLeft, resizeRight, resizeTop, resizeBottom,
                resizeTopLeft, resizeTopRight, resizeBottomLeft, resizeBottomRight,
                minWidth: 520, minHeight: 420);
        }
    }
}

