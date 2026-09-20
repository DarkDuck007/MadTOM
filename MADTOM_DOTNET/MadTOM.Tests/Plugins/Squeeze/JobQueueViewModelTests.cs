using System.Linq;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using Xunit;

namespace SQUEEZE.Tests;

public class JobQueueViewModelTests
{
    [Fact]
    public async Task LoadsInitialJobsFromBackend()
    {
        var backend = new MockTranscoderBackendService();
        var vm = new JobQueueViewModel(backend);
        await vm.LoadJobsAsync();

        Assert.Equal(3, vm.Jobs.Count);
        Assert.NotNull(vm.SelectedJob);
        Assert.Equal("raw_footage_4k.mkv", vm.SelectedJob.Name);
    }

    [Fact]
    public async Task AddJob_AppendsNewQueuedJob_AndSelectsIt()
    {
        var backend = new MockTranscoderBackendService();
        var vm = new JobQueueViewModel(backend);
        await vm.LoadJobsAsync();

        var initialCount = vm.Jobs.Count;
        vm.OnAddJobRequested = async () => await vm.EnqueueJobAsync("selected.mp4", "source", "fast1080");
        await vm.AddJobAsync();

        Assert.Equal(initialCount + 1, vm.Jobs.Count);
        Assert.NotNull(vm.SelectedJob);
        Assert.Equal(JobStatus.Queued, vm.SelectedJob.Status);
    }

    [Fact]
    public async Task AddJobWithoutPickerDoesNotCreateDummyMedia()
    {
        var vm = new JobQueueViewModel(new MockTranscoderBackendService());
        var count = vm.Jobs.Count;
        await vm.AddJobAsync();
        Assert.Equal(count, vm.Jobs.Count);
    }

    [Fact]
    public async Task ClearCompleted_RemovesFinishedJobs()
    {
        var backend = new MockTranscoderBackendService();
        var vm = new JobQueueViewModel(backend);
        await vm.LoadJobsAsync();

        Assert.Contains(vm.Jobs, j => j.Status == JobStatus.Completed);

        await vm.ClearCompletedAsync();

        Assert.DoesNotContain(vm.Jobs, j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public void SelectJob_UpdatesSelectedJob_AndTriggersCallback()
    {
        var backend = new MockTranscoderBackendService();
        var vm = new JobQueueViewModel(backend);
        TranscodeJob? callbackJob = null;
        vm.OnJobSelected = j => callbackJob = j;

        var target = new TranscodeJob { Name = "test_target.mp4" };
        vm.SelectJob(target);

        Assert.Same(target, vm.SelectedJob);
        Assert.Same(target, callbackJob);
    }

    [Fact]
    public async Task CancelJob_SetsStatusToCancelled()
    {
        var backend = new MockTranscoderBackendService();
        var vm = new JobQueueViewModel(backend);
        await vm.LoadJobsAsync();

        var jobToCancel = vm.Jobs.First(j => j.CanCancel);
        Assert.True(jobToCancel.CanCancel);

        await vm.CancelJobAsync(jobToCancel);

        Assert.Equal(JobStatus.Cancelled, jobToCancel.Status);
        Assert.False(jobToCancel.CanCancel);
        Assert.Equal("CANCELLED", jobToCancel.ProgressLabel);
    }

    [Theory]
    [InlineData(JobStatus.Uploading, 45, true, "UPLOADING 45%")]
    [InlineData(JobStatus.Downloading, 82, true, "DOWNLOADING 82%")]
    [InlineData(JobStatus.Queued, 0, true, "QUEUED")]
    [InlineData(JobStatus.Encoding, 60, true, "60%")]
    [InlineData(JobStatus.Paused, 30, true, "PAUSED")]
    [InlineData(JobStatus.Completed, 100, false, "DONE")]
    [InlineData(JobStatus.Failed, 20, false, "FAILED")]
    [InlineData(JobStatus.Cancelled, 15, false, "CANCELLED")]
    public void TranscodeJob_CanCancelAndProgressLabel_FormatCorrectly(JobStatus status, double progress, bool expectedCanCancel, string expectedLabel)
    {
        var job = new TranscodeJob
        {
            Status = status,
            Progress = progress
        };

        Assert.Equal(expectedCanCancel, job.CanCancel);
        Assert.Equal(expectedLabel, job.ProgressLabel);
    }
}

