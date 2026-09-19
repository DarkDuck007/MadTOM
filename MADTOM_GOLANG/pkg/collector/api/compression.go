package api

import (
	"context"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/metadata"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"sync"
)

// One bounded, process-owned encoder; no per-client codec workspaces are retained.
var responseCodec struct {
	sync.Mutex
	encoder *zstd.Encoder
}

const compressionHeader = "madtom-accept-zstd"
const maxCompressedResponse = 16 * 1024 * 1024

func encodeResponse[T proto.Message](ctx context.Context, message T) (T, error) {
	md, _ := metadata.FromIncomingContext(ctx)
	accepted := false
	for _, v := range md.Get(compressionHeader) {
		if v == "1" {
			accepted = true
		}
	}
	size := proto.Size(message)
	if !accepted || size < 1024 || size > maxCompressedResponse {
		return message, nil
	}
	raw, err := proto.Marshal(message)
	if err != nil {
		return message, err
	}
	responseCodec.Lock()
	if responseCodec.encoder == nil {
		responseCodec.encoder, err = zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithWindowSize(1<<20), zstd.WithEncoderLevel(zstd.SpeedFastest))
	}
	if err != nil {
		responseCodec.Unlock()
		return message, err
	}
	compressed := responseCodec.encoder.EncodeAll(raw, nil)
	responseCodec.Unlock()
	// Account for envelope overhead and require at least 10% savings.
	if len(compressed)+16 > len(raw)*9/10 {
		return message, nil
	}
	out := message.ProtoReflect().New()
	fields := out.Descriptor().Fields()
	out.Set(fields.ByName("zstd_payload"), protoreflect.ValueOfBytes(compressed))
	out.Set(fields.ByName("decoded_size"), protoreflect.ValueOfUint32(uint32(len(raw))))
	return out.Interface().(T), nil
}
