using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MADTOM.PluginContracts;

namespace SQUEEZE.Services;

/// <summary>
/// Manages SQUEEZE dynamic theme resource mapping.
/// Synchronizes with MADTOM Console's active theme palette when hosted,
/// while providing standalone fallback resources for independent execution.
/// </summary>
public static class SqueezeThemeService
{
    private static readonly Dictionary<string, string> FallbackHexColors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Standard MADTOM Theme Keys
        ["Background"] = "#070A12",
        ["HeaderBackground"] = "#0B111E",
        ["SidebarBackground"] = "#0C1220",
        ["CardBackground"] = "#0C1220",
        ["CardHoverBackground"] = "#0F172A",
        ["DarkBase"] = "#04060B",
        ["Border"] = "#1E293B",
        ["BorderSubtle"] = "#0F172A",
        ["TextPrimary"] = "#F8FAFC",
        ["TextSecondary"] = "#94A3B8",
        ["TextMuted"] = "#64748B",
        ["Accent"] = "#06B6D4",
        ["AccentSubtle"] = "#083344",
        ["AccentText"] = "#22D3EE",
        ["Healthy"] = "#10B981",
        ["HealthySubtle"] = "#064E3B",
        ["Warning"] = "#F59E0B",
        ["WarningSubtle"] = "#451A03",
        ["Critical"] = "#F43F5E",
        ["CriticalSubtle"] = "#4C0519",

        // Cyber / Legacy Aliases
        ["CyberBg"] = "#070A12",
        ["CyberCard"] = "#0C1220",
        ["CyberPanel"] = "#0C1220",
        ["CyberPanelDark"] = "#04060B",
        ["CyberBorder"] = "#1E293B",
        ["CyberBorderLight"] = "#30363D",
        ["CyberGold"] = "#F59E0B",
        ["CyberGoldLight"] = "#F5A623",
        ["CyberCyan"] = "#06B6D4",
        ["CyberCyanLight"] = "#22D3EE",
        ["CyberGreen"] = "#10B981",
        ["CyberGreenLight"] = "#34D399",
        ["CyberBlue"] = "#38BDF8",
        ["CyberPurple"] = "#A855F7",
        ["CyberMuted"] = "#64748B",
        ["CyberText"] = "#F8FAFC",
        ["CyberWhite"] = "#FFFFFF"
    };

    public static void InitializeStandaloneDefaults()
    {
        ApplyColorMap(FallbackHexColors);
    }

    public static void ApplyThemeFromHost(IThemeHost themeHost)
    {
        if (themeHost == null)
        {
            InitializeStandaloneDefaults();
            return;
        }

        var palette = themeHost.GetPalette(themeHost.CurrentTheme);
        if (palette != null && palette.Colors != null && palette.Colors.Count > 0)
        {
            var mapped = new Dictionary<string, string>(FallbackHexColors, StringComparer.OrdinalIgnoreCase);

            foreach (var (key, val) in palette.Colors)
            {
                if (!string.IsNullOrEmpty(val))
                {
                    mapped[key] = val;
                }
            }

            // Sync Cyber aliases with host palette
            string bg = mapped.GetValueOrDefault("Background", "#070A12");
            string card = mapped.GetValueOrDefault("CardBackground", mapped.GetValueOrDefault("SurfaceElevated", "#0C1220"));
            string darkBase = mapped.GetValueOrDefault("DarkBase", "#04060B");
            string border = mapped.GetValueOrDefault("Border", "#1E293B");
            string text = mapped.GetValueOrDefault("TextPrimary", "#F8FAFC");
            string muted = mapped.GetValueOrDefault("TextMuted", "#64748B");
            string accent = mapped.GetValueOrDefault("Accent", "#06B6D4");
            string accentText = mapped.GetValueOrDefault("AccentText", mapped.GetValueOrDefault("AccentHover", "#22D3EE"));
            string healthy = mapped.GetValueOrDefault("Healthy", mapped.GetValueOrDefault("Success", "#10B981"));
            string warning = mapped.GetValueOrDefault("Warning", "#F59E0B");
            string critical = mapped.GetValueOrDefault("Critical", "#F43F5E");

            mapped["CyberBg"] = bg;
            mapped["CyberCard"] = card;
            mapped["CyberPanel"] = card;
            mapped["CyberPanelDark"] = darkBase;
            mapped["CyberBorder"] = border;
            mapped["CyberBorderLight"] = border;
            mapped["CyberText"] = text;
            mapped["CyberMuted"] = muted;
            mapped["CyberCyan"] = accent;
            mapped["CyberCyanLight"] = accentText;
            mapped["CyberBlue"] = accentText;
            mapped["CyberGreen"] = healthy;
            mapped["CyberGreenLight"] = healthy;
            mapped["CyberGold"] = warning;
            mapped["CyberGoldLight"] = warning;

            ApplyColorMap(mapped);
        }
        else
        {
            InitializeStandaloneDefaults();
        }
    }

    private static void ApplyColorMap(IReadOnlyDictionary<string, string> colors)
    {
        void UpdateResources()
        {
            var app = Application.Current;
            if (app == null) return;

            var res = app.Resources;

            foreach (var (key, hex) in colors)
            {
                if (Color.TryParse(hex, out var color))
                {
                    var brush = new ImmutableSolidColorBrush(color);

                    // Cyber legacy keys
                    res[key] = brush;

                    // Standard MADTOM keys
                    res[$"{key}Brush"] = brush;
                    res[$"Color{key}"] = color;
                    res[$"AppColor.{key}"] = color;
                    res[$"AppBrush.{key}"] = brush;
                }
            }

            // Map common aliases
            MapAlias(res, colors, "Background", "AppBg");
            MapAlias(res, colors, "HeaderBackground", "HeaderBg");
            MapAlias(res, colors, "SidebarBackground", "SidebarBg");
            MapAlias(res, colors, "CardBackground", "CardBg");
            MapAlias(res, colors, "CardHoverBackground", "CardHoverBg");
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateResources();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateResources);
        }
    }

    private static void MapAlias(IResourceDictionary res, IReadOnlyDictionary<string, string> colors, string source, string target)
    {
        if (colors.TryGetValue(source, out var hex) && Color.TryParse(hex, out var color))
        {
            var brush = new ImmutableSolidColorBrush(color);
            res[$"{target}Brush"] = brush;
            res[$"Color{target}"] = color;
        }
    }
}
