using System.Collections.Concurrent;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Media;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

/// <summary>
/// Background/cache-aware track preparation with a global concurrency gate and in-flight dedupe.
/// </summary>
public sealed class TrackPrepService
{
    private readonly PlayableMediaProcessor _processor;
    private readonly ILogger<TrackPrepService> _logger;
    private readonly MediaConcurrencyGate _gate;
    /// <summary>
    /// Lazy wraps the prep Task so ConcurrentDictionary.GetOrAdd cannot start ProcessTrackAsync
    /// twice under race (valueFactory may run more than once; Lazy.Value runs once).
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<TrackInfoEntity>>> _inflight =
        new(StringComparer.Ordinal);

    public TrackPrepService(
        PlayableMediaProcessor processor,
        MediaConcurrencyGate gate,
        ILogger<TrackPrepService> logger)
    {
        _processor = processor;
        _logger = logger;
        _gate = gate;
    }

    public Task<TrackInfoEntity> EnsurePreparedAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
        => EnsurePreparedAsync(client, track, PreparedAssetKind.Audio, cancellationToken);

    public Task<TrackInfoEntity> EnsurePreparedAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(kind, track);
        if (key is null)
        {
            return RunGatedUngatedKeyAsync(client, track, kind, cancellationToken);
        }

        var lazy = _inflight.GetOrAdd(
            key,
            static (k, state) => new Lazy<Task<TrackInfoEntity>>(
                () => state.self.RunGatedAsync(state.client, state.track, state.kind, k),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (self: this, client, track, kind));

        return AwaitSharedAsync(lazy.Value, cancellationToken);
    }

    public void StartBackgroundPrep(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken,
        Action<Exception>? onError = null)
        => StartBackgroundPrep(client, track, PreparedAssetKind.Audio, cancellationToken, onError);

    public void StartBackgroundPrep(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken,
        Action<Exception>? onError = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsurePreparedAsync(client, track, kind, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background prep failed for {Title} kind={Kind}", track.Title, kind);
                onError?.Invoke(ex);
            }
        }, CancellationToken.None);
    }

    private async Task<TrackInfoEntity> RunGatedAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        string key)
    {
        // Shared work must not use a per-caller CT: CancelTrack / PrepCts cancel would abort
        // shared work for every waiter. Callers still observe cancel via AwaitSharedAsync.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await _processor.ProcessTrackAsync(client, track, kind, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<TrackInfoEntity> RunGatedUngatedKeyAsync(
        MezonClient client,
        TrackInfoEntity track,
        PreparedAssetKind kind,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _processor.ProcessTrackAsync(client, track, kind, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<TrackInfoEntity> AwaitSharedAsync(
        Task<TrackInfoEntity> task,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancel);
        var completed = await Task.WhenAny(task, cancel.Task).ConfigureAwait(false);
        if (completed != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await task.ConfigureAwait(false);
    }

    private static string? BuildKey(PreparedAssetKind kind, TrackInfoEntity track)
    {
        var id = BuildKey(track);
        return id is null ? null : $"{kind}:{id}";
    }

    private static string? BuildKey(TrackInfoEntity track)
    {
        if (!string.IsNullOrWhiteSpace(track.ExternalId) && !string.IsNullOrWhiteSpace(track.Source)
            && track.Source is not "unknown")
        {
            return $"{track.Source}:{track.ExternalId}";
        }

        if (TrackIdentityHelper.TryParseYoutubeId(track.WebpageUrl ?? track.MediaUrl, out var ytId))
        {
            return $"{TrackIdentityHelper.SourceYoutube}:{ytId}";
        }

        if (string.Equals(track.Source, TrackIdentityHelper.SourceUrl, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(track.MediaUrl))
        {
            return $"{TrackIdentityHelper.SourceUrl}:{TrackIdentityHelper.ForDirectUrl(track.MediaUrl)}";
        }

        var url = track.WebpageUrl ?? track.MediaUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            return $"fallback:{TrackIdentityHelper.ForDirectUrl(url)}";
        }

        return null;
    }
}
