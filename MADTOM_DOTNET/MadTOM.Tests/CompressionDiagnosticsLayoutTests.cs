using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MadTOM.Tests;

public class CompressionDiagnosticsLayoutTests
{
    [Theory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(380)]
    public void DiagnosticValueColumnsRemainInsideNarrowTooltip(double width)
    {
        // Exercise Avalonia's layout with the actual XAML column definitions. Fixed
        // 755px columns previously placed every statistic outside the tooltip.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Plugins")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "src", "Plugins", "Telemetry",
            "MADTOM.Plugins.Telemetry", "Views", "CompressionDiagnosticsView.axaml"));
        var valueGrid = document.Descendants().First(element => element.Name.LocalName == "Grid" &&
            element.Elements().Any(child => (string?)child.Attribute("Text") == "{Binding State}"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions((string)valueGrid.Attribute("ColumnDefinitions")!) };
        var cells = new List<Border>();
        foreach (var field in new[] { "State", "Raw", "Zstd", "Ratio", "Count" })
        {
            var element = valueGrid.Elements().Single(e => (string?)e.Attribute("Text") == "{Binding " + field + "}");
            var cell = new Border { Height = 12, MinWidth = 20 };
            Grid.SetColumn(cell, (int?)element.Attribute("Grid.Column") ?? 0);
            grid.Children.Add(cell);
            cells.Add(cell);
            Assert.Equal("Wrap", (string?)element.Attribute("TextWrapping"));
        }
        grid.Measure(new Size(width, 100));
        grid.Arrange(new Rect(0, 0, width, 100));
        Assert.All(cells, cell =>
        {
            Assert.True(cell.Bounds.Width >= 20);
            Assert.True(cell.Bounds.Right <= width + 0.01, $"Value ends at {cell.Bounds.Right}, tooltip width {width}");
        });
    }
}
