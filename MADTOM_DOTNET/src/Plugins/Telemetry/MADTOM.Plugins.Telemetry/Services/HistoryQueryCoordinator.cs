using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MadTOM.Services;

/// <summary>Shares identical in-flight requests and bounds active history RPCs.</summary>
public sealed class HistoryQueryCoordinator : IDisposable
{
    public readonly record struct Key(string Endpoint, string Node, string Metric, long StartTicks, long EndTicks, int Points);
    private sealed class Request
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<IReadOnlyList<LODPoint>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Readers;
    }
    private readonly object _gate = new();
    private readonly SemaphoreSlim _global;
    private readonly int _perCollector;
    private readonly Dictionary<string, SemaphoreSlim> _collectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Key, Request> _requests = new();
    private bool _disposed;

    public HistoryQueryCoordinator(int globalLimit = 8, int perCollectorLimit = 4)
    {
        if (globalLimit < 1 || perCollectorLimit < 1) throw new ArgumentOutOfRangeException(nameof(globalLimit));
        _global = new(globalLimit, globalLimit);
        _perCollector = perCollectorLimit;
    }

    public async Task<IReadOnlyList<LODPoint>> QueryAsync(Key key,
        Func<CancellationToken, Task<IReadOnlyList<LODPoint>>> fetch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        key = key with { Endpoint = key.Endpoint.Trim().ToLowerInvariant(), Metric = key.Metric.ToLowerInvariant() };
        Request request;
        SemaphoreSlim slots;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_collectors.TryGetValue(key.Endpoint, out slots!))
                _collectors[key.Endpoint] = slots = new(_perCollector, _perCollector);
            if (!_requests.TryGetValue(key, out request!))
            {
                _requests[key] = request = new();
                start = true;
            }
            request.Readers++;
        }
        if (start) _ = RunAsync(key, request, slots, fetch);
        try { return await request.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                request.Readers--;
                if (request.Readers == 0 && !request.Completion.Task.IsCompleted)
                {
                    if (_requests.TryGetValue(key, out var active) && ReferenceEquals(active, request)) _requests.Remove(key);
                    request.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task RunAsync(Key key, Request request, SemaphoreSlim slots,
        Func<CancellationToken, Task<IReadOnlyList<LODPoint>>> fetch)
    {
        bool localHeld = false, globalHeld = false;
        try
        {
            var ct = request.Cancellation.Token;
            await slots.WaitAsync(ct).ConfigureAwait(false); localHeld = true;
            await _global.WaitAsync(ct).ConfigureAwait(false); globalHeld = true;
            request.Completion.TrySetResult(await fetch(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { request.Completion.TrySetCanceled(); }
        catch (Exception ex) { request.Completion.TrySetException(ex); }
        finally
        {
            if (globalHeld) _global.Release();
            if (localHeld) slots.Release();
            lock (_gate)
            {
                if (_requests.TryGetValue(key, out var active) && ReferenceEquals(active, request)) _requests.Remove(key);
                request.Cancellation.Dispose();
            }
            // Observe faults even if every reader cancelled its wait.
            _ = request.Completion.Task.Exception;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var request in new List<Request>(_requests.Values)) request.Cancellation.Cancel();
        }
    }
}
