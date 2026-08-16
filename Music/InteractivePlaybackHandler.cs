using Mezon.Net.Client;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Music.Interactive;
using Mezube.Playback;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    private static bool IsAbsoluteHttpUrl(string query)
        => Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task HandleFreeTextPlayAsync(
        ICommandContext ctx,
        string query,
        PlaybackTarget target,
        PlaybackMode mode,
        long preparingMessageId,
        uint? preparingCreateTime,
        bool wantVideo,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrackInfoEntity> hits;
        try
        {
            hits = await _youtube.SearchAsync(
                    query,
                    ctx.Author.Username ?? ctx.Author.Id.ToString(),
                    SearchPickSession.MaxResults,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Free-text search failed");
            await UpdateOrReplyAsync(ctx, preparingMessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (hits.Count == 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("Try another search — nothing playable came back from YouTube."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (hits.Count == 1)
        {
            await EnqueueResolvedTrackAsync(
                    ctx,
                    hits[0].WithRequester(ctx.Author.Id, ctx.Author.Username ?? ctx.Author.Id.ToString()),
                    target,
                    mode,
                    preparingMessageId,
                    preparingCreateTime,
                    wantVideo,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var candidates = hits
            .Select(TrackCandidate.FromTrack)
            .Where(c => c is not null)
            .Cast<TrackCandidate>()
            .Take(SearchPickSession.MaxResults)
            .ToList();
        if (candidates.Count == 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.TrackNotFound("Try another search — nothing playable came back from YouTube."),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (candidates.Count == 1)
        {
            await EnqueueResolvedTrackAsync(
                    ctx,
                    candidates[0].ToTrackInfo(ctx.Author.Id, ctx.Author.Username ?? ctx.Author.Id.ToString()),
                    target,
                    mode,
                    preparingMessageId,
                    preparingCreateTime,
                    wantVideo,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var session = new SearchPickSession
        {
            ClanId = target.ClanId,
            ChannelId = ctx.Channel.Id,
            UserId = ctx.Author.Id,
            Mode = mode == PlaybackMode.Voice ? "voice" : "streaming",
            TargetChannelId = target.ChannelId,
            TargetChannelLabel = target.ChannelLabel,
            TargetRoomName = target.RoomName,
            Query = query,
            Candidates = candidates,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            WantVideo = wantVideo,
        };

        try
        {
            await _sessions.SaveSearchPickAsync(preparingMessageId, session, cancellationToken)
                .ConfigureAwait(false);
            var content = PlayerMessageBuilder.SearchPick(query, candidates, preparingMessageId, ctx.Author.Id);
            await UpdateOrReplyAsync(ctx, preparingMessageId, content, preparingCreateTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search pick UI failed");
            await UpdateOrReplyAsync(ctx, preparingMessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
        }
    }

    private async Task EnqueueResolvedTrackAsync(
        ICommandContext ctx,
        TrackInfoEntity track,
        PlaybackTarget target,
        PlaybackMode mode,
        long preparingMessageId,
        uint? preparingCreateTime,
        bool wantVideo,
        CancellationToken cancellationToken)
    {
        if (IsTooLarge(track, wantVideo) || track.IsTooLarge)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparingMessageId,
                    PlayerMessageBuilder.CopyrightBlocked(),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var play = new QueuedPlay(track, target, preparingMessageId, preparingCreateTime, WantVideo: wantVideo);
        var kind = await EnqueueOrStartAsync(
                GetState(target.ClanId),
                play,
                mode,
                ctx.Client,
                ctx.Channel.Id,
                ctx.Author.Id,
                attachPreparingAsControl: true,
                cancellationToken)
            .ConfigureAwait(false);
        await ReplyEnqueueAsync(ctx, kind, play, target.ChannelLabel, preparingMessageId, preparingCreateTime)
            .ConfigureAwait(false);
    }

    public async Task HandleSearchPickButtonAsync(IInteractionContext ctx, MezubeButtonId.Parsed parts)
    {
        try
        {
            if (parts.Action != MezubeButtonId.ActionSubmit)
            {
                return;
            }

            if (ctx.User.Id != parts.UserId)
            {
                await ctx.SendEphemeralAsync(PlayerMessageBuilder.PickerNotYours(
                        "Only the person who ran !play can pick a track."))
                    .ConfigureAwait(false);
                return;
            }

            var messageId = ctx.Message?.Id ?? parts.MessageId;
            // Claim immediately so a second click can't re-enqueue while CDN prep runs.
            var session = await _sessions.TakeSearchPickAsync(messageId, ctx.CancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                await ctx.RespondAsync(PlayerMessageBuilder.Error(
                        "Picker expired",
                        "That search timed out — run !play again."))
                    .ConfigureAwait(false);
                return;
            }

            if (session.UserId != ctx.User.Id)
            {
                // Restore so the original requester can still finish.
                await _sessions.SaveSearchPickAsync(messageId, session, ctx.CancellationToken).ConfigureAwait(false);
                await ctx.SendEphemeralAsync(PlayerMessageBuilder.PickerNotYours(
                        "Only the person who ran !play can pick a track."))
                    .ConfigureAwait(false);
                return;
            }

            var selected = InteractionExtraData.ParseSelectedValues(
                (ctx.Interaction as ButtonInteraction)?.ExtraData);
            if (selected.Count > 1)
            {
                await _sessions.SaveSearchPickAsync(messageId, session, ctx.CancellationToken).ConfigureAwait(false);
                await ctx.RespondAsync(PlayerMessageBuilder.Error(
                        "Pick only one",
                        "Choose a single track, then tap Play selected."))
                    .ConfigureAwait(false);
                return;
            }

            var token = selected.FirstOrDefault();
            var candidate = session.Candidates.FirstOrDefault(c => c.Token == token);
            if (candidate is null)
            {
                await _sessions.SaveSearchPickAsync(messageId, session, ctx.CancellationToken).ConfigureAwait(false);
                await ctx.RespondAsync(PlayerMessageBuilder.Error(
                        "Nothing picked yet",
                        "Choose one track, then tap Play selected."))
                    .ConfigureAwait(false);
                return;
            }

            var mode = session.Mode == "voice" ? PlaybackMode.Voice : PlaybackMode.Streaming;
            if (mode == PlaybackMode.Voice)
            {
                await ctx.RespondAsync(PlayerMessageBuilder.Error(
                        "Streaming only",
                        "STN no longer publishes into voice channels. Run !play with a #stream channel."))
                    .ConfigureAwait(false);
                return;
            }
            var target = new PlaybackTarget(
                session.ClanId,
                session.TargetChannelId,
                RoomName: session.TargetRoomName,
                ChannelLabel: session.TargetChannelLabel);
            var track = candidate.ToTrackInfo(ctx.User.Id, ctx.User.Username ?? ctx.User.Id.ToString());

            // Strip radio + button before any prep/CDN work so the user can't spam.
            await ctx.UpdateMessageAsync(PlayerMessageBuilder.Preparing(
                    PlayerMessageBuilder.FormatDestination(
                        mode == PlaybackMode.Voice ? "voice" : "streaming",
                        target.ChannelLabel)))
                .ConfigureAwait(false);

            await EnqueueFromInteractionAsync(ctx, track, target, mode, messageId, session.WantVideo).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search pick button failed");
            try
            {
                await ctx.RespondAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
            }
            catch (Exception notifyEx)
            {
                _logger.LogDebug(notifyEx, "Search pick awkward reply failed");
            }
        }
    }

    private async Task EnqueueFromInteractionAsync(
        IInteractionContext ctx,
        TrackInfoEntity track,
        PlaybackTarget target,
        PlaybackMode mode,
        long messageId,
        bool wantVideo)
    {
        if (IsTooLarge(track, wantVideo) || track.IsTooLarge)
        {
            await ctx.UpdateMessageAsync(PlayerMessageBuilder.CopyrightBlocked()).ConfigureAwait(false);
            return;
        }

        var play = new QueuedPlay(track, target, messageId, null, WantVideo: wantVideo);
        var kind = await EnqueueOrStartAsync(
                GetState(target.ClanId),
                play,
                mode,
                ctx.Client,
                ctx.Channel.Id,
                ctx.User.Id,
                attachPreparingAsControl: true,
                ctx.CancellationToken)
            .ConfigureAwait(false);

        var content = kind switch
        {
            PlayEnqueueKind.QueueFull => PlayerMessageBuilder.QueueFull(),
            PlayEnqueueKind.SlotsFull => PlayerMessageBuilder.PlaybackSlotsFull(),
            PlayEnqueueKind.ModeConflict => PlayerMessageBuilder.Error(
                "Mode changed",
                "Playback switched while you were picking. Run !play again."),
            PlayEnqueueKind.CutIn => PlayerMessageBuilder.PlayingNext(
                track.Title,
                "Cut in ahead of the default playlist."),
            PlayEnqueueKind.Queued => PlayerMessageBuilder.Queued(track, 1, target.ChannelLabel),
            _ => null,
        };
        if (content is not null)
        {
            await ctx.UpdateMessageAsync(content).ConfigureAwait(false);
        }
    }

    public async Task HandlePlaylistImportButtonAsync(IInteractionContext ctx, MezubeButtonId.Parsed parts)
    {
        try
        {
            if (ctx.User.Id != parts.UserId)
            {
                await ctx.SendEphemeralAsync(PlayerMessageBuilder.PickerNotYours(
                        "Only the person who started this import can use these buttons."))
                    .ConfigureAwait(false);
                return;
            }

            var messageId = ctx.Message?.Id ?? parts.MessageId;
            var session = await _sessions.TryGetPlaylistImportAsync(messageId, ctx.CancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                await ctx.RespondAsync(PlayerMessageBuilder.Error(
                        "Picker expired",
                        "That playlist import timed out — start create again if you still want it."))
                    .ConfigureAwait(false);
                return;
            }

            if (session.UserId != ctx.User.Id)
            {
                await ctx.SendEphemeralAsync(PlayerMessageBuilder.PickerNotYours(
                        "Only the person who started this import can use these buttons."))
                    .ConfigureAwait(false);
                return;
            }

            var pageValues = InteractionExtraData.ParseSelectedValues(
                (ctx.Interaction as ButtonInteraction)?.ExtraData);
            MergePlaylistSelections(session, pageValues);

            switch (parts.Action)
            {
                case MezubeButtonId.ActionCancel:
                    await _sessions.DeletePlaylistImportAsync(messageId, ctx.CancellationToken).ConfigureAwait(false);
                    await ctx.UpdateMessageAsync(PlayerMessageBuilder.Status(
                            "Import cancelled",
                            [
                                new("Playlist", session.PlaylistName),
                                new("Note", "It was created empty — delete it or add tracks later."),
                            ]))
                        .ConfigureAwait(false);
                    return;

                case MezubeButtonId.ActionPrev:
                    session.Page = Math.Max(0, session.Page - 1);
                    await _sessions.SavePlaylistImportAsync(messageId, session, ctx.CancellationToken)
                        .ConfigureAwait(false);
                    await ctx.UpdateMessageAsync(PlayerMessageBuilder.PlaylistImportPick(session, messageId, session.UserId))
                        .ConfigureAwait(false);
                    return;

                case MezubeButtonId.ActionNext:
                    session.Page = Math.Min(session.PageCount - 1, session.Page + 1);
                    await _sessions.SavePlaylistImportAsync(messageId, session, ctx.CancellationToken)
                        .ConfigureAwait(false);
                    await ctx.UpdateMessageAsync(PlayerMessageBuilder.PlaylistImportPick(session, messageId, session.UserId))
                        .ConfigureAwait(false);
                    return;

                case MezubeButtonId.ActionConfirm:
                    await ConfirmPlaylistImportAsync(ctx, session, messageId).ConfigureAwait(false);
                    return;

                default:
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist import button failed");
            try
            {
                await ctx.RespondAsync(PlayerMessageBuilder.Awkward()).ConfigureAwait(false);
            }
            catch (Exception notifyEx)
            {
                _logger.LogDebug(notifyEx, "Playlist import awkward reply failed");
            }
        }
    }

    private static void MergePlaylistSelections(PlaylistImportSession session, IReadOnlyList<string> pageValues)
    {
        if (pageValues.Count == 0)
        {
            return;
        }

        var set = new HashSet<string>(session.SelectedTokens, StringComparer.Ordinal);
        var pageTokens = session.PageCandidates().Select(c => c.Token).ToHashSet(StringComparer.Ordinal);
        set.RemoveWhere(t => pageTokens.Contains(t));
        foreach (var token in pageValues)
        {
            if (!pageTokens.Contains(token))
            {
                continue;
            }

            set.Add(token);
            if (set.Count >= PlaylistImportSession.SelectMax)
            {
                break;
            }
        }

        session.SelectedTokens = set.Take(PlaylistImportSession.SelectMax).ToList();
    }

    private async Task ConfirmPlaylistImportAsync(
        IInteractionContext ctx,
        PlaylistImportSession session,
        long messageId)
    {
        var selected = session.SelectedTokens
            .Select(t => session.Candidates.FirstOrDefault(c => c.Token == t))
            .Where(c => c is not null)
            .Cast<TrackCandidate>()
            .Take(PlaylistImportSession.SelectMax)
            .ToList();

        if (selected.Count == 0)
        {
            await ctx.UpdateMessageAsync(PlayerMessageBuilder.Error(
                    "Nothing selected",
                    "Pick at least one track, then tap Confirm."))
                .ConfigureAwait(false);
            await _sessions.SavePlaylistImportAsync(messageId, session, ctx.CancellationToken).ConfigureAwait(false);
            return;
        }

        var added = 0;
        var prepTracks = new List<TrackInfoEntity>();
        foreach (var candidate in selected)
        {
            try
            {
                var duration = candidate.DurationSeconds is double secs
                    ? TimeSpan.FromSeconds(secs)
                    : (TimeSpan?)null;
                var trackId = candidate.TrackId
                    ?? await _tracks.UpsertMetadataAsync(
                            new TrackEntity
                            {
                                Source = candidate.Source,
                                ExternalId = candidate.ExternalId,
                                Title = candidate.Title,
                                WebpageUrl = candidate.WebpageUrl,
                                ThumbnailUrl = candidate.ThumbnailUrl,
                                Duration = duration,
                                SourceBytes = candidate.SourceBytes,
                                IsTooLarge = false,
                            },
                            ctx.CancellationToken)
                        .ConfigureAwait(false);

                await _playlists.AddItemAsync(session.PlaylistId, trackId, ctx.User.Id, ctx.CancellationToken)
                    .ConfigureAwait(false);
                added++;
                prepTracks.Add(new TrackInfoEntity
                {
                    TrackId = trackId,
                    Title = candidate.Title,
                    MediaUrl = candidate.WebpageUrl,
                    WebpageUrl = candidate.WebpageUrl,
                    ThumbnailUrl = candidate.ThumbnailUrl,
                    RequestedBy = ctx.User.Username ?? ctx.User.Id.ToString(),
                    RequestedByUserId = ctx.User.Id,
                    Duration = duration,
                    Source = candidate.Source,
                    ExternalId = candidate.ExternalId,
                    SourceBytes = candidate.SourceBytes,
                    IsTooLarge = false,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playlist import add skipped for {Token}", candidate.Token);
            }
        }

        await _sessions.DeletePlaylistImportAsync(messageId, ctx.CancellationToken).ConfigureAwait(false);
        await ctx.UpdateMessageAsync(PlayerMessageBuilder.PlaylistImportDone(session.PlaylistName, added))
            .ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            var ok = 0;
            var fail = 0;
            foreach (var track in prepTracks)
            {
                try
                {
                    await _prep.EnsurePreparedAsync(ctx.Client, track, CancellationToken.None).ConfigureAwait(false);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    _logger.LogWarning(ex, "Playlist import prep failed for {Title}", track.Title);
                }
            }

            try
            {
                await ctx.Channel.SendEphemeralAsync(
                        PlayerMessageBuilder.PlaylistPrepDone(session.PlaylistName, ok, prepTracks.Count, fail),
                        ctx.User.Id)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Playlist import ephemeral notify failed");
            }
        }, CancellationToken.None);
    }

    private async Task BeginPlaylistImportAsync(
        ICommandContext ctx,
        long playlistId,
        string playlistName,
        string importUrl,
        CancellationToken cancellationToken)
    {
        var preparing = await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                "Importing playlist",
                [
                    new("Status", "Fetching track list (metadata only)…"),
                    new("Playlist", playlistName),
                ]))
            .ConfigureAwait(false);
        var preparingCreateTime = preparing.CreateTimeSeconds > 0 ? preparing.CreateTimeSeconds : (uint?)null;

        IReadOnlyList<TrackCandidate> candidates;
        try
        {
            candidates = await _externalPlaylists.ImportCandidatesAsync(
                    importUrl,
                    ctx.Author.Username ?? ctx.Author.Id.ToString(),
                    PlaylistImportSession.FetchMax,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist import fetch failed");
            await UpdateOrReplyAsync(ctx, preparing.MessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        if (candidates.Count == 0)
        {
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.Error(
                        "Nothing to import",
                        [
                            new("Playlist", playlistName),
                            new("Note", "Created empty — nothing usable after size filters."),
                        ]),
                    preparingCreateTime)
                .ConfigureAwait(false);
            return;
        }

        var session = new PlaylistImportSession
        {
            ClanId = ctx.Clan?.Id ?? ctx.Channel.ClanId,
            ChannelId = ctx.Channel.Id,
            UserId = ctx.Author.Id,
            PlaylistId = playlistId,
            PlaylistName = playlistName,
            SourceUrl = importUrl,
            Candidates = candidates.ToList(),
            SelectedTokens = [],
            Page = 0,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            await _sessions.SavePlaylistImportAsync(preparing.MessageId, session, cancellationToken)
                .ConfigureAwait(false);
            await UpdateOrReplyAsync(
                    ctx,
                    preparing.MessageId,
                    PlayerMessageBuilder.PlaylistImportPick(session, preparing.MessageId, ctx.Author.Id),
                    preparingCreateTime)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist import UI failed");
            await UpdateOrReplyAsync(ctx, preparing.MessageId, PlayerMessageBuilder.Awkward(), preparingCreateTime)
                .ConfigureAwait(false);
        }
    }
}
