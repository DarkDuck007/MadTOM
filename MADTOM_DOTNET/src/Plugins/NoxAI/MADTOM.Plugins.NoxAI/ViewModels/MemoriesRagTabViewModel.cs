using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
}

public sealed partial class HotMemoryItemModel : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed partial class MemoriesRagTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
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
        }
        else
        {
            Memories.Clear();
            HotMemories.Clear();
            StatusText = "No node selected";
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
}
