using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SQUEEZE.ViewModels;
using SQUEEZE.Services;

namespace SQUEEZE.Views;

public partial class MainView : UserControl
{
    private readonly ConcurrentDictionary<string, IStorageFile> _pendingSaveFiles = new();

    public MainView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachFilePickerToViewModel();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachFilePickerToViewModel();
    }

    private void AttachFilePickerToViewModel()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.FilePickerAction = OpenFilePickerAsync;
            vm.SaveFilePickerAction = OpenSaveFilePickerAsync;
            vm.OnDownloadCompleted = async (destPath) =>
            {
                if (_pendingSaveFiles.TryRemove(destPath, out var storageFile))
                {
                    try
                    {
                        await using var srcStream = File.OpenRead(destPath);
                        await using var dstStream = await storageFile.OpenWriteAsync();
                        await srcStream.CopyToAsync(dstStream);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SQUEEZE] Failed to write downloaded file to storage destination: {ex.Message}");
                    }
                    finally
                    {
                        try { File.Delete(destPath); } catch { }
                    }
                }
            };
        }
    }

    private async Task<string?> OpenSaveFilePickerAsync(string suggestedFileName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "SQUEEZE // Save Transcoded Output",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = Path.GetExtension(suggestedFileName).TrimStart('.'),
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Media Output")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.webm", "*.*" },
                    MimeTypes = new[] { "video/mp4", "video/x-matroska", "video/webm", "video/*" }
                }
            }
        });

        if (file != null)
        {
            var localPath = file.TryGetLocalPath() ?? (file.Path.IsAbsoluteUri && file.Path.IsFile ? file.Path.LocalPath : null);
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                return localPath;
            }

            // Virtual / Android SAF storage destination: stage locally and flush via OnDownloadCompleted
            var stagingBase = Path.Combine(Path.GetTempPath(), "squeeze_staging", "downloads");
            if (!Directory.Exists(stagingBase))
            {
                Directory.CreateDirectory(stagingBase);
            }

            var tempPath = Path.Combine(stagingBase, $"dl_{Guid.NewGuid():N}_{suggestedFileName}");
            _pendingSaveFiles[tempPath] = file;
            return tempPath;
        }

        return null;
    }

    private async Task<string?> OpenFilePickerAsync()
    {
        MainViewModel.Log("[SQUEEZE_PICKER] OpenFilePickerAsync entering");
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                throw new InvalidOperationException("The file picker is not available yet. Please try again.");
            }

            MainViewModel.Log("[SQUEEZE_PICKER] Calling topLevel.StorageProvider.OpenFilePickerAsync");
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "SQUEEZE // Select Media File",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Media Files")
                    {
                        Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.ts", "*.m4v", "*.flv", "*.wmv", "*.*" },
                        MimeTypes = new[] { "video/*", "application/octet-stream", "*/*" },
                        AppleUniformTypeIdentifiers = new[] { "public.movie", "public.video" }
                    },
                    new("All Files")
                    {
                        Patterns = new[] { "*.*" },
                        MimeTypes = new[] { "*/*" }
                    }
                }
            });

            MainViewModel.Log($"[SQUEEZE_PICKER] OpenFilePickerAsync returned files count: {files?.Count ?? -1}");
            if (files != null && files.Count > 0)
            {
                using var item = files[0];
                MainViewModel.Log($"[SQUEEZE_PICKER] Picked item name='{item.Name}'");
                return await ResolveStorageFilePathAsync(item);
            }
        }
        catch (Exception ex)
        {
            MainViewModel.Log($"[SQUEEZE_PICKER_ERR] OpenFilePickerAsync exception: {ex}");
            ShowFilePickerError(ex);
        }

        return null;
    }

    private async Task<string?> ResolveStorageFilePathAsync(IStorageItem item)
    {
        try
        {
            if (item is not IStorageFile storageFile)
                throw new IOException("The selection is not a readable file.");

            if (DataContext is MainViewModel vm)
                vm.SourceWarningMessage = "Reading selected media stream...";

            var stagingRoot = Path.Combine(Path.GetTempPath(), "squeeze_staging", "uploads");
            return await MediaFileStager.ResolveAsync(storageFile, stagingRoot,
                useLocalPath: !OperatingSystem.IsAndroid());
        }
        catch (Exception ex)
        {
            MainViewModel.Log($"[SQUEEZE_PICKER_ERR] Failed to resolve storage item: {ex}");
            ShowFilePickerError(ex);
            return null;
        }
    }

    private void ShowFilePickerError(Exception exception)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainViewModel vm)
                vm.SourceWarningMessage = $"Failed to read selected file: {exception.Message}";
        });
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        var item = files?.FirstOrDefault();
        if (item != null)
        {
            var path = await ResolveStorageFilePathAsync(item);
            if (!string.IsNullOrWhiteSpace(path) && DataContext is MainViewModel vm)
            {
                vm.LoadSourceFile(path);
                e.Handled = true;
            }
        }
    }
}
