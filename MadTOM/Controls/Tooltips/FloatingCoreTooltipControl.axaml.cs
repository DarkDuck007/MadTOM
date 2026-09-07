using Avalonia;
using Avalonia.Controls;
using MadTOM.Common.Utils;

namespace MadTOM.Controls.Tooltips;

public partial class FloatingCoreTooltipControl : UserControl
{
    public static readonly StyledProperty<string> HostIdProperty =
        AvaloniaProperty.Register<FloatingCoreTooltipControl, string>(nameof(HostId), string.Empty);

    public static readonly StyledProperty<int> ThreadIndexProperty =
        AvaloniaProperty.Register<FloatingCoreTooltipControl, int>(nameof(ThreadIndex), 0);

    public static readonly StyledProperty<float> LoadProperty =
        AvaloniaProperty.Register<FloatingCoreTooltipControl, float>(nameof(Load), 0.0f);

    public string HostId
    {
        get => GetValue(HostIdProperty);
        set => SetValue(HostIdProperty, value);
    }

    public int ThreadIndex
    {
        get => GetValue(ThreadIndexProperty);
        set => SetValue(ThreadIndexProperty, value);
    }

    public float Load
    {
        get => GetValue(LoadProperty);
        set => SetValue(LoadProperty, value);
    }

    public FloatingCoreTooltipControl()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HostIdProperty)
        {
            HostTextBlock.Text = change.GetNewValue<string>();
        }
        else if (change.Property == ThreadIndexProperty)
        {
            ThreadTextBlock.Text = $"Thread #{change.GetNewValue<int>()}";
        }
        else if (change.Property == LoadProperty)
        {
            float load = change.GetNewValue<float>();
            LoadTextBlock.Text = $"{load * 100.0:F1}%";
            LoadTextBlock.Foreground = ColorInterpolator.InterpolateLoadBrush(load);
        }
    }
}

