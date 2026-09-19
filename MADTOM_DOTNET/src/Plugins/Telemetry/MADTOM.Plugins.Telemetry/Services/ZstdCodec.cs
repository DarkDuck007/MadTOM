using System;
using System.IO;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace MadTOM.Services;

/// <summary>One process-owned codec pair. Serialized access bounds retained workspaces.</summary>
public static class ZstdCodec
{
    public const int MaxDecodedBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Compressor Encoder = new(1);
    private static readonly Decompressor Decoder = new();

    static ZstdCodec()
    {
        Encoder.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 20);
        Decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 24);
    }

    public static byte[]? CompressIfUseful(byte[] raw)
    {
        if (raw.Length < 1024 || raw.Length > MaxDecodedBytes) return null;
        lock (Gate)
        {
            var encoded = Encoder.Wrap(raw).ToArray();
            return encoded.Length + 16 <= raw.Length * 9L / 10 ? encoded : null;
        }
    }

    public static byte[] Decode(ReadOnlySpan<byte> encoded, int decodedSize)
    {
        if (decodedSize <= 0 || decodedSize > MaxDecodedBytes || encoded.Length > MaxDecodedBytes)
            throw new InvalidDataException("Invalid zstd payload size.");
        var raw = new byte[decodedSize];
        lock (Gate)
        {
            if (Decoder.Unwrap(encoded, raw) != decodedSize)
                throw new InvalidDataException("Zstd decoded length does not match envelope.");
        }
        return raw;
    }
}
