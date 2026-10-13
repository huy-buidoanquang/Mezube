using Mezube.Music;

namespace Mezube.Tests;

public sealed class ShutdownKeepsCurrentTrackTests
{
    [Theory]
    [InlineData(PlayerDestroyReason.None, false, true)]
    [InlineData(PlayerDestroyReason.Skip, false, true)]
    [InlineData(PlayerDestroyReason.UserStop, false, true)]
    [InlineData(PlayerDestroyReason.TrackFailed, false, true)]
    [InlineData(PlayerDestroyReason.Seek, false, false)]
    [InlineData(PlayerDestroyReason.None, true, false)]
    [InlineData(PlayerDestroyReason.UserStop, true, false)]
    public void ShouldAdvanceAfterTrack_skips_history_and_queue_advance_on_shutdown(
        PlayerDestroyReason reason,
        bool shuttingDown,
        bool expected)
    {
        Assert.Equal(expected, MusicPlayer.ShouldAdvanceAfterTrack(reason, shuttingDown));
    }
}
