using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class TransferRegressionTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task LargeUploadStreamsInBoundedWritesAndWaitsForServerAcceptance()
    {
        var source = Path.GetTempFileName();
        const long size = 500L * 1024 * 1024;
        using (var file = File.OpenWrite(source)) file.SetLength(size);
        var progress = new List<double>();
        using var handler = new Handler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health")) return Json("{}");
            if (path.EndsWith("/events")) return new(HttpStatusCode.NotFound);
            if (path.EndsWith("/upload"))
            {
                Assert.Equal(size, req.Content!.Headers.ContentLength);
                using var sink = new UploadSink();
                await req.Content.CopyToAsync(sink);
                Assert.Equal(size, sink.BytesWritten);
                Assert.InRange(sink.LargestWrite, 1, 65536);
                Assert.NotEmpty(progress);
                Assert.All(progress, value => Assert.InRange(value, 0, 99));
                return new(HttpStatusCode.NoContent);
            }
            if (req.Method == HttpMethod.Post)
                return Json("""{"job_id":"large","filename":"large.mp4","status":"awaiting_upload"}""");
            if (path.EndsWith("/jobs")) return Json("[]");
            return Json("""{"job_id":"large","filename":"large.mp4","status":"queued"}""");
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        service.JobUpdated += (_, job) =>
        {
            if (job.Status == JobStatus.Uploading) progress.Add(job.Progress);
        };
        try
        {
            Assert.True(await service.ConnectAsync("http://localhost"));
            var job = await service.AddJobAsync("large.mp4", "", "", source);
            Assert.Equal(JobStatus.Queued, job.Status);
        }
        finally { File.Delete(source); }
    }

    private sealed class UploadSink : Stream
    {
        public long BytesWritten { get; private set; }
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            LargestWrite = Math.Max(LargestWrite, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BytesWritten += buffer.Length;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SubmissionSendsCurrentOptionsAndUploadsBeforeReturningWithoutExtraStart()
    {
        var source = Path.GetTempFileName();
        await File.WriteAllTextAsync(source, "real source bytes");
        var calls = new List<string>();
        using var handler = new Handler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            Assert.Equal("secret", req.Headers.Authorization?.Parameter);
            lock (calls) calls.Add($"{req.Method} {path}");
            if (path.EndsWith("/health")) return Json("{}");
            if (path == "/api/v1/jobs")
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                var spec = body.RootElement.GetProperty("spec");
                Assert.Equal("", body.RootElement.GetProperty("preset_key").GetString());
                Assert.Equal("libx264", spec.GetProperty("video_codec").GetString());
                Assert.Equal(19, spec.GetProperty("crf").GetInt32());
                Assert.Equal(720, spec.GetProperty("height").GetInt32());
                Assert.False(spec.GetProperty("deinterlace").GetBoolean());
                return Json("""{"job_id":"one","filename":"clip.mov","status":"awaiting_upload","spec":{"container":"mkv"}}""", HttpStatusCode.Created);
            }
            if (path.EndsWith("/upload"))
            {
                Assert.Equal("real source bytes", await req.Content!.ReadAsStringAsync());
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/events")) return new(HttpStatusCode.NotFound);
            Assert.Equal("GET", req.Method.Method);
            return Json("""{"job_id":"one","filename":"clip.mov","status":"completed","spec":{"container":"mkv"}}""");
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        try
        {
            await service.ConnectAsync("http://localhost", "secret");
            var job = await service.AddJobAsync("clip.mov", "source", "local-custom", source,
                new TranscodePreset { Codec = "libx264", Crf = 19, ResolutionLimit = "720p", Deinterlace = false, Container = "mkv" });
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal("clip_squeezed.mkv", job.TargetOutputName);
            Assert.Equal(100, job.Progress);
            lock (calls) Assert.DoesNotContain(calls, c => c.EndsWith("/start"));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task UploadFailureIsReportedAndNeverStartsTheJob()
    {
        var source = Path.GetTempFileName();
        await File.WriteAllTextAsync(source, "media");
        bool cancelled = false;
        using var handler = new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            Assert.False(path.EndsWith("/start"));
            if (path.EndsWith("/health")) return Task.FromResult(Json("{}"));
            if (path.EndsWith("/upload")) return Task.FromResult(Json("{\"error\":\"disk full\"}", HttpStatusCode.BadRequest));
            if (req.Method == HttpMethod.Delete) cancelled = true;
            return Task.FromResult(Json("""{"job_id":"one","filename":"clip.mp4","status":"awaiting_upload"}"""));
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        try
        {
            await service.ConnectAsync("http://localhost");
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.AddJobAsync("clip.mp4", "", "", source));
            Assert.Contains("disk full", ex.Message);
            Assert.True(cancelled);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task OfflineSubmissionDoesNotInventAQueuedJob()
    {
        using var service = new HttpTranscoderBackendService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddJobAsync("clip.mp4", "", "fast1080"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadUsesAuthenticationAndPreservesDestinationOnFailure(bool success)
    {
        var destination = Path.GetTempFileName();
        await File.WriteAllTextAsync(destination, "existing content");
        using var handler = new Handler(req =>
        {
            Assert.Equal("secret", req.Headers.Authorization?.Parameter);
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health")) return Task.FromResult(Json("{}"));
            if (path.EndsWith("/jobs")) return Task.FromResult(Json("""[{"job_id":"one","filename":"clip.mp4","status":"completed"}]"""));
            return Task.FromResult(Json(success ? "converted bytes" : "expired", success ? HttpStatusCode.OK : HttpStatusCode.Gone));
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        try
        {
            await service.ConnectAsync("http://localhost", "secret");
            var job = (await service.GetJobsAsync()).Single();
            if (success) await service.DownloadJobAsync(job.Id, destination);
            else await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadJobAsync(job.Id, destination));
            Assert.Equal(success ? "converted bytes" : "existing content", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".*.part"));
        }
        finally { File.Delete(destination); }
    }

    [Fact]
    public async Task PausedJobUsesResumeEndpointAndClearCompletedHidesWithoutDeletingOutput()
    {
        bool resumed = false;
        using var handler = new Handler(req =>
        {
            Assert.NotEqual(HttpMethod.Delete, req.Method);
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health")) return Task.FromResult(Json("{}"));
            if (path.EndsWith("/events")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path.EndsWith("/jobs")) return Task.FromResult(Json("""[{"job_id":"one","filename":"clip.mp4","status":"paused"},{"job_id":"done","filename":"done.mp4","status":"completed"}]"""));
            if (req.Method == HttpMethod.Post) { Assert.EndsWith("/resume", path); resumed = true; }
            return Task.FromResult(Json("""{"job_id":"one","filename":"clip.mp4","status":"encoding"}"""));
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        await service.ConnectAsync("http://localhost");
        var jobs = await service.GetJobsAsync();
        Assert.True(await service.StartJobAsync(jobs[0].Id));
        Assert.True(resumed);
        await service.ClearCompletedJobsAsync();
        Assert.Single(await service.GetJobsAsync());
    }

    [Fact]
    public async Task FractionalBitrateEventsRetainJobIdentityAndCompletionMetadata()
    {
        var completed = new TaskCompletionSource<TranscodeJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<TranscodeJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health")) return Task.FromResult(Json("{}"));
            if (path.EndsWith("/jobs")) return Task.FromResult(Json("""[{"job_id":"one","filename":"clip.mov","status":"encoding","spec":{"container":"mkv"}}]"""));
            return Task.FromResult(Json("event: progress\ndata: {\"fps\":30,\"bitrate\":123.45,\"speed\":\"1.5x\",\"progress\":75}\n\nevent: complete\ndata: {\"job_id\":\"one\",\"filename\":\"clip.mov\",\"status\":\"completed\",\"spec\":{\"container\":\"mkv\"}}\n\n"));
        });
        using var client = new HttpClient(handler);
        using var service = new HttpTranscoderBackendService(client);
        service.JobUpdated += (_, job) =>
        {
            if (job.Status == JobStatus.Completed) completed.TrySetResult(job);
            if (job.Progress == 75) progress.TrySetResult(job);
        };
        await service.ConnectAsync("http://localhost");
        var original = (await service.GetJobsAsync()).Single();
        var live = await progress.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(123, live.BitrateKbps);
        Assert.Equal("clip.mov", live.Name);
        var final = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original.Id, final.Id);
        Assert.Equal("clip_squeezed.mkv", final.TargetOutputName);
    }
}
