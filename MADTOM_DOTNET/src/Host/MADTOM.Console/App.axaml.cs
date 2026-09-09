using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MADTOM.Console.Hosting;
using MADTOM.Console.ViewModels;
using MADTOM.Console.Views;
using MadTOM;

namespace MADTOM.Console;

public partial class App : Application
{
    private PluginManager? _pluginManager;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var hostContext = new ConsoleHostContext();
            _pluginManager = new PluginManager(hostContext);

            // Register primary Telemetry plugin
            var telemetryModule = new TelemetryPluginModule();
            await _pluginManager.RegisterModuleAsync(telemetryModule);

            // Register sample plugins for multi-module switching
            await _pluginManager.RegisterModuleAsync(new SysadminPlaceholderPluginModule());
            await _pluginManager.RegisterModuleAsync(new EscPlaceholderPluginModule());

            var viewModel = new ConsoleMainViewModel(hostContext, _pluginManager);

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.Exit += async (_, _) =>
            {
                if (_pluginManager != null)
                {
                    await _pluginManager.DisposeAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

