package manager

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/agent"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/internal/fakevm"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

// fakeClock is the manager's Now(); the background sweep runs on real time but reads this clock.
type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *fakeClock) Now() time.Time { c.mu.Lock(); defer c.mu.Unlock(); return c.now }
func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	c.now = c.now.Add(d)
	c.mu.Unlock()
}

type harness struct {
	m       *Manager
	fake    *fakevm.Backend
	clock   *fakeClock
	cfg     *config.Effective
	images  string
	started bool
}

func newHarness(t *testing.T, tweak func(*config.Sandbox)) *harness {
	t.Helper()
	dir := t.TempDir()
	images := filepath.Join(dir, "images")
	for _, version := range []string{"2026.11.20-1", "2026.11.20-2"} {
		if err := os.MkdirAll(filepath.Join(images, version), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(images, version, "rootfs.ext4"), []byte("rootfs"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.Symlink("2026.11.20-1", filepath.Join(images, "current")); err != nil {
		t.Fatal(err)
	}
	sb := config.Defaults()
	sb.Enabled, sb.AllowAllChats = true, true
	sb.DiskWatermarkPercent = 100
	sb.TempVM.PoolSize, sb.PreVM.PoolSize = 0, 0
	if tweak != nil {
		tweak(&sb)
	}
	cfg := &config.Effective{Host: config.Host{StateDir: filepath.Join(dir, "state"), ImagesDir: images}, Sandbox: sb}
	clock := &fakeClock{now: time.Date(2026, 11, 20, 10, 0, 0, 0, time.UTC)}
	fake := fakevm.New()
	m, err := New(cfg, Options{Backend: fake, Mkfs: func(p string, size int64) error { // sparse, full-size like a real mkfs'd volume
		f, err := os.OpenFile(p, os.O_CREATE|os.O_WRONLY, 0o600)
		if err != nil {
			return err
		}
		defer f.Close()
		return f.Truncate(size)
	}, Now: clock.Now})
	if err != nil {
		t.Fatal(err)
	}
	h := &harness{m: m, fake: fake, clock: clock, cfg: cfg, images: images}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := m.Close(ctx); err != nil {
			t.Errorf("close: %v", err)
		}
	})
	return h
}

func (h *harness) start(t *testing.T) {
	t.Helper()
	if err := h.m.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	h.started = true
}

func (h *harness) exec(t *testing.T, chat int64, code string) ExecResponse {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	r, err := h.m.Exec(ctx, ExecRequest{ChatID: chat, Code: code, Session: "main", Kind: "python", VM: vmm.RoleChat})
	if err != nil {
		t.Fatalf("exec %q: %v", code, err)
	}
	return r
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func repr(r ExecResponse) string {
	if r.ResultRepr == nil {
		return "<nil>"
	}
	return *r.ResultRepr
}

func errCode(err error) string {
	var e *Error
	if errors.As(err, &e) {
		return e.Code
	}
	return ""
}

func TestSessionSurvivesHibernateAndRestoreGetsFreshEntropy(t *testing.T) {
	h := newHarness(t, nil)
	first := h.exec(t, -100, "x = 41")
	if !first.OK || !first.VM.ColdStarted || first.SessionReset {
		t.Fatalf("first exec: %+v", first)
	}
	if err := h.m.Action(context.Background(), -100, "hibernate"); err != nil {
		t.Fatal(err)
	}
	if st := h.m.ChatInfo(-100).State; st != "hibernated" {
		t.Fatalf("state after hibernate = %q", st)
	}
	if len(h.fake.Running()) != 0 {
		t.Fatalf("hibernated VM still running")
	}
	second := h.exec(t, -100, "x")
	if repr(second) != "41" || !second.VM.Restored || second.SessionReset {
		t.Fatalf("session not restored: %+v (repr %s)", second, repr(second))
	}
	ms := h.fake.Machines
	if len(ms) != 2 || len(ms[0].Entropy) == 0 || len(ms[1].Entropy) == 0 {
		t.Fatalf("expected init with entropy on boot and restore, machines=%d", len(ms))
	}
	if bytes.Equal(ms[0].Entropy[0], ms[1].Entropy[len(ms[1].Entropy)-1]) || len(ms[1].Entropy[0]) < 32 {
		t.Fatal("restored VM was not reseeded with fresh entropy")
	}
}

func TestIdleHibernationThenTTLDropsSessionKeepsDisk(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.WarmSeconds = 60; s.HibernateTTLHours = 24 })
	h.start(t)
	h.exec(t, -200, "x = 7; write:/data/notes/a.md:hello")
	h.clock.Advance(30 * time.Second)
	time.Sleep(400 * time.Millisecond) // a few sweeps: still inside warm_seconds
	if st := h.m.ChatInfo(-200).State; st != "warm" {
		t.Fatalf("hibernated too early: %q", st)
	}
	h.clock.Advance(31 * time.Second)
	waitFor(t, "idle hibernation", func() bool { return h.m.ChatInfo(-200).State == "hibernated" })

	h.clock.Advance(25 * time.Hour)
	waitFor(t, "snapshot expiry", func() bool { return h.m.ChatInfo(-200).State == "cold" })
	r := h.exec(t, -200, "x")
	if r.Error == nil || !strings.Contains(*r.Error, "NameError") || !r.SessionReset {
		t.Fatalf("expected a lost session reported as reset: %+v", r)
	}
	ctx := context.Background()
	text, err := h.m.ReadText(ctx, -200, "/data/notes/a.md", 100)
	if err != nil || text.Text != "hello" {
		t.Fatalf("disk content lost: %+v %v", text, err)
	}
}

