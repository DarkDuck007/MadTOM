package manager

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

type ttsRequest struct {
	Op            string           `json:"op"`
	Lang          string           `json:"lang,omitempty"`
	Engine        string           `json:"engine,omitempty"`
	InstalledOnly *bool            `json:"installed_only,omitempty"`
	Text          string           `json:"text,omitempty"`
	Segments      json.RawMessage  `json:"segments,omitempty"`
	Voice         string           `json:"voice,omitempty"`
	Speed         *float64         `json:"speed,omitempty"`
	Format        string           `json:"format,omitempty"`
	SampleRate    *int             `json:"sample_rate,omitempty"`
	NormalizeLUFS *float64         `json:"normalize_lufs,omitempty"`
	Subtitles     string           `json:"subtitles,omitempty"`
	RefAudioB64   *string          `json:"ref_audio_b64,omitempty"`
	RefText       *string          `json:"ref_text,omitempty"`
}

var (
	ttsInflightMu sync.Mutex
	ttsInflight   = map[string]int{}
)

func acquireTTSInflight(vmID string, limit int) bool {
	ttsInflightMu.Lock()
	defer ttsInflightMu.Unlock()
	if ttsInflight[vmID] >= limit {
		return false
	}
	ttsInflight[vmID]++
	return true
}

func releaseTTSInflight(vmID string) {
	ttsInflightMu.Lock()
	defer ttsInflightMu.Unlock()
	if ttsInflight[vmID] <= 1 {
		delete(ttsInflight, vmID)
	} else {
		ttsInflight[vmID]--
	}
}

func writeTTSFrame(conn net.Conn, payload []byte) {
	var hdr [4]byte
	binary.BigEndian.PutUint32(hdr[:], uint32(len(payload)))
	_, _ = conn.Write(hdr[:])
	_, _ = conn.Write(payload)
}

func writeTTSError(conn net.Conn, code, msg string) {
	data, _ := json.Marshal(map[string]any{"ok": false, "error": code, "message": msg})
	writeTTSFrame(conn, data)
}

