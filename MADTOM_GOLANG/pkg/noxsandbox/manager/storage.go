package manager

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
	"golang.org/x/sys/unix"
)

type savedSnapshot struct {
	Snapshot vmm.Snapshot
	Spec     vmm.Spec
	Created  time.Time
}
type metadata struct {
	Snapshot       *savedSnapshot
	Manifest       agent.Manifest
	ManifestAt     *time.Time
	CPUDate        string
	CPUSeconds     float64
	NextSession    uint64
	Sessions       map[string]time.Time
	LastBackupDate string
	LastUsed       time.Time
	Reset          bool
}

func (manager *Manager) chatDir(id int64) string {
	return filepath.Join(manager.host.StateDir, "chats", strconv.FormatInt(id, 10))
}
func (manager *Manager) volume(id int64) string {
	return filepath.Join(manager.chatDir(id), "volume.ext4")
}
func (manager *Manager) snapshotDir(id int64) string {
	return filepath.Join(manager.chatDir(id), "snapshot")
}
func (manager *Manager) backupDir(id int64) string {
	return filepath.Join(manager.host.StateDir, "backups", strconv.FormatInt(id, 10))
}

func writeJSON(path string, data any) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(path), ".meta-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	encodeErr := json.NewEncoder(file).Encode(data)
	if err := errors.Join(encodeErr, file.Sync(), file.Close()); err != nil {
		return err
	}
	if err := os.Rename(file.Name(), path); err != nil {
		return err
	}
	parent, err := os.Open(filepath.Dir(path))
	if err != nil {
		return err
	}
	defer parent.Close()
	return parent.Sync()
}

func (manager *Manager) save(chat *chat) error {
	return writeJSON(filepath.Join(manager.chatDir(chat.id), "meta.json"), chat.meta)
}
func (manager *Manager) persist(chat *chat) {
	if err := manager.save(chat); err != nil {
		log.Printf("nox-sandbox: persist chat %d: %v", chat.id, err)
	}
}

func (manager *Manager) loadChats() error {
	entries, err := os.ReadDir(filepath.Join(manager.host.StateDir, "chats"))
	if err != nil {
		return err
	}
	for _, entry := range entries {
		id, err := strconv.ParseInt(entry.Name(), 10, 64)
		if err != nil || !entry.IsDir() || strconv.FormatInt(id, 10) != entry.Name() {
			continue
		}
		chat := manager.chat(id)
		data, err := os.ReadFile(filepath.Join(manager.chatDir(id), "meta.json"))
		if err == nil {
			if err := json.Unmarshal(data, &chat.meta); err != nil {
				return fmt.Errorf("chat %d metadata: %w", id, err)
			}
		} else if !os.IsNotExist(err) {
			return err
		}
		if chat.meta.Sessions == nil {
			chat.meta.Sessions = make(map[string]time.Time)
		}
		if chat.meta.Snapshot != nil {
			chat.meta.Snapshot.Snapshot.Dir = manager.snapshotDir(id)
			chat.state = "hibernated"
			chat.since = chat.meta.Snapshot.Created
		} else if len(chat.meta.Sessions) > 0 {
			chat.meta.Sessions = make(map[string]time.Time)
			chat.meta.Reset = true
		}
	}
	return nil
}

func defaultMkfs(path string, _ int64) error {
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	command := exec.CommandContext(ctx, "mkfs.ext4", "-q", "-F", "-E", "root_owner=1000:1000,nodiscard", path)
	if data, err := command.CombinedOutput(); err != nil {
		return fmt.Errorf("mkfs.ext4: %w: %s", err, data)
	}
	return nil
}

func (manager *Manager) ensureVolume(chatID int64, size int64) error {
	path := manager.volume(chatID)
	info, err := os.Stat(path)
	if err == nil {
		if info.Size() < size {
			return os.Truncate(path, size)
		}
		return nil
	}
	if !os.IsNotExist(err) {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(path), ".volume-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	if err := errors.Join(file.Truncate(size), file.Close()); err != nil {
		return err
	}
	if err := manager.mkfs(file.Name(), size); err != nil {
		return err
	}
	return os.Rename(file.Name(), path)
}

