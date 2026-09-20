using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.NoxAI.ViewModels;
using MADTOM.Plugins.NoxAI.Views;

namespace MADTOM.Plugins.NoxAI;

public sealed class NoxAIPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "nox-ai";
    public string DisplayName => "NOX AI";
    public string Description => "Local LLM orchestrator, GPU inference manager, and prompt context memory pipeline";
    public string IconGlyph => "🧠";
    public string Category => "AI";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 50;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new NoxAITrayMenuSection(_mainViewModel);
            _trayRegistration = hostContext.Tray.RegisterSection(section);
        }

        return Task.CompletedTask;
    }

    public Control CreateView()
    {
        _mainViewModel ??= new MainViewModel();
        _mainView = new MainView
        {
            DataContext = _mainViewModel
        };
        return _mainView;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync()
    {
        _trayRegistration?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class NoxAITrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "nox-ai";
    public string PluginName => "NOX AI";
    public int OrderWeight => 50;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public NoxAITrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Inference: " + _mainViewModel.InferenceEngineState),
            TrayMenuItemDescriptor.TextItem("GPU: " + _mainViewModel.GpuStats)
        };
    }
}
