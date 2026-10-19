using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Web;

namespace MADTOM.Identity;

public static class PairingUtils
{
    public static string DeriveComparisonCode(byte[] hubCertDer, byte[] deviceCertDer, byte[] nonceHub, byte[] nonceDevice)
    {
        var input = hubCertDer
            .Concat(deviceCertDer)
            .Concat(nonceHub)
            .Concat(nonceDevice)
            .ToArray();

        var hash = SHA256.HashData(input);
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, 4));
        var pin = value % 10000;
        return pin.ToString("D4");
    }

    public static string CreateQrUri(string host, int port, string hubFingerprint, string token, string sessionId)
    {
        return $"madtom://pair?host={Uri.EscapeDataString(host)}&port={port}&fp={Uri.EscapeDataString(hubFingerprint)}&tok={Uri.EscapeDataString(token)}&sid={Uri.EscapeDataString(sessionId)}";
    }

    public static bool TryParseQrUri(string uriString, out PairingQrDetails details)
    {
        details = new PairingQrDetails();
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "madtom", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase)) return false;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var host = query["host"];
        var portStr = query["port"];
        var fp = query["fp"];
        var tok = query["tok"];
        var sid = query["sid"];

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(sid))
            return false;

        if (!int.TryParse(portStr, out var port))
            port = 7443;

        details = new PairingQrDetails
        {
            Host = host,
            Port = port,
            HubFingerprintSha256 = fp,
            Token = tok ?? string.Empty,
            SessionId = sid
        };
        return true;
    }

    public static string GetLocalLanIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is System.Net.IPEndPoint ep && ep.Address != System.Net.IPAddress.Any && !System.Net.IPAddress.IsLoopback(ep.Address))
            {
                return ep.Address.ToString();
            }
        }
        catch { }

        try
        {
            foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var ipProps = netInterface.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }
}

public sealed class PairingQrDetails
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 7443;
    public string HubFingerprintSha256 { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
}
