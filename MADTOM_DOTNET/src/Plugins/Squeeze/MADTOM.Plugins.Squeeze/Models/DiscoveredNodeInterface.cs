using System;

namespace SQUEEZE.Models;

public class DiscoveredNodeInterface
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string BaseUrl => $"http://{Host}:{Port}";
    public string DisplayText => $"{Host}:{Port}";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool MatchesUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) &&
                   uri.Port == Port;
        }
        return url.Contains($"{Host}:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) =>
        obj is DiscoveredNodeInterface other &&
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) &&
        Port == other.Port;

    public override int GetHashCode() =>
        HashCode.Combine(Host.ToLowerInvariant(), Port);

    public override string ToString() => DisplayText;
}
