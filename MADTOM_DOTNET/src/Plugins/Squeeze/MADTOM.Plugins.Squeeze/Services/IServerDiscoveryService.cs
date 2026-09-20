using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SQUEEZE.Services;

public class DiscoveredServer
{
    public string NodeId { get; set; } = "UNKNOWN";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string BaseUrl => $"http://{Host}:{Port}";
    public string Version { get; set; } = "1.0.0";
    public string HardwareGpu { get; set; } = "none";
    public bool RequiresAuth { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string DisplayText => $"{NodeId} ({Host}:{Port})";
}

public interface IServerDiscoveryService
{
    event EventHandler<DiscoveredServer>? ServerDiscovered;
    IReadOnlyList<DiscoveredServer> DiscoveredServers { get; }
    Task StartDiscoveryAsync(CancellationToken cancellationToken = default);
    Task StopDiscoveryAsync();
    Task ScanNetworkAsync(CancellationToken cancellationToken = default);
}

