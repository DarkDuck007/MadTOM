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
