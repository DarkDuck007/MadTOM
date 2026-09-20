package jobs

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/storage"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
	"sync"
	"testing"
	"time"
)

type silentPublisher struct{}

type interruptedReader struct{}

func (interruptedReader) Read([]byte) (int, error) { return 0, io.ErrUnexpectedEOF }

func TestInterruptedUploadResumes(t *testing.T) {
	p, r := testPool(t, 1, 1)
	d, err := p.Create(types.CreateJobRequest{Filename: "resume.mp4", FileSize: 6})
	if err != nil {
		t.Fatal(err)
	}
	partial := io.NopCloser(io.MultiReader(strings.NewReader("abc"), interruptedReader{}))
	n, err := p.Upload(d.JobID, 0, partial)
	if n != 3 || !errors.Is(err, io.ErrUnexpectedEOF) {
		t.Fatalf("offset=%d error=%v", n, err)
	}
	j, _ := p.Repo.Get(d.JobID)
	if got := j.Snapshot(); got.Status != AwaitingUpload || got.UploadOffset != 3 {
		t.Fatal(got)
	}
	if n, err = p.Upload(d.JobID, 3, io.NopCloser(strings.NewReader("def"))); n != 6 || err != nil {
		t.Fatalf("offset=%d error=%v", n, err)
	}
	if started(t, r) != d.JobID {
		t.Fatal("resumed upload not queued")
	}
	r.release <- struct{}{}
	awaitStatus(t, p, d.JobID, Completed)
}

func (silentPublisher) Publish(string, string, any) {}
func (silentPublisher) Forget(string)               {}

type fakeControl struct{}

func (fakeControl) Pause() error  { return nil }
func (fakeControl) Resume() error { return nil }

type blockingRunner struct {
	started     chan string
	release     chan struct{}
	mu          sync.Mutex
	active, max int
}

func (r *blockingRunner) Run(ctx context.Context, _ types.JobSpec, input, output string, ready func(ffmpeg.Control), progress func(types.Progress), _ func(string)) error {
	r.mu.Lock()
	r.active++
	if r.active > r.max {
		r.max = r.active
	}
	r.mu.Unlock()
	defer func() { r.mu.Lock(); r.active--; r.mu.Unlock() }()
	ready(fakeControl{})
	progress(types.Progress{Progress: 25})
	r.started <- strings.TrimSuffix(filepath.Base(input), ".part")
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-r.release:
		return os.WriteFile(output, []byte("encoded"), 0600)
	}
}
func testPool(t *testing.T, workers, queue int) (*WorkerPool, *blockingRunner) {
	t.Helper()
	root := t.TempDir()
	s, err := storage.New(filepath.Join(root, "scratch"), filepath.Join(root, "out"))
	if err != nil {
		t.Fatal(err)
	}
	r := &blockingRunner{started: make(chan string, 20), release: make(chan struct{}, 20)}
	p := NewPool(context.Background(), workers, queue, 1024, s, r, silentPublisher{})
	t.Cleanup(p.Close)
	return p, r
}
func createUpload(t *testing.T, p *WorkerPool) string {
	t.Helper()
	d, e := p.Create(types.CreateJobRequest{Filename: "source.mp4", FileSize: 3})
	if e != nil {
		t.Fatal(e)
	}
	if _, e = p.Upload(d.JobID, 0, io.NopCloser(strings.NewReader("abc"))); e != nil {
		t.Fatal(e)
	}
	return d.JobID
}
func started(t *testing.T, r *blockingRunner) string {
	t.Helper()
	select {
	case id := <-r.started:
		return id
	case <-time.After(3 * time.Second):
		t.Fatal("worker never started")
		return ""
	}
}
func awaitStatus(t *testing.T, p *WorkerPool, id, status string) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		j, e := p.Repo.Get(id)
		if e != nil {
			t.Fatal(e)
		}
		if j.Snapshot().Status == status {
			return
		}
		time.Sleep(time.Millisecond)
	}
	j, _ := p.Repo.Get(id)
	t.Fatalf("wanted %s got %+v", status, j.Snapshot())
}

