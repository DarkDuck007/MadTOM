package manager

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"math"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/image"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

type Options struct {
	Backend vmm.Backend
	Mkfs    func(path string, sizeBytes int64) error
	Now     func() time.Time
}

type Manager struct {
	mu              sync.Mutex
	grants          grantStore
	cfg             *config.Effective
	host            config.Host
	backend         vmm.Backend
	mkfs            func(string, int64) error
	now             func() time.Time
	image           *image.Runner
	chats           map[int64]*chat
	jobs            map[string]*job
	slots           map[*slot]struct{}
	ready           map[vmm.Role][]*slot
	changed         chan struct{}
	ctx             context.Context
	cancel          context.CancelFunc
	workers         sync.WaitGroup
	started, closed bool
	lastBuildDay    string
	lastCPUSweep    time.Time
}

type chat struct {
	id     int64
	state  string
	since  time.Time
	born   time.Time
	active int
	jobs   int
	busy   map[string]bool
	slot   *slot
	meta   metadata
}

func (chat *chat) idle() bool {
	return chat.active == 0 && chat.jobs == 0 && (chat.state == "warm" || chat.state == "cold" || chat.state == "hibernated")
}

type slot struct {
	spec            vmm.Spec
	machine         vmm.Machine
	started         time.Time
	charge          *chat
	lastCPU         float64
	stopMu          sync.Mutex
	stopped         bool
	stopFailed      bool
	inspectorResume string
}

