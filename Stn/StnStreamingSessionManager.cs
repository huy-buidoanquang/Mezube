using System.Collections.Concurrent;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Stn;

/// <summary>
/// One STN streaming WebSocket per stream channel so concurrent clan streams do not share
/// disconnect / publisher-ended state.
/// </summary>
public sealed class StnStreamingSessionManager : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StnStreamingSessionManager> _logger;
    private readonly ConcurrentDictionary<long, StnSocketClient> _sessions = new();
    private readonly object _createGate = new();

    public StnStreamingSessionManager(
        BotOptions options,
        ILoggerFactory loggerFactory,
        ILogger<StnStreamingSessionManager> logger)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public StnSocketClient GetOrCreate(long streamChannelId)
    {
        if (_sessions.TryGetValue(streamChannelId, out var existing))
        {
            return existing;
        }

        lock (_createGate)
        {
            if (_sessions.TryGetValue(streamChannelId, out existing))
            {
                return existing;
            }

            var created = new StnSocketClient(
                _options,
                _loggerFactory.CreateLogger<StnSocketClient>());
            _sessions[streamChannelId] = created;
            _logger.LogDebug("Created STN streaming session channel={ChannelId}", streamChannelId);
            return created;
        }
    }

    public bool TryGet(long streamChannelId, out StnSocketClient? client)
        => _sessions.TryGetValue(streamChannelId, out client);

    public async Task RemoveAndDisposeAsync(long streamChannelId)
    {
        if (!_sessions.TryRemove(streamChannelId, out var client))
        {
            return;
        }

        _logger.LogDebug("Disposing STN streaming session channel={ChannelId}", streamChannelId);
        await client.DisposeAsync().ConfigureAwait(false);
    }

    public async Task DisposeAllAsync()
    {
        foreach (var channelId in _sessions.Keys)
        {
            await RemoveAndDisposeAsync(channelId).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
        => await DisposeAllAsync().ConfigureAwait(false);
}
