using System.IO;
using Google.Protobuf;
using Grpc.Core;

namespace MadTOM.Services;

public static class ClientResponseCompression
{
    public static Metadata AcceptHeaders() => new() { { "madtom-accept-zstd", "1" } };

    public static T Decode<T>(T response, ClientPayloadCounter counter) where T : IMessage<T>, new()
    {
        var payloadField = response.Descriptor.FindFieldByName("zstd_payload");
        var sizeField = response.Descriptor.FindFieldByName("decoded_size");
        var payload = (ByteString)payloadField.Accessor.GetValue(response);
        var size = (uint)sizeField.Accessor.GetValue(response);
        if (payload.IsEmpty)
        {
            if (size != 0) throw new InvalidDataException("Missing zstd response payload.");
            counter.Record(response.CalculateSize());
            return response;
        }
        if (size == 0 || size > ZstdCodec.MaxDecodedBytes) throw new InvalidDataException("Invalid zstd response size.");
        var raw = ZstdCodec.Decode(payload.Span, (int)size);
        var decoded = new T();
        decoded.MergeFrom(raw);
        if (!((ByteString)payloadField.Accessor.GetValue(decoded)).IsEmpty || (uint)sizeField.Accessor.GetValue(decoded) != 0)
            throw new InvalidDataException("Nested zstd response envelope.");
        counter.RecordCompressed(raw.Length, payload.Length);
        return decoded;
    }
}
