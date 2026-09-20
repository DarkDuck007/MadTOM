using System.Linq;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class MockBackendServiceTests
{
    [Fact]
    public async Task GetNodeInfo_ReturnsOnlineServerWithHardwareStats()
    {
        var service = new MockTranscoderBackendService();
        var node = await service.GetNodeInfoAsync();

        Assert.NotNull(node);
        Assert.True(node.IsOnline);
        Assert.Equal("NODE_01", node.NodeName);
        Assert.Contains("192.168.1.140", node.LanAddress);
        Assert.Contains("RTX 4070", node.GpuStatus);
    }

    [Fact]
    public async Task GetJobs_ReturnsThreeSampleJobs()
    {
        var service = new MockTranscoderBackendService();
        var jobs = await service.GetJobsAsync();

        Assert.Equal(3, jobs.Count);
        Assert.Contains(jobs, j => j.Status == JobStatus.Idle);
        Assert.Contains(jobs, j => j.Status == JobStatus.Queued);
        Assert.Contains(jobs, j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task StartJob_UpdatesStatusToEncoding()
    {
        var service = new MockTranscoderBackendService();
        var jobs = await service.GetJobsAsync();
        var idleJob = jobs.First(j => j.Status == JobStatus.Idle);

        var started = await service.StartJobAsync(idleJob.Id);

        Assert.True(started);
        Assert.Equal(JobStatus.Encoding, idleJob.Status);
    }

    [Fact]
    public async Task PauseJob_UpdatesStatusToPaused()
    {
        var service = new MockTranscoderBackendService();
        var jobs = await service.GetJobsAsync();
        var job = jobs.First();

        await service.StartJobAsync(job.Id);
        var paused = await service.PauseJobAsync(job.Id);

        Assert.True(paused);
        Assert.Equal(JobStatus.Paused, job.Status);
    }

    [Fact]
    public async Task CancelJob_UpdatesStatusToCancelled()
    {
        var service = new MockTranscoderBackendService();
        var jobs = await service.GetJobsAsync();
        var job = jobs.First();

        var cancelled = await service.CancelJobAsync(job.Id);

        Assert.True(cancelled);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task DownloadUrl_ReturnsValidEndpoint()
    {
        var service = new MockTranscoderBackendService();
        var jobs = await service.GetJobsAsync();
        var completedJob = jobs.First(j => j.Status == JobStatus.Completed);

        var url = await service.GetDownloadUrlAsync(completedJob.Id);

        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.Contains("http", url);
    }
}

