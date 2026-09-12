using System;
using System.Collections.Generic;
using MadTOM.Theming;

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

    /// <summary>
    /// All available base/fallback theme palettes discovered by the host.
    /// </summary>
    IReadOnlyList<ThemePaletteModel> AvailablePalettes { get; }

    /// <summary>
    /// Retrieves the fallback theme palette for the specified theme key.
    /// </summary>
    ThemePaletteModel? GetPalette(string themeName);
}
