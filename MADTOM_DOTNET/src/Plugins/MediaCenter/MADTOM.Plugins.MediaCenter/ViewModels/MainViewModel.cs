using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.MediaCenter.ViewModels;

public sealed partial class MediaItem : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public string Format { get; init; } = "H.265 4K";
    public string Status { get; init; } = "Ready";
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _libraryStatus = "Coming soon";

    [ObservableProperty]
    private string _activePlayerState = "Coming soon";

    [ObservableProperty]
    private MediaItem? _selectedMedia;

    public ObservableCollection<MediaItem> LibraryItems { get; } = new();

    public string ModuleTitle => "MADTOM MEDIA CENTER";
    public string ModuleSubtitle => "Centralized media streaming, DLNA/UPnP library indexing, and transcoding pipeline.";
    public string ComingSoonMessage => "This module is in active development and will manage home theater libraries, NAS metadata, and local renderers.";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void PlayMedia()
    {
        ActivePlayerState = "Coming soon";
    }
}
