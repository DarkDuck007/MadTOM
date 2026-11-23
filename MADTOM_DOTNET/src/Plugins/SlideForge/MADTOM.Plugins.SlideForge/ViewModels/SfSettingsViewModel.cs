using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfConfiguredNodeItem(
    string Id,
    string Alias,
    string Endpoint,
    string PublicKeyDisplay,
    string PinnedFingerprintDisplay
);

public sealed partial class SfSettingsViewModel : ObservableObject, IDisposable
{
    private readonly SfNodeManager _nodeManager;
    private readonly SfMdnsDiscoveryService _mdnsService = new();

    private string _clientPrivateKey = string.Empty;

    [ObservableProperty] private string _newNodeId = string.Empty;
    [ObservableProperty] private string _newNodeAlias = "SlideForge";
    [ObservableProperty] private string _newNodeEndpoint = "127.0.0.1:50057";
    [ObservableProperty] private string _envSnippet = string.Empty;
    [ObservableProperty] private string _copyButtonText = "Copy Snippet";
    [ObservableProperty] private bool _isScanningMdns;
    [ObservableProperty] private string _scanButtonText = "Scan LAN / mDNS";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEditingNode;
    [ObservableProperty] private string _editingOriginalId = string.Empty;

    public ObservableCollection<SfConfiguredNodeItem> ConfiguredNodes { get; } = new();
    public ObservableCollection<DiscoveredSfNode> DiscoveredMdnsNodes { get; } = new();

    public string SaveButtonText => IsEditingNode ? "Save Changes" : "+ Add Node";

