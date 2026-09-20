using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQUEEZE.Models;

namespace SQUEEZE.Services;

public class HttpTranscoderBackendService : ITranscoderBackendService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<Guid, string> _guidToServerId = new();
    private readonly ConcurrentDictionary<string, Guid> _serverIdToGuid = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeSseStreams = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeUploadCts = new();
    private readonly ConcurrentDictionary<Guid, TranscodeJob> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _hiddenJobs = new();
    private string _baseUrl = "http://127.0.0.1:8080";
    private string? _token;
    private bool _isConnected = false;
    private CancellationTokenSource? _serviceCts;

    public string BaseUrl => _baseUrl;
    public bool IsConnected => _isConnected;

    public event EventHandler<TranscodeJob>? JobUpdated;
    public event EventHandler<TelemetryMetrics>? TelemetryUpdated;
    public event EventHandler<string>? StdoutLineReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public HttpTranscoderBackendService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient == null;
        // Use the same streaming transport on Android and desktop. The native Android
        // handler can buffer custom HttpContent before writing it to the connection.
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<bool> ConnectAsync(string baseUrl, string? token = null)
    {
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _serviceCts = null;
        foreach (var stream in _activeSseStreams.Values) stream.Cancel();
        _activeSseStreams.Clear();
        _guidToServerId.Clear();
        _serverIdToGuid.Clear();
        _jobs.Clear();
        _hiddenJobs.Clear();
        _baseUrl = baseUrl.TrimEnd('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        bool success = await TestConnectionAsync(_baseUrl, _token);
        _isConnected = success;
        if (_isConnected)
        {
            _serviceCts = new CancellationTokenSource();
            // Start background sync for active jobs
            _ = StartActiveJobsSyncAsync(_serviceCts.Token);
        }

        ConnectionStatusChanged?.Invoke(this, _isConnected);
        return success;
    }

    public async Task<bool> TestConnectionAsync(string baseUrl, string? token = null)
    {
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/v1/health";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.SendAsync(req, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ServerNodeInfo> GetNodeInfoAsync()
    {
        if (!_isConnected)
        {
            return new ServerNodeInfo { IsOnline = false, NodeName = "OFFLINE" };
        }

        try
        {
            var url = $"{_baseUrl}/api/v1/health";
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await SendControlAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return new ServerNodeInfo { IsOnline = false, NodeName = "UNREACHABLE" };
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return ParseNodeInfo(doc.RootElement);
        }
        catch
        {
            _isConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);
            return new ServerNodeInfo { IsOnline = false, NodeName = "OFFLINE" };
        }
    }

    public async Task<ServerNodeInfo> RescanHardwareAsync()
    {
        if (!_isConnected)
        {
            return new ServerNodeInfo { IsOnline = false, NodeName = "OFFLINE" };
        }

        try
        {
            var url = $"{_baseUrl}/api/v1/hardware/rescan";
            using var req = CreateRequest(HttpMethod.Post, url);
            using var resp = await SendControlAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return await GetNodeInfoAsync();
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return ParseNodeInfo(doc.RootElement);
        }
        catch
        {
            return await GetNodeInfoAsync();
        }
    }

    private ServerNodeInfo ParseNodeInfo(JsonElement root)
    {
        var info = new ServerNodeInfo
        {
            IsOnline = true,
            NodeName = root.TryGetProperty("node_id", out var n) ? n.GetString() ?? "NODE" : "NODE",
            LanAddress = _baseUrl.Replace("http://", "").Replace("https://", ""),
            Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0",
            UptimeSeconds = root.TryGetProperty("uptime_seconds", out var u) ? u.GetDouble() : 0,
            CpuCores = root.TryGetProperty("cpu_cores", out var c) ? c.GetInt32() : 1,
            ProcessMemoryBytes = root.TryGetProperty("process_memory_bytes", out var pm) ? pm.GetUInt64() : 0,
            SystemMemoryBytes = root.TryGetProperty("system_memory_bytes", out var sm) ? sm.GetUInt64() : 0,
            AvailableMemoryBytes = root.TryGetProperty("available_memory_bytes", out var am) ? am.GetUInt64() : 0,
            MaxConcurrent = root.TryGetProperty("max_concurrent", out var mc) ? mc.GetInt32() : 1,
            HardwareStatus = root.TryGetProperty("hardware_status", out var hs) ? hs.GetString() ?? "" : "",
            PauseSupported = root.TryGetProperty("pause_supported", out var ps) && ps.GetBoolean()
        };

        if (root.TryGetProperty("load_average", out var la) && la.ValueKind == JsonValueKind.Number)
        {
            info.LoadAverage = la.GetDouble();
        }

        if (root.TryGetProperty("hardware_encoders", out var hwe) && hwe.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in hwe.EnumerateArray())
            {
                if (el.GetString() is string s) info.HardwareEncoders.Add(s);
            }
        }

        if (root.TryGetProperty("encoders", out var enc) && enc.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in enc.EnumerateArray())
            {
                if (el.GetString() is string s) info.Encoders.Add(s);
            }
        }

        return info;
    }

    public async Task<IReadOnlyList<TranscodePreset>> GetPresetsAsync()
    {
        if (!_isConnected) return new List<TranscodePreset>();

        try
        {
            var url = $"{_baseUrl}/api/v1/presets";
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await SendControlAsync(req);
            if (!resp.IsSuccessStatusCode) return new List<TranscodePreset>();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var result = new List<TranscodePreset>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var key = el.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? key : key;
                var category = el.TryGetProperty("category", out var cat) ? cat.GetString() ?? "General" : "General";
                var description = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var tag = el.TryGetProperty("tag", out var tg) ? tg.GetString() : null;

                var preset = new TranscodePreset
                {
                    Key = key,
                    Title = title,
                    Category = category,
                    Description = description,
                    Tag = tag
                };

                if (el.TryGetProperty("spec", out var spec))
                {
                    if (spec.TryGetProperty("video_codec", out var vc)) preset.Codec = vc.GetString() ?? "libx264";
                    if (spec.TryGetProperty("crf", out var crf) && crf.ValueKind == JsonValueKind.Number) preset.Crf = crf.GetDouble();
                    if (spec.TryGetProperty("audio_codec", out var ac)) preset.AudioCodec = ac.GetString() ?? "aac";
                    if (spec.TryGetProperty("audio_bitrate", out var ab) && ab.ValueKind == JsonValueKind.Number) preset.AudioBitrate = $"{ab.GetInt32()}k";
                    preset.Container = spec.TryGetProperty("container", out var container) ? container.GetString() ?? "mp4" : "mp4";
                    preset.EncoderPreset = spec.TryGetProperty("preset", out var speed) ? speed.GetString() : null;
                    preset.Width = spec.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    preset.Height = spec.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    var displayHeight = preset.Height > 0 ? preset.Height : preset.Width switch { 1920 => 1080, 1280 => 720, 854 => 480, _ => 0 };
                    preset.ResolutionLimit = displayHeight > 0 ? $"{displayHeight}p" : "Source Native";
                    preset.Framerate = spec.TryGetProperty("fps", out var fps) && fps.GetDouble() > 0 ? fps.GetDouble().ToString(CultureInfo.InvariantCulture) : "source";
                    preset.Deinterlace = spec.TryGetProperty("deinterlace", out var deint) && deint.GetBoolean();
                    preset.DenoiseFilter = spec.TryGetProperty("denoise", out var denoise) ? denoise.GetString() ?? "hqdn3d" : "hqdn3d";
                    preset.Denoise = !string.IsNullOrEmpty(preset.DenoiseFilter) && spec.TryGetProperty("denoise", out _);
                    preset.Tune = spec.TryGetProperty("tune", out var tune) ? tune.GetString() ?? "" : "";
                    preset.AudioChannels = spec.TryGetProperty("audio_channels", out var channels) ? channels.GetInt32() : 0;
                    preset.Grayscale = spec.TryGetProperty("grayscale", out var grayscale) && grayscale.GetBoolean();
                }

                result.Add(preset);
            }

            return result;
        }
        catch
        {
            return new List<TranscodePreset>();
        }
    }

    public async Task<IReadOnlyList<TranscodeJob>> GetJobsAsync()
    {
        if (!_isConnected) return new List<TranscodeJob>();

        try
        {
            var connectionToken = _serviceCts?.Token ?? CancellationToken.None;
            var url = $"{_baseUrl}/api/v1/jobs";
            using var req = CreateRequest(HttpMethod.Get, url);
            using var resp = await SendControlAsync(req);
            if (!resp.IsSuccessStatusCode) return new List<TranscodeJob>();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var result = new List<TranscodeJob>();

            connectionToken.ThrowIfCancellationRequested();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var job = MapJobDetail(el);
                if (_hiddenJobs.ContainsKey(job.ServerJobId)) continue;
                result.Add(job);
                _jobs[job.Id] = job;

                // If job is encoding or queued, ensure SSE stream is listening
                if (job.Status == JobStatus.Encoding || job.Status == JobStatus.Queued || job.Status == JobStatus.Paused)
                {
                    ListenJobEvents(job.ServerJobId, job.Id);
                }
            }

            return result;
        }
        catch
        {
            return new List<TranscodeJob>();
        }
    }

    public async Task<TranscodeJob> AddJobAsync(string name, string meta, string presetKey, string? filePath = null, TranscodePreset? options = null)
    {
        if (!_isConnected) throw new InvalidOperationException("Connect to a server before submitting media.");
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Select an existing local media file.", filePath);
        var connectionToken = _serviceCts?.Token ?? CancellationToken.None;
        using var source = File.OpenRead(filePath);
        if (source.Length == 0) throw new InvalidOperationException("The selected file is empty.");
        using var req = CreateRequest(HttpMethod.Post, $"{_baseUrl}/api/v1/jobs");
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            filename = Path.GetFileName(filePath),
            file_size = source.Length,
            preset_key = options == null ? presetKey : "",
            spec = options == null ? new Dictionary<string, object>() : BuildSpec(options)
        }), Encoding.UTF8, "application/json");
        using var resp = await SendControlAsync(req);
        await EnsureSuccessAsync(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        connectionToken.ThrowIfCancellationRequested();
        var job = MapJobDetail(doc.RootElement);
        job.Meta = meta;
        job.PresetKey = presetKey;
        job.Options = options?.Clone();
        PublishJob(job);
        bool uploadAccepted = false;
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        uploadCts.CancelAfter(TimeSpan.FromMinutes(30));
        _activeUploadCts[job.ServerJobId] = uploadCts;
        try
        {
            using var uploadReq = CreateRequest(HttpMethod.Post, $"{_baseUrl}/api/v1/jobs/{job.ServerJobId}/upload");
            uploadReq.Content = new ProgressStreamContent(source, 65536, (sent, total) =>
            {
                if (total > 0)
                {
                    job.Progress = Math.Min(99.0, (double)sent / total * 100.0);
                    job.Status = JobStatus.Uploading;
                    PublishJob(job);
                }
            });
            using var uploadResp = await _httpClient.SendAsync(uploadReq, uploadCts.Token);
            // The server queues automatically after verification. A full queue retains the upload for Start retry.
            if (uploadResp.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                await EnsureSuccessAsync(uploadResp);
            uploadAccepted = true;
            job = await RefreshJobAsync(job.ServerJobId);
            ListenJobEvents(job.ServerJobId, job.Id);
            return job;
        }
        catch (Exception ex)
        {
            if (connectionToken.IsCancellationRequested) throw;
            if (uploadAccepted)
            {
                ListenJobEvents(job.ServerJobId, job.Id);
                throw;
            }
            await CancelJobAsync(job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            PublishJob(job);
            throw;
        }
        finally
        {
            _activeUploadCts.TryRemove(job.ServerJobId, out _);
        }
    }

    private static Dictionary<string, object> BuildSpec(TranscodePreset p)
    {
        var heights = new[] { 1080, 720, 480 };
        int height = p.Height;
        int width = p.Width;
        if (width == 0 && height == 0)
            height = heights.FirstOrDefault(h => p.ResolutionLimit.StartsWith($"{h}p", StringComparison.Ordinal));
        var speeds = new[] { "ultrafast", "superfast", "veryfast", "faster", "medium", "slow", "slower", "veryslow", "veryslow" };
        string speed = p.EncoderPreset ?? (p.Codec is "libx264" or "libx265" ? speeds[Math.Clamp(p.SpeedIndex, 0, 8)] : "");
        return new Dictionary<string, object>
        {
            ["video_codec"] = p.Codec,
            ["crf"] = (int)Math.Round(p.Crf),
            ["container"] = p.Container,
            ["preset"] = speed,
            ["audio_codec"] = p.AudioCodec,
            ["audio_bitrate"] = p.AudioCodec is "copy" or "none" ? 0 : int.Parse(p.AudioBitrate.TrimEnd('k', 'K'), CultureInfo.InvariantCulture),
            ["width"] = width,
            ["height"] = height,
            ["fps"] = double.TryParse(p.Framerate, CultureInfo.InvariantCulture, out var fps) ? fps : 0,
            ["deinterlace"] = p.Deinterlace,
            ["denoise"] = p.Denoise ? p.DenoiseFilter : "",
            ["tune"] = p.Tune,
            ["audio_channels"] = p.AudioChannels,
            ["grayscale"] = p.Grayscale,
            ["pixel_format"] = p.PixelFormat,
            ["crop_top"] = p.CropTop,
            ["crop_bottom"] = p.CropBottom,
            ["crop_left"] = p.CropLeft,
            ["crop_right"] = p.CropRight
        };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private void PublishJob(TranscodeJob job)
    {
        _jobs[job.Id] = job;
        if (!_hiddenJobs.ContainsKey(job.ServerJobId)) JobUpdated?.Invoke(this, job);
    }

    private async Task<TranscodeJob> RefreshJobAsync(string serverId)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{_baseUrl}/api/v1/jobs/{serverId}");
        using var resp = await SendControlAsync(req);
        await EnsureSuccessAsync(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var job = MapJobDetail(doc.RootElement);
        PublishJob(job);
        return job;
    }

    public async Task<bool> StartJobAsync(Guid jobId)
    {
        if (!_guidToServerId.TryGetValue(jobId, out var serverId)) return false;
        try
        {
            var action = _jobs.TryGetValue(jobId, out var current) && current.Status == JobStatus.Paused ? "resume" : "start";
            var url = $"{_baseUrl}/api/v1/jobs/{serverId}/{action}";
            using var req = CreateRequest(HttpMethod.Post, url);
            using var resp = await SendControlAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                await RefreshJobAsync(serverId);
                ListenJobEvents(serverId, jobId);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PauseJobAsync(Guid jobId)
    {
        if (!_guidToServerId.TryGetValue(jobId, out var serverId)) return false;
        try
        {
            var url = $"{_baseUrl}/api/v1/jobs/{serverId}/pause";
            using var req = CreateRequest(HttpMethod.Post, url);
            using var resp = await SendControlAsync(req);
            if (resp.IsSuccessStatusCode) await RefreshJobAsync(serverId);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ResumeJobAsync(Guid jobId)
    {
        if (!_guidToServerId.TryGetValue(jobId, out var serverId)) return false;
        try
        {
            var url = $"{_baseUrl}/api/v1/jobs/{serverId}/resume";
            using var req = CreateRequest(HttpMethod.Post, url);
            using var resp = await SendControlAsync(req);
            if (resp.IsSuccessStatusCode) await RefreshJobAsync(serverId);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        if (!_guidToServerId.TryGetValue(jobId, out var serverId)) return false;
        try
        {
            if (_activeUploadCts.TryRemove(serverId, out var uploadCts))
            {
                uploadCts.Cancel();
                uploadCts.Dispose();
            }

            var url = $"{_baseUrl}/api/v1/jobs/{serverId}";
            using var req = CreateRequest(HttpMethod.Delete, url);
            using var resp = await SendControlAsync(req);

            if (_activeSseStreams.TryRemove(serverId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Cancelled;
                PublishJob(job);
            }

            if (resp.IsSuccessStatusCode) await RefreshJobAsync(serverId);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Cancelled;
                PublishJob(job);
            }
            return false;
        }
    }

    public Task ClearCompletedJobsAsync()
    {
        foreach (var job in _jobs.Values.Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
            _hiddenJobs[job.ServerJobId] = true;
        return Task.CompletedTask;
    }

    public async Task DownloadJobAsync(Guid jobId, string destinationPath)
    {
        var url = await GetDownloadUrlAsync(jobId);
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("The job is no longer available on this server.");
        // A sibling temporary file preserves an existing destination if the transfer fails.
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".part";
        _jobs.TryGetValue(jobId, out var job);
        try
        {
            using var req = CreateRequest(HttpMethod.Get, url);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(resp);
            var contentLength = resp.Content.Headers.ContentLength ?? 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var stream = await resp.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    totalRead += read;
                    if (job != null && contentLength > 0)
                    {
                        job.Progress = Math.Min(100.0, (double)totalRead / contentLength * 100.0);
                        job.Status = JobStatus.Downloading;
                        PublishJob(job);
                    }
                }
            }
            File.Move(temporaryPath, destinationPath, true);
            if (job != null)
            {
                job.Progress = 100.0;
                job.Status = JobStatus.Completed;
                PublishJob(job);
            }
        }
        catch
        {
            if (job != null)
            {
                job.Status = JobStatus.Completed;
                PublishJob(job);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task<string> GetDownloadUrlAsync(Guid jobId)
    {
        if (_guidToServerId.TryGetValue(jobId, out var serverId))
        {
            return Task.FromResult($"{_baseUrl}/api/v1/jobs/{serverId}/download");
        }
        return Task.FromResult(string.Empty);
    }

    private void ListenJobEvents(string serverId, Guid localGuid)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts?.Token ?? CancellationToken.None);
        if (!_activeSseStreams.TryAdd(serverId, cts)) { cts.Dispose(); return; }

        _ = Task.Run(async () =>
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/jobs/{serverId}/events";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return;

                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? currentEvent = null;
                var currentData = new StringBuilder();

                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line == null) break;

                    if (line.StartsWith("event:"))
                    {
                        currentEvent = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:"))
                    {
                        if (currentData.Length > 0) currentData.Append('\n');
                        currentData.Append(line.Substring(5).Trim());
                    }
                    else if (string.IsNullOrWhiteSpace(line))
                    {
                        // Dispatch event
                        if (!string.IsNullOrEmpty(currentEvent) && currentData.Length > 0)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            HandleSseEvent(currentEvent, currentData.ToString(), localGuid, serverId);
                        }
                        currentEvent = null;
                        currentData.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancel
            }
            catch
            {
                // Disconnect
            }
            finally
            {
                ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_activeSseStreams).Remove(new(serverId, cts));
                cts.Dispose();
            }
        });
    }

    private void HandleSseEvent(string eventType, string data, Guid localGuid, string serverId)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            switch (eventType)
            {
                case "status":
                case "complete":
                case "error":
                case "cancelled":
                    var statusJob = MapJobDetail(root, localGuid);
                    PublishJob(statusJob);
                    if (!string.IsNullOrEmpty(statusJob.Error)) StdoutLineReceived?.Invoke(this, statusJob.Error);
                    break;

                case "progress":
                    var metrics = new TelemetryMetrics
                    {
                        Fps = root.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 0,
                        SpeedFactor = ParseSpeed(root.TryGetProperty("speed", out var sp) ? sp.GetString() : null),
                        BitrateKbps = root.TryGetProperty("bitrate", out var br) ? (int)Math.Round(br.GetDouble()) : 0,
                        CurrentFrame = root.TryGetProperty("frame", out var fr) ? fr.GetInt64() : 0,
                        StatusText = "ENCODING"
                    };

                    double pct = root.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0;
                    if (root.TryGetProperty("eta", out var etaProp) && etaProp.GetString() is string etaStr)
                    {
                        if (TimeSpan.TryParse(etaStr, out var parsedEta)) metrics.Eta = parsedEta;
                    }

                    metrics.ProgressPercent = pct;
                    TelemetryUpdated?.Invoke(this, metrics);

                    if (_jobs.TryGetValue(localGuid, out var previous))
                    {
                        var updatedJob = previous.Clone();
                        updatedJob.Progress = pct;
                        updatedJob.Fps = metrics.Fps;
                        updatedJob.Speed = metrics.SpeedFactor;
                        updatedJob.BitrateKbps = metrics.BitrateKbps;
                        updatedJob.EtaText = metrics.Eta > TimeSpan.Zero ? metrics.Eta.ToString(@"hh\:mm\:ss") : null;
                        PublishJob(updatedJob);
                    }
                    break;

                case "log":
                    if (root.TryGetProperty("line", out var lineProp) && lineProp.GetString() is string line)
                    {
                        StdoutLineReceived?.Invoke(this, line);
                    }
                    break;

            }
        }
        catch
        {
            // Ignore parse errors in event stream
        }
    }

    private static double ParseSpeed(string? speedStr)
    {
        if (string.IsNullOrEmpty(speedStr)) return 1.0;
        speedStr = speedStr.TrimEnd('x', 'X');
        return double.TryParse(speedStr, CultureInfo.InvariantCulture, out var v) ? v : 1.0;
    }

    private TranscodeJob MapJobDetail(JsonElement root, Guid? existingGuid = null)
    {
        var serverId = root.TryGetProperty("job_id", out var jid) ? jid.GetString() ?? "" : "";
        var filename = root.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
        var statusStr = root.TryGetProperty("status", out var st) ? st.GetString() ?? "idle" : "idle";

        var guid = existingGuid ?? _serverIdToGuid.GetOrAdd(serverId, _ => Guid.NewGuid());
        _guidToServerId[guid] = serverId;

        JobStatus status = statusStr switch
        {
            "encoding" => JobStatus.Encoding,
            "queued" => JobStatus.Queued,
            "paused" => JobStatus.Paused,
            "completed" => JobStatus.Completed,
            "failed" => JobStatus.Failed,
            "cancelled" => JobStatus.Cancelled,
            "awaiting_upload" => root.TryGetProperty("file_size", out var size) && root.TryGetProperty("upload_offset", out var offset) && size.GetInt64() == offset.GetInt64() ? JobStatus.Idle : JobStatus.Uploading,
            _ => JobStatus.Idle
        };

        double progress = 0.0;
        if (root.TryGetProperty("progress", out var progProp) && progProp.ValueKind == JsonValueKind.Object && progProp.TryGetProperty("progress", out var pVal))
        {
            progress = pVal.GetDouble();
        }

        _jobs.TryGetValue(guid, out var previous);
        return new TranscodeJob
        {
            Id = guid,
            PresetKey = previous?.PresetKey ?? "",
            Meta = previous?.Meta ?? "",
            Options = previous?.Options,
            Container = root.TryGetProperty("spec", out var spec) && spec.TryGetProperty("container", out var container) ? container.GetString() ?? "mp4" : previous?.Container ?? "mp4",
            Error = root.TryGetProperty("error", out var error) ? error.GetString() : null,
            ServerJobId = serverId,
            Name = filename,
            Status = status,
            Progress = status == JobStatus.Completed ? 100 : progress,
            Fps = progProp.ValueKind == JsonValueKind.Object && progProp.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 0,
            Speed = progProp.ValueKind == JsonValueKind.Object && progProp.TryGetProperty("speed", out var speed) ? ParseSpeed(speed.GetString()) : 0,
            BitrateKbps = progProp.ValueKind == JsonValueKind.Object && progProp.TryGetProperty("bitrate", out var bitrate) ? (int)Math.Round(bitrate.GetDouble()) : 0,
            EtaText = progProp.ValueKind == JsonValueKind.Object && progProp.TryGetProperty("eta", out var eta) ? eta.GetString() : null,
            DownloadUrl = status == JobStatus.Completed ? $"{_baseUrl}/api/v1/jobs/{serverId}/download" : null
        };
    }

    private async Task StartActiveJobsSyncAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isConnected)
        {
            try
            {
                await Task.Delay(5000, token);
                var jobs = await GetJobsAsync();
                token.ThrowIfCancellationRequested();
                foreach (var job in jobs) PublishJob(job);
            }
            catch
            {
                // Continue background sync
            }
        }
    }

    private async Task<HttpResponseMessage> SendControlAsync(HttpRequestMessage request)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts?.Token ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await _httpClient.SendAsync(request, timeout.Token);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        return req;
    }

    public void Dispose()
    {
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        foreach (var cts in _activeSseStreams.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeSseStreams.Clear();
        foreach (var cts in _activeUploadCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeUploadCts.Clear();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly int _bufferSize;
        private readonly Action<long, long> _onProgress;

        public ProgressStreamContent(Stream stream, int bufferSize, Action<long, long> onProgress)
        {
            _stream = stream;
            _bufferSize = bufferSize;
            _onProgress = onProgress;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[_bufferSize];
            long totalBytes = _stream.Length;
            long bytesSent = 0;
            var lastReport = System.Diagnostics.Stopwatch.StartNew();
            int read;
            while ((read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesSent += read;
                if (lastReport.ElapsedMilliseconds >= 100 || bytesSent == totalBytes)
                {
                    _onProgress(bytesSent, totalBytes);
                    lastReport.Restart();
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.Length;
            return true;
        }
    }
}
