using System;
using System.IO;
using System.Threading.Tasks;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class MediaFileStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "squeeze-stager-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NonSeekableSelection_PreservesNameAndBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var file = new StorageFile("my video.mkv", () => new NonSeekableStream(bytes));

        var path = await MediaFileStager.StageAsync(file.Name, file.OpenReadAsync, _root);

        Assert.Equal("my video.mkv", Path.GetFileName(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        // Uploads must be able to reopen and seek the staged file.
        using var upload = File.OpenRead(path);
        Assert.True(upload.CanSeek);
    }

    [Fact]
    public async Task EmptySelection_ReportsFailureAndRemovesStagingDirectory()
    {
        using var file = new StorageFile("empty.mp4", () => new MemoryStream());
        await Assert.ThrowsAsync<IOException>(() => MediaFileStager.StageAsync(file.Name, file.OpenReadAsync, _root));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task ProviderFailure_IsPropagatedAndDoesNotLeavePartialFile()
    {
        using var file = new StorageFile("video.mp4", () => throw new UnauthorizedAccessException("Access expired"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MediaFileStager.StageAsync(file.Name, file.OpenReadAsync, _root));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task RepeatedSelectionsWithSameName_PreserveBothFiles()
    {
        using var first = new StorageFile("video.mp4", () => new MemoryStream(new byte[] { 1 }));
        using var second = new StorageFile("video.mp4", () => new MemoryStream(new byte[] { 2 }));
        var firstPath = await MediaFileStager.StageAsync(first.Name, first.OpenReadAsync, _root);
        var secondPath = await MediaFileStager.StageAsync(second.Name, second.OpenReadAsync, _root);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(secondPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class StorageFile(string name, Func<Stream> open) : IDisposable
    {
        public string Name => name;
        public Task<Stream> OpenReadAsync() => Task.FromResult(open());
        public void Dispose() { }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
