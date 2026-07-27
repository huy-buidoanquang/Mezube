using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Mezube.Commands;
using Mezube.Music;
using Mezube.Ui;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MezonLogLevel = Mezon.Net.Logging.LogLevel;

namespace Mezube.Bot;

public sealed class MezubeBot : BackgroundService
{
    private static readonly AsyncLocal<(long ClanId, long ChannelId)?> RateLimitNotifyTarget = new();

    private readonly BotOptions _options;
    private readonly MusicPlayer _player;
    private readonly BindStore _binds;
    private readonly StreamingChannelSinkHolder _streamingHolder;
    private readonly VoiceChannelSinkHolder _voiceHolder;
    private readonly MusicVizAssets _viz;
    private readonly ILogger<MezubeBot> _logger;
    private MezonClient? _client;
    private long _lastRateLimitNotifyMs;

    public MezubeBot(
        BotOptions options,
        MusicPlayer player,
        BindStore binds,
        StreamingChannelSinkHolder streamingHolder,
        VoiceChannelSinkHolder voiceHolder,
        MusicVizAssets viz,
        ILogger<MezubeBot> logger)
    {
        _options = options;
        _player = player;
        _binds = binds;
        _streamingHolder = streamingHolder;
        _voiceHolder = voiceHolder;
        _viz = viz;
        _logger = logger;
    }

