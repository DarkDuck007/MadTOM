using System;
using System.Collections.Generic;
using System.Linq;
using SQUEEZE.Models;

namespace SQUEEZE.Services;

public class PresetService : IPresetService
{
    private readonly List<TranscodePreset> _presets = new();
    private string _defaultPresetKey = "fast1080";

    public event EventHandler<string>? DefaultPresetChanged;
    public event EventHandler<TranscodePreset>? CustomPresetAdded;
    public event EventHandler? PresetsReloaded;

    public string DefaultPresetKey => _defaultPresetKey;

    public PresetService()
    {
        InitializeCatalog();
    }

    private void InitializeCatalog()
    {
        // 1. General & Standard
        _presets.Add(new TranscodePreset
        {
            Key = "fast1080",
            Title = "Fast 1080p30 (Default)",
            Category = "General",
            Codec = "libx264",
            Crf = 22,
            SpeedIndex = 4,
            Tag = "DEFAULT",
            Description = "Standard H.264 high-speed delivery profile."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "ultra_compression",
            Title = "Ultra Compression",
            Category = "General",
            Codec = "libx264",
            Crf = 30,
            SpeedIndex = 7, // veryslow
            EncoderPreset = "veryslow",
            Tune = "film",
            Denoise = true,
            DenoiseFilter = "hqdn3d=1.5:1.5:6:6",
            Deinterlace = false,
            Width = 1280,
            Height = 0,
            ResolutionLimit = "720p HD (1280x720)",
            Framerate = "30",
            AudioCodec = "aac",
            AudioBitrate = "64k",
            AudioChannels = 1,
            Container = "mp4",
            PixelFormat = "yuv420p",
            Tag = "TINY",
            Description = "Maximum space saving • H.264 CRF 30 • veryslow • tune film • hqdn3d denoise • 64k mono audio."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "veryfast1080",
            Title = "Very Fast 1080p30",
            Category = "General",
            Codec = "libx264",
            Crf = 24,
            SpeedIndex = 2,
            Description = "High speed draft encode for fast client previews."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "hq1080surround",
            Title = "HQ 1080p30 Surround",
            Category = "General",
            Codec = "libx264",
            Crf = 20,
            SpeedIndex = 5,
            Description = "H.264 High Profile • CRF 20.0 • 5.1 AC3 Audio"
        });
        _presets.Add(new TranscodePreset
        {
            Key = "superfast720",
            Title = "Super Fast 720p30",
            Category = "General",
            Codec = "libx264",
            Crf = 24,
            SpeedIndex = 1,
            Description = "720p resolution limit • Fast turnaround"
        });
        _presets.Add(new TranscodePreset
        {
            Key = "proxy480",
            Title = "Ultrafast Proxy 480p",
            Category = "General",
            Codec = "libx264",
            Crf = 28,
            SpeedIndex = 0,
            Description = "Lowest CPU overhead proxy for NLE editing."
        });

        // 2. Web & Social Media
        _presets.Add(new TranscodePreset
        {
            Key = "discord",
            Title = "Discord 25MB Target",
            Category = "Web",
            Codec = "libx264",
            Crf = 28,
            SpeedIndex = 3,
            Tag = "POPULAR",
            Description = "Automatic bitrate sizing for free Discord uploads."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "youtube4k",
            Title = "YouTube HQ 2160p 4K",
            Category = "Web",
            Codec = "libx265",
            Crf = 18,
            SpeedIndex = 5,
            Description = "HEVC 10-bit • High GOP alignment for VP9 processing."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "vimeopro",
            Title = "Vimeo Pro 1080p",
            Category = "Web",
            Codec = "libx264",
            Crf = 19,
            SpeedIndex = 5,
            Description = "High bitrate H.264 profile for cinema portfolio hosting."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "twitch60",
            Title = "Twitch Stream 1080p60",
            Category = "Web",
            Codec = "libx264",
            Crf = 20,
            SpeedIndex = 3,
            Description = "Constant CBR 6000 kbps • Keyframe interval = 2s."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "tiktok_vert",
            Title = "TikTok Vertical 1080x1920",
            Category = "Web",
            Codec = "libx264",
            Crf = 20,
            SpeedIndex = 3,
            Description = "9:16 portrait orientation cropper & AAC encoder."
        });

        // 3. Hardware Accelerated
        _presets.Add(new TranscodePreset
        {
            Key = "nvenc_fast",
            Title = "NVIDIA NVENC H.264 Fast",
            Category = "Hardware",
            Codec = "h264_nvenc",
            Crf = 22,
            SpeedIndex = 1,
            Tag = "10X SPEED",
            Description = "Offloads transcode to NVIDIA CUDA encoder cores."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "nvenc_hevc",
            Title = "NVIDIA NVENC HEVC 10-bit",
            Category = "Hardware",
            Codec = "hevc_nvenc",
            Crf = 20,
            SpeedIndex = 1,
            Description = "GPU hardware HEVC with spatial adaptive quantization."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "qsv_h264",
            Title = "Intel QuickSync H.264",
            Category = "Hardware",
            Codec = "libx264",
            Crf = 22,
            SpeedIndex = 1,
            Description = "Integrated Intel iGPU hardware execution pipeline."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "vce_amf",
            Title = "AMD VCE / AMF 1080p",
            Category = "Hardware",
            Codec = "libx264",
            Crf = 22,
            SpeedIndex = 1,
            Description = "Radeon GPU hardware video acceleration engine."
        });

        // 4. Production & Archival
        _presets.Add(new TranscodePreset
        {
            Key = "hevc",
            Title = "HEVC Archival Master",
            Category = "Production",
            Codec = "libx265",
            Crf = 24,
            SpeedIndex = 5,
            Tag = "10-BIT",
            Description = "libx265 10-bit pipeline preventing color banding."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "av1_master",
            Title = "SVT-AV1 Next-Gen Master",
            Category = "Production",
            Codec = "libsvtav1",
            Crf = 22,
            SpeedIndex = 5,
            Description = "Maximum efficiency open-standard codec at low bitrates."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "prores_proxy",
            Title = "ProRes Proxy Passthrough",
            Category = "Production",
            Codec = "libx264",
            Crf = 18,
            SpeedIndex = 6,
            Description = "Apple ProRes 422 Proxy format for linear edit suites."
        });
        _presets.Add(new TranscodePreset
        {
            Key = "x264_lossless",
            Title = "H.264 Visual Lossless",
            Category = "Production",
            Codec = "libx264",
            Crf = 16,
            SpeedIndex = 6,
            Description = "CRF 16.0 ultra-high fidelity archival standard."
        });
    }

