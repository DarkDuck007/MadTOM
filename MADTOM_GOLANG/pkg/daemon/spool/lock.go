package spool

import (
	"fmt"
	"os"
	"path/filepath"
	"syscall"
)

// LockDirectory holds exclusive daemon ownership across migration and normal operation.
// Keep the file in place: unlinking a flock file allows competing locks on different inodes.
func LockDirectory(dir string) (*os.File, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, err
	}
	f, err := os.OpenFile(filepath.Join(dir, ".daemon.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err := syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		f.Close()
		return nil, fmt.Errorf("spool directory is already in use: %w", err)
	}
	return f, nil
}
