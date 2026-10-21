using System;
using System.Collections.Generic;
using Google.Protobuf;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

public readonly record struct LogChunkKey(string Node, ulong ChunkId);

/// <summary>
/// Thread-safe client-side LRU cache for system log chunks.
/// Retains raw zstd-compressed chunks within a configurable memory budget,
/// and maintains a small LRU of decoded chunks for smooth UI viewport rendering.
/// </summary>
public sealed class LogChunkCache
{
    private readonly object _lock = new();

    // Compressed chunks cache (LRU)
    private readonly Dictionary<LogChunkKey, LinkedListNode<CompressedEntry>> _compressedMap = new();
    private readonly LinkedList<CompressedEntry> _compressedLru = new();
    private readonly Dictionary<string, List<ChunkRange>> _nodeRanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeStats> _nodeStats = new(StringComparer.OrdinalIgnoreCase);
    private long _totalCompressedBytes;
    private long _totalRawBytes;
    private long _totalRecordCount;
    private long _maxCacheBytes;

    private sealed class NodeStats
    {
        public long CompressedBytes;
        public long RawBytes;
        public long RecordCount;
    }

    // Decoded chunks cache (LRU)
    private readonly Dictionary<LogChunkKey, LinkedListNode<DecodedEntry>> _decodedMap = new();
    private readonly LinkedList<DecodedEntry> _decodedLru = new();
    private readonly Dictionary<LogChunkKey, LogChunkError> _chunkErrors = new();
    private int _maxDecodedChunks;

    public sealed class LogChunkError
    {
        public ulong ChunkId { get; init; }
        public string ErrorType { get; init; } = "UNKNOWN";
        public string Message { get; init; } = string.Empty;
        public string? StackTrace { get; init; }
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }

    public long TotalCompressedBytes
    {
        get
        {
            lock (_lock) return _totalCompressedBytes;
        }
    }

    public long TotalRawBytes
    {
        get
        {
            lock (_lock) return _totalRawBytes;
        }
    }

    public long TotalRecordCount
    {
        get
        {
            lock (_lock) return _totalRecordCount;
        }
    }

    public long StorageBytes
    {
        get
        {
            lock (_lock) return _totalCompressedBytes + (_compressedMap.Count * 64L) + (_decodedMap.Count * 256L);
        }
    }

    public int CompressedChunkCount
    {
        get
        {
            lock (_lock) return _compressedMap.Count;
        }
    }

    public int DecodedChunkCount
    {
        get
        {
            lock (_lock) return _decodedMap.Count;
        }
    }

    public long LimitBytes => MaxCacheBytes;
    public int DecodedChunkLimit => MaxDecodedChunks;

    public void Configure(long maxCacheBytes, int maxDecodedChunks)
    {
        lock (_lock)
        {
            _maxCacheBytes = Math.Max(16 * 1024 * 1024, maxCacheBytes);
            _maxDecodedChunks = Math.Max(4, maxDecodedChunks);
            EvictCompressedIfNeeded();
            EvictDecodedIfNeeded();
        }
    }

    public long MaxCacheBytes
    {
        get
        {
            lock (_lock) return _maxCacheBytes;
        }
        set
        {
            lock (_lock)
            {
                _maxCacheBytes = Math.Max(16 * 1024 * 1024, value);
                EvictCompressedIfNeeded();
            }
        }
    }

    public int MaxDecodedChunks
    {
        get
        {
            lock (_lock) return _maxDecodedChunks;
        }
        set
        {
            lock (_lock)
            {
                _maxDecodedChunks = Math.Max(4, value);
                EvictDecodedIfNeeded();
            }
        }
    }

    public LogChunkCache(long maxCacheBytes = 128L * 1024 * 1024, int maxDecodedChunks = 32)
    {
        _maxCacheBytes = Math.Max(16 * 1024 * 1024, maxCacheBytes);
        _maxDecodedChunks = Math.Max(4, maxDecodedChunks);
    }

