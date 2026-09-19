namespace MadTOM.ViewModels;

/// <summary>Pointer dismissal applies only to transient hover previews.</summary>
public sealed class DiagnosticsPanelState
{
    public bool Pinned { get; set; }
    public bool PointerInside { get; set; }
    public bool AnchorInside { get; set; }
    public bool OpenedByTap { get; private set; }
    public bool ShouldDismiss => !Pinned && !OpenedByTap && !PointerInside && !AnchorInside;
    public void OpenByTap() => OpenedByTap = true;
}
