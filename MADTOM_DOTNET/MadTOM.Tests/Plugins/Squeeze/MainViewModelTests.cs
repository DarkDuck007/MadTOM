using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using Xunit;

namespace SQUEEZE.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Startup_InitializesWithDefaultPreset()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        Assert.Contains("Fast 1080p30", mainVm.ActivePresetTitle);
        Assert.False(mainVm.IsCustomPreset);
        Assert.Equal("libx264", mainVm.TranscodeParams.Codec);
        Assert.Equal(22.0, mainVm.TranscodeParams.Crf);
    }

    [Fact]
    public void ManualParameterChange_SwitchesActivePresetToCustom()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        // Manually alter CRF
        mainVm.TranscodeParams.Crf = 16.5;

        Assert.Equal("Custom", mainVm.ActivePresetTitle);
        Assert.True(mainVm.IsCustomPreset);
    }

    [Fact]
    public void SelectingPresetFromCatalog_UpdatesActivePreset_AndClearsCustomFlag()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        // First modify to make it Custom
        mainVm.TranscodeParams.Codec = "h264_nvenc";
        Assert.True(mainVm.IsCustomPreset);

        // Now select a preset from catalog
        var hevcPreset = presetService.GetPresetByKey("hevc");
        Assert.NotNull(hevcPreset);

        mainVm.SelectPreset(hevcPreset);

        Assert.Equal(hevcPreset.Title, mainVm.ActivePresetTitle);
        Assert.False(mainVm.IsCustomPreset);
        Assert.Equal("libx265", mainVm.TranscodeParams.Codec);
        Assert.Equal(24.0, mainVm.TranscodeParams.Crf);
    }

    [Fact]
    public void AddPresetModal_Workflow_CreatesAndSelectsNewCustomPreset()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        // Set custom params
        mainVm.TranscodeParams.Codec = "libsvtav1";
        mainVm.TranscodeParams.Crf = 26.0;

        // Open Add Preset Modal
        mainVm.OpenAddPresetModal();
        Assert.True(mainVm.IsAddPresetModalOpen);
        Assert.NotNull(mainVm.AddPresetModal);

        // Save new preset
        mainVm.AddPresetModal.PresetName = "AV1 Lightweight 1080p";
        mainVm.AddPresetModal.Description = "Optimized SVT-AV1 preset for web delivery";
        mainVm.AddPresetModal.Save();

        // Modal should close and active preset should be the new custom preset
        Assert.False(mainVm.IsAddPresetModalOpen);
        Assert.Null(mainVm.AddPresetModal);
        Assert.Equal("AV1 Lightweight 1080p", mainVm.ActivePresetTitle);
        Assert.False(mainVm.IsCustomPreset);

        // Should appear in custom presets collection
        Assert.Contains(mainVm.CustomPresets, p => p.Title == "AV1 Lightweight 1080p");
    }

    [Fact]
    public void SetDefaultPreset_PersistsNewDefaultPreset()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        // Select Discord preset
        var discordPreset = presetService.GetPresetByKey("discord");
        Assert.NotNull(discordPreset);
        mainVm.SelectPreset(discordPreset);

        // Set as default
        mainVm.SetDefaultPreset();

        Assert.Equal("discord", presetService.DefaultPresetKey);
        Assert.Contains("Discord 25MB Target", mainVm.ActivePresetTitle);
    }

    [Fact]
    public async Task EnqueueAndStartCommand_StartsActiveJob()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();

        // Select first idle job
        var idleJob = mainVm.JobQueue.Jobs.First(j => j.Status == JobStatus.Idle);
        mainVm.JobQueue.SelectJob(idleJob);

        await mainVm.EnqueueAndStartAsync();

        Assert.Equal(JobStatus.Encoding, idleJob.Status);
    }

    [Fact]
    public async Task PauseCommand_PausesActiveJob()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();

        var job = mainVm.JobQueue.Jobs.First();
        mainVm.JobQueue.SelectJob(job);

        await mainVm.EnqueueAndStartAsync();
        Assert.Equal(JobStatus.Encoding, job.Status);

        await mainVm.PauseAsync();
        Assert.Equal(JobStatus.Paused, job.Status);
    }

    [Fact]
    public void SearchFilter_FiltersPresetsCorrectly()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        mainVm.SearchQuery = "discord";

        Assert.Single(mainVm.WebPresets);
        Assert.Equal("Discord 25MB Target", mainVm.WebPresets[0].Title);
        Assert.Empty(mainVm.GeneralPresets);
        Assert.Empty(mainVm.HardwarePresets);
        Assert.Empty(mainVm.ProductionPresets);
    }

    [Fact]
    public void LoadSourceFile_WithValidFile_UpdatesSourcePayloadAndTargetOutput()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tempFile, "sample video test stream content");
            mainVm.LoadSourceFile(tempFile);

            Assert.True(mainVm.HasSelectedSourceFile);
            Assert.Equal(System.IO.Path.GetFileName(tempFile), mainVm.SelectedSourceFileName);
            Assert.Contains("Media", mainVm.SelectedSourceMeta);
            Assert.Contains("_squeezed", mainVm.SelectedTargetOutputName);
            Assert.Null(mainVm.SourceWarningMessage);
            Assert.Equal(mainVm.SelectedSourceFileName, mainVm.ActivePayloadTitle);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BrowseFilesAsync_InvokesFilePickerAndLoadsFile()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tempFile, "video test data");
            mainVm.FilePickerAction = () => Task.FromResult<string?>(tempFile);

            await mainVm.BrowseFilesAsync();

            Assert.True(mainVm.HasSelectedSourceFile);
            Assert.Equal(System.IO.Path.GetFileName(tempFile), mainVm.SelectedSourceFileName);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AddJob_WithoutFileAndCancelledPicker_SetsWarningAndDoesNotAddJob()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();
        var initialCount = mainVm.JobQueue.Jobs.Count;

        // User cancels picker
        mainVm.FilePickerAction = () => Task.FromResult<string?>(null);

        await mainVm.HandleAddJobAsync();

        Assert.NotNull(mainVm.SourceWarningMessage);
        Assert.Contains("NO MEDIA FILE SELECTED", mainVm.SourceWarningMessage);
        Assert.Equal(initialCount, mainVm.JobQueue.Jobs.Count);
    }

    [Fact]
    public async Task AddJob_WithStagedFile_EnqueuesJobWithRealFilename()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();
        var initialCount = mainVm.JobQueue.Jobs.Count;

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tempFile, "video stream payload");
            mainVm.LoadSourceFile(tempFile);

            await mainVm.HandleAddJobAsync();

            Assert.Equal(initialCount + 1, mainVm.JobQueue.Jobs.Count);
            Assert.Equal(System.IO.Path.GetFileName(tempFile), mainVm.JobQueue.SelectedJob?.Name);
            Assert.Null(mainVm.SourceWarningMessage);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EnqueueAndStart_WithoutFile_SetsWarningMessage()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();

        // Clear queue selection
        mainVm.JobQueue.SelectedJob = null;
        mainVm.FilePickerAction = () => Task.FromResult<string?>(null);

        await mainVm.EnqueueAndStartAsync();

        Assert.NotNull(mainVm.SourceWarningMessage);
        Assert.Contains("NO MEDIA FILE SELECTED", mainVm.SourceWarningMessage);
    }

    [Fact]
    public void ToggleJobQueueCollapse_TogglesCollapsedStateAndColumnWidth()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        Assert.False(mainVm.IsJobQueueCollapsed);
        Assert.Equal(350, mainVm.SidebarColumnWidth.Value);

        // Collapse
        mainVm.ToggleJobQueueCollapse();
        Assert.True(mainVm.IsJobQueueCollapsed);
        Assert.Equal(38, mainVm.SidebarColumnWidth.Value);

        // Expand back
        mainVm.ToggleJobQueueCollapse();
        Assert.False(mainVm.IsJobQueueCollapsed);
        Assert.Equal(350, mainVm.SidebarColumnWidth.Value);
    }

    [Fact]
    public void RigInfoDialog_OpenAndClose_ControlsVisibilityState()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        Assert.False(mainVm.IsRigInfoOpen);

        mainVm.OpenRigInfo();
        Assert.True(mainVm.IsRigInfoOpen);

        mainVm.CloseRigInfo();
        Assert.False(mainVm.IsRigInfoOpen);
    }

    [Fact]
    public async Task CancelCommand_CancelsCurrentlySelectedJob()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);
        await mainVm.JobQueue.LoadJobsAsync();

        var jobToCancel = mainVm.JobQueue.Jobs.First(j => j.CanCancel);
        mainVm.JobQueue.SelectedJob = jobToCancel;

        await mainVm.CancelAsync();

        Assert.Equal(JobStatus.Cancelled, jobToCancel.Status);
    }

    [Fact]
    public void ServerNodeInfo_DetailedEncoders_DifferentiatesHardwareAndSoftwareEncoders()
    {
        var nodeInfo = new ServerNodeInfo
        {
            Encoders = new List<string> { "libx264", "h264_nvenc", "h264_vaapi", "hevc_nvenc" },
            HardwareEncoders = new List<string> { "h264_vaapi" }
        };

        var detailed = nodeInfo.DetailedEncoders;
        Assert.Equal(4, detailed.Count);

        var cpuEnc = detailed.First(e => e.Name == "libx264");
        Assert.False(cpuEnc.IsHardware);
        Assert.True(cpuEnc.IsSupported);
        Assert.Equal("CPU", cpuEnc.BadgeText);
        Assert.Equal(1.0, cpuEnc.Opacity);

        var unsupportedHw = detailed.First(e => e.Name == "h264_nvenc");
        Assert.True(unsupportedHw.IsHardware);
        Assert.False(unsupportedHw.IsSupported);
        Assert.Equal("○ NO HW", unsupportedHw.BadgeText);
        Assert.Equal(0.4, unsupportedHw.Opacity);
        Assert.Equal("#6E7681", unsupportedHw.TextColor);

        var supportedHw = detailed.First(e => e.Name == "h264_vaapi");
        Assert.True(supportedHw.IsHardware);
        Assert.True(supportedHw.IsSupported);
        Assert.Equal("⚡ HW", supportedHw.BadgeText);
        Assert.Equal(1.0, supportedHw.Opacity);
        Assert.Equal("#3FB950", supportedHw.TextColor);
    }

    [Fact]
    public async Task RescanHardwareCommand_CallsBackendAndUpdatesNodeInfo()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        Assert.False(mainVm.IsRescanningHardware);

        await mainVm.RescanHardwareCommand.ExecuteAsync(null);

        Assert.False(mainVm.IsRescanningHardware);
        Assert.NotNull(mainVm.NodeInfo);
        Assert.Contains("Rescanned", mainVm.NodeInfo.HardwareStatus);
    }

    [Fact]
    public void ToggleEncoderSummaryExpanded_TogglesState()
    {
        var presetService = new PresetService();
        var backendService = new MockTranscoderBackendService();
        var mainVm = new MainViewModel(presetService, backendService);

        Assert.False(mainVm.IsEncoderSummaryExpanded);
        mainVm.ToggleEncoderSummaryExpanded();
        Assert.True(mainVm.IsEncoderSummaryExpanded);
        mainVm.ToggleEncoderSummaryExpanded();
        Assert.False(mainVm.IsEncoderSummaryExpanded);
    }

    [Fact]
    public void TranscodeJob_CanPauseAndResume_MatchStatus()
    {
        var job = new TranscodeJob { Status = JobStatus.Encoding };
        Assert.True(job.CanPause);
        Assert.False(job.CanResume);
        Assert.True(job.CanCancel);

        job.Status = JobStatus.Paused;
        Assert.False(job.CanPause);
        Assert.True(job.CanResume);
        Assert.True(job.CanCancel);

        job.Status = JobStatus.Completed;
        Assert.False(job.CanPause);
        Assert.False(job.CanResume);
        Assert.False(job.CanCancel);
    }

    [Fact]
    public async Task JobQueueViewModel_PauseAndResumeJobCommands_UpdateJobStatus()
    {
        var backendService = new MockTranscoderBackendService();
        var jobQueue = new JobQueueViewModel(backendService);
        await jobQueue.LoadJobsAsync();

        var job = jobQueue.Jobs.First();
        await backendService.StartJobAsync(job.Id);
        Assert.Equal(JobStatus.Encoding, job.Status);
        Assert.True(job.CanPause);

        await jobQueue.PauseJobCommand.ExecuteAsync(job);
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.True(job.CanResume);

        await jobQueue.ResumeJobCommand.ExecuteAsync(job);
        Assert.Equal(JobStatus.Encoding, job.Status);
        Assert.True(job.CanPause);
    }
}

