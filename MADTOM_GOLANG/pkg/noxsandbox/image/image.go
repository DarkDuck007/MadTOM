package image

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
)

var (
	pip            = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}(\[[A-Za-z0-9,._-]+\])?((==|>=|<=|~=)[A-Za-z0-9.*+!_-]{1,32})?$`)
	apt            = regexp.MustCompile(`^[a-z0-9][a-z0-9+.-]{1,62}$`)
	versionPattern = regexp.MustCompile(`^[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[1-9][0-9]*$`)
	pipName        = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]*`)
)

type Packages struct {
	Pip []string `json:"pip"`
	Apt []string `json:"apt"`
}

func Normalize(value Packages) (Packages, error) {
	result := Packages{Pip: []string{}, Apt: []string{}}
	for _, group := range []struct {
		items   []string
		pattern *regexp.Regexp
		output  *[]string
	}{
		{value.Pip, pip, &result.Pip}, {value.Apt, apt, &result.Apt},
	} {
		if len(group.items) > 1024 {
			return Packages{}, fmt.Errorf("too many packages")
		}
		for _, name := range group.items {
			if len(name) > 512 || !group.pattern.MatchString(name) {
				return Packages{}, fmt.Errorf("invalid package specification: %.80q", name)
			}
			*group.output = append(*group.output, name)
		}
		slices.Sort(*group.output)
		*group.output = slices.Compact(*group.output)
	}
	return result, nil
}

func ImportName(pkg string) string     { return strings.ReplaceAll(pipName.FindString(pkg), "-", "_") }
func ValidVersion(version string) bool { return versionPattern.MatchString(version) }

type Build struct {
	Version  string    `json:"version"`
	OK       bool      `json:"ok"`
	Finished time.Time `json:"finished"`
	LogTail  string    `json:"log_tail"`
}

type Status struct {
	CurrentVersion  string         `json:"current_version"`
	Building        bool           `json:"building"`
	BuildStarted    *time.Time     `json:"build_started"`
	LastBuild       *Build         `json:"last_build"`
	Packages        Packages       `json:"packages"`
	PendingPackages Packages       `json:"pending_packages"`
	VersionsInUse   map[string]int `json:"versions_in_use"`
}

type Runner struct {
	mu      sync.Mutex
	host    config.Host
	now     func() time.Time
	started *time.Time
	last    *Build
	desired *Packages
}

func New(host config.Host, now func() time.Time) *Runner {
	runner := &Runner{host: host, now: now}
	var state struct {
		Desired *Packages
		Last    *Build
	}
	if data, err := os.ReadFile(runner.statePath()); err == nil && json.Unmarshal(data, &state) == nil {
		runner.desired, runner.last = state.Desired, state.Last
	}
	return runner
}

func (runner *Runner) statePath() string {
	return filepath.Join(runner.host.StateDir, "image-status.json")
}

func (runner *Runner) save() error {
	data, err := json.Marshal(struct {
		Desired *Packages
		Last    *Build
	}{runner.desired, runner.last})
	if err != nil {
		return err
	}
	return writeAtomic(runner.statePath(), data)
}