    /// <summary>
    /// Inserts or updates a log chunk in the compressed cache.
    /// </summary>
    public void PutChunk(string collector, string node, LogChunk chunk)
    {
        if (chunk == null || string.IsNullOrEmpty(node)) return;
        var key = new LogChunkKey(node, chunk.ChunkId);
        var bytes = chunk.ZstdRecords?.Length ?? 0;
        var rawBytes = (long)chunk.RawSize;
        var recCount = (long)chunk.RecordCount;

        lock (_lock)
        {
            if (_compressedMap.TryGetValue(key, out var existingNode))
            {
                var existingChunk = existingNode.Value.Chunk;
                // Never overwrite a newer or more complete chunk with an older snapshot!
                if (existingChunk.LastSeq > chunk.LastSeq ||
                    (existingChunk.LastSeq == chunk.LastSeq && existingChunk.RecordCount > chunk.RecordCount) ||
                    (existingChunk.LastSeq == chunk.LastSeq && existingChunk.RecordCount == chunk.RecordCount && existingChunk.Sealed && !chunk.Sealed))
                {
                    return;
                }

                _totalCompressedBytes -= existingNode.Value.ByteSize;
                _totalRawBytes -= existingNode.Value.Chunk.RawSize;
                _totalRecordCount -= existingNode.Value.Chunk.RecordCount;

                if (_nodeStats.TryGetValue(node, out var ns))
                {
                    ns.CompressedBytes -= existingNode.Value.ByteSize;
                    ns.RawBytes -= existingNode.Value.Chunk.RawSize;
                    ns.RecordCount -= existingNode.Value.Chunk.RecordCount;
                }

                _compressedLru.Remove(existingNode);
                _compressedMap.Remove(key);
            }

            var entry = new CompressedEntry(key, chunk, bytes);
            var newNode = _compressedLru.AddFirst(entry);
            _compressedMap[key] = newNode;
            _totalCompressedBytes += bytes;
            _totalRawBytes += rawBytes;
            _totalRecordCount += recCount;

            var currentNs = GetOrCreateNodeStatsLocked(node);
            currentNs.CompressedBytes += bytes;
            currentNs.RawBytes += rawBytes;
            currentNs.RecordCount += recCount;

            UpdateNodeRangeLocked(node, chunk.ChunkId, chunk.FirstSeq, chunk.LastSeq);

            // Invalidate any cached decoded representation on update, so next fetch decodes the fresh records
            if (_decodedMap.TryGetValue(key, out var decodedNode))
            {
                _decodedLru.Remove(decodedNode);
                _decodedMap.Remove(key);
            }

            _chunkErrors.Remove(key);
            EvictCompressedIfNeeded();
        }
    }

    private NodeStats GetOrCreateNodeStatsLocked(string node)
    {
        if (!_nodeStats.TryGetValue(node, out var stats))
        {
            stats = new NodeStats();
            _nodeStats[node] = stats;
        }
        return stats;
    }

    public long GetNodeCompressedBytes(string node)
    {
        if (string.IsNullOrEmpty(node)) return 0;
        lock (_lock)
        {
            return _nodeStats.TryGetValue(node, out var s) ? s.CompressedBytes : 0;
        }
    }

    public long GetNodeRecordCount(string node)
    {
        if (string.IsNullOrEmpty(node)) return 0;
        lock (_lock)
        {
            return _nodeStats.TryGetValue(node, out var s) ? s.RecordCount : 0;
        }
    }

    private readonly record struct ChunkRange(ulong FirstSeq, ulong LastSeq, ulong ChunkId);

    private void UpdateNodeRangeLocked(string node, ulong chunkId, ulong firstSeq, ulong lastSeq)
    {
        if (!_nodeRanges.TryGetValue(node, out var list))
        {
            list = new List<ChunkRange>();
            _nodeRanges[node] = list;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ChunkId == chunkId)
            {
                list.RemoveAt(i);
                break;
            }
        }

