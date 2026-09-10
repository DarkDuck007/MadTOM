using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MadTOM.Models;

/// <summary>
/// Represents a single metric series rendered on a chart control.
/// Supports multiple series per graph for aggregated / merged views.
/// </summary>
public partial class ChartSeriesModel : ObservableObject
{
    [ObservableProperty]
    private string _metric = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _colorHex = "#06B6D4";

    partial void OnColorHexChanged(string value)
    {
        _linePen = null;
        _fillBrush = null;
        _solidBrush = null;
        OnPropertyChanged(nameof(ResolvedColor));
        OnPropertyChanged(nameof(LinePen));
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(SolidBrush));
    }

    [ObservableProperty]
    private double[] _values = Array.Empty<double>();

    [ObservableProperty]
    private long[] _timestamps = Array.Empty<long>();

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private double _latestValue;

    public ChartSeriesModel() { }

    public ChartSeriesModel(string metric, string label, string colorHex)
    {
        _metric = metric;
        _label = label;
        _colorHex = colorHex;
    }

    public Color ResolvedColor
    {
        get
        {
            if (Color.TryParse(ColorHex, out var parsed))
                return parsed;
            return Color.FromRgb(6, 182, 212);
        }
    }

    public event Action? ConfigurationChanged;

    [RelayCommand]
    public void ChangeColor(string hex)
    {
        ColorHex = hex;
        ConfigurationChanged?.Invoke();
    }

    private IPen? _linePen;
    private IBrush? _fillBrush;
    private IBrush? _solidBrush;

    public IPen LinePen => _linePen ??= new ImmutablePen(new ImmutableSolidColorBrush(ResolvedColor), 2.0);

    public IBrush FillBrush => _fillBrush ??= new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0.0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1.0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(55, ResolvedColor.R, ResolvedColor.G, ResolvedColor.B), 0.0),
            new GradientStop(Color.FromArgb(2, ResolvedColor.R, ResolvedColor.G, ResolvedColor.B), 1.0)
        }
    };

    public IBrush SolidBrush => _solidBrush ??= new ImmutableSolidColorBrush(ResolvedColor);
}
