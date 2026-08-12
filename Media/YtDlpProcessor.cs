using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Helpers;

namespace Mezube.Media;

public sealed class YtDlpProcessor
{
    private readonly BotOptions _options;
    private readonly ILogger<YtDlpProcessor> _logger;

    public YtDlpProcessor(BotOptions options, ILogger<YtDlpProcessor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<TrackInfoEntity?> ResolveTrackAsync(string query, string? requestedBy, CancellationToken cancellationToken = default)
    {
        var input = LooksLikeUrl(query) ? query : $"ytsearch1:{query}";
        var json = await RunAsync(
            [
                "--no-playlist",
                "--no-warnings",
                "-J",
                "-f", "bestaudio/best",
                input,
            ],
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            root = entries.EnumerateArray().FirstOrDefault();
            if (root.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }
        }

        return ParseTrackElement(root, query, requestedBy, InferSource(query, root));
    }

    /// <summary>YouTube free-text search returning up to <paramref name="maxResults"/> entries (metadata only).</summary>
    public async Task<IReadOnlyList<TrackInfoEntity>> SearchTracksAsync(
        string query,
        string? requestedBy,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var n = Math.Clamp(maxResults, 1, 10);
        var input = $"ytsearch{n}:{query.Trim()}";
        var json = await RunAsync(
            [
                "--no-playlist",
                "--no-warnings",
                "-J",
                "-f", "bestaudio/best",
                input,
            ],
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<TrackInfoEntity>();
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (list.Count >= n)
                {
                    break;
                }

                if (entry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                var track = ParseTrackElement(entry, query, requestedBy, TrackIdentityHelper.SourceYoutube);
                if (track is not null)
                {
                    list.Add(track);
                }
            }

            return list;
        }

        var single = ParseTrackElement(root, query, requestedBy, TrackIdentityHelper.SourceYoutube);
        return single is null ? [] : [single];
    }

    /// <summary>Resolve a playlist/set into individual tracks (capped).</summary>
    public async Task<IReadOnlyList<TrackInfoEntity>> ResolvePlaylistAsync(
        string playlistUrl,
        string? requestedBy,
        int maxTracks,
        CancellationToken cancellationToken = default)
    {
        var end = Math.Max(1, maxTracks);
        var json = await RunAsync(
            [
                "--yes-playlist",
                "--playlist-end",
                end.ToString(),
                "--no-warnings",
                "-J",
                "-f", "bestaudio/best",
                playlistUrl,
            ],
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var source = InferSource(playlistUrl, root);
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            var single = ParseTrackElement(root, playlistUrl, requestedBy, source);
            return single is null ? [] : [single];
        }

        var list = new List<TrackInfoEntity>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (list.Count >= maxTracks)
            {
                break;
            }

            if (entry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var entrySource = InferSource(playlistUrl, entry);
            var track = ParseTrackElement(entry, playlistUrl, requestedBy, entrySource);
            if (track is not null
                && !string.IsNullOrWhiteSpace(track.WebpageUrl ?? track.MediaUrl))
            {
                list.Add(track);
            }
        }

        return list;
    }

    private static string InferSource(string query, JsonElement root)
    {
        var extractor = root.TryGetProperty("extractor", out var ex) ? ex.GetString() : null;
        if (!string.IsNullOrWhiteSpace(extractor)
            && extractor.Contains("soundcloud", StringComparison.OrdinalIgnoreCase))
        {
            return TrackIdentityHelper.SourceSoundcloud;
        }

        var webpage = root.TryGetProperty("webpage_url", out var w) ? w.GetString() : null;
        if (TrackIdentityHelper.IsSoundCloudUrl(webpage) || TrackIdentityHelper.IsSoundCloudUrl(query))
        {
            return TrackIdentityHelper.SourceSoundcloud;
        }

        return TrackIdentityHelper.SourceYoutube;
    }

    private static TrackInfoEntity? ParseTrackElement(
        JsonElement root,
        string fallbackQuery,
        string? requestedBy,
        string source)
    {
        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var rawWebpage = root.TryGetProperty("webpage_url", out var webEl) ? webEl.GetString() : null;
        var rawUrl = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : root.TryGetProperty("original_url", out var origEl) ? origEl.GetString() : null;

        string? webpageUrl;
        if (string.Equals(source, TrackIdentityHelper.SourceSoundcloud, StringComparison.Ordinal))
        {
            var idHint = root.TryGetProperty("id", out var idHintEl) ? idHintEl.GetString() : null;
            webpageUrl = TrackIdentityHelper.ResolveSoundCloudEntryUrl(rawWebpage, rawUrl, idHint)
                         ?? (TryAbsoluteOrNull(rawWebpage) ?? TryAbsoluteOrNull(rawUrl));
        }
        else
        {
            webpageUrl = TryAbsoluteOrNull(rawWebpage) ?? TryAbsoluteOrNull(rawUrl);
        }

        var thumbnail = TryReadThumbnail(root);
        TimeSpan? duration = null;
        if (root.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out var seconds) && seconds > 0)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        long? sourceBytes = TryReadSourceBytes(root);

        string? externalId = null;
        if (root.TryGetProperty("id", out var idEl))
        {
            externalId = idEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(externalId)
            && TrackIdentityHelper.TryParseYoutubeId(webpageUrl, out var fromWeb))
        {
            externalId = fromWeb;
        }

        if (string.IsNullOrWhiteSpace(externalId)
            && TrackIdentityHelper.TryParseYoutubeId(fallbackQuery, out var fromQuery))
        {
            externalId = fromQuery;
        }

        if (string.IsNullOrWhiteSpace(externalId)
            && string.Equals(source, TrackIdentityHelper.SourceSoundcloud, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(webpageUrl))
        {
            externalId = TrackIdentityHelper.ForDirectUrl(
                TrackIdentityHelper.NormalizeAbsoluteUrl(webpageUrl));
        }

        if (string.IsNullOrWhiteSpace(webpageUrl) && string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        var mediaUrl = !string.IsNullOrWhiteSpace(webpageUrl)
            ? webpageUrl!
            : TryReadUrlFromJson(root) ?? fallbackQuery;

        return new TrackInfoEntity
        {
            Title = string.IsNullOrWhiteSpace(title) ? fallbackQuery : title!,
            MediaUrl = mediaUrl,
            WebpageUrl = webpageUrl ?? mediaUrl,
            ThumbnailUrl = thumbnail,
            RequestedBy = requestedBy,
            Duration = duration,
            Source = source,
            ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId,
            SourceBytes = sourceBytes,
        };
    }

    private static string? TryAbsoluteOrNull(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    private static long? TryReadSourceBytes(JsonElement root)
    {
        if (TryReadPositiveInt64(root, "filesize", out var exact))
        {
            return exact;
        }

        if (TryReadPositiveInt64(root, "filesize_approx", out var approx))
        {
            return approx;
        }

        if (root.TryGetProperty("requested_downloads", out var downloads)
            && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in downloads.EnumerateArray())
            {
                if (TryReadPositiveInt64(item, "filesize", out var dExact))
                {
                    return dExact;
                }

                if (TryReadPositiveInt64(item, "filesize_approx", out var dApprox))
                {
                    return dApprox;
                }
            }
        }

        return null;
    }

    private static bool TryReadPositiveInt64(JsonElement el, string name, out long value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n) && n > 0)
        {
            value = n;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d) && d > 0)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    public async Task<string?> DownloadTrackAudioAsync(string source, string outputPathWithoutExt, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPathWithoutExt) ?? _options.TempDir);
        var template = outputPathWithoutExt + ".%(ext)s";
        // Avoid -x/--audio-format (requires ffmpeg). Prefer progressive audio containers.
        var output = await RunAsync(
            [
                "--no-playlist",
                "--no-warnings",
                "-f", "bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio/best",
                "-o", template,
                source,
            ],
            cancellationToken).ConfigureAwait(false);

        // yt-dlp writes beside the template; find newest matching file.
        var dir = Path.GetDirectoryName(outputPathWithoutExt)!;
        var prefix = Path.GetFileName(outputPathWithoutExt);
        var match = Directory.EnumerateFiles(dir, prefix + ".*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (match is null)
        {
            _logger.LogWarning("yt-dlp download produced no file for {Source}. stdout={Stdout}", source, output);
        }

        return match;
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var attempts = BuildLaunchAttempts();
        Exception? lastStartError = null;

        foreach (var (fileName, prefixArgs) in attempts)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in prefixArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            try
            {
                if (!process.Start())
                {
                    _logger.LogWarning("Failed to start media extractor via {File}", fileName);
                    continue;
                }
            }
            catch (Exception ex)
            {
                lastStartError = ex;
                _logger.LogDebug(ex, "Unable to launch media extractor via {File}", fileName);
                continue;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "yt-dlp exited with code {Code} via {File}. stderr={Stderr}",
                    process.ExitCode,
                    fileName,
                    stderr.ToString().Trim());
                return null;
            }

            return stdout.ToString().Trim();
        }

        _logger.LogError(
            lastStartError,
            "Unable to launch yt-dlp. Install with: pip install -U yt-dlp (or set MEZUBE_YTDLP_PATH).");
        return null;
    }

    private List<(string FileName, string[] PrefixArgs)> BuildLaunchAttempts()
    {
        var attempts = new List<(string, string[])>
        {
            (_options.YtDlpPath, Array.Empty<string>()),
        };

        if (!string.Equals(_options.YtDlpPath, "yt-dlp", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add(("yt-dlp", Array.Empty<string>()));
        }

        attempts.Add(("python", ["-m", "yt_dlp"]));
        attempts.Add(("py", ["-m", "yt_dlp"]));
        return attempts;
    }

    private static string? TryReadUrlFromJson(JsonElement root)
    {
        if (root.TryGetProperty("url", out var urlEl))
        {
            var url = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        if (root.TryGetProperty("requested_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                if (format.TryGetProperty("url", out var fUrl))
                {
                    var url = fUrl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
        }

        return null;
    }

    private static string? TryReadThumbnail(JsonElement root)
    {
        if (root.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String)
        {
            return thumb.GetString();
        }

        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            return thumbs.EnumerateArray().LastOrDefault().TryGetProperty("url", out var u) ? u.GetString() : null;
        }

        return null;
    }

    private static bool LooksLikeUrl(string query)
        => Uri.TryCreate(query, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
