using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Net.Client;
using MADTOM.Protocol.Pairing.V1;

namespace MADTOM.Identity;

public sealed class PairingResult
{
    public bool Success { get; set; }
    public string TrustId { get; set; } = string.Empty;
    public string[] GrantedScopes { get; set; } = Array.Empty<string>();
    public string HubFingerprintSha256 { get; set; } = string.Empty;
    public string ComparisonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class PairingClient
{
    private readonly DeviceCertificate _deviceCert;
    private readonly string _deviceName;
    private readonly string _platform;

    public PairingClient(DeviceCertificate deviceCert, string deviceName, string platform)
    {
        _deviceCert = deviceCert ?? throw new ArgumentNullException(nameof(deviceCert));
        _deviceName = deviceName;
        _platform = platform;
    }

    public static GrpcChannel CreateSecureChannel(string host, int port, string? pinnedFingerprint = null, DeviceCertificate? clientCert = null)
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                {
                    if (certificate == null) return false;
                    if (string.IsNullOrEmpty(pinnedFingerprint)) return true;

                    var certHash = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
                    return string.Equals(certHash, pinnedFingerprint, StringComparison.OrdinalIgnoreCase);
                },
                ClientCertificates = clientCert != null ? new X509CertificateCollection { clientCert.Certificate } : null
            }
        };

        return GrpcChannel.ForAddress($"https://{host}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
    }

    public async Task<PairingResult> PairWithQrAsync(
        GrpcChannel channel,
        PairingQrDetails qrDetails,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(qrDetails);

        var client = new Pairing.PairingClient(channel);
        var nonceDevice = RandomNumberGenerator.GetBytes(32);
        var tokenBytes = Convert.FromHexString(qrDetails.Token);

        var beginReq = new BeginRequest
        {
            SessionId = qrDetails.SessionId,
            DeviceCertDer = ByteString.CopyFrom(_deviceCert.RawCertDer),
            DeviceName = _deviceName,
            Platform = _platform,
            NonceDevice = ByteString.CopyFrom(nonceDevice),
            Token = ByteString.CopyFrom(tokenBytes)
        };

        var beginRes = await client.BeginAsync(beginReq, cancellationToken: ct);

        // Verify hub cert fingerprint against pinned fingerprint from QR
        using var hubX509 = X509CertificateLoader.LoadCertificate(beginRes.HubCertDer.ToByteArray());
        var receivedHubFp = Convert.ToHexString(hubX509.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

        if (!string.Equals(receivedHubFp, qrDetails.HubFingerprintSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new PairingResult
            {
                Success = false,
                Message = "Hub certificate fingerprint mismatch! Potential MITM attack aborted."
            };
        }

        var completeReq = new CompleteRequest
        {
            SessionId = qrDetails.SessionId,
            SasConfirmed = true
        };

        var completeRes = await client.CompleteAsync(completeReq, cancellationToken: ct);

        return new PairingResult
        {
            Success = completeRes.Accepted,
            TrustId = completeRes.TrustId,
            GrantedScopes = completeRes.GrantedScopes.ToArray(),
            HubFingerprintSha256 = receivedHubFp,
            ComparisonCode = beginRes.ComparisonCode,
            Message = completeRes.Message
        };
    }

    public async Task<(string ComparisonCode, Func<bool, Task<PairingResult>> ConfirmCallback)> BeginManualPairingAsync(
        GrpcChannel channel,
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var client = new Pairing.PairingClient(channel);
        var nonceDevice = RandomNumberGenerator.GetBytes(32);

        var beginReq = new BeginRequest
        {
            SessionId = sessionId,
            DeviceCertDer = ByteString.CopyFrom(_deviceCert.RawCertDer),
            DeviceName = _deviceName,
            Platform = _platform,
            NonceDevice = ByteString.CopyFrom(nonceDevice)
        };

        var beginRes = await client.BeginAsync(beginReq, cancellationToken: ct);

        using var hubX509 = X509CertificateLoader.LoadCertificate(beginRes.HubCertDer.ToByteArray());
        var hubFp = Convert.ToHexString(hubX509.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

        var localCode = PairingUtils.DeriveComparisonCode(
            beginRes.HubCertDer.ToByteArray(),
            _deviceCert.RawCertDer,
            beginRes.NonceHub.ToByteArray(),
            nonceDevice);

        async Task<PairingResult> ConfirmAsync(bool confirmed)
        {
            var completeReq = new CompleteRequest
            {
                SessionId = sessionId,
                SasConfirmed = confirmed
            };

            var completeRes = await client.CompleteAsync(completeReq, cancellationToken: ct);
            return new PairingResult
            {
                Success = completeRes.Accepted,
                TrustId = completeRes.TrustId,
                GrantedScopes = completeRes.GrantedScopes.ToArray(),
                HubFingerprintSha256 = hubFp,
                ComparisonCode = localCode,
                Message = completeRes.Message
            };
        }

        return (localCode, ConfirmAsync);
    }
}
