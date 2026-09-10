package ingest

import (
	"context"
	"log"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

type ScrapeTarget struct {
	NodeID          string
	Address         string
	Interval        time.Duration
	LastAckedSeg    string
	LastAckedOffset int64
	conn            *grpc.ClientConn
}

// PullScraper periodically scrapes pull-mode node daemons.
type PullScraper struct {
	mu       sync.Mutex
	pipeline *Pipeline
	targets  []*ScrapeTarget
	stopChan chan struct{}
	ctx      context.Context
	cancel   context.CancelFunc
	wg       sync.WaitGroup
	stopOnce sync.Once
}

// NewPullScraper initializes the scraper scheduler.
func NewPullScraper(pipeline *Pipeline) *PullScraper {
	ctx, cancel := context.WithCancel(context.Background())
	return &PullScraper{
		ctx: ctx, cancel: cancel,
		pipeline: pipeline,
		stopChan: make(chan struct{}),
	}
}

// AddTarget registers a pull node endpoint to scrape.
func (p *PullScraper) AddTarget(nodeID, address string, interval time.Duration) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.targets = append(p.targets, &ScrapeTarget{
		NodeID:   nodeID,
		Address:  address,
		Interval: interval,
	})
}

// Start initiates scraping loops for all configured targets.
func (p *PullScraper) Start() {
	p.mu.Lock()
	defer p.mu.Unlock()

	for _, target := range p.targets {
		p.wg.Add(1)
		go func() { defer p.wg.Done(); p.scrapeLoop(target) }()
	}
}

// Stop terminates all scraping loops.
func (p *PullScraper) Stop() {
	p.stopOnce.Do(func() { close(p.stopChan); p.cancel() })
	p.wg.Wait()
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, target := range p.targets {
		if target.conn != nil {
			_ = target.conn.Close()
			target.conn = nil
		}
	}
}

func (p *PullScraper) scrapeLoop(target *ScrapeTarget) {
	ticker := time.NewTicker(target.Interval)
	defer ticker.Stop()

	for {
		select {
		case <-p.stopChan:
			return
		case <-ticker.C:
			p.executeScrape(target)
		}
	}
}

func (p *PullScraper) executeScrape(target *ScrapeTarget) {
	if target.conn == nil {
		dialCtx, dialCancel := context.WithTimeout(p.ctx, 3*time.Second)
		conn, err := grpc.DialContext(dialCtx, target.Address, grpc.WithTransportCredentials(insecure.NewCredentials()), grpc.WithBlock())
		dialCancel()
		if err != nil {
			log.Printf("[Scraper] Failed to dial node %s at %s: %v", target.NodeID, target.Address, err)
			return
		}
		target.conn = conn
	}

	client := madtomv1.NewIngestServiceClient(target.conn)
	req := &madtomv1.PollRequest{
		NodeId:             target.NodeID,
		LastAckedSegmentId: target.LastAckedSeg,
		LastAckedOffset:    target.LastAckedOffset,
		MaxSamples:         500,
		Config:             p.pipeline.reg.TransportConfig(target.NodeID),
	}

	// Drain up to the requested budget on each poll cycle, otherwise a 5s
	// scrape interval can never catch up with the daemon's 1s sampling cadence.
	for i := 0; i < 100; i++ {
		// Each RPC gets its own budget; database writes and earlier backlog
		// requests must not consume the next request's deadline.
		ctx, cancel := context.WithTimeout(p.ctx, 5*time.Second)
		batch, err := client.PollTelemetry(ctx, req)
		cancel()
		if err != nil {
			log.Printf("[Scraper] Poll error from %s at %s after %d batches this cycle: %v", target.NodeID, target.Address, i, err)
			_ = target.conn.Close()
			target.conn = nil
			return
		}
		if batch == nil || batch.NodeId != target.NodeID {
			log.Printf("[Scraper] Unexpected batch identity from %s at %s: %q", target.NodeID, target.Address, batch.GetNodeId())
			_ = target.conn.Close()
			target.conn = nil
			return
		}
		if err := p.pipeline.ProcessBatch(batch, "PULL"); err != nil {
			log.Printf("[Scraper] Ingestion failed for %s segment %s: %v", target.NodeID, batch.SegmentId, err)
			return
		}
		target.LastAckedSeg = batch.SegmentId
		target.LastAckedOffset = batch.SegmentOffset
		req.LastAckedSegmentId = batch.SegmentId
		req.LastAckedOffset = batch.SegmentOffset
		if len(batch.Samples) > 0 && time.Since(time.Unix(0, batch.Samples[len(batch.Samples)-1].TimestampUnixNano)) < target.Interval {
			return
		}
	}
}
