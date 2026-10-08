using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQUEEZE.Models;
using SQUEEZE.Services;

namespace SQUEEZE.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ITranscoderBackendService _backendService;
    private readonly IServerDiscoveryService _discoveryService;
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SQUEEZE",
        "settings.json");

    private readonly object _autoConnectLock = new();
    private CancellationTokenSource? _reactiveAutoConnectCts;

    [ObservableProperty]
    private string _serverUrl = "http://127.0.0.1:8080";

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private bool _autoConnect = true;

    [ObservableProperty]
    private string? _lastConnectedNodeId;

    [ObservableProperty]
    private string _statusMessage = "Ready to connect";

    [ObservableProperty]
    private string _statusColor = "#8B949E";

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private ObservableCollection<DiscoveredServer> _discoveredServers = new();

    [ObservableProperty]
    private ObservableCollection<DiscoveredNodeViewModel> _discoveredNodes = new();

    [ObservableProperty]
    private ObservableCollection<AutoConnectPriorityItemViewModel> _autoConnectPriorityList = new();

    [ObservableProperty]
    private DiscoveredServer? _selectedDiscoveredServer;

    public Action? OnClose { get; set; }
    public Action<bool>? OnConnectionChanged { get; set; }

    public SettingsViewModel(ITranscoderBackendService backendService, IServerDiscoveryService discoveryService)
    {
        _backendService = backendService;
        _discoveryService = discoveryService;

        _discoveryService.ServerDiscovered += OnServerDiscovered;
        LoadSavedSettings();

        // Initial scan for LAN servers
        _ = ScanNetwork();
    }

    private void RunOnUi(Action action)
    {
        if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    private void OnServerDiscovered(object? sender, DiscoveredServer server)
    {
        RunOnUi(() =>
        {
            // Update raw discovered servers list (for backwards compatibility)
            var existingServer = DiscoveredServers.FirstOrDefault(s => s.BaseUrl == server.BaseUrl);
            if (existingServer != null)
            {
                existingServer.LastSeen = server.LastSeen;
            }
            else
            {
                DiscoveredServers.Add(server);
            }

            // Aggregate by NodeId
            var nodeVm = DiscoveredNodes.FirstOrDefault(n =>
                string.Equals(n.NodeId, server.NodeId, StringComparison.OrdinalIgnoreCase));

            // Check if there is a preferred interface configured in the priority list for this node
            var priorityPref = AutoConnectPriorityList.FirstOrDefault(p =>
                string.Equals(p.NodeId, server.NodeId, StringComparison.OrdinalIgnoreCase));
            string? preferredUrl = priorityPref?.PriorityInterface;

            if (nodeVm == null)
            {
                nodeVm = new DiscoveredNodeViewModel
                {
                    NodeId = server.NodeId,
                    OnInterfaceSelectionChanged = (node, iface) =>
                    {
                        SetNodePriorityInterface(node.NodeId, iface.BaseUrl);
                    }
                };
                nodeVm.AddOrUpdateInterface(server, preferredUrl);
                DiscoveredNodes.Add(nodeVm);
            }
            else
            {
                nodeVm.AddOrUpdateInterface(server, preferredUrl);
            }

            // Update priority list item availability and available interfaces
            if (priorityPref != null)
            {
                priorityPref.IsAvailable = true;
                foreach (var iface in nodeVm.Interfaces)
                {
                    if (!priorityPref.AvailableInterfaces.Contains(iface.BaseUrl))
                    {
                        priorityPref.AvailableInterfaces.Add(iface.BaseUrl);
                    }
                }
            }
        });

        // Trigger reactive auto-connect if disconnected and this node is in the priority list
        if (AutoConnect && !_backendService.IsConnected && !IsBusy)
        {
            bool isKnownNode = AutoConnectPriorityList.Any(p =>
                string.Equals(p.NodeId, server.NodeId, StringComparison.OrdinalIgnoreCase));
            if (isKnownNode)
            {
                TriggerReactiveAutoConnect();
            }
        }
    }

    private void TriggerReactiveAutoConnect()
    {
        lock (_autoConnectLock)
        {
            _reactiveAutoConnectCts?.Cancel();
            _reactiveAutoConnectCts = new CancellationTokenSource();
            var token = _reactiveAutoConnectCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Debounce window to let multiple interfaces of discovered devices arrive
                    await Task.Delay(800, token);
                    if (token.IsCancellationRequested || _backendService.IsConnected) return;
                    await TryAutoConnectAsync(token);
                }
                catch (OperationCanceledException) { }
                catch { }
            }, token);
        }
    }

    public void SetNodePriorityInterface(string nodeId, string interfaceUrl)
    {
        RunOnUi(() =>
        {
            var existing = AutoConnectPriorityList.FirstOrDefault(p =>
                string.Equals(p.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.PriorityInterface = interfaceUrl;
                if (!existing.AvailableInterfaces.Contains(interfaceUrl))
                {
                    existing.AvailableInterfaces.Add(interfaceUrl);
                }
            }
            else
            {
                var newItem = new AutoConnectPriorityItemViewModel
                {
                    NodeId = nodeId,
                    PriorityInterface = interfaceUrl,
                    AuthToken = AuthToken,
                    LastConnected = DateTime.UtcNow,
                    PriorityRank = AutoConnectPriorityList.Count + 1,
                    IsAvailable = true
                };
                newItem.AvailableInterfaces.Add(interfaceUrl);
                AutoConnectPriorityList.Add(newItem);
            }
            UpdatePriorityRanks();
            SaveSettings();
        });
    }

    public void RecordSuccessfulConnection(string nodeId, string baseUrl)
    {
        RunOnUi(() =>
        {
            ServerUrl = baseUrl;
            LastConnectedNodeId = nodeId;

            var existing = AutoConnectPriorityList.FirstOrDefault(p =>
                string.Equals(p.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.PriorityInterface = baseUrl;
                existing.LastConnected = DateTime.UtcNow;
                if (!existing.AvailableInterfaces.Contains(baseUrl))
                {
                    existing.AvailableInterfaces.Add(baseUrl);
                }
            }
            else
            {
                var newItem = new AutoConnectPriorityItemViewModel
                {
                    NodeId = nodeId,
                    PriorityInterface = baseUrl,
                    AuthToken = AuthToken,
                    LastConnected = DateTime.UtcNow,
                    PriorityRank = AutoConnectPriorityList.Count + 1,
                    IsAvailable = true
                };
                newItem.AvailableInterfaces.Add(baseUrl);
                AutoConnectPriorityList.Add(newItem);
            }

            UpdatePriorityRanks();
            SaveSettings();
        });
    }

    private void UpdatePriorityRanks()
    {
        for (int i = 0; i < AutoConnectPriorityList.Count; i++)
        {
            AutoConnectPriorityList[i].PriorityRank = i + 1;
        }
    }

    [RelayCommand]
    public void MovePriorityUp(AutoConnectPriorityItemViewModel? item)
    {
        if (item == null) return;
        int idx = AutoConnectPriorityList.IndexOf(item);
        if (idx > 0)
        {
            AutoConnectPriorityList.Move(idx, idx - 1);
            UpdatePriorityRanks();
            SaveSettings();
        }
    }

    [RelayCommand]
    public void MovePriorityDown(AutoConnectPriorityItemViewModel? item)
    {
        if (item == null) return;
        int idx = AutoConnectPriorityList.IndexOf(item);
        if (idx >= 0 && idx < AutoConnectPriorityList.Count - 1)
        {
            AutoConnectPriorityList.Move(idx, idx + 1);
            UpdatePriorityRanks();
            SaveSettings();
        }
    }

    [RelayCommand]
    public void RemovePriorityItem(AutoConnectPriorityItemViewModel? item)
    {
        if (item == null) return;
        AutoConnectPriorityList.Remove(item);
        UpdatePriorityRanks();
        SaveSettings();
    }

    [RelayCommand]
    public async Task ConnectToPriorityItem(AutoConnectPriorityItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.PriorityInterface)) return;
        ServerUrl = item.PriorityInterface;
        if (!string.IsNullOrWhiteSpace(item.AuthToken))
        {
            AuthToken = item.AuthToken;
        }
        await Connect();
    }

    [RelayCommand]
    public async Task SelectAndConnectNode(DiscoveredNodeViewModel? node)
    {
        if (node == null || node.SelectedInterface == null) return;

        ServerUrl = node.SelectedInterface.BaseUrl;
        SetNodePriorityInterface(node.NodeId, node.SelectedInterface.BaseUrl);
        await Connect();
    }

    [RelayCommand]
    public async Task SelectAndConnect(DiscoveredServer? server)
    {
        if (server == null) return;
        ServerUrl = server.BaseUrl;
        SetNodePriorityInterface(server.NodeId, server.BaseUrl);
        await Connect();
    }

    [RelayCommand]
    public async Task TestConnection()
    {
        IsBusy = true;
        StatusMessage = "Testing connection...";
        StatusColor = "#58A6FF";

        try
        {
            bool success = await _backendService.TestConnectionAsync(ServerUrl, AuthToken);
            if (success)
            {
                StatusMessage = "Connection successful! Server online.";
                StatusColor = "#3FB950";
            }
            else
            {
                StatusMessage = "Connection failed. Check host, port, or auth token.";
                StatusColor = "#F85149";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            StatusColor = "#F85149";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task Connect()
    {
        IsBusy = true;
        StatusMessage = "Connecting to server...";
        StatusColor = "#58A6FF";

        try
        {
            bool success = await _backendService.ConnectAsync(ServerUrl, AuthToken);
            if (success)
            {
                StatusMessage = "Connected successfully!";
                StatusColor = "#3FB950";

                var nodeInfo = await _backendService.GetNodeInfoAsync();
                string nodeId = !string.IsNullOrWhiteSpace(nodeInfo.NodeName) ? nodeInfo.NodeName : "SQUEEZE Node";
                RecordSuccessfulConnection(nodeId, ServerUrl);

                OnConnectionChanged?.Invoke(true);
            }
            else
            {
                StatusMessage = "Connection failed. Server unreachable.";
                StatusColor = "#F85149";
                OnConnectionChanged?.Invoke(false);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
            StatusColor = "#F85149";
            OnConnectionChanged?.Invoke(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ScanNetwork()
    {
        IsBusy = true;
        StatusMessage = "Scanning network for SQUEEZE nodes...";
        StatusColor = "#58A6FF";

        try
        {
            await _discoveryService.ScanNetworkAsync();
            StatusMessage = $"Scan finished. Found {DiscoveredNodes.Count} node(s).";
            StatusColor = "#8B949E";
        }
        catch
        {
            StatusMessage = "Network scan failed.";
            StatusColor = "#F85149";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> TryAutoConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!AutoConnect) return false;

        if (AutoConnectPriorityList.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(ServerUrl))
            {
                bool ok = await _backendService.ConnectAsync(ServerUrl, AuthToken);
                if (ok)
                {
                    var info = await _backendService.GetNodeInfoAsync();
                    RecordSuccessfulConnection(info.NodeName ?? "SQUEEZE Node", ServerUrl);
                    OnConnectionChanged?.Invoke(true);
                    return true;
                }
            }
            return false;
        }

        // Fast Path: Try top priority item directly on its priority interface
        var top = AutoConnectPriorityList[0];
        if (!string.IsNullOrWhiteSpace(top.PriorityInterface))
        {
            try
            {
                bool fastOk = await _backendService.TestConnectionAsync(top.PriorityInterface, top.AuthToken ?? AuthToken);
                if (fastOk)
                {
                    bool connected = await _backendService.ConnectAsync(top.PriorityInterface, top.AuthToken ?? AuthToken);
                    if (connected)
                    {
                        RecordSuccessfulConnection(top.NodeId, top.PriorityInterface);
                        OnConnectionChanged?.Invoke(true);
                        return true;
                    }
                }
            }
            catch { }
        }

        // Settling delay: wait briefly (up to 1.2s) for discovery packets to arrive across multiple interfaces
        try
        {
            await Task.Delay(1200, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        // Iterate through priority list in configured priority order
        foreach (var pref in AutoConnectPriorityList.ToList())
        {
            if (cancellationToken.IsCancellationRequested) return false;

            // Find matching discovered node
            var discovered = DiscoveredNodes.FirstOrDefault(n =>
                string.Equals(n.NodeId, pref.NodeId, StringComparison.OrdinalIgnoreCase));

            if (discovered != null && discovered.Interfaces.Count > 0)
            {
                // Prefer configured priority interface, otherwise fallback to any discovered interface
                var targetIface = discovered.Interfaces.FirstOrDefault(i => i.MatchesUrl(pref.PriorityInterface))
                                  ?? discovered.SelectedInterface
                                  ?? discovered.Interfaces[0];

                string targetUrl = targetIface.BaseUrl;
                string? token = pref.AuthToken ?? AuthToken;

                try
                {
                    bool ok = await _backendService.ConnectAsync(targetUrl, token);
                    if (ok)
                    {
                        RecordSuccessfulConnection(pref.NodeId, targetUrl);
                        OnConnectionChanged?.Invoke(true);
                        return true;
                    }
                }
                catch { }
            }
        }

        // Secondary fallback: Try direct connection to priority interface of any remaining known node
        foreach (var pref in AutoConnectPriorityList.ToList())
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!string.IsNullOrWhiteSpace(pref.PriorityInterface))
            {
                try
                {
                    bool ok = await _backendService.ConnectAsync(pref.PriorityInterface, pref.AuthToken ?? AuthToken);
                    if (ok)
                    {
                        RecordSuccessfulConnection(pref.NodeId, pref.PriorityInterface);
                        OnConnectionChanged?.Invoke(true);
                        return true;
                    }
                }
                catch { }
            }
        }

        return false;
    }

    [RelayCommand]
    public void Close()
    {
        OnClose?.Invoke();
    }

    public void LoadSavedSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<ServerConnectionSettings>(json);
                if (settings != null)
                {
                    ServerUrl = settings.ServerUrl;
                    AuthToken = settings.AuthToken ?? string.Empty;
                    AutoConnect = settings.AutoConnect;
                    LastConnectedNodeId = settings.LastConnectedNodeId;

                    AutoConnectPriorityList.Clear();
                    int rank = 1;
                    if (settings.AutoConnectPriorityList != null && settings.AutoConnectPriorityList.Count > 0)
                    {
                        foreach (var item in settings.AutoConnectPriorityList)
                        {
                            AutoConnectPriorityList.Add(AutoConnectPriorityItemViewModel.FromModel(item, rank++));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(ServerUrl))
                    {
                        // Migrate existing single server configuration into initial priority entry
                        var defaultPref = new AutoConnectNodePreference
                        {
                            NodeId = !string.IsNullOrWhiteSpace(LastConnectedNodeId) ? LastConnectedNodeId : "Default Node",
                            PriorityInterface = ServerUrl,
                            AuthToken = AuthToken,
                            LastConnected = DateTime.UtcNow
                        };
                        AutoConnectPriorityList.Add(AutoConnectPriorityItemViewModel.FromModel(defaultPref, 1));
                    }
                }
            }
        }
        catch
        {
            // Ignore settings read errors
        }
    }

    public void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var settings = new ServerConnectionSettings
            {
                ServerUrl = ServerUrl,
                AuthToken = AuthToken,
                AutoConnect = AutoConnect,
                LastConnectedNodeId = LastConnectedNodeId,
                AutoConnectPriorityList = AutoConnectPriorityList.Select(i => i.ToModel()).ToList()
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore settings write errors
        }
    }
}
