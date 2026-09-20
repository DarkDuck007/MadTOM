package ffmpeg

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"sync"
	"time"
)

type Runner struct{ FFmpeg, FFprobe string }
type Control interface {
	Pause() error
	Resume() error
}
type process struct {
	mu sync.Mutex
	p  *os.Process
}

func (p *process) Pause() error  { p.mu.Lock(); defer p.mu.Unlock(); return pause(p.p) }
func (p *process) Resume() error { p.mu.Lock(); defer p.mu.Unlock(); return resume(p.p) }

func (r Runner) Run(ctx context.Context, spec types.JobSpec, input, output string, ready func(Control), progress func(types.Progress), log func(string)) error {
	duration, err := Probe(ctx, r.FFprobe, input)
	if err != nil {
		return err
	}
	args, err := Build(spec, input, output)
	if err != nil {
		return err
	}
	cmd := exec.CommandContext(ctx, r.FFmpeg, args...)
	cmd.Cancel = func() error { _ = resume(cmd.Process); return interrupt(cmd.Process) }
	cmd.WaitDelay = 3 * time.Second
	out, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	defer out.Close()
	errout, err := cmd.StderrPipe()
	if err != nil {
		return err
	}
	defer errout.Close()
	if err = cmd.Start(); err != nil {
		return err
	}
	ready(&process{p: cmd.Process})
	var wg sync.WaitGroup
	wg.Add(2)
	var parseErr, scanErr error
	var lastLine string
	go func() {
		defer wg.Done()
		parseErr = ParseProgress(out, duration, progress)
		if parseErr != nil {
			_ = cmd.Process.Kill()
		}
	}()
	go func() {
		defer wg.Done()
		scanner := bufio.NewScanner(errout)
		scanner.Buffer(make([]byte, 4096), 1<<20)
		for scanner.Scan() {
			lastLine = scanner.Text()
			log(lastLine)
		}
		scanErr = scanner.Err()
		if scanErr != nil {
			_ = cmd.Process.Kill()
		}
	}()
	// Drain both pipes before Wait closes them.
	wg.Wait()
	err = cmd.Wait()
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if err != nil {
		return fmt.Errorf("ffmpeg: %w: %s", err, lastLine)
	}
	return errors.Join(parseErr, scanErr)
}
