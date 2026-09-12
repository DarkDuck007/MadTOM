using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Localization;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly ILexiconService _lexiconService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private ViewModelBase _currentView;

    public HeaderViewModel Header { get; }
    public SidebarViewModel Sidebar { get; }
    public FleetViewModel FleetView { get; }
    public HostDetailViewModel HostDetailView { get; }
    public GlobalRadarViewModel GlobalRadarView { get; }

    // Action Modal State
    [ObservableProperty]
    private bool _isActionModalOpen;

    [ObservableProperty]
    private string _modalTitle = "Confirm Remote Signal";

    [ObservableProperty]
    private string _modalHostId = string.Empty;

    [ObservableProperty]
    private string _modalCommandName = string.Empty;

    [ObservableProperty]
    private int _modalPid;

    [ObservableProperty]
    private int _modalSignal;

    [ObservableProperty]
    private string _modalSignalName = "SIGTERM (15)";

    // Toast State
    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private string _toastIcon = "ℹ️";

    private DispatcherTimer? _toastTimer;

    public CollectorSettingsViewModel CollectorSettings { get; }

    [ObservableProperty]
    private bool _isCollectorSettingsOpen;

    public MainViewModel(
        ITelemetryDataProvider telemetryProvider,
        ILexiconService lexiconService,
        INotificationService notificationService)
    {
        _telemetryProvider = telemetryProvider;
        _lexiconService = lexiconService;
        _notificationService = notificationService;

        var nodeGroupStore = new NodeGroupStore();
        var globalMetricsStore = new GlobalMetricsStore();

        Header = new HeaderViewModel(_lexiconService, _telemetryProvider, globalMetricsStore);
        Sidebar = new SidebarViewModel(_telemetryProvider);

        var metricsTab = new HostMetricsTabViewModel(_telemetryProvider, new GraphLayoutStore(GraphLayoutStore.DefaultPath));
        var processesTab = new HostProcessesTabViewModel(_telemetryProvider);
        var logsTab = new HostLogsTabViewModel(_telemetryProvider);
        var flightTab = new HostFlightTabViewModel(_telemetryProvider);

        HostDetailView = new HostDetailViewModel(_telemetryProvider, _lexiconService, metricsTab, processesTab, logsTab, flightTab);
        FleetView = new FleetViewModel(_telemetryProvider, nodeGroupStore);
        GlobalRadarView = new GlobalRadarViewModel(_telemetryProvider);

        var manager = (_telemetryProvider as CollectorTelemetryDataProvider)?.CollectorManager ?? new MultiCollectorManager();
        CollectorSettings = new CollectorSettingsViewModel(manager, _telemetryProvider, nodeGroupStore, globalMetricsStore);
        CollectorSettings.CloseRequested += () => IsCollectorSettingsOpen = false;

        _currentView = FleetView;

        // Wire navigation events
        Sidebar.ViewChangeRequested += NavigateToView;
        Sidebar.HostSelected += OpenHostDetail;
        FleetView.OpenHostDetailRequested += OpenHostDetail;
        FleetView.OpenCollectorSettingsRequested += OpenCollectorSettings;
        Header.OpenGlobalMetricsRequested += () =>
        {
            CollectorSettings.RefreshNodes();
            CollectorSettings.SelectedTabIndex = 3;
            IsCollectorSettingsOpen = true;
        };
        FleetView.ConfigureNodeRequested += (node) =>
        {
            CollectorSettings.RefreshNodes();
            CollectorSettings.SelectedNode = node;
            CollectorSettings.SelectedTabIndex = 1;
            IsCollectorSettingsOpen = true;
        };
        HostDetailView.BackToFleetRequested += () => NavigateToView("fleet");

        // Wire sidebar collapse sync
        var initialSettings = MADTOM.PluginContracts.AppSettingsStore.Load();
        Sidebar.IsCollapsed = initialSettings.IsTelemetrySidebarCollapsed;
        Header.IsSidebarCollapsed = Sidebar.IsCollapsed;

        Header.ToggleSidebarCollapseRequested += () => Sidebar.ToggleCollapse();
        Sidebar.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Sidebar.IsCollapsed))
            {
                Header.IsSidebarCollapsed = Sidebar.IsCollapsed;
                var currentSettings = MADTOM.PluginContracts.AppSettingsStore.Load();
                currentSettings.IsTelemetrySidebarCollapsed = Sidebar.IsCollapsed;
                MADTOM.PluginContracts.AppSettingsStore.Save(currentSettings);
            }
        };

        // Wire process signal confirmation
        processesTab.ActionConfirmationRequested += (pid, name, signal, hostId) =>
        {
            ModalPid = pid;
            ModalCommandName = name;
            ModalSignal = signal;
            ModalHostId = hostId;
            ModalSignalName = signal == 9 ? "SIGKILL (9)" : "SIGTERM (15)";
            ModalTitle = $"Dispatch {ModalSignalName} to PID {pid}";
            IsActionModalOpen = true;
        };

        // Wire toast notification
        _notificationService.ToastRequested += (s, args) =>
        {
            ToastMessage = args.Message;
            ToastIcon = args.Icon;
            IsToastVisible = true;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2600)
            };
            _toastTimer.Tick += (ts, te) =>
            {
                IsToastVisible = false;
                _toastTimer.Stop();
            };
            _toastTimer.Start();
        };
    }

    public MainViewModel() : this(new CollectorTelemetryDataProvider(new MultiCollectorManager()), LexiconService.Instance, NotificationService.Instance)
    {
    }

    public void NavigateToView(string viewKey)
    {
        CurrentView = viewKey.ToLowerInvariant() switch
        {
            "fleet" => FleetView,
            "detail" => HostDetailView,
            "radar" => GlobalRadarView,
            _ => FleetView
        };
        Sidebar.ActiveView = viewKey.ToLowerInvariant();
    }

    public void OpenHostDetail(string hostId)
    {
        HostDetailView.SelectHost(hostId);
        CurrentView = HostDetailView;
        Sidebar.ActiveView = "detail";
    }

    [RelayCommand]
    public void ConfirmActionModal()
    {
        _telemetryProvider.SendSignal(ModalHostId, ModalPid, ModalSignal);
        IsActionModalOpen = false;
    }

    [RelayCommand]
    public void CancelActionModal()
    {
        IsActionModalOpen = false;
    }

    [RelayCommand]
    public void OpenCollectorSettings()
    {
        CollectorSettings.RefreshNodes();
        IsCollectorSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseCollectorSettings()
    {
        IsCollectorSettingsOpen = false;
    }
}