func writeAtomic(path string, data []byte) error {
	file, err := os.CreateTemp(filepath.Dir(path), ".image-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	if _, err := file.Write(data); err != nil {
		_ = file.Close()
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	return os.Rename(file.Name(), path)
}

func (runner *Runner) Current() string {
	link, err := os.Readlink(filepath.Join(runner.host.ImagesDir, "current"))
	if err != nil {
		return ""
	}
	version := filepath.Base(filepath.Clean(link))
	if !ValidVersion(version) || (link != version && filepath.Clean(link) != filepath.Join(runner.host.ImagesDir, version)) {
		return ""
	}
	return version
}

func (runner *Runner) packages(version string) Packages {
	var manifest struct {
		Packages Packages `json:"packages"`
	}
	if version != "" {
		if data, err := os.ReadFile(filepath.Join(runner.host.ImagesDir, version, "manifest.json")); err == nil {
			_ = json.Unmarshal(data, &manifest)
		}
	}
	if manifest.Packages.Pip == nil {
		manifest.Packages.Pip = []string{}
	}
	if manifest.Packages.Apt == nil {
		manifest.Packages.Apt = []string{}
	}
	return manifest.Packages
}

func (runner *Runner) applied(version string) (Packages, bool) {
	empty := Packages{Pip: []string{}, Apt: []string{}}
	if version == "" {
		return empty, false
	}
	data, err := os.ReadFile(filepath.Join(runner.host.ImagesDir, version, "approved-packages.json"))
	if err != nil {
		return empty, false
	}
	var packages Packages
	if json.Unmarshal(data, &packages) != nil {
		return empty, false
	}
	return packages, true
}

func (runner *Runner) status() Status {
	version := runner.Current()
	installed := runner.packages(version)
	pending := Packages{Pip: []string{}, Apt: []string{}}
	if runner.desired != nil {
		desired := *runner.desired
		for _, pkg := range desired.Pip {
			if !slices.Contains(installed.Pip, pkg) {
				pending.Pip = append(pending.Pip, pkg)
			}
		}
		for _, pkg := range desired.Apt {
			if !slices.Contains(installed.Apt, pkg) {
				pending.Apt = append(pending.Apt, pkg)
			}
		}
	}
	return Status{CurrentVersion: version, Building: runner.started != nil, BuildStarted: runner.started, LastBuild: runner.last,
		Packages: installed, PendingPackages: pending, VersionsInUse: map[string]int{}}
}

func (runner *Runner) Status() Status {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	return runner.status()
}

func (runner *Runner) SetPackages(packages Packages) error {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	previous := runner.desired
	runner.desired = &packages
	if err := runner.save(); err != nil {
		runner.desired = previous
		return err
	}
	return nil
}

func (runner *Runner) Pending() bool {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	if runner.desired == nil {
		return false
	}
	if applied, ok := runner.applied(runner.Current()); ok {
		return !reflect.DeepEqual(*runner.desired, applied)
	}
	pending := runner.status().PendingPackages
	return len(pending.Pip)+len(pending.Apt) > 0
}

func (runner *Runner) Begin() (func(context.Context, func(context.Context, string, Packages) error), bool) {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	if runner.started != nil {
		return nil, false
	}
	now := runner.now()
	runner.started = &now
	packages, _ := runner.applied(runner.Current())
	if runner.desired != nil {
		packages = *runner.desired
	}
	return func(ctx context.Context, smoke func(context.Context, string, Packages) error) {
		runner.build(ctx, now, packages, smoke)
	}, true
}

func (runner *Runner) build(ctx context.Context, now time.Time, packages Packages, smoke func(context.Context, string, Packages) error) {
	var tail tailBuffer
	version, err := runner.run(ctx, now, packages, smoke, &tail)
	if err != nil {
		_, _ = fmt.Fprintf(&tail, "\n%s\n", err)
	}
	runner.mu.Lock()
	defer runner.mu.Unlock()
	runner.started = nil
	runner.last = &Build{Version: version, OK: err == nil, Finished: runner.now(), LogTail: strings.ToValidUTF8(tail.String(), "�")}
	if err := runner.save(); err != nil {
		log.Printf("nox-sandbox: save image status: %v", err)
	}
}

func (runner *Runner) run(ctx context.Context, now time.Time, packages Packages, smoke func(context.Context, string, Packages) error, tail *tailBuffer) (string, error) {
	if _, err := Normalize(packages); err != nil {
		return "", err
	}
	if err := os.MkdirAll(runner.host.ImagesDir, 0755); err != nil {
		return "", err
	}
	date := now.Format("2006.01.02")
	entries, err := os.ReadDir(runner.host.ImagesDir)
	if err != nil {
		return "", err
	}
	number := 0
	for _, entry := range entries {
		if part, ok := strings.CutPrefix(entry.Name(), date+"-"); ok {
			if value, err := strconv.Atoi(part); err == nil {
				number = max(number, value)
			}
		}
	}
	version := fmt.Sprintf("%s-%d", date, number+1)
	file, err := os.CreateTemp(runner.host.StateDir, ".packages-*.json")
	if err != nil {
		return version, err
	}
	defer os.Remove(file.Name())
	err = json.NewEncoder(file).Encode(packages)
	closeErr := file.Close()
	if err != nil {
		return version, err
	}
	if closeErr != nil {
		return version, closeErr
	}
	buildCtx, cancel := context.WithTimeout(ctx, 2*time.Hour)
	defer cancel()
	out := filepath.Join(runner.host.ImagesDir, version)
	command := exec.CommandContext(buildCtx, runner.host.ImageBuildScript, "--out", out, "--packages", file.Name(), "--version", version)
	command.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	command.Cancel = func() error { return syscall.Kill(-command.Process.Pid, syscall.SIGKILL) }
	command.WaitDelay = 5 * time.Second
	command.Stdout, command.Stderr = tail, tail
	if err := command.Run(); err != nil {
		return version, fmt.Errorf("build script: %w", err)
	}
	if err := smoke(buildCtx, version, packages); err != nil {
		return version, fmt.Errorf("smoke test: %w", err)
	}
	if err := buildCtx.Err(); err != nil {
		return version, err
	}
	approved, err := json.Marshal(packages)
	if err != nil {
		return version, err
	}
	if err := writeAtomic(filepath.Join(out, "approved-packages.json"), approved); err != nil {
		return version, err
	}
	next := filepath.Join(runner.host.ImagesDir, ".current-"+version)
	if err := os.Symlink(version, next); err != nil {
		return version, err
	}
	defer os.Remove(next)
	return version, os.Rename(next, filepath.Join(runner.host.ImagesDir, "current"))
}

type tailBuffer struct{ buffer bytes.Buffer }

func (tail *tailBuffer) String() string { return tail.buffer.String() }

func (tail *tailBuffer) Write(data []byte) (int, error) {
	const limit = 16 << 10
	count := len(data)
	if count >= limit {
		tail.buffer.Reset()
		data = data[count-limit:]
	} else if excess := tail.buffer.Len() + count - limit; excess > 0 {
		tail.buffer.Next(excess)
	}
	_, _ = tail.buffer.Write(data)
	return count, nil
}
