using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.SlideForge.ViewModels;
using MADTOM.Plugins.SlideForge.Views;

namespace MADTOM.Plugins.SlideForge;

public sealed class SlideForgePluginModule : IPluginModule
{
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "slideforge";
    public string DisplayName => "SlideForge";
    public string Description => "Monitor and administer SlideForge document-generation bot via slideforge-probe";
    public string IconGlyph => "🎞️";
    public string Category => "AI";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 125;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new SlideForgeTrayMenuSection(_mainViewModel);
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
        _mainViewModel?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SlideForgeTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "slideforge";
    public string PluginName => "SlideForge";
    public int OrderWeight => 55;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public SlideForgeTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        string nodeName = _mainViewModel.SelectedNode?.AliasOrId ?? "No Node";
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Active Node: " + nodeName),
            TrayMenuItemDescriptor.TextItem("Status: " + _mainViewModel.ConnectionStatusBadge),
            TrayMenuItemDescriptor.TextItem(_mainViewModel.BotStatusSummary)
        };
    }
}
