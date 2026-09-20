using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SQUEEZE.Services;

public class MdnsServerDiscoveryService : IServerDiscoveryService, IDisposable
{
    private readonly ConcurrentDictionary<string, DiscoveredServer> _servers = new();
    private CancellationTokenSource? _discoveryCts;
    private UdpClient? _queryClient;
    private UdpClient? _multicastClient;
    private readonly HttpClient _httpProbeClient = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    public event EventHandler<DiscoveredServer>? ServerDiscovered;

    public IReadOnlyList<DiscoveredServer> DiscoveredServers => _servers.Values.ToList();

    public Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        _discoveryCts?.Cancel();
        _discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        InitSocketsAndListeners(_discoveryCts.Token);

        return ScanNetworkAsync(_discoveryCts.Token);
    }

    public Task StopDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        CloseSockets();
        return Task.CompletedTask;
    }

    private void InitSocketsAndListeners(CancellationToken token)
    {
        CloseSockets();

        // 1. Ephemeral query client: sends queries and receives direct unicast mDNS responses.
        // Works on all platforms (including Android without port 5353 bind permissions).
        try
        {
            _queryClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _queryClient.EnableBroadcast = true;
            _ = ListenSocketAsync(_queryClient, token);
        }
        catch
        {
            // Ignore socket creation failure
        }

        // 2. Multicast listener on 5353: attempts to join mDNS group 224.0.0.251 if port is available.
        try
        {
            var mc = new UdpClient();
            mc.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            mc.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            mc.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));
            _multicastClient = mc;
            _ = ListenSocketAsync(_multicastClient, token);
        }
        catch
        {
            // Port 5353 is often bound exclusively by system mDNS (e.g. on Android netd or Avahi).
            // Unicast reception via _queryClient ensures discovery still succeeds.
        }
    }

    private void CloseSockets()
    {
        try { _queryClient?.Dispose(); } catch { }
        _queryClient = null;

        try { _multicastClient?.Dispose(); } catch { }
        _multicastClient = null;
    }

    public async Task ScanNetworkAsync(CancellationToken cancellationToken = default)
    {
        if (_queryClient == null)
        {
            InitSocketsAndListeners(cancellationToken);
        }

        // 1. Send mDNS queries (multicast + broadcast, standard + unicast-preferred)
        SendMdnsQuery();

        // 2. Direct probe of localhost
        var directProbes = new List<Task>
        {
            ProbeHttpEndpointAsync("127.0.0.1", 8080, cancellationToken),
            ProbeHttpEndpointAsync("localhost", 8080, cancellationToken)
        };

        // 3. Subnet discovery: probe active LAN network interfaces and gateways
        var subnetTasks = ProbeLocalSubnetsAsync(cancellationToken);

        try
        {
            await Task.WhenAll(Task.WhenAll(directProbes), subnetTasks);
        }
        catch
        {
            // Ignore scan task cancellations or individual errors
        }
    }

    public static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        return false;
    }

    private void SendMdnsQuery()
    {
        if (_queryClient == null) return;

        try
        {
            byte[] stdQuery = BuildMdnsPtrQuery("_squeeze._tcp.local", unicastResponse: false);
            byte[] quQuery = BuildMdnsPtrQuery("_squeeze._tcp.local", unicastResponse: true);

            var multicastEp = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, 5353);

            _queryClient.Send(stdQuery, stdQuery.Length, multicastEp);
            _queryClient.Send(quQuery, quQuery.Length, multicastEp);
            _queryClient.Send(stdQuery, stdQuery.Length, broadcastEp);

            // Also broadcast directly to subnet broadcast addresses (vital for mobile hotspots that drop multicast)
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
                        var broadcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }
                        var directedBroadcastEp = new IPEndPoint(new IPAddress(broadcastBytes), 5353);
                        try
                        {
                            _queryClient.Send(stdQuery, stdQuery.Length, directedBroadcastEp);
                        }
                        catch { }
                    }
                }
            }
        }
        catch
        {
            // Best-effort send
        }
    }

    private async Task ListenSocketAsync(UdpClient client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(token);
                ParseMdnsResponse(result.Buffer, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch
        {
            // Socket closed or error
        }
    }

    private async Task ProbeLocalSubnetsAsync(CancellationToken token)
    {
        var targetIps = new List<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var ipProps = ni.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var ip = unicast.Address;
                    var mask = unicast.IPv4Mask;
                    // STRICT FILTER: Only probe subnets on genuine RFC 1918 private LANs (avoids carrier CLAT 192.0.0.x)
                    if (!IsPrivateLanAddress(ip) || mask == null) continue;

                    // Add gateway addresses for instant discovery if gateway is on a private LAN
                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLanAddress(gw.Address))
                        {
                            targetIps.Add(gw.Address.ToString());
                        }
                    }

                    // Sweep the local /24 subnet around this device
                    var ipBytes = ip.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    if (maskBytes[0] == 255 && maskBytes[1] == 255)
                    {
                        for (int i = 1; i <= 254; i++)
                        {
                            if (i == ipBytes[3]) continue; // Skip local device IP
                            targetIps.Add($"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{i}");
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully if interface enumeration is restricted
        }

        var distinctIps = targetIps.Distinct().ToList();
        if (distinctIps.Count == 0) return;

        // Bounded parallel sweep with maximum 32 concurrent sockets
        using var throttle = new SemaphoreSlim(32, 32);
        var tasks = distinctIps.Select(async ip =>
        {
            await throttle.WaitAsync(token);
            try
            {
                if (!token.IsCancellationRequested)
                {
                    await ProbeHttpEndpointAsync(ip, 8080, token);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task ProbeHttpEndpointAsync(string host, int port, CancellationToken token)
    {
        try
        {
            var url = $"http://{host}:{port}/api/v1/health";
            using var response = await _httpProbeClient.GetAsync(url, token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var nodeId = root.TryGetProperty("node_id", out var nid) ? nid.GetString() ?? host : host;
                var version = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? "1.0.0" : "1.0.0";
                var hwStatus = root.TryGetProperty("hardware_status", out var hw) ? hw.GetString() ?? "" : "";

                var server = new DiscoveredServer
                {
                    NodeId = nodeId,
                    Host = host,
                    Port = port,
                    Version = version,
                    HardwareGpu = hwStatus,
                    RequiresAuth = false,
                    LastSeen = DateTime.UtcNow
                };

                RegisterServer(server);
            }
        }
        catch
        {
            // Endpoint not reachable
        }
    }

    private void ParseMdnsResponse(byte[] data, IPEndPoint sender)
    {
        try
        {
            string text = Encoding.UTF8.GetString(data);
            if (!text.Contains("_squeeze") && !text.Contains("node_id="))
            {
                return;
            }

            var addr = sender.Address;
            if (addr.IsIPv4MappedToIPv6)
            {
                addr = addr.MapToIPv4();
            }

            string nodeId = addr.ToString();
            string version = "1.0.0";
            string gpu = "none";
            bool auth = false;
            int port = 8080;
            string? advertisedIp = null;
            var advertisedIps = new List<string>();

            foreach (var part in text.Split(new[] { '\0', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("node_id=")) nodeId = part.Substring("node_id=".Length);
                else if (part.StartsWith("version=")) version = part.Substring("version=".Length);
                else if (part.StartsWith("gpu=")) gpu = part.Substring("gpu=".Length);
                else if (part.StartsWith("auth=")) auth = part.Substring("auth=".Length).Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (part.StartsWith("port=") && int.TryParse(part.Substring("port=".Length), out var p)) port = p;
                else if (part.StartsWith("ip=")) advertisedIp = part.Substring("ip=".Length);
                else if (part.StartsWith("ips="))
                {
                    advertisedIps.AddRange(part.Substring("ips=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            // Build ordered list of candidate hosts: prioritize advertised IP from TXT record, then sender address
            var candidateHosts = new List<string>();
            if (!string.IsNullOrWhiteSpace(advertisedIp) && IPAddress.TryParse(advertisedIp, out var advIp) && IsPrivateLanAddress(advIp))
            {
                candidateHosts.Add(advertisedIp);
            }
            foreach (var aip in advertisedIps)
            {
                if (IPAddress.TryParse(aip, out var ip) && IsPrivateLanAddress(ip) && !candidateHosts.Contains(aip))
                {
                    candidateHosts.Add(aip);
                }
            }
            if (IsPrivateLanAddress(addr) && !candidateHosts.Contains(addr.ToString()))
            {
                candidateHosts.Add(addr.ToString());
            }

            // If no candidate is on a private LAN (e.g. 192.0.0.1 on a 10.10 network), reject it
            if (candidateHosts.Count == 0)
            {
                return;
            }

            // Verify candidates with HTTP health probe before registering so unroutable/bogus IPs are never shown
            _ = Task.Run(async () =>
            {
                foreach (var candidate in candidateHosts)
                {
                    try
                    {
                        var url = $"http://{candidate}:{port}/api/v1/health";
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                        using var response = await _httpProbeClient.GetAsync(url, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync(cts.Token);
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            var probedNodeId = root.TryGetProperty("node_id", out var nid) ? nid.GetString() ?? nodeId : nodeId;
                            var probedVersion = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? version : version;
                            var hwStatus = root.TryGetProperty("hardware_status", out var hw) ? hw.GetString() ?? gpu : gpu;

                            var verifiedServer = new DiscoveredServer
                            {
                                NodeId = probedNodeId,
                                Host = candidate,
                                Port = port,
                                Version = probedVersion,
                                HardwareGpu = hwStatus,
                                RequiresAuth = auth,
                                LastSeen = DateTime.UtcNow
                            };
                            RegisterServer(verifiedServer);
                            break;
                        }
                    }
                    catch
                    {
                        // Try next candidate
                    }
                }
            });
        }
        catch
        {
            // Ignore malformed mDNS packets
        }
    }

    private void RegisterServer(DiscoveredServer server)
    {
        string key = $"{server.Host}:{server.Port}";
        _servers.AddOrUpdate(key, server, (_, _) => server);
        ServerDiscovered?.Invoke(this, server);
    }

    private static byte[] BuildMdnsPtrQuery(string serviceName, bool unicastResponse)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: Transaction ID (0), Flags (0), Questions (1), Answers (0), Authority (0), Additional (0)
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((byte)0); writer.Write((byte)1); // Questions: 1 (big endian)
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        // QNAME
        foreach (var label in serviceName.Split('.'))
        {
            if (string.IsNullOrEmpty(label)) continue;
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0); // End of QNAME

        // QTYPE: PTR (12)
        writer.Write((byte)0); writer.Write((byte)12);

        // QCLASS: IN (1) with optional QU bit (0x8000) for unicast response
        if (unicastResponse)
        {
            writer.Write((byte)0x80); writer.Write((byte)1);
        }
        else
        {
            writer.Write((byte)0); writer.Write((byte)1);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        CloseSockets();
        _httpProbeClient.Dispose();
    }
}
