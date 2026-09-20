package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"github.com/DarkDuck007/madtom/pkg/squeeze/api"
	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"github.com/DarkDuck007/madtom/pkg/squeeze/discovery"
	"github.com/DarkDuck007/madtom/pkg/squeeze/ffmpeg"
	"github.com/DarkDuck007/madtom/pkg/squeeze/jobs"
	"github.com/DarkDuck007/madtom/pkg/squeeze/storage"
	"syscall"
	"time"
)

func main() {
	if err := run(); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return
		}
		slog.Error("server stopped", "error", err)
		os.Exit(1)
	}
}
func run() error {
	c, err := config.Parse(os.Args[1:])
	if err != nil {
		return err
	}
	for _, binary := range []string{c.FFmpeg, c.FFprobe} {
		if _, err = exec.LookPath(binary); err != nil {
			return fmt.Errorf("required executable %q: %w", binary, err)
		}
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	encoders, hardware, hwStatus, err := ffmpeg.ProbeHardware(ctx, c.FFmpeg, c.Scratch, false)
	if err != nil {
		return fmt.Errorf("encoder probe: %w", err)
	}
	store, err := storage.New(c.Scratch, c.Output)
	if err != nil {
		return err
	}
	broker := api.NewBroker()
	pool := jobs.NewPool(ctx, c.Workers, c.QueueSize, c.MaxUpload, store, ffmpeg.Runner{FFmpeg: c.FFmpeg, FFprobe: c.FFprobe}, broker)
	defer pool.Close()
	service := &api.Server{Config: c, Pool: pool, Broker: broker, Encoders: encoders, Hardware: hardware, HardwareStatus: hwStatus, Started: time.Now()}
	server := &http.Server{Addr: c.Address(), Handler: service.Handler(), ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 60 * time.Second, MaxHeaderBytes: 32 << 10}
	listener, err := net.Listen("tcp", c.Address())
	if err != nil {
		return err
	}
	if c.MDNS {
		ad, e := discovery.Advertise(c, hardware)
		if e != nil {
			slog.Warn("mDNS unavailable; direct HTTP access remains available", "error", e)
		} else {
			defer ad.Shutdown()
		}
	}
	if e := pool.Sweep(time.Now().Add(-c.Retention)); e != nil {
		slog.Warn("retention cleanup", "error", e)
	}
	maintenanceDone := make(chan struct{})
	go func() {
		defer close(maintenanceDone)
		interval := time.Minute
		if c.Retention < interval {
			interval = c.Retention
		}
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case now := <-ticker.C:
				if e := pool.Sweep(now.Add(-c.Retention)); e != nil {
					slog.Warn("retention cleanup", "error", e)
				}
			}
		}
	}()
	errCh := make(chan error, 1)
	go func() { errCh <- server.Serve(listener) }()
	slog.Info("SQUEEZE server listening", "address", listener.Addr(), "workers", c.Workers, "node_id", c.NodeID)
	select {
	case <-ctx.Done():
	case err = <-errCh:
		stop()
	}
	// Closing HTTP connections first releases uploads and SSE streams before workers stop.
	_ = server.Close()
	pool.Close()
	<-maintenanceDone
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}
