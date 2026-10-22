using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class MemoryItemModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string EntityKey { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Importance { get; init; } = 0.8;
    public bool IsActive { get; init; } = true;
    public double Score { get; init; }
    public bool HasScore => Score > 0;
    public string ScoreDisplay => Score > 0 ? $"Match: {Score:F2}" : string.Empty;

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
}

public sealed partial class HotMemoryItemModel : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
}

public sealed partial class UserProfileItemModel : ObservableObject
{
    public long UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Bio { get; init; } = string.Empty;
    public string PhotoUniqueId { get; init; } = string.Empty;
    public string PhotoDescription { get; init; } = string.Empty;
    public string AudioTitle { get; init; } = string.Empty;
    public string AudioPerformer { get; init; } = string.Empty;
    public int AudioDuration { get; init; }
    public string Birthdate { get; init; } = string.Empty;
    public string PersonalChatTitle { get; init; } = string.Empty;
    public string PersonalChatUsername { get; init; } = string.Empty;
    public string LastCheckedAt { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public double RelevanceScore { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatarImage;

    [ObservableProperty]
    private bool _isLoadingAvatar;

    public bool HasAvatar => AvatarImage != null;
    public bool HasScore => RelevanceScore > 0;
    public string ScoreDisplay => RelevanceScore > 0 ? $"Relevance: {RelevanceScore:F2}" : string.Empty;

    public string DisplayTitle => !string.IsNullOrWhiteSpace(FullName)
        ? FullName
        : (!string.IsNullOrWhiteSpace(Username) ? $"@{Username}" : $"User {UserId}");

    public string HandleDisplay => !string.IsNullOrWhiteSpace(Username) ? $"@{Username}" : $"ID: {UserId}";

    public bool HasBio => !string.IsNullOrWhiteSpace(Bio);
    public bool HasPhotoDescription => !string.IsNullOrWhiteSpace(PhotoDescription);
    public bool HasBirthdate => !string.IsNullOrWhiteSpace(Birthdate);

    public Avalonia.Media.FlowDirection BioFlowDirection => NoxChatSessionParser.IsRtl(Bio) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment BioTextAlignment => NoxChatSessionParser.IsRtl(Bio) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public Avalonia.Media.FlowDirection PhotoDescriptionFlowDirection => NoxChatSessionParser.IsRtl(PhotoDescription) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment PhotoDescriptionTextAlignment => NoxChatSessionParser.IsRtl(PhotoDescription) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public Avalonia.Media.FlowDirection TitleFlowDirection => NoxChatSessionParser.IsRtl(DisplayTitle) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TitleTextAlignment => NoxChatSessionParser.IsRtl(DisplayTitle) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioTitle) || !string.IsNullOrWhiteSpace(AudioPerformer);
    public string AudioDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AudioTitle) && string.IsNullOrWhiteSpace(AudioPerformer)) return string.Empty;
            var title = string.IsNullOrWhiteSpace(AudioTitle) ? "Audio" : AudioTitle;
            var performer = string.IsNullOrWhiteSpace(AudioPerformer) ? "" : $" - {AudioPerformer}";
            var duration = AudioDuration > 0 ? $" ({AudioDuration / 60}:{AudioDuration % 60:D2})" : "";
            return $"{title}{performer}{duration}";
        }
    }

    public bool HasPersonalChat => !string.IsNullOrWhiteSpace(PersonalChatTitle) || !string.IsNullOrWhiteSpace(PersonalChatUsername);
    public string PersonalChatDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PersonalChatTitle) && string.IsNullOrWhiteSpace(PersonalChatUsername)) return string.Empty;
            if (!string.IsNullOrWhiteSpace(PersonalChatTitle) && !string.IsNullOrWhiteSpace(PersonalChatUsername))
                return $"{PersonalChatTitle} (@{PersonalChatUsername})";
            return !string.IsNullOrWhiteSpace(PersonalChatTitle) ? PersonalChatTitle : $"@{PersonalChatUsername}";
        }
    }
}

