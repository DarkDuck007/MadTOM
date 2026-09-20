using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MadTOM.Controls;

namespace MadTOM.Tests;

public class PerformanceSpinnerTests
{
    [Fact]
    public void CompactSpinnerStacksButtonsAndPreservesNativeSpinEvents()
    {
        var styles = new PerformanceNumberStyles();
        var style = styles.OfType<Style>().Single(s =>
            s.Selector?.ToString() == "NumericUpDown.performance-number /template/ ButtonSpinner");
        var template = (IControlTemplate)style.Setters.OfType<Setter>()
            .Single(s => s.Property == TemplatedControl.TemplateProperty).Value!;
        var spinner = new ButtonSpinner { Template = template };
        spinner.ApplyTemplate();
        var buttons = spinner.GetVisualDescendants().OfType<RepeatButton>().ToArray();
        var increase = Assert.Single(buttons, b => b.Name == "PART_IncreaseButton");
        var decrease = Assert.Single(buttons, b => b.Name == "PART_DecreaseButton");
        Assert.Same(increase.Parent, decrease.Parent);
        Assert.Equal(0, Grid.GetRow(increase));
        Assert.Equal(1, Grid.GetRow(decrease));
        var directions = new List<SpinDirection>();
        spinner.Spin += (_, e) => directions.Add(e.Direction);
        increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        decrease.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { SpinDirection.Increase, SpinDirection.Decrease }, directions);
    }
}
