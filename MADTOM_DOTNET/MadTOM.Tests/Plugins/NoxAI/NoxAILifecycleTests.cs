using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.NoxAI;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

public class NoxAILifecycleTests
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
    public void NoxAIPluginModule_Metadata_IsValid()
    {
        var module = new NoxAIPluginModule();
        Assert.Equal("nox-ai", module.Id);
        Assert.Equal("NOX AI", module.DisplayName);
        Assert.Equal(50, module.OrderWeight);
        Assert.Equal("AI", module.Category);
        Assert.Equal("🧠", module.IconGlyph);
    }

    [Fact]
    public async Task NoxAIPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new NoxAIPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void NoxAI_MainViewModel_InitializesPlaceholderState()
    {
        var vm = new MainViewModel();
        Assert.Empty(vm.LoadedModels);
        Assert.Null(vm.SelectedModel);
        Assert.Contains("Coming soon", vm.InferenceEngineState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOX AI", vm.ModuleTitle);

        vm.UnloadModelCommand.Execute(null);
        Assert.Contains("Coming soon", vm.InferenceEngineState, StringComparison.OrdinalIgnoreCase);
    }
}

