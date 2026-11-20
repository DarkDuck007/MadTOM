package config

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"syscall"
	"time"

	"gopkg.in/yaml.v3"
)

type Ceilings struct {
	VMMemMB       int `yaml:"vm_mem_mb"`
	VMVCPUs       int `yaml:"vm_vcpus"`
	DiskGBPerChat int `yaml:"disk_gb_per_chat"`
	MaxVMs        int `yaml:"max_vms"`
	MaxGuestMemMB int `yaml:"max_guest_mem_mb"`
	ExecTimeoutS  int `yaml:"exec_timeout_s"`
	MaxFrameBytes int `yaml:"max_frame_bytes"`
}

type Host struct {
	FirecrackerBin    string   `yaml:"firecracker_bin"`
	JailerBin         string   `yaml:"jailer_bin"`
	KernelPath        string   `yaml:"kernel_path"`
	ImagesDir         string   `yaml:"images_dir"`
	StateDir          string   `yaml:"state_dir"`
	ChrootBase        string   `yaml:"chroot_base"`
	SocketPath        string   `yaml:"socket_path"`
	SocketGroup       string   `yaml:"socket_group"`
	AllowedUIDs       []uint32 `yaml:"allowed_uids"`
	JailUIDBase       int      `yaml:"jail_uid_base"`
	JailUIDCount      int      `yaml:"jail_uid_count"`
	VMMemOverheadMB   int      `yaml:"vm_mem_overhead_mb"`
	JailUID           int      `yaml:"jail_uid,omitempty"`
	JailGID           int      `yaml:"jail_gid,omitempty"`
	NoxChatConfigPath string   `yaml:"noxchat_config_path"`
	ImageBuildScript  string   `yaml:"image_build_script"`
	AgentVsockPort    uint32   `yaml:"agent_vsock_port"`
	Ceilings          Ceilings `yaml:"ceilings"`
}

const (
	DefaultJailUIDBase     = 64000
	DefaultJailUIDCount    = 64
	DefaultVMMemOverheadMB = 192
)

type ChatVM struct {
	MemMB           int `yaml:"mem_mb"`
	VCPUs           int `yaml:"vcpus"`
	CPUQuotaPercent int `yaml:"cpu_quota_percent"`
	DiskGB          int `yaml:"disk_gb"`
	DiskBwMBs       int `yaml:"disk_bw_mb_s"`
	DiskIOPS        int `yaml:"disk_iops"`
	PidsMax         int `yaml:"pids_max"`
}

type TempVM struct {
	MemMB           int `yaml:"mem_mb"`
	VCPUs           int `yaml:"vcpus"`
	CPUQuotaPercent int `yaml:"cpu_quota_percent"`
	TmpDiskMB       int `yaml:"tmp_disk_mb"`
	PoolSize        int `yaml:"pool_size"`
}

type PreVM struct {
	MemMB           int `yaml:"mem_mb"`
	VCPUs           int `yaml:"vcpus"`
	CPUQuotaPercent int `yaml:"cpu_quota_percent"`
	PoolSize        int `yaml:"pool_size"`
	ExecTimeoutMS   int `yaml:"exec_timeout_ms"`
}

type Global struct {
	MaxVMs              int `yaml:"max_vms"`
	MaxGuestMemMB       int `yaml:"max_guest_mem_mb"`
	QueueTimeoutSeconds int `yaml:"queue_timeout_seconds"`
}

type LibraryRequests struct {
	MaxPendingPerChat  int    `yaml:"max_pending_per_chat"`
	RebuildWindowLocal string `yaml:"rebuild_window_local"`
}

