package transport

import (
	"context"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/daemon/spool"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

// PushClient samples independently of the connection and sends one durable batch
// at a time. Only a matching successful ACK permits deleting that batch.
type PushClient struct {
	mu                    sync.RWMutex
	collectorAddr, nodeID string
	wal                   *spool.WALManager
	engine                *collector.Engine
	config                *madtomv1.NodeConfig
	ctx                   context.Context
	cancel                context.CancelFunc
	wg                    sync.WaitGroup
	isConnected           bool
}

func NewPushClient(address, nodeID string, wal *spool.WALManager, engine *collector.Engine, cfg *madtomv1.NodeConfig) *PushClient {
	if cfg == nil {
		cfg = collector.DefaultConfig(nodeID)
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &PushClient{collectorAddr: address, nodeID: nodeID, wal: wal, engine: engine, config: cfg, ctx: ctx, cancel: cancel}
}
func (p *PushClient) Start() { p.wg.Add(2); go p.sampleLoop(); go p.runLoop() }
func (p *PushClient) Stop()  { p.cancel(); p.wg.Wait() }
func (p *PushClient) sampleLoop() {
	defer p.wg.Done()
	for {
		p.mu.RLock()
		cfg := p.config
		p.mu.RUnlock()
		_, _ = p.wal.WriteMetrics([]*madtomv1.SystemMetrics{p.engine.Collect(cfg)})
		if !wait(p.ctx, pollInterval(cfg)) {
			return
		}
	}
}
func pollInterval(cfg *madtomv1.NodeConfig) time.Duration {
	if cfg.FastPollIntervalMs < 100 {
		return time.Second
	}
	return time.Duration(cfg.FastPollIntervalMs) * time.Millisecond
}
func wait(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}
func (p *PushClient) runLoop() {
	defer p.wg.Done()
	for p.ctx.Err() == nil {
		p.connect()
		p.setConnected(false)
		if !wait(p.ctx, time.Second) {
			return
		}
	}
}
func (p *PushClient) connect() {
	conn, err := grpc.NewClient(p.collectorAddr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return
	}
	defer conn.Close()
	ctx, cancel := context.WithCancel(p.ctx)
	defer cancel()
	stream, err := madtomv1.NewIngestServiceClient(conn).PushBatchStream(ctx)
	if err != nil {
		return
	}
	for ctx.Err() == nil {
		batch, err := p.wal.ReadBatchChunk(spool.DefaultChunkMaxSamples)
		if err != nil {
			return
		}
		if batch == nil {
			if !wait(ctx, 100*time.Millisecond) {
				return
			}
			continue
		}
		// Bound ACK waits and blocked sends; cancellation also unblocks Recv.
		timeout := time.AfterFunc(10*time.Second, cancel)
		err = stream.Send(batch)
		var ack *madtomv1.BatchAck
		if err == nil {
			ack, err = stream.Recv()
		}
		timeout.Stop()
		if err != nil || ack == nil || !ack.Success || ack.NodeId != batch.NodeId || ack.SegmentId != batch.SegmentId || ack.SegmentOffset != batch.SegmentOffset {
			return
		}
		if err = p.wal.AcknowledgeSegment(ack.SegmentId); err != nil {
			return
		}
		p.mu.Lock()
		if ack.Config != nil {
			p.config = ack.Config
		}
		p.isConnected = true
		p.mu.Unlock()
	}
}
func (p *PushClient) setConnected(value bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.isConnected = value
}
func (p *PushClient) IsConnected() bool { p.mu.RLock(); defer p.mu.RUnlock(); return p.isConnected }
