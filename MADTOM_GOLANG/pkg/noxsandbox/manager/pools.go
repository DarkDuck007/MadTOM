package manager

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/image"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
	"log"
	"os"
	"path/filepath"
	"time"
)

func poolSpec(spec vmm.Spec) vmm.Spec { spec.ID, spec.ChatID = "", 0; return spec }

func (manager *Manager) baseDir(spec vmm.Spec) string {
	data, _ := json.Marshal(poolSpec(spec))
	hash := sha256.Sum256(data)
	return filepath.Join(manager.host.StateDir, "pools", spec.ImageVersion, fmt.Sprintf("%s-%x", spec.Role, hash[:8]))
}

func (manager *Manager) base(spec vmm.Spec) *vmm.Snapshot {
	dir := manager.baseDir(spec)
	var snapshot vmm.Snapshot
	if data, err := os.ReadFile(filepath.Join(dir, "base.json")); err != nil || json.Unmarshal(data, &snapshot) != nil {
		return nil
	}
	snapshot.Dir, snapshot.ImageVersion = dir, spec.ImageVersion
	return &snapshot
}

func (manager *Manager) makeBase(ctx context.Context, spec vmm.Spec) error {
	if manager.diskPercent() >= manager.Config().Sandbox.DiskWatermarkPercent {
		return Err("capacity", "disk watermark reached")
	}
	dir := manager.baseDir(spec)
	if err := os.MkdirAll(filepath.Dir(dir), 0700); err != nil {
		return err
	}
	staging, err := os.MkdirTemp(filepath.Dir(dir), ".base-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(staging)
	slot, err := manager.reserve(ctx, spec, time.Now(), false)
	if err != nil {
		return err
	}
	defer manager.stopSlot(slot)
	if err := manager.launch(ctx, slot, nil); err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, 40*time.Second)
	defer cancel()
	if err := manager.client(slot).PrepareHibernate(ctx); err != nil {
		return err
	}
	if err := slot.machine.Pause(ctx); err != nil {
		return err
	}
	snapshot, err := slot.machine.Snapshot(ctx, staging)
	if err != nil {
		return err
	}
	if manager.diskPercent() >= manager.Config().Sandbox.DiskWatermarkPercent {
		return Err("capacity", "disk watermark reached")
	}
	if err := writeJSON(filepath.Join(staging, "base.json"), snapshot); err != nil {
		return err
	}
	if err := os.RemoveAll(dir); err != nil {
		return err
	}
	return os.Rename(staging, dir)
}

func (manager *Manager) fillPools(ctx context.Context) {
	cfg := manager.Config().Sandbox
	version := manager.image.Current()
	for _, target := range []struct {
		role  vmm.Role
		count int
	}{{vmm.RoleTemp, cfg.TempVM.PoolSize}, {vmm.RolePre, cfg.PreVM.PoolSize}} {
		spec := manager.spec(target.role, 0, version, cfg)
		manager.mu.Lock()
		var discard []*slot
		ready := manager.ready[target.role]
		kept := ready[:0]
		for _, slot := range ready {
			if !cfg.Enabled || len(kept) >= target.count || poolSpec(slot.spec) != spec || dead(slot) {
				discard = append(discard, slot)
			} else {
				kept = append(kept, slot)
			}
		}
		manager.ready[target.role] = kept
		need := len(kept) < target.count
		manager.mu.Unlock()
		for _, slot := range discard {
			_ = manager.stopSlot(slot)
		}
		if !cfg.Enabled || !need || version == "" || ctx.Err() != nil {
			continue
		}
		snapshot := manager.base(spec)
		if snapshot == nil {
			if err := manager.makeBase(ctx, spec); err != nil {
				continue
			}
			snapshot = manager.base(spec)
			if snapshot == nil {
				continue
			}
		}
		slot, err := manager.reserve(ctx, spec, time.Now(), false)
		if err != nil {
			continue
		}
		if err := manager.launch(ctx, slot, snapshot); err != nil {
			log.Printf("nox-sandbox: restore pool %s: %v", target.role, err)
			_ = os.Remove(filepath.Join(manager.baseDir(spec), "base.json"))
			_ = os.RemoveAll(manager.baseDir(spec))
			continue
		}
		manager.mu.Lock()
		manager.ready[target.role] = append(manager.ready[target.role], slot)
		manager.notify()
		manager.mu.Unlock()
	}
}

