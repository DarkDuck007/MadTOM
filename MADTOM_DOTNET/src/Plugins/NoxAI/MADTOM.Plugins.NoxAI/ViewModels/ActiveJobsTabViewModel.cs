using System;
using System.Collections.ObjectModel;
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
}

public sealed partial class ActiveJobsTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _jobsSummary = "0 active jobs";

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
            JobsSummary = "0 active jobs";
        }
    }

    public void OnJobEventReceived(NoxJobProgressEvent job)
    {
        var existing = Jobs.FirstOrDefault(j => j.JobId == job.JobId);
        if (existing != null)
        {
            Jobs.Remove(existing);
        }

        if (job.Status is not ("completed" or "failed") || Jobs.Count < 50)
        {
            Jobs.Insert(0, new ActiveJobItemModel
            {
                JobId = job.JobId,
                JobType = job.JobType,
                Status = job.Status,
                Progress = job.Progress,
                Details = job.Details,
                ElapsedFormatted = $"{job.ElapsedMs / 1000}s"
            });
        }
        JobsSummary = $"{Jobs.Count(j => j.Status == "running")} running ({Jobs.Count} tracked)";
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
                    ElapsedFormatted = $"{j.ElapsedMs / 1000}s"
                });
            }
            JobsSummary = $"{Jobs.Count} active jobs";
        }
        catch (Exception ex)
        {
            JobsSummary = $"Error: {ex.Message}";
        }
    }
}
