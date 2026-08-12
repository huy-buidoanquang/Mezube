using Mezon.Net.Client;
using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music.Interactive;
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
            await ctx.ReplyAsync(PlayerMessageBuilder.NothingPlaying())
                .ConfigureAwait(false);
            return;
        }

        if (state.Mode != PlaybackMode.Voice || !_options.StnWhipEnabled)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Seek unavailable",
                    "Jumping in a track only works for voice (WHIP) playback right now."))
                .ConfigureAwait(false);
            return;
        }

        if (ctx.Args.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Missing position",
                    [
                        new("Examples", $"{_options.CommandPrefix}seek 1:30"),
                        new("Relative", $"{_options.CommandPrefix}seek +15  or  {_options.CommandPrefix}seek -10"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        var durationMs = (long)(state.Queue.Current!.Duration?.TotalMilliseconds ?? 0);
        if (durationMs <= 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Couldn’t seek",
                    "I don’t know how long this track is."))
                .ConfigureAwait(false);
            return;
        }

        var currentPos = await _playerStore.EffectivePositionMsAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (!TryParseSeek(ctx.Args[0], currentPos, durationMs, out var targetMs))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Hmm, that time doesn’t look right",
                    "Try mm:ss, plain seconds, or relative +N / -N."))
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
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Couldn’t seek",
                    "Playback didn’t restart from that spot — try again."))
                .ConfigureAwait(false);
            return;
        }

        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Jumped ahead",
                $"Now at {TimeSpan.FromMilliseconds(targetMs):m\\:ss}."))
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
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Nothing playing",
                    "There’s no track to vote on right now."))
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
                    [
                        new("Progress", $"{votes}/{needed}"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.Skip;
        await TryAdvancePersistedAsync(clanId, hid, skipLoop: true, PlayEndReason.VoteSkip, cancellationToken)
            .ConfigureAwait(false);
        await SkipStateAsync(state, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Skipping!",
                [
                    new("Votes", $"{votes}/{needed}"),
                    new("Next", "Moving on to the next track."),
                ]))
            .ConfigureAwait(false);
    }

    public async Task MusicChannelAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanConfigureDjAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only the clan owner can manage which channels accept music commands."))
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
                        ? "Every channel can queue music."
                        : string.Join(", ", await FormatChannelMentionsAsync(ctx.Client, channels, cancellationToken)
                            .ConfigureAwait(false));
                    await ctx.ReplyAsync(PlayerMessageBuilder.MusicChannelsListed(body)).ConfigureAwait(false);
                    return;
                }
            case "clear":
                await _commandChannels.ClearAsync(clanId, cancellationToken).ConfigureAwait(false);
                await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                        "Music channels",
                        "Allowlist cleared — music commands work everywhere again."))
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
                                $"Tag a channel, e.g. {_options.CommandPrefix}musicchannel {sub} #channel"))
                            .ConfigureAwait(false);
                        return;
                    }

                    if (sub == "add")
                    {
                        await _commandChannels.AddAsync(clanId, id, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                                "Channel allowed",
                                $"{mention} can now accept music commands."))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _commandChannels.RemoveAsync(clanId, id, cancellationToken).ConfigureAwait(false);
                        var mention = await FormatChannelMentionAsync(ctx.Client, id, cancellationToken)
                            .ConfigureAwait(false);
                        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                                "Channel removed",
                                $"{mention} is off the music allowlist."))
                            .ConfigureAwait(false);
                    }

                    return;
                }
            default:
                await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                        "Unknown option",
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
                    "Need a bit more",
                    [
                        new(
                            "Try",
                            $"{_options.CommandPrefix}playlist create | add | play | list [name] | delete | default"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        var sub = ctx.Args[0].Trim().ToLowerInvariant();
        switch (sub)
        {
            case "list":
                {
                    if (ctx.Args.Count >= 2)
                    {
                        var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                        var pl = await _playlists.TryGetByNameAsync(clanId, name, cancellationToken)
                            .ConfigureAwait(false);
                        if (pl is null)
                        {
                            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                    "Playlist not found",
                                    $"I couldn’t find “{name}” in this clan."))
                                .ConfigureAwait(false);
                            return;
                        }

                        var items = await _playlists.ListItemsAsync(pl.Id, cancellationToken)
                            .ConfigureAwait(false);
                        const int previewCap = 25;
                        var preview = items
                            .Take(previewCap)
                            .Select(item =>
                            {
                                var title = item.Track?.Title ?? $"track#{item.TrackId}";
                                var dur = item.Track?.Duration is TimeSpan d
                                    ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
                                    : "?:??";
                                return (title, dur, item.Track?.IsTooLarge == true);
                            })
                            .ToList();
                        await ctx.ReplyAsync(PlayerMessageBuilder.PlaylistTracks(
                                pl.Name,
                                pl.IsDefault,
                                preview,
                                items.Count,
                                previewCap))
                            .ConfigureAwait(false);
                        return;
                    }

                    var lists = await _playlists.ListAsync(clanId, cancellationToken).ConfigureAwait(false);
                    await ctx.ReplyAsync(PlayerMessageBuilder.PlaylistCatalog(
                            lists.Select(p => (p.Name, p.IsDefault)).ToList(),
                            _options.CommandPrefix))
                        .ConfigureAwait(false);
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
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing name",
                                $"Try {_options.CommandPrefix}playlist create <name> — optional YouTube/SoundCloud playlist URL at the end."))
                            .ConfigureAwait(false);
                        return;
                    }

                    // create <name> OR create <name> <url> (url is last absolute http token).
                    string name;
                    string? importUrl = null;
                    if (ctx.Args.Count >= 3
                        && IsAbsoluteHttpUrl(ctx.Args[^1])
                        && _externalPlaylists.CanImport(ctx.Args[^1]))
                    {
                        importUrl = ctx.Args[^1].Trim();
                        name = string.Join(' ', ctx.Args.Skip(1).Take(ctx.Args.Count - 2)).Trim();
                    }
                    else if (ctx.Args.Count >= 3 && IsAbsoluteHttpUrl(ctx.Args[^1]))
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Link not supported",
                                "Use a YouTube playlist or SoundCloud set / short link."))
                            .ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing name",
                                $"Try {_options.CommandPrefix}playlist create <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    try
                    {
                        var created = await _playlists.CreateAsync(clanId, name, ctx.Author.Id, cancellationToken)
                            .ConfigureAwait(false);
                        if (importUrl is null)
                        {
                            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                                    "Playlist ready",
                                    [
                                        new("Name", name),
                                        new("Next", $"Add songs with {_options.CommandPrefix}playlist add {name} <query>"),
                                    ]))
                                .ConfigureAwait(false);
                            return;
                        }

                        await BeginPlaylistImportAsync(ctx, created.Id, created.Name, importUrl, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Create playlist failed");
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Couldn’t create playlist",
                                "That name might already be taken — try another."))
                            .ConfigureAwait(false);
                    }

                    return;
                }
            case "delete":
                {
                    if (ctx.Args.Count < 2)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing name",
                                $"Try {_options.CommandPrefix}playlist delete <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    var ok = await _playlists.DeleteAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    await ctx.ReplyAsync(ok
                            ? PlayerMessageBuilder.Ok("Playlist deleted", [new("Name", name)])
                            : PlayerMessageBuilder.Error("Playlist not found", $"I couldn’t find “{name}”."))
                        .ConfigureAwait(false);
                    return;
                }
            case "add":
                {
                    if (ctx.Args.Count < 3)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Need playlist + track",
                                $"Try {_options.CommandPrefix}playlist add <name> <url or search>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = ctx.Args[1];
                    var query = string.Join(' ', ctx.Args.Skip(2));
                    var pl = await _playlists.TryGetByNameAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    if (pl is null)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Playlist not found",
                                $"I couldn’t find “{name}”."))
                            .ConfigureAwait(false);
                        return;
                    }

                    var (track, err) = await TryResolveAsync(ctx, query, cancellationToken).ConfigureAwait(false);
                    if (track is null)
                    {
                        await ctx.ReplyAsync(err ?? PlayerMessageBuilder.TrackNotFound())
                            .ConfigureAwait(false);
                        return;
                    }

                    var trackId = await EnsureTrackIdAsync(track, cancellationToken).ConfigureAwait(false);
                    if (trackId == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Couldn’t save track",
                                "Something went wrong storing that song — try again."))
                            .ConfigureAwait(false);
                        return;
                    }

                    await _playlists.AddItemAsync(pl.Id, trackId, ctx.Author.Id, cancellationToken).ConfigureAwait(false);
                    await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                            "Added to playlist",
                            [
                                new("Track", track.Title),
                                new("Playlist", pl.Name),
                            ]))
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
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing name",
                                $"Try {_options.CommandPrefix}playlist play <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

                    var name = string.Join(' ', ctx.Args.Skip(1)).Trim();
                    var pl = await _playlists.TryGetByNameAsync(clanId, name, cancellationToken).ConfigureAwait(false);
                    if (pl is null)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Playlist not found",
                                $"I couldn’t find “{name}”."))
                            .ConfigureAwait(false);
                        return;
                    }

                    var items = await _playlists.ListItemsAsync(pl.Id, cancellationToken).ConfigureAwait(false);
                    if (items.Count == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                                "Empty playlist",
                                [
                                    new("Playlist", name),
                                    new("Tip", "Add a few tracks first, then play it."),
                                ]))
                            .ConfigureAwait(false);
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
                                "Join a voice channel first",
                                "Hop into voice (or use !play for a single track), then try again."))
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

                    await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                            "Playlist queued",
                            [
                                new("Playlist", pl.Name),
                                new("Added", $"{take.Count} track(s)"),
                                new(
                                    "Note",
                                    take.Count < items.Count
                                        ? $"Queue hit the {_options.MaxQueuePerClan}-track cap — the rest weren’t added."
                                        : "You’re all set."),
                            ]))
                        .ConfigureAwait(false);
                    return;
                }
            default:
                await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                        "Unknown playlist command",
                        "create, add, play, list, delete, or default."))
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
                    [
                        new(
                            "Current",
                            current is null ? "none" : current.Name),
                        new(
                            "Tip",
                            current is null
                                ? $"Set one with {_options.CommandPrefix}playlist default <name>"
                                : "Plays when the queue goes idle (needs a default stream channel)."),
                    ]))
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
                        "Only a DJ or the clan owner can clear the default playlist."))
                    .ConfigureAwait(false);
                return;
            }

            await _playlists.SetDefaultAsync(clanId, null, cancellationToken).ConfigureAwait(false);
            var state = GetState(clanId);
            state.DefaultAutoplayArmed = false;
            state.PlayingDefaultPlaylist = false;
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                    "Default playlist cleared",
                    "Idle autoplay won’t kick in anymore."))
                .ConfigureAwait(false);
            return;
        }

        if (!await _access.CanStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only a DJ or the clan owner can set the default playlist."))
                .ConfigureAwait(false);
            return;
        }

        var pl = await _playlists.TryGetByNameAsync(clanId, nameOrNone, cancellationToken).ConfigureAwait(false);
        if (pl is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Playlist not found",
                    $"I couldn’t find “{nameOrNone}”."))
                .ConfigureAwait(false);
            return;
        }

        var items = await _playlists.ListItemsAsync(pl.Id, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Empty playlist",
                    "Add a few tracks before making it the default."))
                .ConfigureAwait(false);
            return;
        }

        var streamId = await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken)
            .ConfigureAwait(false);
        if (streamId is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Need a stream channel",
                    "Pick a default stream channel first, then enable default playlist autoplay."))
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
                [
                    new("Playlist", pl.Name),
                    new(
                        "When",
                        started
                            ? "Playing now on the default stream channel."
                            : $"Autoplays after about {IdleSessionTtl.TotalMinutes:0} minutes idle."),
                ]))
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
