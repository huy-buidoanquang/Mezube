using Mezube.Domain.Entities;
using Mezube.Music;

namespace Mezube.Tests;

public sealed class PlaylistAddNameMatchTests
{
    private static readonly PlaylistEntity My = new() { Id = 1, Name = "My" };
    private static readonly PlaylistEntity MyMix = new() { Id = 2, Name = "My Mix" };
    private static readonly PlaylistEntity Chill = new() { Id = 3, Name = "chill" };

    [Fact]
    public void MatchLeadingPlaylistName_single_word_name_leaves_query()
    {
        var (playlist, count) = MusicPlayer.MatchLeadingPlaylistName(["Chill", "lofi", "beats"], [My, MyMix, Chill]);

        Assert.Same(Chill, playlist);
        Assert.Equal(1, count);
    }

    [Fact]
    public void MatchLeadingPlaylistName_multi_word_name_leaves_query()
    {
        var (playlist, count) = MusicPlayer.MatchLeadingPlaylistName(
            ["my", "mix", "https://youtu.be/x"],
            [MyMix, Chill]);

        Assert.Same(MyMix, playlist);
        Assert.Equal(2, count);
    }

    [Fact]
    public void MatchLeadingPlaylistName_prefers_longest_name()
    {
        var (playlist, count) = MusicPlayer.MatchLeadingPlaylistName(["My", "Mix", "song"], [My, MyMix]);

        Assert.Same(MyMix, playlist);
        Assert.Equal(2, count);
    }

    [Fact]
    public void MatchLeadingPlaylistName_keeps_last_arg_for_query()
    {
        var (playlist, count) = MusicPlayer.MatchLeadingPlaylistName(["My", "Mix"], [My, MyMix]);

        Assert.Same(My, playlist);
        Assert.Equal(1, count);
    }

    [Fact]
    public void MatchLeadingPlaylistName_no_match_returns_null()
    {
        var (playlist, count) = MusicPlayer.MatchLeadingPlaylistName(["Road", "Trip", "song"], [My, MyMix, Chill]);

        Assert.Null(playlist);
        Assert.Equal(0, count);
    }
}
