using Avalonia.Media;
using MadTOM.Theming;
using Xunit;

namespace MadTOM.Tests;

[Collection("GlobalSingletons")]
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

    [Fact]
    public void AvailableThemes_ContainsAllBuiltinThemes()
    {
        var service = ThemeService.Instance;
        var themes = service.AvailableThemes;

        Assert.True(themes.Count >= 8);
        Assert.Contains("default-dark", themes);
        Assert.Contains("pure-light", themes);
        Assert.Contains("paper-white", themes);
        Assert.Contains("minimal-mono", themes);
        Assert.Contains("anti-bleed-grey", themes);
        Assert.Contains("tft-amber-terminal", themes);
        Assert.Contains("high-contrast", themes);
        Assert.Contains("solarized-dark", themes);
    }

    [Theory]
    [InlineData("pure-light", "#f8fafc", "#0284c7")]
    [InlineData("paper-white", "#fbf9f5", "#b45309")]
    [InlineData("minimal-mono", "#121214", "#e4e4e7")]
    [InlineData("anti-bleed-grey", "#23272e", "#00d4ff")]
    [InlineData("tft-amber-terminal", "#1b1c18", "#ffb000")]
    [InlineData("solarized-dark", "#002b36", "#2aa198")]
    public void ApplyTheme_LoadsCorrectPaletteColors(string themeName, string expectedBgHex, string expectedAccentHex)
    {
        var service = ThemeService.Instance;
        service.ApplyTheme(themeName);
        Assert.Equal(themeName, service.CurrentTheme);

        var bg = service.GetColor("Background");
        var expectedBg = Color.Parse(expectedBgHex);
        Assert.Equal(expectedBg, bg);

        var accent = service.GetColor("Accent");
        var expectedAccent = Color.Parse(expectedAccentHex);
        Assert.Equal(expectedAccent, accent);
    }
}

