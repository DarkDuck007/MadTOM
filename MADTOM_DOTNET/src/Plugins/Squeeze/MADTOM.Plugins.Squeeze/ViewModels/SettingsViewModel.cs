using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
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

    [ObservableProperty]
    private string _serverUrl = "http://127.0.0.1:8080";

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private bool _autoConnect = true;

    [ObservableProperty]
    private string _statusMessage = "Ready to connect";

    [ObservableProperty]
    private string _statusColor = "#8B949E";

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private ObservableCollection<DiscoveredServer> _discoveredServers = new();

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

    private void OnServerDiscovered(object? sender, DiscoveredServer server)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var existing in DiscoveredServers)
            {
                if (existing.BaseUrl == server.BaseUrl)
                {
                    existing.LastSeen = server.LastSeen;
                    return;
                }
            }
            DiscoveredServers.Add(server);
        });
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
                SaveSettings();
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
            StatusMessage = $"Scan finished. Found {DiscoveredServers.Count} server(s).";
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

    [RelayCommand]
    public async Task SelectAndConnect(DiscoveredServer? server)
    {
        if (server == null) return;
        ServerUrl = server.BaseUrl;
        await Connect();
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
                AutoConnect = AutoConnect
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
