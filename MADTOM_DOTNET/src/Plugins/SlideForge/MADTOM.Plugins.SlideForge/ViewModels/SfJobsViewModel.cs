using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

public sealed record SfJobFileItem(long Id, string Name, string Kind, long Bytes, string Role)
{
    public string SizeDisplay => SfJsonHelpers.FormatBytes(Bytes);
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

    public ObservableCollection<SfJobRow> Jobs { get; } = new();

    public IReadOnlyList<string> StatusOptions { get; } = new[]
    {
        AllStatuses, "queued", "running", "waiting_input", "completed", "failed", "cancelled"
    };

    public bool HasDetail => Detail is not null;
    public string PageSummary => TotalCount == 0
        ? "0 jobs"
        : $"{Offset + 1}–{Math.Min(Offset + Jobs.Count, TotalCount)} of {TotalCount}";

    partial void OnStatusFilterChanged(string value) => Offset = 0;
    partial void OnSearchTextChanged(string value) => Offset = 0;
    partial void OnDetailChanged(SfJobDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageSummary));
    partial void OnOffsetChanged(int value) => OnPropertyChanged(nameof(PageSummary));

    public SfJobsViewModel(SfRpc? rpc)
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
            Detail = ParseJobDetail(row.JobId, root);
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
            var root = await _rpc.CallAsync("get_job_file", new
            {
                job_id = Detail.JobId,
                file_id = file.Id,
                mode = "bytes",
            }, ct, timeoutMs: 60000);

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
                detail.Files.Add(new SfJobFileItem(
                    Id: SfJsonHelpers.GetLong(f, "id"),
                    Name: SfJsonHelpers.GetString(f, "name"),
                    Kind: SfJsonHelpers.GetString(f, "kind"),
                    Bytes: SfJsonHelpers.GetLong(f, "bytes"),
                    Role: SfJsonHelpers.GetString(f, "role")));
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
