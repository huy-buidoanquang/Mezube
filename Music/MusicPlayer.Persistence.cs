using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    private async Task<bool> EnsureCommandChannelAsync(ICommandContext ctx, CancellationToken cancellationToken)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        var allowed = await _access.IsCommandChannelAllowedAsync(clanId, ctx.Channel.Id, cancellationToken)
            .ConfigureAwait(false);
        if (allowed)
        {
            return true;
        }

        await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                "Channel not allowed",
                "Music enqueue is restricted to configured channels. Ask the clan owner (!musicchannel)."))
            .ConfigureAwait(false);
        return false;
    }

    private async Task PersistEnqueueAsync(long clanId, QueuedPlay play, string mode)
    {
        try
        {
            var payload = QueuedTrackPayload.From(
                play.Track,
                mode,
                play.Target,
                play.ReplyMessageId,
                play.ReplyCreateTimeSeconds);
            await _playerStore.EnqueueAsync(clanId, payload).ConfigureAwait(false);
            await _playerStore.TouchTtlAsync(clanId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis enqueue failed clan={ClanId}", clanId);
        }
    }

    private async Task<long> EnsureTrackIdAsync(TrackInfoEntity track, CancellationToken cancellationToken)
    {
        if (track.TrackId is long id && id > 0)
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(track.ExternalId) || string.IsNullOrWhiteSpace(track.Source)
            || track.Source is "unknown")
        {
            return 0;
        }

        return await _tracks.UpsertMetadataAsync(
                new TrackEntity
                {
                    Source = track.Source,
                    ExternalId = track.ExternalId!,
                    Title = track.Title,
                    WebpageUrl = track.WebpageUrl,
                    ThumbnailUrl = track.ThumbnailUrl,
                    Duration = track.Duration,
                    // Never promote MediaUrl (often youtube/soundcloud webpage) to playable_url.
                    PlayableUrl = PlayableUrlHelper.NullIfNotPrepared(track.MediaUrl),
                    SourceBytes = track.SourceBytes,
                    IsTooLarge = track.IsTooLarge,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long?> BeginHistoryAsync(
        long clanId,
        QueuedPlay play,
        string mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackId = await EnsureTrackIdAsync(play.Track, cancellationToken).ConfigureAwait(false);
            if (trackId == 0)
            {
                return null;
            }

            var historyId = await _history.StartAsync(
                    clanId,
                    trackId,
                    mode,
                    play.Target.ChannelId,
                    play.Track.RequestedByUserId,
                    cancellationToken)
                .ConfigureAwait(false);
            var trackWithId = play.Track.TrackId == trackId
                ? play.Track
                : new TrackInfoEntity
                {
                    TrackId = trackId,
                    Title = play.Track.Title,
                    MediaUrl = play.Track.MediaUrl,
                    WebpageUrl = play.Track.WebpageUrl,
                    ThumbnailUrl = play.Track.ThumbnailUrl,
                    RequestedBy = play.Track.RequestedBy,
                    RequestedByUserId = play.Track.RequestedByUserId,
                    Duration = play.Track.Duration,
                    Source = play.Track.Source,
                    ExternalId = play.Track.ExternalId,
                    SourceBytes = play.Track.SourceBytes,
                    IsTooLarge = play.Track.IsTooLarge,
                };
            await _playerStore.SetPlayHistoryIdAsync(clanId, historyId, cancellationToken).ConfigureAwait(false);
            await _playerStore.SetCurrentAsync(
                    clanId,
                    QueuedTrackPayload.From(
                        trackWithId,
                        mode,
                        play.Target,
                        play.ReplyMessageId,
                        play.ReplyCreateTimeSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
            return historyId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start play history clan={ClanId}", clanId);
            return null;
        }
    }

    private async Task CloseHistoryAsync(long? historyId, string reason, CancellationToken cancellationToken)
    {
        if (historyId is not long id)
        {
            return;
        }

        try
        {
            await _history.CloseAsync(id, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close play history {HistoryId}", id);
        }
    }

    /// <summary>CAS advance used by skip / natural end / vote-skip. Returns false if stale.</summary>
    private async Task<bool> TryAdvancePersistedAsync(
        long clanId,
        long expectedHistoryId,
        bool skipLoop,
        string endReason,
        CancellationToken cancellationToken)
    {
        await CloseHistoryAsync(expectedHistoryId, endReason, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _playerStore.TryAdvanceAsync(clanId, expectedHistoryId, skipLoop, cancellationToken)
                .ConfigureAwait(false);
            return result.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis advance failed clan={ClanId}", clanId);
            return true; // fall back to in-memory advance
        }
    }

    private async Task ClearPersistedSessionAsync(long clanId, CancellationToken cancellationToken)
    {
        try
        {
            await _history.CloseOpenForClanAsync(clanId, PlayEndReason.Stop, cancellationToken).ConfigureAwait(false);
            await _playerStore.ClearSessionAsync(clanId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear Redis session clan={ClanId}", clanId);
        }
    }
}
