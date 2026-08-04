namespace Mezube.Helpers;

/// <summary>
/// <c>tracks.playable_url</c> must be a prepared STN-ready asset (Mezon CDN .ogg/.opus), never a source webpage.
/// </summary>
public static class PlayableUrlHelper
{
    public static bool IsPreparedPlayableUrl(string? rawUrl)
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
        return path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
    }

    public static string? NullIfNotPrepared(string? rawUrl)
        => IsPreparedPlayableUrl(rawUrl) ? rawUrl : null;
}
