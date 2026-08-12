using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    public async Task SetLoopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var arg = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "cycle";
        var current = await _playerStore.GetLoopModeAsync(clanId, cancellationToken).ConfigureAwait(false);
        LoopMode next = arg switch
        {
            "off" or "none" or "0" => LoopMode.Off,
            "track" or "song" or "one" or "1" => LoopMode.Track,
            "queue" or "all" or "2" => LoopMode.Queue,
            _ => current switch
            {
                LoopMode.Off => LoopMode.Track,
                LoopMode.Track => LoopMode.Queue,
                _ => LoopMode.Off,
            },
        };
        await _playerStore.SetLoopModeAsync(clanId, next, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Loop mode",
                next switch
                {
                    LoopMode.Track => "Looping the current track.",
                    LoopMode.Queue => "Looping the whole queue.",
                    _ => "Loop disabled.",
                }))
            .ConfigureAwait(false);
    }

    public async Task SeekAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        if (!state.IsPlaying || state.Queue.CurrentItem is null || state.Target is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status("Nothing playing", "Queue is empty."))
                .ConfigureAwait(false);
            return;
        }

        if (state.Mode != PlaybackMode.Voice || !_options.StnWhipEnabled)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Seek unavailable",
                    "Seek is supported for WHIP voice playback only."))
                .ConfigureAwait(false);
            return;
        }

        if (ctx.Args.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Missing position",
                    $"Example: `{_options.CommandPrefix}seek 1:30` \n `{_options.CommandPrefix}seek +15`"))
                .ConfigureAwait(false);
            return;
        }

        var durationMs = (long)(state.Queue.Current!.Duration?.TotalMilliseconds ?? 0);
        if (durationMs <= 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Seek failed", "Track duration is unknown."))
                .ConfigureAwait(false);
            return;
        }

        var currentPos = await _playerStore.EffectivePositionMsAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (!TryParseSeek(ctx.Args[0], currentPos, durationMs, out var targetMs))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Invalid seek",
                    "Use mm:ss | seconds | relative +N / -N."))
                .ConfigureAwait(false);
            return;
        }

        await _playerStore.SetPositionAsync(clanId, targetMs, durationMs, paused: false, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _voiceSink.PlayAsync(state.Target, state.Queue.Current!, cancellationToken, startOffsetMs: targetMs)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seek restart failed clan={ClanId}", clanId);
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Seek failed", "Could not restart playback from that position."))
                .ConfigureAwait(false);
            return;
        }

        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Seeked",
                $"Jumped to {TimeSpan.FromMilliseconds(targetMs):m\\:ss}."))
            .ConfigureAwait(false);
    }

    public async Task VoteSkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var state = GetState(clanId);
        var historyId = state.PlayHistoryId
            ?? await _playerStore.GetPlayHistoryIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (historyId is not long hid)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status("Nothing playing", "No active track to vote-skip."))
                .ConfigureAwait(false);
            return;
        }

        var (met, votes, needed, error) = await _access.TryVoteSkipAsync(
                clanId,
                hid,
                ctx.Author.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Vote-skip", error)).ConfigureAwait(false);
            return;
        }

        if (!met)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                    "Vote recorded",
                    $"Votes: {votes}/{needed}"))
                .ConfigureAwait(false);
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.Skip;
        await TryAdvancePersistedAsync(clanId, hid, skipLoop: true, PlayEndReason.VoteSkip, cancellationToken)
            .ConfigureAwait(false);
        await SkipStateAsync(state, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Vote-skip passed", $"Votes: {votes}/{needed} — skipping."))
            .ConfigureAwait(false);
    }

    public async Task MusicChannelAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanConfigureDjAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed("Only the clan owner can manage music channels."))
                .ConfigureAwait(false);
            return;
        }

        var sub = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                {
                    var channels = await _commandChannels.ListAsync(clanId, cancellationToken).ConfigureAwait(false);
                    var body = channels.Count == 0
                        ? "No allowlist — enqueue works in every channel."
                        : string.Join(", ", await FormatChannelMentionsAsync(ctx.Client, channels, cancellationToken)
                            .ConfigureAwait(false));
                    await ctx.ReplyAsync(PlayerMessageBuilder.Status("Music channels", body)).ConfigureAwait(false);
                    return;
                }
            case "clear":
                await _commandChannels.ClearAsync(clanId, cancellationToken).ConfigureAwait(false);
                await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Music channels", "Allowlist cleared (all channels allowed)."))
                    .ConfigureAwait(false);
                return;
            case "add":
            case "remove":
                {
                    var channelId = ChannelTargetParser.TryGetHashtagChannelId(ctx)
                        ?? (ctx.Args.Count > 1 && long.TryParse(ctx.Args[1], out var parsed) ? parsed : (long?)null);
                    if (channelId is not long id)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing channel",
                                $"Example: `{_options.CommandPrefix}musicchannel {sub} #channel`"))
                            .ConfigureAwait(false);
                        return;
                    }

                    if (sub == "add")
                    {
                        await _commandChannels.AddAsync(clanId, id, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Music channels", $"Added {mention} to allowlist."))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _commandChannels.RemoveAsync(clanId, id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Music channels", $"Removed {mention} from allowlist."))
                            .ConfigureAwait(false);
                    }

                    return;
                }
            default:
                await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                        "Unknown subcommand",
                        "Use add | remove | list | clear."))
                    .ConfigureAwait(false);
                return;
        }
    }

    public async Task PlaylistAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (ctx.Args.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Missing args",
                    $"Usage: {_options.CommandPrefix}playlist create | add | play | list | delete | default"))
                .ConfigureAwait(false);
            return;
        }

        var sub = ctx.Args[0].Trim().ToLowerInvariant();
        switch (sub)
        {
            case "list":
                {
                    var lists = await _playlists.ListAsync(clanId, cancellationToken).ConfigureAwait(false);
                    var body = lists.Count == 0
                        ? "No playlists yet."
                        : string.Join("\n", lists.Select(p =>
                            p.IsDefault ? $"• **{p.Name}** (default)" : $"• **{p.Name}**"));
                    await ctx.ReplyAsync(PlayerMessageBuilder.Status("Playlists", body)).ConfigureAwait(false);
                    return;
                }
            case "default":
                {
                    await PlaylistDefaultAsync(ctx, clanId, cancellationToken).ConfigureAwait(false);
                    return;
                }
            case "create":
                {
                    if (ctx.Args.Count < 2)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Missing name", "playlist create <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    try
                    {
                        await _playlists.CreateAsync(clanId, name, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Playlist created", name)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Create playlist failed");
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Create failed", "Name may already exist."))
                            .ConfigureAwait(false);
                    }

                    return;
                }
            case "delete":
                {
                    if (ctx.Args.Count < 2)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Missing name", "playlist delete <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    var ok = await _playlists.DeleteAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    await ctx.ReplyAsync(ok
                            ? PlayerMessageBuilder.Ok("Playlist deleted", name)
                            : PlayerMessageBuilder.Error("Not found", name))
                        .ConfigureAwait(false);
                    return;
                }
            case "add":
                {
                    if (ctx.Args.Count < 3)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing args",
                                "playlist add <name> <url | query>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = ctx.Args[1];
                    var query = string.Join(' ', ctx.Args.Skip(2));
                    var pl = await _playlists.TryGetByNameAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    if (pl is null)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Not found", name)).ConfigureAwait(false);
                        return;
                    }

                    var (track, err) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
                    if (track is null)
                    {
                        await ctx.ReplyAsync(err ?? PlayerMessageBuilder.Error("Not found", "No track matched."))
                            .ConfigureAwait(false);
                        return;
                    }

                    var trackId = await EnsureTrackIdAsync(track, cancellationToken).ConfigureAwait(false);
                    if (trackId == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Add failed", "Could not persist track."))
                            .ConfigureAwait(false);
                        return;
                    }

                    await _playlists.AddItemAsync(pl.Id, trackId, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                    await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Added to playlist", $"{track.Title} → {pl.Name}"))
                        .ConfigureAwait(false);
                    return;
                }
            case "play":
                {
                    if (!await EnsureCommandChannelAsync(ctx, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (ctx.Args.Count < 2)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Missing name", "playlist play <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    var pl = await _playlists.TryGetByNameAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    if (pl is null)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error("Not found", name)).ConfigureAwait(false);
                        return;
                    }

                    var items = await _playlists.ListItemsAsync(pl.Id, cancellationToken).ConfigureAwait(false);
                    if (items.Count == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Status("Empty playlist", name)).ConfigureAwait(false);
                        return;
                    }

                    var state = GetState(clanId);
                    var used = state.Queue.TotalCount;
                    var slots = Math.Max(0, _options.MaxQueuePerClan - used);
                    if (slots == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.QueueFull()).ConfigureAwait(false);
                        return;
                    }

                    var take = items.Take(slots).ToList();
                    // Require a voice/stream target via presence or default — enqueue as voice using presence.
                    if (!_binds.TryGetUserVoiceChannel(clanId, ctx.Author.Id, out var voiceId))
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "No destination",
                                "Join a voice channel (or use !play for single tracks)."))
                            .ConfigureAwait(false);
                        return;
                    }

                    var channel = await ctx.Client.GetChannelAsync(voiceId, cancellationToken).ConfigureAwait(false);
                    var target = new Playback.PlaybackTarget(
                        clanId,
                        voiceId,
                        RoomName: voiceId.ToString(),
                        ChannelLabel: channel.Name);

                    foreach (var item in take)
                    {
                        if (item.Track is null)
                        {
                            continue;
                        }

                        var info = item.Track.ToTrackInfo(ctx.Author.Username)
                            .WithRequester(ctx.Author.Id, ctx.Author.Username);
                        var play = new QueuedPlay(info, target);
                        state.Queue.Enqueue(play);
                        await PersistEnqueueAsync(clanId, play, "voice").ConfigureAwait(false);
                        StartBackgroundPrep(ctx.Client, state, play);
                    }

                    state.PlayingDefaultPlaylist = false;

                    if (!state.IsPlaying && state.Queue.Count > 0)
                    {
                        if (await TryAcquirePlaySlotAsync().ConfigureAwait(false))
                        {
                            state.CancelIdleDestroy();
                            state.PlayingDefaultPlaylist = false;
                            state.Mode = PlaybackMode.Voice;
                            state.Target = target;
                            state.NotifyClient = ctx.Client;
                            state.NotifyChannelId = ctx.Channel.Id;
                            state.ClanId = clanId;
                            state.HoldsPlaySlot = true;
                            ResetPrepToken(state);
                            StartPump(state, clanId);
                        }
                    }

                    var msg = take.Count < items.Count
                        ? $"Added {take.Count} tracks from playlist (queue reached limit {_options.MaxQueuePerClan})."
                        : $"Added {take.Count} tracks from playlist **{pl.Name}**.";
                    await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Playlist queued", msg)).ConfigureAwait(false);
                    return;
                }
            default:
                await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                        "Unknown subcommand",
                        "create, add, play, list, delete, default"))
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task PlaylistDefaultAsync(ICommandContext ctx, long clanId, CancellationToken cancellationToken)
    {
        if (ctx.Args.Count < 2)
        {
            var current = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Default playlist",
                    current is null
                        ? "none — set with !playlist default <name>"
                        : $"**{current.Name}**"))
                .ConfigureAwait(false);
            return;
        }

        var nameOrNone = string.Join(' ', ctx.Args.Skip(1)).Trim();
        if (nameOrNone.Equals("none", StringComparison.OrdinalIgnoreCase)
            || nameOrNone.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || nameOrNone.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (!await _access.CanStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                        "Only DJ role or clan owner can clear the default playlist."))
                    .ConfigureAwait(false);
                return;
            }

            await _playlists.SetDefaultAsync(clanId, null, cancellationToken).ConfigureAwait(false);
            var state = GetState(clanId);
            state.DefaultAutoplayArmed = false;
            state.PlayingDefaultPlaylist = false;
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok("Default playlist", "Cleared.")).ConfigureAwait(false);
            return;
        }

        if (!await _access.CanStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only DJ role or clan owner can set the default playlist."))
                .ConfigureAwait(false);
            return;
        }

        var pl = await _playlists.TryGetByNameAsync(clanId, nameOrNone, cancellationToken).ConfigureAwait(false);
        if (pl is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Not found", nameOrNone)).ConfigureAwait(false);
            return;
        }

        var items = await _playlists.ListItemsAsync(pl.Id, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Empty playlist",
                    "Add tracks before setting it as default."))
                .ConfigureAwait(false);
            return;
        }

        var streamId = await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken)
            .ConfigureAwait(false);
        if (streamId is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "No stream channel",
                    "Set a default stream channel before enabling default playlist autoplay."))
                .ConfigureAwait(false);
            return;
        }

        await _playlists.SetDefaultAsync(clanId, pl.Id, cancellationToken).ConfigureAwait(false);
        var playerState = GetState(clanId);
        playerState.DefaultPlaylistCursor = 0;
        playerState.DefaultAutoplayArmed = true;
        playerState.NotifyClient ??= ctx.Client;
        playerState.NotifyChannelId ??= ctx.Channel.Id;

        var started = false;
        if (!playerState.IsPlaying && playerState.Queue.TotalCount == 0)
        {
            started = await TryStartDefaultPlaylistAsync(playerState, clanId, cancellationToken)
                .ConfigureAwait(false);
            if (!started)
            {
                ScheduleIdleDestroy(clanId, playerState);
            }
        }

        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Default playlist set",
                started
                    ? $"**{pl.Name}** — playing on default stream."
                    : $"**{pl.Name}** — will autoplay after {IdleSessionTtl.TotalMinutes:0} minutes idle."))
            .ConfigureAwait(false);
    }

    private static bool TryParseSeek(string raw, long currentMs, long durationMs, out long targetMs)
    {
        targetMs = 0;
        var text = raw.Trim();
        if (text.StartsWith('+') || text.StartsWith('-'))
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var deltaSec))
            {
                return false;
            }

            targetMs = (long)Math.Clamp(currentMs + deltaSec * 1000, 0, durationMs);
            return true;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }

            if (!int.TryParse(parts[^1], out var seconds)
                || !int.TryParse(parts[^2], out var minutes))
            {
                return false;
            }

            var hours = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], out hours))
            {
                return false;
            }

            targetMs = (long)Math.Clamp(((hours * 3600) + (minutes * 60) + seconds) * 1000L, 0, durationMs);
            return true;
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
        {
            return false;
        }

        targetMs = (long)Math.Clamp(sec * 1000, 0, durationMs);
        return true;
    }
}