func (manager *Manager) ephemeral(ctx context.Context, role vmm.Role, chatID int64, cfg config.Sandbox, deadline time.Time) (*slot, bool, int64, int64, error) {
	requested := time.Now()
	spec := manager.spec(role, chatID, manager.image.Current(), cfg)
	if role == vmm.RoleTemp || role == vmm.RolePre {
		manager.mu.Lock()
		ready := manager.ready[role]
		for index, slot := range ready {
			if poolSpec(slot.spec) == poolSpec(spec) && !dead(slot) {
				manager.ready[role] = append(ready[:index], ready[index+1:]...)
				slot.spec.ChatID = chatID
				if value, err := slot.machine.CPUSeconds(); err == nil {
					slot.lastCPU = max(0, value)
				}
				manager.mu.Unlock()
				return slot, true, 0, 0, nil
			}
		}
		manager.mu.Unlock()
	}
	slot, err := manager.reserve(ctx, spec, deadline, true)
	if err != nil {
		return nil, false, 0, 0, err
	}
	start := time.Now()
	var snapshot *vmm.Snapshot
	if role == vmm.RoleTemp || role == vmm.RolePre {
		snapshot = manager.base(spec)
	}
	if err := manager.launch(ctx, slot, snapshot); err != nil {
		log.Printf("nox-sandbox: start %s: %v", role, err)
		if role == vmm.RoleInspector {
			return slot, false, 0, 0, Err("vm_failed", "inspector VM failed to start")
		}
		if snapshot == nil || ctx.Err() != nil {
			return nil, false, 0, 0, Err("vm_failed", "single-use VM failed to start")
		}
		slot, err = manager.reserve(ctx, spec, deadline, true)
		if err != nil {
			return nil, false, 0, 0, err
		}
		if err := manager.launch(ctx, slot, nil); err != nil {
			return nil, false, 0, 0, Err("vm_failed", "single-use VM failed to start")
		}
		snapshot = nil
	}
	return slot, snapshot != nil, start.Sub(requested).Milliseconds(), time.Since(start).Milliseconds(), nil
}

func (manager *Manager) ImageStatus() image.Status {
	status := manager.image.Status()
	manager.mu.Lock()
	defer manager.mu.Unlock()
	for _, chat := range manager.chats {
		if chat.meta.Snapshot != nil {
			status.VersionsInUse[chat.meta.Snapshot.Snapshot.ImageVersion]++
		}
	}
	for slot := range manager.slots {
		status.VersionsInUse[slot.spec.ImageVersion]++
	}
	return status
}

func (manager *Manager) SetPackages(packages image.Packages) error {
	valid, err := image.Normalize(packages)
	if err != nil {
		return Err("bad_request", err.Error())
	}
	return manager.image.SetPackages(valid)
}

func (manager *Manager) Rebuild() error {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if manager.closed {
		return Err("vm_failed", "service is stopping")
	}
	run, ok := manager.image.Begin()
	if !ok {
		return Err("busy", "image build already in progress")
	}
	manager.workers.Go(func() { run(manager.ctx, manager.smoke) })
	return nil
}

func (manager *Manager) scheduled(ctx context.Context) {
	cfg, now := manager.Config().Sandbox, manager.now()
	date := now.Format("2006-01-02")
	if now.Format("15:04") == cfg.LibraryRequests.RebuildWindowLocal && manager.lastBuildDay != date && manager.image.Pending() {
		if manager.Rebuild() == nil {
			manager.lastBuildDay = date
		}
	}
	if cfg.BackupsKeep < 1 || now.Hour() < cfg.BackupHourLocal || manager.diskPercent() >= cfg.DiskWatermarkPercent {
		return
	}
	manager.mu.Lock()
	var chosen *chat
	for _, item := range manager.chats {
		if item.idle() && item.meta.LastBackupDate != date {
			if _, err := os.Stat(manager.volume(item.id)); err == nil {
				chosen = item
				break
			}
		}
	}
	if chosen == nil {
		manager.mu.Unlock()
		return
	}
	slot := chosen.slot
	if slot != nil {
		manager.setState(chosen, "hibernating")
		manager.mu.Unlock()
		if err := manager.hibernate(ctx, chosen, slot, true); err != nil {
			log.Printf("nox-sandbox: prepare backup: %v", err)
		}
		manager.mu.Lock()
	}
	if manager.chats[chosen.id] != chosen || !chosen.idle() || chosen.slot != nil {
		manager.mu.Unlock()
		return
	}
	previous := chosen.state
	manager.setState(chosen, "stopping")
	manager.mu.Unlock()
	err := manager.backup(ctx, chosen.id, cfg.BackupsKeep, false)
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if err == nil {
		chosen.meta.LastBackupDate = date
		manager.persist(chosen)
	} else {
		log.Printf("nox-sandbox: daily backup: %v", err)
	}
	manager.setState(chosen, previous)
}
