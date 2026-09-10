using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MadTOM.Common.Converters;

public static class ValueConverters
{
    private static readonly IBrush EmeraldBrush = new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly IBrush AmberBrush = new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly IBrush RoseBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 63, 94));
    private static readonly IBrush SlateBrush = new ImmutableSolidColorBrush(Color.FromRgb(100, 116, 139));

    private static readonly IBrush CardBorderNormal = new ImmutableSolidColorBrush(Color.FromArgb(200, 30, 41, 59));
    private static readonly IBrush CardBorderWarning = new ImmutableSolidColorBrush(Color.FromArgb(220, 245, 158, 11));
    private static readonly IBrush CardBorderCritical = new ImmutableSolidColorBrush(Color.FromArgb(220, 244, 63, 94));

    private static readonly IBrush BaremetalBadgeBg = new ImmutableSolidColorBrush(Color.FromArgb(220, 8, 51, 68));
    private static readonly IBrush BaremetalBadgeBorder = new ImmutableSolidColorBrush(Color.FromArgb(180, 21, 94, 117));
    private static readonly IBrush BaremetalBadgeFg = new ImmutableSolidColorBrush(Color.FromRgb(34, 211, 238));

    private static readonly IBrush VmBadgeBg = new ImmutableSolidColorBrush(Color.FromArgb(220, 30, 27, 75));
    private static readonly IBrush VmBadgeBorder = new ImmutableSolidColorBrush(Color.FromArgb(180, 67, 56, 202));
    private static readonly IBrush VmBadgeFg = new ImmutableSolidColorBrush(Color.FromRgb(129, 140, 248));

    public static readonly IValueConverter StatusToBrush =
        new FuncValueConverter<string, IBrush>(status =>
            status?.ToLowerInvariant() switch
            {
                "healthy" or "online" => EmeraldBrush,
                "warning" or "stale" => AmberBrush,
                "critical" or "offline" => RoseBrush,
                _ => SlateBrush
            });

    public static readonly IValueConverter StatusToBorderBrush =
        new FuncValueConverter<string, IBrush>(status =>
            status?.ToLowerInvariant() switch
            {
                "warning" or "stale" => CardBorderWarning,
                "critical" or "offline" => CardBorderCritical,
                _ => CardBorderNormal
            });

    public static readonly IValueConverter RoleToBadgeBg =
        new FuncValueConverter<string, IBrush>(role =>
            role?.ToLowerInvariant() == "baremetal" ? BaremetalBadgeBg : VmBadgeBg);

    public static readonly IValueConverter RoleToBadgeBorder =
        new FuncValueConverter<string, IBrush>(role =>
            role?.ToLowerInvariant() == "baremetal" ? BaremetalBadgeBorder : VmBadgeBorder);

    public static readonly IValueConverter RoleToBadgeFg =
        new FuncValueConverter<string, IBrush>(role =>
            role?.ToLowerInvariant() == "baremetal" ? BaremetalBadgeFg : VmBadgeFg);

    public static readonly IValueConverter BooleanToVisibility =
        new FuncValueConverter<bool, bool>(b => b);

    public static readonly IValueConverter BoolToNavPadding =
        new FuncValueConverter<bool, Avalonia.Thickness>(isCollapsed =>
            isCollapsed ? new Avalonia.Thickness(6, 14) : new Avalonia.Thickness(12, 14));

    public static readonly IValueConverter BoolToScrollMargin =
        new FuncValueConverter<bool, Avalonia.Thickness>(isCollapsed =>
            isCollapsed ? new Avalonia.Thickness(6, 12) : new Avalonia.Thickness(12, 14));

    public static readonly IValueConverter BoolToFooterPadding =
        new FuncValueConverter<bool, Avalonia.Thickness>(isCollapsed =>
            isCollapsed ? new Avalonia.Thickness(6, 8) : new Avalonia.Thickness(12, 10));

    public static readonly IValueConverter BoolToArrow =
        new FuncValueConverter<bool, string>(isExpanded => isExpanded ? "▾" : "▸");

    public static readonly IValueConverter InverseBooleanToVisibility =
        new FuncValueConverter<bool, bool>(b => !b);
}
