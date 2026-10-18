using Mezube.Domain;
using Mezon.Net.Logging;
using Microsoft.Extensions.Configuration;

namespace Mezube.Bot;

public sealed class BotOptions
{
    private const int DefaultAudioBitrateKbps = 128;

    public long BotId { get; set; }
    public string Token { get; set; } = string.Empty;
    /// <summary>Gateway Basic-Auth server key.</summary>
    public string ServerKey { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
    /// <summary>
    /// Minimum severity forwarded by Mezon.Net (<c>Mezon:LogLevel</c> or <c>Logging:LogLevel:Mezon</c>).
    /// </summary>
    public LogLevel MezonNetLogLevel { get; set; } = LogLevel.Warning;
    public string CommandPrefix { get; set; } = "!";
    public long DefaultClanId { get; set; }
    public long DefaultStreamChannelId { get; set; }
    public int StreamConnectTimeoutMs { get; set; } = 10000;
    public int StreamReconnectBackoffMs { get; set; } = 1000;
    /// <summary>Maximum number of consecutive reconnect attempts after a failed stream connection.</summary>
    public int StreamReconnectMaxAttempts { get; set; } = 8;
    /// <summary>Upper bound for exponential stream reconnect backoff.</summary>
    public int StreamReconnectMaxBackoffMs { get; set; } = 30000;
    /// <summary>Random delay added to reconnects to avoid synchronized retry bursts.</summary>
    public int StreamReconnectJitterMs { get; set; } = 250;
    /// <summary>How long a connection must stay connected before the retry counter resets.</summary>
    public int StreamReconnectStableResetMs { get; set; } = 30000;
    public int StreamMaxSessions { get; set; } = 32;
    public string YtDlpPath { get; set; } = "yt-dlp";
    /// <summary>
    /// YouTube Innertube clients for yt-dlp (<c>--extractor-args youtube:player_client=…</c>).
    /// Download retries rotate through <see cref="Media.YtDlpYoutubePolicy.FallbackPlayerClients"/>.
    /// </summary>
    public string YtDlpPlayerClients { get; set; } = "android,web,mweb";
    /// <summary>
    /// JS runtime name for n-sig (<c>deno</c>, <c>node</c>, <c>bun</c>, <c>quickjs</c>). Empty or <c>none</c> = yt-dlp default.
    /// </summary>
    public string YtDlpJsRuntime { get; set; } = "deno";
    /// <summary>Optional absolute path to the JS runtime binary (appended as <c>runtime:path</c>).</summary>
    public string YtDlpJsRuntimePath { get; set; } = string.Empty;
    /// <summary>Optional Netscape cookies file for yt-dlp (<c>--cookies</c>). Ignored when missing.</summary>
    public string YtDlpCookiesPath { get; set; } = string.Empty;
    /// <summary>Download attempts including the first (YouTube 403 / empty output).</summary>
    public int YtDlpDownloadRetries { get; set; } = 3;
    /// <summary>Base delay before retry 2; attempt 3 waits 2× this.</summary>
    public int YtDlpRetryDelayMs { get; set; } = 5000;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string TempDir { get; set; } = "temp";
    /// <summary>Npgsql connection string, e.g. Host=localhost;Database=mezube;Username=mezube;Password=…</summary>
    public string PostgresConnectionString { get; set; } = string.Empty;
    /// <summary>StackExchange.Redis configuration string, e.g. localhost:6379</summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";
    public int PreparedAudioBitrateKbps { get; set; } = DefaultAudioBitrateKbps;
    public string BotDisplayName { get; set; } = "Mezube";
    /// <summary>Bot avatar used for embed author icon and as thumbnail fallback.</summary>
    public string BotAvatarUrl { get; set; } = string.Empty;
    /// <summary>Equalizer sprite sheet for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizImageUrl { get; set; } = string.Empty;
    /// <summary>Equalizer TexturePacker JSON for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizPositionUrl { get; set; } = string.Empty;
    /// <summary>Public CDN base used after UploadAttachmentFile PUT.</summary>
    public string CdnBaseUrl { get; set; } = string.Empty;
    /// <summary>Multipart part size for Cloudflare R2 (S3-compatible; minimum 5 MiB except last part).</summary>
    public int MultipartUploadPartBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxPrepConcurrency { get; set; } = MezubeConstants.MaxPrepConcurrency;
    public int MaxConcurrentPlayback { get; set; } = MezubeConstants.MaxConcurrentPlayback;
    public int MaxQueuePerClan { get; set; } = MezubeConstants.MaxQueuePerClan;
    public long MaxAudioBytes { get; set; } = MezubeConstants.MaxAudioBytes;
    public long MaxVideoBytes { get; set; } = MezubeConstants.MaxVideoBytes;
    public int InterTrackDelayMs { get; set; } = MezubeConstants.InterTrackDelayMs;

    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new BotOptions();
        configuration.GetSection("Mezon").Bind(options);
        configuration.GetSection("Mezube").Bind(options);
        options.MezonNetLogLevel = ParseMezonNetLogLevel(configuration);

        if (string.IsNullOrWhiteSpace(options.CommandPrefix))
        {
            options.CommandPrefix = "!";
        }

        return options;
    }

