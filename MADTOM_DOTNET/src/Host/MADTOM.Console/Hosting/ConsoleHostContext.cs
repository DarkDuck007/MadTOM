using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;

namespace MADTOM.Console.Hosting;

/// <summary>
/// Host context provided to all loaded plugins by MADTOM Console.
/// Dispatches global events and facilitates weak decoupled messaging.
/// </summary>
public sealed class ConsoleHostContext : IPluginHostContext, ILexiconHost, IThemeHost, IPluginNotificationService
{
    // PluginId -> (LexiconKey -> (StringKey -> TranslatedValue))
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Dictionary<string, string>>> _pluginLexicons
        = new(StringComparer.OrdinalIgnoreCase);

    public IPluginNotificationService Notifications => this;
    public ILexiconHost Lexicons => this;
    public IThemeHost Themes => this;
    public IMessenger Messenger => WeakReferenceMessenger.Default;

    // --- ILexiconHost Implementation ---
    public string CurrentLexicon { get; private set; } = "goose";
    public event EventHandler<string>? CurrentLexiconChanged;

    public void SetLexicon(string lexiconKey)
    {
        if (string.IsNullOrWhiteSpace(lexiconKey) || CurrentLexicon.Equals(lexiconKey, StringComparison.OrdinalIgnoreCase))
            return;

        CurrentLexicon = lexiconKey;
        CurrentLexiconChanged?.Invoke(this, lexiconKey);
    }

    public void RegisterLexicon(string pluginId, string lexiconKey, IReadOnlyDictionary<string, string> entries)
    {
        var pluginDict = _pluginLexicons.GetOrAdd(pluginId, _ => new(StringComparer.OrdinalIgnoreCase));
        pluginDict[lexiconKey] = new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);
    }

    public string GetString(string pluginId, string key, string? fallback = null)
    {
        if (_pluginLexicons.TryGetValue(pluginId, out var pluginDict))
        {
            if (pluginDict.TryGetValue(CurrentLexicon, out var entries) && entries.TryGetValue(key, out var val))
            {
                return val;
            }

            // Fallback to "standard" if available
            if (pluginDict.TryGetValue("standard", out var standardEntries) && standardEntries.TryGetValue(key, out var stdVal))
            {
                return stdVal;
            }
        }

        return fallback ?? key;
    }

    // --- IThemeHost Implementation ---
    public string CurrentTheme => Theming.ConsoleThemeManager.Instance.CurrentTheme;
    public event EventHandler<string>? CurrentThemeChanged;

    public IReadOnlyList<MadTOM.Theming.ThemePaletteModel> AvailablePalettes =>
        Theming.ConsoleThemeManager.Instance.AvailablePalettes;

    public MadTOM.Theming.ThemePaletteModel? GetPalette(string themeName) =>
        Theming.ConsoleThemeManager.Instance.GetPalette(themeName);

    public void SetTheme(string themeKey)
    {
        if (string.IsNullOrWhiteSpace(themeKey))
            return;

        Theming.ConsoleThemeManager.Instance.ApplyTheme(themeKey);
        CurrentThemeChanged?.Invoke(this, themeKey);
    }

    // --- IPluginNotificationService Implementation ---
    public event EventHandler<(string Message, string Icon, int DurationMs)>? ToastTriggered;

    public void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000)
    {
        ToastTriggered?.Invoke(this, (message, icon, durationMs));
    }
}

