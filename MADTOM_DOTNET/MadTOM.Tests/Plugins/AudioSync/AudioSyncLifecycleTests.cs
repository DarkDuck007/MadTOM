using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.AudioSync;
using MADTOM.Plugins.AudioSync.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.AudioSync;

public class AudioSyncLifecycleTests
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
    public void AudioSyncPluginModule_Metadata_IsValid()
    {
        var module = new AudioSyncPluginModule();
        Assert.Equal("audio-sync", module.Id);
        Assert.Equal("AUDIOSYNC", module.DisplayName);
        Assert.Equal(60, module.OrderWeight);
        Assert.Equal("Audio", module.Category);
        Assert.Equal("🔊", module.IconGlyph);
    }

    [Fact]
    public async Task AudioSyncPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new AudioSyncPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void AudioSync_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.AudioZones);
        Assert.Null(vm.SelectedZone);
        Assert.Contains("Coming soon", vm.MasterClockStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUDIOSYNC", vm.ModuleTitle);

        vm.CalibrateClockCommand.Execute(null);
        Assert.Contains("Coming soon", vm.MasterClockStatus, StringComparison.OrdinalIgnoreCase);
    }
}

