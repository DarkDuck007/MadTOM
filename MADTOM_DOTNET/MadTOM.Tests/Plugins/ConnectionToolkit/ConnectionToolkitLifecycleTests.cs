using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.ConnectionToolkit;
using MADTOM.Plugins.ConnectionToolkit.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.ConnectionToolkit;

public class ConnectionToolkitLifecycleTests
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
    public void ConnectionToolkitPluginModule_Metadata_IsValid()
    {
        var module = new ConnectionToolkitPluginModule();
        Assert.Equal("connection-toolkit", module.Id);
        Assert.Equal("Connection Toolkit", module.DisplayName);
        Assert.Equal(80, module.OrderWeight);
        Assert.Equal("Connectivity", module.Category);
        Assert.Equal("🔌", module.IconGlyph);
    }

    [Fact]
    public async Task ConnectionToolkitPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new ConnectionToolkitPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void ConnectionToolkit_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.ConfiguredEndpoints);
        Assert.Null(vm.SelectedEndpoint);
        Assert.Contains("Coming soon", vm.TunnelStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONNECTION TOOLKIT", vm.ModuleTitle);

        vm.ConnectAllCommand.Execute(null);
        Assert.Contains("Coming soon", vm.TunnelStatus, StringComparison.OrdinalIgnoreCase);
    }
}

