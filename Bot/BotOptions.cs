using Mezube.Domain;
using Mezube.Stn;
using Mezon.Net.Logging;
using Microsoft.Extensions.Configuration;

namespace Mezube.Bot;

public sealed class BotOptions
{
    private const int DefaultAudioBitrateKbps = 128;
    private const int DefaultAudioChannels = 2;
    private const int DefaultAudioSampleRate = 48000;

    public MezonOptions Mezon { get; set; } = new();
    public MediaOptions Media { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();

    public long BotId { get; set; }
    public string Token { get; set; } = string.Empty;
    /// <summary>Gateway Basic-Auth server key (dev: defaultkey, prod: HTTP3m3zonPr0dkey).</summary>
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
    /// <summary>
    /// STN origin only (no path), e.g. <c>https://stn.mezon.ai</c> or <c>http://localhost:8081</c>.
    /// Voice REST uses <c>/api/v2/voice/*</c>; streaming WS uses <c>ws(s)://…/ws</c>.
    /// </summary>
    public string StnBaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// When true, voice uses STN WHIP (ffmpeg Opus push to LiveKit) instead of v2 CDN pull-publish.
    /// Requires ffmpeg with <c>whip</c> muxer. Falls back to v2 if muxer is missing.
    /// Default false (set in appsettings).
    /// </summary>
    public bool StnWhipEnabled { get; set; }
    /// <summary>
    /// Optional STN streaming <c>publisher_password</c>. Sent in WS <c>Value.Password</c>.
    /// Leave empty when STN does not require a publisher password.
    /// </summary>
    public string StnPublisherPassword { get; set; } = string.Empty;
    public string YtDlpPath { get; set; } = "yt-dlp";
    /// <summary>
    /// YouTube Innertube clients for yt-dlp (<c>--extractor-args youtube:player_client=…</c>).
    /// Download retries rotate through <see cref="Media.YtDlpYoutubePolicy.FallbackPlayerClients"/>.
    /// </summary>
    public string YtDlpPlayerClients { get; set; } = "android,web,mweb";
    /// <summary>
    /// JS runtime name for n-sig (<c>deno</c>, <c>node</c>, <c>bun</c>, <c>quickjs</c>). Empty = auto (deno, then node).
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
    /// <summary>Legacy SQLite path (migration script only). Runtime uses Postgres.</summary>
    public string TracksDbPath { get; set; } = "data/tracks.db";
    /// <summary>Npgsql connection string, e.g. Host=localhost;Database=mezube;Username=mezube;Password=…</summary>
    public string PostgresConnectionString { get; set; } = string.Empty;
    /// <summary>StackExchange.Redis configuration string, e.g. localhost:6379</summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";
    public int PreparedAudioBitrateKbps { get; set; } = DefaultAudioBitrateKbps;
    public int PreparedAudioChannels { get; set; } = DefaultAudioChannels;
    public int PreparedAudioSampleRate { get; set; } = DefaultAudioSampleRate;
    public int WhipAudioBitrateKbps { get; set; } = DefaultAudioBitrateKbps;
    public int WhipAudioChannels { get; set; } = DefaultAudioChannels;
    public int WhipAudioSampleRate { get; set; } = DefaultAudioSampleRate;
    public bool WhipEncoderDisabled { get; set; }
    public string WhipOpusApplication { get; set; } = "audio";
    public string WhipOpusVbr { get; set; } = "on";
    public int WhipOpusComplexity { get; set; } = 10;
    public int WhipPacketLossPercent { get; set; } = 3;
    public bool WhipEnableInbandFec { get; set; } = true;
    public int WhipHandshakeTimeoutMs { get; set; } = 10000;
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
    public int InterTrackDelayMs { get; set; } = MezubeConstants.InterTrackDelayMs;

    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new BotOptions();
        configuration.GetSection("Mezon").Bind(options);
        configuration.GetSection("Mezube").Bind(options);
        configuration.GetSection("Mezon").Bind(options.Mezon);
        configuration.GetSection("Mezube").Bind(options.Media);
        configuration.GetSection("Mezube").Bind(options.Persistence);
        options.CopyToNested();
        options.MezonNetLogLevel = ParseMezonNetLogLevel(configuration);

        if (string.IsNullOrWhiteSpace(options.CommandPrefix))
        {
            options.CommandPrefix = "!";
        }

        if (!string.IsNullOrWhiteSpace(options.StnBaseUrl))
        {
            options.StnBaseUrl = StnUrl.NormalizeBase(options.StnBaseUrl);
        }

        return options;
    }

