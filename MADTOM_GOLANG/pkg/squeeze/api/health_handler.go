package api

import (
	"context"
	"net/http"
	"os"
	"runtime"
	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strconv"
	"strings"
	"time"
)

func (s *Server) health(w http.ResponseWriter, r *http.Request) {
	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)
	hwStatus := s.HardwareStatus
	if hwStatus == "" {
		hwStatus = "compiled_encoders_only; device availability is checked when encoding"
	}
	h := types.Health{Status: "ok", Version: config.Version, NodeID: s.Config.NodeID, UptimeSeconds: time.Since(s.Started).Seconds(), CPUCores: runtime.NumCPU(), ProcessMemoryBytes: mem.Sys, MaxConcurrent: s.Config.Workers, Jobs: map[string]int{}, Encoders: s.Encoders, HardwareEncoders: s.Hardware, HardwareStatus: hwStatus, PauseSupported: ffmpeg.PauseSupported}
	for _, j := range s.Pool.Repo.List(nil) {
		h.Jobs[j.Status]++
	}
	if data, err := os.ReadFile("/proc/loadavg"); err == nil {
		f := strings.Fields(string(data))
		if len(f) > 0 {
			if n, e := strconv.ParseFloat(f[0], 64); e == nil {
				h.LoadAverage = &n
			}
		}
	}
	if data, err := os.ReadFile("/proc/meminfo"); err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			f := strings.Fields(line)
			if len(f) < 2 {
				continue
			}
			n, _ := strconv.ParseUint(f[1], 10, 64)
			switch f[0] {
			case "MemTotal:":
				h.SystemMemoryBytes = n * 1024
			case "MemAvailable:":
				h.AvailableMemoryBytes = n * 1024
			}
		}
	}
	respond(w, 200, h)
}

func (s *Server) rescanHardware(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()
	bin := s.Config.FFmpeg
	if bin == "" {
		bin = "ffmpeg"
	}
	encoders, hardware, status, err := ffmpeg.ProbeHardware(ctx, bin, s.Config.Scratch, true)
	if err != nil {
		problem(w, 500, "hardware rescan failed: "+err.Error())
		return
	}
	s.Encoders = encoders
	s.Hardware = hardware
	s.HardwareStatus = status
	s.health(w, r)
}