public sealed partial class MemoriesRagTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;
    private readonly ConcurrentDictionary<long, Bitmap> _avatarCache = new();

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private int _selectedSubTabIndex;

    // --- Memories & Hot Scratchpad ---
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isSearchResultsActive;
    [ObservableProperty] private string _searchResultsSummary = string.Empty;
    [ObservableProperty] private string _newMemoryContent = string.Empty;
    [ObservableProperty] private string _newMemoryCategory = "fact";
    [ObservableProperty] private string _newMemoryEntity = string.Empty;
    [ObservableProperty] private string _newHotKey = string.Empty;
    [ObservableProperty] private string _newHotContent = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    public ObservableCollection<MemoryItemModel> Memories { get; } = new();
    public ObservableCollection<HotMemoryItemModel> HotMemories { get; } = new();

    // --- User Profiles & Avatars ---
    private readonly System.Threading.SemaphoreSlim _profilesLock = new(1, 1);

    [ObservableProperty] private UserProfileItemModel? _selectedUserProfile;
    [ObservableProperty] private string _profileSearchQuery = string.Empty;
    [ObservableProperty] private bool _isProfileSearching;
    [ObservableProperty] private bool _isProfileSearchResultsActive;
    [ObservableProperty] private string _profileSearchResultsSummary = string.Empty;

    // Full-Screen Avatar Lightbox Modal
    [ObservableProperty] private bool _isAvatarLightboxOpen;
    [ObservableProperty] private Bitmap? _lightboxAvatarImage;
    [ObservableProperty] private string _lightboxTitle = string.Empty;
    [ObservableProperty] private string _lightboxSubtitle = string.Empty;

    public ObservableCollection<UserProfileItemModel> UserProfiles { get; } = new();

    public MemoriesRagTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value != null)
        {
            _ = RefreshMemories();
            _ = RefreshUserProfiles();
        }
        else
        {
            Memories.Clear();
            HotMemories.Clear();
            UserProfiles.Clear();
            SelectedUserProfile = null;
            StatusText = "No node selected";
        }
    }

    partial void OnSelectedSubTabIndexChanged(int value)
    {
        if (value == 1 && UserProfiles.Count == 0 && SelectedNode != null)
        {
            _ = RefreshUserProfiles();
        }
    }

    partial void OnSelectedUserProfileChanged(UserProfileItemModel? value)
    {
        if (value != null && value.AvatarImage == null && !value.IsLoadingAvatar)
        {
            _ = LoadAvatarAsync(value);
        }
    }

    [RelayCommand]
    public async Task RefreshMemories()
    {
        if (SelectedNode == null) return;
        IsSearchResultsActive = false;
        SearchResultsSummary = string.Empty;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.QueryMemoriesAsync(new NoxMemoryQueryRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0,
                Limit = 50
            });

            Memories.Clear();
            foreach (var m in res.Memories)
            {
                Memories.Add(new MemoryItemModel
                {
                    Id = m.Id,
                    Category = m.Category,
                    EntityKey = m.EntityKey,
                    Content = m.Content,
                    Importance = m.Importance,
                    IsActive = m.IsActive
                });
            }

            var hotRes = await client.ListHotMemoriesAsync(new NoxHotMemoryRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0
            });

            HotMemories.Clear();
            foreach (var h in hotRes.Items)
            {
                HotMemories.Add(new HotMemoryItemModel
                {
                    Key = h.Key,
                    Content = h.Content
                });
            }
            StatusText = $"Loaded {Memories.Count} memories and {HotMemories.Count} hot keys.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SearchMemoriesHybrid()
    {
        if (SelectedNode == null) return;
        string query = SearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            await RefreshMemories();
            return;
        }

        IsSearching = true;
        StatusText = "Searching memories (hybrid semantic & BM25)...";
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.SearchMemoriesHybridAsync(new NoxHybridSearchRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0,
                QueryText = query,
                Limit = 50
            });

            Memories.Clear();
            foreach (var r in res.Results)
            {
                var m = r.Memory;
                if (m == null) continue;
                double score = r.DenseScore > 0 ? r.DenseScore : (r.RrfScore > 0 ? Math.Min(1.0, r.RrfScore * 60.0) : 0.0);
                Memories.Add(new MemoryItemModel
                {
                    Id = m.Id,
                    Category = m.Category,
                    EntityKey = m.EntityKey,
                    Content = m.Content,
                    Importance = m.Importance,
                    IsActive = m.IsActive,
                    Score = score
                });
            }

            IsSearchResultsActive = true;
            SearchResultsSummary = $"Semantic Search: {Memories.Count} result{(Memories.Count == 1 ? "" : "s")} for \"{query}\"";
            StatusText = $"Found {Memories.Count} memories matching \"{query}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Semantic search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public async Task ClearSearch()
    {
        SearchQuery = string.Empty;
        await RefreshMemories();
    }

    [RelayCommand]
    public async Task AddMemory()
    {
        if (string.IsNullOrWhiteSpace(NewMemoryContent) || SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            await client.UpsertMemoryAsync(new NoxUpsertMemoryRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0,
                Category = NewMemoryCategory,
                EntityKey = NewMemoryEntity,
                Content = NewMemoryContent.Trim(),
                Importance = 0.8,
                IsActive = true
            });
            NewMemoryContent = string.Empty;
            await RefreshMemories();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add memory: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task AddHotMemory()
    {
        if (string.IsNullOrWhiteSpace(NewHotKey) || SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            await client.UpsertHotMemoryAsync(new NoxUpsertHotMemoryRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0,
                Key = NewHotKey.Trim(),
                Content = NewHotContent.Trim()
            });
            NewHotKey = string.Empty;
            NewHotContent = string.Empty;
            await RefreshMemories();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add hot memory: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteMemory(string id)
    {
        if (SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            await client.DeleteMemoryAsync(new NoxDeleteMemoryRequest
            {
                NodeId = SelectedNode.Id,
                Id = id,
                HardDelete = false
            });
            await RefreshMemories();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to delete: {ex.Message}";
        }
    }

    // =========================================================================
    // User Profiles & Avatars Methods
    // =========================================================================

    [RelayCommand]
    public void OpenAvatarLightbox(UserProfileItemModel? profile = null)
    {
        var target = profile ?? SelectedUserProfile;
        if (target == null) return;

        LightboxAvatarImage = target.AvatarImage;
        LightboxTitle = target.DisplayTitle;
        LightboxSubtitle = target.HandleDisplay;
        IsAvatarLightboxOpen = true;
    }

    [RelayCommand]
    public void CloseAvatarLightbox()
    {
        IsAvatarLightboxOpen = false;
    }

    [RelayCommand]
    public async Task RefreshUserProfiles()
    {
        if (SelectedNode == null) return;
        await _profilesLock.WaitAsync();
        IsProfileSearchResultsActive = false;
        ProfileSearchResultsSummary = string.Empty;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.QueryUserProfilesAsync(new NoxUserProfileQueryRequest
            {
                NodeId = SelectedNode.Id,
                Limit = 100
            });

            UserProfiles.Clear();
            foreach (var p in res.Profiles)
            {
                var item = MapUserProfile(p);
                UserProfiles.Add(item);
            }

            if (SelectedUserProfile == null && UserProfiles.Count > 0)
            {
                SelectedUserProfile = UserProfiles[0];
            }

            StatusText = $"Loaded {UserProfiles.Count} user profiles.";
            TriggerAvatarLoading(UserProfiles);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to query user profiles: {ex}";
        }
        finally
        {
            _profilesLock.Release();
        }
    }

    [RelayCommand]
    public async Task SearchUserProfiles()
    {
        if (SelectedNode == null) return;
        string query = ProfileSearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            await RefreshUserProfiles();
            return;
        }

        await _profilesLock.WaitAsync();
        IsProfileSearching = true;
        StatusText = "Searching user profiles in Qdrant & SQLite...";
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.SearchUserProfilesAsync(new NoxUserProfileSearchRequest
            {
                NodeId = SelectedNode.Id,
                Query = query,
                Limit = 50
            });

            UserProfiles.Clear();
            foreach (var p in res.Profiles)
            {
                var item = MapUserProfile(p);
                UserProfiles.Add(item);
            }

            IsProfileSearchResultsActive = true;
            ProfileSearchResultsSummary = $"Profile Search: {UserProfiles.Count} result{(UserProfiles.Count == 1 ? "" : "s")} for \"{query}\"";
            StatusText = $"Found {UserProfiles.Count} profiles matching \"{query}\".";

            if (UserProfiles.Count > 0)
            {
                SelectedUserProfile = UserProfiles[0];
            }

            TriggerAvatarLoading(UserProfiles);
        }
        catch (Exception ex)
        {
            StatusText = $"Profile search failed: {ex.Message}";
        }
        finally
        {
            IsProfileSearching = false;
            _profilesLock.Release();
        }
    }

    [RelayCommand]
    public async Task ClearProfileSearch()
    {
        ProfileSearchQuery = string.Empty;
        await RefreshUserProfiles();
    }

    public async Task LoadAvatarAsync(UserProfileItemModel profile)
    {
        if (profile.AvatarImage != null || profile.UserId == 0 || SelectedNode == null) return;

        if (_avatarCache.TryGetValue(profile.UserId, out var cached))
        {
            profile.AvatarImage = cached;
            return;
        }

        profile.IsLoadingAvatar = true;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var resp = await client.GetUserProfilePhotoAsync(new NoxUserProfilePhotoRequest
            {
                NodeId = SelectedNode.Id,
                UserId = profile.UserId
            });

            if (resp.PhotoBytes != null && resp.PhotoBytes.Length > 0)
            {
                var raw = resp.PhotoBytes.ToByteArray();
                var bmp = await Task.Run(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(raw);
                        return new Bitmap(ms);
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (bmp != null)
                {
                    _avatarCache[profile.UserId] = bmp;
                    profile.AvatarImage = bmp;
                }
            }
        }
        catch (Exception)
        {
            // Ignore individual avatar load failures
        }
        finally
        {
            profile.IsLoadingAvatar = false;
        }
    }

    private void TriggerAvatarLoading(IEnumerable<UserProfileItemModel> items)
    {
        var list = items.ToList();
        _ = Task.Run(async () =>
        {
            foreach (var item in list)
            {
                if (item.AvatarImage == null)
                {
                    await LoadAvatarAsync(item);
                }
            }
        });
    }

    private static UserProfileItemModel MapUserProfile(NoxUserProfile p)
    {
        return new UserProfileItemModel
        {
            UserId = p.UserId,
            Username = p.Username ?? string.Empty,
            FullName = p.FullName ?? string.Empty,
            Bio = p.Bio ?? string.Empty,
            PhotoUniqueId = p.PhotoUniqueId ?? string.Empty,
            PhotoDescription = p.PhotoDescription ?? string.Empty,
            AudioTitle = p.AudioTitle ?? string.Empty,
            AudioPerformer = p.AudioPerformer ?? string.Empty,
            AudioDuration = p.AudioDuration,
            Birthdate = p.Birthdate ?? string.Empty,
            PersonalChatTitle = p.PersonalChatTitle ?? string.Empty,
            PersonalChatUsername = p.PersonalChatUsername ?? string.Empty,
            LastCheckedAt = p.LastCheckedAt ?? string.Empty,
            CreatedAt = p.CreatedAt ?? string.Empty,
            RelevanceScore = p.RelevanceScore
        };
    }
}
