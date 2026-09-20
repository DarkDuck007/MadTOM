package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
)

func TestPresetsEndpoints(t *testing.T) {
	crfVal := 22
	testPresets := []types.Preset{
		{
			Key:         "fast1080",
			Title:       "Fast 1080p30",
			Category:    "General",
			Description: "Standard H.264 profile",
			Tag:         "DEFAULT",
			Spec: types.JobSpec{
				VideoCodec: "libx264",
				CRF:        &crfVal,
				Preset:     "medium",
				Container:  "mp4",
				AudioCodec: "aac",
			},
		},
		{
			Key:         "discord8mb",
			Title:       "Discord 8MB Target",
			Category:    "Web",
			Description: "Low bitrate web share",
			Spec: types.JobSpec{
				VideoCodec: "libx264",
				Preset:     "fast",
				Container:  "mp4",
				AudioCodec: "aac",
			},
		},
	}

	cfg := config.Config{
		Presets: testPresets,
	}
	s := &Server{
		Config:  cfg,
		Started: time.Now(),
	}
	handler := s.Handler()

	// 1. GET /api/v1/presets
	req := httptest.NewRequest("GET", "/api/v1/presets", nil)
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rec.Code)
	}

	var list []types.Preset
	if err := json.NewDecoder(rec.Body).Decode(&list); err != nil {
		t.Fatal(err)
	}
	if len(list) != 2 {
		t.Fatalf("expected 2 presets, got %d", len(list))
	}
	if list[0].Key != "fast1080" || list[1].Key != "discord8mb" {
		t.Fatalf("unexpected presets in list: %+v", list)
	}

	// 2. GET /api/v1/presets/fast1080
	req = httptest.NewRequest("GET", "/api/v1/presets/fast1080", nil)
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d", rec.Code)
	}
	var p types.Preset
	if err := json.NewDecoder(rec.Body).Decode(&p); err != nil {
		t.Fatal(err)
	}
	if p.Key != "fast1080" || p.Title != "Fast 1080p30" {
		t.Fatalf("unexpected preset: %+v", p)
	}

	// 3. GET /api/v1/presets/unknown
	req = httptest.NewRequest("GET", "/api/v1/presets/unknown", nil)
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusNotFound {
		t.Fatalf("expected 404, got %d", rec.Code)
	}
}

func TestCreatePresetDefaultsAndExplicitOverrides(t *testing.T) {
	s, h := fixture(t)
	q := 20
	s.Config.Presets = []types.Preset{{Key: "filtered", Spec: types.JobSpec{
		VideoCodec: "libx264", CRF: &q, Preset: "slow", Container: "mkv", AudioCodec: "aac",
		Width: 1280, FPS: 30, Deinterlace: true, Grayscale: true, Denoise: "hqdn3d=1.5:1.5:6:6", Tune: "film", AudioChannels: 1,
	}}}
	for _, tc := range []struct {
		name, spec string
		overridden bool
	}{
		{"defaults", `{}`, false},
		{"zero and false", `{"width":0,"fps":0,"deinterlace":false,"grayscale":false,"denoise":""}`, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := request(h, "POST", "/api/v1/jobs", strings.NewReader(`{"filename":"clip.mp4","file_size":10,"preset_key":"filtered","spec":`+tc.spec+`}`), nil)
			if w.Code != 201 {
				t.Fatalf("%d: %s", w.Code, w.Body.String())
			}
			var d types.JobDetail
			if err := json.Unmarshal(w.Body.Bytes(), &d); err != nil {
				t.Fatal(err)
			}
			if d.Spec.Container != "mkv" || d.Spec.Tune != "film" || d.Spec.AudioChannels != 1 {
				t.Fatalf("lost preset fields: %+v", d.Spec)
			}
			if tc.overridden {
				if d.Spec.Width != 0 || d.Spec.FPS != 0 || d.Spec.Deinterlace || d.Spec.Grayscale || d.Spec.Denoise != "" {
					t.Fatalf("ignored overrides: %+v", d.Spec)
				}
			} else if !d.Spec.Deinterlace || !d.Spec.Grayscale || d.Spec.Denoise == "" {
				t.Fatalf("lost filters: %+v", d.Spec)
			}
		})
	}
	w := request(h, "POST", "/api/v1/jobs", strings.NewReader(`{"filename":"clip.mp4","file_size":10,"preset_key":"missing","spec":{}}`), nil)
	if w.Code != 400 {
		t.Fatalf("unknown preset accepted: %d", w.Code)
	}
}
