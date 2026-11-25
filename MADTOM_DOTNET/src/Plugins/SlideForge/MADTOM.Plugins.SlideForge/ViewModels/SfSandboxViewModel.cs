using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed partial class SfSandboxVmRow : ObservableObject
{
    public long ChatId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _userLabel;

    [ObservableProperty]
    private string _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHot))]
    [NotifyPropertyChangedFor(nameof(IsCold))]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    [NotifyPropertyChangedFor(nameof(UptimeDisplay))]
    private string _state;

    [ObservableProperty]
    private string _imageVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesDisplay))]
    [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
    private long _memMb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourcesDisplay))]
    [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
    private int _vcpus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskDisplay))]
    [NotifyPropertyChangedFor(nameof(DiskUsageFraction))]
    private long _diskUsedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskDisplay))]
    [NotifyPropertyChangedFor(nameof(DiskUsageFraction))]
    private long _diskQuotaBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRunDisplay))]
    private string _lastUsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UptimeDisplay))]
    private long _uptimeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuDisplay))]
    [NotifyPropertyChangedFor(nameof(CpuFraction))]
    private double _cpuSecondsToday;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuDisplay))]
    [NotifyPropertyChangedFor(nameof(CpuFraction))]
    private double _cpuBudgetSeconds;

    [ObservableProperty]
    private string _cpuResetsAt;

    [ObservableProperty]
    private long _pid;

    [ObservableProperty]
    private string _since;

    [ObservableProperty]
    private int _backups;

    public SfSandboxVmRow(
        long ChatId,
        string UserLabel,
        string Kind,
        string State,
        string ImageVersion,
        long MemMb,
        int Vcpus,
        long DiskUsedBytes,
        long DiskQuotaBytes,
        string LastUsed,
        long UptimeSeconds = 0,
        double CpuSecondsToday = 0.0,
        double CpuBudgetSeconds = 0.0,
        string CpuResetsAt = "",
        long Pid = 0,
        string Since = "",
        int Backups = 0)
    {
        this.ChatId = ChatId;
        _userLabel = UserLabel;
        _kind = Kind;
        _state = State;
        _imageVersion = ImageVersion;
        _memMb = MemMb;
        _vcpus = Vcpus;
        _diskUsedBytes = DiskUsedBytes;
        _diskQuotaBytes = DiskQuotaBytes;
        _lastUsed = LastUsed;
        _uptimeSeconds = UptimeSeconds;
        _cpuSecondsToday = CpuSecondsToday;
        _cpuBudgetSeconds = CpuBudgetSeconds;
        _cpuResetsAt = CpuResetsAt;
        _pid = Pid;
        _since = Since;
        _backups = Backups;
    }

    public void UpdateFrom(SfSandboxVmRow other)
    {
        UserLabel = other.UserLabel;
        Kind = other.Kind;
        State = other.State;
        ImageVersion = other.ImageVersion;
        MemMb = other.MemMb;
        Vcpus = other.Vcpus;
        DiskUsedBytes = other.DiskUsedBytes;
        DiskQuotaBytes = other.DiskQuotaBytes;
        LastUsed = other.LastUsed;
        UptimeSeconds = other.UptimeSeconds;
        CpuSecondsToday = other.CpuSecondsToday;
        CpuBudgetSeconds = other.CpuBudgetSeconds;
        CpuResetsAt = other.CpuResetsAt;
        Pid = other.Pid;
        Since = other.Since;
        Backups = other.Backups;
    }

    public string DisplayLabel => !string.IsNullOrWhiteSpace(UserLabel)
        ? $"{UserLabel} ({ChatId})"
        : ChatId.ToString(CultureInfo.InvariantCulture);

    public bool IsHot => State is "booting" or "warm" or "busy" or "running";
    public bool IsCold => State is "hibernated" or "cold";
    public string StateBadge => string.IsNullOrWhiteSpace(State) ? "UNKNOWN" : State.ToUpperInvariant();

    public string UptimeDisplay => IsHot ? SfJsonHelpers.FormatDuration(UptimeSeconds) : "—";
    public string ResourcesDisplay => $"{MemMb} MiB / {Vcpus} vCPU";
    public string MemoryDisplay => $"{MemMb} MiB ({Vcpus} vCPU)";

    public string CpuDisplay => CpuBudgetSeconds > 0
        ? $"{CpuSecondsToday.ToString("0.#", CultureInfo.InvariantCulture)}s / {CpuBudgetSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s"
        : $"{CpuSecondsToday.ToString("0.#", CultureInfo.InvariantCulture)}s";

    public double CpuFraction => CpuBudgetSeconds > 0
        ? Math.Clamp(CpuSecondsToday / CpuBudgetSeconds, 0.0, 1.0)
        : 0.0;

    public string DiskDisplay => DiskQuotaBytes > 0
        ? $"{SfJsonHelpers.FormatBytes(DiskUsedBytes)} / {SfJsonHelpers.FormatBytes(DiskQuotaBytes)}"
        : SfJsonHelpers.FormatBytes(DiskUsedBytes);

    public double DiskUsageFraction => DiskQuotaBytes > 0
        ? Math.Clamp((double)DiskUsedBytes / DiskQuotaBytes, 0.0, 1.0)
        : 0.0;

    public string LastRunDisplay => SfJsonHelpers.FormatTimestamp(LastUsed);
}