    private static LogLevel ParseMezonNetLogLevel(IConfiguration configuration)
    {
        var text = configuration["Mezon:LogLevel"]
            ?? configuration["Logging:LogLevel:Mezon"]
            ?? configuration["Logging:LogLevel:Default"];

        if (!string.IsNullOrWhiteSpace(text)
            && Enum.TryParse(text, ignoreCase: true, out LogLevel level))
        {
            return level;
        }

        return LogLevel.Warning;
    }

    public void Validate()
    {
        if (BotId == 0)
        {
            throw new InvalidOperationException("Mezon:BotId is required.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new InvalidOperationException("Mezon:Token is required.");
        }

        if (StreamConnectTimeoutMs < 1000
            || StreamReconnectBackoffMs < 1
            || StreamReconnectMaxAttempts < 0
            || StreamReconnectMaxBackoffMs < StreamReconnectBackoffMs
            || StreamReconnectJitterMs < 0
            || StreamReconnectStableResetMs < 0)
        {
            throw new InvalidOperationException("Mezube stream connect timeout/reconnect settings have invalid values.");
        }

        if (StreamMaxSessions < 1)
        {
            throw new InvalidOperationException("Mezube:StreamMaxSessions must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(CdnBaseUrl))
        {
            throw new InvalidOperationException("Mezube:CdnBaseUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            throw new InvalidOperationException("Mezube:PostgresConnectionString is required.");
        }

        if (string.IsNullOrWhiteSpace(RedisConnectionString))
        {
            throw new InvalidOperationException("Mezube:RedisConnectionString is required.");
        }

        ValidateAudioSettings();
    }

    private void ValidateAudioSettings()
    {
        ValidateBitrate(nameof(PreparedAudioBitrateKbps), PreparedAudioBitrateKbps);

        if (MaxPrepConcurrency < 1)
        {
            throw new InvalidOperationException("Mezube:MaxPrepConcurrency must be >= 1.");
        }

        if (MaxConcurrentPlayback < 1)
        {
            throw new InvalidOperationException("Mezube:MaxConcurrentPlayback must be >= 1.");
        }

        if (MaxQueuePerClan < 1)
        {
            throw new InvalidOperationException("Mezube:MaxQueuePerClan must be >= 1.");
        }

        if (MaxAudioBytes < 1)
        {
            throw new InvalidOperationException("Mezube:MaxAudioBytes must be >= 1.");
        }

        if (MaxVideoBytes < 1)
        {
            throw new InvalidOperationException("Mezube:MaxVideoBytes must be >= 1.");
        }

        if (MultipartUploadPartBytes < 5 * 1024 * 1024)
        {
            throw new InvalidOperationException("Mezube:MultipartUploadPartBytes must be >= 5 MiB (Cloudflare R2 / S3 multipart minimum).");
        }

        if (InterTrackDelayMs < 0)
        {
            throw new InvalidOperationException("Mezube:InterTrackDelayMs must be >= 0.");
        }

        if (YtDlpDownloadRetries < 1)
        {
            throw new InvalidOperationException("Mezube:YtDlpDownloadRetries must be >= 1.");
        }

        if (YtDlpRetryDelayMs < 0)
        {
            throw new InvalidOperationException("Mezube:YtDlpRetryDelayMs must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(YtDlpPlayerClients))
        {
            YtDlpPlayerClients = "android,web,mweb";
        }
    }

    private static void ValidateBitrate(string name, int value)
    {
        if (value is < 32 or > 512)
        {
            throw new InvalidOperationException($"Mezube:{name} must be between 32 and 512 kb/s.");
        }
    }
}
