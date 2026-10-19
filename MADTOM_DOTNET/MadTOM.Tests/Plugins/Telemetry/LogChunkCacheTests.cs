using System;
using System.Collections.Generic;
using Google.Protobuf;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;
using Xunit;
using ZstdSharp;

namespace MadTOM.Tests.Plugins.Telemetry;

public class LogChunkCacheTests
{
    private LogChunk CreateTestChunk(ulong chunkId, ulong firstSeq, int count)
    {
        var list = new LogRecordList();
        for (int i = 0; i < count; i++)
        {
            list.Records.Add(new LogRecord
            {
                Seq = firstSeq + (ulong)i,
                TimestampUnixNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                Priority = 6,
                Unit = "systemd.service",
                Message = $"Test log line #{firstSeq + (ulong)i}"
            });
        }

        byte[] raw = list.ToByteArray();
        using var compressor = new Compressor(1);
        byte[] compressed = compressor.Wrap(raw).ToArray();

        return new LogChunk
        {
            NodeId = "node-a",
            ChunkId = chunkId,
            FirstSeq = firstSeq,
            LastSeq = firstSeq + (ulong)count - 1,
            RecordCount = (uint)count,
            FirstTsUnixNano = list.Records[0].TimestampUnixNano,
            LastTsUnixNano = list.Records[^1].TimestampUnixNano,
            RawSize = (uint)raw.Length,
            ZstdRecords = ByteString.CopyFrom(compressed),
            Sealed = true
        };
    }

    [Fact]
    public void PutChunk_And_TryGetChunk_RetrievesRawChunk()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        var chunk = CreateTestChunk(1, 100, 256);

        cache.PutChunk("ep1", "node-a", chunk);

        Assert.True(cache.TryGetChunk("ep1", "node-a", 1, out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal((ulong)1, retrieved.ChunkId);
        Assert.Equal((ulong)100, retrieved.FirstSeq);
        Assert.Equal((ulong)355, retrieved.LastSeq);
        Assert.Equal(1, cache.CompressedChunkCount);
    }

    [Fact]
    public void GetDecodedChunk_DecodesCompressedChunk()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        var chunk = CreateTestChunk(0, 0, 10);

        cache.PutChunk("ep1", "node-a", chunk);

        var decoded = cache.GetDecodedChunk("ep1", "node-a", 0);
        Assert.NotNull(decoded);
        Assert.Equal(10, decoded.Count);
        Assert.Equal("Test log line #0", decoded[0].Message);
        Assert.Equal("Test log line #9", decoded[9].Message);
        Assert.Equal(1, cache.DecodedChunkCount);
    }

    [Fact]
    public void GetRecord_AccessesByMonotonicSeq()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        var chunk0 = CreateTestChunk(0, 0, 256);
        var chunk1 = CreateTestChunk(1, 256, 256);

        cache.PutChunk("ep1", "node-a", chunk0);
        cache.PutChunk("ep1", "node-a", chunk1);

        var r5 = cache.GetRecord("ep1", "node-a", 5);
        Assert.NotNull(r5);
        Assert.Equal((ulong)5, r5.Seq);
        Assert.Equal("Test log line #5", r5.Message);

        var r300 = cache.GetRecord("ep1", "node-a", 300);
        Assert.NotNull(r300);
        Assert.Equal((ulong)300, r300.Seq);
        Assert.Equal("Test log line #300", r300.Message);

