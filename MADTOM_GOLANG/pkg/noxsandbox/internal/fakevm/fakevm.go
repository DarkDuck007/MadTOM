// Package fakevm is a test double for vmm.Backend: machines are in-process and their guest agent speaks the
// real framed-JSON protocol (contracts §3) over net.Pipe, backed by an in-memory filesystem per machine.
// Volumes are keyed by Spec.DataDisk so a chat's /data survives Stop/Boot like a real disk image does.
package fakevm

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

// Volume is the fake content of a chat disk (guest path -> bytes).
type Volume struct {
	mu    sync.Mutex
	Files map[string][]byte
}

func (v *Volume) snapshot() map[string][]byte {
	v.mu.Lock()
	defer v.mu.Unlock()
	out := make(map[string][]byte, len(v.Files))
	for k, b := range v.Files {
		out[k] = append([]byte(nil), b...)
	}
	return out
}

// Backend records every lifecycle call so tests can assert on them.
type Backend struct {
	mu        sync.Mutex
	Volumes   map[string]*Volume // by DataDisk host path
	Machines  []*Machine
	Boots     atomic.Int64
	Restores  atomic.Int64
	BootDelay time.Duration
	FailBoot  error
	// ExecHook lets a test control exec results: return handled=true to use resp.
	ExecHook func(m *Machine, req map[string]any) (resp map[string]any, handled bool)
	// CPU seconds reported per machine per exec call.
	CPUPerExec float64
	seq        atomic.Int64
}

func New() *Backend { return &Backend{Volumes: map[string]*Volume{}, CPUPerExec: 0.01} }

func (b *Backend) volume(disk string) *Volume {
	b.mu.Lock()
	defer b.mu.Unlock()
	if disk == "" {
		return &Volume{Files: map[string][]byte{}} // tmpfs
	}
	v, ok := b.Volumes[disk]
	if !ok {
		v = &Volume{Files: map[string][]byte{}}
		b.Volumes[disk] = v
	}
	return v
}

func (b *Backend) Boot(ctx context.Context, s vmm.Spec) (vmm.Machine, error) {
	if b.FailBoot != nil {
		return nil, b.FailBoot
	}
	if b.BootDelay > 0 {
		select {
		case <-time.After(b.BootDelay):
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	b.Boots.Add(1)
	return b.newMachine(s, nil), nil
}

func (b *Backend) Restore(ctx context.Context, s vmm.Spec, snap vmm.Snapshot) (vmm.Machine, error) {
	if b.FailBoot != nil {
		return nil, b.FailBoot
	}
	b.Restores.Add(1)
	var sessions map[string]map[string]string
	if raw, err := os.ReadFile(path.Join(snap.Dir, "fake-sessions.json")); err == nil {
		_ = json.Unmarshal(raw, &sessions)
	}
	return b.newMachine(s, sessions), nil
}

func (b *Backend) newMachine(s vmm.Spec, sessions map[string]map[string]string) *Machine {
	if s.ID == "" {
		s.ID = fmt.Sprintf("fake-%d", b.seq.Add(1))
	}
	if sessions == nil {
		sessions = map[string]map[string]string{}
	}
	m := &Machine{b: b, spec: s, vol: b.volume(s.DataDisk), sessions: sessions, done: make(chan struct{}),
		busy: map[string]bool{}}
	b.mu.Lock()
	b.Machines = append(b.Machines, m)
	b.mu.Unlock()
	return m
}

// Running returns machines that have not been stopped.
func (b *Backend) Running() []*Machine {
	b.mu.Lock()
	defer b.mu.Unlock()
	var out []*Machine
	for _, m := range b.Machines {
		if !m.Stopped() {
			out = append(out, m)
		}
	}
	return out
}

// Machine is a fake VM. Python "sessions" are maps of variable name -> value set with `name = value` code.
type Machine struct {
	b        *Backend
	spec     vmm.Spec
	vol      *Volume
	mu       sync.Mutex
	sessions map[string]map[string]string
	busy     map[string]bool
	paused   bool
	stopped  bool
	cpu      float64
	balloon  int
	done     chan struct{}
	Inited   atomic.Int64
	Entropy  [][]byte
	Calls    []string
}

func (m *Machine) ID() string            { return m.spec.ID }
func (m *Machine) Spec() vmm.Spec        { return m.spec }
func (m *Machine) Stopped() bool         { m.mu.Lock(); defer m.mu.Unlock(); return m.stopped }
func (m *Machine) Paused() bool          { m.mu.Lock(); defer m.mu.Unlock(); return m.paused }
func (m *Machine) Done() <-chan struct{} { return m.done }

func (m *Machine) Pause(ctx context.Context) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.paused = true
	return nil
}
func (m *Machine) Resume(ctx context.Context) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.paused = false
	return nil
}
func (m *Machine) SetBalloon(ctx context.Context, mib int) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.balloon = mib
	return nil
}
func (m *Machine) Snapshot(ctx context.Context, dir string) (vmm.Snapshot, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.paused {
		return vmm.Snapshot{}, errors.New("snapshot of a running VM")
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return vmm.Snapshot{}, err
	}
	raw, _ := json.Marshal(m.sessions)
	if err := os.WriteFile(path.Join(dir, "fake-sessions.json"), raw, 0o600); err != nil {
		return vmm.Snapshot{}, err
	}
	_ = os.WriteFile(path.Join(dir, "mem"), make([]byte, 4096), 0o600)
	return vmm.Snapshot{Dir: dir, ImageVersion: m.spec.ImageVersion, MemBytes: 4096}, nil
}
func (m *Machine) CPUSeconds() (float64, error) { m.mu.Lock(); defer m.mu.Unlock(); return m.cpu, nil }
func (m *Machine) Stop(ctx context.Context) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.stopped {
		m.stopped = true
		close(m.done)
	}
	return nil
}

