using System.Collections.Generic;
using MadTOM.Localization;
using Xunit;

namespace MadTOM.Tests;

[Collection("GlobalSingletons")]
public class LexiconServiceTests
{
    [Fact]
    public void DefaultPack_IsGoose()
    {
        var service = LexiconService.Instance;
        service.LoadLexicon("goose");
        Assert.Equal("goose", service.CurrentPack);
        Assert.Equal("The Pond Overview 🪿", service["fleetTitle"]);
        Assert.Contains("Feather-light", service["fleetSubtitle"]);
    }

    [Fact]
    public void SwitchPack_ToStandard_ReturnsEnterpriseTerminology()
    {
        var service = LexiconService.Instance;
        service.LoadLexicon("standard");
        Assert.Equal("standard", service.CurrentPack);
        Assert.Equal("Nodes", service["fleetTitle"]);
        Assert.Contains("TWAMP transit", service["fleetSubtitle"]);
    }

    [Fact]
    public void SwitchPack_ToFeline_ReturnsFelineTerminology()
    {
        var service = LexiconService.Instance;
        service.LoadLexicon("feline");
        Assert.Equal("feline", service.CurrentPack);
        Assert.Equal("The Clowder Domain 🐱", service["FleetTitle"]);
    }

    [Fact]
    public void MissingKey_ReturnsSentinel()
    {
        var service = LexiconService.Instance;
        var val = service["NonExistentKey12345"];
        Assert.Equal("!NonExistentKey12345!", val);
    }

    [Fact]
    public void ImportLexicon_RegistersNewCommunityPack()
    {
        var service = LexiconService.Instance;
        string customJson = """
        {
            "FleetTitle": "CYBER BATTALION",
            "AppSubtitle": "Combat Net Matrix"
        }
        """;

        service.ImportLexicon("cyberpunk", customJson);
        Assert.Equal("cyberpunk", service.CurrentPack);
        Assert.Equal("CYBER BATTALION", service["FleetTitle"]);
        Assert.Equal("Combat Net Matrix", service["AppSubtitle"]);
        Assert.Contains("cyberpunk", service.AvailablePacks);
    }

    [Fact]
    public void LexiconChanged_EventFiresOnSwitch()
    {
        var service = LexiconService.Instance;
        string? firedPack = null;
        service.LexiconChanged += (_, pack) => firedPack = pack;

        service.LoadLexicon("standard");
        Assert.Equal("standard", firedPack);
    }

    [Fact]
    public void LocExtension_UpdatesTargetProperty_WhenLexiconChanges()
    {
        var service = LexiconService.Instance;
        service.LoadLexicon("goose");

        var loc = new LocExtension("fleetTitle");
        var obj = loc.ProvideValue(null!);
        Assert.NotNull(obj);
        var binding = Assert.IsType<Avalonia.Data.Binding>(obj);

        Assert.Equal("[fleetTitle]", binding.Path);
        Assert.Same(service, binding.Source);
        Assert.Equal(Avalonia.Data.BindingMode.OneWay, binding.Mode);
    }
}
