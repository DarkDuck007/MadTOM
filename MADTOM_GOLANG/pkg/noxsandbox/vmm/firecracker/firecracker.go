package firecracker

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
	"golang.org/x/sys/unix"
)

type Backend struct {
	host         config.Host
	cgroupParent string
	uidMu        sync.Mutex
	nextUIDSlot  int
	freeUIDSlots []int
}

func New(host config.Host) *Backend {
	if host.JailUIDBase == 0 {
		host.JailUIDBase = max(host.JailUID, host.JailGID)
		if host.JailUIDBase == 0 {
			host.JailUIDBase = config.DefaultJailUIDBase
		}
	}
	if host.JailUIDCount == 0 {
		host.JailUIDCount = config.DefaultJailUIDCount
	}
	if host.VMMemOverheadMB == 0 {
		host.VMMemOverheadMB = config.DefaultVMMemOverheadMB
	}
	parent := filepath.Base(host.FirecrackerBin)
	if data, err := os.ReadFile("/proc/self/cgroup"); err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			group, ok := strings.CutPrefix(line, "0::/")
			if !ok {
				continue
			}
			if candidate := filepath.Dir(group); filepath.Clean(group) == group && filepath.Base(group) == "supervisor" && filepath.Base(candidate) == "nox-sandbox.service" {
				parent = candidate
			}
			break
		}
	}
	return &Backend{host: host, cgroupParent: parent}
}

func (backend *Backend) acquireUIDSlot() (int, error) {
	backend.uidMu.Lock()
	defer backend.uidMu.Unlock()
	if count := len(backend.freeUIDSlots); count > 0 {
		slot := backend.freeUIDSlots[count-1]
		backend.freeUIDSlots = backend.freeUIDSlots[:count-1]
		return slot, nil
	}
	if backend.nextUIDSlot >= backend.host.JailUIDCount {
		return 0, fmt.Errorf("no free jail UID/GID slots")
	}
	slot := backend.nextUIDSlot
	backend.nextUIDSlot++
	return slot, nil
}

func (backend *Backend) releaseUIDSlot(slot int) {
	backend.uidMu.Lock()
	defer backend.uidMu.Unlock()
	backend.freeUIDSlots = append(backend.freeUIDSlots, slot)
}

type machine struct {
	spec              vmm.Spec
	host              config.Host
	dir, root, cgroup string
	cmd               *exec.Cmd
	client            *http.Client
	jailID            int
	releaseUID        func()
	launcherDone      chan struct{}
	done              chan struct{}
	waitErr           error
	pid               int
	stopMu            sync.Mutex
	stopped           bool
}

