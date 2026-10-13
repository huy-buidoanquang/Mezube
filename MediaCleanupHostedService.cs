using Mezube.Bot;
using Mezube.Media;
using Mezube.Music;
using Mezube.Sfu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mezube;

/// <summary>Async media teardown on host stop (replaces sync GetAwaiter().GetResult() on ApplicationStopping).</summary>
public sealed class MediaCleanupHostedService : IHostedService
{
    private static readonly TimeSpan PreparedMaxAge = TimeSpan.FromDays(7);

    private readonly IServiceProvider _services;
    private readonly ILogger<MediaCleanupHostedService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public MediaCleanupHostedService(IServiceProvider services, ILogger<MediaCleanupHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = SweepTempDirAsync(_services.GetRequiredService<BotOptions>().TempDir, _stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();

        try
        {
            _logger.LogDebug("Cancelling playback pumps during host shutdown");
            _services.GetRequiredService<MusicPlayer>().CancelAllSessions();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancel playback sessions during shutdown ignored");
        }

        try
        {
            _logger.LogDebug("Disposing all SFU publisher sessions during host shutdown");
            await _services.GetRequiredService<SfuPublisherSessionManager>().DisposeAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose SFU publisher sessions during shutdown");
        }

        try
        {
            _services.GetRequiredService<IConnectionMultiplexer>().Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis multiplexer dispose ignored");
        }
    }

    private static void DeleteFilesWrittenBefore(string dir, string pattern, DateTime cutoffUtc)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, pattern))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
            {
                DownloadedMediaFiles.TryDelete(file);
            }
        }
    }

    private async Task SweepTempDirAsync(string tempDir, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    DeleteFilesWrittenBefore(tempDir, "mezube_*", startedUtc);
                    DeleteFilesWrittenBefore(
                        Path.Combine(tempDir, PreparedAudioCache.FolderName),
                        "*",
                        DateTime.UtcNow - PreparedMaxAge);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Temp dir sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
