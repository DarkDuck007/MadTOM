using System;

namespace SQUEEZE.Models;

public class ServerConnectionSettings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string AuthToken { get; set; } = string.Empty;
    public bool AutoConnect { get; set; } = true;
    public string? LastConnectedNodeId { get; set; }
}

