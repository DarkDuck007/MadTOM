namespace MADTOM.PluginContracts;

/// <summary>
/// Host service allowing plugins to display system toasts / notifications in the MADTOM Console frame.
/// </summary>
public interface IPluginNotificationService
{
    /// <summary>
    /// Displays a temporary floating toast notification.
    /// </summary>
    void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000);
}

