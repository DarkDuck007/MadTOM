using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MadTOM.Views;

public partial class TelemetryRootView : UserControl
{
    public TelemetryRootView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ViewModels.MainViewModel vm)
        {
            if (vm.HostDetailView.MetricsTab.IsCustomizationModalOpen)
            {
                if (vm.HostDetailView.MetricsTab.IsCustomProcessModalOpen)
                {
                    vm.HostDetailView.MetricsTab.CancelCustomProcess();
                    e.Handled = true;
                }
                else if (vm.HostDetailView.MetricsTab.IsSavePresetModalOpen)
                {
                    vm.HostDetailView.MetricsTab.CloseSavePresetModal();
                    e.Handled = true;
                }
                else
                {
                    vm.HostDetailView.MetricsTab.CloseCustomizationModal();
                    e.Handled = true;
                }
            }
            else if (vm.HostDetailView.MetricsTab.IsCustomScopeModalOpen)
            {
                vm.HostDetailView.MetricsTab.CancelCustomScope();
                e.Handled = true;
            }
            else if (vm.IsCollectorSettingsOpen)
            {
                if (vm.CollectorSettings.IsUnsavedPromptOpen)
                {
                    vm.CollectorSettings.CancelClosePrompt();
                    e.Handled = true;
                }
                else
                {
                    vm.CollectorSettings.CloseCommand.Execute(null);
                    e.Handled = true;
                }
            }
            else if (vm.IsActionModalOpen)
            {
                vm.IsActionModalOpen = false;
                e.Handled = true;
            }
        }
    }

    private void OnSidebarSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && !vm.Sidebar.IsCollapsed)
        {
            vm.Sidebar.SidebarWidth += e.Vector.X;
        }
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
