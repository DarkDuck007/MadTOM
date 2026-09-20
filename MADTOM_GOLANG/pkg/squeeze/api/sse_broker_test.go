package api

import (
	"sync"
	"testing"
)

func TestBrokerFanoutAndDisconnect(t *testing.T) {
	b := NewBroker()
	a, closeA := b.Subscribe("a")
	a2, closeA2 := b.Subscribe("a")
	other, closeOther := b.Subscribe("b")
	defer closeA2()
	defer closeOther()
	b.Publish("a", "log", "hello")
	if (<-a).Data != "hello" || (<-a2).Data != "hello" {
		t.Fatal("fanout failed")
	}
	select {
	case <-other:
		t.Fatal("cross-job event")
	default:
	}
	closeA()
	closeA()
	if _, ok := <-a; ok {
		t.Fatal("channel not closed")
	}
}
func TestBrokerSlowClient(t *testing.T) {
	b := NewBroker()
	ch, closeCh := b.Subscribe("job")
	defer closeCh()
	for i := 0; i < 1000; i++ {
		b.Publish("job", "progress", i)
	}
	n := 0
	for range ch {
		n++
	}
	if n != 64 {
		t.Fatal(n)
	}
}
func TestBrokerConcurrent(t *testing.T) {
	b := NewBroker()
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, done := b.Subscribe("job")
			for n := 0; n < 20; n++ {
				b.Publish("job", "log", n)
			}
			done()
		}()
	}
	wg.Wait()
	b.Forget("job")
}