type Sandbox struct {
	Enabled                     bool            `yaml:"enabled"`
	SocketPath                  string          `yaml:"socket_path"`
	EnabledChats                []int64         `yaml:"enabled_chats"`
	AllowAllChats               bool            `yaml:"allow_all_chats"`
	AllowGuestChats             bool            `yaml:"allow_guest_chats"`
	ClientTimeoutMarginSeconds  int             `yaml:"client_timeout_margin_seconds"`
	ChatVM                      ChatVM          `yaml:"chat_vm"`
	TempVM                      TempVM          `yaml:"temp_vm"`
	PreVM                       PreVM           `yaml:"pre_vm"`
	WarmSeconds                 int             `yaml:"warm_seconds"`
	Hibernate                   bool            `yaml:"hibernate"`
	HibernateTTLHours           int             `yaml:"hibernate_ttl_hours"`
	MaxSessionLifetimeMinutes   int             `yaml:"max_session_lifetime_minutes"`
	ExecTimeoutSeconds          int             `yaml:"exec_timeout_seconds"`
	ExecTimeoutMaxSeconds       int             `yaml:"exec_timeout_max_seconds"`
	BackgroundTimeoutMaxSeconds int             `yaml:"background_timeout_max_seconds"`
	MaxBackgroundJobsPerChat    int             `yaml:"max_background_jobs_per_chat"`
	MaxOutputBytes              int             `yaml:"max_output_bytes"`
	MaxInputBytes               int             `yaml:"max_input_bytes"`
	MaxExportBytes              int             `yaml:"max_export_bytes"`
	MaxExportFiles              int             `yaml:"max_export_files"`
	MaxFileListEntries          int             `yaml:"max_file_list_entries"`
	MaxTextViewBytes            int             `yaml:"max_text_view_bytes"`
	PreviewMaxPx                int             `yaml:"preview_max_px"`
	DailyCPUSecondsPerChat      int             `yaml:"daily_cpu_seconds_per_chat"`
	SnapshotQuotaMBPerChat      int             `yaml:"snapshot_quota_mb_per_chat"`
	BackupsKeep                 int             `yaml:"backups_keep"`
	BackupHourLocal             int             `yaml:"backup_hour_local"`
	DiskWatermarkPercent        int             `yaml:"disk_watermark_percent"`
	Global                      Global          `yaml:"global"`
	DiskIndexEntries            int             `yaml:"disk_index_entries"`
	ExportAllowedExts           []string        `yaml:"export_allowed_exts"`
	LibraryRequests             LibraryRequests `yaml:"library_requests"`
	PromptUntrustedWrap         bool            `yaml:"prompt_untrusted_wrap"`
}

type Effective struct {
	Host    Host    `yaml:"host"`
	Sandbox Sandbox `yaml:"sandbox"`
}

func (cfg Effective) MarshalJSON() ([]byte, error) {
	data, err := yaml.Marshal(cfg)
	if err != nil {
		return nil, err
	}
	var fields map[string]any
	if err := yaml.Unmarshal(data, &fields); err != nil {
		return nil, err
	}
	return json.Marshal(fields)
}

func Defaults() Sandbox {
	return Sandbox{
		SocketPath: "/run/nox-sandbox/api.sock", EnabledChats: []int64{}, ClientTimeoutMarginSeconds: 15,
		ChatVM: ChatVM{1024, 2, 100, 2, 100, 2000, 512}, TempVM: TempVM{1024, 2, 100, 1024, 1}, PreVM: PreVM{192, 1, 50, 2, 300},
		WarmSeconds: 300, Hibernate: true, HibernateTTLHours: 24, MaxSessionLifetimeMinutes: 60,
		ExecTimeoutSeconds: 120, ExecTimeoutMaxSeconds: 600, BackgroundTimeoutMaxSeconds: 1800, MaxBackgroundJobsPerChat: 2,
		MaxOutputBytes: 65536, MaxInputBytes: 52428800, MaxExportBytes: 52428800, MaxExportFiles: 20,
		MaxFileListEntries: 2000, MaxTextViewBytes: 262144, PreviewMaxPx: 1600, DailyCPUSecondsPerChat: 3600,
		SnapshotQuotaMBPerChat: 2048, BackupsKeep: 3, BackupHourLocal: 4, DiskWatermarkPercent: 90,
		Global: Global{8, 8192, 30}, DiskIndexEntries: 15,
		ExportAllowedExts: []string{"pdf", "docx", "xlsx", "pptx", "odt", "ods", "odp", "csv", "tsv", "txt", "md", "json", "html", "xml", "yaml", "svg", "png", "jpg", "jpeg", "webp", "gif", "zip", "py", "ipynb", "log"},
		LibraryRequests:   LibraryRequests{5, "04:30"}, PromptUntrustedWrap: true,
	}
}

