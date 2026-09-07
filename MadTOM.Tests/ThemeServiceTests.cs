using Avalonia.Media;
using MadTOM.Theming;
using Xunit;

namespace MadTOM.Tests;

public class ThemeServiceTests
{
    [Fact]
    public void DefaultTheme_LoadsDefaultDarkPalette()
    {
        var service = ThemeService.Instance;
        service.ApplyTheme("default-dark");
        Assert.Equal("default-dark", service.CurrentTheme);

        var bg = service.GetColor("Background");
        Assert.Equal(Color.FromRgb(7, 10, 18), bg);

        var accentBrush = service.GetBrush("Accent");
        Assert.NotNull(accentBrush);
    }

    [Fact]
    public void ApplyTheme_SwitchesToHighContrast()
    {
        var service = ThemeService.Instance;
        service.ApplyTheme("high-contrast");
        Assert.Equal("high-contrast", service.CurrentTheme);
    }

    [Fact]
    public void ImportTheme_LoadsAndAppliesCustomTheme()
    {
        var service = ThemeService.Instance;
        string customThemeJson = """
        {
            "ThemeName": "neon-matrix",
            "DisplayName": "Neon Matrix",
            "Colors": {
                "Background": "#001100",
                "Accent": "#00ff66",
                "TextPrimary": "#ffffff"
            }
        }
        """;

        service.ImportTheme("neon-matrix", customThemeJson);
        Assert.Equal("neon-matrix", service.CurrentTheme);

        var bg = service.GetColor("Background");
        Assert.Equal(Color.FromRgb(0, 17, 0), bg);

        var accent = service.GetColor("Accent");
        Assert.Equal(Color.FromRgb(0, 255, 102), accent);
    }

    [Fact]
    public void ThemeChanged_EventFiresWhenThemeIsApplied()
    {
        var service = ThemeService.Instance;
        string? changedTheme = null;
        service.ThemeChanged += (_, name) => changedTheme = name;

        service.ApplyTheme("default-dark");
        Assert.Equal("default-dark", changedTheme);
    }
}

