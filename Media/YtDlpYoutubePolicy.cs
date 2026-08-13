namespace Mezube.Media;

/// <summary>
/// YouTube googlevideo 403 / n-sig mitigations for yt-dlp
/// (<c>player_client</c> rotation + transient-error detection).
/// </summary>
public static class YtDlpYoutubePolicy
{
    public const string DefaultPlayerClients = "android,web,mweb";

    public static readonly string[] FallbackPlayerClients =
    [
        DefaultPlayerClients,
        "ios,tv,web_safari",
        "tv_embedded,web",
    ];

    public static string PlayerClientsForAttempt(int attempt, string? configuredPrimary = null)
    {
        var primary = string.IsNullOrWhiteSpace(configuredPrimary)
            ? FallbackPlayerClients[0]
            : configuredPrimary.Trim();

        if (attempt <= 0)
        {
            return primary;
        }

        var idx = Math.Min(attempt, FallbackPlayerClients.Length - 1);
        var fallback = FallbackPlayerClients[idx];
        return string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase)
            ? FallbackPlayerClients[Math.Min(idx + 1, FallbackPlayerClients.Length - 1)]
            : fallback;
    }

    public static bool IsYoutubeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (source.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return source.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
               || source.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
               || source.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase)
               || source.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTransientDownloadFailure(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return true;
        }

        return Contains(stderr, "HTTP Error 403")
               || Contains(stderr, "HTTP Error 429")
               || Contains(stderr, "Forbidden")
               || Contains(stderr, "unable to download video data")
               || Contains(stderr, "download returned no file")
               || Contains(stderr, "This content isn't available")
               || Contains(stderr, "try again later")
               || Contains(stderr, "nsig")
               || Contains(stderr, "SABR")
               || Contains(stderr, "Requested format is not available")
               || Contains(stderr, "Sign in to confirm");
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