func readConfig(path string, limit int64, trusted bool) ([]byte, error) {
	file, err := os.OpenFile(path, os.O_RDONLY|syscall.O_NONBLOCK, 0)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || (trusted && (info.Mode().Perm()&022 != 0 || (os.Geteuid() == 0 && info.Sys().(*syscall.Stat_t).Uid != 0))) {
		return nil, fmt.Errorf("unsafe config file %s", path)
	}
	data, err := io.ReadAll(io.LimitReader(file, limit+1))
	if int64(len(data)) > limit {
		return nil, fmt.Errorf("config too large: %s", path)
	}
	return data, err
}

func decode(data []byte, target any, strict bool) error {
	decoder := yaml.NewDecoder(bytes.NewReader(data))
	decoder.KnownFields(strict)
	if err := decoder.Decode(target); err != nil {
		return err
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return fmt.Errorf("expected one YAML document")
	}
	return nil
}

func Load(hostPath string) (*Effective, error) {
	data, err := readConfig(hostPath, 1<<20, true)
	if err != nil {
		return nil, err
	}
	host := Host{JailUIDCount: DefaultJailUIDCount, VMMemOverheadMB: DefaultVMMemOverheadMB}
	if err := decode(data, &host, true); err != nil {
		return nil, err
	}
	for _, path := range []string{host.FirecrackerBin, host.JailerBin, host.KernelPath, host.ImagesDir, host.StateDir, host.ChrootBase, host.SocketPath, host.NoxChatConfigPath, host.ImageBuildScript} {
		if !filepath.IsAbs(path) || filepath.Clean(path) != path || path == "/" {
			return nil, fmt.Errorf("host paths must be absolute, canonical, and non-root: %q", path)
		}
	}
	ceil := host.Ceilings
	if host.SocketGroup == "" || host.AllowedUIDs == nil || host.AgentVsockPort == 0 ||
		!positive(ceil.VMMemMB, ceil.VMVCPUs, ceil.DiskGBPerChat, ceil.MaxVMs, ceil.MaxGuestMemMB, ceil.ExecTimeoutS, ceil.MaxFrameBytes) ||
		int64(ceil.MaxFrameBytes) > int64(^uint32(0)) || int64(ceil.DiskGBPerChat) > (1<<33)-1 {
		return nil, fmt.Errorf("host.yaml requires all host settings and positive, representable ceilings")
	}
	if host.JailUID < 0 || host.JailGID < 0 {
		return nil, fmt.Errorf("deprecated jail_uid and jail_gid must be positive when set")
	}
	if host.JailUIDBase == 0 {
		if host.JailUID != 0 && host.JailGID != 0 && host.JailUID != host.JailGID {
			return nil, fmt.Errorf("jail_uid and jail_gid disagree; set jail_uid_base explicitly")
		}
		host.JailUIDBase = max(host.JailUID, host.JailGID)
		if host.JailUIDBase == 0 {
			host.JailUIDBase = DefaultJailUIDBase
		}
	}
	const maxLinuxID = int64(^uint32(0)) - 1
	maxID := min(maxLinuxID, int64(^uint(0)>>1))
	if !positive(host.JailUIDBase, host.JailUIDCount, host.VMMemOverheadMB) ||
		int64(host.JailUIDCount)-1 > maxID-int64(host.JailUIDBase) {
		return nil, fmt.Errorf("invalid jail UID/GID range or VM memory overhead")
	}
	const maxMemoryMiB = ((1 << 63) - 1) >> 20
	if int64(host.VMMemOverheadMB) > maxMemoryMiB-int64(ceil.VMMemMB) {
		return nil, fmt.Errorf("VM memory ceiling plus overhead exceeds cgroup limit representation")
	}
	sandbox := loadSandbox(host)
	remaining := host.JailUIDCount - ceil.MaxVMs
	if remaining < sandbox.TempVM.PoolSize || remaining-sandbox.TempVM.PoolSize < sandbox.PreVM.PoolSize {
		return nil, fmt.Errorf("jail_uid_count must cover ceilings.max_vms plus temp_vm.pool_size and pre_vm.pool_size")
	}
	return &Effective{Host: host, Sandbox: sandbox}, nil
}

