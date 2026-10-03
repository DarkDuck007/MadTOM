using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MADTOM.Console.Hosting;

/// <summary>
/// Coordinates idempotent, asynchronous shutdown across desktop and direct-display (DRM) execution.
/// Awaits plugin and host disposal before terminating the process.
/// </summary>
public static class AppShutdownCoordinator
{
    private static int _isShuttingDown;
    private static Func<Task>? _asyncCleanup;
    private static Action? _lifetimeExit;
    private static IDisposable? _sigtermRegistration;
    private static IDisposable? _sigintRegistration;

    public static void Initialize(Func<Task> asyncCleanup, Action lifetimeExit)
    {
        _asyncCleanup = asyncCleanup;
        _lifetimeExit = lifetimeExit;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            ShutdownAsync().GetAwaiter().GetResult();
        };

        global::System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _ = ShutdownAsync();
        };

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                _sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    _ = ShutdownAsync();
                });

                _sigintRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    ctx.Cancel = true;
                    _ = ShutdownAsync();
                });
            }
            catch
            {
                // PosixSignalRegistration may fail if unsupported in certain virtualization or test containers
            }
        }
    }

    public static async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
        {
            return;
        }

        try
        {
            if (_asyncCleanup != null)
            {
                await _asyncCleanup().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"[AppShutdownCoordinator] Error during shutdown cleanup: {ex.Message}");
        }
        finally
        {
            _sigtermRegistration?.Dispose();
            _sigintRegistration?.Dispose();

            if (_lifetimeExit != null)
            {
                _lifetimeExit();
            }
            else
            {
                Environment.Exit(0);
            }
        }
    }
}
