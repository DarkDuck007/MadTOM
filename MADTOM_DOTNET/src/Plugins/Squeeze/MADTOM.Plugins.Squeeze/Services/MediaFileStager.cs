using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace SQUEEZE.Services;

public static class MediaFileStager
{
    public static async Task<string> ResolveAsync(IStorageFile file, string stagingRoot, bool useLocalPath = true)
    {
        // Android document providers grant access to a stream, not a filesystem path.
        // Do not inspect/convert their URI before opening that stream.
        if (useLocalPath)
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                return localPath;
        }

        return await StageAsync(file.Name, file.OpenReadAsync, stagingRoot);
    }

    public static async Task<string> StageAsync(string name, Func<Task<Stream>> openRead, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "input_media";
        foreach (var character in Path.GetInvalidFileNameChars())
            name = name.Replace(character, '_');
        if (name is "." or "..") name = "input_media";

        var directory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, name);
        try
        {
            await using (var source = await openRead())
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(target);
                await target.FlushAsync();
                if (target.Length == 0)
                    throw new IOException("The selected file is empty.");
            }

            return destination;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
            throw;
        }
    }
}
