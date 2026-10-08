package transport

import (
	"context"
	"log"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/config"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/logs"
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

	chunker               *logs.Chunker
	tailer                *logs.JournalTailer
	liveChunks            []*madtomv1.LogChunk
	lastCursor            string
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

	var lastSeq uint64
	var lastCur string
	if wal != nil {
		lastSeq, lastCur = wal.GetLogState()
	}
	chunker, _ := logs.NewChunker(nodeID, lastSeq)

	p := &PushClient{
		collectorAddr: address,
		nodeID:        nodeID,
		spoolDir:      sDir,
		wal:           wal,
		engine:        engine,
		config:        cfg,
		ctx:           ctx,
		cancel:        cancel,
		chunker:       chunker,
		lastCursor:    lastCur,
	}

	p.tailer = logs.NewJournalTailer(p.onLogRecord)
	return p
}

func (p *PushClient) onLogRecord(rec *madtomv1.LogRecord, cur string) {
	p.mu.Lock()
	defer p.mu.Unlock()

	p.lastCursor = cur
	if p.chunker == nil {
		return
	}

	sealed, err := p.chunker.AddRecord(rec)
	if err != nil {
		log.Printf("[PushClient] Chunker error: %v", err)
		return
	}

	if sealed != nil {
		if p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			if p.wal != nil {
				if _, err := p.wal.WriteBatch(nil, []*madtomv1.LogChunk{sealed}); err != nil {
					log.Printf("[PushClient] WAL log append failed: %v", err)
				}
				_ = p.wal.SetLogState(p.chunker.CurrentSeq(), cur)
			}
		}
		if p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY || p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			if p.isConnected {
				if len(p.liveChunks) < 64 {
					p.liveChunks = append(p.liveChunks, sealed)
				}
			}
		}
	}
}

func (p *PushClient) Start() {
	p.wg.Add(2)
	p.tailer.Start(p.config, p.lastCursor)
	go p.sampleLoop()
	go p.runLoop()
}

func (p *PushClient) Stop() {
	p.tailer.Stop()
	p.cancel()
	p.wg.Wait()

	p.mu.Lock()
	defer p.mu.Unlock()
	if p.chunker != nil {
		if sealed, _ := p.chunker.SealCurrent(); sealed != nil {
			if p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE && p.wal != nil {
				_, _ = p.wal.WriteBatch(nil, []*madtomv1.LogChunk{sealed})
				_ = p.wal.SetLogState(p.chunker.CurrentSeq(), p.lastCursor)
			}
		}
		p.chunker.Close()
	}
}
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
		// Attach live monitor chunks if in MONITOR_ONLY or MONITOR_AND_STORE mode
		p.mu.Lock()
		if p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY || p.config.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			if len(p.liveChunks) > 0 {
				batch.LogChunks = append(batch.LogChunks, p.liveChunks...)
				p.liveChunks = nil
			} else if unsealed, _ := p.chunker.BuildUnsealedChunk(); unsealed != nil {
				batch.LogChunks = append(batch.LogChunks, unsealed)
			}
		}
		batch.DaemonVersion = DaemonVersion
		batch.SupportedFeatures = SupportedFeatures
		batch.AckedConfigHash = optin.ComputeConfigHash(p.config)
		p.mu.Unlock()

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
			if p.tailer.NeedsRestart(ack.Config) {
				p.tailer.Start(ack.Config, p.lastCursor)
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
