using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.VideoFeedCenter.ViewModels;

public sealed partial class FeedItemViewModel : ObservableObject
{
    private FeedInfo _info;
    [ObservableProperty]
    private string _lastError = string.Empty;
    public bool IsLive => State == FeedState.Live;
    partial void OnStateChanged(FeedState value) => OnPropertyChanged(nameof(IsLive));

    [ObservableProperty]
    private string _feedId;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private FeedState _state;

    [ObservableProperty]
    private bool _isTrusted;

    [ObservableProperty]
    private string _sourcePlatform;

    [ObservableProperty]
    private uint _srtPort;

    [ObservableProperty]
    private string _srtPassphrase;

    [ObservableProperty]
    private double _currentFps;

    [ObservableProperty]
    private uint _currentBitrateBps;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private double _zoomRatio = 1.0;

    [ObservableProperty]
    private string _requestedResolution = "1920x1080 (30 fps)";

    [ObservableProperty]
    private string _grantedResolution = "Pending";

    [ObservableProperty]
    private string _requestedCodec = "HEVC";

    [ObservableProperty]
    private string _grantedCodec = "Pending";

    [ObservableProperty]
    private double _glassToGlassMs;

    [ObservableProperty]
    private double _networkRttMs;

    [ObservableProperty]
    private double _ingestProcessingMs;

    public FeedInfo Info => _info;

    public FeedItemViewModel(FeedInfo info)
    {
        _info = info;
        _feedId = info.FeedId;
        _displayName = info.DisplayName;
        _state = info.State;
        _isTrusted = info.IsTrusted;
        _sourcePlatform = info.SourcePlatform;
        _srtPort = info.SrtTarget?.Port ?? 0;
        _srtPassphrase = info.SrtTarget?.Passphrase ?? string.Empty;
        _currentFps = info.CurrentFps;
        _currentBitrateBps = info.CurrentBitrateBps;
        _isRecording = info.IsRecording;

        UpdateFromInfo(info);
    }

    public void UpdateFromInfo(FeedInfo info)
    {
        _info = info;
        FeedId = info.FeedId;
        DisplayName = info.DisplayName;
        State = info.State;
        LastError = info.LastError;
        IsTrusted = info.IsTrusted;
        SourcePlatform = info.SourcePlatform;
        SrtPort = info.SrtTarget?.Port ?? 0;
        SrtPassphrase = info.SrtTarget?.Passphrase ?? string.Empty;
        CurrentFps = Math.Round(info.CurrentFps, 1);
        CurrentBitrateBps = info.CurrentBitrateBps;
        IsRecording = info.IsRecording;

        if (info.RequestedProfile != null)
        {
            RequestedResolution = $"{info.RequestedProfile.Width}x{info.RequestedProfile.Height} ({info.RequestedProfile.Fps} fps)";
            RequestedCodec = info.RequestedProfile.Codec.ToUpperInvariant();
        }

        if (info.GrantedProfile != null)
        {
            GrantedResolution = $"{info.GrantedProfile.Width}x{info.GrantedProfile.Height} ({info.GrantedProfile.Fps} fps)";
            GrantedCodec = info.GrantedProfile.Codec.ToUpperInvariant();
        }
        else
        {
            GrantedResolution = "Waiting for source...";
            GrantedCodec = "Waiting for source...";
        }

        var zoomProp = info.Properties.FirstOrDefault(p => string.Equals(p.Id, "camera.zoom_ratio", StringComparison.OrdinalIgnoreCase));
        if (zoomProp != null && Math.Abs(ZoomRatio - zoomProp.Value) > 0.05)
        {
            ZoomRatio = Math.Round(zoomProp.Value, 2);
        }
    }
}
