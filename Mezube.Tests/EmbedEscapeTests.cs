using System.Text.Json;
using Mezube.Domain.Entities;
using Mezube.Ui;

namespace Mezube.Tests;

public sealed class EmbedEscapeTests
{
    private const string Title = """Artist - "Song" \ Live""";

    [Fact]
    public void NowPlaying_keeps_quotes_and_backslashes_in_title_and_fields()
    {
        var track = new TrackInfoEntity
        {
            Title = Title,
            MediaUrl = "https://example.com/a.ogg",
            RequestedBy = """DJ "Q" \o/""",
        };

        var embed = FirstEmbed(PlayerMessageBuilder.NowPlaying(track, 0, "streaming", nextTitle: Title).ToJson());

        Assert.Equal(Title, embed.GetProperty("description").GetString());
        Assert.Equal(Title, FieldValue(embed, "Next up"));
        Assert.Equal("""DJ "Q" \o/""", FieldValue(embed, "Requested by"));
    }

    [Fact]
    public void PlaylistTracks_keeps_quotes_and_backslashes_in_names()
    {
        var content = PlayerMessageBuilder.PlaylistTracks(Title, false, [(Title, "3:00", false)], 1);

        var embed = FirstEmbed(content.ToJson());

        Assert.Equal($"{Title} · 1 track(s)", FieldValue(embed, "About"));
        Assert.Equal($"{Title} (3:00)", FieldValue(embed, "#1"));
    }

    private static JsonElement FirstEmbed(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("embed")[0].Clone();

    private static string? FieldValue(JsonElement embed, string name)
        => embed.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == name)
            .GetProperty("value").GetString();
}
