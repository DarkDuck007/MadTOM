package manager

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/preview"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
	"log"
	"os"
	"path"
	"slices"
	"strings"
	"time"
)

func (manager *Manager) Touch(chatID int64) string {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if chat := manager.chats[chatID]; chat != nil {
		if chat.state == "warm" || chat.state == "busy" {
			chat.meta.LastUsed = manager.now()
		}
		return chat.state
	}
	return "cold"
}

func (manager *Manager) Action(ctx context.Context, chatID int64, action string) error {
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return err
	}
	defer finish()
	manager.mu.Lock()
	chat := manager.chats[chatID]
	if chat == nil {
		manager.mu.Unlock()
		return nil
	}
	switch chat.state {
	case "booting", "stopping", "inspector", "hibernating":
		manager.mu.Unlock()
		return Err("busy", "chat VM transition in progress")
	}
	slot := chat.slot
	if action == "hibernate" {
		if !chat.idle() {
			manager.mu.Unlock()
			return Err("busy", "chat is in use")
		}
		if slot == nil {
			manager.mu.Unlock()
			return nil
		}
		manager.setState(chat, "hibernating")
		manager.mu.Unlock()
		if err := manager.hibernate(ctx, chat, slot, true); err != nil {
			log.Printf("nox-sandbox: hibernate chat %d: %v", chatID, err)
			return Err("vm_failed", "hibernation failed")
		}
		return nil
	}
	if action == "wipe" && (chat.active > 0 || chat.jobs > 0) {
		manager.mu.Unlock()
		return Err("busy", "chat is in use")
	}
	manager.setState(chat, "stopping")
	manager.mu.Unlock()
	if err := manager.stopSlot(slot); err != nil {
		manager.finishCold(chat, slot)
		return Err("vm_failed", "failed to stop VM")
	}
	if action != "wipe" {
		manager.finishCold(chat, slot)
		return nil
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if err := errors.Join(os.RemoveAll(manager.chatDir(chatID)), os.RemoveAll(manager.backupDir(chatID))); err != nil {
		chat.slot = nil
		manager.invalidate(chat)
		manager.setState(chat, "cold")
		return err
	}
	for id, item := range manager.jobs {
		if item.ChatID == chatID {
			delete(manager.jobs, id)
			_ = os.Remove(manager.jobsDir() + "/" + id + ".json")
		}
	}
	delete(manager.chats, chatID)
	manager.notify()
	return nil
}

func (manager *Manager) ResetSession(ctx context.Context, chatID int64, session string) error {
	if session == "" {
		session = "main"
	}
	if !sessionName.MatchString(session) || session == "new" {
		return Err("bad_request", "invalid session name")
	}
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return err
	}
	defer finish()
	manager.mu.Lock()
	chat := manager.chats[chatID]
	if chat == nil || chat.state == "cold" {
		manager.mu.Unlock()
		return nil
	}
	if chat.busy[session] {
		manager.mu.Unlock()
		return Err("busy", "session is busy")
	}
	cfg := manager.cfg.Sandbox
	chat.busy[session] = true
	chat.active++
	manager.mu.Unlock()
	defer manager.releaseSession(chat, ExecRequest{VM: vmm.RoleChat, Session: session})
	slot, _, _, _, err := manager.getChatVM(ctx, chat, cfg, time.Now().Add(time.Duration(cfg.Global.QueueTimeoutSeconds)*time.Second))
	if err != nil {
		return err
	}
	if err := manager.client(slot).ResetSession(ctx, session); err != nil {
		manager.protocolFailure(chat, slot, err)
		return guestError(err)
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if chat.slot == slot && chat.state == "busy" {
		chat.meta.Sessions[session] = manager.now()
		manager.persist(chat)
	}
	return nil
}

