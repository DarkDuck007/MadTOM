using System;
using System.Collections.Generic;

namespace SQUEEZE.Models;

public class AutoConnectNodePreference
{
    public string NodeId { get; set; } = string.Empty;
    public string PriorityInterface { get; set; } = string.Empty;
    public string? AuthToken { get; set; }
    public DateTime LastConnected { get; set; } = DateTime.UtcNow;
}

public class ServerConnectionSettings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string AuthToken { get; set; } = string.Empty;
    public bool AutoConnect { get; set; } = true;
    public string? LastConnectedNodeId { get; set; }
    public List<AutoConnectNodePreference> AutoConnectPriorityList { get; set; } = new();
}
