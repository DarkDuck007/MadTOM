using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.Services;

public readonly record struct ApiChunkKey(string Node, ulong ChunkId);
public readonly record struct NodeCallKey(string Node, string CallId);

/// <summary>
/// High-efficiency client-side in-memory cache for NoxChat Raw LLM API Calls.
/// Consolidates incoming individual/small API calls into large chunks (target 32 calls)
/// to maximize Zstd dictionary and back-reference compression efficiency.
/// Employs on-the-fly partial decoding: individual records are decompressed on demand
/// and retained in a small decoded LRU cache while raw payloads remain compressed in memory.
/// </summary>
public sealed class NoxApiCallChunkCache
{
    public const int TargetChunkRecords = 32;

    private readonly object _lock = new();

    // Compressed chunks cache (LRU)
    private readonly Dictionary<ApiChunkKey, LinkedListNode<CompressedEntry>> _compressedMap = new();
    private readonly LinkedList<CompressedEntry> _compressedLru = new();

    // Decoded chunks cache (LRU) for on-the-fly viewing
    private readonly Dictionary<ApiChunkKey, LinkedListNode<DecodedEntry>> _decodedMap = new();
    private readonly LinkedList<DecodedEntry> _decodedLru = new();

    // CallId -> ChunkId mapping for instantaneous O(1) partial record lookup
    private readonly Dictionary<NodeCallKey, ulong> _callIdToChunkMap = new();

    // Unsealed staging buffer per node before reaching TargetChunkRecords (32)
    private readonly Dictionary<string, List<NoxRawApiCallRecord>> _pendingRecordsPerNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _nextChunkIdPerNode = new(StringComparer.OrdinalIgnoreCase);

    private long _totalCompressedBytes;
    private long _totalRawBytes;
    private long _totalRecordCount;
    private long _maxCacheBytes;
    private int _maxDecodedChunks;

    public long TotalCompressedBytes { get { lock (_lock) return _totalCompressedBytes; } }
    public long TotalRawBytes { get { lock (_lock) return _totalRawBytes; } }
    public long StorageBytes
    {
        get
        {
            lock (_lock)
            {
                long pendingBytes = 0;
                foreach (var list in _pendingRecordsPerNode.Values)
                {
                    pendingBytes += list.Count * 256L;
                }
                return _totalCompressedBytes + (_compressedMap.Count * 64L) + (_decodedMap.Count * 256L) + (_callIdToChunkMap.Count * 48L) + pendingBytes;
            }
        }
    }
    public double Ratio { get { lock (_lock) return _totalCompressedBytes > 0 ? (double)_totalRawBytes / _totalCompressedBytes : 1.0; } }
    public long TotalRecordCount { get { lock (_lock) return _totalRecordCount; } }
    public int CompressedChunkCount { get { lock (_lock) return _compressedMap.Count; } }
    public int DecodedChunkCount { get { lock (_lock) return _decodedMap.Count; } }

    public NoxApiCallChunkCache(long maxCacheBytes = 64L * 1024 * 1024, int maxDecodedChunks = 16)
    {
        _maxCacheBytes = Math.Max(8 * 1024 * 1024, maxCacheBytes);
        _maxDecodedChunks = Math.Max(1, maxDecodedChunks);
    }

    public void Configure(long maxCacheBytes, int maxDecodedChunks)
    {
        lock (_lock)
        {
            _maxCacheBytes = Math.Max(8 * 1024 * 1024, maxCacheBytes);
            _maxDecodedChunks = Math.Max(1, maxDecodedChunks);
            EvictCompressedIfNeeded();
            EvictDecodedIfNeeded();
        }
    }

    /// <summary>
    /// Ingests a NoxApiCallChunk from the server/WAL. Decodes any records and consolidates them
    /// into large, highly-compressed client chunks (32 records per chunk).
    /// Returns the decoded records.
    /// </summary>
    public IReadOnlyList<NoxRawApiCallRecord> PutChunk(string node, NoxApiCallChunk chunk)
    {
        if (chunk == null || string.IsNullOrEmpty(node)) return Array.Empty<NoxRawApiCallRecord>();

        IReadOnlyList<NoxRawApiCallRecord>? records = null;
        if (chunk.ZstdRecords != null && !chunk.ZstdRecords.IsEmpty)
        {
            try
            {
                var raw = ZstdCodec.Decode(chunk.ZstdRecords.Span, (int)chunk.RawSize);
                var list = new NoxApiCallRecordList();
                list.MergeFrom(raw);
                records = list.Records;
            }
            catch { }
        }

        if (records == null || records.Count == 0)
        {
            return Array.Empty<NoxRawApiCallRecord>();
        }

        PutRecords(node, records);
        return records;
    }

