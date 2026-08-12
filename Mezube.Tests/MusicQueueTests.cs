using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Playback;

namespace Mezube.Tests;

public sealed class MusicQueueTests
{
    private static QueuedPlay Play(string title, bool fromDefault = false)
        => new(
            new TrackInfoEntity { Title = title, MediaUrl = "https://example.com/" + title },
            new PlaybackTarget(1, 2, ChannelLabel: "ch"),
            IsFromDefault: fromDefault);

    [Fact]
    public void EnqueueFront_puts_item_ahead_of_pending()
    {
        var q = new MusicQueue();
        q.Enqueue(Play("a"));
        q.Enqueue(Play("b"));
        q.EnqueueFront(Play("x"));

        Assert.Equal("x", q.PeekNext()!.Track.Title);
        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void TryDequeueNext_sets_current_and_total_count()
    {
        var q = new MusicQueue();
        q.Enqueue(Play("a"));
        q.Enqueue(Play("b"));

        var first = q.TryDequeueNext();
        Assert.Equal("a", first!.Track.Title);
        Assert.Equal("a", q.CurrentItem!.Track.Title);
        Assert.Equal(2, q.TotalCount); // current + 1 pending
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void TryRemovePending_removes_first_match_only()
    {
        var q = new MusicQueue();
        q.Enqueue(Play("a"));
        q.Enqueue(Play("b"));
        q.Enqueue(Play("a"));

        Assert.True(q.TryRemovePending(p => p.Track.Title == "a"));
        Assert.Equal(["b", "a"], q.Snapshot().Select(p => p.Track.Title).ToArray());
    }
}
