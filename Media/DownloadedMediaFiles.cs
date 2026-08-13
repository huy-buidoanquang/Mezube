namespace Mezube.Media;

public static class DownloadedMediaFiles
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".m4a", ".opus", ".ogg", ".mp3", ".mp4", ".mkv", ".aac", ".wav", ".flac", ".m4b",
    };

    public static bool IsJunkName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCompletedMedia(string path)
        => !IsJunkName(path) && MediaExtensions.Contains(Path.GetExtension(path));

    public static string? FindCompleted(string dir, string prefix)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, prefix + ".*")
            .Where(IsCompletedMedia)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static void DeletePrefixed(string dir, string prefix)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, prefix + ".*"))
        {
            TryDelete(file);
        }

        var bare = Path.Combine(dir, prefix);
        TryDelete(bare);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
