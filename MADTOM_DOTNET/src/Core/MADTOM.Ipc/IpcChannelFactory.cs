using System;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using MADTOM.Protocol.Core.V1;

namespace MADTOM.Ipc;

public static class IpcChannelFactory
{
    public static GrpcChannel CreateChannel(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.Equals(endpoint.Transport, "uds", StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnixDomainSocketChannel(endpoint.Address);
        }

        if (string.Equals(endpoint.Transport, "pipe", StringComparison.OrdinalIgnoreCase))
        {
            return CreateNamedPipeChannel(endpoint.Address);
        }

        // Default TCP channel
        var targetUri = endpoint.Address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.Address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Address
            : $"http://{endpoint.Address}";

        return GrpcChannel.ForAddress(targetUri);
    }

    public static GrpcChannel CreateUnixDomainSocketChannel(string socketPath)
    {
        var udsEndPoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(udsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
    }

    public static GrpcChannel CreateNamedPipeChannel(string pipePath)
    {
        var pipeName = pipePath;
        if (pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            pipeName = pipeName.Substring(@"\\.\pipe\".Length);
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(ct);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            }
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
    }

    public static GrpcChannel CreatePlatformLocalChannel(string processId)
    {
        var path = IpcPaths.GetSocketPath(processId);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateNamedPipeChannel(path);
        }
        return CreateUnixDomainSocketChannel(path);
    }
}
