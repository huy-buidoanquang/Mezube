using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Mezube.Application;
using Mezube.Helpers;
using Mezube.Infrastructure.Caching;
using Mezube.Infrastructure.Caching.Snapshots;
using Mezube.Music;
using Mezube.Ui;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
    private readonly IClanSettingsService _clanSettings;
    private readonly MezonEntityCacheBridge _entityCache;
    private readonly IEntitySnapshotStore _snapshots;
    private readonly MezonSnapshotKeyFactory _snapshotKeys;
    private readonly PlaybackAccess _access;
    private readonly ILogger<MezubeBot> _logger;
    private readonly ILogger _mezonLogger;
    private readonly ConcurrentDictionary<(long ClanId, long MessageId), byte> _controlOneShot = new();
    private readonly ConcurrentDictionary<long, byte> _ownerHydrateInFlight = new();
    private MezonClient? _client;
    private long _lastRateLimitNotifyMs;

    public MezubeBot(
        BotOptions options,
        MusicPlayer player,
        BindStore binds,
        StreamingChannelSinkHolder streamingHolder,
        VoiceChannelSinkHolder voiceHolder,
        MusicVizAssets viz,
        IClanSettingsService clanSettings,
        MezonEntityCacheBridge entityCache,
        IEntitySnapshotStore snapshots,
        MezonSnapshotKeyFactory snapshotKeys,
        PlaybackAccess access,
        ILogger<MezubeBot> logger,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _player = player;
        _binds = binds;
        _streamingHolder = streamingHolder;
        _voiceHolder = voiceHolder;
        _viz = viz;
        _clanSettings = clanSettings;
        _entityCache = entityCache;
        _snapshots = snapshots;
        _snapshotKeys = snapshotKeys;
        _access = access;
        _logger = logger;
        _mezonLogger = loggerFactory.CreateLogger("Mezon");
    }

    public MezonClient Client => _client ?? throw new InvalidOperationException("Bot not started.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        var clientOptions = new MezonClientOptions(_options.BotId, _options.Token, _options.Host, _options.Port, _options.UseSsl)
        {
            ServerKey = _options.ServerKey,
            TransportType = Mezon.Net.Core.TransportType.Tcp,
            LogLevel = _options.MezonNetLogLevel,
            MaxTransportRequestsPerSecond = 60,
            MaxTransportRequestsPerMinute = 500,
            DefaultRatelimitCallback = TransportRateLimitedHandlerAsync,
        };

        await using var client = new MezonClient(clientOptions);
        _client = client;
        _streamingHolder.SetClient(client);
        _voiceHolder.SetClient(client);
        WireClientLog(client);
        await _entityCache.AttachAsync(client, stoppingToken).ConfigureAwait(false);

        var commands = new CommandService(_options.CommandPrefix);
        commands.Use(async (ctx, next) =>
        {
            RateLimitNotifyTarget.Value = (ctx.Channel.ClanId, ctx.Channel.Id);
            CommandReplyTracker.Clear();
            try
            {
                await next(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Command {Command} failed", ctx.Name);
                try
                {
                    var awkward = PlayerMessageBuilder.Awkward();
                    var prior = CommandReplyTracker.Peek();
                    if (prior is not null)
                    {
                        await prior.Channel.UpdateMessageAsync(
                                prior.MessageId,
                                awkward,
                                hideEdited: true,
                                createTimeSeconds: prior.CreateTimeSeconds)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await ctx.ReplyAsync(awkward).ConfigureAwait(false);
                    }
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, "Failed to send awkward error reply");
                }
            }
            finally
            {
                CommandReplyTracker.Clear();
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
                var messageId = ctx.Message?.Id ?? parts.MessageId;
                var lockKey = (clanId, messageId);
                if (!_controlOneShot.TryAdd(lockKey, 0))
                {
                    return;
                }

                switch (parts.Action)
                {
                    case MezubeButtonId.ActionSkip:
                        {
                            var outcome = await _player
                                .TrySkipAsync(ctx.Client, clanId, ctx.User.Id, ctx.CancellationToken)
                                .ConfigureAwait(false);
                            if (!outcome.Allowed)
                            {
                                _controlOneShot.TryRemove(lockKey, out _);
                                await ctx.RespondAsync(outcome.Content).ConfigureAwait(false);
                                break;
                            }

                            await UpdateControlMessageAsync(ctx, outcome.Content).ConfigureAwait(false);
                            break;
                        }
                    case MezubeButtonId.ActionStop:
                        {
                            var outcome = await _player
                                .TryStopAsync(ctx.Client, clanId, ctx.User.Id, ctx.CancellationToken)
                                .ConfigureAwait(false);
                            if (!outcome.Allowed)
                            {
                                _controlOneShot.TryRemove(lockKey, out _);
                                await ctx.RespondAsync(outcome.Content).ConfigureAwait(false);
                                break;
                            }

                            await UpdateControlMessageAsync(ctx, outcome.Content).ConfigureAwait(false);
                            break;
                        }
                    default:
                        _controlOneShot.TryRemove(lockKey, out _);
                        break;
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
            // Owner sync is post-login connect-init only (no REST from Ready — Sdk hard rule).
        };

        client.ClanJoined += data =>
        {
            ClanJoinResponse joined = data;
            // Event path: L1 only. Stub installs have CreatorId=0 — hydrate in background.
            TrySyncOwnerFromL1(client, joined.ClanId, stoppingToken);
            ScheduleOwnerHydrateIfNeeded(client, joined.ClanId, stoppingToken);
            return Task.CompletedTask;
        };

        client.ClanUserAdded += data =>
        {
            AddClanUserEventResponse added = data;
            if (added.User.UserId == client.BotId && added.ClanId != 0)
            {
                _logger.LogInformation(
                    "Bot installed into clan={ClanId}; scheduling owner hydrate",
                    added.ClanId);
                TrySyncOwnerFromL1(client, added.ClanId, stoppingToken);
                ScheduleOwnerHydrateIfNeeded(client, added.ClanId, stoppingToken);
            }

            return Task.CompletedTask;
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
            _binds.SetUserVoiceChannel(joined.ClanId, joined.UserId, joined.VoiceChannelId);
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
            _binds.ClearUserVoiceChannel(left.ClanId, left.VoiceUserId);
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

        _ = EnsureClansJoinedAndSyncOwnersAsync(client, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown requested.");
        }
        finally
        {
            await _entityCache.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Task UpdateControlMessageAsync(IInteractionContext ctx, Mezon.Net.Client.MessageContent content)
    {
        if (ctx.Message is not null)
        {
            return ctx.UpdateMessageAsync(content);
        }

        return ctx.RespondAsync(content);
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

    /// <summary>Event-safe: only reads L1 cache; never calls REST.</summary>
    private void TrySyncOwnerFromL1(MezonClient client, long clanId, CancellationToken cancellationToken)
    {
        if (!client.Clans.TryGet(clanId, out var clan) || clan.CreatorId == 0)
        {
            return;
        }

        _ = PersistOwnerAsync(clanId, clan.CreatorId, cancellationToken);
    }

    /// <summary>
    /// Fire-and-forget app-owned hydrate when L1 is a stub (CreatorId=0).
    /// Does not block the event dispatch path (Sdk hard rule).
    /// </summary>
    private void ScheduleOwnerHydrateIfNeeded(MezonClient client, long clanId, CancellationToken cancellationToken)
    {
        if (clanId == 0)
        {
            return;
        }

        if (client.Clans.TryGet(clanId, out var clan) && clan.CreatorId != 0)
        {
            return;
        }

        if (!_ownerHydrateInFlight.TryAdd(clanId, 0))
        {
            return;
        }

        _ = HydrateClanOwnerAsync(client, clanId, cancellationToken);
    }

    private async Task HydrateClanOwnerAsync(MezonClient client, long clanId, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _clanSettings.GetOwnerIdAsync(clanId, cancellationToken).ConfigureAwait(false);
            if (existing is long ownerId && ownerId != 0)
            {
                return;
            }

            // Prefer L2 before REST.
            try
            {
                var snapshot = await _snapshots.GetAsync<ClanSnapshotDto>(_snapshotKeys.Clan(clanId), cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot is { CreatorId: not 0 })
                {
                    await PersistOwnerAsync(clanId, snapshot.CreatorId, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Owner hydrated from L2 clan={ClanId} owner={OwnerId}",
                        clanId,
                        snapshot.CreatorId);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "L2 owner hydrate miss clan={ClanId}", clanId);
            }

            // App-owned background REST. Do NOT use GetClanAsync here: L1 already holds a stub
            // (CreatorId=0) from ClanJoined, so GetOrFetch would never hit the API.
            var list = await client.ListClanDescsAsync(new ListClanDescParams()).ConfigureAwait(false);
            for (var i = 0; i < list.Clandesc.Count; i++)
            {
                var desc = list.Clandesc[i];
                if (desc.ClanId != clanId || desc.CreatorId == 0)
                {
                    continue;
                }

                await PersistOwnerAndSnapshotAsync(desc.ClanId, desc.CreatorId, desc.ClanName, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Owner hydrated after mid-session install clan={ClanId} owner={OwnerId}",
                    clanId,
                    desc.CreatorId);
                return;
            }

            _logger.LogWarning("Owner hydrate found no CreatorId for clan={ClanId}", clanId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Owner hydrate failed clan={ClanId}", clanId);
        }
        finally
        {
            _ownerHydrateInFlight.TryRemove(clanId, out _);
        }
    }

    private async Task PersistOwnerAndSnapshotAsync(
        long clanId,
        long ownerId,
        string? name,
        CancellationToken cancellationToken)
    {
        await PersistOwnerAsync(clanId, ownerId, cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _snapshots.SetAsync(
                    _snapshotKeys.Clan(clanId),
                    new ClanSnapshotDto
                    {
                        ClanId = clanId,
                        CreatorId = ownerId,
                        Name = string.IsNullOrWhiteSpace(name) ? null : name,
                        Revision = revision,
                    },
                    new CacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                        Revision = revision,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "L2 clan snapshot after owner hydrate failed clan={ClanId}", clanId);
        }
    }

    private async Task PersistOwnerAsync(long clanId, long ownerId, CancellationToken cancellationToken)
    {
        try
        {
            await _clanSettings.EnsureOwnerAsync(clanId, ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Owner persist failed clan={ClanId}", clanId);
        }
    }

    /// <summary>
    /// Connect-init path (allowed to call ListClanDescs / Refresh). Seeds Postgres owner_id.
    /// </summary>
    private async Task EnsureClansJoinedAndSyncOwnersAsync(MezonClient client, CancellationToken cancellationToken)
    {
        if (client.Clans.Count == 0)
        {
            for (var attempt = 1; attempt <= 12 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    await client.RefreshClanMembershipAsync(cancellationToken).ConfigureAwait(false);
                    if (client.Clans.Count > 0)
                    {
                        _logger.LogInformation("Clan membership ready: {Count} clan(s)", client.Clans.Count);
                        break;
                    }

                    _logger.LogWarning(
                        "Clan list empty (attempt {Attempt}/12). Invite bot to a clan if needed.",
                        attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Clan join retry {Attempt}/12 failed", attempt);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 3)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            _logger.LogInformation("Clan membership already ready: {Count} clan(s)", client.Clans.Count);
        }

        await SyncOwnersAndWarmDjAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connect-init only: one ListClanDescs for owner_id + L2 clan DTOs.
    /// DJ roles: warm only clans that already have dj_role_id (no blanket ListRoles / LoadChannels).
    /// </summary>
    private async Task SyncOwnersAndWarmDjAsync(MezonClient client, CancellationToken cancellationToken)
    {
        try
        {
            var clans = await client.ListClanDescsAsync(new ListClanDescParams()).ConfigureAwait(false);
            var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = 0; i < clans.Clandesc.Count; i++)
            {
                var desc = clans.Clandesc[i];
                if (desc.ClanId == 0)
                {
                    continue;
                }

                if (desc.CreatorId != 0)
                {
                    await _clanSettings.EnsureOwnerAsync(desc.ClanId, desc.CreatorId, cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    await _snapshots.SetAsync(
                            _snapshotKeys.Clan(desc.ClanId),
                            new ClanSnapshotDto
                            {
                                ClanId = desc.ClanId,
                                CreatorId = desc.CreatorId,
                                Name = string.IsNullOrWhiteSpace(desc.ClanName) ? null : desc.ClanName,
                                Revision = revision + i,
                            },
                            new CacheEntryOptions
                            {
                                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                                Revision = revision + i,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "L2 clan seed failed clan={ClanId}", desc.ClanId);
                }

                var djRoleId = await _clanSettings.GetDjRoleIdAsync(desc.ClanId, cancellationToken)
                    .ConfigureAwait(false);
                if (djRoleId is long roleId)
                {
                    await _access.WarmDjRoleMembershipAsync(client, desc.ClanId, roleId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-login owner/DJ warm failed");
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
                    _mezonLogger.LogTrace("{MezonLog}", text);
                    break;
                case MezonLogLevel.Debug:
                    _mezonLogger.LogDebug("{MezonLog}", text);
                    break;
                case MezonLogLevel.Warning:
                    _mezonLogger.LogWarning("{MezonLog}", text);
                    break;
                case MezonLogLevel.Error:
                case MezonLogLevel.Critical:
                    _mezonLogger.LogError("{MezonLog}", text);
                    break;
                default:
                    _mezonLogger.LogInformation("{MezonLog}", text);
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
