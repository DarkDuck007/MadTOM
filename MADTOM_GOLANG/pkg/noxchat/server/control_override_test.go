package server

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestStudioStoreModeSurvivesProbeRestart(t *testing.T) {
	dataDir := t.TempDir()
	newCfg := func() *config.ProbeConfig {
		cfg := config.DefaultConfig()
		cfg.SpoolDir = filepath.Join(dataDir, "noxchat_spool")
		cfg.StoreRawLogs = false // config.yaml default
		return cfg
	}

	s, err := NewServer(newCfg(), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.UpdateControlConfig(context.Background(), &pb.NoxUpdateControlConfigRequest{NodeId: s.cfg.NodeID, StoreRawLogs: true}); err != nil {
		t.Fatal(err)
	}
	s.Stop()

	s2, err := NewServer(newCfg(), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if !s2.cfg.StoreRawLogs {
		t.Fatal("restart reverted Studio's Stream & Store mode to the config.yaml default")
	}
	if _, err := s2.UpdateControlConfig(context.Background(), &pb.NoxUpdateControlConfigRequest{NodeId: s2.cfg.NodeID, StoreRawLogs: false}); err != nil {
		t.Fatal(err)
	}
	s2.Stop()

	s3, err := NewServer(newCfg(), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer s3.Stop()
	if s3.cfg.StoreRawLogs {
		t.Fatal("Studio turning storage off was not persisted")
	}
}
