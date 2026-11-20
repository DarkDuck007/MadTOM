package api

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"os/user"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/internal/fakevm"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/manager"
)

func serve(t *testing.T, allowed []uint32, maxFrame int) (string, *manager.Manager) {
	t.Helper()
	if os.Getuid() == 0 {
		t.Skip("peer-credential tests need a non-root uid")
	}
	dir := t.TempDir()
	images := filepath.Join(dir, "images")
	_ = os.MkdirAll(filepath.Join(images, "2026.11.20-1"), 0o755)
	_ = os.WriteFile(filepath.Join(images, "2026.11.20-1", "rootfs.ext4"), []byte("r"), 0o644)
	_ = os.Symlink("2026.11.20-1", filepath.Join(images, "current"))
	group, err := user.LookupGroupId(itoa(os.Getgid()))
	if err != nil {
		t.Fatal(err)
	}
	host := config.Host{StateDir: filepath.Join(dir, "state"), ImagesDir: images, SocketPath: filepath.Join(dir, "api.sock"),
		SocketGroup: group.Name, AllowedUIDs: allowed, Ceilings: config.Ceilings{MaxFrameBytes: maxFrame}}
	sb := config.Defaults()
	sb.Enabled, sb.AllowAllChats, sb.DiskWatermarkPercent = true, true, 100
	sb.TempVM.PoolSize, sb.PreVM.PoolSize = 0, 0
	cfg := &config.Effective{Host: host, Sandbox: sb}
	m, err := manager.New(cfg, manager.Options{Backend: fakevm.New(), Mkfs: func(p string, size int64) error {
		f, err := os.Create(p)
		if err != nil {
			return err
		}
		defer f.Close()
		return f.Truncate(size)
	}})
	if err != nil {
		t.Fatal(err)
	}
	ln, err := Listen(host)
	if err != nil {
		t.Fatal(err)
	}
	if info, err := os.Stat(host.SocketPath); err != nil || info.Mode().Perm() != 0o660 {
		t.Fatalf("socket mode: %v %v", info.Mode(), err)
	}
	srv := &http.Server{Handler: NewHandler(m, cfg), ConnContext: ConnContext}
	go srv.Serve(ln)
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(ctx)
		_ = m.Close(ctx)
	})
	return host.SocketPath, m
}

func itoa(v int) string { b, _ := json.Marshal(v); return string(b) }

func client(sock string) *http.Client {
	return &http.Client{Timeout: 10 * time.Second, Transport: &http.Transport{DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, "unix", sock)
	}}}
}

func TestPeerCredentialsGateTheSocket(t *testing.T) {
	sock, _ := serve(t, []uint32{}, 1<<20) // our uid is not allowed (and not root)
	if _, err := client(sock).Get("http://x/healthz"); err == nil {
		t.Fatal("a non-allowed uid got an HTTP response")
	}
	sock, _ = serve(t, []uint32{uint32(os.Getuid())}, 1<<20)
	resp, err := client(sock).Get("http://x/healthz")
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("allowed uid refused: %v %v", resp, err)
	}
	resp.Body.Close()
}

func TestExecRoundTripAndBodyLimits(t *testing.T) {
	sock, _ := serve(t, []uint32{uint32(os.Getuid())}, 64<<10)
	c := client(sock)
	body := `{"chat_id": -5, "vm": "chat", "session": "main", "kind": "python", "code": "print:hello"}`
	resp, err := c.Post("http://x/v1/exec", "application/json", strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != 200 || !strings.Contains(string(raw), `"stdout":"hello\n"`) {
		t.Fatalf("exec: %d %s", resp.StatusCode, raw)
	}

	big := `{"chat_id": -5, "code": "` + strings.Repeat("a", 200<<10) + `"}`
	resp, err = c.Post("http://x/v1/exec", "application/json", strings.NewReader(big))
	if err != nil {
		t.Fatal(err)
	}
	raw, _ = io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode < 400 || !strings.Contains(string(raw), "too_large") {
		t.Fatalf("oversized body accepted: %d %s", resp.StatusCode, raw)
	}

	// Unknown fields (e.g. a smuggled host path) are rejected, not ignored.
	resp, err = c.Post("http://x/v1/exec", "application/json", bytes.NewReader([]byte(`{"chat_id": -5, "code": "x", "host_path": "/etc/shadow"}`)))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("unknown field accepted: %d", resp.StatusCode)
	}
}

func TestBodylessActionIsAccepted(t *testing.T) {
	sock, _ := serve(t, []uint32{uint32(os.Getuid())}, 64<<10)
	c := client(sock)
	resp, err := c.Post("http://x/v1/exec", "application/json", strings.NewReader(`{"chat_id": -5, "vm": "chat", "session": "main", "code": "x = 1"}`))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	resp, err = c.Post("http://x/v1/chats/-5/hibernate", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("body-less hibernate refused: %d %s", resp.StatusCode, raw)
	}
}

func TestGrantEndpoints(t *testing.T) {
	sock, m := serve(t, []uint32{uint32(os.Getuid())}, 64<<10)
	c := client(sock)
	put := func(body string) int {
		req, _ := http.NewRequest(http.MethodPut, "http://x/v1/chats/1998/grant", strings.NewReader(body))
		resp, err := c.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		return resp.StatusCode
	}
	if code := put(`{"enabled": true, "by": "user:1"}`); code != 200 || !m.Granted(1998) {
		t.Fatalf("grant failed: %d", code)
	}
	resp, err := c.Get("http://x/v1/grants")
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if !strings.Contains(string(raw), `"chat_id":1998`) || !strings.Contains(string(raw), `"granted_by":"user:1"`) {
		t.Fatalf("grants listing: %s", raw)
	}
	if code := put(`{"enabled": false}`); code != 200 || m.Granted(1998) {
		t.Fatalf("revoke failed: %d", code)
	}
	if code := put(`{"enabled": true, "chat_id": 5}`); code != 400 {
		t.Fatalf("unknown field accepted: %d", code)
	}
}
