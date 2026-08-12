namespace Mezube.Infrastructure.Persistence.Redis;

public static class RedisKeyNames
{
    public const string Prefix = "mezon:music:";
    public static readonly TimeSpan PlayerTtl = TimeSpan.FromHours(4);
    public static readonly TimeSpan VoiceTtl = TimeSpan.FromHours(24);

    public static string Player(long clanId) => $"{Prefix}player:{clanId}";
    public static string Queue(long clanId) => $"{Prefix}queue:{clanId}";
    public static string Voice(long clanId) => $"{Prefix}voice:{clanId}";
    public static string VoteSkip(long clanId, long playHistoryId) => $"{Prefix}voteskip:{clanId}:{playHistoryId}";
    /// <summary>SET of clan ids with an active player/queue (avoids KEYS scans on restore).</summary>
    public static string ActiveClans => $"{Prefix}active_clans";
}