    public IReadOnlyList<TranscodePreset> GetAllPresets()
    {
        return _presets.ToList();
    }

    public IReadOnlyList<TranscodePreset> GetPresetsByCategory(string category)
    {
        return _presets.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public TranscodePreset? GetPresetByKey(string key)
    {
        return _presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public void SetDefaultPresetKey(string key)
    {
        var preset = GetPresetByKey(key);
        if (preset == null) return;

        // Clear previous DEFAULT tag if it was on another preset
        foreach (var p in _presets)
        {
            if (p.Tag == "DEFAULT") p.Tag = null;
        }

        preset.Tag = "DEFAULT";
        _defaultPresetKey = key;
        DefaultPresetChanged?.Invoke(this, key);
    }

    public TranscodePreset AddCustomPreset(string title, string description, TranscodePreset currentSettings)
    {
        var key = "custom_" + Guid.NewGuid().ToString("N")[..8];
        var custom = currentSettings.Clone();
        custom.Key = key;
        custom.Title = title;
        custom.Description = string.IsNullOrWhiteSpace(description) ? "User created custom profile" : description;
        custom.Category = "Custom";
        custom.Tag = "CUSTOM";

        _presets.Add(custom);
        CustomPresetAdded?.Invoke(this, custom);
        return custom;
    }

    public void LoadServerPresets(IEnumerable<TranscodePreset> serverPresets)
    {
        var incoming = serverPresets.ToList();
        if (incoming.Count == 0) return;

        // Preserve existing user custom presets
        var customList = _presets.Where(p => p.Category == "Custom").ToList();
        _presets.Clear();
        _presets.AddRange(incoming);
        _presets.AddRange(customList);

        // Ensure default preset is valid
        if (!_presets.Any(p => p.Key == _defaultPresetKey))
        {
            var first = _presets.FirstOrDefault(p => p.Tag == "DEFAULT") ?? _presets.FirstOrDefault();
            if (first != null) _defaultPresetKey = first.Key;
        }

        PresetsReloaded?.Invoke(this, EventArgs.Empty);
    }
}

