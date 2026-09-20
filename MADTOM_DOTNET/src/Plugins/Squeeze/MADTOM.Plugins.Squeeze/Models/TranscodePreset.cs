namespace SQUEEZE.Models;

public class TranscodePreset
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string Container { get; set; } = "mp4";
    public string? EncoderPreset { get; set; }
    public string Tune { get; set; } = string.Empty;
    public int AudioChannels { get; set; }
    public bool Grayscale { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Codec { get; set; } = "libx264";
    public double Crf { get; set; } = 22.0;
    public int SpeedIndex { get; set; } = 4; // medium
    public string Description { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string Framerate { get; set; } = "vfr";
    public string ResolutionLimit { get; set; } = "1080p";
    public string Cropping { get; set; } = "Automatic";
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLeft { get; set; }
    public int CropRight { get; set; }
    public string PixelFormat { get; set; } = "yuv420p";
    public bool Deinterlace { get; set; } = true;
    public string DenoiseFilter { get; set; } = "hqdn3d";
    public bool Denoise { get; set; } = false;
    public string AudioCodec { get; set; } = "aac";
    public string AudioBitrate { get; set; } = "160k";

    public TranscodePreset Clone()
    {
        return (TranscodePreset)MemberwiseClone();
    }
}

