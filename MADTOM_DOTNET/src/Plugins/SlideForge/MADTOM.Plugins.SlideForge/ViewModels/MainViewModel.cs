using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MadTOM.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SfNodeManager _nodeManager;
    private CancellationTokenSource? _connectionCts;

    [ObservableProperty] private int _selectedWorkspaceTabIndex;
    [ObservableProperty] private SfNodeModel? _selectedNode;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSettingsModalOpen;

    [ObservableProperty] private string _connectionStatusBadge = "• DISCONNECTED";
    [ObservableProperty] private string _botStatusSummary = "Bot: —";
    [ObservableProperty] private string _versionsSummary = "SlideForge: — · Probe: — · Python: —";
    [ObservableProperty] private string _uptimeSummary = "Uptime: —";
    [ObservableProperty] private string _spoolSummary = "Spool: —";

    // TOFU certificate prompts
    [ObservableProperty] private bool _hasPendingFirstUse;
    [ObservableProperty] private string _pendingFirstUseNodeKey = string.Empty;
    [ObservableProperty] private string _pendingFirstUseFingerprint = string.Empty;

    [ObservableProperty] private bool _hasCertificateMismatch;
    [ObservableProperty] private string _mismatchNodeKey = string.Empty;
    [ObservableProperty] private string _mismatchPinnedFingerprint = string.Empty;
    [ObservableProperty] private string _mismatchPresentedFingerprint = string.Empty;

    public ObservableCollection<SfNodeModel> ActiveNodes { get; } = new();

    public SfOverviewViewModel OverviewTab { get; }
    public SfJobsViewModel JobsTab { get; }
    public SfUsersViewModel UsersTab { get; }
    public SfGuidesViewModel GuidesTab { get; }
    public SfSandboxViewModel SandboxTab { get; }
    public SfConfigViewModel ConfigTab { get; }
    public SfLogsViewModel LogsTab { get; }
    public SfSettingsViewModel SettingsViewModel { get; }

    public MainViewModel(SfNodeManager? nodeManager = null)
    {
        _nodeManager = nodeManager ?? new SfNodeManager();

        OverviewTab = new SfOverviewViewModel(null);
        JobsTab = new SfJobsViewModel(null);
        UsersTab = new SfUsersViewModel(null);
        GuidesTab = new SfGuidesViewModel(null);
        SandboxTab = new SfSandboxViewModel(null);
        ConfigTab = new SfConfigViewModel(null);
        LogsTab = new SfLogsViewModel();
        SettingsViewModel = new SfSettingsViewModel(_nodeManager);

        SettingsViewModel.CloseRequested += () => IsSettingsModalOpen = false;
        _nodeManager.NodesChanged += () => Dispatcher.UIThread.Post(RefreshActiveNodes);
        _nodeManager.CertificateFirstUseDetected += (nodeKey, fp) => Dispatcher.UIThread.Post(() =>
        {
            PendingFirstUseNodeKey = nodeKey;
            PendingFirstUseFingerprint = fp;
            HasPendingFirstUse = true;
            HasCertificateMismatch = false;
            ConnectionStatusBadge = "• TRUST CERT";
        });
        _nodeManager.CertificateMismatchDetected += (nodeKey, pinned, presented) => Dispatcher.UIThread.Post(() =>
        {
            MismatchNodeKey = nodeKey;
            MismatchPinnedFingerprint = pinned;
            MismatchPresentedFingerprint = presented;
            HasCertificateMismatch = true;
            HasPendingFirstUse = false;
            ConnectionStatusBadge = "• CERT MISMATCH";
        });

        RefreshActiveNodes();
    }

    private void RefreshActiveNodes()
    {
        string? prevId = SelectedNode?.Id;
        ActiveNodes.Clear();
        foreach (var n in _nodeManager.Nodes)
            ActiveNodes.Add(n);

        if (prevId != null)
            SelectedNode = ActiveNodes.FirstOrDefault(n => n.Id.Equals(prevId, StringComparison.OrdinalIgnoreCase));

        SelectedNode ??= ActiveNodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(SfNodeModel? value)
    {
        Disconnect();
        ClearCertificateAlerts();

        if (value is not null)
        {
            Connect();
        }
        else
        {
            ConnectionStatusBadge = "• NO NODES";
        }
    }

    [RelayCommand]
    public void Connect()
    {
        var node = SelectedNode;
        if (node is null) return;

        Disconnect();
        ClearCertificateAlerts();

        var rpc = _nodeManager.CreateRpc(node);
        OverviewTab.SetRpc(rpc);
        JobsTab.SetRpc(rpc);
        UsersTab.SetRpc(rpc);
        GuidesTab.SetRpc(rpc);
        SandboxTab.SetRpc(rpc);
        ConfigTab.SetRpc(rpc);

        IsConnected = true;
        ConnectionStatusBadge = "• CONNECTING";
        _connectionCts = new CancellationTokenSource();
        var ct = _connectionCts.Token;

        StartStatusPollLoop(node, ct);
        StartStateStreamLoop(node, ct);
        StartLogsStreamLoop(node, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (ct.IsCancellationRequested || SelectedNode != node) return;
                    await OverviewTab.LoadAsync(ct);
                    await JobsTab.LoadAsync(ct);
                });
            }
            catch { }
        }, ct);
    }

    [RelayCommand]
    public void Disconnect()
    {
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

        IsConnected = false;
        ConnectionStatusBadge = "• DISCONNECTED";
        BotStatusSummary = "Bot: —";

        OverviewTab.SetRpc(null);
        JobsTab.SetRpc(null);
        UsersTab.SetRpc(null);
        GuidesTab.SetRpc(null);
        SandboxTab.SetRpc(null);
        ConfigTab.SetRpc(null);
    }

    [RelayCommand]
    public void TrustPendingCertificate()
    {
        if (string.IsNullOrWhiteSpace(PendingFirstUseNodeKey) || string.IsNullOrWhiteSpace(PendingFirstUseFingerprint))
            return;

        _nodeManager.TrustPin(PendingFirstUseNodeKey, PendingFirstUseFingerprint, SelectedNode?.Id);
        HasPendingFirstUse = false;
        PendingFirstUseNodeKey = string.Empty;
        PendingFirstUseFingerprint = string.Empty;
        SettingsViewModel.RefreshNodesList();
        Connect();
    }

    [RelayCommand]
    public void DismissFirstUsePrompt()
    {
        HasPendingFirstUse = false;
        Disconnect();
    }

    [RelayCommand]
    public void ForgetMismatchedPin()
    {
        if (string.IsNullOrWhiteSpace(MismatchNodeKey)) return;
        _nodeManager.ForgetPin(MismatchNodeKey, SelectedNode?.Id);
        HasCertificateMismatch = false;
        MismatchNodeKey = string.Empty;
        MismatchPinnedFingerprint = string.Empty;
        MismatchPresentedFingerprint = string.Empty;
        SettingsViewModel.RefreshNodesList();
        Connect();
    }

    [RelayCommand]
    public void OpenSettings()
    {
        SettingsViewModel.RefreshNodesList();
        IsSettingsModalOpen = true;
    }

    private void ClearCertificateAlerts()
    {
        HasPendingFirstUse = false;
        HasCertificateMismatch = false;
    }

    private void StartStatusPollLoop(SfNodeModel node, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            int backoffMs = 2000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    var status = await client.GetNodeStatusAsync(
                        new SfNodeStatusRequest { NodeId = node.Id },
                        deadline: DateTime.UtcNow.AddSeconds(10),
                        cancellationToken: ct);

                    backoffMs = 2000;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedNode != node) return;
                        ConnectionStatusBadge = status.BotRunning ? "• RUNNING" : "• PROBE READY";
                        BotStatusSummary = status.BotRunning ? "Bot: Running" : "Bot: Disconnected";
                        VersionsSummary = $"SlideForge: {OrDash(status.SlideforgeVersion)} · Probe: {OrDash(status.ProbeVersion)} · Python: {OrDash(status.PythonVersion)}";
                        UptimeSummary = $"Uptime: {FormatUptime(status.UptimeSeconds)}";
                        SpoolSummary = $"Spool: {SfJsonHelpers.FormatBytes((long)status.CurrentSpoolBytes)} / {SfJsonHelpers.FormatBytes((long)status.MaxSpoolBytes)}";
                    });

                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedNode != node) return;
                        if (!HasPendingFirstUse && !HasCertificateMismatch)
                            ConnectionStatusBadge = "• OFFLINE";
                    });
                    try { await Task.Delay(backoffMs, ct); } catch { break; }
                    backoffMs = Math.Min(backoffMs * 2, 15000);
                }
            }
        }, ct);
    }

    private void StartStateStreamLoop(SfNodeModel node, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            int backoffMs = 1500;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    using var call = client.SubscribeState(
                        new SfStateSubscriptionRequest { NodeId = node.Id },
                        cancellationToken: ct);

                    await call.ResponseHeadersAsync.WaitAsync(ct);
                    backoffMs = 1500;

                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        var chunk = call.ResponseStream.Current;
                        if (chunk == null || chunk.ZstdPayload.IsEmpty) continue;

                        try
                        {
                            byte[] raw = ZstdCodec.Decode(chunk.ZstdPayload.Span, (int)chunk.DecodedSize);
                            var payload = SfStateChunkPayload.Parser.ParseFrom(raw);

                            Dispatcher.UIThread.Post(() =>
                            {
                                if (ct.IsCancellationRequested || SelectedNode != node) return;
                                foreach (var sample in payload.TelemetrySamples)
                                    OverviewTab.ApplyTelemetry(sample);
                                foreach (var ev in payload.JobEvents)
                                    JobsTab.ApplyJobEvent(ev);
                            });
                        }
                        catch
                        {
                            // Skip malformed chunk
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(backoffMs, ct); } catch { break; }
                    backoffMs = Math.Min(backoffMs * 2, 15000);
                }
            }
        }, ct);
    }

    private void StartLogsStreamLoop(SfNodeModel node, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            int backoffMs = 2000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    using var call = client.SubscribeLogsAndApi(
                        new SfLogApiSubscriptionRequest
                        {
                            NodeId = node.Id,
                            IncludeRawApiCalls = node.IncludeApiCalls
                        },
                        cancellationToken: ct);

                    await call.ResponseHeadersAsync.WaitAsync(ct);
                    backoffMs = 2000;

                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        var chunk = call.ResponseStream.Current;
                        if (chunk == null || chunk.ZstdPayload.IsEmpty) continue;

                        try
                        {
                            byte[] raw = ZstdCodec.Decode(chunk.ZstdPayload.Span, (int)chunk.DecodedSize);
                            var payload = SfLogApiChunkPayload.Parser.ParseFrom(raw);

                            Dispatcher.UIThread.Post(() =>
                            {
                                if (ct.IsCancellationRequested || SelectedNode != node) return;
                                foreach (var rec in payload.LogRecords)
                                    LogsTab.ApplyLogRecord(rec);
                                foreach (var api in payload.ApiCallRecords)
                                    LogsTab.ApplyApiCall(api);
                            });
                        }
                        catch
                        {
                            // Skip malformed chunk
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(backoffMs, ct); } catch { break; }
                    backoffMs = Math.Min(backoffMs * 2, 15000);
                }
            }
        }, ct);
    }

    private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0) return "0s";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    public void Dispose()
    {
        Disconnect();
        SettingsViewModel.Dispose();
        _ = _nodeManager.DisposeAsync();
    }
}
