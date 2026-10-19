using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MADTOM.Identity;

public sealed class TrustEntry
{
    public string TrustId { get; set; } = string.Empty;
    public string CertFingerprintSha256 { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public List<string> GrantedScopes { get; set; } = new();
    public string Issuer { get; set; } = "local";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

public interface ITrustProvider
{
    bool IsTrusted(string certFingerprintSha256, string? requiredScope = null);
    TrustEntry? GetByFingerprint(string certFingerprintSha256);
    TrustEntry? GetById(string trustId);
    IReadOnlyList<TrustEntry> GetAll();
    Task AddOrUpdateTrustAsync(TrustEntry entry);
    Task<bool> RevokeTrustAsync(string trustId);
    void UpdateLastSeen(string certFingerprintSha256);
}

public sealed class TrustStore : ITrustProvider
{
    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, TrustEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public TrustStore(string? customStorePath = null)
    {
        _storePath = customStorePath ?? GetDefaultStorePath();
        Load();
    }

    public static string GetDefaultStorePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "MADTOM");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "trust_store.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Path.Combine(home, ".local", "share", "madtom");
        Directory.CreateDirectory(xdg);
        return Path.Combine(xdg, "trust_store.json");
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<TrustEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (list != null)
            {
                foreach (var item in list)
                {
                    _entries[item.CertFingerprintSha256] = item;
                }
            }
        }
        catch { }
    }

    private async Task SaveAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var list = _entries.Values.ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var tmp = _storePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _storePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool IsTrusted(string certFingerprintSha256, string? requiredScope = null)
    {
        if (string.IsNullOrEmpty(certFingerprintSha256)) return false;
        if (!_entries.TryGetValue(certFingerprintSha256, out var entry)) return false;

        if (string.IsNullOrEmpty(requiredScope)) return true;
        return entry.GrantedScopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
    }

    public TrustEntry? GetByFingerprint(string certFingerprintSha256)
    {
        _entries.TryGetValue(certFingerprintSha256, out var entry);
        return entry;
    }

    public TrustEntry? GetById(string trustId)
    {
        return _entries.Values.FirstOrDefault(e => string.Equals(e.TrustId, trustId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<TrustEntry> GetAll() => _entries.Values.ToList();

    public async Task AddOrUpdateTrustAsync(TrustEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.TrustId))
        {
            entry.TrustId = "t_" + Guid.NewGuid().ToString("N")[..12];
        }

        _entries[entry.CertFingerprintSha256] = entry;
        await SaveAsync();
    }

    public async Task<bool> RevokeTrustAsync(string trustId)
    {
        var target = _entries.Values.FirstOrDefault(e => string.Equals(e.TrustId, trustId, StringComparison.OrdinalIgnoreCase));
        if (target != null && _entries.TryRemove(target.CertFingerprintSha256, out _))
        {
            await SaveAsync();
            return true;
        }
        return false;
    }

    public void UpdateLastSeen(string certFingerprintSha256)
    {
        if (_entries.TryGetValue(certFingerprintSha256, out var entry))
        {
            entry.LastSeenAtUtc = DateTime.UtcNow;
        }
    }
}
