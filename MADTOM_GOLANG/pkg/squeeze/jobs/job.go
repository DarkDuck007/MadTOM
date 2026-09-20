package jobs

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"path/filepath"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
	"sync"
	"time"
)

const (
	AwaitingUpload = "awaiting_upload"
	Queued         = "queued"
	Encoding       = "encoding"
	Paused         = "paused"
	Completed      = "completed"
	Failed         = "failed"
	Cancelled      = "cancelled"
)

var ErrNotFound = fmt.Errorf("job not found")
var ErrConflict = fmt.Errorf("operation not allowed in current job state")
var ErrQueueFull = fmt.Errorf("encode queue is full; retry start later")

func Terminal(status string) bool {
	return status == Completed || status == Failed || status == Cancelled
}

type Job struct {
	mu           sync.Mutex
	uploadMu     sync.Mutex
	detail       types.JobDetail
	expectedHash string
	cancel       context.CancelFunc
	control      ffmpeg.Control
	uploadBody   io.ReadCloser
}

func newJob(req types.CreateJobRequest, max int64) (*Job, error) {
	if req.Filename == "" || len(req.Filename) > 255 || strings.ContainsAny(req.Filename, "/\\\r\n\x00") || filepath.Base(req.Filename) != req.Filename {
		return nil, fmt.Errorf("filename must be a plain filename")
	}
	if req.FileSize <= 0 || req.FileSize > max {
		return nil, fmt.Errorf("file_size must be 1..%d bytes", max)
	}
	if req.SHA256 != "" {
		b, e := hex.DecodeString(req.SHA256)
		if e != nil || len(b) != 32 {
			return nil, fmt.Errorf("sha256 must be 64 hexadecimal characters")
		}
	}
	spec, err := ffmpeg.Normalize(req.Spec)
	if err != nil {
		return nil, err
	}
	idBytes := make([]byte, 16)
	if _, err = rand.Read(idBytes); err != nil {
		return nil, err
	}
	id := hex.EncodeToString(idBytes)
	now := time.Now().UTC()
	base := "/api/v1/jobs/" + id
	return &Job{expectedHash: strings.ToLower(req.SHA256), detail: types.JobDetail{JobID: id, Status: AwaitingUpload, Filename: req.Filename, FileSize: req.FileSize, Spec: spec, CreatedAt: now, UpdatedAt: now, UploadURL: base + "/upload", EventsURL: base + "/events"}}, nil
}
func (j *Job) Snapshot() types.JobDetail { j.mu.Lock(); defer j.mu.Unlock(); return j.snapshot() }
func (j *Job) snapshot() types.JobDetail {
	d := j.detail
	if d.Spec.CRF != nil {
		v := *d.Spec.CRF
		d.Spec.CRF = &v
	}
	return d
}
func (j *Job) status(s string) { j.detail.Status = s; j.detail.UpdatedAt = time.Now().UTC() }
