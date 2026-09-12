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

        Assert.True(themes.Count >= 13);
        Assert.Contains("default-dark", themes);
        Assert.Contains("pure-light", themes);
        Assert.Contains("paper-white", themes);
        Assert.Contains("minimal-mono", themes);
        Assert.Contains("anti-bleed-grey", themes);
        Assert.Contains("tft-amber-terminal", themes);
        Assert.Contains("high-contrast", themes);
        Assert.Contains("solarized-dark", themes);
        Assert.Contains("simple-purple-dark", themes);
        Assert.Contains("simple-purple-light", themes);
        Assert.Contains("cyberpunk-high-contrast", themes);
        Assert.Contains("lavender", themes);
        Assert.Contains("neon-glass", themes);
    }

    [Theory]
    [InlineData("pure-light", "#f8fafc", "#0284c7")]
    [InlineData("paper-white", "#fbf9f5", "#b45309")]
    [InlineData("minimal-mono", "#121214", "#e4e4e7")]
    [InlineData("anti-bleed-grey", "#23272e", "#00d4ff")]
    [InlineData("tft-amber-terminal", "#1b1c18", "#ffb000")]
    [InlineData("solarized-dark", "#002b36", "#2aa198")]
    [InlineData("simple-purple-dark", "#000229", "#A4A6E3")]
    [InlineData("simple-purple-light", "#E2EBF3", "#6A4CD4")]
    [InlineData("cyberpunk-high-contrast", "#000229", "#00F5A0")]
    [InlineData("lavender", "#F5F8FC", "#6A4CD4")]
    [InlineData("neon-glass", "#080C42", "#6A4CD4")]
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

    [Fact]
    public void ThemeParser_ParsesYamlWithCommentsAndQuotes()
    {
        string yaml = """
        # Custom Synthwave Theme
        themeName: synthwave-84
        displayName: Synthwave '84
        category: Retro Sci-Fi
        colors:
          # Background tones
          Background: "#262335"
          HeaderBackground: '#241b2f'
          Accent: "#ff7edb" # Neon Pink
          TextPrimary: "#ffffff"
        """;

        var palette = ThemeParser.Parse(yaml);
        Assert.NotNull(palette);
        Assert.Equal("synthwave-84", palette.ThemeName);
        Assert.Equal("Synthwave '84", palette.DisplayName);
        Assert.Equal("Retro Sci-Fi", palette.Category);
        Assert.Equal("#262335", palette.Colors["Background"]);
        Assert.Equal("#241b2f", palette.Colors["HeaderBackground"]);
        Assert.Equal("#ff7edb", palette.Colors["Accent"]);
        Assert.Equal("#ffffff", palette.Colors["TextPrimary"]);
    }

    [Fact]
    public void ThemeParser_InfersDisplayNameAndCategoryWhenOmitted()
    {
        string yaml = """
        colors:
          Background: "#ffffff"
          Accent: "#0000ff"
        """;

        var palette = ThemeParser.Parse(yaml, fallbackName: "my-custom-light");
        Assert.NotNull(palette);
        Assert.Equal("my-custom-light", palette.ThemeName);
        Assert.Equal("My Custom Light", palette.DisplayName);
        Assert.Equal("Light", palette.GetEffectiveCategory());
    }

    [Fact]
    public void ThemeService_RuntimeYamlLoading_And_ThemesCollectionChanged()
    {
        var service = ThemeService.Instance;
        bool eventFired = false;
        service.ThemesCollectionChanged += (_, _) => eventFired = true;

        string customYaml = """
        themeName: unit-test-matrix
        displayName: Unit Test Matrix
        category: Test
        colors:
          Background: "#001100"
          Accent: "#00ff00"
        """;

        service.ImportTheme("unit-test-matrix", customYaml);

        Assert.True(eventFired);
        Assert.Contains("unit-test-matrix", service.AvailableThemes);
        var palette = service.GetPalette("unit-test-matrix");
        Assert.NotNull(palette);
        Assert.Equal("Unit Test Matrix", palette.DisplayName);
        Assert.Equal("Test", palette.Category);
        Assert.Equal("unit-test-matrix", service.CurrentTheme);
        Assert.Equal(Color.FromRgb(0, 17, 0), service.GetColor("Background"));
        Assert.Equal(Color.FromRgb(0, 255, 0), service.GetColor("Accent"));
    }

    [Fact]
    public void ThemeService_AvailablePalettes_ReflectsAllDiscoveredThemes()
    {
        var service = ThemeService.Instance;
        var palettes = service.AvailablePalettes;

        Assert.NotEmpty(palettes);
        Assert.All(palettes, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.ThemeName));
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
            Assert.NotEmpty(p.Colors);
            Assert.False(string.IsNullOrWhiteSpace(p.GetEffectiveCategory()));
        });
    }

    [Fact]
    public void ThemeService_ScanThemesDirectory_DiscoversRuntimeYamlAndJsonThemes()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "madtom_test_themes_" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            string yamlPath = System.IO.Path.Combine(tempDir, "cyber-synth.yaml");
            System.IO.File.WriteAllText(yamlPath, """
            themeName: cyber-synth
            displayName: Cyber Synthwave
            category: Custom Synth
            colors:
              Background: "#1a0826"
              Accent: "#ff007f"
            """);

            string jsonPath = System.IO.Path.Combine(tempDir, "retro-amber.json");
            System.IO.File.WriteAllText(jsonPath, """
            {
              "themeName": "retro-amber-v2",
              "displayName": "Retro Amber V2",
              "category": "CRT",
              "colors": {
                "Background": "#110e00",
                "Accent": "#ffaa00"
              }
            }
            """);

            var service = ThemeService.Instance;
            service.ScanThemesDirectory(tempDir, isUserDir: true);

            Assert.Contains("cyber-synth", service.AvailableThemes);
            Assert.Contains("retro-amber-v2", service.AvailableThemes);

            var p1 = service.GetPalette("cyber-synth");
            Assert.NotNull(p1);
            Assert.Equal("Cyber Synthwave", p1.DisplayName);
            Assert.Equal("Custom Synth", p1.Category);

            var p2 = service.GetPalette("retro-amber-v2");
            Assert.NotNull(p2);
            Assert.Equal("Retro Amber V2", p2.DisplayName);
            Assert.Equal("CRT", p2.Category);
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ThemePaletteModel_CloneAndMerge_PreservesBaseAndOverwritesOverrides()
    {
        var fallback = new ThemePaletteModel
        {
            ThemeName = "my-base",
            DisplayName = "My Base",
            Category = "Dark",
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#111111",
                ["Accent"] = "#aaaaaa",
                ["TextPrimary"] = "#ffffff"
            }
        };

        var overrides = new ThemePaletteModel
        {
            ThemeName = "my-base",
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#000229",
                ["Accent"] = "#6A4CD4"
            }
        };

        var clone = fallback.Clone();
        clone.Merge(overrides);

        Assert.Equal("my-base", clone.ThemeName);
        Assert.Equal("My Base", clone.DisplayName);
        Assert.Equal("#000229", clone.Colors["Background"]); // Overridden
        Assert.Equal("#6A4CD4", clone.Colors["Accent"]); // Overridden
        Assert.Equal("#ffffff", clone.Colors["TextPrimary"]); // Preserved from base!
    }

    [Fact]
    public void ThemeService_ApplyTheme_WithHostFallback_AppliesBaseAndPluginOverrides()
    {
        var service = ThemeService.Instance;

        var hostFallback = new ThemePaletteModel
        {
            ThemeName = "test-host-theme",
            DisplayName = "Test Host Theme",
            Category = "Host Category",
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#101010",
                ["HeaderBackground"] = "#202020",
                ["Accent"] = "#303030",
                ["TextPrimary"] = "#f0f0f0"
            }
        };

        // Apply with host fallback
        service.ApplyTheme("test-host-theme", hostFallback);

        Assert.Equal("test-host-theme", service.CurrentTheme);
        Assert.Equal(Color.FromRgb(16, 16, 16), service.GetColor("Background"));
        Assert.Equal(Color.FromRgb(32, 32, 32), service.GetColor("HeaderBackground"));
        Assert.Equal(Color.FromRgb(48, 48, 48), service.GetColor("Accent"));
        Assert.Equal(Color.FromRgb(240, 240, 240), service.GetColor("TextPrimary"));
    }

    [Fact]
    public void ThemeService_PluginDirectoryOverride_TakesPrecedenceOverHostFallback()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "madtom_override_test_" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            string overrideFile = System.IO.Path.Combine(tempDir, "override-test.yaml");
            System.IO.File.WriteAllText(overrideFile, """
            themeName: override-test
            colors:
              Background: "#990000"
              Accent: "#ff0099"
            """);

            var service = ThemeService.Instance;
            service.ScanThemesDirectory(tempDir, isUserDir: false);

            var hostFallback = new ThemePaletteModel
            {
                ThemeName = "override-test",
                DisplayName = "Override Test Theme",
                Category = "Test",
                Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#000000",
                    ["TextPrimary"] = "#ffffff",
                    ["Accent"] = "#0000ff"
                }
            };

            service.ApplyTheme("override-test", hostFallback);

            Assert.Equal("override-test", service.CurrentTheme);
            // Overridden in YAML
            Assert.Equal(Color.FromRgb(153, 0, 0), service.GetColor("Background"));
            Assert.Equal(Color.FromRgb(255, 0, 153), service.GetColor("Accent"));
            // Preserved from host fallback
            Assert.Equal(Color.FromRgb(255, 255, 255), service.GetColor("TextPrimary"));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }
}

