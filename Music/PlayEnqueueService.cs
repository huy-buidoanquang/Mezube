using Mezon.Net.Sdk;
using Mezube.Bot;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Media;
using Mezube.Playback;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public enum PlayEnqueueKind
{
    Started,
    Queued,
    CutIn,
    QueueFull,
    SlotsFull,
    ModeConflict,
}

/// <summary>Single enqueue/start path for !play, search pick, playlist, and SoundCloud sets.</summary>
public sealed class PlayEnqueueService
{
    private readonly BotOptions _options;
    private readonly IClanPlayerStore _playerStore;
    private readonly TrackPrepService _prep;
    private readonly ILogger<PlayEnqueueService> _logger;

    public PlayEnqueueService(
        BotOptions options,
        IClanPlayerStore playerStore,
        TrackPrepService prep,
        ILogger<PlayEnqueueService> logger)
    {
        _options = options;
        _playerStore = playerStore;
        _prep = prep;
        _logger = logger;
    }

    public async Task<PlayEnqueueKind> EnqueueOrStartAsync(
        ClanPlaybackSession state,
        QueuedPlay play,
        PlaybackMode mode,
        MezonClient client,
        long notifyChannelId,
        long controlUserId,
        bool attachPreparingAsControl,
        Func<ClanPlaybackSession, bool> tryClaimSlot,
        Action<ClanPlaybackSession> resetPrepToken,
        Action<ClanPlaybackSession, long> startPump,
        CancellationToken cancellationToken = default)
        => await EnqueueManyOrStartAsync(
            state,
            [play],
            mode,
            client,
            notifyChannelId,
            controlUserId,
            attachPreparingAsControl,
            tryClaimSlot,
            resetPrepToken,
            startPump,
            onTooLarge: null,
            releaseSlot: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<PlayEnqueueKind> EnqueueManyOrStartAsync(
        ClanPlaybackSession state,
        IReadOnlyList<QueuedPlay> plays,
        PlaybackMode mode,
        MezonClient client,
        long notifyChannelId,
        long controlUserId,
        bool attachPreparingAsControl,
        Func<ClanPlaybackSession, bool> tryClaimSlot,
        Action<ClanPlaybackSession> resetPrepToken,
        Action<ClanPlaybackSession, long> startPump,
        Action<ClanPlaybackSession, QueuedPlay>? onTooLarge = null,
        Action<ClanPlaybackSession>? releaseSlot = null,
        CancellationToken cancellationToken = default)
    {
        if (plays.Count == 0)
        {
            return PlayEnqueueKind.QueueFull;
        }

        plays = ClanSessionBinder.PinAll(state, plays);
        var clanId = plays[0].Target.ClanId;
        var modeKey = mode == PlaybackMode.Voice ? "voice" : "streaming";
        return await state.WithStartLockAsync(async () =>
        {
            plays = ClanSessionBinder.PinAll(state, plays);
            if (state.IsPlaying || state.PumpRunning)
            {
                if (state.Mode != mode)
                {
                    return PlayEnqueueKind.ModeConflict;
                }

                var interruptDefault = mode == PlaybackMode.Streaming
                    && state.Queue.CurrentItem?.IsFromDefault == true;
                state.PlayingDefaultPlaylist = false;
                var added = 0;
                foreach (var play in plays)
                {
                    if (state.Queue.TotalCount >= _options.MaxQueuePerClan)
                    {
                        break;
                    }

                    if (interruptDefault && added == 0)
                    {
                        state.Queue.EnqueueFront(play);
                        await PersistEnqueueAsync(play, modeKey, front: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        state.Queue.Enqueue(play);
                        await PersistEnqueueAsync(play, modeKey, front: false, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    StartBackgroundPrep(client, state, play, OnPrepError(state, play, onTooLarge));
                    added++;
                }

                if (added == 0)
                {
                    return PlayEnqueueKind.QueueFull;
                }

                // The request was accepted and now owns the session again.
                // Do not let a previous !stop/idle/failure reason make the
                // next natural EOF tear down SFU.
                state.ResetTerminalDestroyReason();

                if (interruptDefault)
                {
                    state.LastDestroyReason = PlayerDestroyReason.Skip;
                    state.CancelTrack();
                    return PlayEnqueueKind.CutIn;
                }

                return PlayEnqueueKind.Queued;
            }

            if (state.Queue.TotalCount + plays.Count > _options.MaxQueuePerClan
                && state.Queue.TotalCount >= _options.MaxQueuePerClan)
            {
                return PlayEnqueueKind.QueueFull;
            }

            if (!tryClaimSlot(state))
            {
                return PlayEnqueueKind.SlotsFull;
            }

            state.BumpGeneration();
            state.CancelIdleDestroy();
            state.IdleAwaitingDisconnect = false;
            state.PlayingDefaultPlaylist = false;
            resetPrepToken(state);

            var startedAdded = 0;
            foreach (var play in plays)
            {
                if (state.Queue.TotalCount >= _options.MaxQueuePerClan)
                {
                    break;
                }

                state.Queue.Enqueue(play);
                await PersistEnqueueAsync(play, modeKey, front: false, cancellationToken)
                    .ConfigureAwait(false);
                StartBackgroundPrep(client, state, play, OnPrepError(state, play, onTooLarge));
                startedAdded++;
            }

            if (startedAdded == 0)
            {
                releaseSlot?.Invoke(state);
                return PlayEnqueueKind.QueueFull;
            }

            // The request was accepted and now owns the session again. This
            // must happen after enqueue/persistence succeeds so rejected
            // mode/queue/slot requests cannot alter the existing state.
            state.ResetTerminalDestroyReason();

            var first = plays[0];
            state.Mode = mode;
            state.Target = first.Target;
            state.NotifyClient = client;
            state.NotifyChannelId = notifyChannelId;
            state.NotifyChannel = null;
            state.ControlUserId = controlUserId;
            state.ClanId = clanId;
            state.ChannelId = first.Target.ChannelId;
            if (attachPreparingAsControl)
            {
                state.ControlMessageId = first.ReplyMessageId;
                state.ControlMessageCreateTimeSeconds = first.ReplyCreateTimeSeconds;
                state.ControlMessageHasButtons = false;
            }

            startPump(state, clanId);
            return PlayEnqueueKind.Started;
        }).ConfigureAwait(false);
    }

    public async Task PersistEnqueueAsync(
        QueuedPlay play,
        string mode,
        bool front,
        CancellationToken cancellationToken)
    {
        var clanId = play.Target.ClanId;
        var channelId = play.Target.ChannelId;
        try
        {
            var payload = QueuedTrackPayload.From(
                play.Track,
                mode,
                play.Target,
                play.ReplyMessageId,
                play.ReplyCreateTimeSeconds,
                play.IsFromDefault,
                play.WantVideo);
            if (front)
            {
                await _playerStore.EnqueueFrontAsync(clanId, channelId, payload, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _playerStore.EnqueueAsync(clanId, channelId, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis enqueue failed clan={ClanId} channel={ChannelId}", clanId, channelId);
        }
    }

    public void StartBackgroundPrep(
        MezonClient client,
        ClanPlaybackSession state,
        QueuedPlay play,
        Action<Exception>? onError = null)
    {
        var ct = state.PrepCts?.Token ?? CancellationToken.None;
        // Bot playback is SFU audio-only. Keep the legacy video preparation
        // available for unrelated callers, but never schedule it for a queue
        // item that can reach the playback pump.
        _prep.StartBackgroundPrep(client, play.Track, PreparedAssetKind.Audio, ct, onError);
    }

    private static Action<Exception>? OnPrepError(
        ClanPlaybackSession state,
        QueuedPlay play,
        Action<ClanPlaybackSession, QueuedPlay>? onTooLarge)
        => onTooLarge is null
            ? null
            : ex =>
            {
                if (ex is AudioTooLargeException)
                {
                    onTooLarge(state, play);
                }
            };
}
