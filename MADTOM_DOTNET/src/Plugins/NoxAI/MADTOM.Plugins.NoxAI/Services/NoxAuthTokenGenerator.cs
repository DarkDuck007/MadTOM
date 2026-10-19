using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace MADTOM.Plugins.NoxAI.Services;

/// <summary>
/// Generates and manages SSH-style cryptographic client keys for NoxChat control center.
/// Public Key Format: `noxchat-ed25519-pub:<base64url>`
/// Private Key Format: `noxchat-ed25519-priv:<base64url>`
/// Legacy Format: `noxchat-ed25519-v1:<base64url>`
/// </summary>
public static class NoxAuthTokenGenerator
{
    public const string PubKeyPrefix = "noxchat-ed25519-pub:";
    public const string PrivKeyPrefix = "noxchat-ed25519-priv:";
    public const string LegacyKeyPrefix = "noxchat-ed25519-v1:";

    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        byte[] privBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        byte[] pubBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        string pubStr = PubKeyPrefix + ToBase64Url(pubBytes);
        string privStr = PrivKeyPrefix + ToBase64Url(privBytes);
        return (pubStr, privStr);
    }

    public static string GenerateKey()
    {
        var (_, priv) = GenerateKeyPair();
        return priv;
    }

    public static string GetPublicKey(string privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey)) return string.Empty;
        if (privateKey.StartsWith(PubKeyPrefix)) return privateKey;

        byte[]? privBytes = DecodeKeyBytes(privateKey);
        if (privBytes == null || privBytes.Length != 32) return string.Empty;

        try
        {
            using var key = Key.Import(SignatureAlgorithm.Ed25519, privBytes, KeyBlobFormat.RawPrivateKey);
            byte[] pubBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            return PubKeyPrefix + ToBase64Url(pubBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static (string Ts, string Nonce, string Sig) CreateAuthHeaders(string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        byte[]? keyBytes = DecodeKeyBytes(clientKey);
        if (keyBytes == null || keyBytes.Length < 16)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string nonce = Guid.NewGuid().ToString("N");
        byte[] dataToSign = Encoding.UTF8.GetBytes($"{ts}:{nonce}");

        // 1. Try Ed25519 signing if 32 bytes (standard Ed25519 private seed)
        if (keyBytes.Length == 32)
        {
            try
            {
                using var key = Key.Import(SignatureAlgorithm.Ed25519, keyBytes, KeyBlobFormat.RawPrivateKey);
                byte[] sigBytes = SignatureAlgorithm.Ed25519.Sign(key, dataToSign);
                string sig = ToBase64Url(sigBytes);
                return (ts, nonce, sig);
            }
            catch
            {
                // Fallback to HMAC
            }
        }

        // 2. Fallback to HMAC
        byte[] hmacKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: keyBytes,
            outputLength: 32,
            salt: Encoding.UTF8.GetBytes("noxchat-salt-v1"),
            info: Encoding.UTF8.GetBytes("noxchat-hmac-v1")
        );

        using var hmac = new HMACSHA256(hmacKey);
        byte[] hmacSigBytes = hmac.ComputeHash(dataToSign);
        string hmacSig = ToBase64Url(hmacSigBytes);
        return (ts, nonce, hmacSig);
    }

    private static byte[]? DecodeKeyBytes(string keyStr)
    {
        string raw = keyStr.Trim();
        if (raw.StartsWith(PrivKeyPrefix)) raw = raw[PrivKeyPrefix.Length..];
        else if (raw.StartsWith(PubKeyPrefix)) raw = raw[PubKeyPrefix.Length..];
        else if (raw.StartsWith(LegacyKeyPrefix)) raw = raw[LegacyKeyPrefix.Length..];

        int rem = raw.Length % 4;
        if (rem > 0) raw += new string('=', 4 - rem);
        raw = raw.Replace("-", "+").Replace("_", "/");

        try
        {
            return Convert.FromBase64String(raw);
        }
        catch
        {
            return null;
        }
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
