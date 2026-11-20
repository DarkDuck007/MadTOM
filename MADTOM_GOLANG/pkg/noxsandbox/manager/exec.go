package manager

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"os"
	"path"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/image"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

var sessionName = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$`)

func allowed(cfg config.Sandbox, id int64, granted bool) error {
	if !cfg.Enabled {
		return Err("disabled", "sandbox is disabled")
	}
	switch cfg.ChatMode() {
	case "all":
		return nil
	case "whitelist":
		if granted || slices.Contains(cfg.EnabledChats, id) {
			return nil
		}
	}
	return Err("forbidden", "chat not enabled")
}

func fileName(name string) (string, bool) {
	if strings.IndexFunc(name, unicode.IsControl) >= 0 {
		return "", false
	}
	name = strings.ReplaceAll(name, `\`, "/")
	if index := strings.LastIndexByte(name, '/'); index >= 0 {
		name = name[index+1:]
	}
	return name, name != "" && name != "." && name != ".." && utf8.RuneCountInString(name) <= 120
}

func validPath(value string) bool {
	if len(value) > 4096 || strings.IndexFunc(value, unicode.IsControl) >= 0 {
		return false
	}
	if value != "/data" && !strings.HasPrefix(value, "/data/") {
		return false
	}
	for _, segment := range strings.Split(value, "/") {
		if segment == ".." {
			return false
		}
	}
	return true
}

func normalizePaths(paths []string, limit int) ([]string, error) {
	if len(paths) > limit {
		return nil, Err("too_large", "too many paths")
	}
	out := make([]string, 0, len(paths))
	for _, item := range paths {
		cleaned, ok := normalizePath(item)
		if !ok {
			return nil, Err("bad_request", "paths must be inside /data")
		}
		out = append(out, cleaned)
	}
	return out, nil
}

func (manager *Manager) validateExec(request *ExecRequest, cfg config.Sandbox, background bool) error {
	if request.ChatID == 0 {
		return Err("bad_request", "chat_id is required")
	}
	if err := allowed(cfg, request.ChatID, manager.Granted(request.ChatID)); err != nil {
		return err
	}
	if request.VM == "" {
		request.VM = vmm.RoleChat
	}
	if request.VM != vmm.RoleChat && request.VM != vmm.RoleTemp {
		return Err("bad_request", "vm must be chat or temp")
	}
	if request.Kind == "" {
		request.Kind = "python"
	}
	if request.Kind != "python" && request.Kind != "shell" {
		return Err("bad_request", "kind must be python or shell")
	}
	if request.Code == "" {
		return Err("bad_request", "code is required")
	}
	if request.Session == "" || request.VM == vmm.RoleTemp {
		request.Session = "main"
	}
	if !sessionName.MatchString(request.Session) {
		return Err("bad_request", "invalid session name")
	}
	if request.TimeoutS < 0 {
		return Err("bad_request", "negative timeout")
	}
	if request.TimeoutS == 0 {
		request.TimeoutS = cfg.ExecTimeoutSeconds
	}
	maximum := cfg.ExecTimeoutMaxSeconds
	if background {
		maximum = cfg.BackgroundTimeoutMaxSeconds
	}
	request.TimeoutS = min(request.TimeoutS, maximum)
	if len(request.Inputs) > 2048 {
		return Err("too_large", "too many inputs")
	}
	bytes := 0
	for _, item := range request.Inputs {
		name, ok := fileName(item.Name)
		if !ok || name != item.Name {
			return Err("bad_request", "input names must be basenames")
		}
		if len(item.Data) > cfg.MaxInputBytes-bytes {
			return Err("too_large", "inputs exceed limit")
		}
		bytes += len(item.Data)
	}
	var err error
	request.Export, err = normalizePaths(request.Export, max(32, min(cfg.MaxExportFiles, 2048)))
	return err
}

func (manager *Manager) reserveSession(chat *chat, request *ExecRequest, cfg config.Sandbox) (bool, error) {
	if chat.slot != nil && manager.sample(chat.slot) {
		manager.persist(chat)
	}
	manager.rollover(chat)
	if chat.meta.CPUSeconds >= float64(cfg.DailyCPUSecondsPerChat) {
		return false, Err("quota_exceeded", "daily CPU budget exceeded")
	}
	if request.VM != vmm.RoleChat {
		chat.active++
		return false, nil
	}
	created := request.Session == "new"
	if created {
		for {
			chat.meta.NextSession++
			request.Session = "s" + strconv.FormatUint(chat.meta.NextSession, 10)
			if _, exists := chat.meta.Sessions[request.Session]; !exists && !chat.busy[request.Session] {
				break
			}
		}
	}
	if chat.busy[request.Session] {
		return false, Err("busy", "session is busy")
	}
	if len(chat.busy) >= 64 || (len(chat.meta.Sessions) >= 256 && chat.meta.Sessions[request.Session].IsZero()) {
		return false, Err("capacity", "session limit reached")
	}
	chat.busy[request.Session] = true
	chat.active++
	if chat.state == "warm" {
		manager.setState(chat, "busy")
	}
	return created, nil
}

func (manager *Manager) releaseSession(chat *chat, request ExecRequest) {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if request.VM == vmm.RoleChat {
		delete(chat.busy, request.Session)
	}
	manager.releaseUse(chat, true)
}

func (manager *Manager) releaseUse(chat *chat, touch bool) {
	chat.active--
	if touch {
		chat.meta.LastUsed = manager.now()
	}
	if chat.active == 0 && chat.state == "busy" {
		manager.setState(chat, "warm")
	}
	manager.notify()
}

func (manager *Manager) getChatVM(ctx context.Context, chat *chat, cfg config.Sandbox, deadline time.Time) (*slot, ExecVM, int64, int64, error) {
	requested := time.Now()
	for {
		manager.mu.Lock()
		switch chat.state {
		case "warm", "busy":
			slot := chat.slot
			manager.setState(chat, "busy")
			manager.mu.Unlock()
			return slot, ExecVM{State: "warm", ImageVersion: slot.spec.ImageVersion}, time.Since(requested).Milliseconds(), 0, nil
		case "cold", "hibernated":
			previous := chat.meta.Snapshot
			manager.setState(chat, "booting")
			manager.mu.Unlock()
			return manager.bootChat(ctx, chat, cfg, previous, requested, deadline)
		default:
			changed := manager.changed
			manager.mu.Unlock()
			if err := wait(ctx, changed, deadline); err != nil {
				return nil, ExecVM{}, 0, 0, err
			}
		}
	}
}

func (manager *Manager) bootChat(ctx context.Context, chat *chat, cfg config.Sandbox, previous *savedSnapshot, requested, deadline time.Time) (*slot, ExecVM, int64, int64, error) {
	defaultSpec := manager.spec(vmm.RoleChat, chat.id, manager.image.Current(), cfg)
	spec := defaultSpec
	var snapshot *vmm.Snapshot
	volumeSize := int64(cfg.ChatVM.DiskGB) << 30
	if previous != nil && image.ValidVersion(previous.Snapshot.ImageVersion) && manager.now().Before(previous.Created.Add(time.Duration(cfg.HibernateTTLHours)*time.Hour)) {
		old := manager.spec(vmm.RoleChat, chat.id, previous.Snapshot.ImageVersion, cfg)
		volume, err := os.Stat(defaultSpec.DataDisk)
		if old.MemMB == previous.Spec.MemMB && old.VCPUs == previous.Spec.VCPUs && old.DiskBwMBs == previous.Spec.DiskBwMBs && old.DiskIOPS == previous.Spec.DiskIOPS && err == nil && volume.Size() >= volumeSize {
			spec, snapshot = old, &previous.Snapshot
		}
	}
	slot, err := manager.reserve(ctx, spec, deadline, true)
	if err != nil {
		manager.mu.Lock()
		if previous != nil {
			manager.setState(chat, "hibernated")
		} else {
			manager.setState(chat, "cold")
		}
		manager.mu.Unlock()
		return nil, ExecVM{}, 0, 0, err
	}
	start := time.Now()
	if snapshot == nil {
		manager.mu.Lock()
		manager.invalidate(chat)
		if previous != nil {
			err = manager.save(chat)
		}
		manager.mu.Unlock()
		if err != nil {
			_ = manager.stopSlot(slot)
			manager.finishCold(chat, nil)
			return nil, ExecVM{}, 0, 0, err
		}
		if err := manager.ensureVolume(chat.id, volumeSize); err != nil {
			_ = manager.stopSlot(slot)
			manager.finishCold(chat, nil)
			return nil, ExecVM{}, 0, 0, err
		}
	}
	manager.mu.Lock()
	slot.charge, chat.slot = chat, slot
	manager.mu.Unlock()
	err = manager.launch(ctx, slot, snapshot)
	if err != nil {
		log.Printf("nox-sandbox: start chat %d: %v", chat.id, err)
		manager.finishCold(chat, slot)
		if snapshot != nil && ctx.Err() == nil {
			manager.mu.Lock()
			if chat.state == "cold" {
				manager.setState(chat, "booting")
				manager.mu.Unlock()
				return manager.bootChat(ctx, chat, cfg, nil, requested, deadline)
			}
			manager.mu.Unlock()
		}
		return nil, ExecVM{}, 0, 0, Err("vm_failed", "chat VM failed to start")
	}
	manager.mu.Lock()
	chat.meta.Snapshot = nil
	if err := manager.save(chat); err != nil {
		manager.setState(chat, "stopping")
		manager.mu.Unlock()
		_ = manager.stopSlot(slot)
		manager.finishCold(chat, slot)
		return nil, ExecVM{}, 0, 0, err
	}
	_ = os.RemoveAll(manager.snapshotDir(chat.id))
	chat.born, chat.meta.LastUsed = manager.now(), manager.now()
	manager.setState(chat, "busy")
	manager.mu.Unlock()
	return slot, ExecVM{State: "warm", ImageVersion: slot.spec.ImageVersion, ColdStarted: snapshot == nil, Restored: snapshot != nil}, start.Sub(requested).Milliseconds(), time.Since(start).Milliseconds(), nil
}

func (manager *Manager) Exec(ctx context.Context, request ExecRequest) (ExecResponse, error) {
	started := time.Now()
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return ExecResponse{}, err
	}
	defer finish()
	manager.mu.Lock()
	cfg := manager.cfg.Sandbox
	if err := manager.validateExec(&request, cfg, false); err != nil {
		manager.mu.Unlock()
		return ExecResponse{}, err
	}
	chat := manager.chat(request.ChatID)
	created, err := manager.reserveSession(chat, &request, cfg)
	manager.mu.Unlock()
	if err != nil {
		return ExecResponse{}, err
	}
	defer manager.releaseSession(chat, request)
	return manager.execute(ctx, chat, request, cfg, created, started)
}

func (manager *Manager) execute(ctx context.Context, chat *chat, request ExecRequest, cfg config.Sandbox, created bool, started time.Time) (ExecResponse, error) {
	response := ExecResponse{Files: emptyFiles()}
	deadline := time.Now().Add(time.Duration(cfg.Global.QueueTimeoutSeconds) * time.Second)
	var slot *slot
	var err error
	if request.VM == vmm.RoleChat {
		slot, response.VM, response.TimingsMS.Queue, response.TimingsMS.Start, err = manager.getChatVM(ctx, chat, cfg, deadline)
	} else {
		var restored bool
		slot, restored, response.TimingsMS.Queue, response.TimingsMS.Start, err = manager.ephemeral(ctx, request.VM, request.ChatID, cfg, deadline)
		if err == nil {
			response.VM = ExecVM{State: "cold", ImageVersion: slot.spec.ImageVersion, ColdStarted: !restored, Restored: restored}
			defer manager.stopSlot(slot)
			manager.charge(slot, chat)
		}
	}
	if err != nil {
		return response, err
	}
	if created {
		response.Session = request.Session
	}
	client := manager.client(slot)
	if len(request.Inputs) > 0 {
		if _, err := client.PutFiles(ctx, "/data/inbox", request.Inputs); err != nil {
			manager.protocolFailure(chat, slot, err)
			return response, guestError(err)
		}
	}
	execStart := time.Now()
	deadlineCtx, finish := context.WithTimeout(ctx, time.Duration(request.TimeoutS)*time.Second)
	result, err := client.Exec(deadlineCtx, request.Session, request.Kind, request.Code, request.TimeoutS, cfg.MaxOutputBytes)
	expired := deadlineCtx.Err()
	finish()
	response.TimingsMS.Exec = time.Since(execStart).Milliseconds()
	if err != nil {
		var network net.Error
		timedOut := errors.Is(expired, context.DeadlineExceeded) || errors.As(err, &network) && network.Timeout()
		if timedOut || ctx.Err() != nil {
			manager.abort(chat, slot)
			if !timedOut {
				return response, Err("timeout", "execution cancelled")
			}
			message := "timeout"
			result = agent.ExecResult{OK: true, ExitCode: -1, Error: &message, SessionReset: request.VM == vmm.RoleChat}
		} else {
			manager.protocolFailure(chat, slot, err)
			return response, guestError(err)
		}
	}
	response.OK, response.ExitCode, response.Stdout, response.Stderr = result.OK, result.ExitCode, result.Stdout, result.Stderr
	response.Truncated, response.ResultRepr, response.Error, response.SessionReset = result.Truncated, result.ResultRepr, result.Error, result.SessionReset
	if response.Error != nil && *response.Error == "busy" {
		return response, Err("busy", "guest session is busy")
	}
	if err == nil && len(request.Export) > 0 {
		files, exportErr := client.GetFiles(ctx, request.Export, cfg.MaxExportBytes, cfg.MaxExportFiles)
		if exportErr != nil {
			manager.protocolFailure(chat, slot, exportErr)
			return response, guestError(exportErr)
		}
		response.Files = cleanFiles(files, cfg)
	}
	if request.VM == vmm.RoleChat {
		if err == nil {
			if manifestErr := manager.refreshManifest(ctx, chat, slot, cfg.MaxFileListEntries); manifestErr != nil {
				manager.protocolFailure(chat, slot, manifestErr)
			}
		}
		manager.mu.Lock()
		if chat.slot == slot && chat.state == "busy" {
			if result.SessionReset || chat.meta.Sessions[request.Session].IsZero() {
				chat.meta.Sessions[request.Session] = manager.now()
			}
			response.SessionReset = response.SessionReset || chat.meta.Reset
			chat.meta.Reset = false
			manager.sample(slot)
			manager.persist(chat)
		}
		if chat.state != "busy" || chat.active > 1 {
			response.VM.State = chat.state
		}
		response.Disk = manager.disk(chat)
		manager.mu.Unlock()
	} else {
		response.Disk.QuotaBytes = int64(slot.spec.TmpMB) << 20
		if err == nil {
			if manifest, manifestErr := client.Manifest(ctx, 1); manifestErr == nil {
				response.Disk.UsedBytes = min(response.Disk.QuotaBytes, max(0, manifest.UsedBytes))
			}
		}
	}
	response.TimingsMS.Total = time.Since(started).Milliseconds()
	return response, nil
}

func (manager *Manager) disk(chat *chat) Disk {
	var quota int64
	if info, err := os.Stat(manager.volume(chat.id)); err == nil {
		quota = info.Size()
	}
	return Disk{min(quota, max(0, chat.meta.Manifest.UsedBytes)), quota}
}

func (manager *Manager) refreshManifest(ctx context.Context, chat *chat, slot *slot, count int) error {
	manifest, err := manager.client(slot).Manifest(ctx, min(count, 10000))
	if err != nil {
		return err
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if chat.slot == slot && chat.state != "stopping" {
		now := manager.now()
		chat.meta.Manifest, chat.meta.ManifestAt = manifest, &now
		manager.persist(chat)
	}
	return nil
}

func (manager *Manager) protocolFailure(chat *chat, slot *slot, err error) {
	var protocol *agent.ProtocolError
	if errors.As(err, &protocol) {
		manager.abort(chat, slot)
	}
}

func (manager *Manager) abort(chat *chat, slot *slot) {
	if slot.spec.Role != vmm.RoleChat {
		_ = manager.stopSlot(slot)
		return
	}
	manager.mu.Lock()
	if chat.slot != slot || chat.state == "stopping" {
		manager.mu.Unlock()
		return
	}
	manager.setState(chat, "stopping")
	manager.mu.Unlock()
	_ = manager.stopSlot(slot)
	manager.finishCold(chat, slot)
}

func emptyFiles() agent.Files { return agent.Files{Files: []agent.File{}, Skipped: []agent.Skipped{}} }

func cleanFiles(received agent.Files, cfg config.Sandbox) agent.Files {
	result := emptyFiles()
	limit := min(cfg.MaxExportFiles, 2048)
	used := 0
	for _, item := range received.Files[:min(len(received.Files), max(limit*2, 0))] {
		name, valid := fileName(item.Name)
		reason := ""
		switch {
		case !valid, !validPath(item.Path), !slices.Contains(cfg.ExportAllowedExts, strings.TrimPrefix(strings.ToLower(path.Ext(name)), ".")):
			reason = "not_regular"
		case len(item.Data) > cfg.MaxExportBytes-used:
			reason = "too_large"
		case len(result.Files) >= limit:
			reason = "limit"
		}
		if reason != "" {
			if validPath(item.Path) {
				result.Skipped = append(result.Skipped, agent.Skipped{Path: item.Path, Reason: reason})
			}
			continue
		}
		item.Sanitized = false
		if format := sanitizableFormat(name); format != "" {
			clean, err := sanitizeImage(item.Data, format)
			if err != nil {
				result.Skipped = append(result.Skipped, agent.Skipped{Path: item.Path, Reason: "bad_image"})
				continue
			}
			item.Data, item.Sanitized = clean, true
			if format == "jpeg" && strings.ToLower(path.Ext(name)) != ".jpg" && strings.ToLower(path.Ext(name)) != ".jpeg" {
				name = strings.TrimSuffix(name, path.Ext(name)) + ".jpg"
			}
		}
		item.Name, item.Size = name, int64(len(item.Data))
		if item.Data == nil {
			item.Data = []byte{}
		}
		used += len(item.Data)
		result.Files = append(result.Files, item)
	}
	for _, item := range received.Skipped[:min(len(received.Skipped), max(limit*2, 0))] {
		if !validPath(item.Path) {
			continue
		}
		switch item.Reason {
		case "symlink", "not_regular", "too_large", "limit":
			result.Skipped = append(result.Skipped, item)
		}
	}
	return result
}

func (manager *Manager) smoke(ctx context.Context, version string, packages image.Packages) error {
	cfg := manager.Config().Sandbox
	spec := manager.spec(vmm.RoleTemp, 0, version, cfg)
	slot, err := manager.reserve(ctx, spec, time.Now().Add(time.Duration(cfg.Global.QueueTimeoutSeconds)*time.Second), true)
	if err != nil {
		return err
	}
	defer manager.stopSlot(slot)
	if err := manager.launch(ctx, slot, nil); err != nil {
		return err
	}
	client := manager.client(slot)
	check := func(kind, code, name string) error {
		ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
		defer cancel()
		result, err := client.Exec(ctx, "main", kind, code, 30, min(4096, cfg.MaxOutputBytes))
		if err != nil {
			return fmt.Errorf("%s: %w", name, err)
		}
		if !result.OK || result.ExitCode != 0 {
			return fmt.Errorf("%s: %s", name, result.Stderr)
		}
		return nil
	}
	for _, pkg := range packages.Pip {
		if err := check("python", "import importlib; importlib.import_module("+strconv.Quote(image.ImportName(pkg))+")", pkg); err != nil {
			return err
		}
	}
	for _, pkg := range packages.Apt {
		if err := check("shell", "command -v "+pkg, pkg); err != nil {
			return err
		}
	}
	return nil
}
