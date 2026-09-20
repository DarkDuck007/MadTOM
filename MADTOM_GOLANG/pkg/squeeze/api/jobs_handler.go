package api

import (
	"encoding/json"
	"io"
	"net/http"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
)

func (s *Server) create(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 64<<10)
	defer r.Body.Close()
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	var raw json.RawMessage
	if err := decoder.Decode(&raw); err != nil {
		problem(w, 400, "invalid job JSON: "+err.Error())
		return
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		problem(w, 400, "expected one JSON object")
		return
	}
	var req types.CreateJobRequest
	strict := json.NewDecoder(strings.NewReader(string(raw)))
	strict.DisallowUnknownFields()
	if err := strict.Decode(&req); err != nil {
		problem(w, 400, "invalid job JSON: "+err.Error())
		return
	}
	if req.PresetKey != "" {
		var defaults *types.JobSpec
		for _, p := range s.Config.Presets {
			if p.Key == req.PresetKey {
				value := p.Spec
				if value.CRF != nil {
					q := *value.CRF
					value.CRF = &q
				}
				defaults = &value
				break
			}
		}
		if defaults == nil {
			problem(w, 400, "unknown preset_key")
			return
		}
		var fields struct {
			Spec json.RawMessage `json:"spec"`
		}
		_ = json.Unmarshal(raw, &fields)
		if len(fields.Spec) > 0 {
			if err := json.Unmarshal(fields.Spec, defaults); err != nil {
				problem(w, 400, "invalid spec: "+err.Error())
				return
			}
		}
		req.Spec = *defaults
	}
	spec, err := ffmpeg.Normalize(req.Spec)
	if err != nil {
		problem(w, 400, err.Error())
		return
	}
	req.Spec = spec
	for _, codec := range []string{spec.VideoCodec, spec.AudioCodec} {
		if codec == "none" || codec == "copy" {
			continue
		}
		found := false
		for _, available := range s.Encoders {
			if codec == available {
				found = true
				break
			}
		}
		if !found {
			problem(w, 400, "encoder unavailable: "+codec)
			return
		}
	}
	d, err := s.Pool.Create(req)
	if err != nil {
		problem(w, 400, err.Error())
		return
	}
	w.Header().Set("Location", "/api/v1/jobs/"+d.JobID)
	respond(w, 201, d)
}
func (s *Server) list(w http.ResponseWriter, r *http.Request) {
	filter := map[string]bool{}
	if query := r.URL.Query().Get("status"); query != "" {
		for _, status := range strings.Split(query, ",") {
			filter[strings.TrimSpace(status)] = true
		}
	}
	respond(w, 200, s.Pool.Repo.List(filter))
}
func (s *Server) detail(w http.ResponseWriter, r *http.Request) {
	j, err := s.Pool.Repo.Get(r.PathValue("id"))
	if err != nil {
		jobError(w, err)
		return
	}
	respond(w, 200, j.Snapshot())
}
func (s *Server) cancel(w http.ResponseWriter, r *http.Request) {
	if err := s.Pool.Cancel(r.PathValue("id")); err != nil {
		jobError(w, err)
		return
	}
	s.detail(w, r)
}
func (s *Server) start(w http.ResponseWriter, r *http.Request) {
	if err := s.Pool.Start(r.PathValue("id")); err != nil {
		jobError(w, err)
		return
	}
	s.detail(w, r)
}
func (s *Server) pause(w http.ResponseWriter, r *http.Request)  { s.control(w, r, false) }
func (s *Server) resume(w http.ResponseWriter, r *http.Request) { s.control(w, r, true) }
func (s *Server) control(w http.ResponseWriter, r *http.Request, resume bool) {
	if !ffmpeg.PauseSupported {
		problem(w, 501, "pause/resume unsupported on this OS")
		return
	}
	if err := s.Pool.Pause(r.PathValue("id"), resume); err != nil {
		jobError(w, err)
		return
	}
	s.detail(w, r)
}
