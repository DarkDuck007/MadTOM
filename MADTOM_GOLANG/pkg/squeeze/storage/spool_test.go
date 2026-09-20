package storage

import (
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestSpool(t *testing.T) {
	s, err := New(filepath.Join(t.TempDir(), "scratch"), filepath.Join(t.TempDir(), "output"))
	if err != nil {
		t.Fatal(err)
	}
	id := "01234567890123456789012345678901"
	if err = s.Create(id); err != nil {
		t.Fatal(err)
	}
	if n, err := s.Append(id, 0, 6, strings.NewReader("abc")); n != 3 || err != nil {
		t.Fatalf("%d %v", n, err)
	}
	if _, err = s.Append(id, 0, 3, strings.NewReader("bad")); err == nil {
		t.Fatal("offset accepted")
	}
	if n, err := s.Append(id, 3, 3, strings.NewReader("defEXTRA")); n != 3 || err == nil {
		t.Fatalf("%d %v", n, err)
	}
	hash, err := s.Hash(id)
	if err != nil || hash != "bef57ec7f53a6d40beb640a780a639c83bc29ac8a9816f1fc6c5c6dcd93c4721" {
		t.Fatalf("%s %v", hash, err)
	}
	f, err := os.Open(s.Input(id))
	if err != nil {
		t.Fatal(err)
	}
	data, _ := io.ReadAll(f)
	f.Close()
	if string(data) != "abcdef" {
		t.Fatal(string(data))
	}
}
func TestSweepOnlyOwnedOldFiles(t *testing.T) {
	s, _ := New(filepath.Join(t.TempDir(), "scratch"), filepath.Join(t.TempDir(), "output"))
	id := "01234567890123456789012345678901"
	if err := s.Create(id); err != nil {
		t.Fatal(err)
	}
	foreign := filepath.Join(s.Scratch, "keep.part")
	if err := os.WriteFile(foreign, []byte("keep"), 0600); err != nil {
		t.Fatal(err)
	}
	before := time.Now().Add(time.Hour)
	if err := s.SweepOrphans(before, map[string]bool{id: true}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(s.Input(id)); err != nil {
		t.Fatal(err)
	}
	if err := s.SweepOrphans(before, nil); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(s.Input(id)); !os.IsNotExist(err) {
		t.Fatal(err)
	}
	if _, err := os.Stat(foreign); err != nil {
		t.Fatal(err)
	}
}
