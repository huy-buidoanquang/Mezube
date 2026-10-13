using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Music;
using Mezube.Playback;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class MediaGateScopeTests
{
    private static QueuedPlay Play(string title)
        => new(
            new TrackInfoEntity { Title = title, MediaUrl = "https://example.com/" + title },
            new PlaybackTarget(1, 2, ChannelLabel: "ch"));

    [Fact]
    public void IsNextUp_is_true_only_for_head_of_pending_queue()
    {
        var q = new MusicQueue();
        var a = Play("a");
        var b = Play("b");
        var c = Play("c");
        q.Enqueue(a);
        q.Enqueue(b);
        q.Enqueue(c);

        Assert.True(PlayEnqueueService.IsNextUp(q, a));
        Assert.False(PlayEnqueueService.IsNextUp(q, b));
        Assert.False(PlayEnqueueService.IsNextUp(q, c));

        var front = Play("front");
        q.EnqueueFront(front);
        Assert.True(PlayEnqueueService.IsNextUp(q, front));
        Assert.False(PlayEnqueueService.IsNextUp(q, a));

        q.TryDequeueNext();
        Assert.True(PlayEnqueueService.IsNextUp(q, a));
    }

    [Fact]
    public async Task EnsurePreparedAsync_does_not_wait_for_media_gate_when_media_is_ready()
    {
        var options = new BotOptions { CdnBaseUrl = "https://pub-test.r2.dev", MaxPrepConcurrency = 1 };
        var gate = new MediaConcurrencyGate(options);
        var uploader = new MezonCdnUploader(options, null!, NullLogger<MezonCdnUploader>.Instance);
        var pipeline = new PipelineProcessor(
            options,
            new YtDlpProcessor(options, NullLogger<YtDlpProcessor>.Instance),
            new FfmpegProcessor(options, NullLogger<FfmpegProcessor>.Instance),
            NullLogger<PipelineProcessor>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var processor = new PlayableMediaProcessor(
            options,
            pipeline,
            uploader,
            null!,
            cache,
            gate,
            NullLogger<PlayableMediaProcessor>.Instance);
        var prep = new TrackPrepService(processor, NullLogger<TrackPrepService>.Instance);
        var track = new TrackInfoEntity { Title = "ready", MediaUrl = "https://pub-test.r2.dev/a.normalized.ogg" };

        var hold = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(() => hold.Task);
        try
        {
            var prepared = prep.EnsurePreparedAsync(null!, track);
            var done = await Task.WhenAny(prepared, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(prepared, done);
            Assert.Equal(track.MediaUrl, (await prepared).MediaUrl);
        }
        finally
        {
            hold.SetResult(0);
            await holder;
        }
    }
}
