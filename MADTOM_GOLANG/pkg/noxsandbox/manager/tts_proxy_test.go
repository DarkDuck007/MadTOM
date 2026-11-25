package manager

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"path/filepath"
	"testing"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/internal/fakevm"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

func roundTripTTS(t *testing.T, mgr *Manager, spec vmm.Spec, payload map[string]any) map[string]any {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		serverConn, err := ln.Accept()
		if err == nil {
			mgr.HandleTTSConn(context.Background(), spec, serverConn)
		}
	}()
	clientConn, err := net.Dial("tcp", ln.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer clientConn.Close()

	raw, err := json.Marshal(payload)
	if err != nil {
		t.Fatal(err)
	}
	var hdr [4]byte
	binary.BigEndian.PutUint32(hdr[:], uint32(len(raw)))
	if _, err := clientConn.Write(hdr[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := clientConn.Write(raw); err != nil {
		t.Fatal(err)
	}
	if _, err := io.ReadFull(clientConn, hdr[:]); err != nil {
		t.Fatal(err)
	}
	respBytes := make([]byte, binary.BigEndian.Uint32(hdr[:]))
	if _, err := io.ReadFull(clientConn, respBytes); err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	if err := json.Unmarshal(respBytes, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func TestTTSProxyIsolationIdentityAndCPUAccounting(t *testing.T) {
	dir := t.TempDir()
	ttsSock := filepath.Join(dir, "tts.sock")
	ln, err := net.Listen("unix", ttsSock)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()

	var seenChatID float64
	var seenRequester string
	mux := http.NewServeMux()
	mux.HandleFunc("/v1/voices", func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(map[string]any{"ok": true, "voices": []string{"piper:fa-mana"}})
	})
	mux.HandleFunc("/v1/synthesize", func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		seenChatID, _ = body["chat_id"].(float64)
		seenRequester, _ = body["requester"].(string)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"ok": true, "format": "wav", "sample_rate": 24000, "duration_s": 1.0,
			"audio_b64": "UklGRg==", "cpu_ms": 1500,
		})
	})
	srv := &http.Server{Handler: mux}
	go srv.Serve(ln)
	defer srv.Close()

	sandboxCfg := config.Defaults()
	sandboxCfg.Enabled = true
	sandboxCfg.TTS.Enabled = true
	eff := &config.Effective{
		Host: config.Host{
			StateDir:      dir,
			TTSSocketPath: ttsSock,
			TTSVsockPort:  53,
		},
		Sandbox: sandboxCfg,
	}
	mgr, err := New(eff, Options{Backend: fakevm.New()})
	if err != nil {
		t.Fatal(err)
	}

	// 1. RolePre and RoleInspector MUST be rejected
	for _, role := range []vmm.Role{vmm.RolePre, vmm.RoleInspector} {
		res := roundTripTTS(t, mgr, vmm.Spec{ID: "vm-denied", ChatID: 777, Role: role}, map[string]any{"op": "voices"})
		if res["ok"] != false || res["error"] != "forbidden" {
			t.Fatalf("expected forbidden for role %s, got %v", role, res)
		}
	}

	// 2. RoleChat synthesizes, stamps host chat_id (guest cannot send unknown chat_id field), and charges CPU
	chatSpec := vmm.Spec{ID: "vm-chat-1", ChatID: 999, Role: vmm.RoleChat}
	badSpoof := roundTripTTS(t, mgr, chatSpec, map[string]any{"op": "synthesize", "text": "hi", "chat_id": 123})
	if badSpoof["ok"] != false || badSpoof["error"] != "bad_request" {
		t.Fatalf("expected unknown field chat_id rejected, got %v", badSpoof)
	}

	okRes := roundTripTTS(t, mgr, chatSpec, map[string]any{"op": "synthesize", "text": "سلام", "voice": "fa-mana"})
	if okRes["ok"] != true {
		t.Fatalf("expected ok synthesis, got %v", okRes)
	}
	if int64(seenChatID) != 999 || seenRequester != "vm:chat:999" {
		t.Fatalf("unexpected stamped identity: chat_id=%v requester=%q", seenChatID, seenRequester)
	}

	mgr.mu.Lock()
	charged := mgr.chat(999).meta.CPUSeconds
	mgr.mu.Unlock()
	if charged < 1.49 {
		t.Fatalf("expected >=1.5s CPU charged to chat 999, got %v", charged)
	}

	// 3. Verify spec.EnableTTS is true for chat/temp and false for pre/inspector
	if !mgr.spec(vmm.RoleChat, 1, "v1", sandboxCfg).EnableTTS {
		t.Fatal("expected EnableTTS=true for chat")
	}
	if !mgr.spec(vmm.RoleTemp, 1, "v1", sandboxCfg).EnableTTS {
		t.Fatal("expected EnableTTS=true for temp")
	}
	if mgr.spec(vmm.RolePre, 1, "v1", sandboxCfg).EnableTTS {
		t.Fatal("expected EnableTTS=false for pre")
	}
	if mgr.spec(vmm.RoleInspector, 1, "v1", sandboxCfg).EnableTTS {
		t.Fatal("expected EnableTTS=false for inspector")
	}
}
