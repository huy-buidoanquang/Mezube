namespace Mezube.Stn;

/// <summary>
/// STN CDN ingest: <c>.ogg</c>/<c>.opus</c> (Opus audio) and <c>.webm</c> (Opus+VP8/VP9).
/// </summary>
public static class StnMediaUrl
{
    public static bool IsSupportedOpusSourceUrl(string? rawUrl)
        => Helpers.PlayableUrlHelper.IsPreparedAudioUrl(rawUrl);

    public static bool IsSupportedStreamingSourceUrl(string? rawUrl)
        => Helpers.PlayableUrlHelper.IsPreparedStreamingUrl(rawUrl);
}
