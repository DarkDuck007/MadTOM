using System;

namespace MadTOM.Services;

public record struct LODPoint(long TimestampUnixNano, double Value, double Min, double Max);

/// <summary>
/// Aggregates incoming 1Hz telemetry samples into resolution-adaptive LOD buckets
/// so that live charts remain smoothly updated when historical scopes (e.g. 5m, 1h)
/// have an end date of NOW.
/// </summary>
public sealed class LiveLODBucketAccumulator
{
    private readonly object _lock = new();
    private TimeSpan _scopeDuration;
    private int _targetPoints;
    private int _bucketSize; // Number of 1s samples to accumulate per bucket

    private int _currentCount;
    private double _runningSum;
    private double _minVal = double.MaxValue;
    private double _maxVal = double.MinValue;
    private long _lastTimestamp;

    public event EventHandler<LODPoint>? BucketCompleted;

    public int BucketSize => _bucketSize;

    public LiveLODBucketAccumulator(TimeSpan scopeDuration, int targetPoints = 60)
    {
        UpdateScope(scopeDuration, targetPoints);
    }

    public void UpdateScope(TimeSpan scopeDuration, int targetPoints = 60)
    {
        lock (_lock)
        {
            _scopeDuration = scopeDuration;
            _targetPoints = Math.Max(2, targetPoints);
            
            // e.g. 5 minutes (300s) / 60 points = 5 samples per bucket
            double secondsPerPoint = _scopeDuration.TotalSeconds / _targetPoints;
            _bucketSize = Math.Max(1, (int)Math.Round(secondsPerPoint));

            ResetBucket();
        }
    }

    public void AddSample(double value, long timestampNano)
    {
        LODPoint? completedPoint = null;

        lock (_lock)
        {
            _currentCount++;
            _runningSum += value;
            _lastTimestamp = timestampNano;
            if (value < _minVal) _minVal = value;
            if (value > _maxVal) _maxVal = value;

            if (_currentCount >= _bucketSize)
            {
                double avg = _runningSum / _currentCount;
                completedPoint = new LODPoint(_lastTimestamp, avg, _minVal, _maxVal);
                ResetBucket();
            }
        }

        if (completedPoint.HasValue)
        {
            BucketCompleted?.Invoke(this, completedPoint.Value);
        }
    }

    private void ResetBucket()
    {
        _currentCount = 0;
        _runningSum = 0;
        _minVal = double.MaxValue;
        _maxVal = double.MinValue;
    }
}

