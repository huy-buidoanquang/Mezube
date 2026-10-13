using Mezube.Bot;
using Mezube.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class TempDirCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mezube-clean-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task StartAsync_deletes_work_files_and_week_old_prepared_audio()
    {
        var prepared = Path.Combine(_dir, PreparedAudioCache.FolderName);
        Directory.CreateDirectory(prepared);
        var work = Write(Path.Combine(_dir, "mezube_abc.webm.part"), DateTime.UtcNow.AddMinutes(-1));
        var workOgg = Write(Path.Combine(_dir, "mezube_abc.normalized.ogg"), DateTime.UtcNow.AddMinutes(-1));
        var other = Write(Path.Combine(_dir, "keep.txt"), DateTime.UtcNow.AddDays(-30));
        var stale = Write(Path.Combine(prepared, "youtube_old.normalized.ogg"), DateTime.UtcNow.AddDays(-8));
        var fresh = Write(Path.Combine(prepared, "youtube_new.normalized.ogg"), DateTime.UtcNow.AddDays(-6));
        var service = Service(_dir);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(work));
        Assert.False(File.Exists(workOgg));
        Assert.True(File.Exists(other));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task StartAsync_ignores_missing_temp_dir()
    {
        var service = Service(Path.Combine(_dir, "missing"));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
    }

    private static MediaCleanupHostedService Service(string tempDir)
        => new(
            new ServiceCollection().AddSingleton(new BotOptions { TempDir = tempDir }).BuildServiceProvider(),
            NullLogger<MediaCleanupHostedService>.Instance);

    private static string Write(string path, DateTime lastWriteUtc)
    {
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
