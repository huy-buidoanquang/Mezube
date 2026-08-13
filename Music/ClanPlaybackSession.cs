using Mezon.Net.Sdk;
using Mezube.Playback;

namespace Mezube.Music;

/// <summary>Per-clan playback session state (queue, pump gate, UI handles, idle timer).</summary>
public sealed class ClanPlaybackSession : IDisposable
{
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();
    private CancellationTokenSource? _trackCts;
    private CancellationTokenSource? _idleCts;
    private int _generation;

    public MusicQueue Queue { get; } = new();
    public PlaybackTarget? Target { get; set; }
    public PlaybackMode Mode { get; set; } = PlaybackMode.Streaming;
    public bool IsPlaying { get; set; }
    public bool PumpRunning { get; set; }
    public long? ClanId { get; set; }
    public long? ControlMessageId { get; set; }
    public uint? ControlMessageCreateTimeSeconds { get; set; }
    /// <summary>True when the control message was created by !np (keeps Skip/Stop on updates).</summary>
    public bool ControlMessageHasButtons { get; set; }
    public long? ControlUserId { get; set; }
    public MezonClient? NotifyClient { get; set; }
    public long? NotifyChannelId { get; set; }
    public Mezon.Net.Sdk.Entities.Channel? NotifyChannel { get; set; }
    public PlayerDestroyReason LastDestroyReason { get; set; }
    public bool HoldsPlaySlot { get; set; }
    public long? PlayHistoryId { get; set; }
    public CancellationTokenSource? PrepCts { get; set; }
    /// <summary>True while autoplaying the clan default playlist (immediate refill on empty).</summary>
    public bool PlayingDefaultPlaylist { get; set; }
    /// <summary>When false (after !stop / default none), idle TTL must not resume default autoplay.</summary>
    public bool DefaultAutoplayArmed { get; set; }
    public int DefaultPlaylistCursor { get; set; }
    public long? CachedDefaultPlaylistId { get; set; }
    public IReadOnlyList<Domain.Entities.PlaylistItemEntity>? CachedDefaultPlaylistItems { get; set; }
    public long PendingSeekMs { get; set; }
    public bool ReplayCurrent { get; set; }
    public CancellationToken SessionToken => _sessionCts.Token;
    public int Generation => Volatile.Read(ref _generation);

    public int BumpGeneration() => Interlocked.Increment(ref _generation);

    public async Task<Mezon.Net.Sdk.Entities.Channel?> ResolveNotifyChannelAsync(
        CancellationToken cancellationToken = default)
    {
        if (NotifyClient is null || NotifyChannelId is not long channelId)
        {
            return null;
        }

        if (NotifyChannel is not null && NotifyChannel.Id == channelId)
        {
            return NotifyChannel;
        }

        NotifyChannel = await NotifyClient.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        return NotifyChannel;
    }

    public async Task<T> WithStartLockAsync<T>(Func<Task<T>> action)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task WithStartLockAsync(Func<Task> action)
        => await WithStartLockAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);

    public async Task<bool> TryEnterPumpAsync()
    {
        if (!await _pumpGate.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        PumpRunning = true;
        return true;
    }

    public void ExitPump()
    {
        PumpRunning = false;
        _pumpGate.Release();
    }

    public void SetTrackCts(CancellationTokenSource cts) => _trackCts = cts;

    public void ClearTrackCts() => _trackCts = null;

    public void CancelTrack()
    {
        try
        {
            _trackCts?.Cancel();
        }
        catch
        {
            // ignored
        }
    }

    public void CancelSession()
    {
        try
        {
            _sessionCts.Cancel();
        }
        catch
        {
        }

        CancelTrack();
        CancelIdleDestroy();
    }

    public void CancelIdleDestroy()
    {
        try
        {
            _idleCts?.Cancel();
        }
        catch
        {
        }

        _idleCts?.Dispose();
        _idleCts = null;
    }

    public void ScheduleIdleDestroy(TimeSpan delay, int generation, Action<int> destroy)
    {
        CancelIdleDestroy();
        var cts = new CancellationTokenSource();
        _idleCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                destroy(generation);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose()
    {
        CancelSession();
        _sessionCts.Dispose();
        PrepCts?.Dispose();
        _pumpGate.Dispose();
        _startGate.Dispose();
    }
}
