using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Playback;

namespace Mezube.Tests;

public sealed class SkipTargetsCurrentTrackTests
{
    [Fact]
    public void IsSkipTarget_rejects_a_skip_aimed_at_a_track_that_already_ended()
    {
        var track = new TrackInfoEntity { Title = "Same song", MediaUrl = "https://cdn.example/song.ogg" };
        var target = new PlaybackTarget(1, 2);
        var first = new QueuedPlay(track, target);
        var second = new QueuedPlay(track, target);
        var queue = new MusicQueue();
        queue.Enqueue(first);
        queue.Enqueue(second);

        queue.TryDequeueNext();
        Assert.True(MusicPlayer.IsSkipTarget(queue, first));

        queue.TryDequeueNext();
        Assert.Equal(first, second);
        Assert.False(MusicPlayer.IsSkipTarget(queue, first));
        Assert.True(MusicPlayer.IsSkipTarget(queue, second));
    }

    [Theory]
    [InlineData(PlayerDestroyReason.Skip, PlayerDestroyReason.None)]
    [InlineData(PlayerDestroyReason.None, PlayerDestroyReason.None)]
    [InlineData(PlayerDestroyReason.UserStop, PlayerDestroyReason.UserStop)]
    public void ClearStaleSkip_drops_only_a_skip_left_from_the_previous_track(
        PlayerDestroyReason reason,
        PlayerDestroyReason expected)
    {
        Assert.Equal(expected, MusicPlayer.ClearStaleSkip(reason));
    }
}