func TestBusyChatIsNeverIdledOrHibernated(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.WarmSeconds = 1 })
	h.start(t)
	done := make(chan ExecResponse, 1)
	go func() { done <- h.exec(t, -300, "sleep:800; y = 1") }()
	waitFor(t, "busy", func() bool { return h.m.ChatInfo(-300).State == "busy" })
	h.clock.Advance(10 * time.Minute)
	time.Sleep(400 * time.Millisecond)
	if st := h.m.ChatInfo(-300).State; st != "busy" {
		t.Fatalf("a running exec was interrupted by idle handling: %q", st)
	}
	if r := <-done; !r.OK || r.Error != nil {
		t.Fatalf("exec failed: %+v", r)
	}
}

func TestChatsAreIsolated(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 1, "write:/data/secret.txt:A-only")
	h.exec(t, 2, "print:hi")
	ctx := context.Background()
	list, err := h.m.ListFiles(ctx, 2, "/data", 2, true)
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range list.Entries {
		if strings.Contains(e.Path, "secret") {
			t.Fatalf("chat 2 sees chat 1's file: %+v", list.Entries)
		}
	}
	if _, err := h.m.ReadText(ctx, 2, "/data/secret.txt", 100); err == nil {
		t.Fatal("chat 2 read chat 1's file")
	}
	disks := map[string]bool{}
	for _, m := range h.fake.Machines {
		disks[m.Spec().DataDisk] = true
	}
	if len(disks) != 2 {
		t.Fatalf("expected one distinct volume per chat, got %v", disks)
	}
}

func TestDisabledAndUnlistedChatsAreRefused(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.AllowAllChats = false; s.EnabledChats = []int64{5} })
	_, err := h.m.Exec(context.Background(), ExecRequest{ChatID: 6, Code: "print:x", Session: "main", VM: vmm.RoleChat})
	if errCode(err) != "forbidden" {
		t.Fatalf("unlisted chat: %v", err)
	}
	h.exec(t, 5, "print:x")
	h.cfg.Sandbox.Enabled = false
	h2 := newHarness(t, func(s *config.Sandbox) { s.Enabled = false })
	_, err = h2.m.Exec(context.Background(), ExecRequest{ChatID: 5, Code: "print:x", Session: "main", VM: vmm.RoleChat})
	if errCode(err) != "disabled" {
		t.Fatalf("disabled sandbox: %v", err)
	}
	if h2.fake.Boots.Load() != 0 {
		t.Fatal("a refused request booted a VM")
	}
}