        Assert.Null(cache.GetRecord("ep1", "node-a", 9999));
    }

    [Fact]
    public void Eviction_RespectsByteQuota()
    {
        // 16MB minimum limit in LogChunkCache
        var cache = new LogChunkCache(maxCacheBytes: 16 * 1024 * 1024, maxDecodedChunks: 2);
        
        // Insert chunks and verify they stay within bounds
        for (ulong i = 0; i < 50; i++)
        {
            var chunk = CreateTestChunk(i, i * 256, 256);
            cache.PutChunk("ep1", "node-a", chunk);
        }

        Assert.True(cache.TotalCompressedBytes <= cache.MaxCacheBytes);
    }

    [Fact]
    public void Clear_EvictsAllOrSpecificNode()
    {
        var cache = new LogChunkCache();
        var chunkA = CreateTestChunk(0, 0, 10);
        var chunkB = CreateTestChunk(0, 0, 10);

        cache.PutChunk("ep1", "node-a", chunkA);
        cache.PutChunk("ep1", "node-b", chunkB);

        Assert.Equal(2, cache.CompressedChunkCount);

        cache.Clear("node-a");
        Assert.False(cache.TryGetChunk("ep1", "node-a", 0, out _));
        Assert.True(cache.TryGetChunk("ep1", "node-b", 0, out _));
        Assert.Equal(1, cache.CompressedChunkCount);

        cache.Clear();
        Assert.Equal(0, cache.CompressedChunkCount);
        Assert.Equal(0, cache.TotalCompressedBytes);
        Assert.Equal(0, cache.TotalRawBytes);
        Assert.Equal(0, cache.TotalRecordCount);
    }

    [Fact]
    public void GetRecord_WithNonAlignedSequences_FindsRecordViaRangeIndex()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        // Chunk 1 has sequences 100 to 355 (crosses 256 boundary)
        var chunk = CreateTestChunk(1, 100, 256);
        cache.PutChunk("ep1", "node-a", chunk);

        // Seq 200 -> heuristic 200 / 256 = 0, but chunkId is 1! Range index must find it.
        var r200 = cache.GetRecord("ep1", "node-a", 200);
        Assert.NotNull(r200);
        Assert.Equal((ulong)200, r200.Seq);
        Assert.Equal("Test log line #200", r200.Message);

        var r100 = cache.GetRecord("ep1", "node-a", 100);
        Assert.NotNull(r100);
        Assert.Equal((ulong)100, r100.Seq);

        var r355 = cache.GetRecord("ep1", "node-a", 355);
        Assert.NotNull(r355);
        Assert.Equal((ulong)355, r355.Seq);

        Assert.Null(cache.GetRecord("ep1", "node-a", 99));
        Assert.Null(cache.GetRecord("ep1", "node-a", 356));
    }

    [Fact]
    public void Accounting_AccuratelyTracksRawAndCompressedBytes()
    {
        var cache = new LogChunkCache();
        var chunk = CreateTestChunk(1, 100, 256);
        cache.PutChunk("ep1", "node-a", chunk);

        Assert.True(cache.TotalCompressedBytes > 0);
        Assert.True(cache.TotalRawBytes > cache.TotalCompressedBytes);
        Assert.Equal(256, cache.TotalRecordCount);
        Assert.True(cache.StorageBytes >= cache.TotalCompressedBytes);

        cache.Clear();
        Assert.Equal(0, cache.TotalCompressedBytes);
        Assert.Equal(0, cache.TotalRawBytes);
        Assert.Equal(0, cache.TotalRecordCount);
    }

    [Fact]
    public void TryGetNodeSequenceRange_ReturnsCorrectBounds_AndTracksMultipleChunks()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        Assert.False(cache.TryGetNodeSequenceRange("node-a", out _, out _));

        var chunk0 = CreateTestChunk(0, 40000, 256); // 40000..40255
        cache.PutChunk("ep1", "node-a", chunk0);

        Assert.True(cache.TryGetNodeSequenceRange("node-a", out var min0, out var max0));
        Assert.Equal((ulong)40000, min0);
        Assert.Equal((ulong)40255, max0);

        var chunk1 = CreateTestChunk(1, 40256, 100); // 40256..40355
        cache.PutChunk("ep1", "node-a", chunk1);

        Assert.True(cache.TryGetNodeSequenceRange("node-a", out var min1, out var max1));
        Assert.Equal((ulong)40000, min1);
        Assert.Equal((ulong)40355, max1);

        cache.Clear("node-a");
        Assert.False(cache.TryGetNodeSequenceRange("node-a", out _, out _));
    }

    [Fact]
    public void PutChunk_WhenUpdatedOrSealed_InvalidatesDecodedCacheAndLoadsNewRecords()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);

        // 1. Partial chunk with 5 records (unsealed)
        var partialChunk = CreateTestChunk(0, 0, 5);
        partialChunk.Sealed = false;
        cache.PutChunk("ep1", "node-a", partialChunk);

        // Read and decode chunk
        var r0 = cache.GetRecord("ep1", "node-a", 0);
        Assert.NotNull(r0);
        Assert.Equal((ulong)0, r0.Seq);

        // Sequence 10 should not exist yet
        Assert.Null(cache.GetRecord("ep1", "node-a", 10));

        // 2. Chunk receives updates and is sealed with 15 records
        var sealedChunk = CreateTestChunk(0, 0, 15);
        sealedChunk.Sealed = true;
        cache.PutChunk("ep1", "node-a", sealedChunk);

        // 3. Sequence 10 must now be loaded without ghost cache getting stuck
        var r10 = cache.GetRecord("ep1", "node-a", 10);
        Assert.NotNull(r10);
        Assert.Equal((ulong)10, r10.Seq);
        Assert.Equal("Test log line #10", r10.Message);
    }

    [Fact]
    public void TryGetChunkIdForSeq_ResolvesCorrectChunkId()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);
        var chunk = CreateTestChunk(5, 500, 256);
        cache.PutChunk("ep1", "node-a", chunk);

        Assert.True(cache.TryGetChunkIdForSeq("node-a", 550, out ulong chunkId));
        Assert.Equal((ulong)5, chunkId);
    }

    [Fact]
    public void PutChunk_DoesNotOverwriteWithStaleChunk_WhenExistingChunkIsNewerOrHasMoreRecords()
    {
        var cache = new LogChunkCache(maxCacheBytes: 10 * 1024 * 1024, maxDecodedChunks: 10);

        // Put newer chunk with 10 records (seq 0..9)
        var newerChunk = CreateTestChunk(0, 0, 10);
        cache.PutChunk("ep1", "node-a", newerChunk);

        Assert.Equal((ulong)9, cache.GetRecord("ep1", "node-a", 9)?.Seq);

        // Attempt to overwrite with older snapshot from query (seq 0..4)
        var olderChunk = CreateTestChunk(0, 0, 5);
        cache.PutChunk("ep1", "node-a", olderChunk);

        // Record 9 must still exist!
        Assert.NotNull(cache.GetRecord("ep1", "node-a", 9));
        Assert.True(cache.TryGetChunk("ep1", "node-a", 0, out var current));
        Assert.Equal(10u, current!.RecordCount);
    }

    [Fact]
    public void PerNodeStats_TracksNodeBytesAndRecordsCorrectly()
    {
        var cache = new LogChunkCache();
        var chunkA = CreateTestChunk(0, 0, 20);
        var chunkB = CreateTestChunk(0, 0, 50);

        cache.PutChunk("ep1", "node-a", chunkA);
        cache.PutChunk("ep1", "node-b", chunkB);

        Assert.Equal(20, cache.GetNodeRecordCount("node-a"));
        Assert.Equal(50, cache.GetNodeRecordCount("node-b"));
        Assert.True(cache.GetNodeCompressedBytes("node-a") > 0);
        Assert.True(cache.GetNodeCompressedBytes("node-b") > cache.GetNodeCompressedBytes("node-a"));

        cache.Clear("node-a");
        Assert.Equal(0, cache.GetNodeRecordCount("node-a"));
        Assert.Equal(0, cache.GetNodeCompressedBytes("node-a"));
        Assert.Equal(50, cache.GetNodeRecordCount("node-b"));
    }

    [Fact]
    public void RecordChunkError_And_GetRecord_ReturnsSyntheticErrorRecord()
    {
        var cache = new LogChunkCache();

        cache.RecordChunkError("node-tp1", 5, "NOT_FOUND", "Chunk #5 not found in collector TSDB", "at TestStack()");

        Assert.True(cache.TryGetChunkError("node-tp1", 5, out var err));
        Assert.NotNull(err);
        Assert.Equal("NOT_FOUND", err.ErrorType);
        Assert.Contains("Chunk #5 not found", err.Message);

        // Sequence in chunk 5 (5 * 256 = 1280)
        var rec = cache.GetRecord("ep1", "node-tp1", 1280);
        Assert.NotNull(rec);
        Assert.Equal((ulong)1280, rec.Seq);
        Assert.Equal(1u, rec.Priority); // Critical / Red
        Assert.Equal("[ERROR:NOT_FOUND]", rec.Unit);
        Assert.Contains("Chunk #5 not found", rec.Message);
        Assert.Contains("TestStack()", rec.Message);

        // Putting the chunk clears the error
        var chunk = CreateTestChunk(5, 1280, 256);
        cache.PutChunk("ep1", "node-tp1", chunk);

        Assert.False(cache.TryGetChunkError("node-tp1", 5, out _));
        var validRec = cache.GetRecord("ep1", "node-tp1", 1280);
        Assert.NotNull(validRec);
        Assert.Equal("systemd.service", validRec.Unit);
    }
}
