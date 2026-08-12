using Mezube.Media;
using Mezube.Playback;
using Mezube.Stn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Mezube;

/// <summary>Async media teardown on host stop (replaces sync GetAwaiter().GetResult() on ApplicationStopping).</summary>
public sealed class MediaCleanupHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MediaCleanupHostedService> _logger;

    public MediaCleanupHostedService(IServiceProvider services, ILogger<MediaCleanupHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Stopping all voice rooms during host shutdown");
            await _services.GetRequiredService<VoiceChannelSink>().StopAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop voice rooms during shutdown");
        }

        try
        {
            _logger.LogDebug("Stopping all active WHIP publishers during host shutdown");
            await _services.GetRequiredService<WhipFfmpegPublisher>().StopAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop all WHIP publishers during shutdown");
        }

        try
        {
            _logger.LogDebug("Disposing all STN streaming sessions during host shutdown");
            await _services.GetRequiredService<StnStreamingSessionManager>().DisposeAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose STN streaming sessions during shutdown");
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
}
