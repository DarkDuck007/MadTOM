using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels.Topology;

public partial class NetworkTopologyViewModel : ViewModelBase
{
    private readonly MultiCollectorManager _collectorManager;
    private readonly string _layoutStoragePath;

    private string _currentNodeId = "";
    private CancellationTokenSource? _streamCts;
    private CancellationTokenSource? _heartbeatCts;
    private string _activeSessionId = "";

    // Header & Progressive Staging
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _loadingProgress;
    [ObservableProperty] private string _currentStageSubtitle = "Ready";
    [ObservableProperty] private string _errorNotice = "";

    // Capabilities
    [ObservableProperty] private bool _hasNetlink;
    [ObservableProperty] private bool _hasSockDiag;
    [ObservableProperty] private bool _hasNftables;
    [ObservableProperty] private bool _hasLegacyIptables;
    [ObservableProperty] private string _nftVersion = "";
    [ObservableProperty] private string _defaultTcpCc = "";

    public string FirewallBackendTag => HasNftables ? "NFTABLES" : (HasLegacyIptables ? "IPTABLES (LEGACY)" : "NONE");
    public string FirewallBackendColor => HasNftables ? "#238636" : (HasLegacyIptables ? "#D29922" : "#484F58");
    public string FirewallBackendTooltip => HasNftables 
        ? (!string.IsNullOrEmpty(NftVersion) ? $"Active backend: {NftVersion}" : "Active backend: nftables ruleset detected")
        : (HasLegacyIptables ? "Active backend: iptables-legacy detected (no nft binary in PATH)" : "No firewall binary (nft / iptables) available on node");

    // Visual Canvas Collections
    public ObservableCollection<TopologyNodeItemViewModel> CanvasNodes { get; } = new();
    public ObservableCollection<TopologyEdgeItemViewModel> CanvasEdges { get; } = new();

    // Dense Inspector Drawer
    [ObservableProperty] private bool _isInspectorOpen;
    [ObservableProperty] private double _inspectorWidth = 480;
    [ObservableProperty] private TopologyNodeItemViewModel? _selectedNode;
    [ObservableProperty] private string _inspectorTab = "routes"; // routes, firewall, sockets, neighbors, diag

    public bool IsRoutesTab => InspectorTab == "routes";
    public bool IsFirewallTab => InspectorTab == "firewall";
    public bool IsSocketsTab => InspectorTab == "sockets";
    public bool IsNeighborsTab => InspectorTab == "neighbors";
    public bool IsDiagTab => InspectorTab == "diag";

    public string InspectorPanelTitle => SelectedNode?.Title ?? "Host Network Tables & Rules";

    partial void OnSelectedNodeChanged(TopologyNodeItemViewModel? value)
    {
        OnPropertyChanged(nameof(InspectorPanelTitle));
    }

