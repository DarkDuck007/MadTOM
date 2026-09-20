using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SQUEEZE.Models;

namespace SQUEEZE.ViewModels;

public partial class TranscodeParamsViewModel : ViewModelBase
{
    private static readonly string[] SpeedNames =
        { "ultrafast", "superfast", "veryfast", "faster", "medium", "slow", "slower", "veryslow", "veryslow" };

    private bool _isUpdatingFromPreset;
    private TranscodePreset? _loadedPreset;

    [ObservableProperty]
    private string _container = "mp4";

    public IReadOnlyList<string> ContainerOptions { get; } = new[] { "mp4", "mkv", "webm" };
    public IReadOnlyList<string> AudioCodecOptions { get; } = new[] { "aac", "libopus", "copy", "none" };
    partial void OnContainerChanged(string value) => NotifyParamChange();

    public Action? OnParameterChangedByUser { get; set; }

    [ObservableProperty]
    private string _codec = "libx264";

    [ObservableProperty]
    private string _framerate = "vfr";

    [ObservableProperty]
    private double _crf = 22.0;

    [ObservableProperty]
    private int _speedIndex = 4; // medium

    [ObservableProperty]
    private string _resolutionLimit = "1080p Full HD (1920x1080)";

    [ObservableProperty]
    private int _width = 0;

    [ObservableProperty]
    private int _height = 0;

    [ObservableProperty]
    private string _cropping = "Automatic";

    [ObservableProperty]
    private string _cropMode = "None";

    [ObservableProperty]
    private int _cropTop = 0;

    [ObservableProperty]
    private int _cropBottom = 0;

    [ObservableProperty]
    private int _cropLeft = 0;

    [ObservableProperty]
    private int _cropRight = 0;

    [ObservableProperty]
    private string _tune = "none";

    [ObservableProperty]
    private string _pixelFormat = "yuv420p";

    [ObservableProperty]
    private bool _deinterlace = true;

    [ObservableProperty]
    private bool _denoise = false;

    [ObservableProperty]
    private string _denoiseFilter = "hqdn3d=1.5:1.5:6:6";

    [ObservableProperty]
    private bool _grayscale = false;

    [ObservableProperty]
    private string _audioTrack = "First audio stream (if present)";

    [ObservableProperty]
    private string _audioCodec = "aac";

    [ObservableProperty]
    private string _audioBitrate = "160k";

    [ObservableProperty]
    private int _audioChannels = 0;

    [ObservableProperty]
    private string _rawCliInput = "-c:v libx264 -crf 22.0 -preset medium -c:a aac -b:a 160k";

    public string SpeedName => _loadedPreset?.EncoderPreset ?? (Codec.Contains("nvenc") ? "p4" : Codec == "libsvtav1" ? "8" : SpeedIndex >= 0 && SpeedIndex < SpeedNames.Length
        ? SpeedNames[SpeedIndex]
        : "medium");

    public string CrfBadgeText => $"{Crf:F1} RF";

    public IReadOnlyList<string> CodecOptions { get; } = new[]
    {
        "libx264",
        "libx265",
        "h264_nvenc",
        "hevc_nvenc",
        "libsvtav1"
    };

    public IReadOnlyList<string> FramerateOptions { get; } = new[]
    {
        "vfr",
        "30",
        "60",
        "24",
        "25",
        "50",
        "source"
    };

    public IReadOnlyList<string> ResolutionOptions { get; } = new[]
    {
        "Source Native",
        "1080p Full HD (1920x1080)",
        "720p HD (1280x720)",
        "480p SD (854x480)",
        "Custom"
    };

    public IReadOnlyList<string> TuneOptions { get; } = new[]
    {
        "none",
        "film",
        "animation",
        "grain",
        "stillimage",
        "fastdecode",
        "zerolatency"
    };

    public IReadOnlyList<string> PixelFormatOptions { get; } = new[]
    {
        "yuv420p",
        "yuv420p10le",
        "yuv444p",
        "nv12",
        "auto"
    };

    public IReadOnlyList<string> CropModeOptions { get; } = new[]
    {
        "None",
        "Automatic",
        "Custom"
    };

    public IReadOnlyList<string> AudioBitrateOptions { get; } = new[]
    {
        "64k",
        "96k",
        "128k",
        "160k",
        "192k",
        "256k",
        "320k"
    };

    public IReadOnlyList<int> AudioChannelOptions { get; } = new[]
    {
        0, // Source
        1, // Mono
        2, // Stereo
        6  // 5.1
    };

    public IReadOnlyList<string> AudioChannelStringOptions { get; } = new[]
    {
        "Source (Auto)",
        "1 (Mono)",
        "2 (Stereo)",
        "6 (5.1 Surround)"
    };

