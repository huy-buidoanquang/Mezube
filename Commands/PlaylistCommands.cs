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

                    var hashtagChannelId = ChannelTargetParser.TryGetHashtagChannelId(ctx);
                    var name = ChannelTargetParser.BuildQuery(ctx.Args.Skip(1));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                                "Missing name",
                                $"Try {_options.CommandPrefix}playlist play [#channel] <name>"))
                            .ConfigureAwait(false);
                        return;
                    }

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

                    var (dest, destError) = await TryResolvePlayDestinationAsync(ctx, hashtagChannelId, cancellationToken)
                        .ConfigureAwait(false);
                    if (destError is not null)
                    {
                        await ctx.ReplyAsync(destError).ConfigureAwait(false);
                        return;
                    }

                    var resolved = dest!.Value;
                    var target = resolved.Target;
                    var mode = resolved.Mode;

                    var plays = new List<QueuedPlay>();
                    foreach (var item in items)
                    {
                        if (item.Track is null)
                        {
                            continue;
                        }

                        var info = item.Track.ToTrackInfo(ctx.Author.Username)
                            .WithRequester(ctx.Author.Id, ctx.Author.Username);
                        if (IsTooLarge(info))
                        {
                            continue;
                        }

                        plays.Add(new QueuedPlay(info, target));
                    }

                    if (plays.Count == 0)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.TrackNotFound(
                                "Every track in that playlist was too large or blocked."))
                            .ConfigureAwait(false);
                        return;
                    }

                    var kind = await EnqueueManyOrStartAsync(
                            GetState(clanId),
                            plays,
                            mode,
                            ctx.Client,
                            ctx.Channel.Id,
                            ctx.Author.Id,
                            attachPreparingAsControl: false,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (kind is PlayEnqueueKind.QueueFull)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.QueueFull()).ConfigureAwait(false);
                        return;
                    }

                    if (kind is PlayEnqueueKind.SlotsFull)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.PlaybackSlotsFull()).ConfigureAwait(false);
                        return;
                    }

                    if (kind is PlayEnqueueKind.ModeConflict)
                    {
                        await ctx.ReplyAsync(PlayerMessageBuilder.ModeConflict(wantVoice: mode == PlaybackMode.Voice))
                            .ConfigureAwait(false);
                        return;
                    }

                    await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                            kind == PlayEnqueueKind.CutIn ? "Playing next" : "Playlist queued",
                            [
                                new("Playlist", pl.Name),
                                new("Added", $"{plays.Count} track(s)"),
                                new(
                                    "Destination",
                                    PlayerMessageBuilder.FormatDestination(
                                        mode == PlaybackMode.Voice ? "voice" : "streaming",
                                        target.ChannelLabel)),
                                new(
                                    "Note",
                                    kind == PlayEnqueueKind.CutIn
                                        ? "Cut in ahead of the default playlist."
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
}
