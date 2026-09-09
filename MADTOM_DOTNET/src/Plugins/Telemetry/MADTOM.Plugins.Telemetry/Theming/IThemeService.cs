using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;

namespace MadTOM.Theming;

public interface IThemeService : INotifyPropertyChanged
{
    string CurrentTheme { get; }
    IReadOnlyList<string> AvailableThemes { get; }
    Color GetColor(string key, Color fallback = default);
    IBrush GetBrush(string key);
    void ApplyTheme(string themeName);
    void ImportTheme(string themeName, string jsonOrFilePath);
    event EventHandler<string>? ThemeChanged;
}

