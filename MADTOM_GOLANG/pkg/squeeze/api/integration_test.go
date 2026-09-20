package api

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"github.com/DarkDuck007/madtom/pkg/squeeze/storage"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
	"testing"
	"time"
)

func TestCancelStalledHTTPUpload(t *testing.T) {
	s, h := fixture(t)
	d := register(t, h, 100, "")
	server := httptest.NewServer(h)
	defer server.Close()
	conn, err := net.DialTimeout("tcp", strings.TrimPrefix(server.URL, "http://"), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	_, err = fmt.Fprintf(conn, "PATCH %s HTTP/1.1\r\nHost: localhost\r\nTus-Resumable: 1.0.0\r\nUpload-Offset: 0\r\nContent-Type: application/offset+octet-stream\r\nContent-Length: 100\r\n\r\nabc", d.UploadURL)
	if err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(3 * time.Second)
	for {
		info, err := os.Stat(s.Pool.Store.Input(d.JobID))
		if err == nil && info.Size() == 3 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("upload never reached scratch")
		}
		time.Sleep(time.Millisecond)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, "DELETE", server.URL+"/api/v1/jobs/"+d.JobID, nil)
	resp, err := server.Client().Do(req)
	if err != nil {
		t.Fatalf("cancellation blocked on socket read: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatal(resp.Status)
	}
	if _, err := os.Stat(s.Pool.Store.Input(d.JobID)); !os.IsNotExist(err) {
		t.Fatal("cancelled upload retained scratch")
	}
}

func fixture(t *testing.T) (*Server, http.Handler) {
	t.Helper()
	root := t.TempDir()
	store, err := storage.New(filepath.Join(root, "scratch"), filepath.Join(root, "out"))
	if err != nil {
		t.Fatal(err)
	}
	b := NewBroker()
	p := jobs.NewPool(context.Background(), 1, 8, 10<<20, store, ffmpeg.Runner{FFmpeg: "ffmpeg", FFprobe: "ffprobe"}, b)
	t.Cleanup(p.Close)
	s := &Server{Config: config.Config{Workers: 1, MaxUpload: 10 << 20, UploadTimeout: time.Minute, NodeID: "test"}, Pool: p, Broker: b, Encoders: []string{"libx264", "libx265", "aac"}, Hardware: []string{}, Started: time.Now()}
	return s, s.Handler()
}
func request(h http.Handler, method, path string, body io.Reader, headers map[string]string) *httptest.ResponseRecorder {
	r := httptest.NewRequest(method, path, body)
	for k, v := range headers {
		r.Header.Set(k, v)
	}
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	return w
}
func register(t *testing.T, h http.Handler, size int, hash string) types.JobDetail {
	t.Helper()
	data, _ := json.Marshal(types.CreateJobRequest{Filename: "clip.mp4", FileSize: int64(size), SHA256: hash, Spec: types.JobSpec{VideoCodec: "libx264", Preset: "ultrafast"}})
	w := request(h, "POST", "/api/v1/jobs", bytes.NewReader(data), nil)
	if w.Code != 201 {
		t.Fatalf("create %d: %s", w.Code, w.Body.String())
	}
	var d types.JobDetail
	if err := json.Unmarshal(w.Body.Bytes(), &d); err != nil {
		t.Fatal(err)
	}
	return d
}
func tus(offset int) map[string]string {
	return map[string]string{"Tus-Resumable": "1.0.0", "Upload-Offset": fmt.Sprint(offset), "Content-Type": "application/offset+octet-stream"}
}

func TestUploadProtocol(t *testing.T) {
	_, h := fixture(t)
	d := register(t, h, 10, "")
	url := d.UploadURL
	checks := []struct {
		method  string
		headers map[string]string
		body    string
		status  int
		offset  string
	}{
		{"HEAD", nil, "", 412, ""},
		{"HEAD", tus(0), "", 200, "0"},
		{"PATCH", map[string]string{"Tus-Resumable": "1.0.0"}, "abc", 415, "0"},
		{"PATCH", tus(1), "abc", 409, "0"},
		{"PATCH", tus(0), "abc", 204, "3"},
		{"HEAD", tus(0), "", 200, "3"},
		{"PATCH", tus(3), "way too much data", 413, "3"},
		{"PATCH", tus(0), "abc", 409, "3"},
		{"POST", map[string]string{"Upload-Offset": "3"}, "def", 204, "6"},
		{"POST", map[string]string{"X-HTTP-Method-Override": "PATCH", "Tus-Resumable": "1.0.0", "Upload-Offset": "6", "Content-Type": "application/offset+octet-stream"}, "gh", 204, "8"},
	}
	for i, c := range checks {
		w := request(h, c.method, url, strings.NewReader(c.body), c.headers)
		if w.Code != c.status || w.Header().Get("Upload-Offset") != c.offset {
			t.Fatalf("case %d: %d offset %q body %s", i, w.Code, w.Header().Get("Upload-Offset"), w.Body.String())
		}
	}
	w := request(h, "OPTIONS", url, nil, nil)
	if w.Code != 204 || w.Header().Get("Tus-Version") != "1.0.0" || w.Header().Get("Tus-Extension") != "" {
		t.Fatal(w)
	}
	w = request(h, "GET", "/api/v1/jobs/"+d.JobID+"/download", nil, nil)
	if w.Code != 409 {
		t.Fatal(w.Code)
	}
	w = request(h, "DELETE", "/api/v1/jobs/"+d.JobID, nil, nil)
	if w.Code != 200 {
		t.Fatal(w.Code)
	}
	w = request(h, "PATCH", url, strings.NewReader("ij"), tus(8))
	if w.Code != 409 {
		t.Fatal(w.Code)
	}
}
func TestAPIValidationAndAuth(t *testing.T) {
	s, _ := fixture(t)
	s.Config.Token = "secret"
	h := s.Handler()
	if w := request(h, "GET", "/api/v1/health", nil, nil); w.Code != 401 {
		t.Fatal(w.Code)
	}
	headers := map[string]string{"Authorization": "Bearer secret"}
	if w := request(h, "GET", "/api/v1/health", nil, headers); w.Code != 200 {
		t.Fatal(w.Code)
	}
	for _, body := range []string{`{}`, `{"filename":"../evil","file_size":3}`, `{"filename":"a","file_size":3,"spec":{"video_codec":"unknown"}}`, `{"filename":"a","file_size":3,"unknown":true}`, `{"filename":"a","file_size":3} {}`} {
		if w := request(h, "POST", "/api/v1/jobs", strings.NewReader(body), headers); w.Code != 400 {
			t.Fatalf("%s -> %d", body, w.Code)
		}
	}
	if w := request(h, "GET", "/api/v1/jobs/missing", nil, headers); w.Code != 404 {
		t.Fatal(w.Code)
	}
}

func TestRealTranscodeEndToEnd(t *testing.T) {
	for _, binary := range []string{"ffmpeg", "ffprobe"} {
		if _, err := exec.LookPath(binary); err != nil {
			t.Skip(binary + " not installed")
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	input := filepath.Join(t.TempDir(), "input.mp4")
	cmd := exec.CommandContext(ctx, "ffmpeg", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1", "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", input)
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("fixture encode: %v %s", err, out)
	}
	data, err := os.ReadFile(input)
	if err != nil {
		t.Fatal(err)
	}
	sum := sha256.Sum256(data)
	s, h := fixture(t)
	d := register(t, h, len(data), hex.EncodeToString(sum[:]))
	half := len(data) / 2
	if w := request(h, "PATCH", d.UploadURL, bytes.NewReader(data[:half]), tus(0)); w.Code != 204 {
		t.Fatalf("first chunk %d %s", w.Code, w.Body.String())
	}
	if w := request(h, "HEAD", d.UploadURL, nil, tus(0)); w.Header().Get("Upload-Offset") != fmt.Sprint(half) {
		t.Fatal(w.Header())
	}
	// Subscribe before the final chunk, exercising live SSE through terminal completion.
	eventRequest := httptest.NewRequest("GET", d.EventsURL, nil).WithContext(ctx)
	stream := httptest.NewRecorder()
	streamDone := make(chan struct{})
	go func() { h.ServeHTTP(stream, eventRequest); close(streamDone) }()
	if w := request(h, "PATCH", d.UploadURL, bytes.NewReader(data[half:]), tus(half)); w.Code != 204 {
		t.Fatalf("final chunk %d %s", w.Code, w.Body.String())
	}
	select {
	case <-streamDone:
	case <-ctx.Done():
		t.Fatal("SSE did not finish")
	}
	j, _ := s.Pool.Repo.Get(d.JobID)
	result := j.Snapshot()
	if result.Status != jobs.Completed {
		t.Fatalf("encode failed: %+v\n%s", result, stream.Body.String())
	}
	if !strings.Contains(stream.Body.String(), "event: complete") {
		t.Fatal(stream.Body.String())
	}
	w := request(h, "GET", result.DownloadURL, nil, nil)
	if w.Code != 200 || w.Body.Len() == 0 {
		t.Fatalf("download %d", w.Code)
	}
	full := append([]byte(nil), w.Body.Bytes()...)
	w = request(h, "GET", result.DownloadURL, nil, map[string]string{"Range": "bytes=0-31"})
	if w.Code != 206 || !bytes.Equal(w.Body.Bytes(), full[:32]) || w.Header().Get("Accept-Ranges") != "bytes" {
		t.Fatalf("range %d %s", w.Code, w.Header())
	}
	w = request(h, "GET", result.DownloadURL, nil, map[string]string{"Range": fmt.Sprintf("bytes=%d-", len(full)+100)})
	if w.Code != 416 {
		t.Fatal(w.Code)
	}
	if _, err := os.Stat(s.Pool.Store.Input(d.JobID)); !os.IsNotExist(err) {
		t.Fatal("scratch survived completion")
	}
	// Reconnection gets a current terminal snapshot, without needing event history.
	w = request(h, "GET", d.EventsURL, nil, map[string]string{"Last-Event-ID": "1"})
	if !strings.Contains(w.Body.String(), "event: complete") {
		t.Fatal(w.Body.String())
	}
	if w = request(h, "DELETE", "/api/v1/jobs/"+d.JobID, nil, nil); w.Code != 200 {
		t.Fatal(w.Code)
	}
	if _, err := os.Stat(s.Pool.Store.Result(d.JobID, result.Spec.Container)); !os.IsNotExist(err) {
		t.Fatal("cancel did not remove output")
	}
}

func TestInvalidMediaFailsAndCleansScratch(t *testing.T) {
	if _, err := exec.LookPath("ffprobe"); err != nil {
		t.Skip("ffprobe not installed")
	}
	s, h := fixture(t)
	d := register(t, h, 3, "")
	w := request(h, "POST", d.UploadURL, strings.NewReader("bad"), nil)
	if w.Code != 204 {
		t.Fatal(w.Code)
	}
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		j, _ := s.Pool.Repo.Get(d.JobID)
		if j.Snapshot().Status == jobs.Failed {
			if _, err := os.Stat(s.Pool.Store.Input(d.JobID)); !os.IsNotExist(err) {
				t.Fatal("scratch remains")
			}
			return
		}
		time.Sleep(time.Millisecond * 10)
	}
	t.Fatal("invalid media did not fail")
}

func TestRescanHardwareEndpoint(t *testing.T) {
	s, h := fixture(t)
	w := request(h, "POST", "/api/v1/hardware/rescan", nil, nil)
	if w.Code != 200 {
		t.Fatalf("expected 200, got %d: %s", w.Code, w.Body.String())
	}
	var health types.Health
	if err := json.Unmarshal(w.Body.Bytes(), &health); err != nil {
		t.Fatalf("unmarshal health: %v", err)
	}
	if len(health.Encoders) == 0 {
		t.Fatal("expected encoders in health response")
	}
	if s.HardwareStatus == "" {
		t.Fatal("expected non-empty hardware status after rescan")
	}
}
