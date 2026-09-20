package storage

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
)

var ownedName = regexp.MustCompile(`^[a-f0-9]{32}\.(part|mp4|mkv|webm|tmp)$`)

type Store struct{ Scratch, Output string }

func New(scratch, output string) (*Store, error) {
	s, err := filepath.Abs(scratch)
	if err != nil {
		return nil, err
	}
	o, err := filepath.Abs(output)
	if err != nil {
		return nil, err
	}
	if s == o {
		return nil, fmt.Errorf("scratch and output directories must differ")
	}
	for _, dir := range []string{s, o} {
		if err := os.MkdirAll(dir, 0700); err != nil {
			return nil, err
		}
	}
	return &Store{s, o}, nil
}
func (s *Store) Input(id string) string       { return filepath.Join(s.Scratch, id+".part") }
func (s *Store) Result(id, ext string) string { return filepath.Join(s.Output, id+"."+ext) }
func (s *Store) Create(id string) error {
	f, e := os.OpenFile(s.Input(id), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	return f.Close()
}

// Append preserves partial data on interrupted requests, and never writes past length.
func (s *Store) Append(id string, offset, remaining int64, r io.Reader) (int64, error) {
	f, err := os.OpenFile(s.Input(id), os.O_WRONLY, 0600)
	if err != nil {
		return 0, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return 0, err
	}
	if info.Size() != offset {
		return 0, fmt.Errorf("spool offset mismatch")
	}
	if _, err = f.Seek(offset, io.SeekStart); err != nil {
		return 0, err
	}
	n, err := io.Copy(f, io.LimitReader(r, remaining))
	if err == nil {
		var extra [1]byte
		var m int
		m, err = r.Read(extra[:])
		if m > 0 {
			err = fmt.Errorf("upload exceeds declared size")
		} else if err == io.EOF {
			err = nil
		}
	}
	if syncErr := f.Sync(); err == nil {
		err = syncErr
	}
	return n, err
}
func (s *Store) Hash(id string) (string, error) {
	f, e := os.Open(s.Input(id))
	if e != nil {
		return "", e
	}
	defer f.Close()
	h := sha256.New()
	if _, e = io.Copy(h, f); e != nil {
		return "", e
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
func (s *Store) RemoveInput(id string) error { return remove(s.Input(id)) }
func remove(path string) error {
	err := os.Remove(path)
	if os.IsNotExist(err) {
		return nil
	}
	return err
}
