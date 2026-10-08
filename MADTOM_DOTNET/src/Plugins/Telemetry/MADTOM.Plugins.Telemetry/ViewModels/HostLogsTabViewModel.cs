using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels;

public partial class HostLogsTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private CancellationTokenSource? _streamCts;
    private string _targetHostId = string.Empty;
    [ObservableProperty]
    private string _collectorEndpoint = string.Empty;

    [ObservableProperty]
    private string _hostId = string.Empty;

    [ObservableProperty]
    private TelemetryOptInMode _logMode = TelemetryOptInMode.OptInOff;

    [ObservableProperty]
    private ulong _totalRecords;

    [ObservableProperty]
    private ulong _firstSeq;

    [ObservableProperty]
    private ulong _lastSeq;

    [ObservableProperty]
    private bool _isFollowMode = true;

    [ObservableProperty]
    private bool _hasNewLogs;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _pauseButtonText = "Pause Stream";

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _statusBadgeText = "Off";

    [ObservableProperty]
    private string _statsSummary = "";

    [ObservableProperty]
    private int _cacheRevision;

    public LogChunkCache? LogCache => _telemetryProvider.LogCache;
    public Func<ulong, bool> IsChunkUnavailablePredicate => IsChunkUnavailable;

    public HostLogsTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;
        _telemetryProvider.NodeConfigUpdated += OnNodeConfigUpdated;
    }

    private void OnNodeConfigUpdated(string nodeId, NodeConfig cfg)
    {
        if (string.Equals(_targetHostId, nodeId, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() =>
            {
                ApplyLogConfig(cfg);
            });
        }
    }

    private async void ApplyLogConfig(NodeConfig cfg)
    {
        var oldMode = LogMode;
        LogMode = cfg.LogMode;
        StatusBadgeText = LogMode switch
        {
            TelemetryOptInMode.OptInOff => "Off",
            TelemetryOptInMode.OptInMonitorOnly => "Live Monitor Only",
            TelemetryOptInMode.OptInMonitorAndStore => "Stored (TSDB)",
            _ => "Unknown"
        };

        if (LogMode == TelemetryOptInMode.OptInOff)
        {
            _streamCts?.Cancel();
            _streamCts?.Dispose();
            _streamCts = null;
            StatusText = "Log collection is turned off for this node. Enable under Node Opt-in.";
            FirstSeq = 0;
            LastSeq = 0;
            TotalRecords = 0;
            StatsSummary = "";
            return;
        }

        if (LogMode == TelemetryOptInMode.OptInMonitorAndStore)
        {
            var stats = await _telemetryProvider.GetLogStatsAsync(_targetHostId);
            if (stats != null && stats.TotalRecords > 0)
            {
                FirstSeq = stats.FirstSeq;
                LastSeq = stats.LastSeq;
                TotalRecords = stats.TotalRecords;
                StatsSummary = $"{stats.TotalRecords:N0} records · {stats.TotalBytes / 1024.0:F1} KB";
            }
            else
            {
                FirstSeq = 0;
                LastSeq = 0;
                TotalRecords = 0;
                StatsSummary = "Awaiting stored records";
            }
        }
        else if (LogMode == TelemetryOptInMode.OptInMonitorOnly)
        {
            if (_telemetryProvider.LogCache?.TryGetNodeSequenceRange(_targetHostId, out var minSeq, out var maxSeq) == true)
            {
                FirstSeq = minSeq;
                LastSeq = maxSeq;
                TotalRecords = LastSeq >= FirstSeq ? (LastSeq - FirstSeq + 1) : 0;
                StatsSummary = $"{TotalRecords:N0} live records | {_telemetryProvider.LogCache.TotalCompressedBytes / 1024.0:F1} KB cache";
            }
            else
            {
                FirstSeq = 0;
                LastSeq = 0;
                TotalRecords = 0;
                StatsSummary = "Live ring buffer";
            }
        }

        if (_streamCts == null)
        {
            StartLogStream(_targetHostId);
        }
    }

    public async void SetTargetHost(string hostId, string collectorEndpoint, bool forceRefresh = false)
    {
        if (!forceRefresh &&
            string.Equals(_targetHostId, hostId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CollectorEndpoint, collectorEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;

        lock (_inFlightChunks)
        {
            _inFlightChunks.Clear();
            _chunkFetchFailures.Clear();
        }

        _targetHostId = hostId ?? string.Empty;
        CollectorEndpoint = collectorEndpoint ?? string.Empty;
        HostId = _targetHostId;
        HasNewLogs = false;

        if (string.IsNullOrEmpty(hostId) || hostId.Equals("aggregated", StringComparison.OrdinalIgnoreCase) || hostId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            LogMode = TelemetryOptInMode.OptInOff;
            StatusBadgeText = "Aggregated Unavailable";
            StatusText = "Log streaming is only available on individual hosts.";
            TotalRecords = 0;
            FirstSeq = 0;
            LastSeq = 0;
            StatsSummary = "";
            return;
        }

        StatusText = "Inspecting node log policy...";
        var cfg = await _telemetryProvider.GetNodeConfigAsync(hostId);
        LogMode = cfg?.LogMode ?? TelemetryOptInMode.OptInOff;

        StatusBadgeText = LogMode switch
        {
            TelemetryOptInMode.OptInOff => "Off",
            TelemetryOptInMode.OptInMonitorOnly => "Live Monitor Only",
            TelemetryOptInMode.OptInMonitorAndStore => "Stored (TSDB)",
            _ => "Unknown"
        };

        if (LogMode == TelemetryOptInMode.OptInOff)
        {
            StatusText = "Log collection is turned off for this node. Enable under Node Opt-in.";
            TotalRecords = 0;
            FirstSeq = 0;
            LastSeq = 0;
            StatsSummary = "";
            return;
        }

        // Fetch stats if stored or inspect cache for Monitor Only
        if (LogMode == TelemetryOptInMode.OptInMonitorAndStore)
        {
            var stats = await _telemetryProvider.GetLogStatsAsync(hostId);
            if (stats != null && stats.TotalRecords > 0)
            {
                FirstSeq = stats.FirstSeq;
                LastSeq = stats.LastSeq;
                TotalRecords = stats.TotalRecords;
                StatsSummary = $"{stats.TotalRecords:N0} records · {stats.TotalBytes / 1024.0:F1} KB";
            }
            else
            {
                FirstSeq = 0;
                LastSeq = 0;
                TotalRecords = 0;
                StatsSummary = "Awaiting stored records";
            }
        }
        else
        {
            if (_telemetryProvider.LogCache?.TryGetNodeSequenceRange(hostId, out var minSeq, out var maxSeq) == true)
            {
                FirstSeq = minSeq;
                LastSeq = maxSeq;
                TotalRecords = LastSeq >= FirstSeq ? (LastSeq - FirstSeq + 1) : 0;
                StatsSummary = $"{TotalRecords:N0} live records | {_telemetryProvider.LogCache.TotalCompressedBytes / 1024.0:F1} KB cache";
            }
            else
            {
                FirstSeq = 0;
                LastSeq = 0;
                TotalRecords = 0;
                StatsSummary = "Live ring buffer";
            }
        }

        StartLogStream(hostId);
    }

    private async void StartLogStream(string hostId)
    {
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;
        StatusText = "Connecting log stream...";

        try
        {
            await foreach (var chunk in _telemetryProvider.SubscribeLogsAsync(hostId, ct))
            {
                if (IsPaused) continue;

                _telemetryProvider.LogCache?.PutChunk(CollectorEndpoint, hostId, chunk);
                Console.WriteLine($"[MADTOM.Logs] Stream chunk: node={hostId}, chunk={chunk.ChunkId}, seq=[{chunk.FirstSeq}..{chunk.LastSeq}], count={chunk.RecordCount}, sealed={chunk.Sealed}");

                Dispatcher.UIThread.Post(() =>
                {
                    if (LogMode == TelemetryOptInMode.OptInMonitorOnly)
                    {
                        if (_telemetryProvider.LogCache?.TryGetNodeSequenceRange(_targetHostId, out var minSeq, out var maxSeq) == true)
                        {
                            FirstSeq = minSeq;
                            LastSeq = maxSeq;
                            TotalRecords = LastSeq >= FirstSeq ? (LastSeq - FirstSeq + 1) : 0;
                        }
                        else
                        {
                            FirstSeq = chunk.FirstSeq;
                            LastSeq = chunk.LastSeq;
                            TotalRecords = chunk.RecordCount;
                        }
                    }
                    else
                    {
                        if (FirstSeq == 0 || chunk.FirstSeq < FirstSeq)
                        {
                            FirstSeq = chunk.FirstSeq;
                        }
                        if (chunk.LastSeq > LastSeq)
                        {
                            LastSeq = chunk.LastSeq;
                        }

                        TotalRecords = LastSeq >= FirstSeq ? (LastSeq - FirstSeq + 1) : 0;
                    }

                    if (!IsFollowMode)
                    {
                        HasNewLogs = true;
                    }

                    CacheRevision++;
                    StatusText = "Streaming active";

                    if (_telemetryProvider.LogCache is { } cache)
                    {
                        StatsSummary = $"{TotalRecords:N0} records | {cache.TotalCompressedBytes / 1024.0:F1} KB cache";
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stream cancellation on host switch
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Stream disconnected: {ex.Message}";
            });
        }
    }

    private readonly System.Collections.Generic.HashSet<ulong> _inFlightChunks = new();
    private readonly System.Collections.Generic.Dictionary<ulong, DateTime> _chunkFetchFailures = new();
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(4);

    public async void HandleNeedChunk(ulong chunkId)
    {
        if (string.IsNullOrEmpty(_targetHostId) || LogMode != TelemetryOptInMode.OptInMonitorAndStore) return;

        lock (_inFlightChunks)
        {
            if (_inFlightChunks.Contains(chunkId)) return;
            if (_chunkFetchFailures.TryGetValue(chunkId, out var lastFail) && DateTime.UtcNow - lastFail < RetryCooldown)
            {
                return;
            }
            _inFlightChunks.Add(chunkId);
        }

        ulong queryStart = chunkId > 0 ? chunkId - 1 : 0;
        Console.WriteLine($"[MADTOM.Logs] NeedChunk triggered: node={_targetHostId}, requestedChunk={chunkId}, queryStart={queryStart}");

        try
        {
            var res = await _telemetryProvider.QueryLogChunksAsync(_targetHostId, queryStart, 6);
            bool hasChunks = false;
            if (res != null && res.Chunks != null && res.Chunks.Count > 0)
            {
                Console.WriteLine($"[MADTOM.Logs] QueryLogChunks success: node={_targetHostId}, returned {res.Chunks.Count} chunks");
                foreach (var chunk in res.Chunks)
                {
                    _telemetryProvider.LogCache?.PutChunk(CollectorEndpoint, _targetHostId, chunk);
                    hasChunks = true;
                }
            }
            else
            {
                Console.WriteLine($"[MADTOM.Logs] QueryLogChunks empty: node={_targetHostId}, no chunks for queryStart={queryStart}");
            }

            lock (_inFlightChunks)
            {
                _inFlightChunks.Remove(chunkId);
                if (!hasChunks)
                {
                    _chunkFetchFailures[chunkId] = DateTime.UtcNow;
                }
                else
                {
                    _chunkFetchFailures.Remove(chunkId);
                    // Also clear previous chunk failure if it was fetched in this window
                    if (chunkId > 0) _chunkFetchFailures.Remove(chunkId - 1);
                    _chunkFetchFailures.Remove(chunkId + 1);
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                CacheRevision++;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MADTOM.Logs] QueryLogChunks error: node={_targetHostId}, chunk={chunkId}, ex={ex.Message}");
            lock (_inFlightChunks)
            {
                _inFlightChunks.Remove(chunkId);
                _chunkFetchFailures[chunkId] = DateTime.UtcNow;
            }
            Dispatcher.UIThread.Post(() =>
            {
                CacheRevision++;
            });
        }
    }

    public bool IsChunkUnavailable(ulong chunkId)
    {
        lock (_inFlightChunks)
        {
            return _chunkFetchFailures.ContainsKey(chunkId);
        }
    }

    [RelayCommand]
    public void ScrollToLatest()
    {
        IsFollowMode = true;
        HasNewLogs = false;
    }

    [RelayCommand]
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        PauseButtonText = IsPaused ? "Resume Stream" : "Pause Stream";
        _telemetryProvider.PauseLogs(IsPaused);
        StatusText = IsPaused ? "Stream paused" : "Streaming active";
    }

    [RelayCommand]
    public void Clear()
    {
        lock (_inFlightChunks)
        {
            _inFlightChunks.Clear();
            _chunkFetchFailures.Clear();
        }
        _telemetryProvider.LogCache?.Clear();
        TotalRecords = 0;
        FirstSeq = 0;
        LastSeq = 0;
        HasNewLogs = false;
        CacheRevision++;
        StatusText = "Cache cleared";
    }
}
