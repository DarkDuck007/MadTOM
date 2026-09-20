using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.AndroidToolkit.ViewModels;
using MADTOM.Plugins.AndroidToolkit.Views;

namespace MADTOM.Plugins.AndroidToolkit;

public sealed class AndroidToolkitPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "android-toolkit";
    public string DisplayName => "Android Toolkit";
    public string Description => "ADB backup, SMS/MMS export, app data archiving, and device sync";
    public string IconGlyph => "📱";
    public string Category => "Mobile";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 30;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new AndroidToolkitTrayMenuSection(_mainViewModel);
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

public sealed class AndroidToolkitTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "android-toolkit";
    public string PluginName => "Android Toolkit";
    public int OrderWeight => 30;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public AndroidToolkitTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        var count = _mainViewModel.Devices.Count;
        var deviceText = count > 0 ? $"{count} Device(s) Connected" : "No Devices Attached";

        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem($"ADB: {deviceText}")
        };
    }
}