    /// <summary>
    /// Ingests multiple API call records for a node, consolidating into large 32-record chunks.
    /// </summary>
    public void PutRecords(string node, IEnumerable<NoxRawApiCallRecord> records)
    {
        if (string.IsNullOrEmpty(node) || records == null) return;

        lock (_lock)
        {
            if (!_pendingRecordsPerNode.TryGetValue(node, out var pending))
            {
                pending = new List<NoxRawApiCallRecord>(TargetChunkRecords);
                _pendingRecordsPerNode[node] = pending;
            }

            foreach (var rec in records)
            {
                if (string.IsNullOrEmpty(rec.CallId)) continue;
                var callKey = new NodeCallKey(node, rec.CallId);

                // Skip duplicate records if already indexed
                if (_callIdToChunkMap.ContainsKey(callKey)) continue;

                pending.Add(rec);

                if (pending.Count >= TargetChunkRecords)
                {
                    SealChunkLocked(node, pending);
                    pending.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Flushes any unsealed pending records for a node (or all nodes) into a sealed chunk.
    /// </summary>
    public void FlushPending(string? node = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(node))
            {
                if (_pendingRecordsPerNode.TryGetValue(node, out var pending) && pending.Count > 0)
                {
                    SealChunkLocked(node, pending);
                    pending.Clear();
                }
                return;
            }

            foreach (var kvp in _pendingRecordsPerNode)
            {
                if (kvp.Value.Count > 0)
                {
                    SealChunkLocked(kvp.Key, kvp.Value);
                    kvp.Value.Clear();
                }
            }
        }
    }

    private void SealChunkLocked(string node, List<NoxRawApiCallRecord> records)
    {
        if (records.Count == 0) return;

        if (!_nextChunkIdPerNode.TryGetValue(node, out ulong chunkId))
        {
            chunkId = 0;
        }
        _nextChunkIdPerNode[node] = chunkId + 1;

        var key = new ApiChunkKey(node, chunkId);
        var list = new NoxApiCallRecordList();
        list.Records.AddRange(records);

        byte[] raw = list.ToByteArray();
        byte[] compressed = ZstdCodec.Encode(raw);

        var chunk = new NoxApiCallChunk
        {
            NodeId = node,
            ChunkId = chunkId,
            FirstSeq = chunkId * (ulong)TargetChunkRecords,
            LastSeq = (chunkId * (ulong)TargetChunkRecords) + (ulong)records.Count - 1,
            RecordCount = (uint)records.Count,
            RawSize = (uint)raw.Length,
            ZstdRecords = ByteString.CopyFrom(compressed),
            Sealed = true
        };

        var entry = new CompressedEntry(key, chunk, compressed.Length);
        var nodeRef = _compressedLru.AddFirst(entry);
        _compressedMap[key] = nodeRef;

        _totalCompressedBytes += compressed.Length;
        _totalRawBytes += raw.Length;
        _totalRecordCount += records.Count;

        // Map CallIds to this new chunkId
        foreach (var r in records)
        {
            if (!string.IsNullOrEmpty(r.CallId))
            {
                _callIdToChunkMap[new NodeCallKey(node, r.CallId)] = chunkId;
            }
        }

        // Keep this recently sealed chunk in decoded LRU for immediate fast UI access
        var decodedEntry = new DecodedEntry(key, new List<NoxRawApiCallRecord>(records));
        var decNode = _decodedLru.AddFirst(decodedEntry);
        _decodedMap[key] = decNode;

        EvictCompressedIfNeeded();
        EvictDecodedIfNeeded();
    }

    /// <summary>
    /// Partially decodes on the fly: retrieves a single API call record by CallId.
    /// If the chunk containing this call is not yet decoded, it decompresses that specific chunk on the fly.
    /// </summary>
    public NoxRawApiCallRecord? GetRecord(string node, string callId)
    {
        if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(callId)) return null;

        lock (_lock)
        {
            // Check pending unsealed buffer first
            if (_pendingRecordsPerNode.TryGetValue(node, out var pending))
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].CallId == callId) return pending[i];
                }
            }

            var callKey = new NodeCallKey(node, callId);
            if (!_callIdToChunkMap.TryGetValue(callKey, out ulong chunkId))
            {
                return null;
            }

            // Check if chunk is already decoded in LRU
            var chunkKey = new ApiChunkKey(node, chunkId);
            if (_decodedMap.TryGetValue(chunkKey, out var decNode))
            {
                _decodedLru.Remove(decNode);
                _decodedLru.AddFirst(decNode);
                foreach (var r in decNode.Value.Records)
                {
                    if (r.CallId == callId) return r;
                }
            }
        }

        // Decode on the fly outside the initial lock
        var decoded = GetDecodedChunk(node, GetChunkIdForCall(node, callId));
        if (decoded != null)
        {
            foreach (var r in decoded)
            {
                if (r.CallId == callId) return r;
            }
        }

        return null;
    }

    private ulong GetChunkIdForCall(string node, string callId)
    {
        lock (_lock)
        {
            return _callIdToChunkMap.TryGetValue(new NodeCallKey(node, callId), out var id) ? id : 0;
        }
    }

    public IReadOnlyList<NoxRawApiCallRecord>? GetDecodedChunk(string node, ulong chunkId)
    {
        if (string.IsNullOrEmpty(node)) return null;
        var key = new ApiChunkKey(node, chunkId);
        NoxApiCallChunk? chunkToDecode = null;

        lock (_lock)
        {
            if (_decodedMap.TryGetValue(key, out var decNode))
            {
                _decodedLru.Remove(decNode);
                _decodedLru.AddFirst(decNode);
                return decNode.Value.Records;
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
            var list = new NoxApiCallRecordList();
            list.MergeFrom(raw);
            var records = list.Records;

            lock (_lock)
            {
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
        catch
        {
            return null;
        }
    }

    public void Clear(string? node = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(node))
            {
                _compressedMap.Clear();
                _compressedLru.Clear();
                _decodedMap.Clear();
                _decodedLru.Clear();
                _callIdToChunkMap.Clear();
                _pendingRecordsPerNode.Clear();
                _nextChunkIdPerNode.Clear();
                _totalCompressedBytes = 0;
                _totalRawBytes = 0;
                _totalRecordCount = 0;
                return;
            }

            // Clear specific node
            _pendingRecordsPerNode.Remove(node);
            _nextChunkIdPerNode.Remove(node);

            var toRemoveComp = _compressedMap.Keys.Where(k => string.Equals(k.Node, node, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in toRemoveComp)
            {
                if (_compressedMap.TryGetValue(k, out var nodeEntry))
                {
                    _totalCompressedBytes -= nodeEntry.Value.ByteSize;
                    _totalRawBytes -= nodeEntry.Value.Chunk.RawSize;
                    _totalRecordCount -= nodeEntry.Value.Chunk.RecordCount;
                    _compressedLru.Remove(nodeEntry);
                    _compressedMap.Remove(k);
                }
            }

            var toRemoveDec = _decodedMap.Keys.Where(k => string.Equals(k.Node, node, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in toRemoveDec)
            {
                if (_decodedMap.TryGetValue(k, out var decEntry))
                {
                    _decodedLru.Remove(decEntry);
                    _decodedMap.Remove(k);
                }
            }

            var toRemoveCalls = _callIdToChunkMap.Keys.Where(k => string.Equals(k.Node, node, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in toRemoveCalls)
            {
                _callIdToChunkMap.Remove(k);
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
            _compressedMap.Remove(last.Value.Key);
            _compressedLru.RemoveLast();

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

    private sealed record CompressedEntry(ApiChunkKey Key, NoxApiCallChunk Chunk, int ByteSize);
    private sealed record DecodedEntry(ApiChunkKey Key, IReadOnlyList<NoxRawApiCallRecord> Records);
}
