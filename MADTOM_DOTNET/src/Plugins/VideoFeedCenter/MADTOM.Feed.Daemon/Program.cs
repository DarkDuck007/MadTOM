using System;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.Feed.Engine;

namespace MADTOM.Feed.Daemon;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("MADTOM Video Feed Center Engine Daemon (madtom-feedd) starting...");

        int tcpPort = 7443;
        if (args.Length > 0 && int.TryParse(args[0], out var customPort))
        {
            tcpPort = customPort;
        }

        await using var engine = new FeedEngineHost();
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Shutdown requested via SIGINT/Ctrl+C...");
        };

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            cts.Cancel();
        };

        try
        {
            await engine.StartAsync(tcpPort, cts.Token);
            Console.WriteLine($"madtom-feedd running. Local IPC published, mTLS listening on port {tcpPort}.");
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.WriteLine("Stopping madtom-feedd...");
            await engine.StopAsync();
            Console.WriteLine("madtom-feedd stopped cleanly.");
        }
    }
}