func (m *Machine) Dial(ctx context.Context) (net.Conn, error) {
	m.mu.Lock()
	stopped, paused := m.stopped, m.paused
	m.mu.Unlock()
	if stopped {
		return nil, errors.New("machine stopped")
	}
	if paused {
		return nil, errors.New("machine paused")
	}
	host, guest := net.Pipe()
	go m.serve(guest)
	return host, nil
}

func (m *Machine) serve(c net.Conn) {
	defer c.Close()
	for {
		var hdr [4]byte
		if _, err := io.ReadFull(c, hdr[:]); err != nil {
			return
		}
		n := binary.BigEndian.Uint32(hdr[:])
		if n > 64<<20 {
			return
		}
		buf := make([]byte, n)
		if _, err := io.ReadFull(c, buf); err != nil {
			return
		}
		var req map[string]any
		resp := map[string]any{}
		if err := json.Unmarshal(buf, &req); err != nil {
			resp = map[string]any{"ok": false, "error": "bad json"}
		} else {
			resp = m.handle(req)
			resp["id"] = req["id"]
		}
		out, _ := json.Marshal(resp)
		binary.BigEndian.PutUint32(hdr[:], uint32(len(out)))
		if _, err := c.Write(append(hdr[:], out...)); err != nil {
			return
		}
	}
}

func str(v any) string {
	s, _ := v.(string)
	return s
}

func (m *Machine) readOnly() bool { return m.spec.Role == vmm.RoleInspector || m.spec.DataReadOnly }

