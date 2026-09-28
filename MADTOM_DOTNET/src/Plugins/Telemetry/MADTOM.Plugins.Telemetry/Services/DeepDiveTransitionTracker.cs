using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MadTOM.Services;

/// <summary>
/// End-to-end timing tracker from the moment an operator clicks a node until
/// all charts in the deep-dive view are fully populated and rendered.
/// Enabled via MADTOM_DEEPDIVE_TIMING=1 or MADTOM_UI_TIMING=1.
/// </summary>
public static class DeepDiveTransitionTracker
{
    private static bool _forceEnabledForTesting;
    public static bool Enabled => _forceEnabledForTesting ||
                                  Environment.GetEnvironmentVariable("MADTOM_DEEPDIVE_TIMING") == "1" ||
                                  UiPerformanceDiagnostics.Enabled;

    public static void SetEnabledForTesting(bool enabled) => _forceEnabledForTesting = enabled;
    public static Action<object>? OutputSink { get; set; }

    public sealed record TransitionRecord(
        string Kind,
        DateTime Utc,
        string TargetHost,
        string FromView,
        string Outcome,
        double TotalWallMs,
        double ViewSwitchMs,
        double HostSpecsMs,
        double LayoutSaveMs,
        double LayoutLoadMs,
        double PopulateMetricsMs,
        double DataQueryWallMs,
        int RpcCount,
        int CacheHits,
        double TransformMs,
        double BatchPublishMs,
        double RenderAllChartsMs,
        int ExpectedChartsCount,
        int RenderedChartsCount,
        Dictionary<string, double> Steps);

    private sealed class StepScope : IDisposable
    {
        private readonly TransitionSession _session;
        private readonly string _stepName;
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _disposed;

        public StepScope(TransitionSession session, string stepName)
        {
            _session = session;
            _stepName = stepName;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            double elapsed = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _session.AddStepDuration(_stepName, elapsed);
        }
    }

    private sealed class TransitionSession
    {
        public readonly string TargetHost;
        public readonly string FromView;
        public readonly long StartTimestamp = Stopwatch.GetTimestamp();
        private readonly object _gate = new();
        private readonly Dictionary<string, double> _stepDurations = new();
        private readonly HashSet<string> _expectedCharts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _renderedCharts = new(StringComparer.OrdinalIgnoreCase);
        private long _batchPublishEnd;
        private long _queryStart;
        private long _queryEnd;
        private int _rpcCount;
        private int _cacheHits;
        private double _transformMs;
        private double _batchPublishMs;
        private int _completed;
        private readonly Timer _timeoutTimer;

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public TransitionSession(string targetHost, string fromView)
        {
            TargetHost = targetHost;
            FromView = fromView;
            _timeoutTimer = new Timer(OnTimeout, null, 15000, Timeout.Infinite);
        }

        public void AddStepDuration(string stepName, double durationMs)
        {
            lock (_gate)
            {
                _stepDurations[stepName] = _stepDurations.GetValueOrDefault(stepName) + durationMs;
            }
        }

        public void MarkQueryStart()
        {
            lock (_gate)
            {
                _queryStart = Stopwatch.GetTimestamp();
            }
        }

        public void RecordQueryStats(int rpcCount, int cacheHits, double transformMs)
        {
            lock (_gate)
            {
                _rpcCount += rpcCount;
                _cacheHits += cacheHits;
                _transformMs += transformMs;
            }
        }

        public void MarkDataReady()
        {
            lock (_gate)
            {
                _queryEnd = Stopwatch.GetTimestamp();
            }
        }

        public void RecordBatchPublish(double durationMs)
        {
            lock (_gate)
            {
                _batchPublishMs = durationMs;
                _batchPublishEnd = Stopwatch.GetTimestamp();
            }
        }

        public void ExpectCharts(IEnumerable<string> chartTitles)
        {
            lock (_gate)
            {
                foreach (var title in chartTitles)
                {
                    if (!string.IsNullOrWhiteSpace(title))
                        _expectedCharts.Add(title);
                }
                // If no charts are expected (e.g. empty node with 0 graphs), complete now
                if (_expectedCharts.Count == 0)
                {
                    CompleteInternal("completed");
                }
            }
        }

        public void RecordChartRendered(string chartTitle)
        {
            lock (_gate)
            {
                if (IsCompleted) return;
                _renderedCharts.Add(chartTitle);

                // If all expected charts have rendered, finalize transition
                if (_expectedCharts.Count > 0 && _expectedCharts.IsSubsetOf(_renderedCharts))
                {
                    CompleteInternal("completed");
                }
            }
        }

        private void OnTimeout(object? state)
        {
            Complete("timeout");
        }

        public void Complete(string outcome)
        {
            lock (_gate)
            {
                CompleteInternal(outcome);
            }
        }

