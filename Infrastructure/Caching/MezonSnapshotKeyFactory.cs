using Mezon.Net.Sdk.Caching;
using Mezube.Bot;

namespace Mezube.Infrastructure.Caching;

/// <summary>Builds Sdk <see cref="CacheKey"/> values for Mezube L2 entity snapshots.</summary>
public sealed class MezonSnapshotKeyFactory
{
    public const string EntityClan = "clan";
    public const string EntityChannel = "channel";
    public const string EntityRole = "role";
    public const string EntityUser = "user";

    private readonly string _environment;
    private readonly long _accountId;

    public MezonSnapshotKeyFactory(BotOptions options)
    {
        _accountId = options.BotId;
        _environment = SanitizeEnv(
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "prod");
    }

    public string EnvironmentName => _environment;

    public long AccountId => _accountId;

    public CacheKey Clan(long clanId) => new(_environment, _accountId, EntityClan, clanId.ToString());

    public CacheKey Channel(long channelId) => new(_environment, _accountId, EntityChannel, channelId.ToString());

    public CacheKey Role(long roleId) => new(_environment, _accountId, EntityRole, roleId.ToString());

    public CacheKey User(long userId) => new(_environment, _accountId, EntityUser, userId.ToString());

    private static string SanitizeEnv(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? "prod" : trimmed.Replace(':', '-');
    }
}