    partial void OnIsEditingNodeChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));

    public event Action? CloseRequested;

    public SfSettingsViewModel(SfNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        RefreshNodesList();
        GenerateNewClientKey();

        _mdnsService.NodeDiscovered += node =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!DiscoveredMdnsNodes.Any(x => x.Endpoint.Equals(node.Endpoint, StringComparison.OrdinalIgnoreCase)))
                    DiscoveredMdnsNodes.Add(node);
            });
        };
    }

    public void RefreshNodesList()
    {
        ConfiguredNodes.Clear();
        foreach (var n in _nodeManager.Nodes)
        {
            string pub = SfAuthTokenGenerator.GetPublicKey(n.AuthKey);
            string pubDisplay = string.IsNullOrEmpty(pub) ? "(no auth key)" : pub;
            string nodeKey = SfNodeManager.NormalizeNodeKey(n.HostEndpoint);
            string? pin = _nodeManager.Pins.GetPin(nodeKey);
            string pinDisplay = pin is null ? "Not pinned (TOFU on first connect)" : $"SHA-256: {pin}";
            ConfiguredNodes.Add(new SfConfiguredNodeItem(n.Id, n.Alias, n.HostEndpoint, pubDisplay, pinDisplay));
        }
    }

    [RelayCommand]
    public void GenerateNewClientKey()
    {
        var (pub, priv) = SfAuthTokenGenerator.GenerateKeyPair();
        _clientPrivateKey = priv;
        EnvSnippet = $"SLIDEFORGE_CONTROL_KEY={pub}";
    }

    [RelayCommand]
    public async Task CopyEnvSnippetAsync()
    {
        if (string.IsNullOrWhiteSpace(EnvSnippet)) return;
        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime switch
            {
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
                Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView => Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView),
                _ => null
            };

            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(EnvSnippet);
                CopyButtonText = "Copied!";
                StatusMessage = "Copied public key snippet to clipboard.";
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Post(() => CopyButtonText = "Copy Snippet"));
            }
            else
            {
                StatusMessage = "Clipboard not available.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ScanMdnsAsync()
    {
        if (IsScanningMdns) return;
        IsScanningMdns = true;
        ScanButtonText = "Scanning...";
        StatusMessage = $"Scanning for {SfMdnsDiscoveryService.ServiceType}...";
        try
        {
            var nodes = await _mdnsService.ScanAsync();
            DiscoveredMdnsNodes.Clear();
            foreach (var n in nodes)
                DiscoveredMdnsNodes.Add(n);
            StatusMessage = DiscoveredMdnsNodes.Count > 0
                ? $"Discovered {DiscoveredMdnsNodes.Count} SlideForge probe(s)."
                : "No SlideForge probes discovered via mDNS.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery error: {ex.Message}";
        }
        finally
        {
            IsScanningMdns = false;
            ScanButtonText = "Scan LAN / mDNS";
        }
    }

    [RelayCommand]
    public void UseDiscoveredNode(DiscoveredSfNode? node)
    {
        if (node is null) return;
        NewNodeId = node.NodeId;
        NewNodeEndpoint = node.Endpoint;
        NewNodeAlias = $"{node.NodeId} (LAN)";
        StatusMessage = $"Filled endpoint {node.Endpoint}. Click Add Node to save.";
    }

    [RelayCommand]
    public void EditNode(string nodeId)
    {
        var node = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;

        IsEditingNode = true;
        EditingOriginalId = node.Id;
        NewNodeId = node.Id;
        NewNodeAlias = node.Alias;
        NewNodeEndpoint = node.HostEndpoint;
        _clientPrivateKey = node.AuthKey;
        string pub = SfAuthTokenGenerator.GetPublicKey(node.AuthKey);
        EnvSnippet = string.IsNullOrEmpty(pub) ? string.Empty : $"SLIDEFORGE_CONTROL_KEY={pub}";
        StatusMessage = $"Editing '{node.Id}' (existing key preserved).";
    }

    [RelayCommand]
    public void CancelEdit()
    {
        IsEditingNode = false;
        EditingOriginalId = string.Empty;
        NewNodeId = string.Empty;
        NewNodeAlias = "SlideForge";
        NewNodeEndpoint = "127.0.0.1:50057";
        GenerateNewClientKey();
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    public void SaveNode()
    {
        string id = string.IsNullOrWhiteSpace(NewNodeId) ? "slideforge" : NewNodeId.Trim();
        string endpoint = string.IsNullOrWhiteSpace(NewNodeEndpoint) ? "127.0.0.1:50057" : NewNodeEndpoint.Trim();
        string alias = string.IsNullOrWhiteSpace(NewNodeAlias) ? id : NewNodeAlias.Trim();

        if (IsEditingNode)
        {
            var existing = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(EditingOriginalId, StringComparison.OrdinalIgnoreCase));
            string keyToUse = !string.IsNullOrWhiteSpace(_clientPrivateKey) ? _clientPrivateKey : (existing?.AuthKey ?? string.Empty);
            if (!id.Equals(EditingOriginalId, StringComparison.OrdinalIgnoreCase))
                _nodeManager.RemoveNode(EditingOriginalId);

            _nodeManager.AddOrUpdateNode(new SfNodeModel(id, endpoint, keyToUse, alias));
            StatusMessage = $"Updated node '{id}'.";
            CancelEdit();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_clientPrivateKey))
                GenerateNewClientKey();

            _nodeManager.AddOrUpdateNode(new SfNodeModel(id, endpoint, _clientPrivateKey, alias));
            StatusMessage = $"Added node '{id}'. Add the public key to the probe's authorized keys or .env.";
            NewNodeId = string.Empty;
            NewNodeAlias = "SlideForge";
            NewNodeEndpoint = "127.0.0.1:50057";
            GenerateNewClientKey();
        }

        RefreshNodesList();
    }

    [RelayCommand]
    public void RemoveNode(string nodeId)
    {
        if (IsEditingNode && EditingOriginalId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            CancelEdit();
        _nodeManager.RemoveNode(nodeId);
        RefreshNodesList();
        StatusMessage = $"Removed node '{nodeId}'.";
    }

    [RelayCommand]
    public void ForgetNodePin(string nodeId)
    {
        var node = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;
        string nodeKey = SfNodeManager.NormalizeNodeKey(node.HostEndpoint);
        _nodeManager.ForgetPin(nodeKey, node.Id);
        RefreshNodesList();
        StatusMessage = $"Forgot TLS pin for {nodeKey}.";
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke();

    public void Dispose()
    {
        _mdnsService.Dispose();
    }
}
