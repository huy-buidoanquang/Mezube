using Mezube.Domain.Entities;

namespace Mezube.Media;

/// <summary>
/// In-process cache of SFU-ready Ogg Opus files under <c>{TempDir}/prepared/</c>.
/// Survives the yt-dlp work-id cleanup so the publisher can play without a CDN round-trip.
/// </summary>
internal static class PreparedAudioCache
{
    public const string FolderName = "prepared";

    public static string? TryGetPath(string tempDir, TrackInfoEntity track)
    {
        if (string.IsNullOrWhiteSpace(track.Source)
            || track.Source is "unknown"
            || string.IsNullOrWhiteSpace(track.ExternalId))
        {
            return null;
        }

        return GetPath(tempDir, track.Source, track.ExternalId);
    }

    public static string GetPath(string tempDir, string source, string externalId)
    {
        var raw = $"{source}_{externalId}";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(raw.Select(ch =>
            ch <= ' ' || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray());
        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return Path.Combine(tempDir, FolderName, safe + ".normalized.ogg");
    }

    public static string NewFallbackPath(string tempDir)
        => Path.Combine(tempDir, FolderName, Guid.NewGuid().ToString("N") + ".normalized.ogg");
}
