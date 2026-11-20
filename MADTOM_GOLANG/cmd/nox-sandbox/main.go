package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/api"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/manager"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm/firecracker"
)

func main() {
	hostPath := flag.String("host-config", "/etc/nox-sandbox/host.yaml", "path to root-owned sandbox host configuration")
	flag.Parse()
	if err := run(*hostPath); err != nil {
		log.Fatal(err)
	}
}

func run(hostPath string) error {
	if os.Geteuid() != 0 {
		return fmt.Errorf("nox-sandbox must run as root")
	}
	syscall.Umask(0077)
	cfg, err := config.Load(hostPath)
	if err != nil {
		return err
	}
	listener, err := api.Listen(cfg.Host)
	if err != nil {
		return err
	}
	defer listener.Close()
	mgr, err := manager.New(cfg, manager.Options{Backend: firecracker.New(cfg.Host)})
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()
	reload := make(chan os.Signal, 1)
	signal.Notify(reload, syscall.SIGHUP)
	defer signal.Stop(reload)
	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case <-reload:
				mgr.Reload()
			}
		}
	}()
	if err := mgr.Start(ctx); err != nil {
		return err
	}
	server := &http.Server{Handler: api.NewHandler(mgr, cfg), ConnContext: api.ConnContext, ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout: 45 * time.Second, IdleTimeout: time.Minute, MaxHeaderBytes: 16 << 10}
	served := make(chan error, 1)
	go func() { served <- server.Serve(listener) }()
	log.Printf("nox-sandbox listening at %s", cfg.Host.SocketPath)
	select {
	case <-ctx.Done():
	case err = <-served:
		if errors.Is(err, http.ErrServerClosed) {
			err = nil
		}
		stop()
	}
	_ = listener.Close()
	shutdown, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()
	closeErr := mgr.Close(shutdown)
	return errors.Join(err, closeErr, server.Shutdown(shutdown))
}
