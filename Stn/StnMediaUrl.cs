namespace Mezube.Stn;

/// <summary>
/// Matches STN <c>IsSupportedOpusSourceURL</c>: absolute URL whose path ends with .ogg or .opus.
/// </summary>
public static class StnMediaUrl
{
    public static bool IsSupportedOpusSourceUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
    }
}
