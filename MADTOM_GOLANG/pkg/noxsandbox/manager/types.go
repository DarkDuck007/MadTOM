package manager

import (
	"encoding/json"
	"errors"
	"log"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

type Error struct{ Code, Message string }

func (err *Error) Error() string     { return err.Message }
func Err(code, message string) error { return &Error{code, message} }

type ExecRequest struct {
	ChatID    int64         `json:"chat_id"`
	VM        vmm.Role      `json:"vm"`
	Session   string        `json:"session"`
	Kind      string        `json:"kind"`
	Code      string        `json:"code"`
	TimeoutS  int           `json:"timeout_s"`
	Inputs    []agent.Input `json:"inputs"`
	Export    []string      `json:"export"`
	Requester string        `json:"requester"`
}
type ExecResponse struct {
	OK           bool    `json:"ok"`
	ExitCode     int     `json:"exit_code"`
	Stdout       string  `json:"stdout"`
	Stderr       string  `json:"stderr"`
	Truncated    bool    `json:"truncated"`
	ResultRepr   *string `json:"result_repr"`
	Error        *string `json:"error"`
	SessionReset bool    `json:"session_reset"`
	agent.Files
	VM        ExecVM  `json:"vm"`
	Disk      Disk    `json:"disk"`
	TimingsMS Timings `json:"timings_ms"`
	Session   string  `json:"session,omitempty"`
}
type ExecVM struct {
	State        string `json:"state"`
	ImageVersion string `json:"image_version"`
	ColdStarted  bool   `json:"cold_started"`
	Restored     bool   `json:"restored"`
}
type Disk struct {
	UsedBytes  int64 `json:"used_bytes"`
	QuotaBytes int64 `json:"quota_bytes"`
}
type Timings struct {
	Queue int64 `json:"queue"`
	Start int64 `json:"start"`
	Exec  int64 `json:"exec"`
	Total int64 `json:"total"`
}

type SessionInfo struct {
	Name    string    `json:"name"`
	Busy    bool      `json:"busy"`
	Started time.Time `json:"started"`
}
type JobInfo struct {
	ID      string    `json:"job_id"`
	State   string    `json:"state"`
	Started time.Time `json:"started"`
}
type JobAccepted struct {
	ID      string `json:"job_id"`
	Session string `json:"session"`
}
type JobResult struct {
	State  string        `json:"state"`
	Result *ExecResponse `json:"result"`
}
type VMInfo struct {
	ChatID           int64         `json:"chat_id"`
	Kind             vmm.Role      `json:"kind"`
	State            string        `json:"state"`
	ImageVersion     string        `json:"image_version"`
	MemMB            int           `json:"mem_mb"`
	VCPUs            int           `json:"vcpus"`
	PID              int           `json:"pid"`
	Since            time.Time     `json:"since"`
	LastUsed         time.Time     `json:"last_used"`
	IdleDeadline     *time.Time    `json:"idle_deadline"`
	SessionDeadline  *time.Time    `json:"session_deadline"`
	DiskUsedBytes    int64         `json:"disk_used_bytes"`
	DiskQuotaBytes   int64         `json:"disk_quota_bytes"`
	SnapshotBytes    int64         `json:"snapshot_bytes"`
	Backups          int           `json:"backups"`
	CPUSecondsToday  float64       `json:"cpu_seconds_today"`
	CPUBudgetSeconds int           `json:"cpu_budget_seconds"`
	CPUResetsAt      time.Time     `json:"cpu_resets_at"`
	SnapshotQuota    int64         `json:"snapshot_quota_bytes"`
	Sessions         []SessionInfo `json:"sessions"`
	Jobs             []JobInfo     `json:"jobs"`
}

type FileList struct {
	Entries   []agent.Entry `json:"entries"`
	AsOf      *time.Time    `json:"as_of"`
	FromCache bool          `json:"from_cache"`
}
type Preview struct {
	PNG    []byte `json:"png_b64"`
	Width  int    `json:"width"`
	Height int    `json:"height"`
	Pages  int    `json:"pages"`
	Kind   string `json:"kind"`
}
type Backup struct {
	ID        string    `json:"id"`
	CreatedAt time.Time `json:"created_at"`
	Bytes     int64     `json:"bytes"`
}
type PreRequest struct {
	ChatID    int64           `json:"chat_id"`
	Code      string          `json:"code"`
	Event     json.RawMessage `json:"event"`
	State     json.RawMessage `json:"state"`
	TimeoutMS int             `json:"timeout_ms"`
}
type PreResponse struct {
	OK        bool              `json:"ok"`
	Actions   []json.RawMessage `json:"actions"`
	State     json.RawMessage   `json:"state"`
	Error     *string           `json:"error"`
	TimingsMS Timings           `json:"timings_ms"`
}

func guestError(err error) error {
	log.Printf("nox-sandbox: guest operation failed: %v", err)
	var remote *agent.RemoteError
	if errors.As(err, &remote) {
		switch remote.Message {
		case "busy":
			return Err("busy", "guest session is busy")
		case "not_found", "not found", "missing":
			return Err("not_found", "guest file not found")
		case "too_large":
			return Err("too_large", "guest file exceeds the size limit")
		case "forbidden", "read_only":
			return Err("forbidden", "guest operation was refused")
		}
	}
	return Err("vm_failed", "guest operation failed")
}
