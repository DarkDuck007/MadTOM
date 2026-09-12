using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;

namespace MadTOM.Theming;

public interface IThemeService : INotifyPropertyChanged
{
    string CurrentTheme { get; }
    IReadOnlyList<string> AvailableThemes { get; }
    IReadOnlyList<ThemePaletteModel> AvailablePalettes { get; }
    ThemePaletteModel? GetPalette(string themeName);
    Color GetColor(string key, Color fallback = default);
    IBrush GetBrush(string key);
    void ApplyTheme(string themeName);
    void ApplyTheme(string themeName, ThemePaletteModel? fallbackPalette);
    void ImportTheme(string themeName, string jsonOrFilePath);
    void ReloadThemes();
    void ScanThemesDirectory(string directory, bool isUserDir = true);
    event EventHandler<string>? ThemeChanged;
    event EventHandler? ThemesCollectionChanged;
}