public sealed record SfSandboxFileRow(
    string Path,
    string Type,
    long Size,
    long Mtime
)
{
    public bool IsDirectory => string.Equals(Type, "dir", StringComparison.OrdinalIgnoreCase);
    public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);
    public bool IsText => !IsDirectory && SfJsonHelpers.IsTextExt(Path);
    public bool IsPreview => !IsDirectory && SfJsonHelpers.IsSandboxPreviewExt(Path);

    public string Icon => Type switch
    {
        "dir" => "📁",
        "symlink" => "🔗",
        _ when IsText => "📄",
        _ when IsPreview => "🖼",
        _ => "📦"
    };

    public string SizeDisplay => IsDirectory ? "—" : SfJsonHelpers.FormatBytes(Size);
    public string MtimeDisplay => SfJsonHelpers.FormatEpoch(Mtime);
}

public sealed partial class SfSandboxViewModel : ObservableObject
{
    private SfRpc? _rpc;
    private int _refreshInFlight;
    private DateTime? _lastSuccessfulRefreshUtc;
    private bool _lastStatsIncludedImageStatus;
    private bool _isSyncingVms;
    private long? _lastSelectedChatId;

    public int AutoRefreshIntervalSeconds { get; set; } = 10;
    public Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

    [ObservableProperty] private SfSandboxVmRow? _selectedVm;
    [ObservableProperty] private string _sessionName = "main";
    [ObservableProperty] private bool _autoRefresh = true;

