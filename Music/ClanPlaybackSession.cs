using Mezon.Net.Sdk;
using Mezube.Playback;

namespace Mezube.Music;

/// <summary>Per-clan playback session state (queue, pump gate, UI handles, idle timer).</summary>
public sealed class ClanPlaybackSession
{
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private CancellationTokenSource? _trackCts;
    private CancellationTokenSource? _idleCts;

    public MusicQueue Queue { get; } = new();
    public PlaybackTarget? Target { get; set; }
    public PlaybackMode Mode { get; set; } = PlaybackMode.Streaming;
    public bool IsPlaying { get; set; }
    public long? ClanId { get; set; }
    public long? ControlMessageId { get; set; }
    public uint? ControlMessageCreateTimeSeconds { get; set; }
    /// <summary>True when the control message was created by !np (keeps Skip/Stop on updates).</summary>
    public bool ControlMessageHasButtons { get; set; }
    public long? ControlUserId { get; set; }
    public MezonClient? NotifyClient { get; set; }
    public long? NotifyChannelId { get; set; }
    public PlayerDestroyReason LastDestroyReason { get; set; }
    public bool HoldsPlaySlot { get; set; }
    public long? PlayHistoryId { get; set; }
    public CancellationTokenSource? PrepCts { get; set; }
    /// <summary>True while autoplaying the clan default playlist (immediate refill on empty).</summary>
    public bool PlayingDefaultPlaylist { get; set; }
    /// <summary>When false (after !stop / default none), idle TTL must not resume default autoplay.</summary>
    public bool DefaultAutoplayArmed { get; set; }
    public int DefaultPlaylistCursor { get; set; }

    public async Task<bool> TryEnterPumpAsync()
    {
        if (!await _pumpGate.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    public void ExitPump() => _pumpGate.Release();

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

    public void ScheduleIdleDestroy(TimeSpan delay, Action destroy)
    {
        CancelIdleDestroy();
        var cts = new CancellationTokenSource();
        _idleCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                destroy();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
