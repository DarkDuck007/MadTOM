package jobs

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/storage"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"sync"
	"time"
)

type Executor interface {
	Run(context.Context, types.JobSpec, string, string, func(ffmpeg.Control), func(types.Progress), func(string)) error
}
type Publisher interface {
	Publish(string, string, any)
	Forget(string)
}
type WorkerPool struct {
	Repo      *Repository
	Store     *storage.Store
	runner    Executor
	events    Publisher
	queue     chan *Job
	ctx       context.Context
	cancel    context.CancelFunc
	wg        sync.WaitGroup
	lifecycle sync.Mutex
	closed    bool
	maxUpload int64
}

func NewPool(parent context.Context, workers, capacity int, maxUpload int64, store *storage.Store, runner Executor, events Publisher) *WorkerPool {
	ctx, cancel := context.WithCancel(parent)
	p := &WorkerPool{Repo: NewRepository(), Store: store, runner: runner, events: events, queue: make(chan *Job, capacity), ctx: ctx, cancel: cancel, maxUpload: maxUpload}
	for i := 0; i < workers; i++ {
		p.wg.Add(1)
		go func() {
			defer p.wg.Done()
			for {
				select {
				case <-ctx.Done():
					return
				case j := <-p.queue:
					p.run(j)
				}
			}
		}()
	}
	return p
}
func (p *WorkerPool) Create(req types.CreateJobRequest) (types.JobDetail, error) {
	p.lifecycle.Lock()
	defer p.lifecycle.Unlock()
	if p.closed || p.ctx.Err() != nil {
		return types.JobDetail{}, ErrConflict
	}
	j, err := newJob(req, p.maxUpload)
	if err != nil {
		return types.JobDetail{}, err
	}
	if err = p.Store.Create(j.detail.JobID); err != nil {
		return types.JobDetail{}, err
	}
	p.Repo.Add(j)
	return j.Snapshot(), nil
}
func (p *WorkerPool) publish(j *Job) { p.events.Publish(j.detail.JobID, "status", j.snapshot()) }
func (p *WorkerPool) enqueue(j *Job) error {
	if p.ctx.Err() != nil {
		return ErrConflict
	}
	if j.detail.Status == Queued || j.detail.Status == Encoding {
		return nil
	}
	if j.detail.Status != AwaitingUpload || j.detail.UploadOffset != j.detail.FileSize || j.detail.SHA256 == "" {
		return ErrConflict
	}
	j.status(Queued)
	select {
	case p.queue <- j:
		p.publish(j)
		return nil
	default:
		j.status(AwaitingUpload)
		return ErrQueueFull
	}
}
func (p *WorkerPool) Start(id string) error {
	j, err := p.Repo.Get(id)
	if err != nil {
		return err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	return p.enqueue(j)
}

// Upload serializes chunks for one job, but allows other jobs and cancellation to proceed.
func (p *WorkerPool) Upload(id string, offset int64, body io.ReadCloser) (int64, error) {
	j, err := p.Repo.Get(id)
	if err != nil {
		return 0, err
	}
	j.uploadMu.Lock()
	defer j.uploadMu.Unlock()
	j.mu.Lock()
	if j.detail.Status != AwaitingUpload || j.detail.UploadOffset != offset {
		n := j.detail.UploadOffset
		j.mu.Unlock()
		return n, ErrConflict
	}
	j.uploadBody = body
	remaining := j.detail.FileSize - offset
	j.mu.Unlock()
	n, writeErr := p.Store.Append(id, offset, remaining, body)
	j.mu.Lock()
	j.uploadBody = nil
	j.detail.UploadOffset += n
	j.detail.UpdatedAt = time.Now().UTC()
	current := j.detail.UploadOffset
	if j.detail.Status == Cancelled {
		j.mu.Unlock()
		return current, ErrConflict
	}
	if writeErr != nil {
		j.mu.Unlock()
		return current, writeErr
	}
	complete := current == j.detail.FileSize
	j.mu.Unlock()
	if !complete {
		return current, nil
	}
	hash, hashErr := p.Store.Hash(id)
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.detail.Status == Cancelled {
		return current, ErrConflict
	}
	if hashErr != nil || j.expectedHash != "" && hash != j.expectedHash {
		j.status(Failed)
		j.detail.Error = "upload SHA256 verification failed"
		p.events.Publish(id, "error", j.snapshot())
		p.cleanupInput(id)
		return current, fmt.Errorf("upload SHA256 verification failed")
	}
	j.detail.SHA256 = hash
	return current, p.enqueue(j)
}

func (p *WorkerPool) run(j *Job) {
	j.mu.Lock()
	if j.detail.Status != Queued || p.ctx.Err() != nil {
		j.mu.Unlock()
		return
	}
	ctx, cancel := context.WithCancel(p.ctx)
	j.cancel = cancel
	j.status(Encoding)
	d := j.snapshot()
	p.publish(j)
	j.mu.Unlock()
	err := p.runner.Run(ctx, d.Spec, p.Store.Input(d.JobID), p.Store.Result(d.JobID, "tmp"), func(c ffmpeg.Control) {
		j.mu.Lock()
		defer j.mu.Unlock()
		if j.detail.Status == Encoding {
			j.control = c
		}
	}, func(progress types.Progress) {
		j.mu.Lock()
		defer j.mu.Unlock()
		if j.detail.Status == Encoding || j.detail.Status == Paused {
			j.detail.Progress = progress
			p.events.Publish(d.JobID, "progress", progress)
		}
	}, func(line string) { p.events.Publish(d.JobID, "log", map[string]string{"line": line}) })
	cancel()
	j.mu.Lock()
	defer j.mu.Unlock()
	j.control = nil
	j.cancel = nil
	if j.detail.Status == Cancelled {
		p.cleanupOutput(d.JobID, d.Spec.Container)
		p.cleanupInput(d.JobID)
		return
	}
	if err == nil {
		info, statErr := os.Stat(p.Store.Result(d.JobID, "tmp"))
		if statErr != nil {
			err = statErr
		} else if info.Size() == 0 {
			err = fmt.Errorf("ffmpeg produced empty output")
		} else {
			err = os.Rename(p.Store.Result(d.JobID, "tmp"), p.Store.Result(d.JobID, d.Spec.Container))
		}
	}
	p.cleanupInput(d.JobID)
	if err != nil {
		j.status(Failed)
		j.detail.Error = err.Error()
		p.cleanupOutput(d.JobID, d.Spec.Container)
		p.events.Publish(d.JobID, "error", j.snapshot())
	} else {
		j.status(Completed)
		j.detail.Progress.Progress = 100
		j.detail.Progress.ETA = "00:00:00"
		j.detail.DownloadURL = "/api/v1/jobs/" + d.JobID + "/download"
		p.events.Publish(d.JobID, "complete", j.snapshot())
	}
}
func (p *WorkerPool) Pause(id string, resume bool) error {
	j, err := p.Repo.Get(id)
	if err != nil {
		return err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.control == nil {
		return ErrConflict
	}
	if resume {
		if j.detail.Status != Paused {
			return ErrConflict
		}
		err = j.control.Resume()
		if err == nil {
			j.status(Encoding)
		}
	} else {
		if j.detail.Status != Encoding {
			return ErrConflict
		}
		err = j.control.Pause()
		if err == nil {
			j.status(Paused)
		}
	}
	if err == nil {
		p.publish(j)
	}
	return err
}
func (p *WorkerPool) Cancel(id string) error {
	j, err := p.Repo.Get(id)
	if err != nil {
		return err
	}
	j.mu.Lock()
	active := j.cancel != nil
	j.status(Cancelled)
	j.detail.DownloadURL = ""
	if j.cancel != nil {
		j.cancel()
	}
	body := j.uploadBody
	p.events.Publish(id, "cancelled", j.snapshot())
	j.mu.Unlock()
	if body != nil {
		_ = body.Close()
	}
	j.uploadMu.Lock()
	defer j.uploadMu.Unlock()
	p.cleanupInput(id)
	if !active {
		p.cleanupOutput(id, j.Snapshot().Spec.Container)
	}
	return nil
}
func (p *WorkerPool) Close() {
	p.lifecycle.Lock()
	if p.closed {
		p.lifecycle.Unlock()
		return
	}
	p.closed = true
	p.cancel()
	p.lifecycle.Unlock()
	for _, j := range p.Repo.all() {
		if !Terminal(j.Snapshot().Status) {
			_ = p.Cancel(j.Snapshot().JobID)
		}
	}
	p.wg.Wait()
}
func (p *WorkerPool) cleanupInput(id string) {
	if err := p.Store.RemoveInput(id); err != nil {
		slog.Error("scratch cleanup", "job_id", id, "error", err)
	}
}
func (p *WorkerPool) cleanupOutput(id, ext string) {
	if err := p.Store.RemoveOutput(id, ext); err != nil {
		slog.Error("output cleanup", "job_id", id, "error", err)
	}
}

func (p *WorkerPool) Sweep(before time.Time) error {
	live := map[string]bool{}
	for _, j := range p.Repo.all() {
		// Never expire a job while a chunk is being written or verified.
		if !j.uploadMu.TryLock() {
			live[j.Snapshot().JobID] = true
			continue
		}
		j.mu.Lock()
		d := j.snapshot()
		expired := d.UpdatedAt.Before(before) && (Terminal(d.Status) || d.Status == AwaitingUpload) && j.cancel == nil
		if expired {
			j.status(Cancelled)
			p.cleanupInput(d.JobID)
			p.cleanupOutput(d.JobID, d.Spec.Container)
			p.Repo.delete(d.JobID)
			p.events.Forget(d.JobID)
		} else {
			live[d.JobID] = true
		}
		j.mu.Unlock()
		j.uploadMu.Unlock()
	}
	return p.Store.SweepOrphans(before, live)
}

func IsConflict(err error) bool { return errors.Is(err, ErrConflict) || errors.Is(err, ErrQueueFull) }
