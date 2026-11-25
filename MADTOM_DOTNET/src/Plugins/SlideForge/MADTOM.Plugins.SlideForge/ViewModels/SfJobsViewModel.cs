using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed partial class SfJobRow : ObservableObject
{
    public string JobId { get; init; } = string.Empty;
    public long UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    public string OutputFormat { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public string? ErrorCode { get; init; }
    public int Charged { get; init; }

    public string UserDisplay => !string.IsNullOrWhiteSpace(Username) ? Username : UserId.ToString(CultureInfo.InvariantCulture);
}

public sealed record SfJobEventItem(string Type, string At, string Summary);

public sealed record SfJobQuestionItem(string Text, IReadOnlyList<string> Options, string? Answer, string? AnswerSource)
{
    public string OptionsDisplay => Options.Count > 0 ? string.Join(" · ", Options) : "—";
    public string AnswerDisplay => string.IsNullOrWhiteSpace(Answer)
        ? "(awaiting answer)"
        : (string.IsNullOrWhiteSpace(AnswerSource) ? Answer : $"{Answer} ({AnswerSource})");
}

public sealed record SfJobFileItem(
    long? Id,
    string Path,
    string Name,
    string Kind,
    long Bytes,
    string Role,
    long Mtime = 0)
{
    public string SizeDisplay => SfJsonHelpers.FormatBytes(Bytes);
    public string MtimeDisplay => SfJsonHelpers.FormatEpoch(Mtime);
    public string RoleBadge => string.IsNullOrWhiteSpace(Role) ? "FILE" : Role.ToUpperInvariant();
    public bool IsPreviewable => SfJsonHelpers.IsJobPreviewExt(string.IsNullOrWhiteSpace(Path) ? Name : Path, Kind);
    public bool IsText => !IsPreviewable && (
        string.Equals(Kind, "text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, "md", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, "json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, "csv", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, "tsv", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, "html", StringComparison.OrdinalIgnoreCase) ||
        SfJsonHelpers.IsTextExt(string.IsNullOrWhiteSpace(Path) ? Name : Path));

    public string Icon => IsPreviewable
        ? (string.Equals(Kind, "pdf", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "📕" : "🖼")
        : IsText ? "📄" : "📦";
}

public sealed class SfJobDetail
{
    public string JobId { get; init; } = string.Empty;
    public string SpecJson { get; init; } = "{}";
    public ObservableCollection<SfJobEventItem> Events { get; } = new();
    public ObservableCollection<SfJobQuestionItem> Questions { get; } = new();
    public ObservableCollection<SfJobFileItem> Files { get; } = new();
}

public sealed partial class SfJobsViewModel : ObservableObject
{
    public const string AllStatuses = "all";

    private SfRpc? _rpc;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _offset;
    [ObservableProperty] private string _statusFilter = AllStatuses;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SfJobRow? _selectedJob;
    [ObservableProperty] private SfJobDetail? _detail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    // Inline file viewer state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(CanPreviewSelectedFile))]
    private SfJobFileItem? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileTextVisible))]
    private string? _fileViewerText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileImageVisible))]
    private Bitmap? _filePreviewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePageStatusText))]
    private int _filePreviewPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePageStatusText))]
    [NotifyPropertyChangedFor(nameof(HasMultipleFilePages))]
    private int _filePreviewPages;

    [ObservableProperty] private string _fileViewerNotice = string.Empty;

    public ObservableCollection<SfJobRow> Jobs { get; } = new();

    public IReadOnlyList<string> StatusOptions { get; } = new[]
    {
        AllStatuses, "queued", "running", "waiting_input", "completed", "failed", "cancelled"
    };

    public bool HasDetail => Detail is not null;
    public bool HasSelectedFile => SelectedFile is not null;
    public bool CanPreviewSelectedFile => SelectedFile is not null && SelectedFile.IsPreviewable;
    public bool IsFileTextVisible => FileViewerText is not null;
    public bool IsFileImageVisible => FilePreviewImage is not null;
    public bool HasMultipleFilePages => FilePreviewPages > 1;
    public string FilePageStatusText => FilePreviewPages > 0
        ? $"Page {FilePreviewPage} / {FilePreviewPages}"
        : $"Page {FilePreviewPage}";

    public string PageSummary => TotalCount == 0
        ? "0 jobs"
        : $"{Offset + 1}–{Math.Min(Offset + Jobs.Count, TotalCount)} of {TotalCount}";

    partial void OnStatusFilterChanged(string value) => Offset = 0;
    partial void OnSearchTextChanged(string value) => Offset = 0;
    partial void OnDetailChanged(SfJobDetail? value)
    {
        OnPropertyChanged(nameof(HasDetail));
        ClearFileViewer();
    }
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnOffsetChanged(int value) => OnPropertyChanged(nameof(PageSummary));

    partial void OnSelectedFileChanged(SfJobFileItem? value)
    {
        FileViewerText = null;
        FilePreviewImage = null;
        FilePreviewPage = 1;
        FilePreviewPages = 0;
        FileViewerNotice = value is null
            ? string.Empty
            : (!value.IsPreviewable && !value.IsText ? "Unknown file type — press Preview / Read to try opening it." : string.Empty);
    }

    public SfJobsViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    private void ClearFileViewer()
    {
        SelectedFile = null;
        FileViewerText = null;
        FilePreviewImage = null;
        FilePreviewPage = 1;
        FilePreviewPages = 0;
        FileViewerNotice = string.Empty;
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
            var payload = new Dictionary<string, object?>
            {
                ["limit"] = PageSize,
                ["offset"] = Offset,
            };

            if (!string.IsNullOrWhiteSpace(StatusFilter) &&
                !string.Equals(StatusFilter, AllStatuses, StringComparison.OrdinalIgnoreCase))
            {
                payload["status"] = StatusFilter;
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                payload["query"] = SearchText.Trim();
            }

            var root = await _rpc.CallAsync("list_jobs", payload, ct);

            TotalCount = SfJsonHelpers.GetInt(root, "total");
            Jobs.Clear();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    Jobs.Add(ParseJobRow(item));
                }
            }

            ErrorMessage = null;
            OnPropertyChanged(nameof(PageSummary));
        }
        catch (SfRpcException ex)
        {
            Jobs.Clear();
            TotalCount = 0;
            ErrorMessage = ex.Code;
            OnPropertyChanged(nameof(PageSummary));
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
    public async Task NextPageAsync(CancellationToken ct = default)
    {
        if (Offset + PageSize >= TotalCount) return;
        Offset += PageSize;
        await LoadAsync(ct);
    }

    [RelayCommand]
    public async Task PrevPageAsync(CancellationToken ct = default)
    {
        if (Offset <= 0) return;
        Offset = Math.Max(0, Offset - PageSize);
        await LoadAsync(ct);
    }

    [RelayCommand]
    public async Task SelectAsync(SfJobRow? row, CancellationToken ct = default)
    {
        SelectedJob = row;
        if (row is null)
        {
            Detail = null;
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
            var root = await _rpc.CallAsync("get_job", new { job_id = row.JobId }, ct);
            var detail = ParseJobDetail(row.JobId, root);
            Detail = detail;
            await TryPopulateFilesFromListRpcAsync(detail, ct);
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
    public async Task LoadJobFilesAsync(CancellationToken ct = default)
    {
        if (Detail is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("list_job_files", new { job_id = Detail.JobId }, ct);
            ApplyListedFiles(Detail, root);
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

    private async Task TryPopulateFilesFromListRpcAsync(SfJobDetail detail, CancellationToken ct)
    {
        if (_rpc is null) return;
        try
        {
            var root = await _rpc.CallAsync("list_job_files", new { job_id = detail.JobId }, ct);
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                ApplyListedFiles(detail, root);
            }
        }
        catch (SfRpcException)
        {
            // Keep files from get_job if older probe/fake does not implement list_job_files.
        }
    }

    private static void ApplyListedFiles(SfJobDetail detail, JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;
        detail.Files.Clear();
        foreach (var f in items.EnumerateArray())
        {
            detail.Files.Add(ParseJobFileItem(f));
        }
    }

    [RelayCommand]
    public async Task InspectFileAsync(SfJobFileItem? file = null, CancellationToken ct = default)
    {
        var target = file ?? SelectedFile;
        if (target is null || Detail is null || _rpc is null) return;
        if (!ReferenceEquals(SelectedFile, target))
        {
            SelectedFile = target;
        }

        if (target.IsPreviewable)
        {
            await LoadFilePreviewAsync(target, FilePreviewPage > 0 ? FilePreviewPage : 1, ct);
        }
        else if (target.IsText)
        {
            await LoadFileTextAsync(target, ct);
        }
        else
        {
            // Unknown type: let the server decide (it reports binary content, which we show as a notice).
            await LoadFileTextAsync(target, ct);
        }
    }

    [RelayCommand]
    public async Task NextFilePageAsync(CancellationToken ct = default)
    {
        if (SelectedFile is null || !SelectedFile.IsPreviewable) return;
        if (FilePreviewPages > 0 && FilePreviewPage >= FilePreviewPages) return;
        FilePreviewPage = Math.Max(1, FilePreviewPage) + 1;
        await LoadFilePreviewAsync(SelectedFile, FilePreviewPage, ct);
    }

    [RelayCommand]
    public async Task PrevFilePageAsync(CancellationToken ct = default)
    {
        if (SelectedFile is null || !SelectedFile.IsPreviewable || FilePreviewPage <= 1) return;
        FilePreviewPage--;
        await LoadFilePreviewAsync(SelectedFile, FilePreviewPage, ct);
    }

    private Dictionary<string, object?> BuildJobFilePayload(SfJobFileItem file, string mode, int? page = null, int? maxBytes = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["job_id"] = Detail!.JobId,
            ["mode"] = mode,
        };
        if (!string.IsNullOrWhiteSpace(file.Path))
        {
            payload["path"] = file.Path;
        }
        else if (file.Id.HasValue)
        {
            payload["file_id"] = file.Id.Value;
        }
        if (page.HasValue)
        {
            payload["page"] = page.Value;
        }
        if (maxBytes.HasValue)
        {
            payload["max_bytes"] = maxBytes.Value;
        }
        return payload;
    }

    private async Task LoadFileTextAsync(SfJobFileItem file, CancellationToken ct)
    {
        if (Detail is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            var payload = BuildJobFilePayload(file, "text");
            var root = await _rpc.CallAsync("get_job_file", payload, ct, timeoutMs: 30000);
            FilePreviewImage = null;
            bool isBinary = SfJsonHelpers.GetBool(root, "binary");
            bool truncated = SfJsonHelpers.GetBool(root, "truncated");
            long size = SfJsonHelpers.GetLong(root, "size", file.Bytes);
            if (isBinary)
            {
                FileViewerText = string.Empty;
                FileViewerNotice = $"Binary file ({SfJsonHelpers.FormatBytes(size)}) — use Save as… to download.";
            }
            else
            {
                FileViewerText = SfJsonHelpers.GetString(root, "text");
                FileViewerNotice = truncated
                    ? $"Showing bounded text preview (truncated, total {SfJsonHelpers.FormatBytes(size)})."
                    : $"{SfJsonHelpers.FormatBytes(size)}";
            }
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            FileViewerText = null;
            FileViewerNotice = ex.Code;
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            FileViewerText = null;
            FileViewerNotice = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFilePreviewAsync(SfJobFileItem file, int page, CancellationToken ct)
    {
        if (Detail is null || _rpc is null) return;
        IsBusy = true;
        try
        {
            var payload = BuildJobFilePayload(file, "preview", page: page);
            var root = await _rpc.CallAsync("get_job_file", payload, ct, timeoutMs: 30000);
            string b64 = SfJsonHelpers.GetString(root, "png_b64");
            var bitmap = SfJsonHelpers.DecodePreviewBitmap(b64, out string? decodeError);
            if (decodeError is not null)
            {
                FilePreviewImage = null;
                FileViewerNotice = decodeError;
                ErrorMessage = decodeError;
                return;
            }

            int width = SfJsonHelpers.GetInt(root, "width");
            int height = SfJsonHelpers.GetInt(root, "height");
            int pages = SfJsonHelpers.GetInt(root, "pages", 1);

            FileViewerText = null;
            FilePreviewImage = bitmap;
            FilePreviewPage = page;
            FilePreviewPages = Math.Max(1, pages);
            FileViewerNotice = FilePreviewPages > 1
                ? $"Page {FilePreviewPage} of {FilePreviewPages} ({width}×{height})"
                : (width > 0 && height > 0 ? $"{width}×{height}" : string.Empty);
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            FilePreviewImage = null;
            FileViewerNotice = ex.Code == "not_supported"
                ? "No page preview available for this file (the bot host may lack a converter). Use Save as… to download."
                : ex.Code;
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            FilePreviewImage = null;
            FileViewerNotice = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task CancelSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedJob is null || _rpc is null) return;
        string jobId = SelectedJob.JobId;
        IsBusy = true;
        try
        {
            await _rpc.CallAsync("cancel_job", new { job_id = jobId }, ct);
            ErrorMessage = null;
            await SelectAsync(SelectedJob, ct);
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
    public async Task ExtendSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedJob is null || _rpc is null) return;
        string jobId = SelectedJob.JobId;
        IsBusy = true;
        try
        {
            await _rpc.CallAsync("extend_job", new { job_id = jobId }, ct);
            ErrorMessage = null;
            await SelectAsync(SelectedJob, ct);
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

    public async Task<(string FileName, byte[] Bytes)?> FetchJobFileBytesAsync(SfJobFileItem file, CancellationToken ct = default)
    {
        if (Detail is null || file is null || _rpc is null) return null;
        IsBusy = true;
        try
        {
            var payload = BuildJobFilePayload(file, "bytes");
            var root = await _rpc.CallAsync("get_job_file", payload, ct, timeoutMs: 60000);

            string? b64 = SfJsonHelpers.GetStringOrNull(root, "bytes_b64")
                ?? SfJsonHelpers.GetStringOrNull(root, "data_b64")
                ?? SfJsonHelpers.GetStringOrNull(root, "content_b64");

            if (string.IsNullOrWhiteSpace(b64))
            {
                ErrorMessage = "empty_file_bytes";
                return null;
            }

            byte[] data = Convert.FromBase64String(b64);
            string name = SfJsonHelpers.GetString(root, "name", file.Name);
            ErrorMessage = null;
            return (name, data);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
            return null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyJobEvent(SfJobEvent ev)
    {
        if (ev is null || string.IsNullOrWhiteSpace(ev.JobId)) return;

        var existing = Jobs.FirstOrDefault(j => string.Equals(j.JobId, ev.JobId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(ev.Status))
                existing.Status = ev.Status;
        }
        else if (MatchesCurrentFilter(ev))
        {
            var row = new SfJobRow
            {
                JobId = ev.JobId,
                UserId = ev.UserId,
                Username = ev.UserId > 0 ? $"u{ev.UserId}" : string.Empty,
                Status = string.IsNullOrWhiteSpace(ev.Status) ? "queued" : ev.Status,
                CreatedAt = FormatTimestamp(ev.TimestampUnixNano),
            };
            Jobs.Insert(0, row);
            TotalCount++;
            OnPropertyChanged(nameof(PageSummary));
        }

        if (Detail is not null && string.Equals(Detail.JobId, ev.JobId, StringComparison.Ordinal))
        {
            Detail.Events.Add(new SfJobEventItem(
                Type: ev.Kind ?? string.Empty,
                At: FormatTimestamp(ev.TimestampUnixNano),
                Summary: ExtractEventSummaryFromJson(ev.Json)));
        }
    }

    private bool MatchesCurrentFilter(SfJobEvent ev)
    {
        if (!string.IsNullOrWhiteSpace(StatusFilter) &&
            !string.Equals(StatusFilter, AllStatuses, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(ev.Status, StatusFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            if (!ev.JobId.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static SfJobRow ParseJobRow(JsonElement el) => new()
    {
        JobId = SfJsonHelpers.GetString(el, "job_id"),
        UserId = SfJsonHelpers.GetLong(el, "user_id"),
        Username = SfJsonHelpers.GetString(el, "username"),
        Status = SfJsonHelpers.GetString(el, "status"),
        OutputFormat = SfJsonHelpers.GetString(el, "output_format"),
        Origin = SfJsonHelpers.GetString(el, "origin"),
        Mode = SfJsonHelpers.GetString(el, "mode"),
        Title = SfJsonHelpers.GetString(el, "title"),
        CreatedAt = SfJsonHelpers.GetString(el, "created_at"),
        StartedAt = SfJsonHelpers.GetStringOrNull(el, "started_at"),
        FinishedAt = SfJsonHelpers.GetStringOrNull(el, "finished_at"),
        ErrorCode = SfJsonHelpers.GetStringOrNull(el, "error_code"),
        Charged = SfJsonHelpers.GetInt(el, "charged"),
    };

    private static SfJobFileItem ParseJobFileItem(JsonElement f)
    {
        long? id = SfJsonHelpers.GetNullableLong(f, "id");
        string path = SfJsonHelpers.GetStringOrNull(f, "path")
            ?? SfJsonHelpers.GetStringOrNull(f, "rel_path")
            ?? string.Empty;
        string name = SfJsonHelpers.GetString(f, "name");
        string kind = SfJsonHelpers.GetString(f, "kind");
        long bytes = f.TryGetProperty("size", out _)
            ? SfJsonHelpers.GetLong(f, "size")
            : SfJsonHelpers.GetLong(f, "bytes");
        string role = SfJsonHelpers.GetString(f, "role");
        long mtime = SfJsonHelpers.GetLong(f, "mtime");
        return new SfJobFileItem(id, path, name, kind, bytes, role, mtime);
    }

    private static SfJobDetail ParseJobDetail(string fallbackJobId, JsonElement root)
    {
        string jobId = fallbackJobId;
        if (root.TryGetProperty("job", out var jobEl) && jobEl.ValueKind == JsonValueKind.Object)
        {
            string parsed = SfJsonHelpers.GetString(jobEl, "job_id");
            if (!string.IsNullOrWhiteSpace(parsed))
                jobId = parsed;
        }

        string specJson = root.TryGetProperty("spec", out var specEl)
            ? SfJsonHelpers.PrettyPrint(specEl)
            : "{}";

        var detail = new SfJobDetail
        {
            JobId = jobId,
            SpecJson = specJson,
        };

        if (root.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in eventsEl.EnumerateArray())
            {
                string type = SfJsonHelpers.GetString(ev, "type");
                string at = SfJsonHelpers.GetString(ev, "at");
                string summary = ev.TryGetProperty("payload", out var payloadEl)
                    ? ExtractEventSummary(payloadEl)
                    : string.Empty;
                detail.Events.Add(new SfJobEventItem(type, at, summary));
            }
        }

        if (root.TryGetProperty("questions", out var qEl) && qEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qEl.EnumerateArray())
            {
                detail.Questions.Add(new SfJobQuestionItem(
                    Text: SfJsonHelpers.GetString(q, "text"),
                    Options: SfJsonHelpers.GetStringList(q, "options"),
                    Answer: SfJsonHelpers.GetStringOrNull(q, "answer"),
                    AnswerSource: SfJsonHelpers.GetStringOrNull(q, "answer_source")));
            }
        }

        if (root.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fEl.EnumerateArray())
            {
                detail.Files.Add(ParseJobFileItem(f));
            }
        }

        return detail;
    }

    private static string ExtractEventSummary(JsonElement payloadEl)
    {
        if (payloadEl.ValueKind == JsonValueKind.Object)
        {
            if (payloadEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;
            if (payloadEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                return msgEl.GetString() ?? string.Empty;
            if (payloadEl.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                return errEl.GetString() ?? string.Empty;
            return payloadEl.GetRawText();
        }
        if (payloadEl.ValueKind == JsonValueKind.String)
            return payloadEl.GetString() ?? string.Empty;
        return payloadEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : payloadEl.GetRawText();
    }

    private static string ExtractEventSummaryFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractEventSummary(doc.RootElement);
        }
        catch
        {
            return json;
        }
    }

    private static string FormatTimestamp(long unixNano)
    {
        if (unixNano <= 0)
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        long ms = unixNano / 1_000_000L;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O", CultureInfo.InvariantCulture);
    }
}