        private void CompleteInternal(string outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _timeoutTimer.Dispose();

            long endTimestamp = Stopwatch.GetTimestamp();
            double totalWallMs = Stopwatch.GetElapsedTime(StartTimestamp, endTimestamp).TotalMilliseconds;
            double dataQueryWallMs = _queryStart > 0 && _queryEnd > 0
                ? Math.Max(0, Stopwatch.GetElapsedTime(_queryStart, _queryEnd).TotalMilliseconds)
                : _stepDurations.GetValueOrDefault("data.query-wall");

            double renderAllChartsMs = _batchPublishEnd > 0
                ? Math.Max(0, Stopwatch.GetElapsedTime(_batchPublishEnd, endTimestamp).TotalMilliseconds)
                : 0;

            var record = new TransitionRecord(
                Kind: "deepdive-transition",
                Utc: DateTime.UtcNow,
                TargetHost: TargetHost,
                FromView: FromView,
                Outcome: outcome,
                TotalWallMs: Math.Round(totalWallMs, 2),
                ViewSwitchMs: Math.Round(_stepDurations.GetValueOrDefault("navigation.view-switch"), 2),
                HostSpecsMs: Math.Round(_stepDurations.GetValueOrDefault("host-detail.specs"), 2),
                LayoutSaveMs: Math.Round(_stepDurations.GetValueOrDefault("metrics.save-layout"), 2),
                LayoutLoadMs: Math.Round(_stepDurations.GetValueOrDefault("metrics.load-layout"), 2),
                PopulateMetricsMs: Math.Round(_stepDurations.GetValueOrDefault("metrics.populate-metrics"), 2),
                DataQueryWallMs: Math.Round(dataQueryWallMs, 2),
                RpcCount: _rpcCount,
                CacheHits: _cacheHits,
                TransformMs: Math.Round(_transformMs, 2),
                BatchPublishMs: Math.Round(_batchPublishMs, 2),
                RenderAllChartsMs: Math.Round(renderAllChartsMs, 2),
                ExpectedChartsCount: _expectedCharts.Count,
                RenderedChartsCount: _renderedCharts.Count,
                Steps: new Dictionary<string, double>(_stepDurations));

            Emit(record);
        }
    }

    private static TransitionSession? _currentSession;
    private static readonly object _sessionLock = new();

    public static bool IsActive
    {
        get
        {
            lock (_sessionLock)
            {
                return _currentSession != null && !_currentSession.IsCompleted;
            }
        }
    }

    public static void Begin(string targetHost, string fromView = "fleet")
    {
        if (!Enabled || string.IsNullOrWhiteSpace(targetHost)) return;

        lock (_sessionLock)
        {
            if (_currentSession != null && !_currentSession.IsCompleted)
            {
                _currentSession.Complete("superseded");
            }
            _currentSession = new TransitionSession(targetHost, fromView);
        }
    }

    public static IDisposable? MeasureStep(string stepName)
    {
        if (!Enabled) return null;
        lock (_sessionLock)
        {
            if (_currentSession == null || _currentSession.IsCompleted) return null;
            return new StepScope(_currentSession, stepName);
        }
    }

    public static void MarkStep(string stepName, double durationMs)
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            if (_currentSession != null && !_currentSession.IsCompleted)
            {
                _currentSession.AddStepDuration(stepName, durationMs);
            }
        }
    }

    public static void MarkQueryStart()
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            _currentSession?.MarkQueryStart();
        }
    }

    public static void RecordQueryStats(int rpcCount, int cacheHits, double transformMs)
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            _currentSession?.RecordQueryStats(rpcCount, cacheHits, transformMs);
        }
    }

    public static void MarkDataReady()
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            _currentSession?.MarkDataReady();
        }
    }

    public static void RecordBatchPublish(double durationMs)
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            _currentSession?.RecordBatchPublish(durationMs);
        }
    }

    public static void ExpectCharts(IEnumerable<string> chartTitles)
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            _currentSession?.ExpectCharts(chartTitles);
        }
    }

    public static void RecordChartRendered(string chartTitle)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(chartTitle)) return;
        lock (_sessionLock)
        {
            _currentSession?.RecordChartRendered(chartTitle);
        }
    }

    public static void Cancel(string reason = "cancelled")
    {
        if (!Enabled) return;
        lock (_sessionLock)
        {
            if (_currentSession != null && !_currentSession.IsCompleted)
            {
                _currentSession.Complete(reason);
            }
        }
    }

    private static void Emit(TransitionRecord record)
    {
        try
        {
            if (OutputSink != null)
            {
                OutputSink(record);
            }
            else
            {
                UiPerformanceDiagnostics.Write(record);
            }
        }
        catch
        {
            // Diagnostics should never crash the host
        }
    }
}

