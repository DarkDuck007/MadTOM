package auth

import (
	"container/heap"
	"sync"
	"time"
)

type replayKey struct {
	ts    int64
	nonce string
}

type replayEntry struct {
	key    replayKey
	seenAt time.Time
	order  uint64
}

type replayEntries []replayEntry

func (q replayEntries) Len() int { return len(q) }
func (q replayEntries) Less(i, j int) bool {
	if q[i].seenAt.Equal(q[j].seenAt) {
		return q[i].order < q[j].order
	}
	return q[i].seenAt.Before(q[j].seenAt)
}
func (q replayEntries) Swap(i, j int) { q[i], q[j] = q[j], q[i] }
func (q *replayEntries) Push(x any)   { *q = append(*q, x.(replayEntry)) }
func (q *replayEntries) Pop() any {
	last := len(*q) - 1
	entry := (*q)[last]
	(*q)[last] = replayEntry{}
	*q = (*q)[:last]
	return entry
}

// ReplayCache remembers timestamp and nonce pairs for a bounded time and capacity.
type ReplayCache struct {
	mu         sync.Mutex
	window     time.Duration
	maxEntries int
	seen       map[replayKey]struct{}
	entries    replayEntries
	order      uint64
}

func NewReplayCache(window time.Duration, maxEntries int) *ReplayCache {
	return &ReplayCache{
		window:     window,
		maxEntries: max(1, maxEntries),
		seen:       make(map[replayKey]struct{}),
	}
}

// Seen checks and records a pair atomically. Replays do not extend its lifetime.
func (c *ReplayCache) Seen(ts int64, nonce string, now time.Time) bool {
	c.mu.Lock()
	defer c.mu.Unlock()

	for len(c.entries) > 0 && now.Sub(c.entries[0].seenAt) > c.window {
		c.evictOldest()
	}
	key := replayKey{ts: ts, nonce: nonce}
	if _, ok := c.seen[key]; ok {
		return true
	}
	if len(c.entries) == c.maxEntries {
		c.evictOldest()
	}
	c.order++
	heap.Push(&c.entries, replayEntry{key: key, seenAt: now, order: c.order})
	c.seen[key] = struct{}{}
	return false
}

func (c *ReplayCache) evictOldest() {
	delete(c.seen, heap.Pop(&c.entries).(replayEntry).key)
}

func (c *ReplayCache) Len() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.entries)
}
