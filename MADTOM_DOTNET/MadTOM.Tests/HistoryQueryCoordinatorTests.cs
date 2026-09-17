using MadTOM.Services;

namespace MadTOM.Tests;

public class HistoryQueryCoordinatorTests
{
    private static HistoryQueryCoordinator.Key Key(string node, string endpoint = "collector") => new(endpoint, node, "cpu.total", 1, 2, 750);

    [Fact]
    public async Task SharedRequestSurvivesOneReaderCancellation()
    {
        using var coordinator = new HistoryQueryCoordinator();
        var release = new TaskCompletionSource<IReadOnlyList<LODPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken transportToken = default;
        Task<IReadOnlyList<LODPoint>> Fetch(CancellationToken ct) { calls++; transportToken = ct; return release.Task.WaitAsync(ct); }
        using var cancelled = new CancellationTokenSource();
        var first = coordinator.QueryAsync(Key("node"), Fetch, cancelled.Token);
        var second = coordinator.QueryAsync(Key("node"), Fetch);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(transportToken.IsCancellationRequested);
        release.SetResult(new[] { new LODPoint(1, 2, 2, 2) });
        Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LastReaderCancellationCancelsTransportAndAllowsRetry()
    {
        using var coordinator = new HistoryQueryCoordinator();
        using var cancelled = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<LODPoint>> Fetch(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { stopped.TrySetResult(); }
            return Array.Empty<LODPoint>();
        }
        var first = coordinator.QueryAsync(Key("node"), Fetch, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await coordinator.QueryAsync(Key("node"), _ => Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>())));
    }

    [Fact]
    public async Task ConcurrencyIsBoundedAndCancelledQueuedQueriesNeverStart()
    {
        using var coordinator = new HistoryQueryCoordinator(3, 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, peak = 0;
        async Task<IReadOnlyList<LODPoint>> Fetch(CancellationToken ct)
        {
            int count = Interlocked.Increment(ref active);
            int prior;
            do { prior = peak; } while (count > prior && Interlocked.CompareExchange(ref peak, count, prior) != prior);
            if (count == 3) full.TrySetResult();
            try { await release.Task.WaitAsync(ct); return Array.Empty<LODPoint>(); }
            finally { Interlocked.Decrement(ref active); }
        }
        var tasks = Enumerable.Range(0, 8).Select(i => coordinator.QueryAsync(Key(i.ToString(), i < 4 ? "a" : "b"), Fetch)).ToArray();
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, active);
        using var cancelled = new CancellationTokenSource();
        bool started = false;
        var queued = coordinator.QueryAsync(Key("cancelled", "a"), _ => { started = true; return Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>()); }, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(started);
        Assert.InRange(peak, 1, 3);
    }
}
