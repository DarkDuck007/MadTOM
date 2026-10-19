using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MADTOM.Plugins.NoxAI.Services;

public sealed record DiscoveredNoxNode(
    string NodeId,
    string Host,
    int Port,
    string Endpoint,
    string Service,
    DateTime LastSeen
);

/// <summary>
/// Scans local network and mDNS for active NoxChat probe daemons (_noxchat-probe._tcp.local).
/// Supports multicast mDNS queries (224.0.0.251:5353), subnet port sweeps (50055), and .local resolution.
/// </summary>
public sealed class NoxMdnsDiscoveryService : IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredNoxNode> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _udpQueryClient;
    private CancellationTokenSource? _scanCts;

    public event Action<DiscoveredNoxNode>? NodeDiscovered;

    public IReadOnlyCollection<DiscoveredNoxNode> DiscoveredNodes => _discovered.Values.OrderByDescending(n => n.LastSeen).ToList();

    public async Task<IReadOnlyCollection<DiscoveredNoxNode>> ScanAsync(CancellationToken cancellationToken = default)
    {
        _scanCts?.Cancel();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _scanCts.Token;

        try
        {
            _udpQueryClient?.Dispose();
            _udpQueryClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0))
            {
                EnableBroadcast = true
            };

            // Start listening for incoming mDNS unicast responses
            _ = ListenMdnsResponsesAsync(_udpQueryClient, token);

            // Send mDNS queries
            SendMdnsQuery();

            // Run direct probes (localhost, local subnets on probe port 50055)
            await ProbeLocalCandidatesAsync(token);

            // Wait brief moment for responses
            await Task.Delay(800, token);
        }
        catch (OperationCanceledException) { }
        catch { }

        return DiscoveredNodes;
    }

    private void SendMdnsQuery()
    {
        if (_udpQueryClient == null) return;

        try
        {
            byte[] stdQuery = BuildMdnsPtrQuery("_noxchat-probe._tcp.local", unicastResponse: false);
            byte[] quQuery = BuildMdnsPtrQuery("_noxchat-probe._tcp.local", unicastResponse: true);

            var multicastEp = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, 5353);

            _udpQueryClient.Send(stdQuery, stdQuery.Length, multicastEp);
            _udpQueryClient.Send(quQuery, quQuery.Length, multicastEp);
            _udpQueryClient.Send(stdQuery, stdQuery.Length, broadcastEp);

            // Broadcast on each active LAN interface
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        IsPrivateLanAddress(unicast.Address) &&
                        unicast.IPv4Mask != null)
                    {
                        var ipBytes = unicast.Address.GetAddressBytes();
                        var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }
                        try
                        {
                            _udpQueryClient.Send(stdQuery, stdQuery.Length, new IPEndPoint(new IPAddress(bcast), 5353));
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private async Task ListenMdnsResponsesAsync(UdpClient client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(token);
                ParseMdnsResponse(result.Buffer, result.RemoteEndPoint);
            }
        }
        catch { }
    }

    private void ParseMdnsResponse(byte[] buffer, IPEndPoint remoteEp)
    {
        try
        {
            string payload = Encoding.ASCII.GetString(buffer);
            if (!payload.Contains("noxchat", StringComparison.OrdinalIgnoreCase))
                return;

            string host = remoteEp.Address.ToString();
            int port = 50055;
            string nodeId = remoteEp.Address.ToString();

            // Extract TXT tags: node_id=..., service=noxchat-probe
            int idx = payload.IndexOf("node_id=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int end = payload.IndexOf('\0', idx);
                if (end < 0) end = payload.Length;
                string seg = payload.Substring(idx + 8, Math.Min(end - (idx + 8), 32));
                // filter printable
                string cleaned = new string(seg.TakeWhile(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    nodeId = cleaned;
                }
            }

            var node = new DiscoveredNoxNode(
                NodeId: nodeId,
                Host: host,
                Port: port,
                Endpoint: $"{host}:{port}",
                Service: "_noxchat-probe._tcp.local",
                LastSeen: DateTime.Now
            );

            RegisterNode(node);
        }
        catch { }
    }

    private async Task ProbeLocalCandidatesAsync(CancellationToken token)
    {
        var candidates = new List<string> { "127.0.0.1", "localhost" };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateLanAddress(unicast.Address))
                        continue;

                    candidates.Add(unicast.Address.ToString());

                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLanAddress(gw.Address))
                        {
                            candidates.Add(gw.Address.ToString());
                        }
                    }
                }
            }
        }
        catch { }

        var tasks = candidates.Distinct().Select(c => ProbeTcpAsync(c, 50055, token));
        await Task.WhenAll(tasks);
    }

    private async Task ProbeTcpAsync(string host, int port, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(400);

            await client.ConnectAsync(host, port, cts.Token);
            if (client.Connected)
            {
                string nodeId = host.Equals("127.0.0.1") || host.Equals("localhost") ? "Local-NoxChat" : $"Nox-{host.Replace(".", "-")}";
                var node = new DiscoveredNoxNode(
                    NodeId: nodeId,
                    Host: host,
                    Port: port,
                    Endpoint: $"{host}:{port}",
                    Service: "tcp-probe",
                    LastSeen: DateTime.Now
                );
                RegisterNode(node);
            }
        }
        catch { }
    }

    private void RegisterNode(DiscoveredNoxNode node)
    {
        _discovered[node.Endpoint] = node;
        NodeDiscovered?.Invoke(node);
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static byte[] BuildMdnsPtrQuery(string serviceName, bool unicastResponse)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0); // ID
        writer.Write((ushort)0); // Flags
        writer.Write((byte)0); writer.Write((byte)1); // Questions: 1
        writer.Write((ushort)0); // Answers
        writer.Write((ushort)0); // Authority
        writer.Write((ushort)0); // Additional

        foreach (var label in serviceName.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0); // Terminate QNAME

        // Type: PTR (12)
        writer.Write((byte)0); writer.Write((byte)12);

        // Class: IN (1), QU bit if unicastResponse requested
        ushort qClass = (ushort)(unicastResponse ? 0x8001 : 0x0001);
        writer.Write((byte)(qClass >> 8));
        writer.Write((byte)(qClass & 0xFF));

        return ms.ToArray();
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _udpQueryClient?.Dispose();
    }
}
