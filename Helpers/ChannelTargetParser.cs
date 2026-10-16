using Mezon.Net.Sdk.Commands;
using System.Text.Json;

namespace Mezube.Helpers;

/// <summary>
/// Resolves a target channel from Mezon hashtag tokens (<c>hg</c> in message content)
/// and strips leftover <c>#label</c> tokens from command args.
/// </summary>
public static class ChannelTargetParser
{
    public static long? TryGetHashtagChannelId(ICommandContext ctx)
        => TryReadHashtagChannelId(ctx.Message.Content.RawJson);

    internal static long? TryReadHashtagChannelId(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("hg", out var hashtags)
                || hashtags.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var tag in hashtags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.Object
                    && tag.TryGetProperty("channelId", out var value)
                    && long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), out var id)
                    && id != 0)
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
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
