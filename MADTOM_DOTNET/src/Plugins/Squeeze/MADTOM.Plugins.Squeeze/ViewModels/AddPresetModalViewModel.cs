using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SQUEEZE.ViewModels;

public partial class AddPresetModalViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _presetName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public IReadOnlyList<string> Categories { get; set; } = new[] { "Custom" };

    [ObservableProperty]
    private string _selectedCategory = "Custom";

    public Action<string, string>? OnSaveRequested { get; set; }
    public Action? OnCancelRequested { get; set; }

    [RelayCommand]
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(PresetName))
        {
            ErrorMessage = "Preset name cannot be empty.";
            return;
        }

        ErrorMessage = null;
        OnSaveRequested?.Invoke(PresetName.Trim(), Description.Trim());
    }

    [RelayCommand]
    public void Cancel()
    {
        OnCancelRequested?.Invoke();
    }
}

