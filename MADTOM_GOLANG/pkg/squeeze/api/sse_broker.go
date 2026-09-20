package api

import (
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"sync"
)

type topic struct {
	sequence    uint64
	subscribers map[chan types.Event]struct{}
}
type SSEBroker struct {
	mu     sync.Mutex
	topics map[string]*topic
}

func NewBroker() *SSEBroker { return &SSEBroker{topics: make(map[string]*topic)} }
func (b *SSEBroker) Subscribe(id string) (<-chan types.Event, func()) {
	b.mu.Lock()
	defer b.mu.Unlock()
	t := b.topics[id]
	if t == nil {
		t = &topic{subscribers: make(map[chan types.Event]struct{})}
		b.topics[id] = t
	}
	ch := make(chan types.Event, 64)
	t.subscribers[ch] = struct{}{}
	return ch, func() {
		b.mu.Lock()
		defer b.mu.Unlock()
		if _, ok := t.subscribers[ch]; ok {
			delete(t.subscribers, ch)
			close(ch)
		}
	}
}
func (b *SSEBroker) Publish(id, kind string, data any) {
	b.mu.Lock()
	defer b.mu.Unlock()
	t := b.topics[id]
	if t == nil {
		return
	}
	t.sequence++
	event := types.Event{ID: t.sequence, Type: kind, Data: data}
	for ch := range t.subscribers {
		select {
		case ch <- event:
		default:
			delete(t.subscribers, ch)
			close(ch)
		}
	}
}
func (b *SSEBroker) Forget(id string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if t := b.topics[id]; t != nil {
		for ch := range t.subscribers {
			delete(t.subscribers, ch)
			close(ch)
		}
		delete(b.topics, id)
	}
}