func positive(values ...int) bool {
	for _, value := range values {
		if value <= 0 {
			return false
		}
	}
	return true
}

func Reload(host Host) *Effective {
	sandbox := loadSandbox(host)
	if host.JailUIDCount > 0 {
		remaining := max(0, host.JailUIDCount-host.Ceilings.MaxVMs)
		sandbox.TempVM.PoolSize = min(sandbox.TempVM.PoolSize, remaining)
		sandbox.PreVM.PoolSize = min(sandbox.PreVM.PoolSize, remaining-sandbox.TempVM.PoolSize)
	}
	return &Effective{Host: host, Sandbox: sandbox}
}

func loadSandbox(host Host) Sandbox {
	sandbox := Defaults()
	var doc struct {
		Sandbox yaml.Node `yaml:"sandbox"`
	}
	if data, err := readConfig(host.NoxChatConfigPath, 4<<20, false); err == nil && decode(data, &doc, false) == nil && doc.Sandbox.Kind == yaml.MappingNode {
		candidate := Defaults()
		if data, err := yaml.Marshal(&doc.Sandbox); err == nil && decode(data, &candidate, true) == nil && candidate.valid() {
			sandbox = candidate
		}
	}
	sandbox.clamp(host.Ceilings)
	return sandbox
}

func (cfg Sandbox) valid() bool {
	_, err := time.Parse("15:04", cfg.LibraryRequests.RebuildWindowLocal)
	return err == nil && cfg.BackupHourLocal >= 0 && cfg.BackupHourLocal < 24 && cfg.DiskWatermarkPercent <= 100 &&
		cfg.BackupsKeep >= 0 && cfg.TempVM.PoolSize >= 0 && cfg.PreVM.PoolSize >= 0 && cfg.DiskIndexEntries >= 0 && cfg.Global.QueueTimeoutSeconds >= 0 &&
		positive(cfg.ChatVM.MemMB, cfg.ChatVM.VCPUs, cfg.ChatVM.CPUQuotaPercent, cfg.ChatVM.DiskGB, cfg.ChatVM.DiskBwMBs, cfg.ChatVM.DiskIOPS, cfg.ChatVM.PidsMax,
			cfg.TempVM.MemMB, cfg.TempVM.VCPUs, cfg.TempVM.CPUQuotaPercent, cfg.TempVM.TmpDiskMB, cfg.PreVM.MemMB, cfg.PreVM.VCPUs, cfg.PreVM.CPUQuotaPercent, cfg.PreVM.ExecTimeoutMS,
			cfg.WarmSeconds, cfg.HibernateTTLHours, cfg.MaxSessionLifetimeMinutes, cfg.ExecTimeoutSeconds, cfg.ExecTimeoutMaxSeconds, cfg.BackgroundTimeoutMaxSeconds,
			cfg.MaxBackgroundJobsPerChat, cfg.MaxOutputBytes, cfg.MaxInputBytes, cfg.MaxExportBytes, cfg.MaxExportFiles, cfg.MaxFileListEntries, cfg.MaxTextViewBytes,
			cfg.PreviewMaxPx, cfg.DailyCPUSecondsPerChat, cfg.SnapshotQuotaMBPerChat, cfg.DiskWatermarkPercent, cfg.Global.MaxVMs, cfg.Global.MaxGuestMemMB, cfg.LibraryRequests.MaxPendingPerChat)
}

