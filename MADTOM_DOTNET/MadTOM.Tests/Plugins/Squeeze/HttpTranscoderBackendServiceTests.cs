using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class HttpTranscoderBackendServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> HandlerFunc { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(HandlerFunc(request));
        }
    }

    [Fact]
    public async Task GetNodeInfoAsync_ParsesServerHealthResponse()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/api/v1/health")
                {
                    var json = """
                    {
                        "status": "healthy",
                        "version": "1.2.0",
                        "node_id": "RIG_DELTA_01",
                        "uptime_seconds": 3600.0,
                        "cpu_cores": 16,
                        "load_average": 0.85,
                        "process_memory_bytes": 67108864,
                        "system_memory_bytes": 34359738368,
                        "available_memory_bytes": 17179869184,
                        "max_concurrent": 2,
                        "hardware_status": "HW ACTIVE",
                        "hardware_encoders": ["h264_nvenc", "hevc_nvenc"],
                        "pause_supported": true
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var service = new HttpTranscoderBackendService(httpClient);
        await service.ConnectAsync("http://localhost:8080");

        var node = await service.GetNodeInfoAsync();

        Assert.True(node.IsOnline);
        Assert.Equal("RIG_DELTA_01", node.NodeName);
        Assert.Equal("1.2.0", node.Version);
        Assert.Equal(16, node.CpuCores);
        Assert.Equal(2, node.HardwareEncoders.Count);
        Assert.True(node.PauseSupported);
        Assert.Contains("HW:", node.GpuStatus);
    }

    [Fact]
    public async Task GetPresetsAsync_ParsesServerPresetsResponse()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/api/v1/presets")
                {
                    var json = """
                    [
                        {
                            "key": "server_fast1080",
                            "title": "Server Fast 1080p",
                            "category": "General",
                            "description": "Server profile",
                            "tag": "SERVER",
                            "spec": {
                                "video_codec": "libx264",
                                "crf": 21,
                                "container": "mp4",
                                "audio_codec": "aac",
                                "audio_bitrate": 192,
                                "width": 1920,
                                "deinterlace": true
                            }
                        }
                    ]
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var service = new HttpTranscoderBackendService(httpClient);
        await service.ConnectAsync("http://localhost:8080");

        var presets = await service.GetPresetsAsync();

        Assert.Single(presets);
        Assert.Equal("server_fast1080", presets[0].Key);
        Assert.Equal("Server Fast 1080p", presets[0].Title);
        Assert.Equal("libx264", presets[0].Codec);
        Assert.Equal(21.0, presets[0].Crf);
        Assert.Equal("192k", presets[0].AudioBitrate);
        Assert.True(presets[0].Deinterlace);
    }

    [Fact]
    public async Task GetJobsAsync_ParsesJobsList()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/api/v1/jobs")
                {
                    var json = """
                    [
                        {
                            "job_id": "job12345",
                            "filename": "render_test.mp4",
                            "status": "encoding",
                            "file_size": 2048,
                            "progress": {
                                "progress": 45.2
                            }
                        }
                    ]
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        var httpClient = new HttpClient(mockHandler);
        var service = new HttpTranscoderBackendService(httpClient);
        await service.ConnectAsync("http://localhost:8080");

        var jobs = await service.GetJobsAsync();

        Assert.Single(jobs);
        Assert.Equal("render_test.mp4", jobs[0].Name);
        Assert.Equal(JobStatus.Encoding, jobs[0].Status);
        Assert.Equal(45.2, jobs[0].Progress);
        Assert.Equal("job12345", jobs[0].ServerJobId);
    }
}

