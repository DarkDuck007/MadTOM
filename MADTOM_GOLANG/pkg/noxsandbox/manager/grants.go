package manager

import (
	"encoding/json"
	"errors"
	"os"
	"slices"
	"sync"
	"time"
)

// Runtime chat grants: chats an admin enabled with NoxChat's /sandbox command. They are persisted in
// state_dir/grants.json and add to (never replace) the static `enabled_chats` list in config.yaml.
type Grant struct {
	ChatID    int64     `json:"chat_id"`
	GrantedBy string    `json:"granted_by"`
	GrantedAt time.Time `json:"granted_at"`
}

type grantStore struct {
	mu    sync.Mutex
	path  string
	chats map[int64]Grant
}

func (store *grantStore) load(path string) error {
	store.mu.Lock()
	defer store.mu.Unlock()
	store.path, store.chats = path, map[int64]Grant{}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	var list []Grant
	if err := json.Unmarshal(data, &list); err != nil {
		return err
	}
	for _, grant := range list {
		if grant.ChatID != 0 {
			store.chats[grant.ChatID] = grant
		}
	}
	return nil
}

func (store *grantStore) listLocked() []Grant {
	list := make([]Grant, 0, len(store.chats))
	for _, grant := range store.chats {
		list = append(list, grant)
	}
	slices.SortFunc(list, func(a, b Grant) int { return a.GrantedAt.Compare(b.GrantedAt) })
	return list
}

func (manager *Manager) Granted(chatID int64) bool {
	manager.grants.mu.Lock()
	defer manager.grants.mu.Unlock()
	_, ok := manager.grants.chats[chatID]
	return ok
}

func (manager *Manager) Grants() []Grant {
	manager.grants.mu.Lock()
	defer manager.grants.mu.Unlock()
	return manager.grants.listLocked()
}

// SetGrant enables or disables a chat at runtime. Disabling does not delete the chat's volume; running
// VMs of the chat are left alone but every new exec/pre-run is refused.
func (manager *Manager) SetGrant(chatID int64, enabled bool, by string) error {
	if chatID == 0 {
		return Err("bad_request", "chat_id is required")
	}
	if len(by) > 128 {
		by = by[:128]
	}
	store := &manager.grants
	store.mu.Lock()
	defer store.mu.Unlock()
	previous, had := store.chats[chatID]
	if enabled {
		if had {
			return nil
		}
		store.chats[chatID] = Grant{ChatID: chatID, GrantedBy: by, GrantedAt: manager.now().UTC()}
	} else {
		if !had {
			return nil
		}
		delete(store.chats, chatID)
	}
	if err := writeJSON(store.path, store.listLocked()); err != nil {
		if had {
			store.chats[chatID] = previous
		} else {
			delete(store.chats, chatID)
		}
		return err
	}
	return nil
}