func (cfg *Sandbox) clamp(ceil Ceilings) {
	cfg.ChatVM.MemMB, cfg.TempVM.MemMB, cfg.PreVM.MemMB = min(cfg.ChatVM.MemMB, ceil.VMMemMB), min(cfg.TempVM.MemMB, ceil.VMMemMB), min(cfg.PreVM.MemMB, ceil.VMMemMB)
	cfg.ChatVM.VCPUs, cfg.TempVM.VCPUs, cfg.PreVM.VCPUs = min(cfg.ChatVM.VCPUs, ceil.VMVCPUs), min(cfg.TempVM.VCPUs, ceil.VMVCPUs), min(cfg.PreVM.VCPUs, ceil.VMVCPUs)
	cfg.ChatVM.CPUQuotaPercent, cfg.TempVM.CPUQuotaPercent, cfg.PreVM.CPUQuotaPercent = min(cfg.ChatVM.CPUQuotaPercent, cfg.ChatVM.VCPUs*100), min(cfg.TempVM.CPUQuotaPercent, cfg.TempVM.VCPUs*100), min(cfg.PreVM.CPUQuotaPercent, cfg.PreVM.VCPUs*100)
	cfg.ChatVM.DiskGB = min(cfg.ChatVM.DiskGB, ceil.DiskGBPerChat)
	cfg.TempVM.TmpDiskMB = min(cfg.TempVM.TmpDiskMB, ceil.VMMemMB)
	cfg.Global.MaxVMs, cfg.Global.MaxGuestMemMB = min(cfg.Global.MaxVMs, ceil.MaxVMs), min(cfg.Global.MaxGuestMemMB, ceil.MaxGuestMemMB)
	cfg.ExecTimeoutMaxSeconds = min(cfg.ExecTimeoutMaxSeconds, ceil.ExecTimeoutS, 86400)
	cfg.ExecTimeoutSeconds = min(cfg.ExecTimeoutSeconds, cfg.ExecTimeoutMaxSeconds)
	cfg.BackgroundTimeoutMaxSeconds = min(cfg.BackgroundTimeoutMaxSeconds, ceil.ExecTimeoutS, 86400)
	cfg.PreVM.ExecTimeoutMS = min(cfg.PreVM.ExecTimeoutMS, min(ceil.ExecTimeoutS, 86400)*1000)
	cfg.Global.QueueTimeoutSeconds = min(cfg.Global.QueueTimeoutSeconds, ceil.ExecTimeoutS, 86400)
	cfg.TempVM.PoolSize, cfg.PreVM.PoolSize = min(cfg.TempVM.PoolSize, cfg.Global.MaxVMs), min(cfg.PreVM.PoolSize, cfg.Global.MaxVMs)
	cfg.MaxInputBytes, cfg.MaxExportBytes = min(cfg.MaxInputBytes, ceil.MaxFrameBytes/4*3), min(cfg.MaxExportBytes, ceil.MaxFrameBytes/4*3)
	cfg.MaxOutputBytes, cfg.MaxTextViewBytes = min(cfg.MaxOutputBytes, ceil.MaxFrameBytes), min(cfg.MaxTextViewBytes, ceil.MaxFrameBytes)
	cfg.PreviewMaxPx = min(cfg.PreviewMaxPx, 8192)
	cfg.WarmSeconds, cfg.MaxSessionLifetimeMinutes, cfg.HibernateTTLHours = min(cfg.WarmSeconds, 31536000), min(cfg.MaxSessionLifetimeMinutes, 525600), min(cfg.HibernateTTLHours, 8760)
}
