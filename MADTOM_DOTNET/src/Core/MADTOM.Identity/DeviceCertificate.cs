using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MADTOM.Identity;

public sealed class DeviceCertificate : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string FingerprintSha256 { get; }
    public byte[] RawCertDer => Certificate.RawData;

    public DeviceCertificate(X509Certificate2 cert)
    {
        Certificate = cert ?? throw new ArgumentNullException(nameof(cert));
        FingerprintSha256 = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
    }

    public static DeviceCertificate GenerateSelfSigned(string deviceName)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            $"CN=MADTOM Device {deviceName}",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new Oid("1.3.6.1.5.5.7.3.1"), // ServerAuth
                    new Oid("1.3.6.1.5.5.7.3.2")  // ClientAuth
                },
                critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(10);

        var cert = req.CreateSelfSigned(notBefore, notAfter);
        return new DeviceCertificate(cert);
    }

    public static DeviceCertificate LoadOrCreate(string pfxPath, string deviceName, string? password = null)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(pfxPath);
                var cert = X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                return new DeviceCertificate(cert);
            }
            catch
            {
                // Re-create if corrupt
            }
        }

        var newCert = GenerateSelfSigned(deviceName);
        var pfxBytes = newCert.Certificate.Export(X509ContentType.Pfx, password);
        var dir = Path.GetDirectoryName(pfxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(pfxPath, pfxBytes);
        return newCert;
    }

    public void Dispose()
    {
        Certificate.Dispose();
    }
}
