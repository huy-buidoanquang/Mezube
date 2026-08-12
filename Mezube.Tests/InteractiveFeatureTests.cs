using Mezube.Helpers;
using Mezube.Music.Interactive;
using Mezube.Ui;

namespace Mezube.Tests;

public sealed class InteractiveFeatureTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLtest123", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLtest123", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://soundcloud.com/artist/sets/mix", true)]
    [InlineData("https://on.soundcloud.com/AbCd", true)]
    [InlineData("https://soundcloud.com/artist/track", false)]
    public void IsExternalPlaylistUrl(string url, bool expected)
        => Assert.Equal(expected, TrackIdentityHelper.IsExternalPlaylistUrl(url));

    [Theory]
    [InlineData("""["youtube:abc","soundcloud:1"]""", 2)]
    [InlineData("""{"values":["youtube:abc"]}""", 1)]
    [InlineData("""{"mezube_radio_search":{"value":"youtube:xyz"}}""", 1)]
    [InlineData("youtube:plain", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseSelectedValues(string? extra, int count)
        => Assert.Equal(count, InteractionExtraData.ParseSelectedValues(extra).Count);

    [Fact]
    public void TrackCandidate_token_roundtrip()
    {
        var c = new TrackCandidate
        {
            Source = TrackIdentityHelper.SourceYoutube,
            ExternalId = "dQw4w9WgXcQ",
            Title = "Rick",
            WebpageUrl = "https://youtu.be/dQw4w9WgXcQ",
        };
        Assert.Equal("youtube:dQw4w9WgXcQ", c.Token);
        Assert.True(TrackCandidate.TryParseToken(c.Token, out var source, out var id));
        Assert.Equal(TrackIdentityHelper.SourceYoutube, source);
        Assert.Equal("dQw4w9WgXcQ", id);
    }

    [Fact]
    public void PlaylistImportSession_pages_by_five()
    {
        var session = new PlaylistImportSession
        {
            Candidates = Enumerable.Range(0, 12)
                .Select(i => new TrackCandidate
                {
                    Source = "youtube",
                    ExternalId = i.ToString(),
                    Title = $"t{i}",
                    WebpageUrl = $"https://youtu.be/{i}",
                })
                .ToList(),
            Page = 0,
        };

        Assert.Equal(3, session.PageCount);
        Assert.Equal(5, session.PageCandidates().Count);
        session.Page = 2;
        Assert.Equal(2, session.PageCandidates().Count);
    }

    [Fact]
    public void SearchPick_help_mentions_picker()
    {
        var play = PlayerMessageBuilder.HelpFields("!", false, false).Single(f => f.Name == "Play").Value;
        Assert.Contains("picker", play, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MezubeButtonId_search_and_import_prefixes()
    {
        Assert.Equal("12*", MezubeButtonId.SearchPickPrefix);
        Assert.Equal("13*", MezubeButtonId.PlaylistImportPrefix);
        var id = MezubeButtonId.CreateSearchPick(1, 2, MezubeButtonId.ActionSubmit);
        Assert.True(MezubeButtonId.TryParse(id, out var parts));
        Assert.Equal(MezubeButtonId.SearchPick, parts.InteractionFunction);
        Assert.Equal(MezubeButtonId.ActionSubmit, parts.Action);
    }
}
