using System.Reflection;
using MadTOM.Controls.Logs;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Tests.Plugins.Telemetry;

public class VirtualLogPrefetchTests
{
    // Exercise the renderer's actual prefetch enumeration without a window/font backend.
    private static ulong[] Prefetch(LogChunkCache cache, ulong first, ulong last, int rows)
    {
        var method = typeof(VirtualLogViewControl).GetMethod("GetVisibleChunkIds", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((IEnumerable<ulong>)method.Invoke(null, [cache, "node", first, last, rows])!).ToArray();
    }

    [Fact]
    public void SparseChunkIds_RequestOnlyChunksInTheViewport()
    {
        var cache = new LogChunkCache();
        cache.RegisterRange("node", new LogChunk { ChunkId = 7, FirstSeq = 1, LastSeq = 10 });
        cache.RegisterRange("node", new LogChunk { ChunkId = ulong.MaxValue, FirstSeq = 11, LastSeq = 30 });

        // Numeric enumeration between these IDs would hang the UI and overflow.
        Assert.Equal(new ulong[] { 7, ulong.MaxValue }, Prefetch(cache, 1, 30, 20));
    }

    [Fact]
    public void UnmappedRows_UseBoundedFallbackAlongsideExactRanges()
    {
        var cache = new LogChunkCache();
        cache.RegisterRange("node", new LogChunk { ChunkId = 1_000_000_000, FirstSeq = 11, LastSeq = 20 });

        Assert.Equal(new ulong[] { 0, 1_000_000_000 }, Prefetch(cache, 1, 20, 19));
    }

    [Fact]
    public void MaximumSequence_DoesNotWrapIntoEarlierRows()
    {
        var cache = new LogChunkCache();
        cache.RegisterRange("node", new LogChunk { ChunkId = 42, FirstSeq = ulong.MaxValue - 1, LastSeq = ulong.MaxValue });

        Assert.Equal(new ulong[] { 42 }, Prefetch(cache, ulong.MaxValue - 1, ulong.MaxValue, 50));
        Assert.Empty(Prefetch(cache, 20, 10, 50));
    }
    [Fact]
    public void RangeReplacementAndClearKeepChunkIndexConsistent()
    {
        var cache = new LogChunkCache();
        cache.RegisterRange("node", new LogChunk { ChunkId = 7, FirstSeq = 1, LastSeq = 10 });
        cache.RegisterRange("node", new LogChunk { ChunkId = 8, FirstSeq = 11, LastSeq = 20 });
        cache.RegisterRange("node", new LogChunk { ChunkId = 7, FirstSeq = 21, LastSeq = 30 });
        cache.RegisterRange("node", new LogChunk { ChunkId = 7, FirstSeq = 21, LastSeq = 30 });
        Assert.Equal(new ulong[] { 8, 7 }, Prefetch(cache, 11, 30, 19));
        cache.Clear("node");
        cache.RegisterRange("node", new LogChunk { ChunkId = 42, FirstSeq = 1, LastSeq = 10 });
        Assert.Equal(new ulong[] { 42 }, Prefetch(cache, 1, 10, 9));
        cache.Clear();
        cache.RegisterRange("node", new LogChunk { ChunkId = 43, FirstSeq = 1, LastSeq = 10 });
        Assert.Equal(new ulong[] { 43 }, Prefetch(cache, 1, 10, 9));
    }

}