func TestBusySessionAndNewSession(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 9, "print:warm")
	go h.m.Exec(context.Background(), ExecRequest{ChatID: 9, Code: "sleep:700", Session: "main", VM: vmm.RoleChat})
	waitFor(t, "busy", func() bool { return h.m.ChatInfo(9).State == "busy" })
	_, err := h.m.Exec(context.Background(), ExecRequest{ChatID: 9, Code: "print:x", Session: "main", VM: vmm.RoleChat})
	if errCode(err) != "busy" {
		t.Fatalf("expected busy, got %v", err)
	}
	r, err := h.m.Exec(context.Background(), ExecRequest{ChatID: 9, Code: "print:x", Session: "new", VM: vmm.RoleChat})
	if err != nil || !r.OK || r.Session == "" || r.Session == "main" {
		t.Fatalf("session=new should run in a fresh session: %+v %v", r, err)
	}
}

func TestTempVMIsSingleUseAndHasNoChatDisk(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 11, "write:/data/keep.txt:chat")
	r, err := h.m.Exec(context.Background(), ExecRequest{ChatID: 11, Code: "write:/data/tmp.txt:t; print:ok", VM: vmm.RoleTemp})
	if err != nil || !r.OK {
		t.Fatalf("temp exec: %+v %v", r, err)
	}
	var temp *fakevm.Machine
	for _, m := range h.fake.Machines {
		if m.Spec().Role == vmm.RoleTemp {
			temp = m
		}
	}
	if temp == nil || temp.Spec().DataDisk != "" {
		t.Fatalf("temp VM must not get a persistent disk: %+v", temp)
	}
	waitFor(t, "temp VM destroyed", func() bool { return temp.Stopped() })
	if _, err := h.m.ReadText(context.Background(), 11, "/data/tmp.txt", 100); err == nil {
		t.Fatal("a temp VM file leaked into the chat volume")
	}
}

func TestGlobalVMCapEvictsLeastRecentlyUsed(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.Global.MaxVMs = 2 })
	h.exec(t, 21, "a = 1")
	h.clock.Advance(time.Second)
	h.exec(t, 22, "b = 2")
	h.clock.Advance(time.Second)
	h.exec(t, 23, "c = 3")
	if n := len(h.fake.Running()); n > 2 {
		t.Fatalf("%d VMs running with max_vms=2", n)
	}
	if st := h.m.ChatInfo(21).State; st != "hibernated" {
		t.Fatalf("LRU chat should be hibernated, got %q", st)
	}
	if st := h.m.ChatInfo(23).State; st != "warm" {
		t.Fatalf("new chat should be warm, got %q", st)
	}
	if r := h.exec(t, 21, "a"); repr(r) != "1" {
		t.Fatalf("evicted chat lost its session: %+v", r)
	}
}

func TestOutputIsCapped(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.MaxOutputBytes = 1000 })
	r := h.exec(t, 31, "big:50000")
	if len(r.Stdout) > 1000 || !r.Truncated {
		t.Fatalf("stdout not capped: len=%d truncated=%v", len(r.Stdout), r.Truncated)
	}
}

func pngBytes(t *testing.T, w, h int) []byte {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	img.Set(0, 0, color.NRGBA{255, 0, 0, 255})
	var b bytes.Buffer
	if err := png.Encode(&b, img); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}

