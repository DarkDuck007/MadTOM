using MadTOM.ViewModels;

namespace MadTOM.Tests;

public class DiagnosticsPanelStateTests
{
    [Fact]
    public void HoverRemainsOpenWhileCrossingFromTriggerIntoPanel()
    {
        var state = new DiagnosticsPanelState { AnchorInside = true };
        Assert.False(state.ShouldDismiss);
        state.AnchorInside = false;
        state.PointerInside = true;
        Assert.False(state.ShouldDismiss);
        state.PointerInside = false;
        Assert.True(state.ShouldDismiss);
    }

    [Fact]
    public void TapKeepsPanelOpenWithoutHover()
    {
        var state = new DiagnosticsPanelState();
        state.OpenByTap();
        Assert.False(state.ShouldDismiss);
    }

    [Fact]
    public void PinKeepsHoverPreviewOpenUntilUnpinned()
    {
        var state = new DiagnosticsPanelState { Pinned = true };
        Assert.False(state.ShouldDismiss);
        state.Pinned = false;
        Assert.True(state.ShouldDismiss);
    }
}
