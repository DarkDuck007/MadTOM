using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MadTOM.Common.Utils;

public static class ColorInterpolator
{
    // Pre-allocated brush cache for 101 integer load steps (0% to 100%) to avoid GC allocations in 2D render loops
    private static readonly ImmutableSolidColorBrush[] BrushCache = new ImmutableSolidColorBrush[101];
    private static readonly Color[] ColorCache = new Color[101];

    static ColorInterpolator()
    {
        for (int i = 0; i <= 100; i++)
        {
            var color = ComputeColor(i / 100.0);
            ColorCache[i] = color;
            BrushCache[i] = new ImmutableSolidColorBrush(color);
        }
    }

    public static Color InterpolateLoadColor(double load)
    {
        if (load <= 0.0) return ColorCache[0];
        if (load >= 1.0) return ColorCache[100];

        int index = Math.Clamp((int)Math.Round(load * 100.0), 0, 100);
        return ColorCache[index];
    }

    public static ImmutableSolidColorBrush InterpolateLoadBrush(double load)
    {
        if (load <= 0.0) return BrushCache[0];
        if (load >= 1.0) return BrushCache[100];

        int index = Math.Clamp((int)Math.Round(load * 100.0), 0, 100);
        return BrushCache[index];
    }

    public static Color ComputeColor(double load)
    {
        load = Math.Clamp(load, 0.0, 1.0);

        if (load < 0.40)
        {
            // Slate (30, 41, 59) -> Emerald (16, 185, 129)
            double t = load / 0.40;
            byte r = (byte)Math.Round(30 + t * (16 - 30));
            byte g = (byte)Math.Round(41 + t * (185 - 41));
            byte b = (byte)Math.Round(59 + t * (129 - 59));
            return Color.FromRgb(r, g, b);
        }
        
        if (load < 0.75)
        {
            // Emerald (16, 185, 129) -> Amber (245, 158, 11)
            double t = (load - 0.40) / 0.35;
            byte r = (byte)Math.Round(16 + t * (245 - 16));
            byte g = (byte)Math.Round(185 + t * (158 - 185));
            byte b = (byte)Math.Round(129 + t * (11 - 129));
            return Color.FromRgb(r, g, b);
        }
        else
        {
            // Amber (245, 158, 11) -> Crimson (244, 63, 94)
            double t = Math.Min(1.0, (load - 0.75) / 0.25);
            byte r = (byte)Math.Round(245 + t * (244 - 245));
            byte g = (byte)Math.Round(158 + t * (63 - 158));
            byte b = (byte)Math.Round(11 + t * (94 - 11));
            return Color.FromRgb(r, g, b);
        }
    }
}

