using Mezube.Music;

namespace Mezube.Tests;

public sealed class CrossClanStreamTargetTests
{
    [Fact]
    public void OtherClanChannelError_same_clan_returns_null()
        => Assert.Null(MusicPlayer.OtherClanChannelError(channelClanId: 42, clanId: 42));

    [Fact]
    public void OtherClanChannelError_other_clan_returns_error()
        => Assert.NotNull(MusicPlayer.OtherClanChannelError(channelClanId: 7, clanId: 42));

    [Fact]
    public void OtherClanChannelError_clanless_command_rejects_clan_channel()
        => Assert.NotNull(MusicPlayer.OtherClanChannelError(channelClanId: 42, clanId: 0));
}
