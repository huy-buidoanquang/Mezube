namespace Mezube.Helpers;

/// <summary>
/// Prepared STN-ready CDN assets: <c>.ogg</c>/<c>.opus</c> audio, or <c>.webm</c> video.
/// Never a YouTube/SoundCloud webpage URL.
/// </summary>
public static class PlayableUrlHelper
{
    public static bool IsPreparedAudioUrl(string? rawUrl)
        => HasPathExtension(rawUrl, ".ogg", ".opus");

    public static bool IsPreparedVideoUrl(string? rawUrl)
        => HasPathExtension(rawUrl, ".webm");

    public static bool IsPreparedStreamingUrl(string? rawUrl)
        => IsPreparedAudioUrl(rawUrl) || IsPreparedVideoUrl(rawUrl);

    /// <summary>Any STN-passthrough asset (audio or video).</summary>
    public static bool IsPreparedPlayableUrl(string? rawUrl)
        => IsPreparedStreamingUrl(rawUrl);

    public static string? NullIfNotPrepared(string? rawUrl)
        => IsPreparedPlayableUrl(rawUrl) ? rawUrl : null;

    public static string? NullIfNotAudio(string? rawUrl)
        => IsPreparedAudioUrl(rawUrl) ? rawUrl : null;

    public static string? NullIfNotVideo(string? rawUrl)
        => IsPreparedVideoUrl(rawUrl) ? rawUrl : null;

    private static bool HasPathExtension(string? rawUrl, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        foreach (var ext in extensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
