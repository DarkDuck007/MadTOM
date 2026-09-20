package api

import (
	"encoding/json"
	"fmt"
	"net/http"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"time"
)

func (s *Server) events(w http.ResponseWriter, r *http.Request) {
	j, err := s.Pool.Repo.Get(r.PathValue("id"))
	if err != nil {
		jobError(w, err)
		return
	}
	if r.Method == "HEAD" {
		w.WriteHeader(405)
		return
	}
	ch, unsubscribe := s.Broker.Subscribe(r.PathValue("id"))
	defer unsubscribe()
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("X-Accel-Buffering", "no")
	controller := http.NewResponseController(w)
	send := func(e types.Event) error {
		_ = controller.SetWriteDeadline(time.Now().Add(15 * time.Second))
		data, err := json.Marshal(e.Data)
		if err != nil {
			return err
		}
		if e.ID > 0 {
			if _, err = fmt.Fprintf(w, "id: %d\n", e.ID); err != nil {
				return err
			}
		}
		if _, err = fmt.Fprintf(w, "event: %s\ndata: %s\n\n", e.Type, data); err != nil {
			return err
		}
		return controller.Flush()
	}
	defer controller.SetWriteDeadline(time.Time{})
	d := j.Snapshot()
	kind := "status"
	switch d.Status {
	case jobs.Completed:
		kind = "complete"
	case jobs.Failed:
		kind = "error"
	case jobs.Cancelled:
		kind = "cancelled"
	}
	if send(types.Event{Type: kind, Data: d}) != nil || jobs.Terminal(d.Status) {
		return
	}
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case e, ok := <-ch:
			if !ok {
				return
			}
			if send(e) != nil {
				return
			}
			if e.Type == "complete" || e.Type == "error" || e.Type == "cancelled" {
				return
			}
		case <-ticker.C:
			_ = controller.SetWriteDeadline(time.Now().Add(15 * time.Second))
			if _, err := fmt.Fprint(w, ": keepalive\n\n"); err != nil {
				return
			}
			if controller.Flush() != nil {
				return
			}
		}
	}
}
