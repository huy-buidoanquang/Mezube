using Mezube.Bot;
using Mezube.Music;
using Mezube.Sfu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class PauseResetAndTimeoutTests
{
    [Fact]
    public async Task BeginStream_starts_the_next_track_unpaused()
    {
        using var http = new HttpClient();
        var session = new SfuPublisherSession(new BotOptions(), http, NullLogger.Instance, 42);
        await session.PauseAsync(true, CancellationToken.None);
        Assert.True(session.IsPaused);

        session.BeginStream("next", new SfuMediaSource(null, Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ogg")));

        Assert.False(session.IsPaused);
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(1, 1, true, true)]
    [InlineData(1, 1, false, false)]
    [InlineData(1, 2, true, false)]
    public void ShouldStopPausedSession_stops_only_the_same_session_still_paused(
        int scheduledGeneration,
        int currentGeneration,
        bool stillPaused,
        bool expected)
    {
        Assert.Equal(expected, MusicPlayer.ShouldStopPausedSession(scheduledGeneration, currentGeneration, stillPaused));
    }
}
