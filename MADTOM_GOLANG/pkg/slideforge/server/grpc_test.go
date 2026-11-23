package server

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"net"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
)

func TestSignedGRPCStreamsAndSpool(t *testing.T) {
	cfg := DefaultConfig()
	cfg.StateDir = t.TempDir()
	cfg.StoreRawLogs = true
	tlsConfig, fingerprint, err := auth.LoadOrCreateTLS(cfg.StateDir, cfg.NodeID)
	if err != nil {
		t.Fatal(err)
	}
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	s, err := New(cfg, auth.NewVerifier([]*auth.KeyPair{{Ed25519PublicKey: pub}}, nil), tlsConfig)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(s.Stop)

	listener := bufconn.Listen(1 << 20)
	t.Cleanup(func() { listener.Close() })
	go s.grpcServer.Serve(listener)
	leaf, err := x509.ParseCertificate(tlsConfig.Certificates[0].Certificate[0])
	if err != nil {
		t.Fatal(err)
	}
	roots := x509.NewCertPool()
	roots.AddCert(leaf)
	conn, err := grpc.NewClient("passthrough:///slideforge",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return listener.DialContext(ctx) }),
		grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{RootCAs: roots, ServerName: cfg.NodeID, MinVersion: tls.VersionTLS13})))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	client := madtomv1.NewSlideForgeControlServiceClient(conn)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	signed := func(nonce string) context.Context {
		ts := strconv.FormatInt(time.Now().Unix(), 10)
		sig := base64.RawURLEncoding.EncodeToString(ed25519.Sign(priv, []byte(ts+":"+nonce)))
		return metadata.AppendToOutgoingContext(ctx, "x-nox-auth-ts", ts, "x-nox-auth-nonce", nonce, "x-nox-auth-sig", sig)
	}

	if _, err := client.GetNodeStatus(ctx, &madtomv1.SfNodeStatusRequest{}); status.Code(err) != codes.Unauthenticated {
		t.Fatalf("unsigned unary call: %v", err)
	}
	statusCtx := signed("status-nonce")
	st, err := client.GetNodeStatus(statusCtx, &madtomv1.SfNodeStatusRequest{})
	if err != nil || st.GetTlsFingerprintSha256() != fingerprint {
		t.Fatalf("signed status or fingerprint: %v, %v", st, err)
	}
	replay, err := client.SubscribeState(statusCtx, &madtomv1.SfStateSubscriptionRequest{})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := replay.Recv(); status.Code(err) != codes.Unauthenticated {
		t.Fatalf("unary signature was replayed on a stream: %v", err)
	}

	states, err := client.SubscribeState(signed("state-nonce"), &madtomv1.SfStateSubscriptionRequest{})
	if err != nil {
		t.Fatal(err)
	}
	s.OnTelemetrySample(&madtomv1.NoxTelemetrySample{TimestampUnixNano: 123, Metrics: map[string]float64{"jobs_active": 2}})
	s.OnJobEvent(&madtomv1.NoxJobProgressEvent{JobId: "job-1", ChatId: 42, JobType: "note", Details: "{}"})
	stateChunk, err := states.Recv()
	if err != nil {
		t.Fatal(err)
	}
	decoder, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer decoder.Close()
	decode := func(data []byte, size uint32, payload proto.Message) {
		t.Helper()
		raw, err := decoder.DecodeAll(data, nil)
		if err != nil || len(raw) != int(size) {
			t.Fatalf("decode zstd payload: size %d, want %d, err %v", len(raw), size, err)
		}
		if err := proto.Unmarshal(raw, payload); err != nil {
			t.Fatal(err)
		}
	}
	state := new(madtomv1.SfStateChunkPayload)
	decode(stateChunk.ZstdPayload, stateChunk.DecodedSize, state)
	if len(state.TelemetrySamples) != 1 || len(state.JobEvents) != 1 ||
		state.TelemetrySamples[0].GetMetrics()["jobs_active"] != 2 || state.JobEvents[0].GetUserId() != 42 {
		t.Fatalf("unexpected state payload: %v", state)
	}

	logs, err := client.SubscribeLogsAndApi(signed("logs-nonce"), &madtomv1.SfLogApiSubscriptionRequest{IncludeRawApiCalls: true})
	if err != nil {
		t.Fatal(err)
	}
	for {
		s.mu.Lock()
		subscribed := s.apiSubscribers > 0
		s.mu.Unlock()
		if subscribed {
			break
		}
		if ctx.Err() != nil {
			t.Fatal("log subscription never registered")
		}
		time.Sleep(5 * time.Millisecond)
	}
	s.OnLogRecord(&madtomv1.LogRecord{Seq: 42, TimestampUnixNano: 123, Message: "hello"})
	s.OnApiCallRecord(&madtomv1.NoxRawApiCallRecord{
		TraceContext: &madtomv1.NoxTraceContext{RunId: "job-1"}, DurationMs: 1250,
		RawRequestJson: strings.Repeat("x", (64<<10)-1) + "é",
	})
	logChunk, err := logs.Recv()
	if err != nil {
		t.Fatal(err)
	}
	logsPayload := new(madtomv1.SfLogApiChunkPayload)
	decode(logChunk.ZstdPayload, logChunk.DecodedSize, logsPayload)
	if logChunk.ChunkKind != "log" || len(logsPayload.LogRecords) != 1 || logsPayload.LogRecords[0].GetSeq() != 42 {
		t.Fatalf("unexpected log payload: %v", logsPayload)
	}
	apiChunk, err := logs.Recv()
	if err != nil {
		t.Fatal(err)
	}
	apiPayload := new(madtomv1.SfLogApiChunkPayload)
	decode(apiChunk.ZstdPayload, apiChunk.DecodedSize, apiPayload)
	if apiChunk.ChunkKind != "api" || len(apiPayload.ApiCallRecords) != 1 {
		t.Fatalf("unexpected API chunk kind=%q records=%d", apiChunk.ChunkKind, len(apiPayload.ApiCallRecords))
	}
	call := apiPayload.ApiCallRecords[0]
	if call.JobId != "job-1" || call.Seconds != 1.25 || !call.Ok || len(call.Json) != (64<<10)-1 {
		t.Fatalf("unexpected API payload: kind=%q, job=%q, seconds=%v, ok=%v, bytes=%d", apiChunk.ChunkKind, call.JobId, call.Seconds, call.Ok, len(call.Json))
	}
	spool, err := client.GetSpoolStats(signed("spool-nonce"), &madtomv1.SfSpoolStatsRequest{})
	if err != nil || spool.GetLogChunks() != 1 || spool.GetApiChunks() != 1 {
		t.Fatalf("spool stats: %v, %v", spool, err)
	}
	if _, err := client.UpdateControlConfig(signed("quota-nonce"), &madtomv1.SfUpdateControlConfigRequest{MaxSpoolBytes: 1}); err != nil {
		t.Fatal(err)
	}
	spool, err = client.GetSpoolStats(signed("spool-nonce-2"), &madtomv1.SfSpoolStatsRequest{})
	if err != nil || spool.GetCurrentBytes() > 1 || spool.GetMaxBytes() != 1 {
		t.Fatalf("spool after quota update: %v, %v", spool, err)
	}
}
