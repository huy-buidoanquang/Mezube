using Mezube.Ui;

namespace Mezube.Tests;

public sealed class PlayerMessageBuilderHelpTests
{
    [Fact]
    public void Member_help_omits_admin_stop_and_playlist_default()
    {
        var fields = PlayerMessageBuilder.HelpFields("!", isDjOrOwner: false, isOwner: false);
        var names = fields.Select(f => f.Name).ToArray();

        Assert.Equal(["Play", "Controls", "Playlist", "Info"], names);
        Assert.DoesNotContain(fields, f => f.Name == "Admin");

        var play = fields.Single(f => f.Name == "Play").Value;
        Assert.Contains("play <query>", play);
        Assert.Contains("-v", play);

        var controls = fields.Single(f => f.Name == "Controls").Value;
        Assert.Contains("voteskip", controls);
        Assert.Contains("your track only", controls);
        Assert.DoesNotContain("stop", controls);
        Assert.DoesNotContain("seek", controls);

        var playlist = fields.Single(f => f.Name == "Playlist").Value;
        Assert.DoesNotContain("default", playlist);
        Assert.DoesNotContain("spotify", string.Join('\n', fields.Select(f => f.Value)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dj_help_includes_stop_and_playlist_default_but_not_admin()
    {
        var fields = PlayerMessageBuilder.HelpFields("!", isDjOrOwner: true, isOwner: false);
        Assert.DoesNotContain(fields, f => f.Name == "Admin");

        var controls = fields.Single(f => f.Name == "Controls").Value;
        Assert.Contains("stop", controls);
        Assert.DoesNotContain("your track only", controls);
        Assert.DoesNotContain("seek", controls);

        var playlist = fields.Single(f => f.Name == "Playlist").Value;
        Assert.Contains("default", playlist);
    }

    [Fact]
    public void Owner_help_includes_admin()
    {
        var fields = PlayerMessageBuilder.HelpFields("!", isDjOrOwner: true, isOwner: true);
        var admin = fields.Single(f => f.Name == "Admin").Value;
        Assert.Contains("setdj", admin);
        Assert.Contains("musicchannel", admin);
    }

    [Fact]
    public void PlayUsage_covers_youtube_and_soundcloud_without_backticks_or_spotify()
    {
        var fields = PlayerMessageBuilder.PlayUsageFields("!");
        Assert.Equal(["YouTube", "SoundCloud", "Channel"], fields.Select(f => f.Name).ToArray());

        var body = string.Join('\n', fields.Select(f => f.Value));
        Assert.Contains("never gonna give you up", body);
        Assert.Contains("soundcloud.com", body);
        Assert.Contains("· !play", body);
        Assert.Contains("#stream", body);
        Assert.Contains("-v", body);
        Assert.DoesNotContain("#voice", body);
        Assert.DoesNotContain('`', body);
        Assert.DoesNotContain("spotify", body, StringComparison.OrdinalIgnoreCase);
    }
}
