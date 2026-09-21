using System;
using System.IO;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class GraphPerformanceSettings
{
    public static GraphPerformanceSettings Current { get; } = new();
    private readonly string _path;
    public double PointsPerPixel { get; private set; } = 1;
    public double HistoryPointsPerPixel { get; private set; } = 3;
    public double MaxZoomLevel => HistoryPointsPerPixel * 10.0;
    public event Action? Changed;

    public GraphPerformanceSettings(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "performance.json");
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path));
            double value = settings?.GraphPointsPerPixel ?? 1;
            double history = settings?.HistoryPointsPerPixel ?? 3;
            if (IsValidHistory(history)) HistoryPointsPerPixel = history;
            if (IsValid(value)) PointsPerPixel = value;
        }
        catch { /* Missing or invalid settings use the default. */ }
    }

    public static bool IsValid(double value) => double.IsFinite(value) && value is >= 0.1 and <= 2;

    public static bool IsValidHistory(double value) => double.IsFinite(value) && value is >= 1 and <= 10;

    public void Save(double value) => Save(value, HistoryPointsPerPixel);

    public void Save(double value, double history)
    {
        if (!IsValid(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!IsValidHistory(history)) throw new ArgumentOutOfRangeException(nameof(history));
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Settings { GraphPointsPerPixel = value, HistoryPointsPerPixel = history }));
        File.Move(_path + ".tmp", _path, true);
        PointsPerPixel = value;
        HistoryPointsPerPixel = history;
        Changed?.Invoke();
    }

    public sealed class Settings { public double GraphPointsPerPixel { get; set; } = 1; public double HistoryPointsPerPixel { get; set; } = 3; }
}
