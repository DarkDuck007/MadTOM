using System;
using System.Threading;
using System.Threading.Tasks;

namespace MADTOM.PluginContracts;

/// <summary>
/// Defines the explicit in-process lifecycle contract for a MADTOM plugin module.
/// Follows the localized lifecycle controller pattern to guarantee clean teardown and GC collection.
/// </summary>
public interface IPluginLifecycle : IAsyncDisposable
{
    /// <summary>
    /// Signals the plugin to start its background workers, telemetry feeds, and timers.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals the plugin to cancel background workers, flush buffers, and close connections.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the plugin whether it can safely close (e.g. check for active tasks, pending actions).
    /// </summary>
    Task<bool> CanCloseAsync() => Task.FromResult(true);
}

