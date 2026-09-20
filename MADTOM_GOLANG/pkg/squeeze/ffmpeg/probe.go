package ffmpeg

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"
)

func Probe(ctx context.Context, binary, input string) (float64, error) {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	out, err := exec.CommandContext(ctx, binary, "-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration:stream=codec_type", "-of", "json", input).Output()
	if err != nil {
		return 0, fmt.Errorf("ffprobe: %w", err)
	}
	var result struct {
		Format struct {
			Duration string `json:"duration"`
		} `json:"format"`
		Streams []struct {
			CodecType string `json:"codec_type"`
		} `json:"streams"`
	}
	if err = json.Unmarshal(out, &result); err != nil {
		return 0, err
	}
	video := false
	for _, s := range result.Streams {
		if s.CodecType == "video" {
			video = true
		}
	}
	if !video {
		return 0, fmt.Errorf("input has no video stream")
	}
	return number(result.Format.Duration), nil
}

// HardwareCache stores the probed encoder capabilities along with the hardware signature.
type HardwareCache struct {
	Signature        string    `json:"signature"`
	CPU              string    `json:"cpu"`
	GPU              string    `json:"gpu"`
	AllEncoders      []string  `json:"all_encoders"`
	HardwareEncoders []string  `json:"hardware_encoders"`
	Status           string    `json:"status"`
	ProbedAt         time.Time `json:"probed_at"`
}

// ComputeHardwareSignature computes a signature based on CPU model, GPU devices, and FFmpeg binary.
func ComputeHardwareSignature(ctx context.Context, binary string) (sig string, cpuDesc string, gpuDesc string) {
	// 1. CPU
	cpuDesc = fmt.Sprintf("%s_%s (%d cores)", runtime.GOOS, runtime.GOARCH, runtime.NumCPU())
	if data, err := os.ReadFile("/proc/cpuinfo"); err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			if strings.HasPrefix(line, "model name") {
				parts := strings.SplitN(line, ":", 2)
				if len(parts) == 2 {
					cpuDesc = fmt.Sprintf("%s (%d cores)", strings.TrimSpace(parts[1]), runtime.NumCPU())
					break
				}
			}
		}
	}

	// 2. GPU
	var gpus []string
	if matches, err := filepath.Glob("/sys/class/drm/card*/device/vendor"); err == nil && len(matches) > 0 {
		for _, vendorPath := range matches {
			devicePath := filepath.Join(filepath.Dir(vendorPath), "device")
			vBytes, errV := os.ReadFile(vendorPath)
			dBytes, errD := os.ReadFile(devicePath)
			if errV == nil && errD == nil {
				v := strings.TrimSpace(string(vBytes))
				d := strings.TrimSpace(string(dBytes))
				gpus = append(gpus, fmt.Sprintf("%s:%s", v, d))
			}
		}
	}
	if len(gpus) == 0 {
		if entries, err := os.ReadDir("/dev/dri"); err == nil {
			for _, e := range entries {
				gpus = append(gpus, e.Name())
			}
		}
	}
	if data, err := os.ReadFile("/proc/driver/nvidia/version"); err == nil {
		lines := strings.Split(string(data), "\n")
		if len(lines) > 0 {
			gpus = append(gpus, strings.TrimSpace(lines[0]))
		}
	}
	sort.Strings(gpus)
	gpuDesc = strings.Join(gpus, ";")

	// 3. FFmpeg version
	ffmpegVer := binary
	outCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	if out, err := exec.CommandContext(outCtx, binary, "-version").Output(); err == nil {
		lines := strings.Split(string(out), "\n")
		if len(lines) > 0 {
			ffmpegVer = strings.TrimSpace(lines[0])
		}
	}

	raw := fmt.Sprintf("%s|%s|%s", cpuDesc, gpuDesc, ffmpegVer)
	hash := sha256.Sum256([]byte(raw))
	sig = hex.EncodeToString(hash[:])
	return sig, cpuDesc, gpuDesc
}

// GetCacheFilePath resolves the file path for the hardware cache JSON.
func GetCacheFilePath(customDir string) string {
	if customDir != "" {
		return filepath.Join(customDir, "hardware_cache.json")
	}
	if userCache, err := os.UserCacheDir(); err == nil && userCache != "" {
		return filepath.Join(userCache, "squeeze", "hardware_cache.json")
	}
	return filepath.Join(os.TempDir(), "squeeze", "hardware_cache.json")
}

// InvalidateHardwareCache removes the cached hardware probe file.
func InvalidateHardwareCache(cacheDir string) error {
	p := GetCacheFilePath(cacheDir)
	if err := os.Remove(p); err != nil && !os.IsNotExist(err) {
		return err
	}
	return nil
}

