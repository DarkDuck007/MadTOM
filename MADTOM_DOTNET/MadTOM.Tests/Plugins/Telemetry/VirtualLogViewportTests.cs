using System.Reflection;
using Avalonia;
using Avalonia.Controls.Primitives;
using MadTOM.Controls.Logs;

namespace MadTOM.Tests.Plugins.Telemetry;

public class VirtualLogViewportTests
{
    private static ScrollBar Bar(VirtualLogViewControl view) =>
        (ScrollBar)typeof(VirtualLogViewControl).GetField("_scrollBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

    [Fact]
    public void InitialArrange_UsesNewViewport_AndKeepsNewestRowAtBottom()
    {
        var view = new VirtualLogViewControl { TotalRecords = 100, FirstSeq = 1, LastSeq = 100 };
        view.Measure(new Size(800, 360));
        view.Arrange(new Rect(0, 0, 800, 360));
        Assert.Equal(20, Bar(view).ViewportSize);
        Assert.Equal(80, Bar(view).Maximum);
        Assert.Equal(80, Bar(view).Value);
        Assert.Equal(100UL, view.FirstSeq + (ulong)Bar(view).Value + 19);
    }

    [Fact]
    public void ResizeAndShortHistory_DoNotLeaveAnExtraScrollablePage()
    {
        var view = new VirtualLogViewControl { TotalRecords = 100, FirstSeq = 1, LastSeq = 100 };
        view.Measure(new Size(800, 180)); view.Arrange(new Rect(0, 0, 800, 180));
        Assert.Equal(90, Bar(view).Value);
        view.Measure(new Size(800, 540)); view.Arrange(new Rect(0, 0, 800, 540));
        Assert.Equal(70, Bar(view).Maximum);
        Assert.Equal(70, Bar(view).Value);
        view.TotalRecords = 3; view.LastSeq = 3;
        Assert.Equal(0, Bar(view).Maximum);
        Assert.Equal(0, Bar(view).Value);
    }
}
