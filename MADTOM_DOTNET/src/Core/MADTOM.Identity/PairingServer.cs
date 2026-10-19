using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using MADTOM.Protocol.Pairing.V1;

namespace MADTOM.Identity;

public sealed class PairingSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(5);
    public byte[] NonceHub { get; set; } = RandomNumberGenerator.GetBytes(32);
    public byte[]? NonceDevice { get; set; }
    public byte[]? DeviceCertDer { get; set; }
    public string DeviceFingerprint { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ComparisonCode { get; set; } = string.Empty;
    public bool IsQrPath { get; set; }
    public bool IsTokenConsumed { get; set; }
    public bool IsCompleted { get; set; }
}

public sealed class PairingServer : Pairing.PairingBase
{
    private readonly ITrustProvider _trustProvider;
    private readonly DeviceCertificate _hubCertificate;
    private readonly ConcurrentDictionary<string, PairingSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public PairingServer(ITrustProvider trustProvider, DeviceCertificate hubCertificate)
    {
        _trustProvider = trustProvider ?? throw new ArgumentNullException(nameof(trustProvider));
        _hubCertificate = hubCertificate ?? throw new ArgumentNullException(nameof(hubCertificate));
    }

    public PairingSession CreateSession(string host, int port, out string qrUri)
    {
        var session = new PairingSession();
        session.ComparisonCode = RandomNumberGenerator.GetInt32(1000, 10000).ToString("D4");
        _sessions[session.SessionId] = session;

        qrUri = PairingUtils.CreateQrUri(host, port, _hubCertificate.FingerprintSha256, session.Token, session.SessionId);
        return session;
    }

    public PairingSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var s);
        return s;
    }

    public override Task<BeginResponse> Begin(BeginRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.SessionId) || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Pairing session not found or expired"));
        }

        if (DateTime.UtcNow > session.ExpiresAtUtc)
        {
            _sessions.TryRemove(request.SessionId, out _);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Pairing session has expired"));
        }

        if (session.IsTokenConsumed || session.IsCompleted)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Pairing token has already been used"));
        }

        if (request.Token != null && request.Token.Length > 0)
        {
            // QR Path
            var requestToken = Convert.ToHexString(request.Token.ToByteArray()).ToLowerInvariant();
            if (!string.Equals(requestToken, session.Token, StringComparison.OrdinalIgnoreCase))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid QR token"));
            }
            session.IsQrPath = true;
        }

        if (request.DeviceCertDer == null || request.DeviceCertDer.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing device certificate"));
        }

        try
        {
            using var x509 = X509CertificateLoader.LoadCertificate(request.DeviceCertDer.ToByteArray());
            session.DeviceFingerprint = Convert.ToHexString(x509.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid device certificate: {ex.Message}"));
        }

        session.DeviceCertDer = request.DeviceCertDer.ToByteArray();
        session.NonceDevice = request.NonceDevice.ToByteArray();
        session.DeviceName = request.DeviceName ?? "Unknown Device";
        session.Platform = request.Platform ?? "generic";

        // Derive 4-digit comparison code
        session.ComparisonCode = PairingUtils.DeriveComparisonCode(
            _hubCertificate.RawCertDer,
            session.DeviceCertDer,
            session.NonceHub,
            session.NonceDevice);

        return Task.FromResult(new BeginResponse
        {
            HubCertDer = ByteString.CopyFrom(_hubCertificate.RawCertDer),
            NonceHub = ByteString.CopyFrom(session.NonceHub),
            ComparisonCode = session.ComparisonCode
        });
    }

    public override async Task<CompleteResponse> Complete(CompleteRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.SessionId) || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Pairing session not found"));
        }

        if (session.IsTokenConsumed || session.IsCompleted)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Pairing token has already been used"));
        }

        if (!session.IsQrPath && !request.SasConfirmed)
        {
            // Manual path aborted due to code mismatch (AT2)
            _sessions.TryRemove(request.SessionId, out _);
            return new CompleteResponse
            {
                Accepted = false,
                Message = "User rejected PIN comparison code mismatch."
            };
        }

        session.IsTokenConsumed = true;
        session.IsCompleted = true;
        _sessions.TryRemove(request.SessionId, out _);

        var trustId = "t_" + Guid.NewGuid().ToString("N")[..12];
        var entry = new TrustEntry
        {
            TrustId = trustId,
            CertFingerprintSha256 = session.DeviceFingerprint,
            DisplayName = session.DeviceName,
            Platform = session.Platform,
            GrantedScopes = new System.Collections.Generic.List<string>
            {
                "video.source.publish",
                "video.feed.view",
                "video.feed.control"
            },
            Issuer = "local",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };

        await _trustProvider.AddOrUpdateTrustAsync(entry);

        var res = new CompleteResponse
        {
            Accepted = true,
            TrustId = trustId,
            Message = "Pairing successful."
        };
        res.GrantedScopes.AddRange(entry.GrantedScopes);
        return res;
    }
}
