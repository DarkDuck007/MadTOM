using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfGuideFile(string Path, long Bytes)
{
    public string SizeDisplay => SfJsonHelpers.FormatBytes(Bytes);
}

public sealed class SfTip
{
    public string Id { get; set; } = string.Empty;
    public string TextEn { get; set; } = string.Empty;
    public string WhenJson { get; set; } = "{}";
    public int Priority { get; set; }
}

public sealed partial class SfGuidesViewModel : ObservableObject
{
    public const string DefaultPreviewSpecJson = """
{
  "origin": "freestyle",
  "mode": "generate",
  "language": "en",
  "output_format": "pptx",
  "title": "Example deck",
  "prompt": "A short deck about solar energy",
  "outline": null,
  "extra": null,
  "pages_min": null,
  "pages_max": null,
  "ai_images": false,
  "web": true,
  "inputs": [],
  "parent_job_id": null
}
""";

    public const string PreviewHint =
        "Prompt preview: renders the exact brief the studio would receive for a job spec, using the live guides, fragments and tips. Edit the JSON, or load the spec of an existing job, then press Render.";

    private const string BadRequestPreviewMessage =
        "The spec is not a valid job spec (needs origin, mode, language, output_format; see the template). Use Reset or Load from job.";

    private SfRpc? _rpc;
    private string _savedText = string.Empty;
    private int _refreshInFlight;
    private bool _suppressTipFormSync;

    [ObservableProperty] private SfGuideFile? _selectedFile;
    [ObservableProperty] private string? _openPath;
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private SfTip? _selectedTip;
    [ObservableProperty] private string _tipIdInput = string.Empty;
    [ObservableProperty] private string _tipTextEnInput = string.Empty;
    [ObservableProperty] private string _tipWhenJsonInput = "{}";
    [ObservableProperty] private int _tipPriorityInput = 5;

    [ObservableProperty] private string _previewSpecJson = DefaultPreviewSpecJson;
    [ObservableProperty] private string _previewJobIdInput = string.Empty;
    [ObservableProperty] private string _promptPreview = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<SfGuideFile> Files { get; } = new();
    public ObservableCollection<SfTip> Tips { get; } = new();

    partial void OnEditorTextChanged(string value)
    {
        IsDirty = OpenPath is not null && !string.Equals(value, _savedText, StringComparison.Ordinal);
    }

    partial void OnSelectedTipChanged(SfTip? value)
    {
        if (value is null || _suppressTipFormSync) return;
        TipIdInput = value.Id;
        TipTextEnInput = value.TextEn;
        TipWhenJsonInput = value.WhenJson;
        TipPriorityInput = value.Priority;
    }

    public SfGuidesViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            await LoadGuidesCoreAsync(ct);
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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

    public async Task RefreshSilentAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        try
        {
            await LoadGuidesCoreAsync(ct);
            await LoadTipsCoreAsync(suppressFormSync: true, ct);
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private async Task LoadGuidesCoreAsync(CancellationToken ct)
    {
        var root = await _rpc!.CallAsync("list_guides", null, ct);
        var incoming = new List<SfGuideFile>();
        var incomingByPath = new Dictionary<string, SfGuideFile>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var file = new SfGuideFile(
                    SfJsonHelpers.GetString(item, "path"),
                    SfJsonHelpers.GetLong(item, "bytes"));
                incoming.Add(file);
                incomingByPath[file.Path] = file;
            }
        }

        string? prevPath = SelectedFile?.Path;

        for (int i = Files.Count - 1; i >= 0; i--)
        {
            if (!incomingByPath.ContainsKey(Files[i].Path))
                Files.RemoveAt(i);
        }