    // Host summary & caps
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunningVmsFraction))]
    [NotifyPropertyChangedFor(nameof(RunningVmsText))]
    private int _runningVms;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunningVmsFraction))]
    [NotifyPropertyChangedFor(nameof(RunningVmsText))]
    private int _maxVms;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestMemFraction))]
    [NotifyPropertyChangedFor(nameof(GuestMemText))]
    private long _guestMemMb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuestMemFraction))]
    [NotifyPropertyChangedFor(nameof(GuestMemText))]
    private long _maxGuestMemMb;

    [ObservableProperty] private long _totalDiskUsedBytes;
    [ObservableProperty] private double _totalCpuSecondsToday;
    [ObservableProperty] private double _stateDirUsedPercent;
    [ObservableProperty] private string _totalsSummary = "0 running";
    [ObservableProperty] private string _hostSummary = string.Empty;

    public double RunningVmsFraction => MaxVms > 0 ? Math.Clamp((double)RunningVms / MaxVms, 0.0, 1.0) : 0.0;
    public double GuestMemFraction => MaxGuestMemMb > 0 ? Math.Clamp((double)GuestMemMb / MaxGuestMemMb, 0.0, 1.0) : 0.0;
    public string RunningVmsText => $"{RunningVms} / {MaxVms} VMs";
    public string GuestMemText => $"{GuestMemMb} / {MaxGuestMemMb} MiB";

    // Selected VM file browser
    [ObservableProperty] private string _filesPath = "/data";
    [ObservableProperty] private string _filesAsOf = string.Empty;
    [ObservableProperty] private bool _filesFromCache;

    public ObservableCollection<SfSandboxFileRow> Files { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviewSelectedFile))]
    [NotifyPropertyChangedFor(nameof(IsTextViewerVisible))]
    [NotifyPropertyChangedFor(nameof(IsImageViewerVisible))]
    private SfSandboxFileRow? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextViewerVisible))]
    private string? _viewerText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageViewerVisible))]
    private Bitmap? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatusText))]
    private int _previewPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatusText))]
    [NotifyPropertyChangedFor(nameof(HasMultiplePages))]
    private int _previewPages;

    [ObservableProperty] private string _viewerNotice = string.Empty;

    public bool CanPreviewSelectedFile => SelectedFile is not null && !SelectedFile.IsDirectory;
    public bool IsTextViewerVisible => ViewerText is not null;
    public bool IsImageViewerVisible => PreviewImage is not null;
    public bool HasMultiplePages => PreviewPages > 1;
    public string PageStatusText => PreviewPages > 0 ? $"Page {PreviewPage} / {PreviewPages}" : $"Page {PreviewPage}";

    // Sandbox image status
    [ObservableProperty] private string _imageVersion = "—";
    [ObservableProperty] private bool _imageBuilding;
    [ObservableProperty] private string _imageStatusSummary = "Not loaded";
    [ObservableProperty] private string _imageLastBuildLog = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<SfSandboxVmRow> Vms { get; } = new();

    public bool HasSelectedVm => SelectedVm is not null;

    partial void OnSelectedVmChanged(SfSandboxVmRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedVm));
        long? newId = value?.ChatId;
        if (_isSyncingVms && newId is not null && newId == _lastSelectedChatId)
            return;

        if (newId is not null && newId == _lastSelectedChatId)
            return;

        _lastSelectedChatId = newId;
        if (value is null)
        {
            Files.Clear();
            SelectedFile = null;
            FilesAsOf = string.Empty;
            FilesFromCache = false;
        }
    }

    partial void OnSelectedFileChanged(SfSandboxFileRow? value)
    {
        ViewerText = null;
        PreviewImage = null;
        PreviewPage = 1;
        PreviewPages = 0;

        if (value is null)
        {
            ViewerNotice = string.Empty;
        }
        else if (value.IsDirectory)
        {
            ViewerNotice = "Directory selected. Click Open or double-click to browse.";
        }
        else
        {
            ViewerNotice = string.Empty;
        }
    }

    public SfSandboxViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
        _lastSuccessfulRefreshUtc = null;
    }

    [RelayCommand]
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshCoreAsync(silent: false, ct);
        if (!_lastStatsIncludedImageStatus)
            await LoadImageStatusAsync(ct);
    }

    public Task RefreshSilentAsync(CancellationToken ct = default)
    {
        if (_rpc is null || !AutoRefresh)
            return Task.CompletedTask;

        if (_lastSuccessfulRefreshUtc is DateTime last &&
            (Clock() - last).TotalSeconds < AutoRefreshIntervalSeconds)
            return Task.CompletedTask;

        return RefreshCoreAsync(silent: true, ct);
    }

    [RelayCommand]
    public Task LoadVmsAsync(CancellationToken ct = default) => RefreshCoreAsync(silent: false, ct);

    private async Task RefreshCoreAsync(bool silent, CancellationToken ct)
    {
        var rpc = _rpc;
        if (rpc is null)
        {
            if (!silent)
                ErrorMessage = "not_connected";
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        if (!silent)
            await RunOnUiAsync(() => IsBusy = true);

        try
        {
            JsonElement root;
            try
            {
                root = await rpc.CallAsync("sandbox_stats", null, ct);
            }
            catch (SfRpcException ex) when (ex.Code is "method_not_allowed" or "unknown_method")
            {
                root = await rpc.CallAsync("sandbox_list_vms", null, ct);
            }

            await RunOnUiAsync(() =>
            {
                ApplyVmStatsPayload(root);
                ErrorMessage = null;
            });
            _lastSuccessfulRefreshUtc = Clock();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ignore cancellation
        }
        catch (SfRpcException ex)
        {
            await RunOnUiAsync(() => ErrorMessage = ex.Code);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
            if (!silent)
                await RunOnUiAsync(() => IsBusy = false);
        }
    }

    private static async Task RunOnUiAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private void ApplyVmStatsPayload(JsonElement root)
    {
        double defaultCpuBudget = 0;
        long defaultDiskQuota = 0;
        if (root.TryGetProperty("caps", out var capsEl) && capsEl.ValueKind == JsonValueKind.Object)
        {
            defaultCpuBudget = SfJsonHelpers.GetDouble(capsEl, "daily_cpu_seconds_per_chat");
            long diskGb = SfJsonHelpers.GetLong(capsEl, "chat_vm_disk_gb");
            if (diskGb > 0) defaultDiskQuota = diskGb * 1024L * 1024L * 1024L;
        }

        var incoming = new List<SfSandboxVmRow>();
        var incomingIds = new HashSet<long>();
        if (root.TryGetProperty("vms", out var vmsEl) && vmsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vmsEl.EnumerateArray())
            {
                long chatId = SfJsonHelpers.GetLong(v, "chat_id");
                if (!incomingIds.Add(chatId))
                    continue;

                string label = SfJsonHelpers.GetStringOrNull(v, "user_label")
                    ?? SfJsonHelpers.GetStringOrNull(v, "chat_title")
                    ?? string.Empty;
                long diskQuota = SfJsonHelpers.GetLong(v, "disk_quota_bytes");
                if (diskQuota <= 0 && defaultDiskQuota > 0) diskQuota = defaultDiskQuota;
                double cpuBudget = SfJsonHelpers.GetDouble(v, "cpu_budget_seconds");
                if (cpuBudget <= 0 && defaultCpuBudget > 0) cpuBudget = defaultCpuBudget;

                incoming.Add(new SfSandboxVmRow(
                    ChatId: chatId,
                    UserLabel: label,
                    Kind: SfJsonHelpers.GetString(v, "kind"),
                    State: SfJsonHelpers.GetString(v, "state"),
                    ImageVersion: SfJsonHelpers.GetString(v, "image_version"),
                    MemMb: SfJsonHelpers.GetLong(v, "mem_mb"),
                    Vcpus: SfJsonHelpers.GetInt(v, "vcpus"),
                    DiskUsedBytes: SfJsonHelpers.GetLong(v, "disk_used_bytes"),
                    DiskQuotaBytes: diskQuota,
                    LastUsed: SfJsonHelpers.GetString(v, "last_used"),
                    UptimeSeconds: SfJsonHelpers.GetLong(v, "uptime_seconds"),
                    CpuSecondsToday: SfJsonHelpers.GetDouble(v, "cpu_seconds_today"),
                    CpuBudgetSeconds: cpuBudget,
                    CpuResetsAt: SfJsonHelpers.GetString(v, "cpu_resets_at"),
                    Pid: SfJsonHelpers.GetLong(v, "pid"),
                    Since: SfJsonHelpers.GetString(v, "since"),
                    Backups: SfJsonHelpers.GetInt(v, "backups")));
            }
        }

        long? prevId = SelectedVm?.ChatId;
        _isSyncingVms = true;
        try
        {
            // Remove vanished rows in place (never Clear)
            for (int i = Vms.Count - 1; i >= 0; i--)
            {
                if (!incomingIds.Contains(Vms[i].ChatId))
                    Vms.RemoveAt(i);
            }

            // Update existing rows in place, reorder if needed, and insert new rows at their target index
            for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
            {
                var next = incoming[targetIdx];
                int existingIdx = -1;
                for (int j = targetIdx; j < Vms.Count; j++)
                {
                    if (Vms[j].ChatId == next.ChatId)
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx >= 0)
                {
                    if (existingIdx != targetIdx)
                        Vms.Move(existingIdx, targetIdx);
                    Vms[targetIdx].UpdateFrom(next);
                }
                else
                {
                    Vms.Insert(targetIdx, next);
                }
            }

            if (prevId.HasValue)
            {
                var match = Vms.FirstOrDefault(vm => vm.ChatId == prevId.Value);
                if (match is not null)
                {
                    if (!ReferenceEquals(SelectedVm, match))
                        SelectedVm = match;
                }
                else
                {
                    _isSyncingVms = false;
                    SelectedVm = null;
                }
            }
        }
        finally
        {
            _isSyncingVms = false;
        }

        if (root.TryGetProperty("totals", out var totalsEl) && totalsEl.ValueKind == JsonValueKind.Object)
        {
            RunningVms = SfJsonHelpers.GetInt(totalsEl, "running");
            MaxVms = SfJsonHelpers.GetInt(totalsEl, "max_vms");
            GuestMemMb = SfJsonHelpers.GetLong(totalsEl, "guest_mem_mb");
            MaxGuestMemMb = SfJsonHelpers.GetLong(totalsEl, "max_guest_mem_mb");
            TotalDiskUsedBytes = SfJsonHelpers.GetLong(totalsEl, "total_disk_used_bytes");
            TotalCpuSecondsToday = SfJsonHelpers.GetDouble(totalsEl, "total_cpu_seconds_today");
            TotalsSummary = $"Running: {RunningVms}/{MaxVms} · Guest RAM: {GuestMemMb}/{MaxGuestMemMb} MiB";
        }

        if (root.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.Object)
        {
            StateDirUsedPercent = SfJsonHelpers.GetDouble(hostEl, "state_dir_used_percent");
            HostSummary = $"State disk: {StateDirUsedPercent:F1}%";
        }

        if ((root.TryGetProperty("image", out var imgEl) || root.TryGetProperty("image_status", out imgEl)) &&
            imgEl.ValueKind == JsonValueKind.Object)
        {
            ApplyImageStatusPayload(imgEl);
            _lastStatsIncludedImageStatus = true;
        }
        else
        {
            _lastStatsIncludedImageStatus = false;
        }
    }

    private void ApplyImageStatusPayload(JsonElement root)
    {
        ImageVersion = SfJsonHelpers.GetString(root, "current_version", "—");
        ImageBuilding = SfJsonHelpers.GetBool(root, "building");
        string buildStarted = SfJsonHelpers.GetString(root, "build_started");
        ImageStatusSummary = ImageBuilding
            ? $"Building since {buildStarted}"
            : $"Current version: {ImageVersion}";

        if (root.TryGetProperty("last_build", out var lb) && lb.ValueKind == JsonValueKind.Object)
        {
            string ver = SfJsonHelpers.GetString(lb, "version");
            bool ok = SfJsonHelpers.GetBool(lb, "ok");
            string fin = SfJsonHelpers.GetString(lb, "finished");
            string tail = SfJsonHelpers.GetString(lb, "log_tail");
            ImageLastBuildLog = $"Last build {ver} ({(ok ? "OK" : "FAILED")} at {fin})\n{tail}";
        }
        else
        {
            ImageLastBuildLog = SfJsonHelpers.PrettyPrint(root);
        }
    }

    // ------------------------------------------------------------------
    // Selected VM File Browser
    // ------------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshFilesAsync(CancellationToken ct = default)
    {
        if (SelectedVm is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            string targetPath = string.IsNullOrWhiteSpace(FilesPath) ? "/data" : FilesPath.Trim();
            var root = await _rpc.CallAsync("sandbox_list_files", new
            {
                chat_id = SelectedVm.ChatId,
                path = targetPath,
                depth = 2,
                refresh = true,
            }, ct, timeoutMs: 30000);

            string? prevPath = SelectedFile?.Path;
            Files.Clear();
            if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entriesEl.EnumerateArray())
                {
                    Files.Add(new SfSandboxFileRow(
                        Path: SfJsonHelpers.GetString(entry, "path"),
                        Type: SfJsonHelpers.GetString(entry, "type"),
                        Size: SfJsonHelpers.GetLong(entry, "size"),
                        Mtime: SfJsonHelpers.GetLong(entry, "mtime")));
                }
            }

            FilesAsOf = SfJsonHelpers.FormatTimestamp(SfJsonHelpers.GetStringOrNull(root, "as_of"));
            FilesFromCache = SfJsonHelpers.GetBool(root, "from_cache");
            if (prevPath is not null)
            {
                SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Path, prevPath, StringComparison.Ordinal));
            }
            StatusMessage = $"Loaded {Files.Count} entr{(Files.Count == 1 ? "y" : "ies")} from {targetPath}.";
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task NavigateUpAsync(CancellationToken ct = default)
    {
        string current = (FilesPath ?? "/data").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(current) || string.Equals(current, "/data", StringComparison.Ordinal))
        {
            FilesPath = "/data";
        }
        else
        {
            int idx = current.LastIndexOf('/');
            string parent = idx > 0 ? current[..idx] : "/data";
            FilesPath = parent.StartsWith("/data", StringComparison.Ordinal) ? parent : "/data";
        }
        await RefreshFilesAsync(ct);
    }

    [RelayCommand]
    public async Task OpenFileAsync(SfSandboxFileRow? file = null, CancellationToken ct = default)
    {
        var target = file ?? SelectedFile;
        if (target is null || SelectedVm is null || _rpc is null) return;
        if (!ReferenceEquals(SelectedFile, target))
            SelectedFile = target;

        if (target.IsDirectory)
        {
            FilesPath = target.Path;
            await RefreshFilesAsync(ct);
            return;
        }

        if (target.IsPreview)
        {
            await PreviewFileAsync(target, ct);
            return;
        }

        await ReadTextFileAsync(target, ct);
    }

    private async Task ReadTextFileAsync(SfSandboxFileRow target, CancellationToken ct)
    {
        if (SelectedVm is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("sandbox_read_text", new
            {
                chat_id = SelectedVm.ChatId,
                path = target.Path,
            }, ct, timeoutMs: 30000);

            PreviewImage = null;
            bool isBinary = SfJsonHelpers.GetBool(root, "binary");
            bool truncated = SfJsonHelpers.GetBool(root, "truncated");
            long size = SfJsonHelpers.GetLong(root, "size", target.Size);
            if (isBinary)
            {
                ViewerText = null;
                ViewerNotice = $"Binary file ({SfJsonHelpers.FormatBytes(size)})";
            }
            else
            {
                ViewerText = SfJsonHelpers.GetString(root, "text");
                ViewerNotice = truncated
                    ? $"Truncated ({SfJsonHelpers.FormatBytes(size)})"
                    : SfJsonHelpers.FormatBytes(size);
            }
            StatusMessage = $"Read {target.Path} ({SfJsonHelpers.FormatBytes(size)}).";
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ViewerText = null;
            ViewerNotice = ex.Code;
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            ViewerText = null;
            ViewerNotice = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task PreviewFileAsync(SfSandboxFileRow? file = null, CancellationToken ct = default)
    {
        var target = file ?? SelectedFile;
        if (target is null || SelectedVm is null || _rpc is null) return;
        if (!ReferenceEquals(SelectedFile, target))
            SelectedFile = target;

        if (target.IsDirectory)
        {
            PreviewImage = null;
            ViewerText = null;
            ViewerNotice = "Directory selected. Click Open or double-click to browse.";
            return;
        }

        if (!target.IsPreview)
        {
            await ReadTextFileAsync(target, ct);
            return;
        }

        int page = PreviewPage > 0 ? PreviewPage : 1;
        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("sandbox_preview", new
            {
                chat_id = SelectedVm.ChatId,
                path = target.Path,
                page,
            }, ct, timeoutMs: 30000);

            string b64 = SfJsonHelpers.GetString(root, "png_b64");
            var bitmap = SfJsonHelpers.DecodePreviewBitmap(b64, out string? decodeError);
            if (decodeError is not null)
            {
                PreviewImage = null;
                ViewerNotice = decodeError;
                ErrorMessage = decodeError;
                return;
            }

            int width = SfJsonHelpers.GetInt(root, "width");
            int height = SfJsonHelpers.GetInt(root, "height");
            int pages = SfJsonHelpers.GetInt(root, "pages", 1);

            ViewerText = null;
            PreviewImage = bitmap;
            PreviewPage = page;
            PreviewPages = Math.Max(1, pages);
            ViewerNotice = PreviewPages > 1
                ? $"Page {PreviewPage} of {PreviewPages} ({width}×{height})"
                : (width > 0 && height > 0 ? $"{width}×{height}" : string.Empty);
            StatusMessage = $"Previewed {target.Path} (page {PreviewPage}/{PreviewPages}).";
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            PreviewImage = null;
            if (string.Equals(ex.Code, "not_supported", StringComparison.OrdinalIgnoreCase))
            {
                ViewerNotice = "No page preview available for this file.";
                ErrorMessage = null;
            }
            else
            {
                ViewerNotice = ex.Code;
                ErrorMessage = ex.Code;
            }
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            ViewerNotice = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task NextPageAsync(CancellationToken ct = default)
    {
        if (PreviewPages > 0 && PreviewPage >= PreviewPages) return;
        PreviewPage = Math.Max(1, PreviewPage) + 1;
        await PreviewFileAsync(SelectedFile, ct);
    }

    [RelayCommand]
    public async Task PrevPageAsync(CancellationToken ct = default)
    {
        if (PreviewPage <= 1) return;
        PreviewPage--;
        await PreviewFileAsync(SelectedFile, ct);
    }

    // ------------------------------------------------------------------
    // VM Lifecycle Actions
    // ------------------------------------------------------------------

    public async Task RunVmActionAsync(string action, CancellationToken ct = default)
    {
        if (SelectedVm is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = SelectedVm.ChatId,
                ["action"] = action,
                ["session"] = string.IsNullOrWhiteSpace(SessionName) ? "main" : SessionName.Trim(),
            };
            await _rpc.CallAsync("sandbox_vm_action", payload, ct, timeoutMs: 60000);
            StatusMessage = $"Action '{action}' completed for {SelectedVm.DisplayLabel}.";
            ErrorMessage = null;
            await LoadVmsAsync(ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] public Task HibernateVmAsync() => RunVmActionAsync("hibernate");
    [RelayCommand] public Task KillVmAsync() => RunVmActionAsync("kill");
    [RelayCommand] public Task RestartVmAsync() => RunVmActionAsync("restart");
    [RelayCommand] public Task WipeVmAsync() => RunVmActionAsync("wipe");
    [RelayCommand] public Task ResetSessionAsync() => RunVmActionAsync("reset_session");

    [RelayCommand]
    public async Task LoadImageStatusAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("sandbox_image_status", null, ct);
            await RunOnUiAsync(() =>
            {
                ApplyImageStatusPayload(root);
                ErrorMessage = null;
            });
        }
        catch (SfRpcException ex)
        {
            await RunOnUiAsync(() => ErrorMessage = ex.Code);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            await RunOnUiAsync(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task RebuildImageAsync(CancellationToken ct = default)
    {
        if (_rpc is null) return;
        IsBusy = true;
        try
        {
            await _rpc.CallAsync("sandbox_rebuild_image", null, ct);
            StatusMessage = "Sandbox image rebuild triggered.";
            ErrorMessage = null;
            await LoadImageStatusAsync(ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
