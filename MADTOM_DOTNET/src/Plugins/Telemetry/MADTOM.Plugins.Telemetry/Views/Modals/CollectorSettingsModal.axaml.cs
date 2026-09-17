using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MadTOM.Common;
using MadTOM.ViewModels;

namespace MadTOM.Views.Modals;

public partial class CollectorSettingsModal : UserControl
{
    private readonly Avalonia.Threading.DispatcherTimer _cacheStatsTimer = new() { Interval = System.TimeSpan.FromSeconds(2) };

    public CollectorSettingsModal()
    {
        InitializeComponent();
        _cacheStatsTimer.Tick += (_, _) => (DataContext as CollectorSettingsViewModel)?.RefreshCacheStats();
        AddHandler(KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape && DataContext is CollectorSettingsViewModel vm)
            {
                if (vm.IsUnsavedPromptOpen)
                {
                    vm.CancelClosePrompt();
                    e.Handled = true;
                }
                else
                {
                    vm.CloseCommand.Execute(null);
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
                minWidth: 520, minHeight: 400);
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cacheStatsTimer.Start();
        this.FindControl<ScrollViewer>("NodeSettingsScrollViewer")?.ScrollToHome();
    }
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _cacheStatsTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

}

