using Mezon.Net.Core;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Media;
using Mezube.Playback;
using Mezube.Ui;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    private bool TryClaimPlaySlot(ClanPlaybackSession state)
    {
        if (state.HoldsPlaySlot)
        {
            return true;
        }

        if (!_playSlots.Wait(0))
        {
            return false;
        }

        state.HoldsPlaySlot = true;
        return true;
    }

    private void ReleasePlaySlot(ClanPlaybackSession state)
    {
        if (!state.HoldsPlaySlot)
        {
            return;
        }

        state.HoldsPlaySlot = false;
        _playSlots.Release();
    }

    public async Task<int> ArmDefaultAutoplayOnStartupAsync(
        MezonClient client,
        IReadOnlySet<long>? excludedClanIds = null,
        CancellationToken cancellationToken = default)
    {
        var clanIds = await _playlists.ListDefaultClanIdsAsync(cancellationToken).ConfigureAwait(false);
        var armed = 0;
        foreach (var clanId in clanIds)
        {
            if (excludedClanIds?.Contains(clanId) == true)
            {
                continue;
            }

            if (await ArmDefaultAutoplayForClanAsync(clanId, client, cancellationToken).ConfigureAwait(false))
            {
                armed++;
            }
        }

        return armed;
    }

    public async Task<bool> ArmDefaultAutoplayForClanAsync(
        long clanId,
        MezonClient client,
        CancellationToken cancellationToken = default)
    {
        var defaultPl = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (defaultPl is null)
        {
            return false;
        }

        if (await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken).ConfigureAwait(false) is not long streamId)
        {
            _logger.LogDebug("Default autoplay not armed: no default stream channel clan={ClanId}", clanId);
            return false;
        }

        var state = GetState(clanId);
        state.ClanId = clanId;
        if (!ClanSessionBinder.IsLive(state))
        {
            state.ChannelId = streamId;
        }
        state.NotifyClient ??= client;
        state.DefaultAutoplayArmed = true;
        state.PlayingDefaultPlaylist = false;

        if (!state.IsPlaying && state.Queue.TotalCount == 0)
        {
            ScheduleIdleDestroy(clanId, state);
        }

        return true;
    }

    public async Task<IReadOnlySet<long>> RestoreSessionsOnStartupAsync(
        MezonClient client,
        CancellationToken cancellationToken = default)
    {
        var restored = new HashSet<long>();
        var sessions = await _playerStore.ListActiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        var closedClans = new HashSet<long>();
        foreach (var session in sessions)
        {
            if (closedClans.Add(session.ClanId))
            {
                try
                {
                    await _history.CloseOpenForClanAsync(session.ClanId, PlayEndReason.Restart, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Close open history failed clan={ClanId}", session.ClanId);
                }
            }

            if (restored.Contains(session.ClanId)
                && TryGetState(session.ClanId, out var live)
                && ClanSessionBinder.IsLive(live))
            {
                if (live.ChannelId != session.ChannelId)
                {
                    await _playerStore.ClearSessionAsync(session.ClanId, session.ChannelId, cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (await TryRestoreClanSessionAsync(session.ClanId, session.ChannelId, client, cancellationToken)
                    .ConfigureAwait(false))
            {
                restored.Add(session.ClanId);
            }
        }

        return restored;
    }

    /// <summary>
    /// Starts the clan pump with a lifetime independent of the command CancellationToken.
    /// Command CTs often cancel when the handler returns; pumps must outlive that.
    /// </summary>
    private void StartPump(ClanPlaybackSession state, long clanId)
        => _ = PumpAsync(state, clanId, state.SessionToken);

    public void CancelAllSessions()
    {
        foreach (var state in _states.Values)
        {
            state.CancelSession();
        }
    }

    private async Task PumpAsync(ClanPlaybackSession state, long clanId, CancellationToken cancellationToken)
    {
        if (!await state.TryEnterPumpAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QueuedPlay? item;
                if (state.ReplayCurrent && state.Queue.CurrentItem is { } current)
                {
                    item = current;
                    state.ReplayCurrent = false;
                    state.PendingSeekMs = 0;
                }
                else
                {
                    item = state.Queue.TryDequeueNext();
                }

                var mode = state.Mode;
                if (item is null)
                {
                    if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                    {
                        state.IsPlaying = false;
                        ScheduleIdleDestroy(clanId, state);
                        return;
                    }

                    if (state.PlayingDefaultPlaylist)
                    {
                        if (await TryEnqueueNextDefaultTrackAsync(state, clanId, CancellationToken.None)
                                .ConfigureAwait(false))
                        {
                            continue;
                        }

                        state.PlayingDefaultPlaylist = false;
                    }

                    state.IsPlaying = false;
                    state.LastDestroyReason = PlayerDestroyReason.QueueEmpty;
                    ScheduleIdleDestroy(clanId, state);
                    return;
                }

                var track = item.Track;
                var target = ClanSessionBinder.BoundTarget(state) ?? item.Target;
                state.Target = target;
                if (target.ChannelId != 0)
                {
                    state.ChannelId = target.ChannelId;
                }

                try
                {
                    await _playerStore.EnsureCurrentAsync(clanId, SessionChannelId(state), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Redis EnsureCurrent failed clan={ClanId} channel={ChannelId}",
                        clanId,
                        state.ChannelId);
                }

                state.CancelIdleDestroy();
                state.IsPlaying = true;
                using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                state.SetTrackCts(trackCts);
                var playStopwatch = Stopwatch.StartNew();

                try
                {
                    _logger.LogDebug(
                        "Playback pipeline start mode={Mode} title={Title} channel={ChannelId} queuedNext={QueuedNext}",
                        mode,
                        track.Title,
                        target.ChannelId,
                        state.Queue.Count);
                    await _streamingSink.PlayAsync(
                            target,
                            track,
                            PreparedAssetKind.Audio,
                            trackCts.Token)
                        .ConfigureAwait(false);
                    _logger.LogDebug(
                        "Playback sink ready mode={Mode} title={Title} channel={ChannelId} elapsedMs={ElapsedMs}",
                        mode,
                        track.Title,
                        target.ChannelId,
                        playStopwatch.ElapsedMilliseconds);
                    if (item.ReplyMessageId is long replyId)
                    {
                        state.ControlMessageId = replyId;
                        state.ControlMessageCreateTimeSeconds = item.ReplyCreateTimeSeconds;
                        state.ControlMessageHasButtons = false;
                    }

                    await SendNowPlayingAsync(state, includeMusicViz: true).ConfigureAwait(false);

                    var modeKey = mode == PlaybackMode.Voice ? "voice" : "streaming";
                    state.PlayHistoryId = await BeginHistoryAsync(clanId, item, modeKey, trackCts.Token)
                        .ConfigureAwait(false);

                    var trackEndedNaturally = false;
                    try
                    {
                        await WaitForTrackEndAsync(state, track, target, trackCts.Token).ConfigureAwait(false);
                        trackEndedNaturally = !trackCts.IsCancellationRequested;
                    }
                    catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                    {
                    }

                    try
                    {
                        // Keep the WS + listeners across normal EOF; only an
                        // explicit cancellation marked UserStop tears it down here.
                        if (ShouldStopSfuAfterTrack(trackEndedNaturally, state.LastDestroyReason))
                        {
                            await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            await _streamingSink.EndTrackAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Stop sink failed channel={ChannelId}", target.ChannelId);
                    }
                }
                catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                {
                    // !skip / !stop cancelled prepare/play. The command already replied �
                    // do NOT treat this as playback failure (that was sending a second bot message).
                    try
                    {
                        if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                        {
                            await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            await _streamingSink.EndTrackAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Teardown after skip/stop cancel ignored channel={ChannelId}", target.ChannelId);
                    }
                }
                catch (AudioTooLargeException)
                {
                    await NotifyCopyrightBlockedAsync(state, item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Media prep / single-track failures keep the SFU room alive and advance the queue.
                    var mediaFailure = IsMediaPrepFailure(ex);
                    state.LastDestroyReason = mediaFailure
                        ? PlayerDestroyReason.TrackFailed
                        : PlayerDestroyReason.SfuFailed;
                    _logger.LogError(ex, "Playback failed for {Title} channel={ChannelId}", track.Title, target.ChannelId);
                    await NotifyPlaybackFailureAsync(state, track, ex).ConfigureAwait(false);

                    if (mediaFailure)
                    {
                        try
                        {
                            await _streamingSink.EndTrackAsync(target, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception endEx)
                        {
                            _logger.LogDebug(endEx, "Soft end after media failure ignored channel={ChannelId}", target.ChannelId);
                        }
                    }
                    else
                    {
                        try
                        {
                            await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception stopEx)
                        {
                            _logger.LogDebug(stopEx, "Stop after failure ignored channel={ChannelId}", target.ChannelId);
                        }

                        if (IsSfuInfrastructureFailure(ex))
                        {
                            state.Queue.Clear(clearCurrent: true);
                            break;
                        }
                    }
                }
                finally
                {
                    var wasSeek = state.LastDestroyReason == PlayerDestroyReason.Seek;
                    var historyId = state.PlayHistoryId;
                    if (!wasSeek)
                    {
                        state.PlayHistoryId = null;
                    }

                    var endReason = state.LastDestroyReason switch
                    {
                        PlayerDestroyReason.Skip => PlayEndReason.Skip,
                        PlayerDestroyReason.UserStop => PlayEndReason.Stop,
                        PlayerDestroyReason.SfuFailed => PlayEndReason.Error,
                        PlayerDestroyReason.TrackFailed => PlayEndReason.Error,
                        PlayerDestroyReason.Seek => PlayEndReason.Completed,
                        _ => PlayEndReason.Completed,
                    };
                    if (!wasSeek && historyId is long hid)
                    {
                        var skipLoop = endReason is PlayEndReason.Skip or PlayEndReason.VoteSkip
                            or PlayEndReason.Stop or PlayEndReason.Error or PlayEndReason.TooLarge;
                        var advanced = await TryAdvancePersistedAsync(
                                clanId,
                                SessionChannelId(state),
                                hid,
                                skipLoop,
                                endReason,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (advanced
                            && endReason == PlayEndReason.Completed
                            && !skipLoop
                            && !item.IsFromDefault)
                        {
                            var loop = await _playerStore.GetLoopModeAsync(clanId, SessionChannelId(state)).ConfigureAwait(false);
                            if (loop == LoopMode.Track)
                            {
                                state.Queue.EnqueueFront(item);
                            }
                            else if (loop == LoopMode.Queue)
                            {
                                state.Queue.Enqueue(item);
                            }
                        }
                    }

                    if (state.LastDestroyReason != PlayerDestroyReason.UserStop
                        && state.LastDestroyReason != PlayerDestroyReason.Seek)
                    {
                        state.LastDestroyReason = PlayerDestroyReason.None;
                    }
                }

                if (state.LastDestroyReason == PlayerDestroyReason.Seek)
                {
                    state.ClearTrackCts();
                    continue;
                }

                if (state.LastDestroyReason is PlayerDestroyReason.Skip or PlayerDestroyReason.UserStop)
                {
                    state.ClearTrackCts();
                    if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    await Task.Delay(_options.InterTrackDelayMs, trackCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                {
                    if (state.LastDestroyReason == PlayerDestroyReason.Seek)
                    {
                        continue;
                    }

                    if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                    {
                        break;
                    }
                }
                finally
                {
                    state.ClearTrackCts();
                }
            }
        }
        finally
        {
            state.IsPlaying = false;
            state.ExitPump();
            ReleasePlaySlot(state);
            if (state.Queue.Count == 0 && state.Queue.Current is null)
            {
                ScheduleIdleDestroy(clanId, state);
            }
        }
    }

    private async Task NotifyCopyrightBlockedAsync(ClanPlaybackSession state, QueuedPlay item)
    {
        if (item.ReplyMessageId is long messageId && state.NotifyClient is not null)
        {
            try
            {
                var channel = await state.ResolveNotifyChannelAsync().ConfigureAwait(false);
                if (channel is null)
                {
                    return;
                }
                await channel.UpdateMessageAsync(
                        messageId,
                        PlayerMessageBuilder.CopyrightBlocked(),
                        hideEdited: true,
                        createTimeSeconds: item.ReplyCreateTimeSeconds)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update copyright message for {Title}", item.Track.Title);
            }
        }

        await NotifyPlaybackFailureAsync(state, item.Track, new AudioTooLargeException(
                item.Track.Title,
                item.Track.SourceBytes ?? 0,
                _options.MaxAudioBytes))
            .ConfigureAwait(false);
    }

    private void ScheduleIdleDestroy(long clanId, ClanPlaybackSession state)
    {
        var generation = state.Generation;
        state.ScheduleIdleDestroy(
            ClanSessionBinder.IdleDelay(state),
            generation,
            gen =>
            {
                if (state.Generation != gen)
                {
                    return;
                }

                if (state.IsPlaying || state.Queue.Count > 0 || state.Queue.Current is not null)
                {
                    return;
                }

                if (state.LastDestroyReason == PlayerDestroyReason.UserStop)
                {
                    TearDownIdleSession(clanId, state);
                    return;
                }

                if (state.IdleAwaitingDisconnect || !state.DefaultAutoplayArmed)
                {
                    TearDownIdleSession(clanId, state);
                    return;
                }

                _ = TryResumeDefaultAfterIdleAsync(clanId, state, gen);
            });
    }

    private void TearDownIdleSession(long clanId, ClanPlaybackSession state)
    {
        if (!_states.TryGetValue(clanId, out var current) || !ReferenceEquals(current, state)
            || !_states.TryRemove(clanId, out _))
        {
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.IdleTimeout;
        var target = state.Target;
        var mode = state.Mode;
        var channelId = SessionChannelId(state);
        state.Target = null;
        state.PlayingDefaultPlaylist = false;
        _logger.LogDebug(
            "Idle player session destroyed clan={ClanId} channel={ChannelId} reason={Reason}",
            clanId,
            channelId,
            state.LastDestroyReason);

        if (channelId != 0)
        {
            _ = ClearPersistedSessionAsync(clanId, channelId, CancellationToken.None);
        }

        if (mode == PlaybackMode.Streaming && target is { })
        {
            _ = TearDownStreamingIdleAsync(target);
        }

        state.Dispose();
    }

    private async Task TryResumeDefaultAfterIdleAsync(long clanId, ClanPlaybackSession state, int generation)
    {
        try
        {
            if (state.Generation != generation)
            {
                return;
            }

            if (state.IsPlaying || state.Queue.Count > 0 || state.Queue.Current is not null)
            {
                return;
            }

            if (state.LastDestroyReason == PlayerDestroyReason.UserStop || !state.DefaultAutoplayArmed)
            {
                TearDownIdleSession(clanId, state);
                return;
            }

            var started = await TryStartDefaultPlaylistAsync(state, clanId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!started)
            {
                state.IdleAwaitingDisconnect = true;
                ScheduleIdleDestroy(clanId, state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Default playlist idle resume failed clan={ClanId}", clanId);
            TearDownIdleSession(clanId, state);
        }
    }

    /// <summary>
    /// Arms and starts default playlist autoplay on the clan default stream channel when idle.
    /// </summary>
    private async Task<bool> TryStartDefaultPlaylistAsync(
        ClanPlaybackSession state,
        long clanId,
        CancellationToken cancellationToken)
    {
        if (state.IsPlaying || state.Queue.TotalCount > 0)
        {
            return false;
        }

        state.PlayingDefaultPlaylist = true;
        state.DefaultAutoplayArmed = true;
        state.IdleAwaitingDisconnect = false;
        state.Mode = PlaybackMode.Streaming;
        state.ClanId = clanId;
        state.LastDestroyReason = PlayerDestroyReason.None;

        if (!state.HoldsPlaySlot && !TryClaimPlaySlot(state))
        {
            state.PlayingDefaultPlaylist = false;
            return false;
        }

        state.BumpGeneration();
        state.CancelIdleDestroy();
        ResetPrepToken(state);

        if (!await TryEnqueueNextDefaultTrackAsync(state, clanId, cancellationToken).ConfigureAwait(false))
        {
            ReleasePlaySlot(state);
            state.PlayingDefaultPlaylist = false;
            return false;
        }

        StartPump(state, clanId);
        return true;
    }

    private async Task<bool> TryEnqueueNextDefaultTrackAsync(
        ClanPlaybackSession state,
        long clanId,
        CancellationToken cancellationToken)
    {
        var playlist = await _playlists.TryGetDefaultAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (playlist is null)
        {
            state.CachedDefaultPlaylistItems = null;
            state.CachedDefaultPlaylistId = null;
            state.DefaultAutoplayArmed = false;
            state.PlayingDefaultPlaylist = false;
            return false;
        }

        var bound = ClanSessionBinder.BoundTarget(state);
        long channelId;
        string? channelLabel = bound?.ChannelLabel;
        if (bound is { ChannelId: not 0 })
        {
            channelId = bound.ChannelId;
        }
        else if (await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken).ConfigureAwait(false)
                 is long defaultChannelId)
        {
            channelId = defaultChannelId;
        }
        else
        {
            _logger.LogDebug("Default playlist skipped: no bound or default stream channel clan={ClanId}", clanId);
            return false;
        }

        IReadOnlyList<Domain.Entities.PlaylistItemEntity> items;
        if (state.CachedDefaultPlaylistId == playlist.Id && state.CachedDefaultPlaylistItems is { Count: > 0 } cached)
        {
            items = cached;
        }
        else
        {
            items = await _playlists.ListItemsAsync(playlist.Id, cancellationToken).ConfigureAwait(false);
            state.CachedDefaultPlaylistId = playlist.Id;
            state.CachedDefaultPlaylistItems = items;
        }
        if (items.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(channelLabel) && state.NotifyClient is not null)
        {
            try
            {
                var channel = await state.NotifyClient.GetChannelAsync(channelId, cancellationToken)
                    .ConfigureAwait(false);
                channelLabel = channel.Name;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetChannelAsync failed for default stream {ChannelId}", channelId);
            }
        }

        var target = new PlaybackTarget(clanId, channelId, ChannelLabel: channelLabel);
        state.Target = target;
        state.ChannelId = channelId;
        state.Mode = PlaybackMode.Streaming;

        // Walk playlist once looking for a playable track from the cursor.
        for (var attempt = 0; attempt < items.Count; attempt++)
        {
            var index = state.DefaultPlaylistCursor % items.Count;
            if (state.DefaultPlaylistCursor < 0)
            {
                index = 0;
            }

            state.DefaultPlaylistCursor = index + 1;
            try
            {
                await _playerStore.SetPlayerFieldAsync(
                        clanId,
                        channelId,
                        "default_playlist_cursor",
                        state.DefaultPlaylistCursor,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            var entry = items[index];
            if (entry.Track is null || entry.Track.IsTooLarge)
            {
                continue;
            }

            if (entry.Track.SourceBytes is long bytes && bytes > _options.MaxAudioBytes)
            {
                continue;
            }

            var info = entry.Track.ToTrackInfo("Auto");
            var play = ClanSessionBinder.Pin(state, new QueuedPlay(info, target, IsFromDefault: true));
            state.Queue.Enqueue(play);
            await PersistEnqueueAsync(clanId, play, "streaming").ConfigureAwait(false);
            if (state.NotifyClient is not null)
            {
                StartBackgroundPrep(state.NotifyClient, state, play);
            }

            return true;
        }

        return false;
    }

    private async Task<bool> TryRestoreClanSessionAsync(
        long clanId,
        long channelId,
        MezonClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _playerStore.GetCurrentAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
            var pending = await _playerStore.SnapshotQueueAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
            if (current is null && pending.Count == 0)
            {
                return false;
            }

            var state = GetState(clanId);
            if (ClanSessionBinder.IsLive(state) && state.ChannelId != 0 && state.ChannelId != channelId)
            {
                await _playerStore.ClearSessionAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            state.CancelIdleDestroy();
            state.Queue.Clear();
            state.ClanId = clanId;
            state.ChannelId = channelId;
            state.NotifyClient = client;
            state.ControlMessageId = null;
            state.ControlMessageCreateTimeSeconds = null;
            state.ControlMessageHasButtons = false;
            state.ControlUserId = null;
            state.PlayHistoryId = null;
            state.PlayingDefaultPlaylist = false;
            state.DefaultAutoplayArmed = false;
            state.DefaultPlaylistCursor = 0;
            state.LastDestroyReason = PlayerDestroyReason.None;

            QueuedPlay? first = null;
            if (current is not null)
            {
                first = ToQueuedPlay(clanId, current);
                state.Mode = ParsePlaybackMode(current.Mode);
                state.Target = first.Target;
                state.Queue.Enqueue(first);
            }

            foreach (var item in pending)
            {
                state.Queue.Enqueue(ToQueuedPlay(clanId, item));
            }

            if (current is null && pending.Count > 0)
            {
                state.Mode = ParsePlaybackMode(pending[0].Mode);
                state.Target = pending[0].ToTarget(clanId);
            }

            if (state.Mode == PlaybackMode.Voice)
            {
                _logger.LogInformation(
                    "Dropping restored voice session clan={ClanId} channel={ChannelId}; bot playback is limited to stream channels",
                    clanId,
                    channelId);
                state.Queue.Clear();
                await _playerStore.ClearSessionAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await _playerStore.SetPlayHistoryIdAsync(clanId, channelId, null, cancellationToken).ConfigureAwait(false);
            await _playerStore.SetPositionAsync(clanId, channelId, 0, 0, paused: false, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var cursorRaw = await _playerStore.GetPlayerAsync(clanId, channelId, cancellationToken)
                    .ConfigureAwait(false);
                if (cursorRaw.TryGetValue("default_playlist_cursor", out var c)
                    && int.TryParse(c, out var cursor))
                {
                    state.DefaultPlaylistCursor = cursor;
                }
            }
            catch
            {
            }

            if (!state.HoldsPlaySlot && !TryClaimPlaySlot(state))
            {
                _logger.LogWarning(
                    "Restore skipped for clan={ClanId} channel={ChannelId}: playback slots full",
                    clanId,
                    channelId);
                state.Queue.Clear();
                return false;
            }

            state.HoldsPlaySlot = true;
            ResetPrepToken(state);
            if (state.NotifyClient is not null)
            {
                if (state.Queue.CurrentItem is { } currentPlay)
                {
                    StartBackgroundPrep(state.NotifyClient, state, currentPlay);
                }

                var snapshot = state.Queue.Snapshot();
                for (var i = 0; i < snapshot.Count; i++)
                {
                    StartBackgroundPrep(state.NotifyClient, state, snapshot[i]);
                }
            }

            _logger.LogInformation(
                "Restored playback session clan={ClanId} channel={ChannelId} current={HasCurrent} pending={PendingCount} mode={Mode}",
                clanId,
                channelId,
                current is not null,
                pending.Count,
                state.Mode);
            StartPump(state, clanId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore playback session clan={ClanId} channel={ChannelId}", clanId, channelId);
            try
            {
                await _playerStore.ClearSessionAsync(clanId, channelId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(
                    clearEx,
                    "Failed to clear broken restored session clan={ClanId} channel={ChannelId}",
                    clanId,
                    channelId);
            }

            return false;
        }
    }

    private static QueuedPlay ToQueuedPlay(long clanId, QueuedTrackPayload payload)
        => new(
            payload.ToTrackInfo(),
            payload.ToTarget(clanId),
            payload.ReplyMessageId,
            payload.ReplyCreateTimeSecs,
            payload.IsFromDefault,
            payload.WantVideo);

    private static PlaybackMode ParsePlaybackMode(string? mode)
        => string.Equals(mode, "voice", StringComparison.OrdinalIgnoreCase)
            ? PlaybackMode.Voice
            : PlaybackMode.Streaming;

    private async Task TearDownStreamingIdleAsync(PlaybackTarget target)
    {
        try
        {
            await _streamingSink.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Streaming session stop on idle timeout failed channel={ChannelId}",
                target.ChannelId);
        }
    }

    /// <summary>How long to keep clan player state after !stop, and the default-playlist idle wait.</summary>
    private static readonly TimeSpan IdleSessionTtl = ClanSessionBinder.IdleDefaultResume;

    private ClanPlaybackSession GetState(long clanId)
    {
        if (clanId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clanId));
        }

        return _states.GetOrAdd(clanId, id => new ClanPlaybackSession { ClanId = id });
    }

    private ClanPlaybackSession GetState(long clanId, long channelId)
    {
        var state = GetState(clanId);
        if (channelId != 0 && (state.ChannelId == 0 || !ClanSessionBinder.IsLive(state)))
        {
            state.ChannelId = channelId;
        }

        return state;
    }

    private static long SessionChannelId(ClanPlaybackSession state)
        => state.ChannelId != 0 ? state.ChannelId : state.Target?.ChannelId ?? 0;

    private bool TryGetState(long clanId, out ClanPlaybackSession state)
        => _states.TryGetValue(clanId, out state!);

    private static bool IsSessionActive(ClanPlaybackSession state)
        => ClanSessionBinder.IsLive(state);

    private ClanPlaybackSession? FindSessionByControlMessage(long clanId, long messageId)
    {
        if (!TryGetState(clanId, out var state) || state.ControlMessageId != messageId)
        {
            return null;
        }

        return state;
    }

    private enum ControlSessionResolve
    {
        Found,
        Nothing,
        Ambiguous,
    }

    private ControlSessionResolve TryResolveControlSession(
        long clanId,
        long? preferredChannelId,
        out ClanPlaybackSession? state)
    {
        state = null;
        if (!TryGetState(clanId, out var resolved) || resolved.ClanId != clanId || !IsSessionActive(resolved))
        {
            return ControlSessionResolve.Nothing;
        }

        state = resolved;
        return ControlSessionResolve.Found;
    }

    private async Task SendNowPlayingAsync(ClanPlaybackSession state, bool includeMusicViz)
    {
        if (state.ControlMessageId is not long messageId || state.ClanId is not long clanId)
        {
            return;
        }

        if (state.NotifyClient is null || state.NotifyChannelId is null)
        {
            return;
        }

        try
        {
            await _viz.EnsureAsync(state.NotifyClient, CancellationToken.None).ConfigureAwait(false);
            var channel = await state.ResolveNotifyChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                return;
            }
            var content = BuildNowPlayingContent(
                state,
                clanId,
                includeMusicViz,
                includeControls: state.ControlMessageHasButtons);
            await channel.UpdateMessageAsync(
                    messageId,
                    content,
                    hideEdited: true,
                    createTimeSeconds: state.ControlMessageCreateTimeSeconds)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send now playing UI for message {MessageId}", messageId);
        }
    }

    private static readonly TimeSpan UpNextLead = TimeSpan.FromSeconds(10);

    private async Task WaitForTrackEndAsync(
        ClanPlaybackSession state,
        TrackInfoEntity track,
        PlaybackTarget target,
        CancellationToken cancellationToken)
    {
        // The in-process SFU publisher EOF is authoritative; duration is only used for up-next UX.
        using var endedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var endedWait = _streamingSink.WaitUntilTrackEndedAsync(target.ChannelId, endedCts.Token);
        var upNextTask = track.Duration is { } d && d > UpNextLead
            ? NotifyStreamingUpNextAsync(state, d, endedCts.Token)
            : Task.CompletedTask;

        await endedWait.ConfigureAwait(false);
        endedCts.Cancel();
        try
        {
            await upNextTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogDebug(
            "Streaming track ended by SFU publisher EOF title={Title} channel={ChannelId}",
            track.Title,
            target.ChannelId);
    }

    internal static bool ShouldStopSfuAfterTrack(
        bool trackEndedNaturally,
        PlayerDestroyReason destroyReason)
        => !trackEndedNaturally && destroyReason == PlayerDestroyReason.UserStop;

    private async Task NotifyStreamingUpNextAsync(
        ClanPlaybackSession state,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var untilNotify = duration - UpNextLead;
        if (untilNotify > TimeSpan.Zero)
        {
            await Task.Delay(untilNotify, cancellationToken).ConfigureAwait(false);
        }

        var next = state.Queue.PeekNext();
        if (next is null)
        {
            return;
        }

        await NotifyUpNextAsync(state, next, (int)Math.Ceiling(UpNextLead.TotalSeconds)).ConfigureAwait(false);
    }

    private async Task NotifyUpNextAsync(ClanPlaybackSession state, QueuedPlay next, int secondsRemaining)
    {
        if (state.NotifyClient is null || state.NotifyChannelId is null)
        {
            return;
        }

        try
        {
            var channel = await state.ResolveNotifyChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                return;
            }
            await channel.SendAsync(PlayerMessageBuilder.UpNext(next.Track, secondsRemaining, next.Target.ChannelLabel))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify up-next for {Title}", next.Track.Title);
        }
    }

    private async Task NotifyPlaybackFailureAsync(ClanPlaybackSession state, TrackInfoEntity track, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        if (state.NotifyClient is null || state.NotifyChannelId is null)
        {
            return;
        }

        try
        {
            var channel = await state.ResolveNotifyChannelAsync().ConfigureAwait(false);
            if (channel is null)
            {
                return;
            }
            var content = ex is AudioTooLargeException
                ? PlayerMessageBuilder.CopyrightBlocked()
                : PlayerMessageBuilder.FromMediaFailure(ex)
                    ?? PlayerMessageBuilder.FromSfuFailure(ex)
                    ?? PlayerMessageBuilder.Awkward();
            await channel.SendAsync(content).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(notifyEx, "Failed to notify playback error for {Title}", track.Title);
        }
    }

    private static bool IsMediaPrepFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is MediaPrepException or AudioTooLargeException)
            {
                return true;
            }

            var msg = cur.Message ?? string.Empty;
            if (msg.Contains("CDN upload failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("download returned no file", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("requires .ogg/.opus", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Playable CDN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSfuInfrastructureFailure(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("SFU", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("SFU publisher", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("session capacity", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long?> TryPreferredStreamChannelIdAsync(
        ICommandContext ctx,
        CancellationToken cancellationToken)
    {
        if (ctx.Channel.Type == (int)ChannelType.Streaming)
        {
            return ctx.Channel.Id;
        }

        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        return await _binds.TryGetDefaultStreamChannelAsync(clanId, cancellationToken).ConfigureAwait(false);
    }

    private static Mezon.Net.Client.MessageContent AmbiguousChannelMessage()
        => PlayerMessageBuilder.Error(
            "Which stream?",
            "This clan has more than one stream playing. Run the command in that #stream channel, or tag it.");
}
