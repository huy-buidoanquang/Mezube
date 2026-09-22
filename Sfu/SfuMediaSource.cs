using Mezube.Domain.Entities;

namespace Mezube.Sfu;

/// <summary>
/// Prepared SFU audio: a local Ogg/Opus file (first play / in-process cache) or an HTTP CDN URL.
/// </summary>
internal readonly record struct SfuMediaSource(Uri? HttpUri, string? LocalPath)
{
    public bool IsLocal => !string.IsNullOrWhiteSpace(LocalPath);

    public static bool TryParse(TrackInfoEntity track, out SfuMediaSource source)
    {
        if (TryParseLocal(track.LocalMediaPath, out source))
        {
            return true;
        }

        return TryParse(track.MediaUrl, out source);
    }

    public static bool TryParse(string? media, out SfuMediaSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(media))
        {
            return false;
        }

        if (TryParseLocal(media, out source))
        {
            return true;
        }

        if (!Uri.TryCreate(media, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !HasOggOpusExtension(uri.AbsolutePath))
        {
            return false;
        }

        source = new SfuMediaSource(uri, LocalPath: null);
        return true;
    }

    private static bool TryParseLocal(string? path, out SfuMediaSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(path)
            || !HasOggOpusExtension(path)
            || !File.Exists(path))
        {
            return false;
        }

        source = new SfuMediaSource(HttpUri: null, path);
        return true;
    }

    private static bool HasOggOpusExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
    }
}
