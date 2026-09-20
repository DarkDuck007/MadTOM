package ffmpeg

import (
	"context"
	"errors"
	"os/exec"
	"path/filepath"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"testing"
	"time"
)

func TestCancelPausedProcess(t *testing.T) {
	if !PauseSupported {
		t.Skip("requires Unix process signals")
	}
	for _, binary := range []string{"ffmpeg", "ffprobe"} {
		if _, err := exec.LookPath(binary); err != nil {
			t.Skip(binary + " missing")
		}
	}
	root := t.TempDir()
	input := filepath.Join(root, "input.mp4")
	ctx, timeout := context.WithTimeout(context.Background(), 15*time.Second)
	defer timeout()
	if out, err := exec.CommandContext(ctx, "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=24", "-t", "1", "-c:v", "libx264", "-threads", "1", input).CombinedOutput(); err != nil {
		t.Fatalf("fixture: %v %s", err, out)
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	ready := make(chan Control, 1)
	done := make(chan error, 1)
	go func() {
		done <- (Runner{"ffmpeg", "ffprobe"}).Run(ctx, types.JobSpec{VideoCodec: "libx264"}, input, filepath.Join(root, "output.tmp"), func(c Control) {
			if err := c.Pause(); err != nil {
				cancel()
			}
			ready <- c
		}, func(types.Progress) {}, func(string) {})
	}()
	select {
	case c := <-ready:
		if err := c.Resume(); err != nil {
			t.Fatal(err)
		}
		if err := c.Pause(); err != nil {
			t.Fatal(err)
		}
	case err := <-done:
		t.Fatalf("process failed before ready: %v", err)
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("expected cancellation: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("paused ffmpeg was not terminated")
	}
}