    private void CopyToNested()
    {
        Mezon.BotId = BotId;
        Mezon.Token = Token;
        Mezon.ServerKey = ServerKey;
        Mezon.Host = Host;
        Mezon.Port = Port;
        Mezon.UseSsl = UseSsl;

        Media.YtDlpPath = YtDlpPath;
        Media.YtDlpPlayerClients = YtDlpPlayerClients;
        Media.YtDlpJsRuntime = YtDlpJsRuntime;
        Media.YtDlpJsRuntimePath = YtDlpJsRuntimePath;
        Media.YtDlpCookiesPath = YtDlpCookiesPath;
        Media.YtDlpDownloadRetries = YtDlpDownloadRetries;
        Media.YtDlpRetryDelayMs = YtDlpRetryDelayMs;
        Media.FfmpegPath = FfmpegPath;
        Media.TempDir = TempDir;
        Media.CdnBaseUrl = CdnBaseUrl;
        Media.MultipartUploadPartBytes = MultipartUploadPartBytes;
        Media.PreparedAudioBitrateKbps = PreparedAudioBitrateKbps;
        Media.PreparedAudioChannels = PreparedAudioChannels;
        Media.PreparedAudioSampleRate = PreparedAudioSampleRate;
        Media.WhipAudioBitrateKbps = WhipAudioBitrateKbps;
        Media.WhipAudioChannels = WhipAudioChannels;
        Media.WhipAudioSampleRate = WhipAudioSampleRate;
        Media.WhipEncoderDisabled = WhipEncoderDisabled;
        Media.WhipOpusApplication = WhipOpusApplication;
        Media.WhipOpusVbr = WhipOpusVbr;
        Media.WhipOpusComplexity = WhipOpusComplexity;
        Media.WhipPacketLossPercent = WhipPacketLossPercent;
        Media.WhipEnableInbandFec = WhipEnableInbandFec;
        Media.WhipHandshakeTimeoutMs = WhipHandshakeTimeoutMs;
        Media.StnWhipEnabled = StnWhipEnabled;
        Media.StnBaseUrl = StnBaseUrl;
        Media.StnPublisherPassword = StnPublisherPassword;

        Persistence.PostgresConnectionString = PostgresConnectionString;
        Persistence.RedisConnectionString = RedisConnectionString;
        Persistence.TracksDbPath = TracksDbPath;
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

        if (string.IsNullOrWhiteSpace(StnBaseUrl))
        {
            throw new InvalidOperationException("Mezube:StnBaseUrl is required.");
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
        ValidateBitrate(nameof(WhipAudioBitrateKbps), WhipAudioBitrateKbps);
        ValidateChannels(nameof(PreparedAudioChannels), PreparedAudioChannels);
        ValidateChannels(nameof(WhipAudioChannels), WhipAudioChannels);
        ValidateSampleRate(nameof(PreparedAudioSampleRate), PreparedAudioSampleRate);
        ValidateSampleRate(nameof(WhipAudioSampleRate), WhipAudioSampleRate);

        if (WhipOpusComplexity is < 0 or > 10)
        {
            throw new InvalidOperationException("Mezube:WhipOpusComplexity must be between 0 and 10.");
        }

        if (WhipPacketLossPercent is < 0 or > 100)
        {
            throw new InvalidOperationException("Mezube:WhipPacketLossPercent must be between 0 and 100.");
        }

        if (WhipHandshakeTimeoutMs < 1000)
        {
            throw new InvalidOperationException("Mezube:WhipHandshakeTimeoutMs must be >= 1000.");
        }

        if (!IsAllowed(WhipOpusApplication, "audio", "voip", "lowdelay"))
        {
            throw new InvalidOperationException("Mezube:WhipOpusApplication must be audio, voip, or lowdelay.");
        }

        if (!IsAllowed(WhipOpusVbr, "on", "off", "constrained"))
        {
            throw new InvalidOperationException("Mezube:WhipOpusVbr must be on, off, or constrained.");
        }

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

    private static void ValidateChannels(string name, int value)
    {
        if (value is < 1 or > 2)
        {
            throw new InvalidOperationException($"Mezube:{name} must be 1 or 2.");
        }
    }

    private static void ValidateSampleRate(string name, int value)
    {
        if (value is < 8000 or > 48000)
        {
            throw new InvalidOperationException($"Mezube:{name} must be between 8000 and 48000.");
        }
    }

    private static bool IsAllowed(string value, params string[] allowed)
    {
        return allowed.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }
}
