using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQUEEZE.Models;
using SQUEEZE.Services;

namespace SQUEEZE.ViewModels;

public partial class JobQueueViewModel : ViewModelBase
{
    private readonly ITranscoderBackendService _backendService;

    [ObservableProperty]
    private ObservableCollection<TranscodeJob> _jobs = new();

    [ObservableProperty]
    private TranscodeJob? _selectedJob;

    public string QueueCountText => $"{Jobs.Count} JOBS";

    public Action<TranscodeJob>? OnJobSelected { get; set; }
    public Action<TranscodeJob>? OnDownloadRequested { get; set; }
    public Func<Task>? OnAddJobRequested { get; set; }

    public JobQueueViewModel(ITranscoderBackendService backendService)
    {
        _backendService = backendService;
        _backendService.JobUpdated += OnBackendJobUpdated;
        _ = LoadJobsAsync();
    }

    public async Task LoadJobsAsync()
    {
        var list = await _backendService.GetJobsAsync();
        var selectedId = SelectedJob?.Id;
        Jobs.Clear();
        foreach (var job in list)
        {
            Jobs.Add(job);
        }
        OnPropertyChanged(nameof(QueueCountText));

        SelectedJob = Jobs.FirstOrDefault(j => j.Id == selectedId);
        if (SelectedJob == null && Jobs.Any())
        {
            SelectJob(Jobs.First());
        }
    }

    private void OnBackendJobUpdated(object? sender, TranscodeJob updatedJob)
    {
        void Update()
        {
            var existing = Jobs.FirstOrDefault(j => j.Id == updatedJob.Id);
            if (existing != null)
            {
                var idx = Jobs.IndexOf(existing);
                if (string.IsNullOrEmpty(updatedJob.Name)) updatedJob.Name = existing.Name;
                if (string.IsNullOrEmpty(updatedJob.PresetKey)) updatedJob.PresetKey = existing.PresetKey;
                if (string.IsNullOrEmpty(updatedJob.Meta)) updatedJob.Meta = existing.Meta;

                Jobs[idx] = updatedJob;
                if (SelectedJob?.Id == updatedJob.Id)
                {
                    SelectedJob = updatedJob;
                }
            }
            else
            {
                Jobs.Add(updatedJob);
            }
            OnPropertyChanged(nameof(QueueCountText));
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Update);
        }
    }

    [RelayCommand]
    public void SelectJob(TranscodeJob? job)
    {
        if (job == null) return;
        SelectedJob = job;
        OnJobSelected?.Invoke(job);
    }

    [RelayCommand]
    public async Task AddJobAsync()
    {
        if (OnAddJobRequested != null)
        {
            await OnAddJobRequested.Invoke();
        }

    }

    public async Task<TranscodeJob> EnqueueJobAsync(string name, string meta, string presetKey, string? filePath = null, TranscodePreset? options = null)
    {
        var job = await _backendService.AddJobAsync(name, meta, presetKey, filePath, options);
        if (!Jobs.Any(j => j.Id == job.Id))
        {
            Jobs.Add(job);
            OnPropertyChanged(nameof(QueueCountText));
        }
        SelectJob(job);
        return job;
    }

    [RelayCommand]
    public async Task ClearCompletedAsync()
    {
        await _backendService.ClearCompletedJobsAsync();
        await LoadJobsAsync();
    }

    [RelayCommand]
    public async Task PauseJobAsync(TranscodeJob? job)
    {
        var target = job ?? SelectedJob;
        if (target == null) return;
        if (await _backendService.PauseJobAsync(target.Id))
        {
            target.Status = JobStatus.Paused;
            OnPropertyChanged(nameof(QueueCountText));
        }
    }

    [RelayCommand]
    public async Task ResumeJobAsync(TranscodeJob? job)
    {
        var target = job ?? SelectedJob;
        if (target == null) return;
        if (await _backendService.ResumeJobAsync(target.Id))
        {
            target.Status = JobStatus.Encoding;
            OnPropertyChanged(nameof(QueueCountText));
        }
    }

    [RelayCommand]
    public async Task CancelJobAsync(TranscodeJob? job)
    {
        var target = job ?? SelectedJob;
        if (target == null) return;
        await _backendService.CancelJobAsync(target.Id);
        target.Status = JobStatus.Cancelled;
        OnPropertyChanged(nameof(QueueCountText));
    }

    [RelayCommand]
    public void DownloadJob(TranscodeJob? job)
    {
        if (job == null) return;
        OnDownloadRequested?.Invoke(job);
    }
}
