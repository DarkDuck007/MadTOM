using System;
using System.Collections.Generic;
using MadTOM.Models;

namespace MadTOM.Services;

public interface ITelemetryDataProvider : IDisposable
{
    IReadOnlyList<FleetNodeModel> GetFleetNodes();
    FleetNodeModel? GetNode(string hostId);
    ClusterTelemetrySummary GetClusterSummary();
    IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId);
    IReadOnlyList<DropRuleModel> GetDropRules(string hostId);
    IReadOnlyList<RegionTrafficModel> GetRegions(string hostId);
    
    event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
    event EventHandler<LogEntryModel>? LogReceived;
    
    void SendSignal(string hostId, int pid, int signal);
    void PauseLogs(bool paused);
    void ClearLogs();
}

