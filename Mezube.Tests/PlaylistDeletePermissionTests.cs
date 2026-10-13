using Mezube.Music;

namespace Mezube.Tests;

public sealed class PlaylistDeletePermissionTests
{
    [Fact]
    public void CanDeletePlaylist_creator_without_dj_is_allowed()
        => Assert.True(MusicPlayer.CanDeletePlaylist(createdBy: 10, userId: 10, isDjOrOwner: false));

    [Fact]
    public void CanDeletePlaylist_dj_or_owner_is_allowed_for_others_playlist()
        => Assert.True(MusicPlayer.CanDeletePlaylist(createdBy: 10, userId: 20, isDjOrOwner: true));

    [Fact]
    public void CanDeletePlaylist_other_member_is_rejected()
        => Assert.False(MusicPlayer.CanDeletePlaylist(createdBy: 10, userId: 20, isDjOrOwner: false));

    [Fact]
    public void CanDeletePlaylist_unknown_creator_requires_dj_or_owner()
    {
        Assert.False(MusicPlayer.CanDeletePlaylist(createdBy: null, userId: 20, isDjOrOwner: false));
        Assert.True(MusicPlayer.CanDeletePlaylist(createdBy: null, userId: 20, isDjOrOwner: true));
    }
}
