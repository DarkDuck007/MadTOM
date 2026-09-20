using System.Collections.Generic;
using SQUEEZE.Models;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class PresetServiceServerSyncTests
{
    [Fact]
    public void LoadServerPresets_OverwritesStandardPresets_AndPreservesCustom()
    {
        var service = new PresetService();

        // Add a user custom preset
        var custom = service.AddCustomPreset("My Custom Stream", "Custom stream desc", new TranscodePreset
        {
            Codec = "libx265",
            Crf = 18.0
        });

        // Server sends new presets
        var serverPresets = new List<TranscodePreset>
        {
            new TranscodePreset
            {
                Key = "srv_4k",
                Title = "Server 4K Master",
                Category = "Production",
                Codec = "libx265",
                Tag = "DEFAULT"
            },
            new TranscodePreset
            {
                Key = "srv_web",
                Title = "Server Web 720p",
                Category = "Web",
                Codec = "libx264"
            }
        };

        bool reloadedFired = false;
        service.PresetsReloaded += (s, e) => reloadedFired = true;

        service.LoadServerPresets(serverPresets);

        Assert.True(reloadedFired);

        var all = service.GetAllPresets();
        // 2 server presets + 1 custom preset = 3 total
        Assert.Equal(3, all.Count);
        Assert.Contains(all, p => p.Key == "srv_4k");
        Assert.Contains(all, p => p.Key == "srv_web");
        Assert.Contains(all, p => p.Key == custom.Key);
        Assert.Equal("srv_4k", service.DefaultPresetKey);
    }
}

