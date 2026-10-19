using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Google.Protobuf;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.Services;

public readonly record struct ApiChunkKey(string Node, ulong ChunkId);

/// <summary>
/// Two-tier LRU cache for NoxChat Raw LLM API Call chunks.
/// Tier 1: Retains zstd-compressed NoxApiCallChunk payloads up to MaxCacheBytes.
/// Tier 2: Retains a pool of decompressed NoxApiCallRecord lists for hot UI viewing.
/// </summary>
public sealed class NoxApiCallChunkCache
{
    private readonly object _lock = new();

    private readonly Dictionary<ApiChunkKey, LinkedListNode<CompressedEntry>> _compressedMap = new();
    private readonly LinkedList<CompressedEntry> _compressedLru = new();

    private readonly Dictionary<ApiChunkKey, LinkedListNode<DecodedEntry>> _decodedMap = new();
    private readonly LinkedList<DecodedEntry> _decodedLru = new();

    private long _totalCompressedBytes;
    private long _totalRawBytes;
    private long _totalRecordCount;
    private long _maxCacheBytes;
    private int _maxDecodedChunks;

    public long TotalCompressedBytes { get { lock (_lock) return _totalCompressedBytes; } }
    public long TotalRawBytes { get { lock (_lock) return _totalRawBytes; } }
    public long StorageBytes { get { lock (_lock) return _totalCompressedBytes + (_compressedMap.Count * 64L) + (_decodedMap.Count * 256L); } }
    public double Ratio { get { lock (_lock) return _totalCompressedBytes > 0 ? (double)_totalRawBytes / _totalCompressedBytes : 1.0; } }
    public long TotalRecordCount { get { lock (_lock) return _totalRecordCount; } }
    public int CompressedChunkCount { get { lock (_lock) return _compressedMap.Count; } }
    public int DecodedChunkCount { get { lock (_lock) return _decodedMap.Count; } }

    public NoxApiCallChunkCache(long maxCacheBytes = 64L * 1024 * 1024, int maxDecodedChunks = 16)
    {
        _maxCacheBytes = Math.Max(8 * 1024 * 1024, maxCacheBytes);
        _maxDecodedChunks = Math.Max(4, maxDecodedChunks);
    }

    public void Configure(long maxCacheBytes, int maxDecodedChunks)
    {
        lock (_lock)
        {
            _maxCacheBytes = Math.Max(8 * 1024 * 1024, maxCacheBytes);
            _maxDecodedChunks = Math.Max(4, maxDecodedChunks);
            EvictCompressedIfNeeded();
            EvictDecodedIfNeeded();
        }
    }

    public void PutChunk(string node, NoxApiCallChunk chunk)
    {
        if (chunk == null || string.IsNullOrEmpty(node)) return;
        var key = new ApiChunkKey(node, chunk.ChunkId);
        int bytes = chunk.ZstdRecords?.Length ?? 0;

        lock (_lock)
        {
            if (_compressedMap.TryGetValue(key, out var existing))
            {
                _totalCompressedBytes -= existing.Value.ByteSize;
                _totalRawBytes -= existing.Value.Chunk.RawSize;
                _totalRecordCount -= existing.Value.Chunk.RecordCount;
                _compressedLru.Remove(existing);
                _compressedMap.Remove(key);
            }

            var entry = new CompressedEntry(key, chunk, bytes);
            var nodeRef = _compressedLru.AddFirst(entry);
            _compressedMap[key] = nodeRef;
            _totalCompressedBytes += bytes;
            _totalRawBytes += chunk.RawSize;
            _totalRecordCount += chunk.RecordCount;

            // Invalidate decoded representation on update
            if (_decodedMap.TryGetValue(key, out var decNode))
            {
                _decodedLru.Remove(decNode);
                _decodedMap.Remove(key);
            }

            EvictCompressedIfNeeded();
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

    public void Clear()
    {
        lock (_lock)
        {
            _compressedMap.Clear();
            _compressedLru.Clear();
            _decodedMap.Clear();
            _decodedLru.Clear();
            _totalCompressedBytes = 0;
            _totalRawBytes = 0;
            _totalRecordCount = 0;
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