// HandleTTSConn proxies a single guest-initiated AF_VSOCK connection (port 53) to the host
// nox-tts daemon over its Unix domain socket, stamping trusted chat_id/role and charging CPU-ms.
func (manager *Manager) HandleTTSConn(ctx context.Context, spec vmm.Spec, conn net.Conn) {
	defer conn.Close()

	// Defense in depth: only chat and temp VMs may ever use TTS.
	if spec.Role != vmm.RoleChat && spec.Role != vmm.RoleTemp {
		writeTTSError(conn, "forbidden", "TTS is not available for this VM role")
		return
	}
	cfg := manager.Config().Sandbox
	if !cfg.TTS.Enabled || manager.host.TTSSocketPath == "" {
		writeTTSError(conn, "disabled", "TTS is disabled on this host")
		return
	}
	if !acquireTTSInflight(spec.ID, max(1, cfg.TTS.MaxInflightPerVM)) {
		writeTTSError(conn, "busy", "too many concurrent TTS requests from this VM")
		return
	}
	defer releaseTTSInflight(spec.ID)

	timeout := time.Duration(max(10, cfg.TTS.RequestTimeoutSeconds)) * time.Second
	_ = conn.SetDeadline(time.Now().Add(timeout))

	var hdr [4]byte
	if _, err := io.ReadFull(conn, hdr[:]); err != nil {
		return
	}
	length := binary.BigEndian.Uint32(hdr[:])
	maxReq := cfg.TTS.MaxRequestBytes
	if maxReq <= 0 {
		maxReq = 8 << 20
	}
	if length == 0 || int(length) > maxReq {
		writeTTSError(conn, "too_large", fmt.Sprintf("TTS request exceeds %d bytes", maxReq))
		return
	}
	raw := make([]byte, int(length))
	if _, err := io.ReadFull(conn, raw); err != nil {
		return
	}
	if !utf8.Valid(raw) {
		writeTTSError(conn, "bad_request", "invalid UTF-8")
		return
	}

	var req ttsRequest
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&req); err != nil {
		writeTTSError(conn, "bad_request", "invalid TTS request JSON")
		return
	}
	if err := dec.Decode(new(any)); err != io.EOF {
		writeTTSError(conn, "bad_request", "trailing JSON in TTS request")
		return
	}

	// Enforce per-chat daily CPU budget before forwarding synthesis.
	if req.Op == "synthesize" && spec.ChatID != 0 {
		manager.mu.Lock()
		ch := manager.chat(spec.ChatID)
		manager.rollover(ch)
		over := ch.meta.CPUSeconds >= float64(cfg.DailyCPUSecondsPerChat)
		manager.mu.Unlock()
		if over {
			writeTTSError(conn, "quota_exceeded", "daily CPU budget exceeded")
			return
		}
	}

	reqCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	client := &http.Client{
		Transport: &http.Transport{
			DisableKeepAlives: true,
			DialContext: func(c context.Context, _, _ string) (net.Conn, error) {
				return (&net.Dialer{}).DialContext(c, "unix", manager.host.TTSSocketPath)
			},
		},
	}

	var httpReq *http.Request
	var err error
	switch req.Op {
	case "status":
		httpReq, err = http.NewRequestWithContext(reqCtx, http.MethodGet, "http://nox-tts/v1/status", nil)
	case "voices":
		q := url.Values{}
		if req.Lang != "" {
			q.Set("lang", req.Lang)
		}
		if req.Engine != "" {
			q.Set("engine", req.Engine)
		}
		if req.InstalledOnly == nil || *req.InstalledOnly {
			q.Set("installed_only", "1")
		}
		target := "http://nox-tts/v1/voices"
		if enc := q.Encode(); enc != "" {
			target += "?" + enc
		}
		httpReq, err = http.NewRequestWithContext(reqCtx, http.MethodGet, target, nil)
	case "synthesize":
		// Re-encode strictly with host-stamped chat_id & requester (guest cannot spoof identity).
		body := map[string]any{
			"chat_id":   spec.ChatID,
			"requester": fmt.Sprintf("vm:%s:%d", spec.Role, spec.ChatID),
		}
		if req.Text != "" {
			body["text"] = req.Text
		}
		if len(req.Segments) > 0 {
			body["segments"] = req.Segments
		}
		if req.Engine != "" {
			body["engine"] = req.Engine
		}
		if req.Voice != "" {
			body["voice"] = req.Voice
		}
		if req.Speed != nil {
			body["speed"] = *req.Speed
		}
		if req.Format != "" {
			body["format"] = req.Format
		}
		if req.SampleRate != nil {
			body["sample_rate"] = *req.SampleRate
		}
		if req.NormalizeLUFS != nil {
			body["normalize_lufs"] = *req.NormalizeLUFS
		}
		if req.Subtitles != "" {
			body["subtitles"] = req.Subtitles
		}
		if req.RefAudioB64 != nil {
			body["ref_audio_b64"] = *req.RefAudioB64
		}
		if req.RefText != nil {
			body["ref_text"] = *req.RefText
		}
		encoded, merr := json.Marshal(body)
		if merr != nil {
			writeTTSError(conn, "bad_request", "could not encode TTS body")
			return
		}
		httpReq, err = http.NewRequestWithContext(reqCtx, http.MethodPost, "http://nox-tts/v1/synthesize", bytes.NewReader(encoded))
		if err == nil {
			httpReq.Header.Set("Content-Type", "application/json")
		}
	default:
		writeTTSError(conn, "bad_request", "unknown TTS op")
		return
	}
	if err != nil {
		writeTTSError(conn, "internal", err.Error())
		return
	}

	resp, err := client.Do(httpReq)
	if err != nil {
		writeTTSError(conn, "unavailable", "host TTS daemon is unavailable")
		return
	}
	defer resp.Body.Close()

	maxResp := int64(max(cfg.MaxExportBytes, 52<<20))
	respBytes, err := io.ReadAll(io.LimitReader(resp.Body, maxResp+1))
	if err != nil {
		writeTTSError(conn, "unavailable", "failed reading TTS response")
		return
	}
	if int64(len(respBytes)) > maxResp {
		writeTTSError(conn, "too_large", "TTS response exceeds size limit")
		return
	}

	// Charge host TTS CPU time against the chat's daily sandbox CPU budget.
	if req.Op == "synthesize" && spec.ChatID != 0 && resp.StatusCode == http.StatusOK {
		var meta struct {
			CPUMS int64 `json:"cpu_ms"`
		}
		if json.Unmarshal(respBytes, &meta) == nil && meta.CPUMS > 0 {
			manager.mu.Lock()
			ch := manager.chat(spec.ChatID)
			manager.rollover(ch)
			ch.meta.CPUSeconds += float64(meta.CPUMS) / 1000.0
			manager.persist(ch)
			manager.mu.Unlock()
		}
	}

	writeTTSFrame(conn, respBytes)
}