func TestFIFOAndPauseResume(t *testing.T) {
	p, r := testPool(t, 1, 4)
	first := createUpload(t, p)
	if started(t, r) != first {
		t.Fatal("wrong first job")
	}
	second := createUpload(t, p)
	third := createUpload(t, p)
	if err := p.Pause(first, false); err != nil {
		t.Fatal(err)
	}
	awaitStatus(t, p, first, Paused)
	if err := p.Pause(first, true); err != nil {
		t.Fatal(err)
	}
	r.release <- struct{}{}
	if started(t, r) != second {
		t.Fatal("FIFO broken")
	}
	r.release <- struct{}{}
	if started(t, r) != third {
		t.Fatal("FIFO broken")
	}
	r.release <- struct{}{}
	awaitStatus(t, p, third, Completed)
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.max != 1 {
		t.Fatal(r.max)
	}
	if _, err := os.Stat(p.Store.Input(first)); !os.IsNotExist(err) {
		t.Fatal("scratch not cleaned")
	}
}
func TestConcurrencyAndCancellation(t *testing.T) {
	p, r := testPool(t, 2, 4)
	a := createUpload(t, p)
	b := createUpload(t, p)
	started(t, r)
	started(t, r)
	c := createUpload(t, p)
	if err := p.Cancel(c); err != nil {
		t.Fatal(err)
	}
	if err := p.Cancel(a); err != nil {
		t.Fatal(err)
	}
	r.release <- struct{}{}
	awaitStatus(t, p, b, Completed)
	p.Close()
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.max != 2 || r.active != 0 {
		t.Fatalf("max=%d active=%d", r.max, r.active)
	}
	awaitStatus(t, p, a, Cancelled)
	awaitStatus(t, p, c, Cancelled)
	select {
	case id := <-r.started:
		t.Fatalf("cancelled queued job ran: %s", id)
	default:
	}
}
func TestQueueFullCanRetry(t *testing.T) {
	p, r := testPool(t, 1, 1)
	createUpload(t, p)
	started(t, r)
	createUpload(t, p)
	d, e := p.Create(types.CreateJobRequest{Filename: "full.mp4", FileSize: 3})
	if e != nil {
		t.Fatal(e)
	}
	if n, e := p.Upload(d.JobID, 0, io.NopCloser(strings.NewReader("abc"))); n != 3 || !errors.Is(e, ErrQueueFull) {
		t.Fatalf("%d %v", n, e)
	}
	r.release <- struct{}{}
	started(t, r)
	if e = p.Start(d.JobID); e != nil {
		t.Fatal(e)
	}
	if e = p.Start(d.JobID); e != nil {
		t.Fatal("start must not duplicate", e)
	}
	r.release <- struct{}{}
	if started(t, r) != d.JobID {
		t.Fatal("retry failed")
	}
	r.release <- struct{}{}
	awaitStatus(t, p, d.JobID, Completed)
}
func TestHashMismatch(t *testing.T) {
	p, _ := testPool(t, 1, 1)
	d, e := p.Create(types.CreateJobRequest{Filename: "bad.mp4", FileSize: 3, SHA256: strings.Repeat("0", 64)})
	if e != nil {
		t.Fatal(e)
	}
	if _, e = p.Upload(d.JobID, 0, io.NopCloser(strings.NewReader("abc"))); e == nil {
		t.Fatal("bad hash accepted")
	}
	awaitStatus(t, p, d.JobID, Failed)
	if _, e = os.Stat(p.Store.Input(d.JobID)); !os.IsNotExist(e) {
		t.Fatal("bad upload retained")
	}
}
func TestCancelDuringUpload(t *testing.T) {
	p, _ := testPool(t, 1, 1)
	d, e := p.Create(types.CreateJobRequest{Filename: "partial.mp4", FileSize: 6})
	if e != nil {
		t.Fatal(e)
	}
	reader, writer := io.Pipe()
	defer writer.Close()
	done := make(chan error, 1)
	go func() { _, e := p.Upload(d.JobID, 0, reader); done <- e }()
	if _, e = writer.Write([]byte("abc")); e != nil {
		t.Fatal(e)
	}
	if e = p.Cancel(d.JobID); e != nil {
		t.Fatal(e)
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("upload cancellation blocked")
	}
	if _, e = os.Stat(p.Store.Input(d.JobID)); !os.IsNotExist(e) {
		t.Fatal("scratch retained")
	}
}
func TestRetention(t *testing.T) {
	p, _ := testPool(t, 1, 1)
	d, e := p.Create(types.CreateJobRequest{Filename: "abandoned.mp4", FileSize: 3})
	if e != nil {
		t.Fatal(e)
	}
	if e = p.Sweep(time.Now().Add(time.Hour)); e != nil {
		t.Fatal(e)
	}
	if _, e = p.Repo.Get(d.JobID); !errors.Is(e, ErrNotFound) {
		t.Fatal("job not expired")
	}
	if _, e = os.Stat(p.Store.Input(d.JobID)); !os.IsNotExist(e) {
		t.Fatal("scratch not expired")
	}
}
