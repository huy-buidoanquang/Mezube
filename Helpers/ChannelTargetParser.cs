using Mezon.Net.Sdk.Commands;

namespace Mezube.Helpers;

/// <summary>
/// Resolves a target channel from Mezon hashtag tokens (<c>hg</c> in message content)
/// and strips leftover <c>#label</c> tokens from command args.
/// </summary>
public static class ChannelTargetParser
{
    public static long? TryGetHashtagChannelId(ICommandContext ctx)
    {
        var hashtags = ctx.Message.Content.Hashtags;
        if (hashtags is null || hashtags.Count == 0)
        {
            return null;
        }

        foreach (var tag in hashtags)
        {
            if (!string.IsNullOrWhiteSpace(tag.ChannelId)
                && long.TryParse(tag.ChannelId, out var id)
                && id != 0)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the music query from args, dropping tokens that look like channel hashtags.
    /// </summary>
    public static string BuildQuery(IEnumerable<string> args)
        => ParsePlayArgs(args).Query;

    /// <summary>
    /// Same as <see cref="BuildQuery"/> plus opt-in video via <c>-v</c> / <c>--video</c>.
    /// </summary>
    public static (string Query, bool WantVideo) ParsePlayArgs(IEnumerable<string> args)
    {
        var wantVideo = false;
        var parts = new List<string>();
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (LooksLikeHashtagToken(raw))
            {
                continue;
            }

            if (IsVideoFlag(raw))
            {
                wantVideo = true;
                continue;
            }

            parts.Add(raw);
        }

        return (string.Join(' ', parts).Trim(), wantVideo);
    }

    private static bool IsVideoFlag(string token)
        => token.Equals("-v", StringComparison.OrdinalIgnoreCase)
           || token.Equals("--video", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHashtagToken(string token)
    {
        if (token.StartsWith('#') && token.Length > 1)
        {
            return true;
        }

        // Some clients leave a bare channel label without '#'.
        return false;
    }
}
