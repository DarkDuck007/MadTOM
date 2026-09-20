package jobs

import (
	"sort"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"sync"
)

type Repository struct {
	mu    sync.RWMutex
	items map[string]*Job
}

func NewRepository() *Repository { return &Repository{items: make(map[string]*Job)} }
func (r *Repository) Add(j *Job) { r.mu.Lock(); defer r.mu.Unlock(); r.items[j.detail.JobID] = j }
func (r *Repository) Get(id string) (*Job, error) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	j, ok := r.items[id]
	if !ok {
		return nil, ErrNotFound
	}
	return j, nil
}
func (r *Repository) all() []*Job {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Job, 0, len(r.items))
	for _, j := range r.items {
		out = append(out, j)
	}
	return out
}
func (r *Repository) List(status map[string]bool) []types.JobDetail {
	out := []types.JobDetail{}
	for _, j := range r.all() {
		d := j.Snapshot()
		if len(status) == 0 || status[d.Status] {
			out = append(out, d)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].CreatedAt.Before(out[j].CreatedAt) })
	return out
}
func (r *Repository) delete(id string) { r.mu.Lock(); defer r.mu.Unlock(); delete(r.items, id) }