func (manager *Manager) withFiles(ctx context.Context, chatID int64, action func(context.Context, agent.Client, config.Sandbox) error) error {
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return err
	}
	defer finish()
	cfg := manager.Config().Sandbox
	deadline := time.Now().Add(time.Duration(cfg.Global.QueueTimeoutSeconds) * time.Second)
	for {
		manager.mu.Lock()
		chat := manager.chats[chatID]
		if chat == nil {
			manager.mu.Unlock()
			return Err("not_found", "chat volume not found")
		}
		switch chat.state {
		case "warm", "busy":
			slot := chat.slot
			chat.active++
			manager.setState(chat, "busy")
			manager.mu.Unlock()
			err := action(ctx, manager.client(slot), cfg)
			if err != nil {
				manager.protocolFailure(chat, slot, err)
			}
			manager.mu.Lock()
			manager.releaseUse(chat, false)
			manager.mu.Unlock()
			return err
		case "cold", "hibernated":
			if _, err := os.Stat(manager.volume(chatID)); err != nil {
				manager.mu.Unlock()
				if os.IsNotExist(err) {
					return Err("not_found", "chat volume not found")
				}
				return err
			}
			previous := chat.state
			manager.setState(chat, "inspector")
			manager.mu.Unlock()
			slot, _, _, _, err := manager.ephemeral(ctx, vmm.RoleInspector, chatID, cfg, deadline)
			if err == nil {
				err = action(ctx, manager.client(slot), cfg)
				err = errors.Join(err, manager.stopSlot(slot))
			}
			manager.mu.Lock()
			if _, retained := manager.slots[slot]; retained {
				slot.inspectorResume = previous
				manager.setState(chat, "stopping")
			} else {
				manager.setState(chat, previous)
			}
			manager.mu.Unlock()
			return err
		default:
			changed := manager.changed
			manager.mu.Unlock()
			if err := wait(ctx, changed, deadline); err != nil {
				return err
			}
		}
	}
}

func convertGuestError(err error) error {
	if err == nil {
		return nil
	}
	var apiError *Error
	if errors.As(err, &apiError) {
		return apiError
	}
	return guestError(err)
}

func (manager *Manager) ListFiles(ctx context.Context, chatID int64, guestPath string, depth int, refresh bool) (FileList, error) {
	if guestPath == "" {
		guestPath = "/data"
	}
	guestPath, valid := normalizePath(guestPath)
	if !valid || depth < 0 || depth > 128 {
		return FileList{}, Err("bad_request", "invalid path or depth")
	}
	if depth == 0 {
		depth = 2
	}
	result := FileList{Entries: []agent.Entry{}, FromCache: true}
	manager.mu.Lock()
	chat := manager.chats[chatID]
	if !refresh && (chat == nil || (chat.state != "warm" && chat.state != "busy")) {
		if chat != nil {
			result.AsOf = chat.meta.ManifestAt
			prefix := guestPath + "/"
			maxEntries := manager.cfg.Sandbox.MaxFileListEntries
			for _, entry := range chat.meta.Manifest.Entries {
				if len(result.Entries) >= maxEntries {
					break
				}
				if rest, ok := strings.CutPrefix(entry.Path, prefix); ok && rest != "" && strings.Count(rest, "/") < depth && validPath(entry.Path) {
					result.Entries = append(result.Entries, entry)
				}
			}
		}
		manager.mu.Unlock()
		return result, nil
	}
	manager.mu.Unlock()
	err := manager.withFiles(ctx, chatID, func(ctx context.Context, client agent.Client, cfg config.Sandbox) error {
		entries, _, err := client.List(ctx, guestPath, depth, min(cfg.MaxFileListEntries, 10000))
		if err != nil {
			return err
		}
		for _, entry := range entries {
			if validPath(entry.Path) {
				result.Entries = append(result.Entries, entry)
			}
		}
		now := manager.now()
		result.AsOf, result.FromCache = &now, false
		return nil
	})
	return result, convertGuestError(err)
}

func (manager *Manager) ReadText(ctx context.Context, chatID int64, guestPath string, maxBytes int) (agent.Text, error) {
	guestPath, valid := normalizePath(guestPath)
	if !valid || maxBytes < 0 {
		return agent.Text{}, Err("bad_request", "invalid path or byte limit")
	}
	var result agent.Text
	err := manager.withFiles(ctx, chatID, func(ctx context.Context, client agent.Client, cfg config.Sandbox) error {
		if maxBytes == 0 {
			maxBytes = cfg.MaxTextViewBytes
		}
		var err error
		result, err = client.ReadText(ctx, guestPath, min(maxBytes, cfg.MaxTextViewBytes))
		return err
	})
	return result, convertGuestError(err)
}

