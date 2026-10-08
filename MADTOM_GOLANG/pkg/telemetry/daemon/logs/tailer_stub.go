//go:build !linux

package logs

import (
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// JournalTailer stub for non-Linux OS.
type JournalTailer struct{}

func NewJournalTailer(recordCb func(*madtomv1.LogRecord, string)) *JournalTailer {
	return &JournalTailer{}
}

func (t *JournalTailer) Start(cfg *madtomv1.NodeConfig, cursor string) {}
func (t *JournalTailer) NeedsRestart(cfg *madtomv1.NodeConfig) bool     { return false }
func (t *JournalTailer) Stop()                                          {}
