namespace Mezube.Infrastructure.Persistence.Redis;

public static class RedisKeyNames
{
    public const string Prefix = "mezon:music:";
    public static readonly TimeSpan PlayerTtl = TimeSpan.FromHours(4);
    public static readonly TimeSpan VoiceTtl = TimeSpan.FromHours(24);

    public static string Player(long clanId, long channelId) => $"{Prefix}player:{clanId}:{channelId}";
    public static string Queue(long clanId, long channelId) => $"{Prefix}queue:{clanId}:{channelId}";
    public static string Voice(long clanId) => $"{Prefix}voice:{clanId}";
    public static string VoteSkip(long clanId, long playHistoryId) => $"{Prefix}voteskip:{clanId}:{playHistoryId}";
    /// <summary>SET of <c>clanId:channelId</c> with an active player/queue (avoids KEYS scans on restore).</summary>
    public static string ActiveSessions => $"{Prefix}active_sessions";
    /// <summary>Legacy SET of clan ids (pre per-channel sessions). Read on restore only.</summary>
    public static string ActiveClans => $"{Prefix}active_clans";
    public static string LegacyPlayer(long clanId) => $"{Prefix}player:{clanId}";
    public static string LegacyQueue(long clanId) => $"{Prefix}queue:{clanId}";

    public static readonly TimeSpan InteractiveSessionTtl = TimeSpan.FromMinutes(10);
    public static string SearchPick(long messageId) => $"{Prefix}pick:{messageId}";
    public static string PlaylistImport(long messageId) => $"{Prefix}plimport:{messageId}";
}
