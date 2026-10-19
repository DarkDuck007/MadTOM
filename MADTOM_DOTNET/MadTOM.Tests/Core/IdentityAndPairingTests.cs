using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using MADTOM.Identity;
using MADTOM.Protocol.Pairing.V1;
using Xunit;

namespace MadTOM.Tests.Core;

public class IdentityAndPairingTests
{
    [Fact]
    public void DeviceCertificate_GeneratesP256SelfSignedWithSha256Fingerprint()
    {
        using var cert = DeviceCertificate.GenerateSelfSigned("test-device");
        Assert.NotNull(cert.Certificate);
        Assert.NotEmpty(cert.FingerprintSha256);
        Assert.Equal(64, cert.FingerprintSha256.Length); // 32 bytes hex = 64 chars
        Assert.True(cert.RawCertDer.Length > 0);
    }

    [Fact]
    public async Task TrustStore_PersistsAndEvaluatesScopesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"trust_{Guid.NewGuid():N}.json");
        try
        {
            var store = new TrustStore(tempFile);
            var entry = new TrustEntry
            {
                CertFingerprintSha256 = "abc123def456",
                DisplayName = "Test Phone",
                GrantedScopes = new() { "video.source.publish", "video.feed.view" }
            };

            await store.AddOrUpdateTrustAsync(entry);

            Assert.True(store.IsTrusted("abc123def456"));
            Assert.True(store.IsTrusted("abc123def456", "video.source.publish"));
            Assert.False(store.IsTrusted("abc123def456", "video.feed.control"));
            Assert.False(store.IsTrusted("untrusted_fp"));

            // Reload from file to verify persistence
            var reloaded = new TrustStore(tempFile);
            Assert.True(reloaded.IsTrusted("abc123def456", "video.source.publish"));

            // Revoke
            var revoked = await reloaded.RevokeTrustAsync(entry.TrustId);
            Assert.True(revoked);
            Assert.False(reloaded.IsTrusted("abc123def456"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PairingUtils_DerivesConsistent4DigitComparisonCode()
    {
        var hubCert = RandomNumberGenerator.GetBytes(100);
        var devCert = RandomNumberGenerator.GetBytes(100);
        var nHub = RandomNumberGenerator.GetBytes(32);
        var nDev = RandomNumberGenerator.GetBytes(32);

        var code1 = PairingUtils.DeriveComparisonCode(hubCert, devCert, nHub, nDev);
        var code2 = PairingUtils.DeriveComparisonCode(hubCert, devCert, nHub, nDev);

        Assert.Equal(4, code1.Length);
        Assert.Equal(code1, code2);
    }

    [Fact]
    public async Task PairingServer_QrPath_SucceedsAndRejectsReuse()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"trust_{Guid.NewGuid():N}.json");
        try
        {
            var trustStore = new TrustStore(tempFile);
            using var hubCert = DeviceCertificate.GenerateSelfSigned("hub");
            using var devCert = DeviceCertificate.GenerateSelfSigned("phone");

            var server = new PairingServer(trustStore, hubCert);
            var session = server.CreateSession("127.0.0.1", 7443, out var qrUri);

            Assert.True(PairingUtils.TryParseQrUri(qrUri, out var details));
            Assert.Equal(hubCert.FingerprintSha256, details.HubFingerprintSha256);

            // Step 1: Begin
            var beginReq = new BeginRequest
            {
                SessionId = session.SessionId,
                DeviceCertDer = ByteString.CopyFrom(devCert.RawCertDer),
                DeviceName = "Pixel 8",
                Platform = "android",
                NonceDevice = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                Token = ByteString.CopyFrom(Convert.FromHexString(session.Token))
            };

            var beginRes = await server.Begin(beginReq, null!);
            Assert.NotNull(beginRes);
            Assert.Equal(4, beginRes.ComparisonCode.Length);

            // Step 2: Complete
            var completeReq = new CompleteRequest
            {
                SessionId = session.SessionId,
                SasConfirmed = true
            };

            var completeRes = await server.Complete(completeReq, null!);
            Assert.True(completeRes.Accepted);
            Assert.Contains("video.source.publish", completeRes.GrantedScopes);
            Assert.True(trustStore.IsTrusted(devCert.FingerprintSha256));

            // AT1: Second use of the same token/session MUST be rejected!
            await Assert.ThrowsAsync<RpcException>(async () =>
            {
                await server.Begin(beginReq, null!);
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task PairingServer_ManualPinPath_AbortsOnMismatch()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"trust_{Guid.NewGuid():N}.json");
        try
        {
            var trustStore = new TrustStore(tempFile);
            using var hubCert = DeviceCertificate.GenerateSelfSigned("hub");
            using var devCert = DeviceCertificate.GenerateSelfSigned("phone");

            var server = new PairingServer(trustStore, hubCert);
            var session = server.CreateSession("127.0.0.1", 7443, out _);

            // Manual path: no token passed in Begin
            var beginReq = new BeginRequest
            {
                SessionId = session.SessionId,
                DeviceCertDer = ByteString.CopyFrom(devCert.RawCertDer),
                DeviceName = "Manual Device",
                Platform = "linux",
                NonceDevice = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32))
            };

            var beginRes = await server.Begin(beginReq, null!);
            Assert.NotNull(beginRes);

            // AT2: User rejects matching code -> SasConfirmed = false
            var completeReq = new CompleteRequest
            {
                SessionId = session.SessionId,
                SasConfirmed = false
            };

            var completeRes = await server.Complete(completeReq, null!);
            Assert.False(completeRes.Accepted);
            Assert.False(trustStore.IsTrusted(devCert.FingerprintSha256));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
