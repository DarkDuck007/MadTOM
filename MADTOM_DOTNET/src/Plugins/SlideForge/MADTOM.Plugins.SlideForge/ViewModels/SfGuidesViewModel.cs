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
    private SfRpc? _rpc;
    private string _savedText = string.Empty;

    [ObservableProperty] private SfGuideFile? _selectedFile;
    [ObservableProperty] private string? _openPath;
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private SfTip? _selectedTip;
    [ObservableProperty] private string _tipIdInput = string.Empty;
    [ObservableProperty] private string _tipTextEnInput = string.Empty;
    [ObservableProperty] private string _tipWhenJsonInput = "{}";
    [ObservableProperty] private int _tipPriorityInput = 5;

    [ObservableProperty] private string _previewSpecJson = "{\n  \"title\": \"Sample Deck\",\n  \"output_format\": \"pptx\",\n  \"language\": \"en\"\n}";
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
        if (value is null) return;
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
            var root = await _rpc.CallAsync("list_guides", null, ct);
            Files.Clear();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    Files.Add(new SfGuideFile(
                        SfJsonHelpers.GetString(item, "path"),
                        SfJsonHelpers.GetLong(item, "bytes")));
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
            var root = await _rpc.CallAsync("list_tips", null, ct);
            Tips.Clear();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string whenJson = item.TryGetProperty("when", out var whenEl) && whenEl.ValueKind != JsonValueKind.Undefined
                        ? whenEl.GetRawText()
                        : "{}";
                    Tips.Add(new SfTip
                    {
                        Id = SfJsonHelpers.GetString(item, "id"),
                        TextEn = SfJsonHelpers.GetString(item, "text_en"),
                        WhenJson = whenJson,
                        Priority = SfJsonHelpers.GetInt(item, "priority"),
                    });
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