// TestHardwareEncoder executes a fast 1-frame dummy dry-run to test if the driver/device initializes.
func TestHardwareEncoder(ctx context.Context, binary, name string) bool {
	testCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()

	var args []string
	if strings.HasSuffix(name, "_vaapi") {
		args = []string{"-hide_banner", "-v", "error", "-init_hw_device", "vaapi=va", "-filter_hw_device", "va", "-f", "lavfi", "-i", "color=size=128x128:rate=1:duration=0.04", "-vf", "format=nv12,hwupload", "-frames:v", "1", "-c:v", name, "-f", "null", "-"}
	} else if strings.HasSuffix(name, "_qsv") {
		args = []string{"-hide_banner", "-v", "error", "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw", "-f", "lavfi", "-i", "color=size=128x128:rate=1:duration=0.04", "-frames:v", "1", "-c:v", name, "-f", "null", "-"}
	} else {
		args = []string{"-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=size=128x128:rate=1:duration=0.04", "-frames:v", "1", "-c:v", name, "-f", "null", "-"}
	}

	cmd := exec.CommandContext(testCtx, binary, args...)
	err := cmd.Run()
	return err == nil
}

// ProbeHardware probes all encoders, actively tests candidate hardware encoders, and caches the result.
func ProbeHardware(ctx context.Context, binary string, cacheDir string, forceRefresh bool) ([]string, []string, string, error) {
	sig, cpuDesc, gpuDesc := ComputeHardwareSignature(ctx, binary)
	cachePath := GetCacheFilePath(cacheDir)

	if !forceRefresh {
		if data, err := os.ReadFile(cachePath); err == nil {
			var c HardwareCache
			if err := json.Unmarshal(data, &c); err == nil {
				if c.Signature == sig && len(c.AllEncoders) > 0 {
					return c.AllEncoders, c.HardwareEncoders, c.Status + " (cached)", nil
				}
			}
		}
	}

	// Probe all compiled encoders from ffmpeg
	probeCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	out, err := exec.CommandContext(probeCtx, binary, "-hide_banner", "-encoders").Output()
	if err != nil {
		return nil, nil, "", fmt.Errorf("ffmpeg -encoders: %w", err)
	}

	all := []string{}
	var candidates []string
	for _, line := range strings.Split(string(out), "\n") {
		fields := strings.Fields(line)
		if len(fields) < 2 || len(fields[0]) != 6 || fields[1] == "=" {
			continue
		}
		if fields[0][0] != 'V' && fields[0][0] != 'A' {
			continue
		}
		name := fields[1]
		all = append(all, name)
		if isCandidateHardware(name) {
			candidates = append(candidates, name)
		}
	}
	sort.Strings(all)

	// Actively test candidate hardware encoders concurrently
	type res struct {
		name string
		ok   bool
	}
	resCh := make(chan res, len(candidates))
	sem := make(chan struct{}, 4)
	var wg sync.WaitGroup
	for _, cand := range candidates {
		wg.Add(1)
		go func(c string) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			ok := TestHardwareEncoder(ctx, binary, c)
			resCh <- res{name: c, ok: ok}
		}(cand)
	}
	wg.Wait()
	close(resCh)

	var verified []string
	for r := range resCh {
		if r.ok {
			verified = append(verified, r.name)
		}
	}
	sort.Strings(verified)

	var status string
	if len(verified) > 0 {
		status = fmt.Sprintf("active_hardware_verified (%d active: %s)", len(verified), strings.Join(verified, ", "))
	} else {
		status = "software_only (no compatible hardware acceleration detected)"
	}

	cacheData := HardwareCache{
		Signature:        sig,
		CPU:              cpuDesc,
		GPU:              gpuDesc,
		AllEncoders:      all,
		HardwareEncoders: verified,
		Status:           status,
		ProbedAt:         time.Now().UTC(),
	}

	if encoded, err := json.MarshalIndent(cacheData, "", "  "); err == nil {
		_ = os.MkdirAll(filepath.Dir(cachePath), 0755)
		_ = os.WriteFile(cachePath, encoded, 0644)
	}

	return all, verified, status, nil
}

func isCandidateHardware(name string) bool {
	return strings.HasSuffix(name, "_nvenc") ||
		strings.HasSuffix(name, "_qsv") ||
		strings.HasSuffix(name, "_vaapi") ||
		strings.HasSuffix(name, "_amf") ||
		strings.HasSuffix(name, "_videotoolbox") ||
		strings.HasSuffix(name, "_v4l2m2m") ||
		strings.HasSuffix(name, "_mf")
}

// Encoders reports active encoders for backwards-compatibility.
func Encoders(ctx context.Context, binary string) ([]string, []string, error) {
	all, hw, _, err := ProbeHardware(ctx, binary, "", false)
	return all, hw, err
}