    public string AudioChannelsString
    {
        get => AudioChannels switch
        {
            1 => "1 (Mono)",
            2 => "2 (Stereo)",
            6 => "6 (5.1 Surround)",
            _ => "Source (Auto)"
        };
        set
        {
            if (value != null && value.Length > 0 && char.IsDigit(value[0]))
            {
                AudioChannels = value[0] - '0';
            }
            else
            {
                AudioChannels = 0;
            }
            OnPropertyChanged(nameof(AudioChannelsString));
            NotifyParamChange();
        }
    }

    public IReadOnlyList<string> DenoisePresetOptions { get; } = new[]
    {
        "hqdn3d=1.5:1.5:6:6",
        "hqdn3d=2:2:3:3",
        "hqdn3d=4:4:6:6"
    };

    public bool IsCustomCrop => string.Equals(CropMode, "Custom", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomResolution => string.Equals(ResolutionLimit, "Custom", StringComparison.OrdinalIgnoreCase);

    partial void OnCodecChanged(string value)
    {
        if (!_isUpdatingFromPreset && _loadedPreset != null) _loadedPreset.EncoderPreset = null;
        OnPropertyChanged(nameof(SpeedName));
        NotifyParamChange();
    }
    partial void OnFramerateChanged(string value) => NotifyParamChange();
    partial void OnCrfChanged(double value)
    {
        OnPropertyChanged(nameof(CrfBadgeText));
        NotifyParamChange();
    }
    partial void OnSpeedIndexChanged(int value)
    {
        if (!_isUpdatingFromPreset && _loadedPreset != null) _loadedPreset.EncoderPreset = null;
        OnPropertyChanged(nameof(SpeedName));
        NotifyParamChange();
    }
    partial void OnResolutionLimitChanged(string value)
    {
        if (!_isUpdatingFromPreset && _loadedPreset != null) { _loadedPreset.Width = 0; _loadedPreset.Height = 0; }
        OnPropertyChanged(nameof(IsCustomResolution));
        NotifyParamChange();
    }
    partial void OnWidthChanged(int value) => NotifyParamChange();
    partial void OnHeightChanged(int value) => NotifyParamChange();
    partial void OnCroppingChanged(string value) => NotifyParamChange();
    partial void OnCropModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomCrop));
        NotifyParamChange();
    }
    partial void OnCropTopChanged(int value) => NotifyParamChange();
    partial void OnCropBottomChanged(int value) => NotifyParamChange();
    partial void OnCropLeftChanged(int value) => NotifyParamChange();
    partial void OnCropRightChanged(int value) => NotifyParamChange();
    partial void OnTuneChanged(string value) => NotifyParamChange();
    partial void OnPixelFormatChanged(string value) => NotifyParamChange();
    partial void OnDeinterlaceChanged(bool value) => NotifyParamChange();
    partial void OnDenoiseChanged(bool value) => NotifyParamChange();
    partial void OnDenoiseFilterChanged(string value) => NotifyParamChange();
    partial void OnGrayscaleChanged(bool value) => NotifyParamChange();
    partial void OnAudioCodecChanged(string value) => NotifyParamChange();
    partial void OnAudioBitrateChanged(string value) => NotifyParamChange();
    partial void OnAudioChannelsChanged(int value)
    {
        OnPropertyChanged(nameof(AudioChannelsString));
        NotifyParamChange();
    }

    private void NotifyParamChange()
    {
        if (_isUpdatingFromPreset) return;

        UpdateCliString();
        OnParameterChangedByUser?.Invoke();
    }

    private void UpdateCliString()
    {
        var input = "input.mp4";
        var output = "output_squeezed." + Container;
        if (_loadedPreset?.Key == "ultra_compression" || (Codec == "libx264" && Math.Abs(Crf - 30) < 0.1 && SpeedName == "veryslow" && Tune == "film"))
        {
            output = "output_verysmall." + Container;
        }

        string crfStr = (_loadedPreset?.Key == "ultra_compression" || (Math.Abs(Crf - 30.0) < 0.001 && _loadedPreset?.Key != null && _loadedPreset.Key != ""))
            ? "30"
            : $"{Crf:F1}";

        var parts = new List<string> { "ffmpeg", "-i", input, "-c:v", Codec, "-crf", crfStr, "-preset", SpeedName };
        if (!string.IsNullOrEmpty(Tune) && Tune != "none")
        {
            parts.Add("-tune");
            parts.Add(Tune);
        }

        var filters = new List<string>();
        if (CropMode == "Custom" && (CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0))
        {
            filters.Add($"crop=iw-{(CropLeft + CropRight)}:ih-{(CropTop + CropBottom)}:{CropLeft}:{CropTop}");
        }

        int targetWidth = Width;
        int targetHeight = Height;
        if (targetWidth == 0 && targetHeight == 0)
        {
            if (ResolutionLimit.Contains("1080p")) { targetWidth = 1920; targetHeight = -1; }
            else if (ResolutionLimit.Contains("720p")) { targetWidth = 1280; targetHeight = -1; }
            else if (ResolutionLimit.Contains("480p")) { targetWidth = 854; targetHeight = -1; }
        }
        else if (targetWidth > 0 && targetHeight == 0)
        {
            targetHeight = -1;
        }

        if (targetWidth > 0 || targetHeight > 0)
        {
            filters.Add($"scale={targetWidth}:{targetHeight}");
        }

        if (Deinterlace) filters.Add("yadif");
        if (Denoise)
        {
            filters.Add(string.IsNullOrWhiteSpace(DenoiseFilter) ? "hqdn3d=1.5:1.5:6:6" : DenoiseFilter);
        }
        if (Grayscale) filters.Add("hue=s=0");
        if (!string.IsNullOrEmpty(PixelFormat) && PixelFormat != "auto")
        {
            filters.Add($"format={PixelFormat}");
        }

        if (filters.Count > 0)
        {
            parts.Add("-vf");
            parts.Add($"\"{string.Join(",", filters)}\"");
        }

        parts.Add("-c:a");
        parts.Add(AudioCodec);
        if (AudioCodec is not "copy" and not "none")
        {
            parts.Add("-b:a");
            parts.Add(AudioBitrate);
            if (AudioChannels > 0)
            {
                parts.Add("-ac");
                parts.Add(AudioChannels.ToString());
            }
        }

        if (!string.IsNullOrEmpty(Framerate) && Framerate != "vfr" && Framerate != "source")
        {
            parts.Add("-r");
            parts.Add(Framerate);
        }

        if (Container == "mp4")
        {
            parts.Add("-movflags");
            parts.Add("+faststart");
        }

        parts.Add(output);
        RawCliInput = string.Join(" ", parts);
    }

    public void LoadFromPreset(TranscodePreset preset)
    {
        _isUpdatingFromPreset = true;
        try
        {
            _loadedPreset = preset.Clone();
            Container = preset.Container;
            Codec = preset.Codec;
            Crf = preset.Crf;
            SpeedIndex = preset.SpeedIndex;
            Framerate = preset.Framerate;
            ResolutionLimit = System.Linq.Enumerable.FirstOrDefault(ResolutionOptions, r => r.StartsWith(preset.ResolutionLimit, StringComparison.Ordinal)) ?? preset.ResolutionLimit;
            Width = preset.Width;
            Height = preset.Height;
            Cropping = preset.Cropping;
            CropTop = preset.CropTop;
            CropBottom = preset.CropBottom;
            CropLeft = preset.CropLeft;
            CropRight = preset.CropRight;
            CropMode = (CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0) ? "Custom" : "None";
            Tune = string.IsNullOrEmpty(preset.Tune) ? "none" : preset.Tune;
            PixelFormat = string.IsNullOrEmpty(preset.PixelFormat) ? "yuv420p" : preset.PixelFormat;
            Deinterlace = preset.Deinterlace;
            Denoise = preset.Denoise;
            DenoiseFilter = string.IsNullOrEmpty(preset.DenoiseFilter) ? "hqdn3d=1.5:1.5:6:6" : preset.DenoiseFilter;
            Grayscale = preset.Grayscale;
            AudioCodec = preset.AudioCodec;
            AudioBitrate = preset.AudioBitrate;
            AudioChannels = preset.AudioChannels;
            OnPropertyChanged(nameof(IsCustomCrop));
            OnPropertyChanged(nameof(IsCustomResolution));
            OnPropertyChanged(nameof(AudioChannelsString));
            UpdateCliString();
        }
        finally
        {
            _isUpdatingFromPreset = false;
        }
    }

    public TranscodePreset CreatePresetSnapshot()
    {
        return new TranscodePreset
        {
            Container = Container,
            EncoderPreset = SpeedName,
            Width = Width > 0 ? Width : (_loadedPreset?.Width ?? 0),
            Height = Height > 0 ? Height : (_loadedPreset?.Height ?? 0),
            Tune = Tune == "none" ? "" : Tune,
            AudioChannels = AudioChannels,
            Grayscale = Grayscale,
            PixelFormat = PixelFormat,
            Codec = Codec,
            Crf = Crf,
            SpeedIndex = SpeedIndex,
            Framerate = Framerate,
            ResolutionLimit = ResolutionLimit,
            Cropping = Cropping,
            CropTop = CropTop,
            CropBottom = CropBottom,
            CropLeft = CropLeft,
            CropRight = CropRight,
            Deinterlace = Deinterlace,
            Denoise = Denoise,
            DenoiseFilter = string.IsNullOrEmpty(DenoiseFilter) ? "hqdn3d=1.5:1.5:6:6" : DenoiseFilter,
            AudioCodec = AudioCodec,
            AudioBitrate = AudioBitrate
        };
    }
}


