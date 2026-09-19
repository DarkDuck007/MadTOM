using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MadTOM.Services;

/// <summary>Logical received protobuf payloads; excludes framing, TLS and socket bytes.</summary>
public sealed class ClientPayloadCounter
{
    private readonly object _gate = new();
    private long _bytes, _messages, _rawBytes, _zstdBytes, _zstdRawBytes, _zstdMessages;
    public void Record(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate) { _bytes += bytes; _rawBytes += bytes; _messages++; }
    }
    public void RecordCompressed(int decodedBytes, int encodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(encodedBytes);
        lock (_gate) { _bytes += decodedBytes; _zstdRawBytes += decodedBytes; _zstdBytes += encodedBytes; _messages++; _zstdMessages++; }
    }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public long Messages { get { lock (_gate) return _messages; } }
    public (long Raw, long Encoded, long Decoded, long CompressedMessages, long Messages) Snapshot()
    { lock (_gate) return (_rawBytes, _zstdBytes, _zstdRawBytes, _zstdMessages, _messages); }

}

public sealed record CompressionDiagnosticRow(string Scope, string State, string Raw, string Zstd,
    string Ratio, string Count, string RawPassed = "—", string TotalPayload = "—");

public sealed record ClientCompressionSnapshot(string Summary, IReadOnlyList<CompressionDiagnosticRow> Rows)
{
    public static ClientCompressionSnapshot Unavailable { get; } = new("Unknown", new[]
    {
        new CompressionDiagnosticRow("Provider", "Not reported", "—", "—", "—", "—")
    });

    public static ClientCompressionSnapshot Capture(TelemetryHistoryCache cache,
        IEnumerable<CollectorClientService> clients)
    {
        var usage = cache.GetUsage(1);
        var rows = new List<CompressionDiagnosticRow>
        {
            MemoryRow("Memory · live history", usage.LiveBytes, usage.LiveZstdBytes, usage.LiveZstdRawBytes, usage.LivePoints),
            MemoryRow("Memory · stored queries", usage.StoredBytes, usage.StoredZstdBytes, usage.StoredZstdRawBytes, usage.StoredPoints),
            MemoryRow("Memory · total", usage.TotalBytes, usage.LiveZstdBytes + usage.StoredZstdBytes,
                usage.LiveZstdRawBytes + usage.StoredZstdRawBytes, usage.LivePoints + usage.StoredPoints)
        };
        foreach (var client in clients.OrderBy(c => c.Endpoint, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (name, counter) in client.ReceivedPayloads)
            {
                var c = counter.Snapshot();
                rows.Add(new($"{client.Endpoint} · {name}", c.CompressedMessages > 0 ? "Zstd active" : "Raw so far",
                    FormatBytes(c.Decoded), FormatBytes(c.Encoded), Ratio(c.Decoded, c.Encoded),
                    $"{c.CompressedMessages:N0}/{c.Messages:N0}", FormatBytes(c.Raw), FormatBytes(c.Raw + c.Encoded)));
            }
            if (client.TransportDiagnostics.Count > 0) rows.AddRange(client.TransportDiagnostics);
            else rows.Add(new($"{client.Endpoint} · daemon → Collector", "Not reported", "—", "—", "—", "—"));
        }
        return new(FormatBytes(usage.LiveZstdBytes + usage.StoredZstdBytes), rows);
    }

    private static string Ratio(long decoded, long encoded) => encoded > 0
        ? ((double)decoded / encoded).ToString("0.00", CultureInfo.InvariantCulture) + "×" : "—";
    private static CompressionDiagnosticRow MemoryRow(string name, long retained, long encoded, long decoded, long count)
        => new(name, encoded > 0 ? "Zstd + raw" : "Raw", FormatBytes(decoded), FormatBytes(encoded),
            Ratio(decoded, encoded), count.ToString("N0"), "—", "~" + FormatBytes(retained));

    public IReadOnlyList<CompressionDiagnosticRow> MemoryRows => Rows.Where(r => r.Scope.StartsWith("Memory ·", StringComparison.Ordinal)).ToArray();
    public IReadOnlyList<CompressionDiagnosticRow> TransportRowsView => Rows.Where(r => !r.Scope.StartsWith("Memory ·", StringComparison.Ordinal)).ToArray();

    public static CompressionDiagnosticRow[] TransportRows(string endpoint, MADTOM.Plugins.Telemetry.Proto.V1.ListNodesResponse response)
    {
        if (!response.TransportStatsSupported)
            return new[] { new CompressionDiagnosticRow(endpoint + " · daemon → Collector", "Not reported", "—", "—", "—", "—") };
        if (response.TransportStats.Count == 0)
            return new[] { new CompressionDiagnosticRow(endpoint + " · daemon → Collector", "Awaiting data", "—", "—", "—", "0") };
        return response.TransportStats.Select(s => new CompressionDiagnosticRow(
            $"{endpoint} · {s.NodeId} → Collector · {s.Mode}",
            s.ZstdBatches > 0 ? "Zstd observed" : "No zstd seen",
            FormatBytes((long)Math.Min(s.DecodedZstdBytes, (ulong)long.MaxValue)),
            FormatBytes((long)Math.Min(s.ZstdBytes, (ulong)long.MaxValue)),
            s.ZstdBytes > 0 ? ((double)s.DecodedZstdBytes / s.ZstdBytes).ToString("0.00", CultureInfo.InvariantCulture) + "×" : "—",
            $"{s.ZstdBatches:N0}/{s.Batches:N0}",
            s.RawBytesSupported ? FormatBytes((long)Math.Min(s.RawBytes, (ulong)long.MaxValue)) : "—",
            s.RawBytesSupported ? FormatBytes((long)Math.Min(s.RawBytes + s.ZstdBytes, (ulong)long.MaxValue)) : "—" )).ToArray();
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1073741824 => (bytes / 1073741824d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
        >= 1048576 => (bytes / 1048576d).ToString("0.##", CultureInfo.InvariantCulture) + " MiB",
        >= 1024 => (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };
}
