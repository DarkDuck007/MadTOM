using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfSandboxVmRow(
    long ChatId,
    string UserLabel,
    string Kind,
    string State,
    string ImageVersion,
    long MemMb,
    int Vcpus,
    long DiskUsedBytes,
    long DiskQuotaBytes,
    string LastUsed
)
{
    public string DisplayLabel => !string.IsNullOrWhiteSpace(UserLabel)
        ? $"{UserLabel} ({ChatId})"
        : ChatId.ToString(CultureInfo.InvariantCulture);

    public string ResourcesDisplay => $"{MemMb} MiB / {Vcpus} vCPU";
    public string DiskDisplay => DiskQuotaBytes > 0
        ? $"{SfJsonHelpers.FormatBytes(DiskUsedBytes)} / {SfJsonHelpers.FormatBytes(DiskQuotaBytes)}"
        : SfJsonHelpers.FormatBytes(DiskUsedBytes);
}

public sealed partial class SfSandboxViewModel : ObservableObject
{
    private SfRpc? _rpc;

    [ObservableProperty] private SfSandboxVmRow? _selectedVm;
    [ObservableProperty] private string _sessionName = "main";
    [ObservableProperty] private string _totalsSummary = "0 running";
    [ObservableProperty] private string _hostSummary = string.Empty;

    [ObservableProperty] private string _imageVersion = "—";
    [ObservableProperty] private bool _imageBuilding;
    [ObservableProperty] private string _imageStatusSummary = "Not loaded";
    [ObservableProperty] private string _imageLastBuildLog = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<SfSandboxVmRow> Vms { get; } = new();

    public bool HasSelectedVm => SelectedVm is not null;

    partial void OnSelectedVmChanged(SfSandboxVmRow? value) => OnPropertyChanged(nameof(HasSelectedVm));

    public SfSandboxViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc) => _rpc = rpc;

    [RelayCommand]
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await LoadVmsAsync(ct);
        await LoadImageStatusAsync(ct);
    }

    [RelayCommand]
    public async Task LoadVmsAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("sandbox_list_vms", null, ct);
            long? prevId = SelectedVm?.ChatId;
            Vms.Clear();

            if (root.TryGetProperty("vms", out var vmsEl) && vmsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vmsEl.EnumerateArray())
                {
                    long chatId = SfJsonHelpers.GetLong(v, "chat_id");
                    string label = SfJsonHelpers.GetStringOrNull(v, "user_label")
                        ?? SfJsonHelpers.GetStringOrNull(v, "chat_title")
                        ?? string.Empty;
                    Vms.Add(new SfSandboxVmRow(
                        ChatId: chatId,
                        UserLabel: label,
                        Kind: SfJsonHelpers.GetString(v, "kind"),
                        State: SfJsonHelpers.GetString(v, "state"),
                        ImageVersion: SfJsonHelpers.GetString(v, "image_version"),
                        MemMb: SfJsonHelpers.GetLong(v, "mem_mb"),
                        Vcpus: SfJsonHelpers.GetInt(v, "vcpus"),
                        DiskUsedBytes: SfJsonHelpers.GetLong(v, "disk_used_bytes"),
                        DiskQuotaBytes: SfJsonHelpers.GetLong(v, "disk_quota_bytes"),
                        LastUsed: SfJsonHelpers.GetString(v, "last_used")));
                }
            }

            if (root.TryGetProperty("totals", out var totalsEl) && totalsEl.ValueKind == JsonValueKind.Object)
            {
                int running = SfJsonHelpers.GetInt(totalsEl, "running");
                int maxVms = SfJsonHelpers.GetInt(totalsEl, "max_vms");
                long mem = SfJsonHelpers.GetLong(totalsEl, "guest_mem_mb");
                long maxMem = SfJsonHelpers.GetLong(totalsEl, "max_guest_mem_mb");
                TotalsSummary = $"Running: {running}/{maxVms} · Guest RAM: {mem}/{maxMem} MiB";
            }

            if (root.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.Object)
            {
                double pct = SfJsonHelpers.GetDouble(hostEl, "state_dir_used_percent");
                HostSummary = $"State disk: {pct:F1}%";
            }

            if (prevId.HasValue)
            {
                foreach (var vm in Vms)
                {
                    if (vm.ChatId == prevId.Value)
                    {
                        SelectedVm = vm;
                        break;
                    }
                }
            }

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