func TestExportSanitizesImagesAndRejectsFakes(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) {
		s.ExportAllowedExts = []string{"png", "jpg", "jpeg", "xlsx", "gif", "md"}
	})
	h.exec(t, 41, "print:x")
	vm := h.fake.Running()[0]
	good := pngBytes(t, 3, 2)
	vm.SetFile("/data/outbox/chart.png", good)
	vm.SetFile("/data/outbox/fake.png", []byte("<script>not an image</script>"))
	vm.SetFile("/data/outbox/mislabelled.jpg", good) // PNG content behind a .jpg name
	vm.SetFile("/data/outbox/huge.png", pngBytes(t, 9000, 1))
	vm.SetFile("/data/outbox/sheet.xlsx", []byte("PK\x03\x04zip"))
	vm.SetFile("/data/outbox/link.png.symlink", good)

	files, err := h.m.Export(context.Background(), 41, []string{"/data/outbox/*"})
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]agent.File{}
	for _, f := range files.Files {
		got[f.Name] = f
	}
	chart, ok := got["chart.png"]
	if !ok || !chart.Sanitized {
		t.Fatalf("valid png not exported as sanitized: %+v", files)
	}
	if img, err := png.Decode(bytes.NewReader(chart.Data)); err != nil || img.Bounds().Dx() != 3 {
		t.Fatalf("re-encoded png invalid: %v", err)
	}
	if sheet, ok := got["sheet.xlsx"]; !ok || sheet.Sanitized || string(sheet.Data) != "PK\x03\x04zip" {
		t.Fatalf("non-image must pass through unsanitized: %+v", sheet)
	}
	reasons := map[string]string{}
	for _, s := range files.Skipped {
		reasons[filepath.Base(s.Path)] = s.Reason
	}
	for _, name := range []string{"fake.png", "mislabelled.jpg", "huge.png"} {
		if _, exported := got[name]; exported || reasons[name] != "bad_image" {
			t.Fatalf("%s should be skipped as bad_image, reasons=%v", name, reasons)
		}
	}
	if _, exported := got["link.png.symlink"]; exported || reasons["link.png.symlink"] != "symlink" {
		t.Fatalf("symlink should be skipped: %v", reasons)
	}
}

func TestCleanFilesIgnoresGuestSanitizedClaimAndBadNames(t *testing.T) {
	cfg := config.Defaults()
	cfg.ExportAllowedExts = []string{"gif", "png", "txt"}
	in := agent.Files{Files: []agent.File{
		{Name: "anim.gif", Path: "/data/outbox/anim.gif", Data: []byte("GIF89a..."), Sanitized: true},
		{Name: "../../etc/passwd.txt", Path: "/data/outbox/x.txt", Data: []byte("x")},
		{Name: "evil.exe", Path: "/data/outbox/evil.exe", Data: []byte("MZ")},
	}}
	out := cleanFiles(in, cfg)
	for _, f := range out.Files {
		if f.Name == "anim.gif" && f.Sanitized {
			t.Fatal("guest-supplied sanitized=true was trusted")
		}
		if strings.Contains(f.Name, "/") || strings.Contains(f.Name, "..") || f.Name == "evil.exe" {
			t.Fatalf("unsafe export accepted: %q", f.Name)
		}
	}
}

func TestPreviewRejectsLyingDimensionsAndReturnsPNG(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 51, "print:x")
	ctx := context.Background()
	if _, err := h.m.Preview(ctx, 51, "/data/evil.png", 1, 1600); err == nil {
		t.Fatal("render with mismatched dimensions was accepted")
	}
	p, err := h.m.Preview(ctx, 51, "/data/ok.png", 1, 1600)
	if err != nil {
		t.Fatal(err)
	}
	img, err := png.Decode(bytes.NewReader(p.PNG))
	if err != nil || img.Bounds().Dx() != 4 || img.Bounds().Dy() != 2 {
		t.Fatalf("preview is not a valid host-encoded png: %v", err)
	}
}

