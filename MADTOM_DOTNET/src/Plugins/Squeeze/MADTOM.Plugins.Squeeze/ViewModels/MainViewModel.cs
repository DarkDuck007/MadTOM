using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQUEEZE.Models;
using SQUEEZE.Services;

namespace SQUEEZE.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public static Action<string>? LogAction { get; set; }
    public static void Log(string message) => LogAction?.Invoke(message);

    private readonly IPresetService _presetService;
    private readonly ITranscoderBackendService _backendService;
    private readonly IServerDiscoveryService _discoveryService;

    [ObservableProperty]
    private GridLength _sidebarColumnWidth = new GridLength(350, GridUnitType.Pixel);
    private double _previousSidebarWidth = 350;

    [ObservableProperty]
    private ServerNodeInfo _nodeInfo = new();

    [ObservableProperty]
    private string _activePresetTitle = "Fast 1080p30 (Default)";

    [ObservableProperty]
    private TranscodePreset? _activePreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePresetForeground))]
    private bool _isCustomPreset = false;

    public string ActivePresetForeground => IsCustomPreset ? "#F5A623" : "#00F0FF";

    [ObservableProperty]
    private bool _isMegaMenuOpen = false;

    [ObservableProperty]
    private bool _isSettingsOpen = false;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isAddPresetModalOpen = false;

    [ObservableProperty]
    private bool _isJobQueueCollapsed = false;

    [ObservableProperty]
    private bool _isRigInfoOpen = false;

    [ObservableProperty]
    private bool _isRescanningHardware = false;

    [ObservableProperty]
    private bool _isEncoderSummaryExpanded = false;

    [ObservableProperty]
    private AddPresetModalViewModel? _addPresetModal;

    [ObservableProperty]
    private string? _downloadNotificationMessage;

    [ObservableProperty]
    private string? _selectedSourceFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePayloadTitle))]
    private string _selectedSourceFileName = "No media loaded";

    [ObservableProperty]
    private long _selectedSourceFileSize = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePayloadMeta))]
    private string _selectedSourceMeta = "Drop media file here or click [BROWSE FILE]";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePayloadTargetOutput))]
    private string _selectedTargetOutputName = "Pending source selection";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePayloadTitle))]
    [NotifyPropertyChangedFor(nameof(ActivePayloadMeta))]
    [NotifyPropertyChangedFor(nameof(ActivePayloadTargetOutput))]
    [NotifyPropertyChangedFor(nameof(HasActivePayload))]
    private bool _hasSelectedSourceFile = false;

    [ObservableProperty]
    private string? _sourceWarningMessage;

    public Func<Task<string?>>? FilePickerAction { get; set; }
    public Func<string, Task<string?>>? SaveFilePickerAction { get; set; }
    public Func<string, Task>? OnDownloadCompleted { get; set; }

    public string ActivePayloadTitle => HasSelectedSourceFile
        ? SelectedSourceFileName
        : (JobQueue?.SelectedJob != null ? JobQueue.SelectedJob.Name : "NO MEDIA LOADED");

    public string ActivePayloadMeta => HasSelectedSourceFile
        ? SelectedSourceMeta
        : (JobQueue?.SelectedJob != null ? JobQueue.SelectedJob.Meta : "Drop media file here or click [BROWSE FILE]");

    public string ActivePayloadTargetOutput => HasSelectedSourceFile
        ? $"TARGET OUTPUT: {SelectedTargetOutputName}"
        : (JobQueue?.SelectedJob != null ? $"TARGET OUTPUT: {JobQueue.SelectedJob.TargetOutputName}" : "TARGET OUTPUT: Pending file selection");

    public bool HasActivePayload => HasSelectedSourceFile || (JobQueue?.SelectedJob != null);

    public void NotifyPayloadChanged()
    {
        OnPropertyChanged(nameof(ActivePayloadTitle));
        OnPropertyChanged(nameof(ActivePayloadMeta));
        OnPropertyChanged(nameof(ActivePayloadTargetOutput));
        OnPropertyChanged(nameof(HasActivePayload));
    }

    public JobQueueViewModel JobQueue { get; }
    public TranscodeParamsViewModel TranscodeParams { get; }
    public TelemetryViewModel Telemetry { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private ObservableCollection<TranscodePreset> _generalPresets = new();

    [ObservableProperty]
    private ObservableCollection<TranscodePreset> _webPresets = new();

    [ObservableProperty]
    private ObservableCollection<TranscodePreset> _hardwarePresets = new();

    [ObservableProperty]
    private ObservableCollection<TranscodePreset> _productionPresets = new();

    [ObservableProperty]
    private ObservableCollection<TranscodePreset> _customPresets = new();

    public MainViewModel() : this(new PresetService(), new HttpTranscoderBackendService(), new MdnsServerDiscoveryService())
    {
    }

    public MainViewModel(IPresetService presetService, ITranscoderBackendService backendService, IServerDiscoveryService? discoveryService = null)
    {
        _presetService = presetService;
        _backendService = backendService;
        _discoveryService = discoveryService ?? new MdnsServerDiscoveryService();

        Settings = new SettingsViewModel(_backendService, _discoveryService);
        Settings.OnClose = () => IsSettingsOpen = false;


        JobQueue = new JobQueueViewModel(_backendService);
        JobQueue.OnAddJobRequested = async () => await HandleAddJobAsync();
        TranscodeParams = new TranscodeParamsViewModel();
        Telemetry = new TelemetryViewModel(_backendService);

        // Hook parameter change detection: any manual modification turns active preset to "Custom"
        TranscodeParams.OnParameterChangedByUser = OnTranscodeParameterChangedManually;

        // Hook job queue interactions
        JobQueue.OnJobSelected = OnJobSelectedFromQueue;
        JobQueue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobQueue.SelectedJob))
            {
                NotifyPayloadChanged();
                Telemetry.ShowJob(JobQueue.SelectedJob);
            }
        };
        JobQueue.OnDownloadRequested = async (job) => await TriggerJobDownload(job);
        Telemetry.OnDownloadTriggered = async () =>
        {
            if (JobQueue.SelectedJob != null)
            {
                await TriggerJobDownload(JobQueue.SelectedJob);
            }
        };

        // Listen for preset catalog changes (e.g. server presets synced)
        _presetService.PresetsReloaded += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RefreshPresetCollections();
                ApplyDefaultStartupPreset();
            });
        };

        // Listen for backend connection status changes
        _backendService.ConnectionStatusChanged += async (s, connected) =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) await OnConnectionStatusChanged(connected);
            else Avalonia.Threading.Dispatcher.UIThread.Post(async () => await OnConnectionStatusChanged(connected));
        };

        // Load presets and default
        RefreshPresetCollections();
        ApplyDefaultStartupPreset();

        // Auto-connect if configured
        _ = AutoConnectOrInitializeAsync();
    }

    private async Task AutoConnectOrInitializeAsync()
    {
        if (Settings.AutoConnect && !string.IsNullOrWhiteSpace(Settings.ServerUrl))
        {
            await _backendService.ConnectAsync(Settings.ServerUrl, Settings.AuthToken);

        }
        else
        {
            await InitializeNodeInfoAsync();
        }
    }

    private async Task OnConnectionStatusChanged(bool connected)
    {
        await InitializeNodeInfoAsync();
        if (connected)
        {
            var serverPresets = await _backendService.GetPresetsAsync();
            if (serverPresets.Count > 0)
            {
                _presetService.LoadServerPresets(serverPresets);
            }
            await JobQueue.LoadJobsAsync();
        }
        RefreshPresetCollections();
    }

    private System.Threading.Timer? _healthPollTimer;

    private async Task InitializeNodeInfoAsync()
    {
        NodeInfo = await _backendService.GetNodeInfoAsync();
        StartHealthPolling();
    }

    private void StartHealthPolling()
    {
        _healthPollTimer?.Dispose();
        _healthPollTimer = new System.Threading.Timer(async _ =>
        {
            if (_backendService.IsConnected)
            {
                try
                {
                    var info = await _backendService.GetNodeInfoAsync();
                    void Update() => NodeInfo = info;
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Update();
                    else Avalonia.Threading.Dispatcher.UIThread.Post(Update);
                }
                catch { }
            }
        }, null, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
    }

    private void ApplyDefaultStartupPreset()
    {
        var defaultKey = _presetService.DefaultPresetKey;
        var preset = _presetService.GetPresetByKey(defaultKey) ?? _presetService.GetAllPresets().FirstOrDefault();
        if (preset != null)
        {
            SelectPreset(preset);
        }
    }

    public void OnTranscodeParameterChangedManually()
    {
        if (HasSelectedSourceFile)
        {
            SelectedTargetOutputName = $"{Path.GetFileNameWithoutExtension(SelectedSourceFileName)}_squeezed.{TranscodeParams.Container}";
            NotifyPayloadChanged();
        }
        ActivePresetTitle = "Custom";
        IsCustomPreset = true;
    }

    private void OnJobSelectedFromQueue(TranscodeJob job)
    {
        HasSelectedSourceFile = false;
        NotifyPayloadChanged();
        var preset = job.Options ?? _presetService.GetPresetByKey(job.PresetKey);
        if (preset != null)
        {
            TranscodeParams.LoadFromPreset(preset);
            ActivePreset = preset;
            ActivePresetTitle = string.IsNullOrWhiteSpace(preset.Title) ? "Custom" : preset.Title;
            IsCustomPreset = string.IsNullOrWhiteSpace(preset.Key);
        }
    }

    [RelayCommand]
    public void SelectPreset(TranscodePreset? preset)
    {
        if (preset == null) return;

        ActivePreset = preset;
        ActivePresetTitle = preset.Title;
        IsCustomPreset = false;
        TranscodeParams.LoadFromPreset(preset);
        if (HasSelectedSourceFile) SelectedTargetOutputName = $"{Path.GetFileNameWithoutExtension(SelectedSourceFileName)}_squeezed.{TranscodeParams.Container}";
        NotifyPayloadChanged();
        IsMegaMenuOpen = false;
    }

    [RelayCommand]
    public void SetDefaultPreset()
    {
        if (ActivePreset != null && !IsCustomPreset)
        {
            _presetService.SetDefaultPresetKey(ActivePreset.Key);
            RefreshPresetCollections();
        }
    }

    [RelayCommand]
    public void ToggleMegaMenu()
    {
        IsMegaMenuOpen = !IsMegaMenuOpen;
    }

    [RelayCommand]
    public void CloseMegaMenu()
    {
        IsMegaMenuOpen = false;
    }

    [RelayCommand]
    public void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    [RelayCommand]
    public async Task BrowseFilesAsync()
    {
        Console.WriteLine($"[SQUEEZE_DEBUG] BrowseFilesAsync: FilePickerAction is {(FilePickerAction != null ? "SET" : "NULL")}");
        if (FilePickerAction != null)
        {
            var path = await FilePickerAction.Invoke();
            Console.WriteLine($"[SQUEEZE_DEBUG] BrowseFilesAsync: received path='{path}'");
            if (!string.IsNullOrWhiteSpace(path))
            {
                LoadSourceFile(path);
            }
        }
    }

    public void LoadSourceFile(string path)
    {
        Action update = () =>
        {
            Console.WriteLine($"[SQUEEZE_DEBUG] LoadSourceFile: '{path}'");
            Log($"[SQUEEZE_DEBUG] LoadSourceFile: '{path}'");
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) throw new FileNotFoundException("The selected file no longer exists.");

                SelectedSourceFilePath = fi.FullName;
                SelectedSourceFileName = fi.Name;
                SelectedSourceFileSize = fi.Length;
                var extStr = string.IsNullOrWhiteSpace(fi.Extension) ? "VIDEO" : fi.Extension.TrimStart('.').ToUpperInvariant();
                SelectedSourceMeta = $"{FormatFileSize(fi.Length)} • {extStr} Media";

                string stem = Path.GetFileNameWithoutExtension(fi.Name);
                string ext = "." + TranscodeParams.Container;
                SelectedTargetOutputName = $"{stem}_squeezed{ext}";

                JobQueue.SelectedJob = null;
                HasSelectedSourceFile = true;
                SourceWarningMessage = null;
                NotifyPayloadChanged();
            }
            catch (Exception ex)
            {
                HasSelectedSourceFile = false;
                SelectedSourceFilePath = null;
                SourceWarningMessage = $"Error loading file: {ex.Message}";
                Log($"[SQUEEZE_ERR] LoadSourceFile failed: {ex.Message}");
            }
        };

        if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            update();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(update);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    [ObservableProperty]
    private bool _isSubmitting;

    public async Task HandleAddJobAsync()
    {
        if (IsSubmitting) return;
        IsSubmitting = true;
        try { await SubmitSourceAsync(); }
        catch (Exception ex) { SourceWarningMessage = $"Submission failed: {ex.Message}"; }
        finally { IsSubmitting = false; }
    }

    private async Task SubmitSourceAsync()
    {
        if (!HasSelectedSourceFile)
        {
            if (FilePickerAction != null)
            {
                var path = await FilePickerAction.Invoke();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    LoadSourceFile(path);
                }
                else
                {
                    SourceWarningMessage = "NO MEDIA FILE SELECTED // Select or drop a file to stage";
                    return;
                }
            }
            else
            {
                SourceWarningMessage = "NO MEDIA FILE SELECTED // Select or drop a file to stage";
                return;
            }
        }

        if (!HasSelectedSourceFile) return;
        string presetKey = IsCustomPreset ? "" : ActivePreset?.Key ?? _presetService.DefaultPresetKey;
        string jobName = SelectedSourceFileName;
        string meta = $"{SelectedSourceMeta} → {TranscodeParams.ResolutionLimit}";
        var options = TranscodeParams.CreatePresetSnapshot();
        options.Key = presetKey;
        options.Title = ActivePresetTitle;
        await JobQueue.EnqueueJobAsync(jobName, meta, presetKey, SelectedSourceFilePath, options);
        SourceWarningMessage = null;
    }

    [RelayCommand]
    public void DismissSourceWarning()
    {
        SourceWarningMessage = null;
    }

    [RelayCommand]
    public async Task StartAsync() => await EnqueueAndStartAsync();

    [RelayCommand]
    public void ToggleEncoderSummaryExpanded()
    {
        IsEncoderSummaryExpanded = !IsEncoderSummaryExpanded;
    }

    [RelayCommand]
    public async Task EnqueueAndStartAsync()
    {
        if (IsSubmitting || JobQueue.SelectedJob?.Status is JobStatus.Encoding or JobStatus.Uploading) return;
        if (JobQueue.SelectedJob != null &&
            (JobQueue.SelectedJob.Status == JobStatus.Queued || JobQueue.SelectedJob.Status == JobStatus.Paused || JobQueue.SelectedJob.Status == JobStatus.Idle))
        {
            if (!await _backendService.StartJobAsync(JobQueue.SelectedJob.Id))
                SourceWarningMessage = "Could not start or resume this job. Check the connection and job status.";
            return;
        }

        await HandleAddJobAsync();
    }

    [RelayCommand]
    public async Task PauseAsync()
    {
        if (JobQueue.SelectedJob != null)
        {
            if (!await _backendService.PauseJobAsync(JobQueue.SelectedJob.Id))
                SourceWarningMessage = "Could not pause this job. The server must support pause and the job must be encoding.";
        }
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        if (JobQueue.SelectedJob != null)
        {
            if (!await _backendService.CancelJobAsync(JobQueue.SelectedJob.Id))
                SourceWarningMessage = "Could not cancel this job.";
            else
                JobQueue.SelectedJob.Status = JobStatus.Cancelled;
        }
    }

    [RelayCommand]
    public void ToggleJobQueueCollapse()
    {
        IsJobQueueCollapsed = !IsJobQueueCollapsed;
        if (IsJobQueueCollapsed)
        {
            if (SidebarColumnWidth.Value > 60) _previousSidebarWidth = SidebarColumnWidth.Value;
            SidebarColumnWidth = new GridLength(38, GridUnitType.Pixel);
        }
        else
        {
            SidebarColumnWidth = new GridLength(_previousSidebarWidth > 100 ? _previousSidebarWidth : 350, GridUnitType.Pixel);
        }
    }

    [RelayCommand]
    public void OpenRigInfo()
    {
        IsRigInfoOpen = true;
    }

    [RelayCommand]
    public void CloseRigInfo()
    {
        IsRigInfoOpen = false;
    }

    [RelayCommand]
    public async Task RescanHardwareAsync()
    {
        if (IsRescanningHardware) return;
        try
        {
            IsRescanningHardware = true;
            var updated = await _backendService.RescanHardwareAsync();
            void Update() => NodeInfo = updated;
            if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Update();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Update);
            }
        }
        catch
        {
            // Ignore rescan failure
        }
        finally
        {
            IsRescanningHardware = false;
        }
    }

    [RelayCommand]
    public void OpenAddPresetModal()
    {
        AddPresetModal = new AddPresetModalViewModel();
        AddPresetModal.OnSaveRequested = (title, desc) =>
        {
            var snapshot = TranscodeParams.CreatePresetSnapshot();
            var newPreset = _presetService.AddCustomPreset(title, desc, snapshot);
            RefreshPresetCollections();
            SelectPreset(newPreset);
            IsAddPresetModalOpen = false;
            AddPresetModal = null;
        };

        AddPresetModal.OnCancelRequested = () =>
        {
            IsAddPresetModalOpen = false;
            AddPresetModal = null;
        };

        IsAddPresetModalOpen = true;
    }

    [RelayCommand]
    public void CloseAddPresetModal()
    {
        IsAddPresetModalOpen = false;
        AddPresetModal = null;
    }

    [RelayCommand]
    public async Task TriggerJobDownload(TranscodeJob? job)
    {
        if (job?.Status != JobStatus.Completed) return;

        try
        {
            string targetFileName = job.TargetOutputName;
            string? destinationPath = null;

            if (SaveFilePickerAction != null)
            {
                destinationPath = await SaveFilePickerAction.Invoke(targetFileName);
            }
            else
            {
                var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(downloadsDir)) Directory.CreateDirectory(downloadsDir);
                destinationPath = Path.Combine(downloadsDir, targetFileName);
            }

            if (string.IsNullOrWhiteSpace(destinationPath)) return;

            DownloadNotificationMessage = $"Downloading {targetFileName} from remote node...";

            await _backendService.DownloadJobAsync(job.Id, destinationPath);
            if (OnDownloadCompleted != null)
            {
                await OnDownloadCompleted.Invoke(destinationPath);
            }
            DownloadNotificationMessage = $"✓ Squeezed output saved to {Path.GetFileName(destinationPath)}";
        }
        catch (Exception ex)
        {
            DownloadNotificationMessage = $"Download failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearDownloadNotification()
    {
        DownloadNotificationMessage = null;
    }

    partial void OnSearchQueryChanged(string value)
    {
        FilterPresets(value);
    }

    private void RefreshPresetCollections()
    {
        FilterPresets(SearchQuery);
    }

    private void FilterPresets(string query)
    {
        var all = _presetService.GetAllPresets();
        if (!string.IsNullOrWhiteSpace(query))
        {
            all = all.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Codec.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        UpdateObservableCollection(GeneralPresets, all.Where(p => p.Category == "General"));
        UpdateObservableCollection(WebPresets, all.Where(p => p.Category == "Web"));
        UpdateObservableCollection(HardwarePresets, all.Where(p => p.Category == "Hardware"));
        UpdateObservableCollection(ProductionPresets, all.Where(p => p.Category == "Production"));
        UpdateObservableCollection(CustomPresets, all.Where(p => p.Category == "Custom"));
    }

    private static void UpdateObservableCollection(ObservableCollection<TranscodePreset> collection, IEnumerable<TranscodePreset> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