func New(cfg *config.Effective, opts Options) (*Manager, error) {
	if cfg == nil || cfg.Host.StateDir == "" || opts.Backend == nil {
		return nil, fmt.Errorf("state_dir and VM backend are required")
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.Mkfs == nil {
		opts.Mkfs = defaultMkfs
	}
	for _, dir := range []string{"chats", "pools", "backups"} {
		if err := os.MkdirAll(filepath.Join(cfg.Host.StateDir, dir), 0700); err != nil {
			return nil, err
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	manager := &Manager{cfg: cfg, host: cfg.Host, backend: opts.Backend, mkfs: opts.Mkfs, now: opts.Now, image: image.New(cfg.Host, opts.Now),
		chats: make(map[int64]*chat), jobs: make(map[string]*job), slots: make(map[*slot]struct{}), ready: make(map[vmm.Role][]*slot),
		changed: make(chan struct{}), ctx: ctx, cancel: cancel}
	if err := manager.loadChats(); err != nil {
		cancel()
		return nil, err
	}
	if err := manager.loadJobs(); err != nil {
		cancel()
		return nil, err
	}
	if err := manager.grants.load(filepath.Join(cfg.Host.StateDir, "grants.json")); err != nil {
		cancel()
		return nil, err
	}
	return manager, nil
}

func (manager *Manager) Config() *config.Effective {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	return manager.cfg
}
func (manager *Manager) Reload() {
	cfg := config.Reload(manager.host)
	manager.mu.Lock()
	defer manager.mu.Unlock()
	manager.cfg = cfg
	manager.notify()
}

func (manager *Manager) operation(ctx context.Context) (context.Context, func(), error) {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if manager.closed {
		return nil, nil, Err("vm_failed", "service is stopping")
	}
	manager.workers.Add(1)
	child, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(manager.ctx, cancel)
	return child, func() { stop(); cancel(); manager.workers.Done() }, nil
}

func (manager *Manager) Start(ctx context.Context) error {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if manager.closed {
		return fmt.Errorf("manager is closed")
	}
	if manager.started {
		return nil
	}
	manager.started = true
	context.AfterFunc(ctx, manager.cancel)
	manager.workers.Go(func() { manager.periodic(250*time.Millisecond, manager.sweep) })
	manager.workers.Go(func() { manager.periodic(time.Second, manager.fillPools) })
	manager.workers.Go(func() { manager.periodic(15*time.Second, manager.scheduled) })
	return nil
}

func (manager *Manager) periodic(interval time.Duration, step func(context.Context)) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for manager.ctx.Err() == nil {
		step(manager.ctx)
		select {
		case <-manager.ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (manager *Manager) Close(ctx context.Context) error {
	manager.mu.Lock()
	manager.closed = true
	manager.cancel()
	manager.notify()
	manager.mu.Unlock()
	done := make(chan struct{})
	go func() { manager.workers.Wait(); close(done) }()
	select {
	case <-done:
	case <-ctx.Done():
		return ctx.Err()
	}
	var result error
	manager.mu.Lock()
	var chats []*chat
	for _, chat := range manager.chats {
		if chat.slot != nil {
			chats = append(chats, chat)
		}
	}
	manager.mu.Unlock()
	for _, chat := range chats {
		manager.mu.Lock()
		slot := chat.slot
		if slot == nil {
			manager.mu.Unlock()
			continue
		}
		manager.setState(chat, "hibernating")
		manager.mu.Unlock()
		result = errors.Join(result, manager.hibernate(ctx, chat, slot, true))
	}
	manager.mu.Lock()
	var slots []*slot
	for slot := range manager.slots {
		slots = append(slots, slot)
	}
	manager.mu.Unlock()
	for _, slot := range slots {
		result = errors.Join(result, manager.stopSlot(slot))
	}
	return result
}

func randomID(size int) string {
	data := make([]byte, size)
	rand.Read(data)
	return hex.EncodeToString(data)
}

func (manager *Manager) chat(id int64) *chat {
	if item := manager.chats[id]; item != nil {
		return item
	}
	item := &chat{id: id, state: "cold", since: manager.now(), busy: make(map[string]bool), meta: metadata{Sessions: make(map[string]time.Time)}}
	manager.chats[id] = item
	return item
}

func (manager *Manager) notify() { close(manager.changed); manager.changed = make(chan struct{}) }
func (manager *Manager) setState(chat *chat, next string) {
	if chat.state != next {
		chat.state, chat.since = next, manager.now()
		manager.notify()
	}
}

func (manager *Manager) spec(role vmm.Role, id int64, version string, cfg config.Sandbox) vmm.Spec {
	spec := vmm.Spec{Role: role, ChatID: id, ImageVersion: version, KernelPath: manager.host.KernelPath,
		RootfsPath: filepath.Join(manager.host.ImagesDir, version, "rootfs.ext4"), DiskBwMBs: cfg.ChatVM.DiskBwMBs, DiskIOPS: cfg.ChatVM.DiskIOPS}
	switch role {
	case vmm.RoleChat:
		spec.MemMB, spec.VCPUs, spec.CPUQuotaPercent = cfg.ChatVM.MemMB, cfg.ChatVM.VCPUs, cfg.ChatVM.CPUQuotaPercent
		spec.DataDisk = manager.volume(id)
	case vmm.RoleTemp, vmm.RoleInspector:
		spec.MemMB, spec.VCPUs, spec.CPUQuotaPercent, spec.TmpMB = cfg.TempVM.MemMB, cfg.TempVM.VCPUs, cfg.TempVM.CPUQuotaPercent, cfg.TempVM.TmpDiskMB
		if role == vmm.RoleInspector {
			spec.DataDisk, spec.DataReadOnly = manager.volume(id), true
		}
	case vmm.RolePre:
		spec.MemMB, spec.VCPUs, spec.CPUQuotaPercent, spec.TmpMB = cfg.PreVM.MemMB, cfg.PreVM.VCPUs, cfg.PreVM.CPUQuotaPercent, min(cfg.PreVM.MemMB/4, 64)
	}
	return spec
}

func (manager *Manager) reserve(ctx context.Context, spec vmm.Spec, deadline time.Time, evict bool) (*slot, error) {
	if spec.ImageVersion == "" {
		return nil, Err("vm_failed", "no current sandbox image")
	}
	for attempt := 0; ; attempt++ {
		if ctx.Err() != nil {
			return nil, Err("timeout", "cancelled waiting for VM capacity")
		}
		if attempt > 0 && !time.Now().Before(deadline) {
			return nil, Err("capacity", "VM capacity wait expired")
		}
		manager.mu.Lock()
		cfg := manager.cfg.Sandbox
		mem := 0
		for slot := range manager.slots {
			mem += slot.spec.MemMB
		}
		if manager.closed {
			manager.mu.Unlock()
			return nil, Err("vm_failed", "service is stopping")
		}
		if spec.MemMB > cfg.Global.MaxGuestMemMB || cfg.Global.MaxVMs <= 0 {
			manager.mu.Unlock()
			return nil, Err("capacity", "VM exceeds host capacity")
		}
		if len(manager.slots) < cfg.Global.MaxVMs && mem <= cfg.Global.MaxGuestMemMB-spec.MemMB {
			spec.ID = "n-" + randomID(8)
			reservation := &slot{spec: spec, started: manager.now()}
			manager.slots[reservation] = struct{}{}
			manager.mu.Unlock()
			return reservation, nil
		}
		if evict && !time.Now().Before(deadline) {
			manager.mu.Unlock()
			return nil, Err("capacity", "VM capacity wait expired")
		}
		var victim *chat
		var pool *slot
		if evict {
			for _, role := range []vmm.Role{vmm.RoleTemp, vmm.RolePre} {
				if ready := manager.ready[role]; len(ready) > 0 {
					pool, manager.ready[role] = ready[len(ready)-1], ready[:len(ready)-1]
					break
				}
			}
			if pool == nil {
				for _, item := range manager.chats {
					if item.state == "warm" && item.idle() && (victim == nil || item.meta.LastUsed.Before(victim.meta.LastUsed)) {
						victim = item
					}
				}
				if victim != nil {
					manager.setState(victim, "hibernating")
				}
			}
		}
		changed := manager.changed
		var victimSlot *slot
		if victim != nil {
			victimSlot = victim.slot
		}
		manager.mu.Unlock()
		if pool != nil {
			if err := manager.stopSlot(pool); err != nil {
				return nil, Err("vm_failed", "pool eviction failed")
			}
			continue
		}
		if victimSlot != nil {
			cleanup, cancel := context.WithTimeout(ctx, max(0, time.Until(deadline)))
			err := manager.hibernate(cleanup, victim, victimSlot, true)
			cancel()
			if err != nil {
				log.Printf("nox-sandbox: LRU hibernate: %v", err)
			}
			continue
		}
		if !evict {
			return nil, Err("capacity", "no spare pool capacity")
		}
		if err := wait(ctx, changed, deadline); err != nil {
			return nil, err
		}
	}
}

func wait(ctx context.Context, changed <-chan struct{}, deadline time.Time) error {
	if !time.Now().Before(deadline) {
		return Err("capacity", "VM capacity wait expired")
	}
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()
	select {
	case <-changed:
		return nil
	case <-ctx.Done():
		return Err("timeout", "VM wait cancelled")
	case <-timer.C:
		return Err("capacity", "VM capacity wait expired")
	}
}

func (manager *Manager) client(slot *slot) agent.Client {
	maximum := manager.host.Ceilings.MaxFrameBytes
	if maximum <= 0 {
		maximum = 128 << 20
	}
	return agent.Client{Machine: slot.machine, MaxFrameBytes: maximum}
}

func (manager *Manager) launch(ctx context.Context, slot *slot, snapshot *vmm.Snapshot) error {
	ctx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()
	var machine vmm.Machine
	var err error
	if snapshot != nil {
		machine, err = manager.backend.Restore(ctx, slot.spec, *snapshot)
	} else {
		machine, err = manager.backend.Boot(ctx, slot.spec)
	}
	manager.mu.Lock()
	slot.machine = machine
	manager.mu.Unlock()
	if err != nil {
		return errors.Join(err, manager.stopSlot(slot))
	}
	seed := make([]byte, 64)
	rand.Read(seed)
	cfg := manager.Config().Sandbox
	if err := manager.client(slot).Init(ctx, manager.now(), seed, slot.spec.Role, cfg.ChatVM.PidsMax, cfg.MaxOutputBytes); err != nil {
		return errors.Join(err, manager.stopSlot(slot))
	}
	if snapshot != nil && slot.spec.Role == vmm.RoleChat {
		if err := machine.SetBalloon(ctx, 0); err != nil {
			return errors.Join(err, manager.stopSlot(slot))
		}
	}
	return nil
}

func (manager *Manager) rollover(chat *chat) {
	day := manager.now().Format("2006-01-02")
	if chat.meta.CPUDate != day {
		chat.meta.CPUDate, chat.meta.CPUSeconds = day, 0
	}
}

func (manager *Manager) charge(slot *slot, chat *chat) {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	slot.charge = chat
}

func (manager *Manager) sample(slot *slot) bool {
	if slot == nil || slot.machine == nil || slot.charge == nil {
		return false
	}
	value, err := slot.machine.CPUSeconds()
	if err != nil || value < 0 || math.IsNaN(value) || math.IsInf(value, 0) {
		return false
	}
	date := slot.charge.meta.CPUDate
	manager.rollover(slot.charge)
	delta := max(0, value-slot.lastCPU)
	slot.lastCPU = value
	if date == slot.charge.meta.CPUDate || date == "" {
		slot.charge.meta.CPUSeconds += delta
	}
	return delta > 0 || date != slot.charge.meta.CPUDate
}

func (manager *Manager) stopSlot(slot *slot) error {
	if slot == nil {
		return nil
	}
	slot.stopMu.Lock()
	defer slot.stopMu.Unlock()
	if slot.stopped {
		return nil
	}
	manager.mu.Lock()
	if manager.sample(slot) {
		manager.persist(slot.charge)
	}
	machine := slot.machine
	manager.mu.Unlock()
	var err error
	if machine != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		err = machine.Stop(ctx)
		if err != nil {
			select {
			case <-machine.Done():
			default:
				manager.mu.Lock()
				slot.stopFailed = true
				manager.mu.Unlock()
				return err
			}
		}
	}
	slot.stopped = true
	manager.mu.Lock()
	delete(manager.slots, slot)
	manager.notify()
	manager.mu.Unlock()
	return err
}

func (manager *Manager) invalidate(chat *chat) {
	chat.meta.Reset = chat.meta.Reset || len(chat.meta.Sessions) > 0 || chat.meta.Snapshot != nil
	chat.meta.Snapshot = nil
	chat.meta.Sessions = make(map[string]time.Time)
}

func (manager *Manager) finishCold(chat *chat, expected *slot) {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if chat.slot != expected {
		return
	}
	manager.finishColdLocked(chat)
}

func (manager *Manager) finishColdLocked(chat *chat) {
	if chat.slot != nil {
		if _, alive := manager.slots[chat.slot]; alive {
			manager.setState(chat, "stopping")
			return
		}
	}
	chat.slot = nil
	manager.invalidate(chat)
	_ = os.RemoveAll(manager.snapshotDir(chat.id))
	manager.persist(chat)
	manager.setState(chat, "cold")
}

func (manager *Manager) hibernate(ctx context.Context, chat *chat, slot *slot, allowSnapshot bool) error {
	cfg := manager.Config().Sandbox
	var stored *savedSnapshot
	var failure error
	if allowSnapshot && cfg.Hibernate && cfg.SnapshotQuotaMBPerChat > 0 && manager.diskPercent() < cfg.DiskWatermarkPercent {
		snapshotCtx, cancel := context.WithTimeout(ctx, 40*time.Second)
		stored, failure = manager.snapshot(snapshotCtx, chat, slot, cfg)
		cancel()
	}
	stopErr := manager.stopSlot(slot)
	if stored == nil || stopErr != nil {
		manager.finishCold(chat, slot)
		return errors.Join(failure, stopErr)
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	chat.slot = nil
	chat.meta.Snapshot = stored
	if err := manager.save(chat); err != nil {
		manager.invalidate(chat)
		_ = os.RemoveAll(manager.snapshotDir(chat.id))
		manager.setState(chat, "cold")
		return err
	}
	manager.setState(chat, "hibernated")
	return nil
}

func (manager *Manager) snapshot(ctx context.Context, chat *chat, slot *slot, cfg config.Sandbox) (*savedSnapshot, error) {
	client := manager.client(slot)
	if err := manager.refreshManifest(ctx, chat, slot, cfg.MaxFileListEntries); err != nil {
		var protocol *agent.ProtocolError
		if errors.As(err, &protocol) {
			return nil, err
		}
	}
	if err := client.PrepareHibernate(ctx); err != nil {
		return nil, err
	}
	if err := slot.machine.SetBalloon(ctx, max(0, slot.spec.MemMB-128)); err != nil {
		return nil, err
	}
	if err := slot.machine.Pause(ctx); err != nil {
		return nil, err
	}
	dir := manager.snapshotDir(chat.id)
	if err := os.RemoveAll(dir); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return nil, err
	}
	snapshot, err := slot.machine.Snapshot(ctx, dir)
	if err != nil {
		return nil, err
	}
	if snapshot.MemBytes < 0 || snapshot.MemBytes/(1<<20) > int64(cfg.SnapshotQuotaMBPerChat) ||
		(snapshot.MemBytes/(1<<20) == int64(cfg.SnapshotQuotaMBPerChat) && snapshot.MemBytes%(1<<20) != 0) || manager.diskPercent() >= cfg.DiskWatermarkPercent {
		return nil, nil
	}
	snapshot.Dir, snapshot.ImageVersion = dir, slot.spec.ImageVersion
	return &savedSnapshot{snapshot, slot.spec, manager.now()}, nil
}

func dead(slot *slot) bool {
	if slot == nil || slot.machine == nil {
		return false
	}
	select {
	case <-slot.machine.Done():
		return true
	default:
		return false
	}
}

func (manager *Manager) sweep(ctx context.Context) {
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if manager.closed {
		return
	}
	cfg, now := manager.cfg.Sandbox, manager.now()
	hibernating := false
	for _, item := range manager.chats {
		if item.state == "hibernated" && item.meta.Snapshot != nil && !now.Before(item.meta.Snapshot.Created.Add(time.Duration(cfg.HibernateTTLHours)*time.Hour)) {
			manager.invalidate(item)
			_ = os.RemoveAll(manager.snapshotDir(item.id))
			manager.persist(item)
			manager.setState(item, "cold")
		}
		if item.slot == nil {
			continue
		}
		expired := !now.Before(item.born.Add(time.Duration(cfg.MaxSessionLifetimeMinutes) * time.Minute))
		if (item.state == "warm" || item.state == "busy") && (expired || dead(item.slot)) {
			slot := item.slot
			manager.setState(item, "stopping")
			manager.workers.Go(func() {
				if err := manager.stopSlot(slot); err != nil {
					log.Printf("nox-sandbox: stop guest: %v", err)
				}
				manager.finishCold(item, slot)
			})
		} else if !hibernating && item.state == "warm" && item.idle() && !now.Before(item.meta.LastUsed.Add(time.Duration(cfg.WarmSeconds)*time.Second)) {
			hibernating = true
			slot := item.slot
			manager.setState(item, "hibernating")
			manager.workers.Go(func() {
				if err := manager.hibernate(ctx, item, slot, true); err != nil {
					log.Printf("nox-sandbox: idle hibernate: %v", err)
				}
			})
		}
	}
	if time.Since(manager.lastCPUSweep) >= time.Second {
		manager.lastCPUSweep = time.Now()
		for slot := range manager.slots {
			if manager.sample(slot) {
				manager.persist(slot.charge)
			}
			if slot.stopFailed {
				slot.stopFailed = false
				manager.workers.Go(func() { manager.retryStop(slot) })
			}
		}
	}
}

func (manager *Manager) retryStop(slot *slot) {
	if err := manager.stopSlot(slot); err != nil {
		log.Printf("nox-sandbox: retry VM stop: %v", err)
	}
	manager.mu.Lock()
	defer manager.mu.Unlock()
	if _, alive := manager.slots[slot]; alive {
		return
	}
	chat := manager.chats[slot.spec.ChatID]
	if chat == nil || chat.state != "stopping" {
		return
	}
	if chat.slot == slot {
		manager.finishColdLocked(chat)
	} else if chat.slot == nil && slot.inspectorResume != "" {
		manager.setState(chat, slot.inspectorResume)
	}
}
