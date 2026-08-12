using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mezube.Helpers;

public static class TrackIdentityHelper
{
    public const string SourceYoutube = "youtube";
    public const string SourceUrl = "url";
    public const string SourceSoundcloud = "soundcloud";

    private static readonly Regex YoutubeIdRegex = new(
        @"^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsSoundCloudHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.ToLowerInvariant();
        return h is "soundcloud.com" or "www.soundcloud.com" or "m.soundcloud.com"
               or "on.soundcloud.com"
               || h.EndsWith(".soundcloud.com", StringComparison.Ordinal);
    }

    public static bool IsSoundCloudUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && IsSoundCloudHost(uri.Host);
    }

    /// <summary>Short links (<c>on.soundcloud.com/…</c>) — resolve via playlist path (may be track or set).</summary>
    public static bool IsSoundCloudShortUrl(string? input)
    {
        if (!IsSoundCloudUrl(input) || !Uri.TryCreate(input!.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("on.soundcloud.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for SoundCloud playlist/set URLs (<c>/sets/…</c>).</summary>
    public static bool IsSoundCloudSetUrl(string? input)
    {
        if (!IsSoundCloudUrl(input) || !Uri.TryCreate(input!.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], "sets", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < segments.Length;
            }
        }

        return false;
    }

    /// <summary>
    /// YouTube playlist URL: <c>/playlist?list=</c> or any youtube URL with a playlist <c>list=</c> id.
    /// </summary>
    public static bool IsYoutubePlaylistUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("youtube.com", StringComparison.Ordinal)
            && !host.Contains("youtu.be", StringComparison.Ordinal)
            && !host.Contains("music.youtube.com", StringComparison.Ordinal))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        if (path.Contains("/playlist", StringComparison.Ordinal)
            && TryGetQueryValue(uri.Query, "list", out _))
        {
            return true;
        }

        return TryGetQueryValue(uri.Query, "list", out var listId) && !string.IsNullOrWhiteSpace(listId);
    }

    public static bool IsExternalPlaylistUrl(string? input)
        => IsSoundCloudSetUrl(input) || IsSoundCloudShortUrl(input) || IsYoutubePlaylistUrl(input);

    /// <summary>
    /// Rebuild a playable SoundCloud webpage URL from yt-dlp flat playlist fields
    /// (relative path, track id, or absolute URL).
    /// </summary>
    public static string? ResolveSoundCloudEntryUrl(string? webpageUrl, string? urlField, string? id = null)
    {
        if (TryAbsoluteHttp(webpageUrl, out var fromWeb))
        {
            return NormalizeAbsoluteUrl(fromWeb);
        }

        if (TryAbsoluteHttp(urlField, out var fromUrl))
        {
            return NormalizeAbsoluteUrl(fromUrl);
        }

        var relative = FirstNonEmpty(webpageUrl, urlField);
        if (!string.IsNullOrWhiteSpace(relative) && relative.Contains('/'))
        {
            return NormalizeAbsoluteUrl("https://soundcloud.com/" + relative.TrimStart('/'));
        }

        var trackId = FirstNonEmpty(id, relative);
        if (!string.IsNullOrWhiteSpace(trackId) && IsAllDigits(trackId))
        {
            return $"https://api.soundcloud.com/tracks/{trackId}";
        }

        return null;
    }

    private static bool TryAbsoluteHttp(string? value, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        absolute = value;
        return true;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private static bool IsAllDigits(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    public static bool TryParseYoutubeId(string? input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (YoutubeIdRegex.IsMatch(trimmed))
        {
            videoId = trimmed;
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be" or "www.youtu.be")
        {
            var id = uri.AbsolutePath.Trim('/');
            if (YoutubeIdRegex.IsMatch(id))
            {
                videoId = id;
                return true;
            }

            return false;
        }

        if (!host.Contains("youtube.com", StringComparison.Ordinal)
            && !host.Contains("youtube-nocookie.com", StringComparison.Ordinal)
            && !host.Contains("music.youtube.com", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryGetQueryValue(uri.Query, "v", out var v) && YoutubeIdRegex.IsMatch(v))
        {
            videoId = v;
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2
            && segments[0] is "embed" or "shorts" or "live" or "v"
            && YoutubeIdRegex.IsMatch(segments[1]))
        {
            videoId = segments[1];
            return true;
        }

        return false;
    }

    public static string ForDirectUrl(string absoluteUrl)
    {
        var normalized = NormalizeAbsoluteUrl(absoluteUrl);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeQueryAlias(string query)
    {
        var normalized = string.Join(
            ' ',
            query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "q:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeAbsoluteUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        builder.Query = FilterTrackingQuery(uri.Query);
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string FilterTrackingQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>();
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            if (key is "si" or "utm_source" or "utm_medium" or "utm_campaign" or "utm_term" or "utm_content"
                or "fbclid" or "gclid" or "feature" or "pp")
            {
                continue;
            }

            kept.Add(pair);
        }

        return kept.Count == 0 ? string.Empty : string.Join('&', kept);
    }

    private static bool TryGetQueryValue(string query, string name, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }
}
