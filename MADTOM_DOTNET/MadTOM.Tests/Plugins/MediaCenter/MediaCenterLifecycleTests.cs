using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.MediaCenter;
using MADTOM.Plugins.MediaCenter.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.MediaCenter;

public class MediaCenterLifecycleTests
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
    public void MediaCenterPluginModule_Metadata_IsValid()
    {
        var module = new MediaCenterPluginModule();
        Assert.Equal("media-center", module.Id);
        Assert.Equal("MediaCenter", module.DisplayName);
        Assert.Equal(40, module.OrderWeight);
        Assert.Equal("Media", module.Category);
        Assert.Equal("📺", module.IconGlyph);
    }

    [Fact]
    public async Task MediaCenterPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new MediaCenterPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void MediaCenter_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.LibraryItems);
        Assert.Contains("Coming soon", vm.LibraryStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MEDIA CENTER", vm.ModuleTitle);

        vm.PlayMediaCommand.Execute(null);
        Assert.Contains("Coming soon", vm.ActivePlayerState, StringComparison.OrdinalIgnoreCase);
    }
}