    partial void OnInspectorTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsRoutesTab));
        OnPropertyChanged(nameof(IsFirewallTab));
        OnPropertyChanged(nameof(IsSocketsTab));
        OnPropertyChanged(nameof(IsNeighborsTab));
        OnPropertyChanged(nameof(IsDiagTab));
    }

    public ObservableCollection<RouteRowViewModel> Routes { get; } = new();
    public ObservableCollection<FirewallRuleRowViewModel> FirewallRules { get; } = new();
    public ObservableCollection<SocketRowViewModel> Sockets { get; } = new();
    public ObservableCollection<NeighborRowViewModel> Neighbors { get; } = new();

    // Active Read-Only Diagnostics
    [ObservableProperty] private string _diagTarget = "1.1.1.1";
    [ObservableProperty] private DiagnosticProbeType _selectedProbeType = DiagnosticProbeType.ProbePing;
    [ObservableProperty] private bool _isProbeRunning;
    [ObservableProperty] private string _probeConsoleOutput = "";

    public event Action? CenterGraphRequested;
    public event Action? CanvasInvalidationRequested;

    public NetworkTopologyViewModel(MultiCollectorManager collectorManager, string? customLayoutPath = null)
    {
        _collectorManager = collectorManager;
        _layoutStoragePath = customLayoutPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MADTOM", "topology_layouts.json");
    }

    public void ActivateForNode(string nodeId, FleetNodeModel? node)
    {
        if (_currentNodeId == nodeId && !string.IsNullOrEmpty(_activeSessionId))
        {
            return; // Already active or activating
        }

        Deactivate();

        _currentNodeId = nodeId;
        if (string.IsNullOrEmpty(nodeId) || nodeId.Equals("all", StringComparison.OrdinalIgnoreCase) || nodeId.Equals("aggregated", StringComparison.OrdinalIgnoreCase))
        {
            CurrentStageSubtitle = "Select an individual node to inspect routing and network topology.";
            return;
        }

        _streamCts = new CancellationTokenSource();
        _heartbeatCts = new CancellationTokenSource();
        _activeSessionId = Guid.NewGuid().ToString("N");

        IsLoading = true;
        IsInspectorOpen = true;
        LoadingProgress = 5.0;
        CurrentStageSubtitle = "Connecting · Establishing secure inspection stream...";
        ErrorNotice = "";

        _ = Task.Run(() => StreamInspectionLoopAsync(nodeId, node, _streamCts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(nodeId, node, _heartbeatCts.Token));
    }

    public void Deactivate()
    {
        try { _streamCts?.Cancel(); _streamCts?.Dispose(); } catch { }
        try { _heartbeatCts?.Cancel(); _heartbeatCts?.Dispose(); } catch { }
        _streamCts = null;
        _heartbeatCts = null;
        _activeSessionId = "";
        IsLoading = false;
        CurrentStageSubtitle = "Idle";
        ClearTopologyData();
    }

    public void ClearTopologyData()
    {
        CanvasNodes.Clear();
        CanvasEdges.Clear();
        Routes.Clear();
        FirewallRules.Clear();
        Sockets.Clear();
        Neighbors.Clear();
        SelectedNode = null;
        ProbeConsoleOutput = "";
        HasNetlink = false;
        HasSockDiag = false;
        HasNftables = false;
        HasLegacyIptables = false;
        NftVersion = "";
        DefaultTcpCc = "";
        OnPropertyChanged(nameof(FirewallBackendTag));
        OnPropertyChanged(nameof(FirewallBackendColor));
        OnPropertyChanged(nameof(FirewallBackendTooltip));
    }

    private async Task<NetworkInspectionService.NetworkInspectionServiceClient?> ResolveInspectionClientAsync(string nodeId, FleetNodeModel? node, CancellationToken ct)
    {
        if (MultiCollectorManager.IsLocalNode(nodeId))
        {
            try
            {
                return _collectorManager.GetLocalDaemonClient().InspectionClient;
            }
            catch
            {
                // Fall back to collector if local daemon cannot initialize
            }
        }

        CollectorClientService? client = null;
        if (node != null)
        {
            client = _collectorManager.GetClientForNode(node);
        }
        if (client == null)
        {
            var allNodes = await _collectorManager.FetchAllNodesAsync(ct);
            var matched = allNodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                client = _collectorManager.GetClientForNode(matched);
            }
        }

        return client?.InspectionClient;
    }

    private async Task StreamInspectionLoopAsync(string nodeId, FleetNodeModel? node, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var inspectionClient = await ResolveInspectionClientAsync(nodeId, node, ct);

            if (inspectionClient == null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ErrorNotice = $"Unable to find active collector or daemon connection for node {nodeId}.";
                    IsLoading = false;
                });
                await Task.Delay(3000, ct);
                continue;
            }

            try
            {
                var req = new TopologyStreamRequest
                {
                    NodeId = nodeId,
                    SessionId = _activeSessionId,
                    LeaseSeconds = 15,
                    IncludeFirewall = true,
                    IncludeSockets = true,
                    MaxSockets = 300
                };

                using var call = inspectionClient.StreamNetworkTopology(req, cancellationToken: ct);
                var stream = call.ResponseStream;

                while (await stream.MoveNext(ct))
                {
                    var ev = stream.Current;
                    Dispatcher.UIThread.Post(() => HandleTopologyEvent(ev));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // If remote collector relay failed or returned not registered, and node might be local, fallback to local daemon
                if (!MultiCollectorManager.IsLocalNode(nodeId) && (ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var localClient = _collectorManager.GetLocalDaemonClient().InspectionClient;
                        var req = new TopologyStreamRequest
                        {
                            NodeId = nodeId,
                            SessionId = _activeSessionId,
                            LeaseSeconds = 15,
                            IncludeFirewall = true,
                            IncludeSockets = true,
                            MaxSockets = 300
                        };
                        using var fallbackCall = localClient.StreamNetworkTopology(req, cancellationToken: ct);
                        var fbStream = fallbackCall.ResponseStream;
                        while (await fbStream.MoveNext(ct))
                        {
                            var ev = fbStream.Current;
                            Dispatcher.UIThread.Post(() => HandleTopologyEvent(ev));
                        }
                        continue;
                    }
                    catch
                    {
                        // Fallback failed, continue to standard error report
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    CurrentStageSubtitle = $"Reconnecting · Stream error: {ex.Message}";
                });
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task HeartbeatLoopAsync(string nodeId, FleetNodeModel? node, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);
                if (string.IsNullOrEmpty(_activeSessionId)) continue;

                var inspectionClient = await ResolveInspectionClientAsync(nodeId, node, ct);
                if (inspectionClient != null)
                {
                    await inspectionClient.RenewInspectionLeaseAsync(new LeaseRenewalRequest
                    {
                        NodeId = nodeId,
                        SessionId = _activeSessionId,
                        LeaseSeconds = 15
                    }, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Heartbeat retry handled on next tick
            }
        }
    }

    private void HandleTopologyEvent(TopologyEvent ev)
    {
        CurrentStageSubtitle = ev.StageDescription;
        if (ev.ProgressPct > 0)
        {
            LoadingProgress = ev.ProgressPct;
        }

        switch (ev.PayloadCase)
        {
            case TopologyEvent.PayloadOneofCase.Capabilities:
                ApplyCapabilities(ev.Capabilities);
                break;

            case TopologyEvent.PayloadOneofCase.Interfaces:
                ApplyInterfaces(ev.Interfaces);
                break;

            case TopologyEvent.PayloadOneofCase.AddressesNeighbors:
                ApplyAddressesNeighbors(ev.AddressesNeighbors);
                break;

            case TopologyEvent.PayloadOneofCase.Routing:
                ApplyRouting(ev.Routing);
                break;

            case TopologyEvent.PayloadOneofCase.Firewall:
                ApplyFirewall(ev.Firewall);
                break;

            case TopologyEvent.PayloadOneofCase.Sockets:
                ApplySockets(ev.Sockets);
                break;

            case TopologyEvent.PayloadOneofCase.Traffic:
                ApplyTrafficRates(ev.Traffic);
                break;
        }

        if (ev.Stage == TopologyStage.Complete)
        {
            IsLoading = false;
            CurrentStageSubtitle = "Live · Real-time topology active";
        }
    }

    private void ApplyCapabilities(CapabilitiesStagePayload cap)
    {
        HasNetlink = cap.HasNetlink;
        HasSockDiag = cap.HasSockDiag;
        HasNftables = cap.HasNftables;
        HasLegacyIptables = cap.HasLegacyIptables;
        NftVersion = cap.NftablesVersion;
        DefaultTcpCc = cap.DefaultCongestionControl;
        OnPropertyChanged(nameof(FirewallBackendTag));
        OnPropertyChanged(nameof(FirewallBackendColor));
        OnPropertyChanged(nameof(FirewallBackendTooltip));
    }

    private void ApplyInterfaces(InterfaceStagePayload ifaces)
    {
        var savedLayout = LoadLayoutCoordinates(_currentNodeId);
        var existingMap = CanvasNodes.ToDictionary(n => n.Id);

        double defaultX = 80;
        double defaultY = 80;

        foreach (var iface in ifaces.Interfaces)
        {
            string id = $"iface:{iface.Name}";
            if (!existingMap.TryGetValue(id, out var nodeVm))
            {
                nodeVm = new TopologyNodeItemViewModel
                {
                    Id = id,
                    Title = iface.Name,
                    Subtitle = string.IsNullOrEmpty(iface.MacAddress) ? iface.Kind : $"{iface.Kind} · {iface.MacAddress}",
                    ItemType = TopologyItemType.Interface,
                    Kind = iface.Kind,
                    X = defaultX,
                    Y = defaultY,
                };
                if (savedLayout.TryGetValue(id, out var pt))
                {
                    nodeVm.X = pt.X;
                    nodeVm.Y = pt.Y;
                }
                else
                {
                    defaultX += 240;
                    if (defaultX > 800) { defaultX = 80; defaultY += 160; }
                }
                CanvasNodes.Add(nodeVm);
            }

            nodeVm.CarrierUp = iface.CarrierUp;
            nodeVm.IsUp = iface.IsUp;
            nodeVm.Mtu = iface.Mtu;
            nodeVm.SpeedMbps = iface.LinkSpeedMbps;
            nodeVm.Duplex = iface.Duplex;
            nodeVm.MacAddress = iface.MacAddress;
            nodeVm.Namespace = iface.Namespace;
            nodeVm.RxRateBps = iface.RxBytesPerSec;
            nodeVm.TxRateBps = iface.TxBytesPerSec;
            nodeVm.RxDropped = iface.RxDropped;
            nodeVm.TxDropped = iface.TxDropped;
            nodeVm.RxErrors = iface.RxErrors;
            nodeVm.TxErrors = iface.TxErrors;

            nodeVm.IpAddresses.Clear();
            foreach (var ip in iface.IpAddresses)
            {
                nodeVm.IpAddresses.Add(ip);
            }
        }
    }

    private void ApplyAddressesNeighbors(AddressesNeighborsStagePayload payload)
    {
        Neighbors.Clear();
        foreach (var n in payload.Neighbors)
        {
            Neighbors.Add(new NeighborRowViewModel
            {
                IpAddress = n.IpAddress,
                MacAddress = n.MacAddress,
                InterfaceName = n.InterfaceName,
                State = n.State,
                IsRouter = n.IsRouter
            });
        }
    }

    private void ApplyRouting(RoutingStagePayload routing)
    {
        Routes.Clear();
        CanvasEdges.Clear();

        var existingNodeMap = CanvasNodes.ToDictionary(n => n.Title);
        var savedLayout = LoadLayoutCoordinates(_currentNodeId);

        foreach (var r in routing.Routes)
        {
            var row = new RouteRowViewModel
            {
                Destination = r.Destination,
                Gateway = r.Gateway,
                InterfaceName = r.InterfaceName,
                Protocol = r.Protocol,
                Scope = r.Scope,
                Metric = r.Metric,
                Table = r.Table,
                IsDefault = r.IsDefault,
                IsIpv6 = r.IsIpv6,
                Type = r.Type,
                PreferredSource = r.PreferredSource
            };
            Routes.Add(row);

            // Connect interfaces to gateways or destination subnets on the canvas
            if (!string.IsNullOrEmpty(r.Gateway))
            {
                string gwNodeId = $"gw:{r.Gateway}";
                var gwNode = CanvasNodes.FirstOrDefault(n => n.Id == gwNodeId);
                if (gwNode == null)
                {
                    double gwX = 400;
                    double gwY = 40;
                    if (savedLayout.TryGetValue(gwNodeId, out var pt))
                    {
                        gwX = pt.X; gwY = pt.Y;
                    }
                    gwNode = new TopologyNodeItemViewModel
                    {
                        Id = gwNodeId,
                        Title = $"Gateway {r.Gateway}",
                        Subtitle = r.IsDefault ? "Default Gateway" : "Next-hop router",
                        ItemType = TopologyItemType.Gateway,
                        X = gwX,
                        Y = gwY,
                        CarrierUp = true,
                        IsUp = true
                    };
                    CanvasNodes.Add(gwNode);
                }

                if (existingNodeMap.TryGetValue(r.InterfaceName, out var ifaceNode))
                {
                    string edgeId = $"{ifaceNode.Id}->{gwNodeId}";
                    if (!CanvasEdges.Any(e => e.Id == edgeId))
                    {
                        CanvasEdges.Add(new TopologyEdgeItemViewModel
                        {
                            Id = edgeId,
                            SourceNodeId = ifaceNode.Id,
                            TargetNodeId = gwNodeId,
                            Label = r.IsDefault ? "default" : r.Destination,
                            Protocol = r.Protocol,
                            Metric = r.Metric,
                            IsDefaultRoute = r.IsDefault,
                            StartX = ifaceNode.X + 100,
                            StartY = ifaceNode.Y + 30,
                            EndX = gwNode.X + 100,
                            EndY = gwNode.Y + 30
                        });
                    }
                }
            }
        }
    }

    private void ApplyFirewall(FirewallStagePayload fw)
    {
        FirewallRules.Clear();
        foreach (var tbl in fw.Tables)
        {
            foreach (var chn in tbl.Chains)
            {
                foreach (var r in chn.Rules)
                {
                    FirewallRules.Add(new FirewallRuleRowViewModel
                    {
                        Handle = r.Handle,
                        Table = tbl.Name,
                        Chain = chn.Name,
                        Statement = r.Statement,
                        Verdict = r.Verdict,
                        Comment = r.Comment,
                        Packets = r.PacketCount,
                        Bytes = r.ByteCount,
                        RawJson = r.RawJson
                    });
                }
            }
        }
    }

    private void ApplySockets(SocketsStagePayload sockets)
    {
        Sockets.Clear();
        foreach (var s in sockets.Sockets)
        {
            Sockets.Add(new SocketRowViewModel
            {
                SocketCookie = s.SocketCookie,
                LocalEndpoint = $"{s.LocalAddress}:{s.LocalPort}",
                RemoteEndpoint = $"{s.RemoteAddress}:{s.RemotePort}",
                State = s.State,
                ProcessName = s.ProcessName,
                Pid = s.Pid,
                InterfaceName = s.InterfaceName,
                CongestionAlgorithm = s.CongestionAlgorithm,
                DeliveryRateMbps = s.DeliveryRateBps / 1_000_000.0,
                PacingRateMbps = s.PacingRateBps / 1_000_000.0,
                RttMs = s.RttMs,
                Cwnd = s.Cwnd,
                Retransmits = s.Retransmits,
                CaState = s.CaState
            });
        }
    }

    private void ApplyTrafficRates(TrafficDeltaPayload traffic)
    {
        foreach (var kvp in traffic.InterfaceRates)
        {
            var node = CanvasNodes.FirstOrDefault(n => n.Title == kvp.Key);
            if (node != null)
            {
                node.RxRateBps = kvp.Value.RxBytesPerSec;
                node.TxRateBps = kvp.Value.TxBytesPerSec;
                node.RxDropped = kvp.Value.RxDroppedDelta;
                node.TxDropped = kvp.Value.TxDroppedDelta;
                node.RxErrors = kvp.Value.RxErrorsDelta;
                node.TxErrors = kvp.Value.TxErrorsDelta;
            }
        }
        CanvasInvalidationRequested?.Invoke();
    }

    [RelayCommand]
    public void SelectCanvasNode(TopologyNodeItemViewModel? node)
    {
        foreach (var n in CanvasNodes) n.IsSelected = false;
        if (node != null)
        {
            node.IsSelected = true;
            SelectedNode = node;
            IsInspectorOpen = true;
        }
        else
        {
            SelectedNode = null;
        }
    }

    [RelayCommand]
    public void ToggleInspector()
    {
        IsInspectorOpen = !IsInspectorOpen;
    }

    [RelayCommand]
    public void DeselectNode()
    {
        if (SelectedNode != null)
        {
            SelectedNode.IsSelected = false;
            SelectedNode = null;
        }
    }

    [RelayCommand]
    public void CloseInspector()
    {
        IsInspectorOpen = false;
        DeselectNode();
    }

    [RelayCommand]
    public void SwitchInspectorTab(string tab)
    {
        InspectorTab = tab;
    }

    [RelayCommand]
    public async Task RunProbeAsync()
    {
        if (IsProbeRunning || string.IsNullOrWhiteSpace(DiagTarget)) return;

        IsProbeRunning = true;
        ProbeConsoleOutput = $"[MADTOM Prober] Starting {SelectedProbeType} to {DiagTarget}...\n";

        try
        {
            var inspectionClient = await ResolveInspectionClientAsync(_currentNodeId, null, CancellationToken.None);

            if (inspectionClient == null)
            {
                ProbeConsoleOutput += "Error: Node inspection connection unavailable.\n";
                IsProbeRunning = false;
                return;
            }

            var req = new DiagnosticProbeRequest
            {
                NodeId = _currentNodeId,
                Type = SelectedProbeType,
                Target = DiagTarget.Trim(),
                PacketCount = 4,
                TimeoutSeconds = 5
            };

            using var call = inspectionClient.StreamDiagnosticProbe(req);
            while (await call.ResponseStream.MoveNext())
            {
                var line = call.ResponseStream.Current.RawLine;
                if (!string.IsNullOrEmpty(line))
                {
                    ProbeConsoleOutput += line;
                }
            }

            ProbeConsoleOutput += "\n[MADTOM Prober] Execution finished.\n";
        }
        catch (Exception ex)
        {
            ProbeConsoleOutput += $"\n[MADTOM Prober] Failure: {ex.Message}\n";
        }
        finally
        {
            IsProbeRunning = false;
        }
    }

    [RelayCommand]
    public void CenterGraph()
    {
        CenterGraphRequested?.Invoke();
    }

    [RelayCommand]
    public void AutoArrange()
    {
        if (CanvasNodes.Count == 0) return;

        // Categorize nodes into tiers
        var interfaces = CanvasNodes.Where(n => n.ItemType == TopologyItemType.Interface).ToList();
        var gateways = CanvasNodes.Where(n => n.ItemType == TopologyItemType.Gateway || n.ItemType == TopologyItemType.Router).ToList();
        var others = CanvasNodes.Except(interfaces).Except(gateways).ToList();

        // Separate interfaces into Loopback, Physical/Bridged/Bonded/WLAN/Wireguard, and Virtual VETH
        var loopback = interfaces.Where(i => i.Kind.Equals("loopback", StringComparison.OrdinalIgnoreCase) || i.Title == "lo").ToList();
        var mainIfaces = interfaces.Where(i => !loopback.Contains(i) && !i.Title.StartsWith("veth", StringComparison.OrdinalIgnoreCase)).ToList();
        var vethIfaces = interfaces.Where(i => i.Title.StartsWith("veth", StringComparison.OrdinalIgnoreCase)).ToList();

        const double cardWidth = 220.0;
        const double cardHeight = 80.0;
        const double hSpacing = 40.0;
        const double vSpacing = 50.0;
        const double startX = 60.0;

        // Tier 0: Loopback interface(s)
        double currentX = startX;
        double currentY = 60.0;
        foreach (var lo in loopback)
        {
            lo.X = currentX;
            lo.Y = currentY;
            currentX += cardWidth + hSpacing;
        }

        // Tier 1: Main active interfaces (eth0, enp2s0, wlp3s0, wg0, docker0, br0)
        currentX = startX;
        currentY = loopback.Count > 0 ? currentY + cardHeight + vSpacing : 60.0;
        int col = 0;
        const int maxCols = 4;
        foreach (var iface in mainIfaces)
        {
            iface.X = startX + col * (cardWidth + hSpacing);
            iface.Y = currentY;
            col++;
            if (col >= maxCols)
            {
                col = 0;
                currentY += cardHeight + vSpacing;
            }
        }
        if (col > 0) currentY += cardHeight + vSpacing;

        // Tier 2: Next-hop Gateways connected to interfaces
        currentX = startX;
        col = 0;
        foreach (var gw in gateways)
        {
            gw.X = startX + col * (cardWidth + hSpacing);
            gw.Y = currentY;
            col++;
            if (col >= maxCols)
            {
                col = 0;
                currentY += cardHeight + vSpacing;
            }
        }
        if (col > 0) currentY += cardHeight + vSpacing;

        // Tier 3: Containers / Virtual veth pairs / Subnets
        col = 0;
        var bottomNodes = vethIfaces.Concat(others).ToList();
        foreach (var veth in bottomNodes)
        {
            veth.X = startX + col * (cardWidth + hSpacing);
            veth.Y = currentY;
            col++;
            if (col >= maxCols)
            {
                col = 0;
                currentY += cardHeight + vSpacing;
            }
        }

        SaveCurrentLayout();
        CenterGraph();
    }

    public void SaveCurrentLayout()
    {
        try
        {
            var dict = CanvasNodes.ToDictionary(n => n.Id, n => new PointD(n.X, n.Y));
            var dir = Path.GetDirectoryName(_layoutStoragePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Dictionary<string, Dictionary<string, PointD>> root = new();
            if (File.Exists(_layoutStoragePath))
            {
                var existingJson = File.ReadAllText(_layoutStoragePath);
                root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PointD>>>(existingJson) ?? root;
            }

            root[_currentNodeId] = dict;
            File.WriteAllText(_layoutStoragePath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-critical layout persistence
        }
    }

    private Dictionary<string, PointD> LoadLayoutCoordinates(string nodeId)
    {
        try
        {
            if (File.Exists(_layoutStoragePath))
            {
                var json = File.ReadAllText(_layoutStoragePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PointD>>>(json);
                if (root != null && root.TryGetValue(nodeId, out var coords))
                {
                    return coords;
                }
            }
        }
        catch { }
        return new Dictionary<string, PointD>();
    }

    public record PointD(double X, double Y);
}
