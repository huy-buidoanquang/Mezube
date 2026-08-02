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
    private readonly SemaphoreSlim _gate;
    /// <summary>
    /// Lazy wraps the prep Task so ConcurrentDictionary.GetOrAdd cannot start ProcessTrackAsync
    /// twice under race (valueFactory may run more than once; Lazy.Value runs once).
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<TrackInfoEntity>>> _inflight =
        new(StringComparer.Ordinal);

    public TrackPrepService(
        PlayableMediaProcessor processor,
        BotOptions options,
        ILogger<TrackPrepService> logger)
    {
        _processor = processor;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxPrepConcurrency));
    }

    public Task<TrackInfoEntity> EnsurePreparedAsync(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(track);
        if (key is null)
        {
            // No stable identity — cannot safely share; still gated to limit blast radius.
            return RunGatedUngatedKeyAsync(client, track, cancellationToken);
        }

        // GetOrAdd may invoke this factory more than once under contention; creating Lazy is cheap.
        // Only the Lazy that wins the dictionary slot has .Value read — that runs prep exactly once.
        var lazy = _inflight.GetOrAdd(
            key,
            static (k, state) => new Lazy<Task<TrackInfoEntity>>(
                () => state.self.RunGatedAsync(state.client, state.track, k),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (self: this, client, track));

        return AwaitSharedAsync(lazy.Value, cancellationToken);
    }

    /// <summary>Fire-and-forget prep for a queued item; errors are logged by the caller continuation.</summary>
    public void StartBackgroundPrep(
        MezonClient client,
        TrackInfoEntity track,
        CancellationToken cancellationToken,
        Action<Exception>? onError = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsurePreparedAsync(client, track, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // stop/clear — waiter cancelled; shared prep (if any) may still finish.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background prep failed for {Title}", track.Title);
                onError?.Invoke(ex);
            }
        }, CancellationToken.None);
    }

    private async Task<TrackInfoEntity> RunGatedAsync(
        MezonClient client,
        TrackInfoEntity track,
        string key)
    {
        // Shared work must not use a per-caller CT: CancelTrack / PrepCts cancel would abort
        // Client work for every waiter, and racing factories used to capture different tokens.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await _processor.ProcessTrackAsync(client, track, CancellationToken.None)
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
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _processor.ProcessTrackAsync(client, track, cancellationToken)
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

        // Last-resort share key so background + Play still dedupe when ExternalId is missing.
        var url = track.WebpageUrl ?? track.MediaUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            return $"fallback:{TrackIdentityHelper.ForDirectUrl(url)}";
        }

        return null;
    }
}
