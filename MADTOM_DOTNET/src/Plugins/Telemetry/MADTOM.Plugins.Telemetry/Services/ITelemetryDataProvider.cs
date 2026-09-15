using System;
using System.Collections.Generic;
using MadTOM.Models;

namespace MadTOM.Services;

public interface ITelemetryDataProvider : IDisposable
{
    TelemetryHistoryCache? HistoryCache => null;
    IReadOnlyList<FleetNodeModel> GetFleetNodes();
    FleetNodeModel? GetNode(string hostId);
    ClusterTelemetrySummary GetClusterSummary();
    IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId);
    IReadOnlyList<DropRuleModel> GetDropRules(string hostId);
    IReadOnlyList<RegionTrafficModel> GetRegions(string hostId);
    
    event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
    event EventHandler<LogEntryModel>? LogReceived;
    
    System.Threading.Tasks.Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>());

    System.Threading.Tasks.Task<MADTOM.Plugins.Telemetry.Proto.V1.NodeConfig?> GetNodeConfigAsync(string hostId, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult<MADTOM.Plugins.Telemetry.Proto.V1.NodeConfig?>(null);
    System.Threading.Tasks.Task<bool> UpdateNodeConfigAsync(string hostId, MADTOM.Plugins.Telemetry.Proto.V1.NodeConfig cfg, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(false);

    System.Threading.Tasks.Task<IReadOnlyList<LODPoint>> QueryHistoryWithResolutionAsync(string hostId, string metric, DateTime start, DateTime end, int targetPoints, System.Threading.CancellationToken ct = default)
        => QueryHistoryAsync(hostId, metric, start, end, ct);

    void SendSignal(string hostId, int pid, int signal);
    void PauseLogs(bool paused);
    void ClearLogs();
}

