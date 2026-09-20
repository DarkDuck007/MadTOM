using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.VNA;
using MADTOM.Plugins.VNA.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.VNA;

public class VNALifecycleTests
{
    private class DummyHostContext : IPluginHostContext
    {
        public IThemeHost Themes => null!;
        public ILexiconHost Lexicons => null!;
        public ITrayMenuService? Tray { get; set; } = new TrayMenuService();
        public IPluginNotificationService Notifications => null!;
        public IMessenger Messenger => WeakReferenceMessenger.Default;
    }

    [Fact]
    public void VNAPluginModule_Metadata_IsValid()
    {
        var module = new VNAPluginModule();
        Assert.Equal("vna", module.Id);
        Assert.Equal("VNA", module.DisplayName);
        Assert.Equal(70, module.OrderWeight);
        Assert.Equal("Network", module.Category);
        Assert.Equal("🌐", module.IconGlyph);
    }

    [Fact]
    public async Task VNAPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new VNAPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void VNA_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.ActiveFlows);
        Assert.Null(vm.SelectedFlow);
        Assert.Contains("Coming soon", vm.CaptureStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISUAL NETWORK ANALYZER", vm.ModuleTitle);

        vm.StartCaptureCommand.Execute(null);
        Assert.Contains("Coming soon", vm.CaptureStatus, StringComparison.OrdinalIgnoreCase);
    }
}

