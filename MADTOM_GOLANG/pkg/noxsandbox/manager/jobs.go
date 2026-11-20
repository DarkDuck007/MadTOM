package manager

import (
	"context"
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"time"
)

type job struct {
	JobInfo
	ChatID       int64 `json:"chat_id"`
	result       *ExecResponse
	done         chan struct{}
	cancel       context.CancelFunc
	size         int64
	requestBytes int64
}

var jobID = regexp.MustCompile(`^J-[0-9a-f]{24}$`)

func (manager *Manager) jobsDir() string { return filepath.Join(manager.host.StateDir, "jobs") }
func (manager *Manager) saveJob(job *job) error {
	return writeJSON(filepath.Join(manager.jobsDir(), job.ID+".json"), job)
}

func (manager *Manager) loadJobs() error {
	entries, err := os.ReadDir(manager.jobsDir())
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if filepath.Ext(entry.Name()) != ".json" || !entry.Type().IsRegular() {
			continue
		}
		data, err := os.ReadFile(filepath.Join(manager.jobsDir(), entry.Name()))
		if err != nil {
			return err
		}
		var record job
		if json.Unmarshal(data, &record) != nil || !jobID.MatchString(record.ID) || entry.Name() != record.ID+".json" {
			continue
		}
		if record.State == "running" || record.State == "done" {
			record.State = "lost"
		}
		record.done = make(chan struct{})
		close(record.done)
		manager.jobs[record.ID] = &record
	}
	manager.trimJobs(0)
	return nil
}

func (manager *Manager) trimJobs(reserve int) {
	const limit = 128
	const resultBudget = 128 << 20
	var finished []*job
	var size int64
	for _, item := range manager.jobs {
		if item.State != "running" {
			finished = append(finished, item)
			size += item.size
		}
	}
	slices.SortFunc(finished, func(first, second *job) int { return first.Started.Compare(second.Started) })
	for _, item := range finished {
		if len(manager.jobs)+reserve <= limit && size <= resultBudget {
			break
		}
		delete(manager.jobs, item.ID)
		_ = os.Remove(filepath.Join(manager.jobsDir(), item.ID+".json"))
		size -= item.size
	}
}

func (manager *Manager) StartJob(request ExecRequest) (JobAccepted, error) {
	started := time.Now()
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if manager.closed {
		return JobAccepted{}, Err("vm_failed", "service is stopping")
	}
	cfg := manager.cfg.Sandbox
	if err := manager.validateExec(&request, cfg, true); err != nil {
		return JobAccepted{}, err
	}
	chat := manager.chat(request.ChatID)
	if chat.jobs >= cfg.MaxBackgroundJobsPerChat {
		return JobAccepted{}, Err("busy", "chat background job limit reached")
	}
	manager.trimJobs(1)
	if len(manager.jobs) >= 128 {
		return JobAccepted{}, Err("capacity", "background queue is full")
	}
	bytes := int64(len(request.Code))
	for _, file := range request.Inputs {
		bytes += int64(len(file.Data))
	}
	for _, path := range request.Export {
		bytes += int64(len(path))
	}
	var queued int64
	for _, job := range manager.jobs {
		queued += job.requestBytes
	}
	if queued+bytes > max(128<<20, int64(manager.host.Ceilings.MaxFrameBytes)) {
		return JobAccepted{}, Err("capacity", "background input queue is full")
	}
	created, err := manager.reserveSession(chat, &request, cfg)
	if err != nil {
		return JobAccepted{}, err
	}
	ctx, cancel := context.WithCancel(manager.ctx)
	item := &job{JobInfo: JobInfo{ID: "J-" + randomID(12), State: "running", Started: manager.now()}, ChatID: chat.id, done: make(chan struct{}), cancel: cancel, requestBytes: bytes}
	if err := manager.saveJob(item); err != nil {
		cancel()
		if request.VM == "chat" {
			delete(chat.busy, request.Session)
		}
		manager.releaseUse(chat, false)
		return JobAccepted{}, err
	}
	chat.jobs++
	manager.jobs[item.ID] = item
	manager.workers.Go(func() {
		defer cancel()
		response, err := manager.execute(ctx, chat, request, cfg, created, started)
		manager.releaseSession(chat, request)
		manager.mu.Lock()
		defer manager.mu.Unlock()
		chat.jobs--
		item.requestBytes = 0
		chat.meta.LastUsed = manager.now()
		switch {
		case manager.ctx.Err() != nil:
			item.State = "lost"
		case ctx.Err() != nil:
			item.State = "cancelled"
		case err != nil:
			item.State = "failed"
		default:
			item.State, item.result = "done", &response
			item.size = int64(len(response.Stdout) + len(response.Stderr))
			for _, file := range response.Files.Files {
				item.size += int64(len(file.Data))
			}
		}
		if err := manager.saveJob(item); err != nil {
			log.Printf("nox-sandbox: persist job: %v", err)
		}
		close(item.done)
		manager.trimJobs(0)
		manager.notify()
	})
	return JobAccepted{item.ID, request.Session}, nil
}

func (manager *Manager) Job(ctx context.Context, id string, wait time.Duration) (JobResult, error) {
	manager.mu.Lock()
	item := manager.jobs[id]
	manager.mu.Unlock()
	if item == nil {
		return JobResult{}, Err("not_found", "job not found")
	}
	if wait > 0 {
		timer := time.NewTimer(wait)
		defer timer.Stop()
		select {
		case <-ctx.Done():
			return JobResult{}, Err("timeout", "job poll cancelled")
		case <-timer.C:
		case <-item.done:
		}
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	return JobResult{item.State, item.result}, nil
}

func (manager *Manager) CancelJob(id string) error {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	item := manager.jobs[id]
	if item == nil {
		return Err("not_found", "job not found")
	}
	if item.State == "running" {
		item.cancel()
	}
	return nil
}

func (manager *Manager) chatJobs(chatID int64) []JobInfo {
	result := []JobInfo{}
	for _, item := range manager.jobs {
		if item.ChatID == chatID {
			result = append(result, item.JobInfo)
		}
	}
	slices.SortFunc(result, func(first, second JobInfo) int { return second.Started.Compare(first.Started) })
	return result
}

func (manager *Manager) Jobs(chatID int64) []JobInfo {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	return manager.chatJobs(chatID)
}
