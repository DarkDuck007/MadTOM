package api

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/image"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/manager"
)

const Version = "0.1.0"

type handler struct {
	manager      *manager.Manager
	maxBodyBytes int64
	health       sync.Once
	version      string
	requests     chan struct{}
	inputBytes   atomic.Int64
}

type object = map[string]any

func NewHandler(manager *manager.Manager, cfg *config.Effective) http.Handler {
	maxBytes := cfg.Host.Ceilings.MaxFrameBytes
	if maxBytes <= 0 {
		maxBytes = 128 << 20
	}
	return &handler{manager: manager, maxBodyBytes: int64(maxBytes), requests: make(chan struct{}, 64)}
}

func respond(writer http.ResponseWriter, status int, body any) {
	writer.Header().Set("Content-Type", "application/json")
	writer.WriteHeader(status)
	if err := json.NewEncoder(writer).Encode(body); err != nil && !errors.Is(err, context.Canceled) {
		log.Printf("nox-sandbox: response: %v", err)
	}
}

func failure(writer http.ResponseWriter, err error) {
	var service *manager.Error
	if !errors.As(err, &service) {
		log.Printf("nox-sandbox: %v", err)
		service = &manager.Error{Code: "internal", Message: "internal service error"}
	}
	status := map[string]int{"bad_request": 400, "forbidden": 403, "not_found": 404, "busy": 409, "capacity": 503, "quota_exceeded": 429,
		"timeout": 504, "vm_failed": 502, "too_large": 413, "disabled": 503, "internal": 500}[service.Code]
	if status == 0 {
		status = 500
	}
	respond(writer, status, object{"error": service.Code, "message": service.Message})
}

func (handler *handler) decode(writer http.ResponseWriter, request *http.Request, target any, limit int64) error {
	limit = min(limit, handler.maxBodyBytes)
	if request.ContentLength > limit {
		return manager.Err("too_large", "request body exceeds limit")
	}
	if request.ContentLength == 0 {
		return nil // body-less POSTs (actions such as /hibernate) mean "no options"
	}
	request.Body = http.MaxBytesReader(writer, request.Body, limit)
	decoder := json.NewDecoder(request.Body)
	decoder.DisallowUnknownFields()
	for _, read := range []struct {
		target   any
		expected error
	}{{target, nil}, {new(any), io.EOF}} {
		err := decoder.Decode(read.target)
		var size *http.MaxBytesError
		if errors.As(err, &size) {
			return manager.Err("too_large", "request body exceeds limit")
		}
		if err != read.expected {
			return manager.Err("bad_request", "expected one JSON object with valid fields")
		}
	}
	return nil
}

func (handler *handler) ServeHTTP(writer http.ResponseWriter, request *http.Request) {
	select {
	case handler.requests <- struct{}{}:
		defer func() { <-handler.requests }()
	default:
		failure(writer, manager.Err("capacity", "too many concurrent requests"))
		return
	}
	if request.Method == http.MethodPost && (request.URL.Path == "/v1/exec" || request.URL.Path == "/v1/jobs") {
		bytes := request.ContentLength
		if bytes < 0 {
			bytes = handler.maxBodyBytes
		}
		if bytes > handler.maxBodyBytes {
			failure(writer, manager.Err("too_large", "request body exceeds limit"))
			return
		}
		total := handler.inputBytes.Add(bytes)
		defer handler.inputBytes.Add(-bytes)
		if total > max(handler.maxBodyBytes, 256<<20) {
			failure(writer, manager.Err("capacity", "too much input queued"))
			return
		}
	}
	body, err := handler.route(writer, request)
	if err != nil {
		failure(writer, err)
		return
	}
	respond(writer, http.StatusOK, body)
}

