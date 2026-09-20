using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class ModelInstanceItem : ObservableObject
{
    public string ModelName { get; init; } = string.Empty;
    public string Parameters { get; init; } = string.Empty;
    public string Quantization { get; init; } = "Q4_K_M";
    public string VramUsage { get; init; } = "4.2 GB";
    public string Status { get; init; } = "Loaded";
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _inferenceEngineState = "Coming soon";

    [ObservableProperty]
    private string _gpuStats = "GPU Telemetry: Standby";

    [ObservableProperty]
    private ModelInstanceItem? _selectedModel;

    public ObservableCollection<ModelInstanceItem> LoadedModels { get; } = new();

    public string ModuleTitle => "MADTOM NOX AI CONTROL CENTER";
    public string ModuleSubtitle => "Local LLM orchestration, vLLM / Ollama backends, model routing, and GPU telemetry.";
    public string ComingSoonMessage => "This module is in active development and will integrate with local AI accelerators and inference engines.";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void UnloadModel()
    {
        InferenceEngineState = "Coming soon";
    }
}
