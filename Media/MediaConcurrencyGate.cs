using Mezube.Bot;

namespace Mezube.Media;

/// <summary>Shared semaphore for yt-dlp metadata + download/ffmpeg prep.</summary>
public sealed class MediaConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _gate;

    public MediaConcurrencyGate(BotOptions options)
    {
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxPrepConcurrency));
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
        => _gate.WaitAsync(cancellationToken);

    public void Release() => _gate.Release();

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