func (manager *Manager) Preview(ctx context.Context, chatID int64, guestPath string, page, maxPx int) (Preview, error) {
	guestPath, valid := normalizePath(guestPath)
	if !valid || page < 0 || maxPx < 0 {
		return Preview{}, Err("bad_request", "invalid path, page or size")
	}
	var result Preview
	err := manager.withFiles(ctx, chatID, func(ctx context.Context, client agent.Client, cfg config.Sandbox) error {
		if maxPx == 0 {
			maxPx = cfg.PreviewMaxPx
		}
		maxPx = min(maxPx, cfg.PreviewMaxPx)
		framePixels := (client.MaxFrameBytes - 512) / 16 * 3
		for maxPx > 0 && (framePixels <= 0 || maxPx > framePixels/maxPx) {
			maxPx--
		}
		if maxPx == 0 {
			return Err("too_large", "preview exceeds the frame limit")
		}
		raw, err := client.Render(ctx, guestPath, max(1, page), maxPx)
		if err != nil {
			return err
		}
		data, err := preview.PNG(raw.Width, raw.Height, maxPx, raw.RGBA)
		if err != nil || raw.Pages < 0 || len(raw.Kind) > 32 {
			return &agent.ProtocolError{Reason: "invalid preview"}
		}
		result = Preview{data, raw.Width, raw.Height, raw.Pages, raw.Kind}
		return nil
	})
	return result, convertGuestError(err)
}

func (manager *Manager) Export(ctx context.Context, chatID int64, paths []string) (agent.Files, error) {
	paths, err := normalizePaths(paths, 2048)
	if err != nil {
		return agent.Files{}, err
	}
	result := emptyFiles()
	if len(paths) == 0 {
		return result, nil
	}
	err = manager.withFiles(ctx, chatID, func(ctx context.Context, client agent.Client, cfg config.Sandbox) error {
		files, err := client.GetFiles(ctx, paths, cfg.MaxExportBytes, cfg.MaxExportFiles)
		if err == nil {
			result = cleanFiles(files, cfg)
		}
		return err
	})
	return result, convertGuestError(err)
}