func (m *Machine) handle(req map[string]any) map[string]any {
	op := str(req["op"])
	m.mu.Lock()
	m.Calls = append(m.Calls, op)
	m.mu.Unlock()
	switch op {
	case "hello":
		return map[string]any{"ok": true, "agent_version": "1", "image_version": m.spec.ImageVersion, "boot_id": m.spec.ID}
	case "init":
		m.Inited.Add(1)
		e, _ := base64.StdEncoding.DecodeString(str(req["entropy_b64"]))
		m.mu.Lock()
		m.Entropy = append(m.Entropy, e)
		m.mu.Unlock()
		return map[string]any{"ok": true}
	case "ping":
		return map[string]any{"ok": true, "load": 0.0, "mem_free_kb": 1000}
	case "exec":
		if m.readOnly() {
			return map[string]any{"ok": false, "error": "read_only"}
		}
		if m.b.ExecHook != nil {
			if r, ok := m.b.ExecHook(m, req); ok {
				return r
			}
		}
		return m.exec(req)
	case "put_files":
		if m.readOnly() {
			return map[string]any{"ok": false, "error": "read_only"}
		}
		dir := str(req["dir"])
		var written []string
		files, _ := req["files"].([]any)
		for _, f := range files {
			fm, _ := f.(map[string]any)
			name := str(fm["name"])
			if name == "" || strings.ContainsAny(name, "/\x00") || name == "." || name == ".." {
				return map[string]any{"ok": false, "error": "bad name"}
			}
			data, _ := base64.StdEncoding.DecodeString(str(fm["data_b64"]))
			p := path.Join(dir, name)
			m.vol.mu.Lock()
			m.vol.Files[p] = data
			m.vol.mu.Unlock()
			written = append(written, p)
		}
		return map[string]any{"ok": true, "written": written}
	case "get_files":
		return m.getFiles(req)
	case "list", "manifest":
		files := m.vol.snapshot()
		var entries []map[string]any
		var used int
		keys := make([]string, 0, len(files))
		for k := range files {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		for _, k := range keys {
			used += len(files[k])
			entries = append(entries, map[string]any{"path": k, "type": "file", "size": len(files[k]), "mtime": 1795100000})
		}
		if op == "list" { // contracts §3: list → entries, truncated; manifest → entries + disk numbers
			return map[string]any{"ok": true, "entries": entries, "truncated": false}
		}
		return map[string]any{"ok": true, "entries": entries, "used_bytes": used, "free_bytes": 1 << 30, "total_bytes": 1 << 30}
	case "read_text":
		b, ok := m.vol.snapshot()[str(req["path"])]
		if !ok {
			return map[string]any{"ok": false, "error": "not found"}
		}
		return map[string]any{"ok": true, "text": string(b), "truncated": false, "binary": false, "size": len(b)}
	case "render":
		w, h := 4, 2
		px := make([]byte, w*h*4)
		for i := range px {
			px[i] = 0xff
		}
		if str(req["path"]) == "/data/evil.png" { // lies about its size
			return map[string]any{"ok": true, "width": 10000, "height": 10000, "rgba_b64": base64.StdEncoding.EncodeToString(px), "pages": 1, "kind": "image"}
		}
		return map[string]any{"ok": true, "width": w, "height": h, "rgba_b64": base64.StdEncoding.EncodeToString(px), "pages": 1, "kind": "image"}
	case "sessions":
		m.mu.Lock()
		defer m.mu.Unlock()
		var out []map[string]any
		for name := range m.sessions {
			out = append(out, map[string]any{"name": name, "busy": m.busy[name], "started": "2026-11-20T00:00:00Z", "pid": 1})
		}
		return map[string]any{"ok": true, "sessions": out}
	case "reset_session":
		m.mu.Lock()
		delete(m.sessions, str(req["session"]))
		m.mu.Unlock()
		return map[string]any{"ok": true}
	case "pre_run":
		code := str(req["code"])
		state, _ := req["state"].(map[string]any)
		if strings.Contains(code, "LOOP") {
			time.Sleep(5 * time.Second)
		}
		if strings.Contains(code, "RAISE") {
			return map[string]any{"ok": false, "error": "Traceback: boom"}
		}
		return map[string]any{"ok": true, "result": map[string]any{"action": "pass"}, "state": state}
	case "prepare_hibernate", "shutdown":
		return map[string]any{"ok": true}
	}
	return map[string]any{"ok": false, "error": "unknown op " + op}
}

// exec understands a tiny language: "sleep:<ms>", "write:<path>:<content>", "<name> = <value>", "<name>"
// (returns the stored value as result_repr; NameError if missing), "print:<text>", "big:<n>" (n bytes stdout),
// "crash" (session_reset). Statements are separated by ';'.
func (m *Machine) exec(req map[string]any) map[string]any {
	session := str(req["session"])
	if session == "" {
		session = "main"
	}
	m.mu.Lock()
	if m.busy[session] {
		m.mu.Unlock()
		return map[string]any{"ok": false, "error": "busy"}
	}
	m.busy[session] = true
	reset := false
	if _, ok := m.sessions[session]; !ok {
		m.sessions[session] = map[string]string{} // the real agent reports session_reset=false for a fresh session
	}
	m.cpu += m.b.CPUPerExec
	m.mu.Unlock()
	defer func() { m.mu.Lock(); delete(m.busy, session); m.mu.Unlock() }()

	timeout := 0.0
	if t, ok := req["timeout_s"].(float64); ok {
		timeout = t
	}
	var stdout strings.Builder
	var result any
	var errText any
	for _, stmt := range strings.Split(str(req["code"]), ";") {
		stmt = strings.TrimSpace(stmt)
		switch {
		case stmt == "":
		case strings.HasPrefix(stmt, "sleep:"):
			var ms int
			fmt.Sscanf(stmt, "sleep:%d", &ms)
			d := time.Duration(ms) * time.Millisecond
			if timeout > 0 && d > time.Duration(timeout*float64(time.Second)) {
				time.Sleep(time.Duration(timeout * float64(time.Second)))
				m.mu.Lock()
				delete(m.sessions, session)
				m.mu.Unlock()
				return map[string]any{"ok": true, "exit_code": -1, "stdout": stdout.String(), "stderr": "", "truncated": false,
					"result_repr": nil, "error": "timeout", "session_reset": true, "duration_ms": int(timeout * 1000), "cpu_ms": 0}
			}
			time.Sleep(d)
		case strings.HasPrefix(stmt, "write:"):
			parts := strings.SplitN(stmt, ":", 3)
			m.vol.mu.Lock()
			m.vol.Files[parts[1]] = []byte(parts[2])
			m.vol.mu.Unlock()
		case strings.HasPrefix(stmt, "print:"):
			stdout.WriteString(strings.TrimPrefix(stmt, "print:") + "\n")
		case strings.HasPrefix(stmt, "big:"):
			var n int
			fmt.Sscanf(stmt, "big:%d", &n)
			stdout.WriteString(strings.Repeat("x", n))
		case stmt == "crash":
			m.mu.Lock()
			delete(m.sessions, session)
			m.mu.Unlock()
			return map[string]any{"ok": true, "exit_code": -9, "stdout": "", "stderr": "", "truncated": false,
				"result_repr": nil, "error": "session_reset", "session_reset": true, "duration_ms": 1, "cpu_ms": 1}
		case strings.Contains(stmt, "="):
			kv := strings.SplitN(stmt, "=", 2)
			m.mu.Lock()
			m.sessions[session][strings.TrimSpace(kv[0])] = strings.TrimSpace(kv[1])
			m.mu.Unlock()
		default:
			m.mu.Lock()
			v, ok := m.sessions[session][stmt]
			m.mu.Unlock()
			if ok {
				result = v
			} else {
				errText = "NameError: name '" + stmt + "' is not defined"
			}
		}
	}
	out := stdout.String()
	truncated := false
	if max, ok := req["max_output_bytes"].(float64); ok && max > 0 && len(out) > int(max) {
		out, truncated = out[:int(max)], true
	}
	return map[string]any{"ok": true, "exit_code": 0, "stdout": out, "stderr": "", "truncated": truncated,
		"result_repr": result, "error": errText, "session_reset": reset, "duration_ms": 1, "cpu_ms": 1, "session": session}
}

func (m *Machine) getFiles(req map[string]any) map[string]any {
	files := m.vol.snapshot()
	maxTotal, _ := req["max_total_bytes"].(float64)
	maxFiles, _ := req["max_files"].(float64)
	var out, skipped []map[string]any
	total := 0
	pats, _ := req["paths"].([]any)
	keys := make([]string, 0, len(files))
	for k := range files {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, p := range pats {
		pat := str(p)
		for _, k := range keys {
			if ok, _ := path.Match(pat, k); !ok {
				continue
			}
			b := files[k]
			if strings.HasSuffix(k, ".symlink") {
				skipped = append(skipped, map[string]any{"path": k, "reason": "symlink"})
				continue
			}
			if (maxFiles > 0 && len(out) >= int(maxFiles)) || (maxTotal > 0 && total+len(b) > int(maxTotal)) {
				skipped = append(skipped, map[string]any{"path": k, "reason": "limit"})
				continue
			}
			total += len(b)
			out = append(out, map[string]any{"path": k, "name": path.Base(k), "size": len(b),
				"data_b64": base64.StdEncoding.EncodeToString(b)})
		}
	}
	return map[string]any{"ok": true, "files": out, "skipped": skipped}
}

// SetFile writes directly into a machine's volume (test setup).
func (m *Machine) SetFile(p string, b []byte) {
	m.vol.mu.Lock()
	defer m.vol.mu.Unlock()
	m.vol.Files[p] = b
}

// Balloon returns the last balloon target.
func (m *Machine) Balloon() int { m.mu.Lock(); defer m.mu.Unlock(); return m.balloon }

// CallsSnapshot returns ops received so far.
func (m *Machine) CallsSnapshot() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	return append([]string(nil), m.Calls...)
}