var validID = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$`)

func (backend *Backend) start(ctx context.Context, spec vmm.Spec, snapshot *vmm.Snapshot) (*machine, error) {
	if !validID.MatchString(spec.ID) || spec.CPUQuotaPercent <= 0 || spec.MemMB <= 0 {
		return nil, fmt.Errorf("invalid instance parameters")
	}
	host := backend.host
	const maxMemoryMiB = ((1 << 63) - 1) >> 20
	if host.VMMemOverheadMB < 0 || int64(spec.MemMB) > maxMemoryMiB-int64(host.VMMemOverheadMB) {
		return nil, fmt.Errorf("invalid VM memory limit")
	}
	executable, err := filepath.EvalSymlinks(host.FirecrackerBin)
	if err != nil {
		return nil, err
	}
	bin := filepath.Base(executable)
	dir := filepath.Join(host.ChrootBase, bin, spec.ID)
	root := filepath.Join(dir, "root")
	if len(filepath.Join(root, "s", "v")) > 107 {
		return nil, fmt.Errorf("jail path exceeds Linux Unix socket limit")
	}
	if err := os.MkdirAll(filepath.Dir(dir), 0700); err != nil {
		return nil, err
	}
	if err := os.Mkdir(dir, 0700); err != nil {
		return nil, err
	}
	launched := false
	uidSlot, err := backend.acquireUIDSlot()
	if err != nil {
		_ = os.Remove(dir)
		return nil, err
	}
	jailID := host.JailUIDBase + uidSlot
	cgroup := filepath.Join("/sys/fs/cgroup", backend.cgroupParent, spec.ID)
	cgroupCreated := false
	defer func() {
		if !launched {
			if cgroupCreated {
				_ = os.Remove(cgroup)
			}
			if os.RemoveAll(dir) == nil {
				backend.releaseUIDSlot(uidSlot)
			}
		}
	}()
	assets := filepath.Join(root, "assets")
	if err := os.MkdirAll(assets, 0755); err != nil {
		return nil, err
	}
	if err := os.Chmod(assets, 0755); err != nil {
		return nil, err
	}
	sockets := filepath.Join(root, "s")
	if err := os.Mkdir(sockets, 0700); err != nil {
		return nil, err
	}
	if err := os.Chown(sockets, jailID, jailID); err != nil {
		return nil, err
	}
	for _, asset := range []struct {
		src, name string
		writable  bool
	}{
		{spec.KernelPath, "kernel", false}, {spec.RootfsPath, "rootfs", false}, {spec.DataDisk, "data", !spec.DataReadOnly},
	} {
		if asset.src == "" {
			continue
		}
		target := filepath.Join(assets, asset.name)
		if err := linkAsset(ctx, asset.src, target, jailID, asset.writable, asset.name == "data"); err != nil {
			return nil, fmt.Errorf("stage %s: %w", asset.name, err)
		}
	}
	if snapshot != nil {
		for _, file := range []string{"state", "mem"} {
			if err := linkAsset(ctx, filepath.Join(snapshot.Dir, file), filepath.Join(assets, "restore-"+file), jailID, false, false); err != nil {
				return nil, err
			}
		}
	}
	memoryLimit := (int64(spec.MemMB) + int64(host.VMMemOverheadMB)) << 20
	cmd := exec.Command(host.JailerBin, "--id", spec.ID, "--exec-file", executable,
		"--uid", strconv.Itoa(jailID), "--gid", strconv.Itoa(jailID), "--chroot-base-dir", host.ChrootBase, "--new-pid-ns",
		"--cgroup-version", "2", "--parent-cgroup", backend.cgroupParent,
		"--cgroup", fmt.Sprintf("cpu.max=%d 100000", int64(spec.CPUQuotaPercent)*1000),
		"--cgroup", fmt.Sprintf("memory.max=%d", memoryLimit),
		"--cgroup", "memory.swap.max=0", "--cgroup", "pids.max=64", "--", "--api-sock", "/s/a")
	cmd.SysProcAttr = &syscall.SysProcAttr{Pdeathsig: syscall.SIGKILL, Cloneflags: unix.CLONE_NEWNET}
	cmd.Stderr = os.Stderr
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(cgroup), 0755); err != nil {
		return nil, err
	}
	if err := os.Mkdir(cgroup, 0755); err != nil {
		return nil, err
	}
	cgroupCreated = true
	if _, err := os.Stat(filepath.Join(cgroup, "cgroup.kill")); err != nil {
		return nil, fmt.Errorf("cgroup v2 with cgroup.kill is required: %w", err)
	}
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	launched = true
	machine := &machine{spec: spec, host: host, dir: dir, root: root, cmd: cmd, jailID: jailID,
		releaseUID: func() { backend.releaseUIDSlot(uidSlot) }, launcherDone: make(chan struct{}),
		done: make(chan struct{}), cgroup: cgroup}
	machine.client = &http.Client{
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
		Transport: &http.Transport{DisableKeepAlives: true, DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(ctx, "unix", filepath.Join(sockets, "a"))
		}},
	}
	go machine.watch()
	select {
	case <-ctx.Done():
		return failedStart(machine, ctx.Err())
	case <-machine.launcherDone:
		if machine.waitErr != nil {
			return failedStart(machine, fmt.Errorf("jailer launch: %w", machine.waitErr))
		}
	}
	if err := os.Chown(root, 0, 0); err != nil {
		return failedStart(machine, err)
	}
	if err := os.Chmod(root, 0755); err != nil {
		return failedStart(machine, err)
	}
	if err := machine.readPID(bin); err != nil {
		return failedStart(machine, err)
	}
	if err := machine.retry(ctx, func(ctx context.Context) error { return machine.request(ctx, http.MethodGet, "/version", nil, nil) }); err != nil {
		return failedStart(machine, err)
	}
	return machine, nil
}

func linkAsset(ctx context.Context, source, target string, jailID int, writable, volume bool) error {
	info, err := os.Lstat(source)
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("expected a regular file")
	}
	if volume && writable {
		if err := os.Link(source, target); err != nil {
			return fmt.Errorf("writable volumes require a same-filesystem jail: %w", err)
		}
		if err := os.Chown(target, jailID, jailID); err != nil {
			return err
		}
		return os.Chmod(target, 0600)
	}
	stat := info.Sys().(*syscall.Stat_t)
	if !volume && stat.Uid == 0 && info.Mode().Perm()&0022 == 0 && (info.Mode().Perm()&0004 != 0 ||
		(int(stat.Gid) == jailID && info.Mode().Perm()&0040 != 0)) {
		if err := os.Link(source, target); err == nil {
			return nil
		}
	}
	if err := vmm.CopySparse(ctx, source, target); err != nil {
		return err
	}
	if err := os.Chown(target, jailID, jailID); err != nil {
		return err
	}
	return os.Chmod(target, 0400)
}

type payload = map[string]any

func (backend *Backend) Boot(ctx context.Context, spec vmm.Spec) (vmm.Machine, error) {
	machine, err := backend.start(ctx, spec, nil)
	if err != nil {
		return bootFailure(machine, err)
	}
	bootArgs := fmt.Sprintf("console=ttyS0 reboot=k panic=1 pci=off root=/dev/vda ro nox.role=%s nox.tmp_mb=%d init=/sbin/nox-init", spec.Role, spec.TmpMB)
	configurations := []struct {
		path string
		body payload
	}{
		{"/machine-config", payload{"vcpu_count": spec.VCPUs, "mem_size_mib": spec.MemMB, "smt": false}},
		{"/boot-source", payload{"kernel_image_path": "/assets/kernel", "boot_args": bootArgs}},
		{"/drives/rootfs", drive("rootfs", true, true, spec)},
		{"/vsock", payload{"uds_path": "/s/v", "guest_cid": 3}},
		{"/entropy", payload{}},
		{"/balloon", payload{"amount_mib": 0, "deflate_on_oom": true, "stats_polling_interval_s": 1}},
	}
	if spec.DataDisk != "" {
		configurations = append(configurations, struct {
			path string
			body payload
		}{"/drives/data", drive("data", false, spec.DataReadOnly, spec)})
	}
	configurations = append(configurations, struct {
		path string
		body payload
	}{"/actions", payload{"action_type": "InstanceStart"}})
	for _, item := range configurations {
		if err := machine.request(ctx, http.MethodPut, item.path, item.body, nil); err != nil {
			return bootFailure(failedStart(machine, err))
		}
	}
	return machine, nil
}

func drive(id string, root, readOnly bool, spec vmm.Spec) payload {
	data := payload{"drive_id": id, "path_on_host": "/assets/" + id, "is_root_device": root, "is_read_only": readOnly}
	limits := payload{}
	if spec.DiskBwMBs > 0 {
		limits["bandwidth"] = payload{"size": int64(spec.DiskBwMBs) << 20, "refill_time": 1000}
	}
	if spec.DiskIOPS > 0 {
		limits["ops"] = payload{"size": spec.DiskIOPS, "refill_time": 1000}
	}
	if len(limits) > 0 {
		data["rate_limiter"] = limits
	}
	return data
}

func (backend *Backend) Restore(ctx context.Context, spec vmm.Spec, snap vmm.Snapshot) (vmm.Machine, error) {
	machine, err := backend.start(ctx, spec, &snap)
	if err != nil {
		return bootFailure(machine, err)
	}
	if err := machine.request(ctx, http.MethodPut, "/snapshot/load", payload{
		"snapshot_path": "/assets/restore-state", "mem_backend": payload{"backend_type": "File", "backend_path": "/assets/restore-mem"}, "resume_vm": true,
	}, nil); err != nil {
		return bootFailure(failedStart(machine, err))
	}
	return machine, nil
}

func bootFailure(machine *machine, err error) (vmm.Machine, error) {
	if machine == nil {
		return nil, err
	}
	return machine, err
}

func failedStart(machine *machine, err error) (*machine, error) {
	cleanupErr := stop(machine)
	select {
	case <-machine.done:
		return nil, errors.Join(err, cleanupErr)
	default:
		return machine, errors.Join(err, cleanupErr)
	}
}

func (machine *machine) watch() {
	machine.waitErr = machine.cmd.Wait()
	close(machine.launcherDone)
	defer close(machine.done)
	for {
		data, err := os.ReadFile(filepath.Join(machine.cgroup, "cgroup.events"))
		if os.IsNotExist(err) || err == nil && strings.Contains("\n"+string(data), "\npopulated 0\n") {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func (machine *machine) readPID(bin string) error {
	file, err := os.OpenFile(filepath.Join(machine.root, bin+".pid"), os.O_RDONLY|unix.O_NOFOLLOW|unix.O_NONBLOCK, 0)
	if err != nil {
		return err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() || info.Size() > 32 {
		return fmt.Errorf("invalid Firecracker PID file")
	}
	data, err := io.ReadAll(io.LimitReader(file, 32))
	if err != nil {
		return err
	}
	pid, err := strconv.Atoi(strings.TrimSpace(string(data)))
	if err != nil || pid <= 1 {
		return fmt.Errorf("invalid Firecracker PID")
	}
	machine.pid = pid
	return nil
}

func (machine *machine) request(ctx context.Context, method, path string, body, response any) error {
	var reader io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			return err
		}
		reader = bytes.NewReader(data)
	}
	request, err := http.NewRequestWithContext(ctx, method, "http://localhost"+path, reader)
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	reply, err := machine.client.Do(request)
	if err != nil {
		return err
	}
	defer reply.Body.Close()
	limited := io.LimitReader(reply.Body, 8192)
	if reply.StatusCode < 200 || reply.StatusCode >= 300 {
		data, _ := io.ReadAll(limited)
		return fmt.Errorf("firecracker %s %s: %d: %s", method, path, reply.StatusCode, data)
	}
	if response != nil {
		return json.NewDecoder(limited).Decode(response)
	}
	return nil
}

func (machine *machine) retry(ctx context.Context, attempt func(context.Context) error) error {
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-machine.done:
			return fmt.Errorf("Firecracker cgroup is no longer populated")
		default:
		}
		short, cancel := context.WithTimeout(ctx, 500*time.Millisecond)
		err := attempt(short)
		cancel()
		if err == nil {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-machine.done:
			return fmt.Errorf("Firecracker cgroup is no longer populated")
		case <-time.After(30 * time.Millisecond):
		}
	}
}

func (machine *machine) ID() string            { return machine.spec.ID }
func (machine *machine) Spec() vmm.Spec        { return machine.spec }
func (machine *machine) Done() <-chan struct{} { return machine.done }
func (machine *machine) PID() int              { return machine.pid }

func (machine *machine) Dial(ctx context.Context) (net.Conn, error) {
	ctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	var connection net.Conn
	err := machine.retry(ctx, func(attempt context.Context) error {
		conn, err := (&net.Dialer{}).DialContext(attempt, "unix", filepath.Join(machine.root, "s", "v"))
		if err != nil {
			return err
		}
		if err := handshake(attempt, conn, machine.host.AgentVsockPort); err != nil {
			_ = conn.Close()
			return err
		}
		connection = conn
		return nil
	})
	return connection, err
}

func handshake(ctx context.Context, conn net.Conn, port uint32) error {
	deadline, _ := ctx.Deadline()
	if err := conn.SetDeadline(deadline); err != nil {
		return err
	}
	if _, err := fmt.Fprintf(conn, "CONNECT %d\n", port); err != nil {
		return err
	}
	var line [15]byte
	for index := range line {
		if _, err := io.ReadFull(conn, line[index:index+1]); err != nil {
			return err
		}
		if line[index] != '\n' {
			continue
		}
		number, ok := strings.CutPrefix(string(line[:index]), "OK ")
		value, err := strconv.ParseUint(number, 10, 32)
		if !ok || err != nil || value == 0 {
			break
		}
		return conn.SetDeadline(time.Time{})
	}
	return fmt.Errorf("invalid Firecracker vsock handshake")
}

func (machine *machine) Pause(ctx context.Context) error {
	return machine.request(ctx, http.MethodPatch, "/vm", payload{"state": "Paused"}, nil)
}
func (machine *machine) Resume(ctx context.Context) error {
	return machine.request(ctx, http.MethodPatch, "/vm", payload{"state": "Resumed"}, nil)
}
func (machine *machine) SetBalloon(ctx context.Context, mib int) error {
	if err := machine.request(ctx, http.MethodPatch, "/balloon", payload{"amount_mib": mib}, nil); err != nil {
		return err
	}
	if mib == 0 {
		return nil
	}
	settle, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	for {
		var stats struct {
			ActualMiB int `json:"actual_mib"`
		}
		if err := machine.request(settle, http.MethodGet, "/balloon/statistics", nil, &stats); err != nil {
			if settle.Err() == nil {
				return err
			}
			return ctx.Err()
		}
		if stats.ActualMiB >= mib*9/10 {
			return nil
		}
		select {
		case <-settle.Done():
			return ctx.Err()
		case <-time.After(150 * time.Millisecond):
		}
	}
}

func (machine *machine) Snapshot(ctx context.Context, dir string) (vmm.Snapshot, error) {
	assets := filepath.Join(machine.root, "assets")
	for _, name := range []string{"state", "mem"} {
		path := filepath.Join(assets, name)
		file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
		if err != nil {
			return vmm.Snapshot{}, err
		}
		defer os.Remove(path)
		err = errors.Join(file.Chown(machine.jailID, machine.jailID), file.Close())
		if err != nil {
			return vmm.Snapshot{}, err
		}
	}
	if err := machine.request(ctx, http.MethodPut, "/snapshot/create", payload{
		"snapshot_type": "Full", "snapshot_path": "/assets/state", "mem_file_path": "/assets/mem",
	}, nil); err != nil {
		return vmm.Snapshot{}, err
	}
	mem, err := os.OpenFile(filepath.Join(assets, "mem"), os.O_RDWR, 0)
	if err != nil {
		return vmm.Snapshot{}, err
	}
	defer mem.Close()
	if err := vmm.PunchZeroPages(ctx, mem); err != nil && !errors.Is(err, unix.EOPNOTSUPP) && !errors.Is(err, unix.ENOSYS) {
		return vmm.Snapshot{}, err
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return vmm.Snapshot{}, err
	}
	if err := os.Chmod(dir, 0700); err != nil {
		return vmm.Snapshot{}, err
	}
	for _, name := range []string{"state", "mem"} {
		destination := filepath.Join(dir, name)
		if err := vmm.CopySparse(ctx, filepath.Join(assets, name), destination); err != nil {
			return vmm.Snapshot{}, err
		}
		if err := os.Chown(destination, 0, 0); err != nil {
			return vmm.Snapshot{}, err
		}
		if err := os.Chmod(destination, 0644); err != nil {
			return vmm.Snapshot{}, err
		}
	}
	info, err := os.Stat(filepath.Join(dir, "mem"))
	if err != nil {
		return vmm.Snapshot{}, err
	}
	return vmm.Snapshot{Dir: dir, ImageVersion: machine.spec.ImageVersion, MemBytes: info.Sys().(*syscall.Stat_t).Blocks * 512}, nil
}

func (machine *machine) CPUSeconds() (float64, error) {
	data, err := os.ReadFile(filepath.Join(machine.cgroup, "cpu.stat"))
	if err != nil {
		return 0, err
	}
	for _, line := range strings.Split(string(data), "\n") {
		if raw, ok := strings.CutPrefix(line, "usage_usec "); ok {
			microseconds, err := strconv.ParseUint(raw, 10, 64)
			return float64(microseconds) / 1e6, err
		}
	}
	return 0, fmt.Errorf("cpu.stat missing usage_usec")
}

func stop(machine *machine) error {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	return machine.Stop(ctx)
}

func (machine *machine) Stop(ctx context.Context) error {
	machine.stopMu.Lock()
	defer machine.stopMu.Unlock()
	if machine.stopped {
		return nil
	}
	if err := machine.cmd.Process.Kill(); err != nil && !errors.Is(err, os.ErrProcessDone) {
		return err
	}
	select {
	case <-machine.launcherDone:
	case <-ctx.Done():
		return ctx.Err()
	}
	if err := machine.killCgroup(); err != nil {
		select {
		case <-machine.done:
		default:
			return err
		}
	}
	select {
	case <-machine.done:
	case <-ctx.Done():
		return ctx.Err()
	}
	err := os.Remove(machine.cgroup)
	if os.IsNotExist(err) {
		err = nil
	}
	jailErr := os.RemoveAll(machine.dir)
	if jailErr == nil {
		machine.releaseUID()
	}
	machine.stopped = true
	return errors.Join(err, jailErr)
}

func (machine *machine) killCgroup() error {
	file, err := os.OpenFile(filepath.Join(machine.cgroup, "cgroup.kill"), os.O_WRONLY, 0)
	if os.IsNotExist(err) {
		if _, statErr := os.Stat(machine.cgroup); os.IsNotExist(statErr) {
			return nil
		}
	}
	if err != nil {
		return fmt.Errorf("open VM cgroup.kill: %w", err)
	}
	_, err = file.WriteString("1")
	return errors.Join(err, file.Close())
}
