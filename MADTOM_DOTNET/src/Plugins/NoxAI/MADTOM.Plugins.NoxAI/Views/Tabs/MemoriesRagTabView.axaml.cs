using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class MemoriesRagTabView : UserControl
{
    public MemoriesRagTabView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MemoriesRagTabViewModel vm)
            {
                _ = vm.SearchMemoriesHybrid();
            }
        }
    }

    private void OnProfileSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MemoriesRagTabViewModel vm)
            {
                _ = vm.SearchUserProfiles();
            }
        }
    }

    private void OnMemoriesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || DataContext is not MemoriesRagTabViewModel vm) return;
        if (!vm.HasMoreMemories || vm.IsLoadingMoreMemories || vm.IsSearchResultsActive) return;

        double maxOffset = System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxOffset > 0 && sv.Offset.Y >= maxOffset - 160)
        {
            _ = vm.LoadMoreMemoriesAsync();
        }
    }

    private void OnProfilesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MemoriesRagTabViewModel vm) return;
        if (!vm.HasMoreUserProfiles || vm.IsLoadingMoreUserProfiles || vm.IsProfileSearchResultsActive) return;

        ScrollViewer? sv = sender as ScrollViewer;
        if (sv == null && e.Source is ScrollViewer srcSv) sv = srcSv;
        if (sv == null) return;

        double maxOffset = System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxOffset > 0 && sv.Offset.Y >= maxOffset - 200)
        {
            _ = vm.LoadMoreUserProfilesAsync();
        }
    }

    private void OnListItemAvatarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: UserProfileItemModel profile })
        {
            if (DataContext is MemoriesRagTabViewModel vm)
            {
                vm.OpenAvatarLightbox(profile);
                AvatarLightboxOverlay?.Focus();
            }
            e.Handled = true;
        }
    }

    private void OnDetailAvatarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MemoriesRagTabViewModel vm && vm.SelectedUserProfile != null)
        {
            vm.OpenAvatarLightbox(vm.SelectedUserProfile);
            AvatarLightboxOverlay?.Focus();
            e.Handled = true;
        }
    }

    private void OnAvatarOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MemoriesRagTabViewModel vm)
        {
            vm.CloseAvatarLightbox();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (DataContext is MemoriesRagTabViewModel vm && vm.IsAvatarLightboxOpen)
            {
                vm.CloseAvatarLightbox();
                e.Handled = true;
            }
        }
    }
}
