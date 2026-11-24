package server

import (
	"bufio"
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxchat/bridge"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// fakeBot speaks the NoxChat control-channel frame protocol: [kind u8][len u32 BE][JSON].
type fakeBot struct {
	ln      net.Listener
	mu      sync.Mutex
	methods []string
	sendCh  chan []byte
}

func startFakeBot(t *testing.T) *fakeBot {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	b := &fakeBot{ln: ln, sendCh: make(chan []byte, 16)}
	go b.serve()
	t.Cleanup(func() { ln.Close() })
	return b
}

func (b *fakeBot) port() int { return b.ln.Addr().(*net.TCPAddr).Port }

func (b *fakeBot) seen() []string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]string(nil), b.methods...)
}

func writeFrame(w io.Writer, kind byte, data []byte) error {
	hdr := make([]byte, 5)
	hdr[0] = kind
	binary.BigEndian.PutUint32(hdr[1:], uint32(len(data)))
	if _, err := w.Write(hdr); err != nil {
		return err
	}
	_, err := w.Write(data)
	return err
}

func (b *fakeBot) serve() {
	for {
		conn, err := b.ln.Accept()
		if err != nil {
			return
		}
		go func(c net.Conn) {
			defer c.Close()
			var wmu sync.Mutex
			go func() {
				for frame := range b.sendCh {
					wmu.Lock()
					writeFrame(c, frame[0], frame[1:])
					wmu.Unlock()
				}
			}()
			r := bufio.NewReader(c)
			for {
				hdr := make([]byte, 5)
				if _, err := io.ReadFull(r, hdr); err != nil {
					return
				}
				body := make([]byte, binary.BigEndian.Uint32(hdr[1:]))
				if _, err := io.ReadFull(r, body); err != nil {
					return
				}
				if hdr[0] != bridge.FrameKindRpcRequest {
					continue
				}
				var req bridge.RpcMessage
				if json.Unmarshal(body, &req) != nil {
					continue
				}
				b.mu.Lock()
				b.methods = append(b.methods, req.Method)
				b.mu.Unlock()
				reply := bridge.RpcMessage{ReqID: req.ReqID, Method: req.Method}
				switch req.Method {
				case "get_overview":
					reply.Payload = json.RawMessage(`{"jobs_active":1,"echo":` + string(req.Payload) + `}`)
				case "cancel_job":
					reply.Error = "job not found"
				case "list_jobs":
					time.Sleep(2 * time.Second) // slower than the test timeout
					reply.Payload = json.RawMessage(`{}`)
				default:
					reply.Payload = json.RawMessage(`{}`)
				}
				data, _ := json.Marshal(reply)
				wmu.Lock()
				writeFrame(c, bridge.FrameKindRpcReply, data)
				wmu.Unlock()
			}
		}(conn)
	}
}

func newTestServer(t *testing.T, botPort int) *Server {
	t.Helper()
	cfg := DefaultConfig()
	cfg.NodeID = "tp2"
	cfg.ListenAddr = "127.0.0.1:0"
	cfg.BotHost = "127.0.0.1"
	cfg.BotPort = botPort
	cfg.StateDir = t.TempDir()
	s, err := New(cfg, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(s.Stop)
	return s
}

func waitConnected(t *testing.T, s *Server) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		st, err := s.GetNodeStatus(context.Background(), &madtomv1.SfNodeStatusRequest{})
		if err == nil && st.BotRunning {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("probe never connected to the fake bot")
}

func TestDefaultAllowlistMatchesContract(t *testing.T) {
	want := []string{
		"get_overview", "list_jobs", "get_job", "list_job_files", "cancel_job", "extend_job", "get_job_file", "get_job_transcript",
		"list_users", "get_user", "set_user_quota", "set_user_banned",
		"list_guides", "get_guide", "put_guide", "list_tips", "upsert_tip", "delete_tip", "preview_prompt",
		"get_config",
		"sandbox_list_vms", "sandbox_stats", "sandbox_vm_action", "sandbox_list_files", "sandbox_read_text", "sandbox_preview",
		"sandbox_backups", "sandbox_restore_backup", "sandbox_library_requests", "sandbox_decide_library",
		"sandbox_image_status", "sandbox_rebuild_image", "sandbox_runs",
	}
	got := append([]string(nil), DefaultConfig().AllowedMethods...)
	sort.Strings(want)
	sort.Strings(got)
	if strings.Join(want, ",") != strings.Join(got, ",") {
		t.Fatalf("allowlist mismatch\nwant %v\n got %v", want, got)
	}
}

func TestCallAllowedMethodPassesPayloadThrough(t *testing.T) {
	bot := startFakeBot(t)
	s := newTestServer(t, bot.port())
	waitConnected(t, s)
	resp, err := s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "get_overview", PayloadJson: `{"days":7}`})
	if err != nil {
		t.Fatal(err)
	}
	if !resp.Ok || resp.Error != "" {
		t.Fatalf("want ok, got %+v", resp)
	}
	var out map[string]any
	if err := json.Unmarshal([]byte(resp.PayloadJson), &out); err != nil {
		t.Fatal(err)
	}
	if out["jobs_active"].(float64) != 1 || out["echo"].(map[string]any)["days"].(float64) != 7 {
		t.Fatalf("payload not passed through: %v", out)
	}
	st, _ := s.GetNodeStatus(context.Background(), &madtomv1.SfNodeStatusRequest{})
	if len(st.AllowedMethods) == 0 || st.NodeId != "tp2" {
		t.Fatalf("status must report node id and allowed methods: %+v", st)
	}
}

