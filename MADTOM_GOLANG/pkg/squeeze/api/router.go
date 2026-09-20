package api

import (
	"encoding/json"
	"errors"
	"net/http"
	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"time"
)

type Server struct {
	Config             config.Config
	Pool               *jobs.WorkerPool
	Broker             *SSEBroker
	Encoders, Hardware []string
	HardwareStatus     string
	Started            time.Time
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/v1/health", s.health)
	mux.HandleFunc("POST /api/v1/hardware/rescan", s.rescanHardware)
	mux.HandleFunc("GET /api/v1/presets", s.listPresets)
	mux.HandleFunc("GET /api/v1/presets/{key}", s.getPreset)
	mux.HandleFunc("POST /api/v1/jobs", s.create)
	mux.HandleFunc("GET /api/v1/jobs", s.list)
	mux.HandleFunc("GET /api/v1/jobs/{id}", s.detail)
	mux.HandleFunc("DELETE /api/v1/jobs/{id}", s.cancel)
	mux.HandleFunc("POST /api/v1/jobs/{id}/start", s.start)
	mux.HandleFunc("POST /api/v1/jobs/{id}/pause", s.pause)
	mux.HandleFunc("POST /api/v1/jobs/{id}/resume", s.resume)
	mux.HandleFunc("/api/v1/jobs/{id}/upload", s.upload)
	mux.HandleFunc("GET /api/v1/jobs/{id}/download", s.download)
	mux.HandleFunc("GET /api/v1/jobs/{id}/events", s.events)
	return s.middleware(mux)
}
func respond(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func problem(w http.ResponseWriter, status int, message string) {
	respond(w, status, map[string]string{"error": message})
}
func jobError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, jobs.ErrNotFound):
		problem(w, 404, err.Error())
	case errors.Is(err, jobs.ErrQueueFull):
		w.Header().Set("Retry-After", "2")
		problem(w, 503, err.Error())
	case errors.Is(err, jobs.ErrConflict):
		problem(w, 409, err.Error())
	default:
		problem(w, 500, err.Error())
	}
}