func TestInspectorIsReadOnlyForHibernatedChat(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 61, "write:/data/a.txt:abc")
	if err := h.m.Action(context.Background(), 61, "hibernate"); err != nil {
		t.Fatal(err)
	}
	text, err := h.m.ReadText(context.Background(), 61, "/data/a.txt", 100)
	if err != nil || text.Text != "abc" {
		t.Fatalf("read via inspector: %+v %v", text, err)
	}
	var inspector *fakevm.Machine
	for _, m := range h.fake.Machines {
		if m.Spec().Role == vmm.RoleInspector {
			inspector = m
		}
	}
	if inspector == nil || !inspector.Spec().DataReadOnly {
		t.Fatalf("hibernated chat files must be read through a read-only inspector VM")
	}
	if st := h.m.ChatInfo(61).State; st != "hibernated" {
		t.Fatalf("inspection changed the chat state to %q", st)
	}
}

func TestPinnedImageOnRestoreAndNewImageAfterReset(t *testing.T) {
	h := newHarness(t, nil)
	h.exec(t, 71, "v = 1")
	if err := h.m.Action(context.Background(), 71, "hibernate"); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(h.images, "current")); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("2026.11.20-2", filepath.Join(h.images, "current")); err != nil {
		t.Fatal(err)
	}
	r := h.exec(t, 71, "v")
	if r.VM.ImageVersion != "2026.11.20-1" || repr(r) != "1" {
		t.Fatalf("restore must stay on the snapshot's image: %+v", r.VM)
	}
	if err := h.m.Action(context.Background(), 71, "restart"); err != nil {
		t.Fatal(err)
	}
	r = h.exec(t, 71, "print:x")
	if r.VM.ImageVersion != "2026.11.20-2" {
		t.Fatalf("restart should pick up the new image, got %s", r.VM.ImageVersion)
	}
}

func TestRuntimeGrantsEnableChatsAndPersist(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.AllowAllChats = false; s.EnabledChats = []int64{5} })
	ctx := context.Background()
	run := func(m *Manager, chat int64) error {
		_, err := m.Exec(ctx, ExecRequest{ChatID: chat, Code: "print:x", Session: "main", VM: vmm.RoleChat})
		return err
	}
	if errCode(run(h.m, 77)) != "forbidden" {
		t.Fatal("ungranted chat allowed")
	}
	if err := h.m.SetGrant(77, true, "user:42"); err != nil {
		t.Fatal(err)
	}
	if err := h.m.SetGrant(77, true, "user:43"); err != nil { // idempotent, keeps the first grant
		t.Fatal(err)
	}
	if err := run(h.m, 77); err != nil {
		t.Fatalf("granted chat refused: %v", err)
	}
	if g := h.m.Grants(); len(g) != 1 || g[0].ChatID != 77 || g[0].GrantedBy != "user:42" {
		t.Fatalf("grants: %+v", g)
	}
	if h.m.SetGrant(0, true, "x") == nil {
		t.Fatal("chat 0 accepted")
	}

	// A second manager over the same state dir sees the grant (survives a service restart).
	m2, err := New(h.cfg, Options{Backend: fakevm.New(), Mkfs: func(string, int64) error { return nil }, Now: h.clock.Now})
	if err != nil {
		t.Fatal(err)
	}
	if !m2.Granted(77) || m2.Granted(78) {
		t.Fatal("grant not persisted")
	}
	_ = m2.Close(ctx)

	if err := h.m.SetGrant(77, false, "user:42"); err != nil {
		t.Fatal(err)
	}
	if errCode(run(h.m, 77)) != "forbidden" {
		t.Fatal("revoked chat still allowed")
	}
	if err := run(h.m, 5); err != nil { // the static config list still works
		t.Fatalf("config-listed chat refused: %v", err)
	}
	if _, err := os.Stat(filepath.Join(h.cfg.Host.StateDir, "grants.json")); err != nil {
		t.Fatal(err)
	}
}