    public MezonClient Client => _client ?? throw new InvalidOperationException("Bot not started.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        var clientOptions = new MezonClientOptions(_options.BotId, _options.Token, _options.Host, _options.Port, _options.UseSsl)
        {
            ServerKey = _options.ServerKey,
            TransportType = Mezon.Net.Core.TransportType.WebSocket,
            LogLevel = MezonLogLevel.Trace,
            MaxTransportRequestsPerSecond = 60,
            MaxTransportRequestsPerMinute = 500,
            DefaultRatelimitCallback = TransportRateLimitedHandlerAsync,
        };

        await using var client = new MezonClient(clientOptions);
        _client = client;
        _streamingHolder.SetClient(client);
        _voiceHolder.SetClient(client);
        WireClientLog(client);

        var commands = new CommandService(_options.CommandPrefix);
        commands.Use(async (ctx, next) =>
        {
            RateLimitNotifyTarget.Value = (ctx.Channel.ClanId, ctx.Channel.Id);
            try
            {
                await next(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Command {Command} failed", ctx.Name);
                try
                {
                    await ctx.ReplyAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to send awkward error reply");
                }
            }
            finally
            {
                RateLimitNotifyTarget.Value = null;
            }
        });
        new MusicCommandModule(_player, _options.CommandPrefix).Register(commands);
        client.UseCommands(commands);

        var interactions = new InteractionRouter();
        interactions.OnButton(MezubeButtonId.PlayerControlsPrefix, async ctx =>
        {
            if (!MezubeButtonId.TryParse(ctx.Interaction.CustomId, out var parts)
                || parts.InteractionFunction != MezubeButtonId.PlayerControls)
            {
                return;
            }

            RateLimitNotifyTarget.Value = (ctx.Channel.ClanId, ctx.Channel.Id);
            try
            {
                var clanId = parts.ClanId ?? ctx.Channel.ClanId;
                switch (parts.Action)
                {
                    case MezubeButtonId.ActionSkip:
                        {
                            var ok = await _player.SkipInternalAsync(clanId, ctx.CancellationToken).ConfigureAwait(false);
                            await ctx.RespondAsync(ok
                                    ? PlayerMessageBuilder.Ok("Skipped", "Moved to the next track (if any).")
                                    : PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."))
                                .ConfigureAwait(false);
                            break;
                        }
                    case MezubeButtonId.ActionStop:
                        {
                            await _player.StopInternalAsync(clanId, ctx.CancellationToken).ConfigureAwait(false);
                            await ctx.RespondAsync(PlayerMessageBuilder.Ok("Stopped", "Playback stopped and queue cleared."))
                                .ConfigureAwait(false);
                            break;
                        }
                }
            }
            catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Button {ButtonId} failed", ctx.Interaction.CustomId);
                try
                {
                    await ctx.RespondAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to send awkward error reply for button");
                }
            }
            finally
            {
                RateLimitNotifyTarget.Value = null;
            }
        });
        client.UseInteractions(interactions);

        client.Ready += async () =>
        {
            _logger.LogInformation(
                "Mezube ready. botId={BotId} latency={Latency}ms prefix={Prefix}",
                client.BotId,
                client.Latency,
                _options.CommandPrefix);
            try
            {
                await _viz.EnsureAsync(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Music viz warm-up failed");
            }
        };

        client.VoiceJoined += data =>
        {
            VoiceJoinedEventResponse joined = data;
            _logger.LogInformation(
                "VoiceJoined userId={UserId} channelId={ChannelId} clanId={ClanId} label={Label}",
                joined.UserId,
                joined.VoiceChannelId,
                joined.ClanId,
                joined.VoiceChannelLabel);
            _binds.SetUserVoiceChannel(joined.UserId, joined.VoiceChannelId);
            return Task.CompletedTask;
        };

        client.VoiceLeaved += data =>
        {
            VoiceLeavedEventResponse left = data;
            _logger.LogInformation(
                "VoiceLeaved userId={UserId} channelId={ChannelId} clanId={ClanId}",
                left.VoiceUserId,
                left.VoiceChannelId,
                left.ClanId);
            _binds.ClearUserVoiceChannel(left.VoiceUserId);
            return Task.CompletedTask;
        };

        client.VoiceStarted += data =>
        {
            VoiceStartedEventResponse started = data;
            _logger.LogInformation(
                "VoiceStarted channelId={ChannelId} clanId={ClanId} id={Id}",
                started.VoiceChannelId,
                started.ClanId,
                started.Id);
            return Task.CompletedTask;
        };

        client.VoiceEnded += data =>
        {
            VoiceEndedEventResponse ended = data;
            _logger.LogInformation(
                "VoiceEnded channelId={ChannelId} clanId={ClanId}",
                ended.VoiceChannelId,
                ended.ClanId);
            return Task.CompletedTask;
        };

        _logger.LogInformation(
            "Logging in bot {BotId} to {Host}:{Port} (STN={Stn})…",
            _options.BotId,
            _options.Host,
            _options.Port,
            _options.StnBaseUrl);
        try
        {
            if (!await client.LoginAsync(stoppingToken).ConfigureAwait(false))
            {
                _logger.LogError("Login failed for bot {BotId}.", _options.BotId);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login/initialize failed for bot {BotId}.", _options.BotId);
            return;
        }

        _ = EnsureClansJoinedLoopAsync(client, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown requested.");
        }
    }

    private async Task TransportRateLimitedHandlerAsync(IRateLimitInfo info)
    {
        _logger.LogWarning(
            "Transport rate limit bucket={Bucket} limit={Limit} resetAfter={ResetAfter}ms",
            info.Bucket,
            info.Limit,
            info.ResetAfter.TotalMilliseconds);

        var target = RateLimitNotifyTarget.Value;
        if (target is null || info.SendBypassMessageAsync is null)
        {
            return;
        }

        // Avoid spamming while the transport waits out a burst.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = Interlocked.Read(ref _lastRateLimitNotifyMs);
        if (now - last < 30_000
            || Interlocked.CompareExchange(ref _lastRateLimitNotifyMs, now, last) != last)
        {
            return;
        }

        try
        {
            await info.SendBypassMessageAsync(
                    target.Value.ClanId,
                    target.Value.ChannelId,
                    PlayerMessageBuilder.RateLimited().ToJson())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send rate-limit bypass warning");
        }
    }

    private async Task EnsureClansJoinedLoopAsync(MezonClient client, CancellationToken cancellationToken)
    {
        // Login/Connected already seeds clans via JoinClanChat. Only retry when still empty.
        if (client.Clans.Count > 0)
        {
            _logger.LogInformation("Clan membership already ready: {Count} clan(s)", client.Clans.Count);
            return;
        }

        for (var attempt = 1; attempt <= 12 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await client.RefreshClanMembershipAsync(cancellationToken).ConfigureAwait(false);
                if (client.Clans.Count > 0)
                {
                    _logger.LogInformation("Clan membership ready: {Count} clan(s)", client.Clans.Count);
                    return;
                }

                _logger.LogWarning("Clan list empty (attempt {Attempt}/12). Invite bot to a clan if needed.", attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clan join retry {Attempt}/12 failed", attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 3)), cancellationToken).ConfigureAwait(false);
        }
    }

    private void WireClientLog(MezonClient client)
    {
        client.Log += message =>
        {
            var text = message.ToString(prependTimestamp: true, timestampKind: DateTimeKind.Utc);
            switch (message.Level)
            {
                case MezonLogLevel.Trace:
                    _logger.LogTrace("{MezonLog}", text);
                    break;
                case MezonLogLevel.Debug:
                    _logger.LogDebug("{MezonLog}", text);
                    break;
                case MezonLogLevel.Warning:
                    _logger.LogWarning("{MezonLog}", text);
                    break;
                case MezonLogLevel.Error:
                case MezonLogLevel.Critical:
                    _logger.LogError("{MezonLog}", text);
                    break;
                default:
                    _logger.LogInformation("{MezonLog}", text);
                    break;
            }

            return Task.CompletedTask;
        };
    }
}

/// <summary>Holds MezonClient for sinks registered before login.</summary>
public sealed class StreamingChannelSinkHolder
{
    private MezonClient? _client;
    public void SetClient(MezonClient client) => _client = client;
    public MezonClient GetClient() => _client ?? throw new InvalidOperationException("Client not ready.");
}

public sealed class VoiceChannelSinkHolder
{
    private MezonClient? _client;
    public void SetClient(MezonClient client) => _client = client;
    public MezonClient GetClient() => _client ?? throw new InvalidOperationException("Client not ready.");
}