func TestCallRefusesMethodsOutsideAllowlistBeforeReachingBot(t *testing.T) {
	bot := startFakeBot(t)
	s := newTestServer(t, bot.port())
	waitConnected(t, s)
	for _, m := range []string{"drop_database", "", "get_overview ", "GET_OVERVIEW", "../get_overview"} {
		resp, err := s.Call(context.Background(), &madtomv1.SfCallRequest{Method: m, PayloadJson: "{}"})
		if err != nil {
			t.Fatal(err)
		}
		if resp.Ok || resp.Error != "method_not_allowed" {
			t.Fatalf("%q: want method_not_allowed, got %+v", m, resp)
		}
	}
	time.Sleep(100 * time.Millisecond)
	if len(bot.seen()) != 0 {
		t.Fatalf("refused methods must never reach the bot, saw %v", bot.seen())
	}
}

func TestCallPayloadValidation(t *testing.T) {
	bot := startFakeBot(t)
	s := newTestServer(t, bot.port())
	waitConnected(t, s)
	resp, _ := s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "get_overview", PayloadJson: "{not json"})
	if resp.Ok || resp.Error != "bad_payload" {
		t.Fatalf("want bad_payload, got %+v", resp)
	}
	big := `{"x":"` + strings.Repeat("a", 1<<20) + `"}`
	resp, _ = s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "get_overview", PayloadJson: big})
	if resp.Ok || resp.Error != "payload_too_large" {
		t.Fatalf("want payload_too_large, got %+v", resp)
	}
	resp, _ = s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "get_overview", PayloadJson: ""})
	if !resp.Ok {
		t.Fatalf("empty payload means {}: %+v", resp)
	}
}

func TestCallBotErrorAndTimeout(t *testing.T) {
	bot := startFakeBot(t)
	s := newTestServer(t, bot.port())
	waitConnected(t, s)
	resp, _ := s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "cancel_job", PayloadJson: `{"job_id":"J-1"}`})
	if resp.Ok || !strings.Contains(resp.Error, "job not found") {
		t.Fatalf("want bot error text, got %+v", resp)
	}
	start := time.Now()
	resp, _ = s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "list_jobs", PayloadJson: "{}", TimeoutMs: 200})
	if resp.Ok || resp.Error != "timeout" {
		t.Fatalf("want timeout, got %+v", resp)
	}
	if time.Since(start) > 1500*time.Millisecond {
		t.Fatal("timeout_ms must be honoured")
	}
}

func TestCallTimeoutIsCapped(t *testing.T) {
	if got := effectiveTimeout(0); got != 30*time.Second {
		t.Fatalf("0 -> 30s, got %v", got)
	}
	if got := effectiveTimeout(999_999_999); got != 120*time.Second {
		t.Fatalf("cap 120s, got %v", got)
	}
	if got := effectiveTimeout(500); got != 500*time.Millisecond {
		t.Fatalf("500ms, got %v", got)
	}
}

func TestCallWithoutBot(t *testing.T) {
	ln, _ := net.Listen("tcp", "127.0.0.1:0")
	port := ln.Addr().(*net.TCPAddr).Port
	ln.Close() // nothing listens there now
	s := newTestServer(t, port)
	resp, err := s.Call(context.Background(), &madtomv1.SfCallRequest{Method: "get_overview", PayloadJson: "{}", TimeoutMs: 300})
	if err != nil {
		t.Fatal(err)
	}
	if resp.Ok || resp.Error != "bot_unavailable" {
		t.Fatalf("want bot_unavailable, got %+v", resp)
	}
}

func TestTelemetryAndJobEventsReachStateBuffer(t *testing.T) {
	bot := startFakeBot(t)
	s := newTestServer(t, bot.port())
	waitConnected(t, s)
	tel, _ := json.Marshal(map[string]any{"timestamp_unix_nano": time.Now().UnixNano(),
		"metrics": map[string]float64{"jobs_active": 2}})
	bot.sendCh <- append([]byte{bridge.FrameKindTelemetry}, tel...)
	ev, _ := json.Marshal(map[string]any{"job_id": "J-abc", "status": "running", "stage": "note",
		"timestamp_unix_nano": time.Now().UnixNano()})
	bot.sendCh <- append([]byte{bridge.FrameKindJobEvent}, ev...)
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		samples, events := s.snapshotState()
		if len(samples) > 0 && len(events) > 0 {
			if samples[len(samples)-1].Metrics["jobs_active"] != 2 || events[len(events)-1].JobId != "J-abc" {
				t.Fatalf("wrong conversion: %+v / %+v", samples, events)
			}
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("telemetry/job event never converted into Sf messages")
}
