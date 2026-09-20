package storage

import (
	"errors"
	"os"
	"path/filepath"
	"time"
)

func (s *Store) RemoveOutput(id, ext string) error {
	return errors.Join(remove(s.Result(id, ext)), remove(s.Result(id, "tmp")))
}

// SweepOrphans removes only server-named files older than retention. Live IDs are protected.
func (s *Store) SweepOrphans(before time.Time, live map[string]bool) error {
	var errs []error
	for _, dir := range []string{s.Scratch, s.Output} {
		entries, err := os.ReadDir(dir)
		if err != nil {
			errs = append(errs, err)
			continue
		}
		for _, entry := range entries {
			if entry.IsDir() || !ownedName.MatchString(entry.Name()) || live[entry.Name()[:32]] {
				continue
			}
			info, err := entry.Info()
			if err != nil {
				errs = append(errs, err)
				continue
			}
			if info.ModTime().Before(before) {
				errs = append(errs, remove(filepath.Join(dir, entry.Name())))
			}
		}
	}
	return errors.Join(errs...)
}
