using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MADTOM.Plugins.SlideForge.Services;

public enum SfPinResult
{
    FirstUse,
    Match,
    Mismatch,
}

public sealed record SfPinMismatch(string NodeKey, string Pinned, string Presented);

public sealed record SfPinFirstUse(string NodeKey, string Presented);

/// <summary>
/// Trust-On-First-Use (TOFU) TLS certificate SHA-256 pinning store for slideforge-probe endpoints.
/// Fails closed if the backing JSON file is corrupt.
/// </summary>
public sealed class SfCertificatePins
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath => _path;
    public string? LoadError { get; private set; }
    public SfPinMismatch? LastMismatch { get; private set; }
    public SfPinFirstUse? LastFirstUse { get; private set; }

    public SfCertificatePins(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Pin file path must not be empty.", nameof(path));

        _path = path;
        Load();
    }

    public static string Fingerprint(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        return Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
    }

    public static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;

        Span<char> buffer = stackalloc char[64];
        int pos = 0;
        foreach (char c in fingerprint)
        {
            if (c == ':' || char.IsWhiteSpace(c))
                continue;
            if (pos >= 64)
                return null;
            if (c is >= '0' and <= '9')
            {
                buffer[pos++] = c;
            }
            else if (c is >= 'a' and <= 'f')
            {
                buffer[pos++] = c;
            }
            else if (c is >= 'A' and <= 'F')
            {
                buffer[pos++] = (char)(c + ('a' - 'A'));
            }
            else
            {
                return null;
            }
        }

        return pos == 64 ? new string(buffer) : null;
    }

    public SfPinResult Evaluate(string nodeKey, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
            return SfPinResult.Mismatch;

        lock (_gate)
        {
            if (LoadError is not null)
                return SfPinResult.Mismatch;

            string? normalized = NormalizeFingerprint(fingerprint);
            if (normalized is null)
                return SfPinResult.Mismatch;

            if (!_pins.TryGetValue(nodeKey, out string? pinned))
                return SfPinResult.FirstUse;

            return string.Equals(pinned, normalized, StringComparison.Ordinal)
                ? SfPinResult.Match
                : SfPinResult.Mismatch;
        }
    }

    public void Pin(string nodeKey, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
            throw new ArgumentException("Node key must not be empty.", nameof(nodeKey));

        string? normalized = NormalizeFingerprint(fingerprint);
        if (normalized is null)
            throw new ArgumentException("Fingerprint must be exactly 64 hexadecimal characters.", nameof(fingerprint));

        lock (_gate)
        {
            _pins[nodeKey] = normalized;
            if (LastMismatch != null && string.Equals(LastMismatch.NodeKey, nodeKey, StringComparison.OrdinalIgnoreCase))
                LastMismatch = null;
            if (LastFirstUse != null && string.Equals(LastFirstUse.NodeKey, nodeKey, StringComparison.OrdinalIgnoreCase))
                LastFirstUse = null;
            SaveLocked();
        }
    }

    public void Forget(string nodeKey)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
            return;

        lock (_gate)
        {
            bool removed = _pins.Remove(nodeKey);
            if (LastMismatch != null && string.Equals(LastMismatch.NodeKey, nodeKey, StringComparison.OrdinalIgnoreCase))
                LastMismatch = null;
            if (removed)
                SaveLocked();
        }
    }

    public string? GetPin(string nodeKey)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
            return null;

        lock (_gate)
        {
            return _pins.TryGetValue(nodeKey, out string? fp) ? fp : null;
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_pins, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool Validate(string nodeKey, X509Certificate2? cert, bool trustOnFirstUse)
    {
        if (cert is null || string.IsNullOrWhiteSpace(nodeKey))
            return false;

        string presented = Fingerprint(cert);

        lock (_gate)
        {
            if (LoadError is not null)
            {
                LastMismatch = new SfPinMismatch(nodeKey, "<corrupt-pin-store>", presented);
                return false;
            }

            if (!_pins.TryGetValue(nodeKey, out string? pinned))
            {
                if (trustOnFirstUse)
                {
                    _pins[nodeKey] = presented;
                    LastFirstUse = null;
                    SaveLocked();
                    return true;
                }

                LastFirstUse = new SfPinFirstUse(nodeKey, presented);
                return false;
            }

            if (string.Equals(pinned, presented, StringComparison.Ordinal))
            {
                return true;
            }

            // Never override an existing pin, even when trustOnFirstUse is true.
            LastMismatch = new SfPinMismatch(nodeKey, pinned, presented);
            return false;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _pins.Clear();
            LoadError = null;

            if (!File.Exists(_path))
                return;

            try
            {
                string raw = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (loaded is null)
                {
                    LoadError = "Pin file deserialized to null.";
                    return;
                }

                foreach (var kvp in loaded)
                {
                    string? normalized = NormalizeFingerprint(kvp.Value);
                    if (string.IsNullOrWhiteSpace(kvp.Key) || normalized is null)
                    {
                        _pins.Clear();
                        LoadError = $"Invalid pin entry for '{kvp.Key}'.";
                        return;
                    }
                    _pins[kvp.Key] = normalized;
                }
            }
            catch (Exception ex)
            {
                _pins.Clear();
                LoadError = ex.Message;
            }
        }
    }

    private void SaveLocked()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true });
        string tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
