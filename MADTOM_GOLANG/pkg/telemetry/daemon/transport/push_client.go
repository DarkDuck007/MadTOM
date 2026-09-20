package transport

import (
	"context"
	"log"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/config"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/spool"
	"github.com/DarkDuck007/madtom/pkg/telemetry/optin"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

// PushClient samples independently of the connection and sends one durable batch
// at a time. Only a matching successful ACK permits deleting that batch.
type PushClient struct {
	mu                    sync.RWMutex
	collectorAddr, nodeID string
	spoolDir              string
	wal                   *spool.WALManager
	engine                *collector.Engine
	config                *madtomv1.NodeConfig
	ctx                   context.Context
	cancel                context.CancelFunc
	wg                    sync.WaitGroup
	isConnected           bool
}

func NewPushClient(address, nodeID string, wal *spool.WALManager, engine *collector.Engine, cfg *madtomv1.NodeConfig, spoolDir ...string) *PushClient {
	if cfg == nil {
		cfg = collector.DefaultConfig(nodeID)
	}
	if engine != nil && address != "" {
		engine.SetCollectorAddr(address)
	}
	var sDir string
	if len(spoolDir) > 0 {
		sDir = spoolDir[0]
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &PushClient{collectorAddr: address, nodeID: nodeID, spoolDir: sDir, wal: wal, engine: engine, config: cfg, ctx: ctx, cancel: cancel}
}
func (p *PushClient) Start() { p.wg.Add(2); go p.sampleLoop(); go p.runLoop() }
func (p *PushClient) Stop()  { p.cancel(); p.wg.Wait() }
func (p *PushClient) sampleLoop() {
	defer p.wg.Done()
	for {
		p.mu.RLock()
		cfg := p.config
		connected := p.isConnected
		p.mu.RUnlock()

		sample := p.engine.Collect(cfg)
		// When offline, strip all MONITOR_ONLY and live-only metrics from the offline WAL backlog.
		// Offline WAL segments are strictly for catch-up replay into TSDB upon reconnect,
		// and TSDB never records MONITOR_ONLY metrics.
		if !connected && cfg != nil {
			optin.StripMonitorOnlyMetrics(sample, cfg)
		}

		if _, err := p.wal.WriteMetrics([]*madtomv1.SystemMetrics{sample}); err != nil {
			log.Printf("[PushClient] WAL append failed: %v", err)
		}
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
	backoff := time.Second
	for p.ctx.Err() == nil {
		hadActivity := p.connect()
		p.setConnected(false)
		if hadActivity {
			backoff = time.Second
		} else {
			if backoff < 15*time.Second {
				backoff *= 2
				if backoff > 15*time.Second {
					backoff = 15 * time.Second
				}
			}
		}
		if !wait(p.ctx, backoff) {
			return
		}
	}
}
func (p *PushClient) connect() bool {
	conn, err := grpc.NewClient(p.collectorAddr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return false
	}
	defer conn.Close()
	ctx, cancel := context.WithCancel(p.ctx)
	defer cancel()
	stream, err := madtomv1.NewIngestServiceClient(conn).PushBatchStream(ctx)
	if err != nil {
		return false
	}
	hadActivity := false
	for ctx.Err() == nil {
		batch, err := p.wal.ReadBatchChunk(spool.DefaultChunkMaxSamples)
		if err != nil {
			return hadActivity
		}
		if batch == nil {
			if !wait(ctx, 100*time.Millisecond) {
				return hadActivity
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
			return hadActivity
		}
		hadActivity = true
		if err = p.wal.AcknowledgeSegment(ack.SegmentId, ack.SegmentOffset); err != nil {
			return hadActivity
		}
		p.mu.Lock()
		if ack.Config != nil {
			p.config = ack.Config
			if p.spoolDir != "" {
				_ = config.SaveConfig(p.spoolDir, ack.Config)
			}
		}
		p.isConnected = true
		p.mu.Unlock()
	}
	return hadActivity
}
func (p *PushClient) setConnected(value bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.isConnected = value
}
func (p *PushClient) IsConnected() bool { p.mu.RLock(); defer p.mu.RUnlock(); return p.isConnected }
