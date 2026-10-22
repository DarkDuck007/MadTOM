using System;
using System.Collections.Generic;
using Google.Protobuf;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MadTOM.Services;
using Xunit;

namespace MadTOM.Tests.Plugins.NoxAI;

public class NoxApiCallChunkCacheTests
{
    private static NoxRawApiCallRecord CreateSampleRecord(int id)
    {
        return new NoxRawApiCallRecord
        {
            CallId = $"call_{id:D4}",
            Caller = "stream_chat",
            Model = "gemini-2.5-flash",
            TimestampUnixNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L + id,
            DurationMs = 120,
            PromptTokens = 850,
            CompletionTokens = 42,
            RawRequestJson = "{\"model\":\"gemini-2.5-flash\",\"system_instruction\":{\"parts\":[{\"text\":\"You are Nox, a smart, cheeky assistant with full access to MADTOM systems. Always keep responses concise, accurate, and witty.\"}]},\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"Hello assistant! How is server telemetry today?\"}]}]}",
            RawResponseJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"All systems nominal. Spool buffer active, zero dropped chunks.\"}],\"role\":\"model\"}}],\"usageMetadata\":{\"promptTokenCount\":850,\"candidatesTokenCount\":42,\"totalTokenCount\":892}}"
        };
    }

    [Fact]
    public void PutRecords_BatchesIntoBiggerChunks_AndAchievesHighCompressionRatio()
    {
        var cache = new NoxApiCallChunkCache(maxCacheBytes: 32 * 1024 * 1024, maxDecodedChunks: 8);

        // Feed 64 records
        var records = new List<NoxRawApiCallRecord>();
        for (int i = 0; i < 64; i++)
        {
            records.Add(CreateSampleRecord(i));
        }

        cache.PutRecords("test-node", records);

        // 64 records with TargetChunkRecords=32 should produce exactly 2 compressed chunks
        Assert.Equal(2, cache.CompressedChunkCount);
        Assert.Equal(64, cache.TotalRecordCount);

        // Verify high compression ratio (should be > 5x, typically 10x-25x on repeating JSON/prompts)
        Assert.True(cache.Ratio > 5.0, $"Expected high compression ratio (>5.0x), got {cache.Ratio:F2}x");
        Assert.True(cache.TotalCompressedBytes < cache.TotalRawBytes / 4);
    }

    [Fact]
    public void GetRecord_PartiallyDecodesOnTheFly()
    {
        var cache = new NoxApiCallChunkCache(maxCacheBytes: 32 * 1024 * 1024, maxDecodedChunks: 4);

        var records = new List<NoxRawApiCallRecord>();
        for (int i = 0; i < 35; i++)
        {
            records.Add(CreateSampleRecord(i));
        }

        cache.PutRecords("node-1", records);
        cache.FlushPending("node-1");

        // Should have 2 chunks (32 records in chunk 0, 3 records in chunk 1)
        Assert.Equal(2, cache.CompressedChunkCount);
        Assert.Equal(35, cache.TotalRecordCount);

        // On-the-fly lookup of record from chunk 0
        var call10 = cache.GetRecord("node-1", "call_0010");
        Assert.NotNull(call10);
        Assert.Equal("call_0010", call10.CallId);
        Assert.Contains("smart, cheeky assistant", call10.RawRequestJson);

        // On-the-fly lookup of record from chunk 1
        var call34 = cache.GetRecord("node-1", "call_0034");
        Assert.NotNull(call34);
        Assert.Equal("call_0034", call34.CallId);

        // Non-existent call returns null
        Assert.Null(cache.GetRecord("node-1", "nonexistent_call"));
    }

    [Fact]
    public void DecodedLRU_EvictsDecodedWhileRetainingCompressed()
    {
        // Max 2 decoded chunks in LRU
        var cache = new NoxApiCallChunkCache(maxCacheBytes: 32 * 1024 * 1024, maxDecodedChunks: 2);

        // Create 3 chunks of 32 records each (96 records total)
        for (int i = 0; i < 96; i++)
        {
            cache.PutRecords("node-2", new[] { CreateSampleRecord(i) });
        }

        Assert.Equal(3, cache.CompressedChunkCount);
        Assert.Equal(96, cache.TotalRecordCount);

        // Access records from chunk 0, 1, 2
        var r0 = cache.GetRecord("node-2", "call_0005");
        var r1 = cache.GetRecord("node-2", "call_0035");
        var r2 = cache.GetRecord("node-2", "call_0070");

        Assert.NotNull(r0);
        Assert.NotNull(r1);
        Assert.NotNull(r2);

        // Decoded chunk count should not exceed maxDecodedChunks (2)
        Assert.True(cache.DecodedChunkCount <= 2, $"Decoded chunks count was {cache.DecodedChunkCount}");

        // Compressed chunks remain all 3 intact
        Assert.Equal(3, cache.CompressedChunkCount);

        // We can still fetch from chunk 0 on the fly even if it was evicted from decoded LRU
        var r0Again = cache.GetRecord("node-2", "call_0005");
        Assert.NotNull(r0Again);
        Assert.Equal("call_0005", r0Again.CallId);
    }
}
