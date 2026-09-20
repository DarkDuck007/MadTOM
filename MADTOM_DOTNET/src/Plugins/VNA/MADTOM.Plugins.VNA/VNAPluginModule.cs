using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.VNA.ViewModels;
using MADTOM.Plugins.VNA.Views;

namespace MADTOM.Plugins.VNA;

public sealed class VNAPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "vna";
    public string DisplayName => "VNA";
    public string Description => "Visual Network Analyzer: real-time packet flow visualization, protocol graphs, and latency topology";
    public string IconGlyph => "🌐";
    public string Category => "Network";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 70;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new VNATrayMenuSection(_mainViewModel);
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

public sealed class VNATrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "vna";
    public string PluginName => "VNA";
    public int OrderWeight => 70;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public VNATrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Capture: " + _mainViewModel.CaptureStatus),
            TrayMenuItemDescriptor.TextItem("Topology: " + _mainViewModel.TopologyState)
        };
    }
}