func (handler *handler) route(writer http.ResponseWriter, request *http.Request) (any, error) {
	mgr, ctx := handler.manager, request.Context()
	ok := object{"ok": true}
	empty := func() error { return handler.decode(writer, request, &struct{}{}, 1024) }
	switch request.Method + " " + request.URL.Path {
	case "GET /healthz":
		handler.health.Do(func() {
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			defer cancel()
			if bin := mgr.Config().Host.FirecrackerBin; bin != "" {
				if version, err := exec.CommandContext(ctx, bin, "--version").Output(); err == nil {
					handler.version = strings.TrimSpace(strings.SplitN(string(version), "\n", 2)[0])
				}
			}
		})
		kvm, err := os.Stat("/dev/kvm")
		return object{"ok": true, "version": Version, "firecracker_version": handler.version, "kvm": err == nil && kvm.Mode()&os.ModeCharDevice != 0, "image_version": mgr.ImageStatus().CurrentVersion}, nil
	case "GET /v1/config":
		return mgr.Config(), nil
	case "POST /v1/reload":
		if err := empty(); err != nil {
			return nil, err
		}
		mgr.Reload()
		return ok, nil
	case "GET /v1/grants":
		return object{"grants": mgr.Grants()}, nil
	case "GET /v1/vms":
		return mgr.Inventory(), nil
	case "GET /v1/image":
		return mgr.ImageStatus(), nil
	case "PUT /v1/image/packages":
		var packages image.Packages
		if err := handler.decode(writer, request, &packages, 1<<20); err != nil {
			return nil, err
		}
		return ok, mgr.SetPackages(packages)
	case "POST /v1/image/rebuild":
		if err := empty(); err != nil {
			return nil, err
		}
		return ok, mgr.Rebuild()
	case "POST /v1/exec", "POST /v1/jobs":
		var input manager.ExecRequest
		if err := handler.decode(writer, request, &input, handler.maxBodyBytes); err != nil {
			return nil, err
		}
		if request.URL.Path == "/v1/jobs" {
			return mgr.StartJob(input)
		}
		return mgr.Exec(ctx, input)
	case "GET /v1/jobs":
		id, err := parseChat(request.URL.Query().Get("chat_id"))
		if err != nil {
			return nil, err
		}
		return object{"jobs": mgr.Jobs(id)}, nil
	case "POST /v1/pre/run":
		var input manager.PreRequest
		if err := handler.decode(writer, request, &input, 2<<20); err != nil {
			return nil, err
		}
		return mgr.PreRun(ctx, input)
	}
	parts := strings.Split(request.URL.Path, "/")
	if len(parts) == 4 && parts[1] == "v1" && parts[2] == "jobs" {
		switch request.Method {
		case http.MethodGet:
			var wait int
			if value := request.URL.Query().Get("wait_s"); value != "" {
				var err error
				wait, err = strconv.Atoi(value)
				if err != nil || wait < 0 {
					return nil, manager.Err("bad_request", "invalid wait_s")
				}
			}
			return mgr.Job(ctx, parts[3], time.Duration(min(wait, 25))*time.Second)
		case http.MethodDelete:
			return ok, mgr.CancelJob(parts[3])
		}
	}
	if len(parts) >= 4 && parts[1] == "v1" && parts[2] == "chats" {
		chatID, err := parseChat(parts[3])
		if err != nil {
			return nil, err
		}
		return handler.chat(writer, request, chatID, strings.Join(parts[4:], "/"))
	}
	return nil, manager.Err("not_found", "endpoint not found")
}

func parseChat(value string) (int64, error) {
	id, err := strconv.ParseInt(value, 10, 64)
	if err != nil || id == 0 {
		return 0, manager.Err("bad_request", "invalid chat_id")
	}
	return id, nil
}

func (handler *handler) chat(writer http.ResponseWriter, request *http.Request, id int64, path string) (any, error) {
	mgr, ctx := handler.manager, request.Context()
	ok := object{"ok": true}
	empty := func() error { return handler.decode(writer, request, &struct{}{}, 1024) }
	decode := func(target any) error { return handler.decode(writer, request, target, 64<<10) }
	switch request.Method + " " + path {
	case "GET ":
		return mgr.ChatInfo(id), nil
	case "PUT grant":
		var body struct {
			Enabled bool   `json:"enabled"`
			By      string `json:"by"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return object{"ok": true, "chat_id": id, "enabled": body.Enabled}, mgr.SetGrant(id, body.Enabled, body.By)
	case "DELETE ":
		uid, authenticated := ctx.Value(peerKey{}).(uint32)
		if !authenticated || uid != 0 {
			return nil, manager.Err("forbidden", "wipe requires the root peer")
		}
		var body struct {
			Confirm string `json:"confirm"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		if body.Confirm != "WIPE" {
			return nil, manager.Err("bad_request", "wipe requires confirmation")
		}
		return ok, mgr.Action(ctx, id, "wipe")
	case "POST touch":
		if err := empty(); err != nil {
			return nil, err
		}
		return object{"state": mgr.Touch(id)}, nil
	case "POST reset_session":
		var body struct {
			Session string `json:"session"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return ok, mgr.ResetSession(ctx, id, body.Session)
	case "POST restart", "POST kill", "POST hibernate":
		if err := empty(); err != nil {
			return nil, err
		}
		if err := mgr.Action(ctx, id, path); err != nil {
			return nil, err
		}
		if path == "restart" {
			ok["image_version"] = mgr.ImageStatus().CurrentVersion
		}
		return ok, nil
	case "POST files/list":
		var body struct {
			Path    string `json:"path"`
			Depth   int    `json:"depth"`
			Refresh bool   `json:"refresh"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return mgr.ListFiles(ctx, id, body.Path, body.Depth, body.Refresh)
	case "POST files/read_text":
		var body struct {
			Path     string `json:"path"`
			MaxBytes int    `json:"max_bytes"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return mgr.ReadText(ctx, id, body.Path, body.MaxBytes)
	case "POST files/preview":
		var body struct {
			Path  string `json:"path"`
			Page  int    `json:"page"`
			MaxPx int    `json:"max_px"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return mgr.Preview(ctx, id, body.Path, body.Page, body.MaxPx)
	case "POST files/export":
		var body struct {
			Paths []string `json:"paths"`
		}
		if err := decode(&body); err != nil {
			return nil, err
		}
		return mgr.Export(ctx, id, body.Paths)
	case "GET backups":
		backups, err := mgr.Backups(id)
		return object{"backups": backups}, err
	}
	if request.Method == http.MethodPost {
		parts := strings.Split(path, "/")
		if len(parts) == 3 && parts[0] == "backups" && parts[2] == "restore" {
			if err := empty(); err != nil {
				return nil, err
			}
			return ok, mgr.RestoreBackup(ctx, id, parts[1])
		}
	}
	return nil, manager.Err("not_found", "endpoint not found")
}
