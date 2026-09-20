using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.AndroidToolkit;
using MADTOM.Plugins.AndroidToolkit.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.AndroidToolkit;

public class AndroidToolkitLifecycleTests
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
    public void AndroidToolkitPluginModule_Metadata_IsValid()
    {
        var module = new AndroidToolkitPluginModule();
        Assert.Equal("android-toolkit", module.Id);
        Assert.Equal("Android Toolkit", module.DisplayName);
        Assert.Equal(30, module.OrderWeight);
        Assert.Equal("Mobile", module.Category);
        Assert.Equal("📱", module.IconGlyph);
    }

    [Fact]
    public async Task AndroidToolkitPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new AndroidToolkitPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void AndroidToolkit_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.Devices);
        Assert.Contains("Coming soon", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANDROID TOOLKIT", vm.ModuleTitle);

        vm.RefreshDevicesCommand.Execute(null);
        Assert.Contains("Coming soon", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}