func normalizePath(value string) (string, bool) {
	if value == "" {
		return "", false
	}
	value = strings.ReplaceAll(value, `\`, "/")
	if !strings.HasPrefix(value, "/") {
		value = "/data/" + value
	}
	if !validPath(value) {
		return "", false
	}
	return path.Clean(value), true
}

func (manager *Manager) PreRun(ctx context.Context, request PreRequest) (PreResponse, error) {
	started := time.Now()
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return PreResponse{}, err
	}
	defer finish()
	cfg := manager.Config().Sandbox
	if err := allowed(cfg, request.ChatID, manager.Granted(request.ChatID)); err != nil {
		return PreResponse{}, err
	}
	if request.ChatID == 0 || request.TimeoutMS < 0 || request.Code == "" || !jsonObject(request.Event) {
		return PreResponse{}, Err("bad_request", "invalid preprocessor request")
	}
	if len(request.State) == 0 {
		request.State = json.RawMessage(`{}`)
	}
	if !jsonObject(request.State) {
		return PreResponse{}, Err("bad_request", "state must be a JSON object")
	}
	if request.TimeoutMS == 0 {
		request.TimeoutMS = cfg.PreVM.ExecTimeoutMS
	}
	request.TimeoutMS = min(request.TimeoutMS, cfg.PreVM.ExecTimeoutMS)
	manager.mu.Lock()
	chat := manager.chat(request.ChatID)
	if chat.slot != nil && manager.sample(chat.slot) {
		manager.persist(chat)
	}
	manager.rollover(chat)
	exceeded := chat.meta.CPUSeconds >= float64(cfg.DailyCPUSecondsPerChat)
	if !exceeded {
		chat.active++
	}
	manager.mu.Unlock()
	if exceeded {
		return PreResponse{}, Err("quota_exceeded", "daily CPU budget exceeded")
	}
	defer manager.releaseSession(chat, ExecRequest{VM: vmm.RolePre})
	slot, _, queue, start, err := manager.ephemeral(ctx, vmm.RolePre, request.ChatID, cfg, time.Now().Add(time.Duration(cfg.Global.QueueTimeoutSeconds)*time.Second))
	if err != nil {
		return PreResponse{}, err
	}
	defer manager.stopSlot(slot)
	manager.charge(slot, chat)
	execStart := time.Now()
	work, cancel := context.WithTimeout(ctx, time.Duration(request.TimeoutMS)*time.Millisecond)
	result, err := manager.client(slot).PreRun(work, request.Code, request.Event, request.State, request.TimeoutMS)
	expired := work.Err()
	cancel()
	response := PreResponse{Actions: []json.RawMessage{}, State: request.State, TimingsMS: Timings{queue, start, time.Since(execStart).Milliseconds(), time.Since(started).Milliseconds()}}
	if err != nil {
		if errors.Is(expired, context.DeadlineExceeded) || time.Since(execStart) >= time.Duration(request.TimeoutMS)*time.Millisecond {
			message := "timeout"
			response.Error = &message
			return response, nil
		}
		return response, guestError(err)
	}
	var value struct {
		Actions []json.RawMessage `json:"actions"`
		State   json.RawMessage   `json:"state"`
	}
	if !result.OK {
		response.Error = result.Error
	} else if !jsonObject(result.Result) || len(result.Result) > min(cfg.MaxOutputBytes, 1<<20) || json.Unmarshal(result.Result, &value) != nil {
		message := "preprocessor must return a JSON object"
		response.Error = &message
	} else {
		response.OK = true
		if value.Actions != nil {
			response.Actions = value.Actions
		}
		for _, state := range []json.RawMessage{value.State, result.State} {
			if jsonObject(state) && len(state) <= min(cfg.MaxOutputBytes, 1<<20) {
				response.State = state
			}
		}
	}
	if response.Error != nil && len(*response.Error) > 4096 {
		text := (*response.Error)[:4096]
		response.Error = &text
	}
	return response, nil
}

func jsonObject(data json.RawMessage) bool {
	value := strings.TrimSpace(string(data))
	return strings.HasPrefix(value, "{") && json.Valid(data)
}

type PoolInfo struct {
	Ready  int `json:"ready"`
	Target int `json:"target"`
}
type Totals struct {
	Running       int `json:"running"`
	GuestMemMB    int `json:"guest_mem_mb"`
	MaxVMs        int `json:"max_vms"`
	MaxGuestMemMB int `json:"max_guest_mem_mb"`
}
type Inventory struct {
	VMs    []VMInfo              `json:"vms"`
	Pools  map[vmm.Role]PoolInfo `json:"pools"`
	Totals Totals                `json:"totals"`
	Host   map[string]int        `json:"host"`
}

func (manager *Manager) ChatInfo(chatID int64) VMInfo {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if chat := manager.chats[chatID]; chat != nil {
		return manager.info(chat)
	}
	cfg := manager.cfg.Sandbox
	return VMInfo{ChatID: chatID, Kind: vmm.RoleChat, State: "cold", ImageVersion: manager.image.Current(), Since: manager.now(), MemMB: cfg.ChatVM.MemMB, VCPUs: cfg.ChatVM.VCPUs,
		CPUBudgetSeconds: cfg.DailyCPUSecondsPerChat, Sessions: []SessionInfo{}, Jobs: manager.chatJobs(chatID)}
}

func (manager *Manager) info(chat *chat) VMInfo {
	cfg := manager.cfg.Sandbox
	manager.rollover(chat)
	if manager.sample(chat.slot) {
		manager.persist(chat)
	}
	disk := manager.disk(chat)
	backups, _ := manager.Backups(chat.id)
	info := VMInfo{ChatID: chat.id, Kind: vmm.RoleChat, State: chat.state, ImageVersion: manager.image.Current(), MemMB: cfg.ChatVM.MemMB, VCPUs: cfg.ChatVM.VCPUs,
		Since: chat.since, LastUsed: chat.meta.LastUsed, DiskUsedBytes: disk.UsedBytes, DiskQuotaBytes: disk.QuotaBytes, Backups: len(backups),
		CPUSecondsToday: chat.meta.CPUSeconds, CPUBudgetSeconds: cfg.DailyCPUSecondsPerChat, Sessions: []SessionInfo{}, Jobs: manager.chatJobs(chat.id)}
	if chat.meta.Snapshot != nil {
		info.ImageVersion, info.SnapshotBytes = chat.meta.Snapshot.Snapshot.ImageVersion, chat.meta.Snapshot.Snapshot.MemBytes
		info.MemMB, info.VCPUs = chat.meta.Snapshot.Spec.MemMB, chat.meta.Snapshot.Spec.VCPUs
	}
	if slot := chat.slot; slot != nil {
		info.ImageVersion, info.MemMB, info.VCPUs = slot.spec.ImageVersion, slot.spec.MemMB, slot.spec.VCPUs
		if machine, ok := slot.machine.(interface{ PID() int }); ok {
			info.PID = machine.PID()
		}
		if !chat.born.IsZero() {
			deadline := chat.born.Add(time.Duration(cfg.MaxSessionLifetimeMinutes) * time.Minute)
			info.SessionDeadline = &deadline
		}
		if chat.state == "warm" && chat.jobs == 0 {
			deadline := chat.meta.LastUsed.Add(time.Duration(cfg.WarmSeconds) * time.Second)
			info.IdleDeadline = &deadline
		}
	}
	for name, started := range chat.meta.Sessions {
		info.Sessions = append(info.Sessions, SessionInfo{name, chat.busy[name], started})
	}
	for name := range chat.busy {
		if _, ok := chat.meta.Sessions[name]; !ok {
			info.Sessions = append(info.Sessions, SessionInfo{name, true, manager.now()})
		}
	}
	slices.SortFunc(info.Sessions, func(first, second SessionInfo) int { return strings.Compare(first.Name, second.Name) })
	return info
}

func (manager *Manager) Inventory() Inventory {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	cfg := manager.cfg.Sandbox
	result := Inventory{VMs: []VMInfo{}, Pools: map[vmm.Role]PoolInfo{vmm.RoleTemp: {len(manager.ready[vmm.RoleTemp]), cfg.TempVM.PoolSize}, vmm.RolePre: {len(manager.ready[vmm.RolePre]), cfg.PreVM.PoolSize}},
		Totals: Totals{Running: len(manager.slots), MaxVMs: cfg.Global.MaxVMs, MaxGuestMemMB: cfg.Global.MaxGuestMemMB}, Host: map[string]int{"state_dir_used_percent": manager.diskPercent()}}
	pool := make(map[*slot]bool)
	for _, ready := range manager.ready {
		for _, slot := range ready {
			pool[slot] = true
		}
	}
	for _, chat := range manager.chats {
		if _, err := os.Stat(manager.volume(chat.id)); err == nil || chat.slot != nil {
			result.VMs = append(result.VMs, manager.info(chat))
		}
	}
	for slot := range manager.slots {
		result.Totals.GuestMemMB += slot.spec.MemMB
		if slot.spec.Role == vmm.RoleChat || pool[slot] {
			continue
		}
		state := "busy"
		if slot.machine == nil {
			state = "booting"
		} else if slot.spec.Role == vmm.RoleInspector {
			state = "inspector"
		}
		info := VMInfo{ChatID: slot.spec.ChatID, Kind: slot.spec.Role, State: state, ImageVersion: slot.spec.ImageVersion, MemMB: slot.spec.MemMB, VCPUs: slot.spec.VCPUs,
			Since: slot.started, LastUsed: slot.started, Sessions: []SessionInfo{}, Jobs: []JobInfo{}}
		if machine, ok := slot.machine.(interface{ PID() int }); ok {
			info.PID = machine.PID()
		}
		result.VMs = append(result.VMs, info)
	}
	slices.SortFunc(result.VMs, func(first, second VMInfo) int {
		if first.ChatID != second.ChatID {
			if first.ChatID < second.ChatID {
				return -1
			}
			return 1
		}
		return strings.Compare(string(first.Kind), string(second.Kind))
	})
	return result
}
