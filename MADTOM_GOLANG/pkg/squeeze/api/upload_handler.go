package api

import (
	"errors"
	"io"
	"net/http"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"strconv"
	"time"
)

func formatInt(n int64) string { return strconv.FormatInt(n, 10) }

// Interrupt a blocked socket read before Close attempts to drain the HTTP body.
type interruptibleBody struct {
	io.ReadCloser
	interrupt func()
}

func (b interruptibleBody) Close() error {
	b.interrupt()
	return b.ReadCloser.Close()
}

func (s *Server) upload(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Tus-Resumable", "1.0.0")
	w.Header().Set("Tus-Version", "1.0.0")
	w.Header().Set("Cache-Control", "no-store")
	if r.Method != "HEAD" && r.Method != "PATCH" && r.Method != "POST" {
		w.Header().Set("Allow", "HEAD, PATCH, POST, OPTIONS")
		problem(w, 405, "method not allowed")
		return
	}
	// POST is a simple streaming alias. PATCH and HEAD implement tus core.
	if (r.Method != "POST" || r.Header.Get("Tus-Resumable") != "") && r.Header.Get("Tus-Resumable") != "1.0.0" {
		problem(w, 412, "Tus-Resumable: 1.0.0 required")
		return
	}
	j, err := s.Pool.Repo.Get(r.PathValue("id"))
	if err != nil {
		jobError(w, err)
		return
	}
	d := j.Snapshot()
	w.Header().Set("Upload-Offset", formatInt(d.UploadOffset))
	w.Header().Set("Upload-Length", formatInt(d.FileSize))
	if r.Method == "HEAD" {
		w.WriteHeader(200)
		return
	}
	if r.Method == "PATCH" && r.Header.Get("Content-Type") != "application/offset+octet-stream" {
		problem(w, 415, "Content-Type must be application/offset+octet-stream")
		return
	}
	if length := r.Header.Get("Upload-Length"); length != "" && length != formatInt(d.FileSize) {
		problem(w, 409, "Upload-Length differs from file_size")
		return
	}
	value := r.Header.Get("Upload-Offset")
	if value == "" && r.Method == "POST" {
		value = "0"
	}
	offset, err := strconv.ParseInt(value, 10, 64)
	if err != nil || offset < 0 {
		problem(w, 400, "nonnegative Upload-Offset required")
		return
	}
	if offset != d.UploadOffset || d.Status != jobs.AwaitingUpload {
		problem(w, 409, "upload offset or state mismatch")
		return
	}
	if r.ContentLength > d.FileSize-offset {
		problem(w, 413, "chunk exceeds declared file_size")
		return
	}
	controller := http.NewResponseController(w)
	_ = controller.SetReadDeadline(time.Now().Add(s.Config.UploadTimeout))
	defer controller.SetReadDeadline(time.Time{})
	r.Body = http.MaxBytesReader(w, r.Body, d.FileSize-offset+1)
	defer r.Body.Close()
	n, err := s.Pool.Upload(d.JobID, offset, interruptibleBody{r.Body, func() { _ = controller.SetReadDeadline(time.Now()) }})
	w.Header().Set("Upload-Offset", formatInt(n))
	if err != nil {
		if jobs.IsConflict(err) || errors.Is(err, jobs.ErrNotFound) {
			jobError(w, err)
		} else {
			problem(w, 400, err.Error())
		}
		return
	}
	w.WriteHeader(204)
}