        var existingPaths = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Files.Count; i++)
        {
            var existing = Files[i];
            existingPaths.Add(existing.Path);
            var updated = incomingByPath[existing.Path];
            if (existing != updated)
                Files[i] = updated;
        }

        foreach (var file in incoming)
        {
            if (existingPaths.Add(file.Path))
                Files.Add(file);
        }

        if (prevPath is not null)
        {
            SfGuideFile? match = null;
            foreach (var f in Files)
            {
                if (string.Equals(f.Path, prevPath, StringComparison.Ordinal))
                {
                    match = f;
                    break;
                }
            }

            if (match is not null)
            {
                if (!ReferenceEquals(SelectedFile, match))
                    SelectedFile = match;
            }
            else
            {
                SelectedFile = null;
            }
        }
    }

    private async Task LoadTipsCoreAsync(bool suppressFormSync, CancellationToken ct)
    {
        var root = await _rpc!.CallAsync("list_tips", null, ct);
        var incoming = new List<SfTip>();
        var incomingById = new Dictionary<string, SfTip>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                string whenJson = item.TryGetProperty("when", out var whenEl) && whenEl.ValueKind != JsonValueKind.Undefined
                    ? whenEl.GetRawText()
                    : "{}";
                var tip = new SfTip
                {
                    Id = SfJsonHelpers.GetString(item, "id"),
                    TextEn = SfJsonHelpers.GetString(item, "text_en"),
                    WhenJson = whenJson,
                    Priority = SfJsonHelpers.GetInt(item, "priority"),
                };
                incoming.Add(tip);
                incomingById[tip.Id] = tip;
            }
        }

        string? prevTipId = SelectedTip?.Id;

        for (int i = Tips.Count - 1; i >= 0; i--)
        {
            if (!incomingById.ContainsKey(Tips[i].Id))
                Tips.RemoveAt(i);
        }

        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Tips.Count; i++)
        {
            var existing = Tips[i];
            existingIds.Add(existing.Id);
            var updated = incomingById[existing.Id];
            if (!TipsEqual(existing, updated))
                Tips[i] = updated;
        }

        foreach (var tip in incoming)
        {
            if (existingIds.Add(tip.Id))
                Tips.Add(tip);
        }

        if (prevTipId is not null)
        {
            SfTip? match = null;
            foreach (var t in Tips)
            {
                if (string.Equals(t.Id, prevTipId, StringComparison.Ordinal))
                {
                    match = t;
                    break;
                }
            }

            bool prevSuppress = _suppressTipFormSync;
            if (suppressFormSync)
                _suppressTipFormSync = true;
            try
            {
                if (match is not null)
                {
                    if (!ReferenceEquals(SelectedTip, match))
                        SelectedTip = match;
                }
                else
                {
                    SelectedTip = null;
                }
            }
            finally
            {
                _suppressTipFormSync = prevSuppress;
            }
        }
    }

    private static bool TipsEqual(SfTip a, SfTip b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
        string.Equals(a.TextEn, b.TextEn, StringComparison.Ordinal) &&
        string.Equals(a.WhenJson, b.WhenJson, StringComparison.Ordinal) &&
        a.Priority == b.Priority;

    public async Task OpenAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("get_guide", new { path }, ct);
            string content = SfJsonHelpers.GetString(root, "content");
            OpenPath = SfJsonHelpers.GetString(root, "path", path);
            _savedText = content;
            EditorText = content;
            IsDirty = false;
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
    public async Task OpenSelectedFileAsync()
    {
        if (SelectedFile is not null)
            await OpenAsync(SelectedFile.Path);
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(OpenPath)) return;
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            await _rpc.CallAsync("put_guide", new { path = OpenPath, content = EditorText }, ct);
            _savedText = EditorText;
            IsDirty = false;
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
    public async Task LoadTipsAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            await LoadTipsCoreAsync(suppressFormSync: false, ct);
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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

    public async Task UpsertTipAsync(SfTip tip, CancellationToken ct = default)
    {
        if (tip is null) return;

        JsonObject whenObj;
        try
        {
            var parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(tip.WhenJson) ? "{}" : tip.WhenJson);
            if (parsed is not JsonObject obj)
            {
                ErrorMessage = "invalid_when_json: must be a JSON object";
                return;
            }
            whenObj = obj;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"invalid_when_json: {ex.Message}";
            return;
        }

        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var tipObj = new Dictionary<string, object?>
            {
                ["id"] = tip.Id,
                ["text_en"] = tip.TextEn,
                ["when"] = whenObj,
                ["priority"] = tip.Priority,
            };

            await _rpc.CallAsync("upsert_tip", new Dictionary<string, object?> { ["tip"] = tipObj }, ct);
            ErrorMessage = null;
            await LoadTipsAsync(ct);
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

    public async Task DeleteTipAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            await _rpc.CallAsync("delete_tip", new { id }, ct);
            ErrorMessage = null;
            await LoadTipsAsync(ct);
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
    public async Task SaveTipFormAsync()
    {
        if (string.IsNullOrWhiteSpace(TipIdInput))
        {
            ErrorMessage = "invalid_tip: id is required";
            return;
        }

        await UpsertTipAsync(new SfTip
        {
            Id = TipIdInput.Trim(),
            TextEn = TipTextEnInput,
            WhenJson = TipWhenJsonInput,
            Priority = TipPriorityInput,
        });
    }

    [RelayCommand]
    public async Task DeleteSelectedTipAsync()
    {
        if (SelectedTip is not null)
            await DeleteTipAsync(SelectedTip.Id);
    }

    [RelayCommand]
    public void NewTipForm()
    {
        SelectedTip = null;
        TipIdInput = string.Empty;
        TipTextEnInput = string.Empty;
        TipWhenJsonInput = "{}";
        TipPriorityInput = 5;
    }

    [RelayCommand]
    public void ResetPreviewSpec()
    {
        PreviewSpecJson = DefaultPreviewSpecJson;
        ErrorMessage = null;
    }

    [RelayCommand]
    public async Task LoadSpecFromJobAsync(string? jobId = null, CancellationToken ct = default)
    {
        string? targetId = string.IsNullOrWhiteSpace(jobId) ? PreviewJobIdInput : jobId;
        targetId = targetId?.Trim();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            ErrorMessage = "Enter a job id to load its spec.";
            return;
        }

        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("get_job", new Dictionary<string, object?> { ["job_id"] = targetId }, ct);
            if (root.TryGetProperty("spec", out var specEl) && specEl.ValueKind == JsonValueKind.Object)
            {
                PreviewSpecJson = SfJsonHelpers.PrettyPrint(specEl);
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = "Job response did not contain a spec object.";
            }
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
    public async Task PreviewPromptAsync(CancellationToken ct = default)
    {
        JsonNode? specNode;
        try
        {
            specNode = JsonNode.Parse(string.IsNullOrWhiteSpace(PreviewSpecJson) ? "{}" : PreviewSpecJson);
            if (specNode is not JsonObject)
            {
                ErrorMessage = "invalid_spec_json: must be a JSON object";
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"invalid_spec_json: {ex.Message}";
            return;
        }

        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("preview_prompt", new Dictionary<string, object?> { ["spec"] = specNode }, ct);
            PromptPreview = SfJsonHelpers.GetString(root, "prompt");
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = string.Equals(ex.Code, "bad_request", StringComparison.OrdinalIgnoreCase)
                ? BadRequestPreviewMessage
                : ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
