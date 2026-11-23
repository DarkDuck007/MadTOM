using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfLogLineItem(string Timestamp, string Level, string Unit, string Message);

public sealed record SfApiCallItem(string Timestamp, string JobId, string Kind, string Name, double Seconds, bool Ok, string Summary)
{
    public string StatusBadge => Ok ? "OK" : "ERR";
    public string DurationDisplay => $"{Seconds:F2}s";
}

public sealed partial class SfLogsViewModel : ObservableObject
{
    public const int MaxLogEntries = 5000;
    public const int MaxApiEntries = 2000;

    private readonly List<SfLogLineItem> _allLogs = new();

    [ObservableProperty] private string _levelFilter = "All";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _totalLogCount;
    [ObservableProperty] private int _totalApiCount;

    public ObservableCollection<SfLogLineItem> FilteredLogs { get; } = new();
    public ObservableCollection<SfApiCallItem> ApiCalls { get; } = new();

    public IReadOnlyList<string> LevelOptions { get; } = new[] { "All", "ERROR", "WARN", "INFO", "DEBUG" };

    partial void OnLevelFilterChanged(string value) => RebuildFilteredLogs();
    partial void OnSearchTextChanged(string value) => RebuildFilteredLogs();

    public void ApplyLogRecord(LogRecord rec)
    {
        if (rec is null) return;
        string ts = FormatTimestamp(rec.TimestampUnixNano);
        string level = MapPriority(rec.Priority);
        var item = new SfLogLineItem(ts, level, rec.Unit ?? string.Empty, rec.Message ?? string.Empty);

        _allLogs.Add(item);
        if (_allLogs.Count > MaxLogEntries)
        {
            _allLogs.RemoveAt(0);
        }
        TotalLogCount = _allLogs.Count;

        if (MatchesFilter(item))
        {
            FilteredLogs.Add(item);
            if (FilteredLogs.Count > MaxLogEntries)
                FilteredLogs.RemoveAt(0);
        }
    }

    public void ApplyApiCall(SfApiCallRecord rec)
    {
        if (rec is null) return;
        var item = new SfApiCallItem(
            Timestamp: FormatTimestamp(rec.TimestampUnixNano),
            JobId: rec.JobId ?? string.Empty,
            Kind: rec.Kind ?? string.Empty,
            Name: rec.Name ?? string.Empty,
            Seconds: rec.Seconds,
            Ok: rec.Ok,
            Summary: rec.Json ?? string.Empty);

        ApiCalls.Insert(0, item);
        if (ApiCalls.Count > MaxApiEntries)
            ApiCalls.RemoveAt(ApiCalls.Count - 1);
        TotalApiCount = ApiCalls.Count;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        _allLogs.Clear();
        FilteredLogs.Clear();
        TotalLogCount = 0;
    }

    [RelayCommand]
    public void ClearApiCalls()
    {
        ApiCalls.Clear();
        TotalApiCount = 0;
    }

    private void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (var item in _allLogs)
        {
            if (MatchesFilter(item))
                FilteredLogs.Add(item);
        }
    }

    private bool MatchesFilter(SfLogLineItem item)
    {
        if (!string.IsNullOrWhiteSpace(LevelFilter) &&
            !string.Equals(LevelFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(item.Level, LevelFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            if (!item.Message.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Unit.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string MapPriority(uint priority) => priority switch
    {
        <= 3 => "ERROR",
        4 => "WARN",
        5 or 6 => "INFO",
        _ => "DEBUG",
    };

    private static string FormatTimestamp(long unixNano)
    {
        if (unixNano <= 0)
            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        long ms = unixNano / 1_000_000L;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
