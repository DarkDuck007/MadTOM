package api

import (
	"net/http"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
)

func (s *Server) listPresets(w http.ResponseWriter, r *http.Request) {
	presets := s.Config.Presets
	if presets == nil {
		presets = []types.Preset{}
	}
	respond(w, http.StatusOK, presets)
}

func (s *Server) getPreset(w http.ResponseWriter, r *http.Request) {
	key := r.PathValue("key")
	for _, p := range s.Config.Presets {
		if p.Key == key {
			respond(w, http.StatusOK, p)
			return
		}
	}
	problem(w, http.StatusNotFound, "preset not found: "+key)
}