        var newRange = new ChunkRange(firstSeq, lastSeq, chunkId);
        int insertIndex = list.BinarySearch(newRange, RangeComparer.Instance);
        if (insertIndex < 0) insertIndex = ~insertIndex;
        list.Insert(insertIndex, newRange);
    }

    private void RemoveNodeRangeLocked(string node, ulong chunkId)
    {
        if (_nodeRanges.TryGetValue(node, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ChunkId == chunkId)
                {
                    list.RemoveAt(i);
                    break;
                }
            }
        }
    }

    private sealed class RangeComparer : IComparer<ChunkRange>
    {
        public static readonly RangeComparer Instance = new();
        public int Compare(ChunkRange x, ChunkRange y) => x.FirstSeq.CompareTo(y.FirstSeq);
    }

    /// <summary>
    /// Returns the sequence bounds [minSeq, maxSeq] currently retained in the cache for the given node.
    /// </summary>
    public bool TryGetNodeSequenceRange(string node, out ulong minSeq, out ulong maxSeq)
    {
        minSeq = 0;
        maxSeq = 0;
        if (string.IsNullOrEmpty(node)) return false;
        lock (_lock)
        {
            if (_nodeRanges.TryGetValue(node, out var list) && list.Count > 0)
            {
                minSeq = list[0].FirstSeq;
                maxSeq = list[^1].LastSeq;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Retrieves a chunk from the compressed cache if present.
    /// </summary>
    public bool TryGetChunk(string collector, string node, ulong chunkId, out LogChunk? chunk)
    {
        if (string.IsNullOrEmpty(node))
        {
            chunk = null;
            return false;
        }
        var key = new LogChunkKey(node, chunkId);
        lock (_lock)
        {
            if (_compressedMap.TryGetValue(key, out var nodeEntry))
            {
                _compressedLru.Remove(nodeEntry);
                _compressedLru.AddFirst(nodeEntry);
                chunk = nodeEntry.Value.Chunk;
                return true;
            }
        }
        chunk = null;
        return false;
    }

    /// <summary>
    /// Gets or decodes the records for a specific chunk.
    /// </summary>
    public IReadOnlyList<LogRecord>? GetDecodedChunk(string collector, string node, ulong chunkId)
    {
        if (string.IsNullOrEmpty(node)) return null;
        var key = new LogChunkKey(node, chunkId);
        LogChunk? chunkToDecode = null;

        lock (_lock)
        {
            if (_decodedMap.TryGetValue(key, out var decodedNode))
            {
                _decodedLru.Remove(decodedNode);
                _decodedLru.AddFirst(decodedNode);
                return decodedNode.Value.Records;
            }

            if (_compressedMap.TryGetValue(key, out var compNode))
            {
                _compressedLru.Remove(compNode);
                _compressedLru.AddFirst(compNode);
                chunkToDecode = compNode.Value.Chunk;
            }
        }

        if (chunkToDecode == null || chunkToDecode.ZstdRecords == null || chunkToDecode.ZstdRecords.IsEmpty)
        {
            return null;
        }

        try
        {
            var raw = ZstdCodec.Decode(chunkToDecode.ZstdRecords.Span, (int)chunkToDecode.RawSize);
            var list = new LogRecordList();
            list.MergeFrom(raw);

            var records = list.Records;
            lock (_lock)
            {
                // Verify chunk was not replaced with a newer version or evicted while decoding
                if (!_compressedMap.TryGetValue(key, out var currentCompNode) ||
                    !ReferenceEquals(currentCompNode.Value.Chunk, chunkToDecode))
                {
                    return records;
                }

                if (_decodedMap.TryGetValue(key, out var existing))
                {
                    _decodedLru.Remove(existing);
                    _decodedMap.Remove(key);
                }

                var entry = new DecodedEntry(key, records);
                var newNode = _decodedLru.AddFirst(entry);
                _decodedMap[key] = newNode;
                EvictDecodedIfNeeded();
            }

            return records;
        }
        catch (Exception ex)
        {
            RecordChunkError(node, chunkId, "DECODE_ERROR", $"Zstd decompression / proto unmarshal failed: {ex.Message}", ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// Records a chunk load or decode error for a specific node and chunk ID.
    /// </summary>
    public void RecordChunkError(string node, ulong chunkId, string errorType, string message, string? stackTrace = null)
    {
        if (string.IsNullOrEmpty(node)) return;
        var key = new LogChunkKey(node, chunkId);
        lock (_lock)
        {
            _chunkErrors[key] = new LogChunkError
            {
                ChunkId = chunkId,
                ErrorType = errorType,
                Message = message,
                StackTrace = stackTrace,
                TimestampUtc = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Checks if a chunk load error was recorded for the given node and chunk ID.
    /// </summary>
    public bool TryGetChunkError(string node, ulong chunkId, out LogChunkError? error)
    {
        if (string.IsNullOrEmpty(node))
        {
            error = null;
            return false;
        }
        var key = new LogChunkKey(node, chunkId);
        lock (_lock)
        {
            return _chunkErrors.TryGetValue(key, out error);
        }
    }

    /// <summary>
    /// Attempts to find the chunk ID containing a specific sequence number for the given node.
    /// Returns true if resolved from active node ranges or compressed chunks; otherwise outputs seq / 256.
    /// </summary>
    public bool TryGetChunkIdForSeq(string node, ulong seq, out ulong chunkId)
    {
        chunkId = seq / 256;
        if (string.IsNullOrEmpty(node)) return false;

        lock (_lock)
        {
            var heuristicKey = new LogChunkKey(node, chunkId);
            if (_compressedMap.TryGetValue(heuristicKey, out var compNode) &&
                seq >= compNode.Value.Chunk.FirstSeq && seq <= compNode.Value.Chunk.LastSeq)
            {
                return true;
            }

            if (seq > 0)
            {
                var altKey = new LogChunkKey(node, (seq - 1) / 256);
                if (_compressedMap.TryGetValue(altKey, out var altNode) &&
                    seq >= altNode.Value.Chunk.FirstSeq && seq <= altNode.Value.Chunk.LastSeq)
                {
                    chunkId = (seq - 1) / 256;
                    return true;
                }
            }

            if (_nodeRanges.TryGetValue(node, out var list) && list.Count > 0)
            {
                int low = 0, high = list.Count - 1;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    var r = list[mid];
                    if (seq < r.FirstSeq)
                    {
                        high = mid - 1;
                    }
                    else if (seq > r.LastSeq)
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        chunkId = r.ChunkId;
                        return true;
                    }
                }

                // If node is 1-based and beyond known ranges
                if (list[0].FirstSeq == 1 && seq > 0)
                {
                    chunkId = (seq - 1) / 256;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets an individual log record by its absolute sequence number if cached.
    /// </summary>
    public LogRecord? GetRecord(string collector, string node, ulong seq)
    {
        if (string.IsNullOrEmpty(node)) return null;

        TryGetChunkIdForSeq(node, seq, out ulong targetChunkId);

        var records = GetDecodedChunk(collector, node, targetChunkId);
        if (records == null && seq > 0)
        {
            ulong altId = (seq - 1) / 256;
            if (altId != targetChunkId)
            {
                var altRecords = GetDecodedChunk(collector, node, altId);
                if (altRecords != null)
                {
                    records = altRecords;
                    targetChunkId = altId;
                }
            }
        }

        if (records != null && records.Count > 0)
        {
            ulong firstSeq = records[0].Seq;
            if (seq >= firstSeq)
            {
                int index = (int)(seq - firstSeq);
                if (index >= 0 && index < records.Count)
                {
                    var r = records[index];
                    if (r.Seq == seq) return r;
                }
            }

            // Secondary search in case sequence numbers have gaps or non-standard indexing
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Seq == seq) return records[i];
            }
        }

        // Countermeasure: If chunk loading or decoding recorded an error, synthesize an error record
        LogChunkError? chunkErr = null;
        if (TryGetChunkError(node, targetChunkId, out var err))
        {
            chunkErr = err;
        }
        else if (seq > 0 && TryGetChunkError(node, (seq - 1) / 256, out var altErr))
        {
            chunkErr = altErr;
            targetChunkId = (seq - 1) / 256;
        }

        if (chunkErr != null)
        {
            long tsNano = new DateTimeOffset(chunkErr.TimestampUtc).ToUnixTimeMilliseconds() * 1_000_000L;
            string stackSnippet = string.IsNullOrWhiteSpace(chunkErr.StackTrace)
                ? string.Empty
                : $" | StackTrace: {chunkErr.StackTrace.Trim().Replace("\r\n", " -> ").Replace("\n", " -> ")}";

            return new LogRecord
            {
                Seq = seq,
                TimestampUnixNano = tsNano,
                Priority = 1, // Syslog Alert / Critical (renders prominent red)
                Unit = $"[ERROR:{chunkErr.ErrorType}]",
                Message = $"[LOG_LOAD_FAILED] Record #{seq} (chunk #{targetChunkId}) failed: {chunkErr.Message}{stackSnippet}"
            };
        }

        return null;
    }

    /// <summary>
    /// Clears cached log chunks for a specific node or all nodes.
    /// </summary>
    public void Clear(string? node = null)
    {
        lock (_lock)
        {
            if (node == null)
            {
                _compressedMap.Clear();
                _compressedLru.Clear();
                _decodedMap.Clear();
                _decodedLru.Clear();
                _nodeRanges.Clear();
                _nodeStats.Clear();
                _chunkErrors.Clear();
                _totalCompressedBytes = 0;
                _totalRawBytes = 0;
                _totalRecordCount = 0;
                return;
            }

            _nodeRanges.Remove(node);
            _nodeStats.Remove(node);

            var errKeysToRemove = new List<LogChunkKey>();
            foreach (var kvp in _chunkErrors)
            {
                if (kvp.Key.Node == node) errKeysToRemove.Add(kvp.Key);
            }
            foreach (var k in errKeysToRemove) _chunkErrors.Remove(k);

            var compKeysToRemove = new List<LogChunkKey>();
            foreach (var kvp in _compressedMap)
            {
                if (kvp.Key.Node == node) compKeysToRemove.Add(kvp.Key);
            }
            foreach (var k in compKeysToRemove)
            {
                if (_compressedMap.TryGetValue(k, out var n))
                {
                    _totalCompressedBytes -= n.Value.ByteSize;
                    _totalRawBytes -= n.Value.Chunk.RawSize;
                    _totalRecordCount -= n.Value.Chunk.RecordCount;
                    _compressedLru.Remove(n);
                    _compressedMap.Remove(k);
                }
            }

            var decKeysToRemove = new List<LogChunkKey>();
            foreach (var kvp in _decodedMap)
            {
                if (kvp.Key.Node == node) decKeysToRemove.Add(kvp.Key);
            }
            foreach (var k in decKeysToRemove)
            {
                if (_decodedMap.TryGetValue(k, out var n))
                {
                    _decodedLru.Remove(n);
                    _decodedMap.Remove(k);
                }
            }
        }
    }

    private void EvictCompressedIfNeeded()
    {
        while (_totalCompressedBytes > _maxCacheBytes && _compressedLru.Count > 1)
        {
            var last = _compressedLru.Last;
            if (last == null) break;

            _totalCompressedBytes -= last.Value.ByteSize;
            _totalRawBytes -= last.Value.Chunk.RawSize;
            _totalRecordCount -= last.Value.Chunk.RecordCount;

            if (_nodeStats.TryGetValue(last.Value.Key.Node, out var ns))
            {
                ns.CompressedBytes -= last.Value.ByteSize;
                ns.RawBytes -= last.Value.Chunk.RawSize;
                ns.RecordCount -= last.Value.Chunk.RecordCount;
            }

            RemoveNodeRangeLocked(last.Value.Key.Node, last.Value.Key.ChunkId);
            _compressedMap.Remove(last.Value.Key);
            _compressedLru.RemoveLast();

            // Also remove from decoded map if present
            if (_decodedMap.TryGetValue(last.Value.Key, out var decNode))
            {
                _decodedLru.Remove(decNode);
                _decodedMap.Remove(last.Value.Key);
            }
        }
    }

    private void EvictDecodedIfNeeded()
    {
        while (_decodedLru.Count > _maxDecodedChunks)
        {
            var last = _decodedLru.Last;
            if (last == null) break;

            _decodedMap.Remove(last.Value.Key);
            _decodedLru.RemoveLast();
        }
    }

    private sealed record CompressedEntry(LogChunkKey Key, LogChunk Chunk, int ByteSize);
    private sealed record DecodedEntry(LogChunkKey Key, IReadOnlyList<LogRecord> Records);
}
