using System;

namespace MADTOM.PluginContracts;

/// <summary>
/// Host service notifying plugins of global theme changes (e.g. Dark, Light, High-Contrast).
/// </summary>
public interface IThemeHost
{
    /// <summary>
    /// Current theme identifier (e.g. "default-dark", "high-contrast").
    /// </summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Event fired when the global theme changes in MADTOM Console.
    /// </summary>
    event EventHandler<string>? CurrentThemeChanged;
}

