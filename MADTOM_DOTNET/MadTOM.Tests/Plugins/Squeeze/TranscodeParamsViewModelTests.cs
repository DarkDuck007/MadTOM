using SQUEEZE.Models;
using SQUEEZE.ViewModels;
using Xunit;

namespace SQUEEZE.Tests;

public class TranscodeParamsViewModelTests
{
    [Fact]
    public void ModifyingCodec_UpdatesRawCliInput_AndFiresUserChangeCallback()
    {
        var vm = new TranscodeParamsViewModel();
        var callbackFired = false;
        vm.OnParameterChangedByUser = () => callbackFired = true;

        vm.Codec = "libx265";

        Assert.True(callbackFired);
        Assert.Contains("-c:v libx265", vm.RawCliInput);
    }

    [Fact]
    public void ModifyingCrf_UpdatesCrfBadge_AndCliString()
    {
        var vm = new TranscodeParamsViewModel();
        var callbackFired = false;
        vm.OnParameterChangedByUser = () => callbackFired = true;

        vm.Crf = 18.0;

        Assert.True(callbackFired);
        Assert.Equal("18.0 RF", vm.CrfBadgeText);
        Assert.Contains("-crf 18.0", vm.RawCliInput);
    }

    [Fact]
    public void ModifyingSpeedIndex_UpdatesSpeedName_AndCliString()
    {
        var vm = new TranscodeParamsViewModel();
        var callbackFired = false;
        vm.OnParameterChangedByUser = () => callbackFired = true;

        vm.SpeedIndex = 1; // superfast

        Assert.True(callbackFired);
        Assert.Equal("superfast", vm.SpeedName);
        Assert.Contains("-preset superfast", vm.RawCliInput);
    }

    [Fact]
    public void LoadFromPreset_DoesNotTriggerUserChangeCallback()
    {
        var vm = new TranscodeParamsViewModel();
        var callbackFired = false;
        vm.OnParameterChangedByUser = () => callbackFired = true;

        var preset = new TranscodePreset
        {
            Codec = "libsvtav1",
            Crf = 26.0,
            SpeedIndex = 5,
            Framerate = "60",
            ResolutionLimit = "720p HD (1280x720)"
        };

        vm.LoadFromPreset(preset);

        Assert.False(callbackFired);
        Assert.Equal("libsvtav1", vm.Codec);
        Assert.Equal(26.0, vm.Crf);
        Assert.Equal(5, vm.SpeedIndex);
        Assert.Contains("-c:v libsvtav1", vm.RawCliInput);
        Assert.Contains("-crf 26.0", vm.RawCliInput);
    }

    [Fact]
    public void CreatePresetSnapshot_PreservesAllCurrentValues()
    {
        var vm = new TranscodeParamsViewModel
        {
            Codec = "h264_nvenc",
            Crf = 21.0,
            SpeedIndex = 2,
            Deinterlace = false,
            Denoise = true
        };

        var snapshot = vm.CreatePresetSnapshot();

        Assert.Equal("h264_nvenc", snapshot.Codec);
        Assert.Equal(21.0, snapshot.Crf);
        Assert.Equal(2, snapshot.SpeedIndex);
        Assert.False(snapshot.Deinterlace);
        Assert.True(snapshot.Denoise);
    }

    [Fact]
    public void LoadUltraCompressionPreset_MatchesExpectedFfmpegCliString()
    {
        var presetService = new Services.PresetService();
        var ultraPreset = System.Linq.Enumerable.FirstOrDefault(presetService.GetAllPresets(), p => p.Key == "ultra_compression");
        Assert.NotNull(ultraPreset);

        var vm = new TranscodeParamsViewModel();
        vm.LoadFromPreset(ultraPreset);

        const string expectedCli = "ffmpeg -i input.mp4 -c:v libx264 -crf 30 -preset veryslow -tune film -vf \"scale=1280:-1,hqdn3d=1.5:1.5:6:6,format=yuv420p\" -c:a aac -b:a 64k -ac 1 -r 30 -movflags +faststart output_verysmall.mp4";
        Assert.Equal(expectedCli, vm.RawCliInput);
    }

    [Fact]
    public void CustomCropping_IncludesCropFilterInCliString()
    {
        var vm = new TranscodeParamsViewModel
        {
            CropMode = "Custom",
            CropTop = 10,
            CropBottom = 10,
            CropLeft = 20,
            CropRight = 20
        };

        Assert.True(vm.IsCustomCrop);
        Assert.Contains("crop=iw-40:ih-20:20:10", vm.RawCliInput);
    }
}

