using Mezube.Helpers;
using Mezube.Media;
using System.Text.Json;

namespace Mezube.Tests;

public sealed class YoutubeSizeAudioOnlyTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("lofi hip hop mix", "ytsearch1:lofi hip hop mix")]
    public void ResolveTrackArgs_selects_download_audio_format(string query, string expectedInput)
    {
        var args = YtDlpProcessor.ResolveTrackArgs(query);

        var f = Array.IndexOf(args, "-f");
        Assert.True(f >= 0);
        Assert.Equal(YtDlpProcessor.AudioFormat, args[f + 1]);
        Assert.Contains("-J", args);
        Assert.Equal(expectedInput, args[^1]);
    }

    [Fact]
    public void ParseTrackElement_reads_selected_audio_size_not_video_formats()
    {
        const string json =
            """
            {
              "id": "abc123def45",
              "title": "4K music video",
              "webpage_url": "https://www.youtube.com/watch?v=abc123def45",
              "duration": 240,
              "format_id": "251",
              "acodec": "opus",
              "vcodec": "none",
              "filesize": 4200000,
              "formats": [
                { "format_id": "251", "acodec": "opus", "vcodec": "none", "filesize": 4200000 },
                { "format_id": "313", "acodec": "none", "vcodec": "vp9", "filesize": 1900000000 }
              ],
              "requested_downloads": [ { "format_id": "251", "filesize": 4200000 } ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var track = YtDlpProcessor.ParseTrackElement(
            doc.RootElement,
            "https://www.youtube.com/watch?v=abc123def45",
            "u",
            TrackIdentityHelper.SourceYoutube);

        Assert.NotNull(track);
        Assert.Equal(4200000, track.SourceBytes);
        Assert.Equal("abc123def45", track.ExternalId);
    }
}
