using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MadTOM.Views;

public partial class TelemetryRootView : UserControl
{
    public TelemetryRootView()
    {
        InitializeComponent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        // Defensive hygiene: clear any focused element inside this visual tree
        // to prevent Avalonia's FocusManager from retaining this branch in memory.
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.Focus(null);
    }
}
