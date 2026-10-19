using System;
using MADTOM.Console.Hosting;
using MADTOM.Console.ViewModels;
using MADTOM.PluginContracts;
using Xunit;

namespace MadTOM.Tests;

public class StudioSidebarStateTests : IDisposable
{
    private ConsoleMainViewModel CreateViewModel()
    {
        var hostContext = new ConsoleHostContext();
        var pluginManager = new PluginManager(hostContext);
        var vm = new ConsoleMainViewModel(hostContext, pluginManager);
        vm.IsSidebarExpandedEnabled = true;
        vm.IsSidebarCollapsedEnabled = true;
        vm.IsSidebarHiddenEnabled = true;
        return vm;
    }

    public void Dispose()
    {
        AppSettingsStore.Save(new AppSettings());
    }

    [Fact]
    public void StudioSidebar_DefaultsToExpanded()
    {
        var vm = CreateViewModel();

        Assert.Equal(StudioSidebarState.Expanded, vm.SidebarState);
        Assert.True(vm.IsSidebarExpanded);
        Assert.False(vm.IsSidebarCollapsed);
        Assert.True(vm.IsSidebarVisible);
        Assert.Equal(220, vm.SidebarWidth);
        Assert.Equal("Expanded", vm.SidebarStateDescription);
    }

    [Fact]
    public void CycleSidebarState_CyclesThroughAllThreeStates_WhenAllEnabled()
    {
        var vm = CreateViewModel();
        vm.IsSidebarExpandedEnabled = true;
        vm.IsSidebarCollapsedEnabled = true;
        vm.IsSidebarHiddenEnabled = true;
        vm.SetSidebarState(StudioSidebarState.Expanded);

        // 1. Expanded -> Collapsed
        vm.CycleSidebarState();
        Assert.Equal(StudioSidebarState.Collapsed, vm.SidebarState);
        Assert.False(vm.IsSidebarExpanded);
        Assert.True(vm.IsSidebarCollapsed);
        Assert.True(vm.IsSidebarVisible);
        Assert.Equal(64, vm.SidebarWidth);
        Assert.Equal("Compact", vm.SidebarStateDescription);

        // 2. Collapsed -> Hidden
        vm.CycleSidebarState();
        Assert.Equal(StudioSidebarState.Hidden, vm.SidebarState);
        Assert.False(vm.IsSidebarExpanded);
        Assert.False(vm.IsSidebarCollapsed);
        Assert.False(vm.IsSidebarVisible);
        Assert.Equal(0, vm.SidebarWidth);
        Assert.Equal("Hidden", vm.SidebarStateDescription);

        // 3. Hidden -> Expanded
        vm.CycleSidebarState();
        Assert.Equal(StudioSidebarState.Expanded, vm.SidebarState);
        Assert.True(vm.IsSidebarExpanded);
        Assert.False(vm.IsSidebarCollapsed);
        Assert.True(vm.IsSidebarVisible);
        Assert.Equal(220, vm.SidebarWidth);
    }

    [Fact]
    public void CycleSidebarState_SkipsDisabledStates()
    {
        var vm = CreateViewModel();

        // Only Expanded and Hidden enabled (Collapsed disabled)
        vm.IsSidebarExpandedEnabled = true;
        vm.IsSidebarCollapsedEnabled = false;
        vm.IsSidebarHiddenEnabled = true;
        vm.SetSidebarState(StudioSidebarState.Expanded);

        // Expanded -> Hidden directly
        vm.CycleSidebarState();
        Assert.Equal(StudioSidebarState.Hidden, vm.SidebarState);
        Assert.False(vm.IsSidebarVisible);

        // Hidden -> Expanded directly
        vm.CycleSidebarState();
        Assert.Equal(StudioSidebarState.Expanded, vm.SidebarState);
        Assert.True(vm.IsSidebarVisible);
    }

    [Fact]
    public void EnablementConstraint_EnforcesAtLeastOneSelected()
    {
        var vm = CreateViewModel();
        vm.IsSidebarExpandedEnabled = true;
        vm.IsSidebarCollapsedEnabled = false;
        vm.IsSidebarHiddenEnabled = false;

        Assert.Equal(1, vm.EnabledStatesCount);

        // Attempting to disable the last enabled state must be rejected and remain true
        vm.IsSidebarExpandedEnabled = false;
        Assert.True(vm.IsSidebarExpandedEnabled);
        Assert.Equal(1, vm.EnabledStatesCount);
    }

    [Fact]
    public void DisablingCurrentState_AutoTransitionsToFirstEnabledState()
    {
        var vm = CreateViewModel();
        vm.IsSidebarExpandedEnabled = true;
        vm.IsSidebarCollapsedEnabled = true;
        vm.IsSidebarHiddenEnabled = true;

        vm.SetSidebarState(StudioSidebarState.Hidden);
        Assert.Equal(StudioSidebarState.Hidden, vm.SidebarState);

        // Disabling Hidden state should automatically transition to an enabled state
        vm.IsSidebarHiddenEnabled = false;
        Assert.NotEqual(StudioSidebarState.Hidden, vm.SidebarState);
        Assert.True(vm.IsStateEnabled(vm.SidebarState));
    }

    [Fact]
    public void SetSidebarState_ParsesStringOrEnumParameter()
    {
        var vm = CreateViewModel();

        vm.SetSidebarState("Collapsed");
        Assert.Equal(StudioSidebarState.Collapsed, vm.SidebarState);

        vm.SetSidebarState("Hidden");
        Assert.Equal(StudioSidebarState.Hidden, vm.SidebarState);

        vm.SetSidebarState(StudioSidebarState.Expanded);
        Assert.Equal(StudioSidebarState.Expanded, vm.SidebarState);
    }
}