func (manager *Manager) diskPercent() int {
	var stats unix.Statfs_t
	if err := unix.Statfs(manager.host.StateDir, &stats); err != nil || stats.Blocks == 0 {
		return 100
	}
	return int(math.Ceil(100 * (1 - float64(stats.Bavail)/float64(stats.Blocks))))
}

var validBackup = regexp.MustCompile(`^[0-9]{8}T[0-9]{6}Z(-[a-f0-9]{12})?$`)

func (manager *Manager) Backups(chatID int64) ([]Backup, error) {
	result := []Backup{}
	entries, err := os.ReadDir(manager.backupDir(chatID))
	if os.IsNotExist(err) {
		return result, nil
	}
	if err != nil {
		return nil, err
	}
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".ext4")
		if !ok || !validBackup.MatchString(id) || !entry.Type().IsRegular() {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			return nil, err
		}
		when, err := time.Parse("20060102T150405Z", id[:16])
		if err != nil {
			continue
		}
		result = append(result, Backup{id, when, info.Size()})
	}
	slices.SortFunc(result, func(first, second Backup) int {
		if cmp := second.CreatedAt.Compare(first.CreatedAt); cmp != 0 {
			return cmp
		}
		return strings.Compare(second.ID, first.ID)
	})
	return result, nil
}

func (manager *Manager) backup(ctx context.Context, chatID int64, keep int, force bool) error {
	dir := manager.backupDir(chatID)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	id := manager.now().UTC().Format("20060102T150405Z") + "-" + randomID(6)
	staging := filepath.Join(dir, "."+id)
	defer os.Remove(staging)
	if err := vmm.CopySparse(ctx, manager.volume(chatID), staging); err != nil {
		return err
	}
	if err := os.Rename(staging, filepath.Join(dir, id+".ext4")); err != nil {
		return err
	}
	if force {
		return nil
	}
	backups, err := manager.Backups(chatID)
	if err != nil {
		return err
	}
	for _, backup := range backups[min(keep, len(backups)):] {
		if err := os.Remove(filepath.Join(dir, backup.ID+".ext4")); err != nil {
			return err
		}
	}
	return nil
}

func (manager *Manager) RestoreBackup(ctx context.Context, chatID int64, id string) error {
	if !validBackup.MatchString(id) {
		return Err("not_found", "backup not found")
	}
	ctx, finish, err := manager.operation(ctx)
	if err != nil {
		return err
	}
	defer finish()
	path := filepath.Join(manager.backupDir(chatID), id+".ext4")
	manager.mu.Lock()
	if _, err := os.Stat(path); err != nil {
		manager.mu.Unlock()
		if os.IsNotExist(err) {
			return Err("not_found", "backup not found")
		}
		return err
	}
	chat := manager.chat(chatID)
	if !chat.idle() {
		manager.mu.Unlock()
		return Err("busy", "chat is in use")
	}
	if manager.diskPercent() >= manager.cfg.Sandbox.DiskWatermarkPercent {
		manager.mu.Unlock()
		return Err("capacity", "disk watermark reached")
	}
	manager.setState(chat, "stopping")
	slot := chat.slot
	manager.mu.Unlock()
	defer manager.finishCold(chat, slot)
	if err := manager.stopSlot(slot); err != nil {
		return err
	}
	if _, err := os.Stat(manager.volume(chatID)); err == nil {
		if err := manager.backup(ctx, chatID, 0, true); err != nil {
			return err
		}
	} else if !os.IsNotExist(err) {
		return err
	}
	staging := filepath.Join(manager.chatDir(chatID), ".restore-"+randomID(6))
	defer os.Remove(staging)
	if err := vmm.CopySparse(ctx, path, staging); err != nil {
		return err
	}
	if volume, err := os.Stat(manager.volume(chatID)); err == nil {
		if restored, err := os.Stat(staging); err == nil && volume.Size() > restored.Size() {
			if err := os.Truncate(staging, volume.Size()); err != nil {
				return err
			}
		}
	}
	manager.mu.Lock()
	manager.invalidate(chat)
	chat.meta.Manifest = agent.Manifest{}
	chat.meta.ManifestAt = nil
	err = manager.save(chat)
	manager.mu.Unlock()
	if err != nil {
		return err
	}
	return os.Rename(staging, manager.volume(chatID))
}
