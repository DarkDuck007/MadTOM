using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class ActiveJobItemModel : ObservableObject
{
    public string JobId { get; init; } = string.Empty;
    public string JobType { get; init; } = string.Empty;
    public string Status { get; init; } = "running";
    public double Progress { get; init; } = 0.0;
    public string Details { get; init; } = string.Empty;
    public string ElapsedFormatted { get; init; } = "0s";
    public long ChatId { get; init; }
    public string Executor { get; init; } = string.Empty;

    public bool HasExecutor => !string.IsNullOrEmpty(Executor);

    public string ExecutorBadge => Executor switch
    {
        "vm" => "🖥 Chat VM",
        "bwrap" => "🔒 Host jail",
        "" or null => string.Empty,
        _ => Executor
    };

    public string ExecutorLocation => Executor switch
    {
        "vm" when ChatId != 0 => string.Create(CultureInfo.InvariantCulture, $"chat {ChatId} sandbox VM · /data/studio/{JobId}"),
        "bwrap" => "host bubblewrap jail",
        _ => string.Empty
    };

    /// <summary>Friendly label for known job types; unknown types fall back to the raw identifier.</summary>
    public string JobTypeLabel => JobType switch
    {
        "image_studio" => "🖼 Image Studio",
        "doc_studio" or "document_studio" => "📄 Document Studio",
        "sticker_pack_analysis" => "📦 Sticker pack analysis",
        "sticker_map" => "🗺 Sticker map",
        "sticker_reduce" => "🧩 Sticker reduce",
        "audio_stt" => "🎙 Speech to text",
        "sound_classifier" => "🔊 Sound classifier",
        "song_recognize" => "🎵 Song recognition",
        "embedding" => "🧠 Embedding",
        _ => string.IsNullOrWhiteSpace(JobType) ? "job" : JobType
    };

    public string StatusLine => ChatId != 0 ? $"{Status} · chat {ChatId}" : Status;

    public static bool IsTerminalStatus(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";
}

public sealed partial class ActiveJobsTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _jobsSummary = "0 active jobs";
    [ObservableProperty] private string _docGenSummary = string.Empty;
    [ObservableProperty] private int _vmJobCount;
    [ObservableProperty] private int _bwrapJobCount;
    [ObservableProperty] private ActiveJobItemModel? _selectedJob;

    public ObservableCollection<ActiveJobItemModel> Jobs { get; } = new();

    public ActiveJobsTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value != null)
        {
            _ = RefreshJobs();
        }
        else
        {
            Jobs.Clear();
            SelectedJob = null;
            JobsSummary = "0 active jobs";
            DocGenSummary = string.Empty;
            VmJobCount = 0;
            BwrapJobCount = 0;
        }
    }

    public void OnJobEventReceived(NoxJobProgressEvent job)
    {
        var existing = Jobs.FirstOrDefault(j => j.JobId == job.JobId);
        bool wasSelected = existing != null && SelectedJob == existing;
        if (existing != null)
        {
            Jobs.Remove(existing);
        }

        if (!ActiveJobItemModel.IsTerminalStatus(job.Status) || Jobs.Count < 50)
        {
            var item = new ActiveJobItemModel
            {
                JobId = job.JobId,
                JobType = job.JobType,
                Status = job.Status,
                Progress = job.Progress,
                Details = job.Details,
                ElapsedFormatted = $"{job.ElapsedMs / 1000}s",
                ChatId = job.ChatId,
                Executor = job.Executor ?? string.Empty
            };
            Jobs.Insert(0, item);
            if (wasSelected)
            {
                SelectedJob = item;
            }
        }
        else if (wasSelected)
        {
            SelectedJob = null;
        }

        int running = Jobs.Count(j => j.Status == "running");
        int queued = Jobs.Count(j => j.Status == "queued");
        JobsSummary = queued > 0
            ? $"{running} running, {queued} queued ({Jobs.Count} tracked)"
            : $"{running} running ({Jobs.Count} tracked)";
        UpdateExecutorCounts();
    }

    [RelayCommand]
    public async Task RefreshJobs()
    {
        if (SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.GetActiveJobsAsync(new NoxActiveJobsRequest
            {
                NodeId = SelectedNode.Id
            });

            string? prevSelectedId = SelectedJob?.JobId;
            Jobs.Clear();
            foreach (var j in res.ActiveJobs)
            {
                Jobs.Add(new ActiveJobItemModel
                {
                    JobId = j.JobId,
                    JobType = j.JobType,
                    Status = j.Status,
                    Progress = j.Progress,
                    Details = j.Details,
                    ElapsedFormatted = $"{j.ElapsedMs / 1000}s",
                    ChatId = j.ChatId,
                    Executor = j.Executor ?? string.Empty
                });
            }
            if (!string.IsNullOrEmpty(prevSelectedId))
            {
                SelectedJob = Jobs.FirstOrDefault(j => j.JobId == prevSelectedId);
            }

            JobsSummary = $"{Jobs.Count} active jobs";
            DocGenSummary = FormatDocGenSummary(res.DocGen);
            UpdateExecutorCounts();
        }
        catch (Exception ex)
        {
            JobsSummary = $"Error: {ex.Message}";
        }
    }

    private void UpdateExecutorCounts()
    {
        VmJobCount = Jobs.Count(j => !ActiveJobItemModel.IsTerminalStatus(j.Status) && j.Executor == "vm");
        BwrapJobCount = Jobs.Count(j => !ActiveJobItemModel.IsTerminalStatus(j.Status) && j.Executor == "bwrap");
    }

    private static string FormatMb(long bytes) =>
        $"{(bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)} MB";

    private static string FormatDocGenSummary(NoxDocGenSettings? docGen)
    {
        if (docGen == null) return string.Empty;
        string summary = $"Doc Studio executor: {docGen.Executor} · copy to /data/documents: {(docGen.CopyToSandbox ? "on" : "off")}";
        if (docGen.Sandbox != null)
        {
            var sb = docGen.Sandbox;
            summary += string.Create(
                CultureInfo.InvariantCulture,
                $" · keep {sb.KeepJobs} jobs · sync ≤ {FormatMb(sb.SyncMaxFileBytes)} · lock wait {sb.LockWaitS}s · exec margin {sb.ExecMarginS}s");
        }
        return summary;
    }
}
