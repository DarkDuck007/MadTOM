package api

import (
	"mime"
	"net/http"
	"os"
	"path/filepath"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"strings"
)

func (s *Server) download(w http.ResponseWriter, r *http.Request) {
	j, err := s.Pool.Repo.Get(r.PathValue("id"))
	if err != nil {
		jobError(w, err)
		return
	}
	d := j.Snapshot()
	if d.Status != jobs.Completed {
		problem(w, 409, "output is not available")
		return
	}
	f, err := os.Open(s.Pool.Store.Result(d.JobID, d.Spec.Container))
	if err != nil {
		problem(w, 410, "output has expired or been removed")
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		problem(w, 500, "cannot stat output")
		return
	}
	name := strings.TrimSuffix(d.Filename, filepath.Ext(d.Filename)) + "-squeezed." + d.Spec.Container
	w.Header().Set("Content-Disposition", mime.FormatMediaType("attachment", map[string]string{"filename": name}))
	w.Header().Set("Content-Type", map[string]string{"mp4": "video/mp4", "mkv": "video/x-matroska", "webm": "video/webm"}[d.Spec.Container])
	http.ServeContent(w, r, name, info.ModTime(), f)
}
