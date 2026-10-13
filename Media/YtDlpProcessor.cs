using System.Diagnostics;
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
    private readonly SemaphoreSlim _lookupGate = new(4);

    public YtDlpProcessor(
        BotOptions options,
        ILogger<YtDlpProcessor> logger)
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
                "--flat-playlist",
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
                "--flat-playlist",
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
                "--flat-playlist",
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

    public Task<string?> DownloadTrackAudioAsync(
        string source,
        string outputPathWithoutExt,
        CancellationToken cancellationToken = default)
        => DownloadAsync(
            source,
            outputPathWithoutExt,
            "bestaudio[acodec=opus]/bestaudio/best",
            _options.MaxAudioBytes,
            mergeOutputFormat: null,
            cancellationToken);

    public Task<string?> DownloadTrackVideoAsync(
        string source,
        string outputPathWithoutExt,
        CancellationToken cancellationToken = default)
    {
        var height = Math.Clamp(_options.PreparedVideoHeight, 360, 1080);
        var format =
            $"bestvideo[height<={height}][vcodec!=none]+bestaudio[acodec!=none]/"
            + $"bestvideo[height<={height}]+bestaudio/"
            + $"best[height<={height}]/best";
        return DownloadAsync(
            source,
            outputPathWithoutExt,
            format,
            _options.MaxVideoBytes,
            mergeOutputFormat: "mkv",
            cancellationToken);
    }

    private async Task<string?> DownloadAsync(
        string source,
        string outputPathWithoutExt,
        string format,
        long maxBytes,
        string? mergeOutputFormat,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPathWithoutExt) ?? _options.TempDir);
        var template = outputPathWithoutExt + ".%(ext)s";
        var dir = Path.GetDirectoryName(outputPathWithoutExt)!;
        var prefix = Path.GetFileName(outputPathWithoutExt);
        var youtube = YtDlpYoutubePolicy.IsYoutubeSource(source);
        var attempts = Math.Max(1, _options.YtDlpDownloadRetries);
        var delayMs = Math.Max(0, _options.YtDlpRetryDelayMs);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                var wait = delayMs * attempt;
                _logger.LogInformation(
                    "Retrying yt-dlp download attempt {Attempt}/{Attempts} in {DelayMs}ms source={Source}",
                    attempt + 1,
                    attempts,
                    wait,
                    source);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                DownloadedMediaFiles.DeletePrefixed(dir, prefix);
            }

            var playerClients = youtube
                ? YtDlpYoutubePolicy.PlayerClientsForAttempt(attempt, _options.YtDlpPlayerClients)
                : null;
            var downloadArgs = new List<string>
            {
                "--no-playlist",
                "--no-warnings",
                "--retries",
                "2",
                "--socket-timeout",
                "20",
                "--max-filesize",
                maxBytes.ToString(),
                "-f",
                format,
                "-o",
                template,
            };
            if (!string.IsNullOrWhiteSpace(mergeOutputFormat))
            {
                downloadArgs.Add("--merge-output-format");
                downloadArgs.Add(mergeOutputFormat);
            }

            downloadArgs.Add(source);
            if (!string.IsNullOrWhiteSpace(_options.FfmpegPath))
            {
                downloadArgs.Insert(0, _options.FfmpegPath);
                downloadArgs.Insert(0, "--ffmpeg-location");
            }

            var result = await RunDetailedAsync(downloadArgs, playerClients, occupyGate: false, cancellationToken)
                .ConfigureAwait(false);
            var match = result.ExitCode == 0
                ? DownloadedMediaFiles.FindCompleted(dir, prefix)
                : null;
            if (match is not null)
            {
                if (attempt > 0)
                {
                    _logger.LogInformation(
                        "yt-dlp download recovered on attempt {Attempt} clients={Clients} source={Source}",
                        attempt + 1,
                        playerClients ?? "(default)",
                        source);
                }

                return match;
            }

            var stderr = result.Stderr;
            var transient = YtDlpYoutubePolicy.IsTransientDownloadFailure(stderr);
            _logger.LogWarning(
                "yt-dlp download produced no file for {Source} attempt={Attempt}/{Attempts} clients={Clients} transient={Transient} stderr={Stderr}",
                source,
                attempt + 1,
                attempts,
                playerClients ?? "(none)",
                transient,
                string.IsNullOrWhiteSpace(stderr) ? "(null)" : stderr.Trim());

            if (!transient)
            {
                break;
            }
        }

        return null;
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await RunDetailedAsync(args, playerClients: null, occupyGate: true, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Stdout : null;
    }

    private readonly record struct YtDlpRun(int ExitCode, string Stdout, string Stderr);

    private async Task<YtDlpRun> RunDetailedAsync(
        IReadOnlyList<string> args,
        string? playerClients,
        bool occupyGate,
        CancellationToken cancellationToken)
    {
        var fullArgs = PrependCommonArgs(args, playerClients);
        var attempts = BuildLaunchAttempts();
        Exception? lastStartError = null;
        var timeout = args.Any(a => a is "-o" or "--output")
            ? ChildProcessRunner.DefaultDownloadTimeout
            : ChildProcessRunner.DefaultMetadataTimeout;

        if (occupyGate)
        {
            await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var (fileName, prefixArgs) in attempts)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                foreach (var arg in prefixArgs)
                {
                    psi.ArgumentList.Add(arg);
                }

                foreach (var arg in fullArgs)
                {
                    psi.ArgumentList.Add(arg);
                }

                ChildProcessResult result;
                try
                {
                    result = await ChildProcessRunner.RunAsync(psi, timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
                {
                    lastStartError = ex;
                    _logger.LogDebug(ex, "Unable to launch media extractor via {File}", fileName);
                    continue;
                }

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "yt-dlp exited with code {Code} via {File}. stderr={Stderr}",
                        result.ExitCode,
                        fileName,
                        result.Stderr.Trim());
                }

                return new YtDlpRun(result.ExitCode, result.Stdout.Trim(), result.Stderr);
            }
        }
        finally
        {
            if (occupyGate)
            {
                _lookupGate.Release();
            }
        }

        _logger.LogError(
            lastStartError,
            "Unable to launch yt-dlp. Install with: pip install -U yt-dlp (or set MEZUBE_YTDLP_PATH).");
        return new YtDlpRun(-1, string.Empty, lastStartError?.Message ?? "yt-dlp not launched");
    }

    internal List<string> PrependCommonArgs(IReadOnlyList<string> userArgs, string? playerClients)
    {
        var args = new List<string>();
        var js = _options.YtDlpJsRuntime?.Trim();
        if (!string.IsNullOrEmpty(js)
            && !string.Equals(js, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(js, "off", StringComparison.OrdinalIgnoreCase))
        {
            var path = _options.YtDlpJsRuntimePath?.Trim();
            args.Add("--js-runtimes");
            args.Add(string.IsNullOrEmpty(path) ? js : $"{js}:{path}");
        }

        var cookies = _options.YtDlpCookiesPath?.Trim();
        if (!string.IsNullOrWhiteSpace(cookies) && File.Exists(cookies))
        {
            args.Add("--cookies");
            args.Add(cookies);
        }
        else if (!string.IsNullOrWhiteSpace(cookies))
        {
            _logger.LogDebug("YtDlpCookiesPath set but file missing: {Path}", cookies);
        }

        var clients = string.IsNullOrWhiteSpace(playerClients)
            ? _options.YtDlpPlayerClients
            : playerClients;
        if (!string.IsNullOrWhiteSpace(clients))
        {
            args.Add("--extractor-args");
            args.Add($"youtube:player_client={clients.Trim()}");
        }

        args.AddRange(userArgs);
        return args;
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
