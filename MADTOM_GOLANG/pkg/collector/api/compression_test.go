package api

import (
	"context"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/metadata"
	"google.golang.org/protobuf/proto"
	"strings"
	"testing"
)

func TestResponseCompressionNegotiationAndLosslessTypes(t *testing.T) {
	large := strings.Repeat("sample", 1000)
	messages := []proto.Message{
		&madtomv1.ListNodesResponse{CollectorName: large},
		&madtomv1.RangeQueryResponse{NodeId: large},
		&madtomv1.LiveTelemetryEvent{NodeId: large},
		&madtomv1.NodeConfig{NodeId: large},
		&madtomv1.ConfigAck{Message: large, Success: true},
	}
	decoder, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer decoder.Close()
	for _, original := range messages {
		copy := proto.Clone(original)
		plain, err := encodeResponse(context.Background(), original)
		if err != nil || !proto.Equal(plain, original) {
			t.Fatal("legacy response changed")
		}
		ctx := metadata.NewIncomingContext(context.Background(), metadata.Pairs(compressionHeader, "1"))
		encoded, err := encodeResponse(ctx, original)
		if err != nil {
			t.Fatal(err)
		}
		v := encoded.ProtoReflect()
		fields := v.Descriptor().Fields()
		payload := v.Get(fields.ByName("zstd_payload")).Bytes()
		if len(payload) == 0 || proto.Size(encoded) >= proto.Size(original) {
			t.Fatal("response not compressed")
		}
		raw, err := decoder.DecodeAll(payload, nil)
		if err != nil {
			t.Fatal(err)
		}
		if uint64(len(raw)) != v.Get(fields.ByName("decoded_size")).Uint() {
			t.Fatal("size mismatch")
		}
		restored := original.ProtoReflect().New().Interface()
		if err := proto.Unmarshal(raw, restored); err != nil {
			t.Fatal(err)
		}
		if !proto.Equal(restored, original) || !proto.Equal(original, copy) {
			t.Fatal("data changed")
		}
	}
	ctx := metadata.NewIncomingContext(context.Background(), metadata.Pairs(compressionHeader, "1"))
	small := &madtomv1.ConfigAck{Success: true}
	result, err := encodeResponse(ctx, small)
	if err != nil || len(result.ZstdPayload) != 0 || !result.Success {
		t.Fatal("small message should stay raw")
	}
}
