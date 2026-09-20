using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.ConnectionToolkit.ViewModels;
using MADTOM.Plugins.ConnectionToolkit.Views;

namespace MADTOM.Plugins.ConnectionToolkit;

public sealed class ConnectionToolkitPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "connection-toolkit";
    public string DisplayName => "Connection Toolkit";
    public string Description => "Unified connection manager: SSH tunnels, WireGuard/VPN links, serial bridges, and port forwarding";
    public string IconGlyph => "🔌";
    public string Category => "Connectivity";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 80;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new ConnectionToolkitTrayMenuSection(_mainViewModel);
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

public sealed class ConnectionToolkitTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "connection-toolkit";
    public string PluginName => "Connection Toolkit";
    public int OrderWeight => 80;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public ConnectionToolkitTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Tunnels: " + _mainViewModel.TunnelStatus),
            TrayMenuItemDescriptor.TextItem("VPN: " + _mainViewModel.VpnStatus)
        };
    }
}