func TestPreRunStateRoundTrip(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.AllowAllChats = false; s.EnabledChats = []int64{9} })
	ctx := context.Background()
	run := func(code, state string) PreResponse {
		t.Helper()
		res, err := h.m.PreRun(ctx, PreRequest{ChatID: 9, Code: code, Event: json.RawMessage(`{"kind":"message"}`), State: json.RawMessage(state)})
		if err != nil || !res.OK {
			t.Fatalf("pre run: %v %+v", err, res)
		}
		return res
	}
	// State returned inside the result object must win over the echoed input state.
	res := run("COUNT", `{"n": 1}`)
	if string(res.State) != `{"n":2}` || len(res.Actions) != 1 {
		t.Fatalf("result-object state lost: state=%s actions=%d", res.State, len(res.Actions))
	}
	// `return result, state` form.
	if res := run("TUPLE", `{"n": 5}`); string(res.State) != `{"n":6}` {
		t.Fatalf("tuple state lost: %s", res.State)
	}
	// No new state: the input is kept.
	if res := run("PASS", `{"keep": true}`); !strings.Contains(string(res.State), `"keep"`) {
		t.Fatalf("input state not kept: %s", res.State)
	}
	if _, err := h.m.PreRun(ctx, PreRequest{ChatID: 10, Code: "PASS", Event: json.RawMessage(`{}`)}); errCode(err) != "forbidden" {
		t.Fatalf("pre run in a disabled chat: %v", err)
	}
}

func TestAllowedHonoursChatsMode(t *testing.T) {
	base := config.Defaults()
	base.Enabled, base.EnabledChats = true, []int64{5}
	cases := []struct {
		chats   string
		allAll  bool
		id      int64
		granted bool
		ok      bool
	}{
		{"all", false, 9, false, true},
		{"whitelist", false, 5, false, true},
		{"whitelist", false, 9, true, true},
		{"whitelist", false, 9, false, false},
		{"whitelist", true, 9, false, false}, // explicit chats wins over legacy allow_all_chats
		{"", true, 9, false, true},           // legacy
		{"none", false, 5, true, false},
	}
	for _, c := range cases {
		cfg := base
		cfg.Chats, cfg.AllowAllChats = c.chats, c.allAll
		if err := allowed(cfg, c.id, c.granted); (err == nil) != c.ok {
			t.Fatalf("%+v: err=%v", c, err)
		}
	}
	off := base
	off.Enabled, off.Chats = false, "all"
	if allowed(off, 5, true) == nil {
		t.Fatal("disabled sandbox must refuse")
	}
}

func TestChatInfoReportsBudgetResetAndSnapshotQuota(t *testing.T) {
	h := newHarness(t, func(s *config.Sandbox) { s.Hibernate = true; s.SnapshotQuotaMBPerChat = 3 })
	h.start(t)
	now := h.clock.Now()
	want := time.Date(now.Year(), now.Month(), now.Day()+1, 0, 0, 0, 0, now.Location())
	cold := h.m.ChatInfo(-700)
	if !cold.CPUResetsAt.Equal(want) || cold.SnapshotQuota != 3<<20 {
		t.Fatalf("cold chat: resets %v (want %v), quota %d", cold.CPUResetsAt, want, cold.SnapshotQuota)
	}
	h.exec(t, -700, "x = 1")
	warm := h.m.ChatInfo(-700)
	if !warm.CPUResetsAt.Equal(want) || warm.SnapshotQuota != 3<<20 || warm.CPUBudgetSeconds != h.cfg.Sandbox.DailyCPUSecondsPerChat {
		t.Fatalf("warm chat: %+v", warm)
	}
	h.clock.Advance(26 * time.Hour)
	if got := h.m.ChatInfo(-700).CPUResetsAt; !got.After(want) {
		t.Fatalf("reset time did not move to the next day: %v", got)
	}
	h.m.cfg.Sandbox.Hibernate = false
	if q := h.m.ChatInfo(-700).SnapshotQuota; q != 0 {
		t.Fatalf("hibernation off should report no snapshot quota, got %d", q)
	}
}
