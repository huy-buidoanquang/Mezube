using Mezube.Stn;
using Microsoft.Extensions.Configuration;

namespace Mezube.Bot;

public sealed class BotOptions
{
    public long BotId { get; set; }
    public string Token { get; set; } = string.Empty;
    /// <summary>Gateway Basic-Auth server key (dev: defaultkey, prod: HTTP3m3zonPr0dkey).</summary>
    public string ServerKey { get; set; } = "defaultkey";
    public string Host { get; set; } = "dev-mezon.nccsoft.vn";
    public string Port { get; set; } = "8088";
    public bool UseSsl { get; set; } = true;
    public string CommandPrefix { get; set; } = "!";
    public long DefaultClanId { get; set; }
    public long DefaultStreamChannelId { get; set; }
    /// <summary>
    /// STN origin only (no path), e.g. <c>https://stn.mezon.ai</c> or <c>http://localhost:8081</c>.
    /// Voice REST uses <c>/api/v2/voice/*</c>; streaming WS uses <c>ws(s)://…/ws</c>.
    /// </summary>
    public string StnBaseUrl { get; set; } = string.Empty;
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string TempDir { get; set; } = "temp";
    public string TracksDbPath { get; set; } = "data/tracks.db";
    public string BotDisplayName { get; set; } = "Mezube";
    /// <summary>Bot avatar used for embed author icon and as thumbnail fallback.</summary>
    public string BotAvatarUrl { get; set; } =
        "https://pub-35517170c1554a008bed9d7565fa4bb2.r2.dev/1783755414765047808/2081603576727080960.gif";
    /// <summary>Equalizer sprite sheet for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizImageUrl { get; set; } = "https://pub-35517170c1554a008bed9d7565fa4bb2.r2.dev/2080678819785609216/2081695963616907264.png";
    /// <summary>Equalizer TexturePacker JSON for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizPositionUrl { get; set; } = "https://pub-35517170c1554a008bed9d7565fa4bb2.r2.dev/2080678819785609216/2081695966087352320.json";
    /// <summary>Public CDN base used after UploadAttachmentFile PUT.</summary>
    public string CdnBaseUrl { get; set; } = "https://cdn.komu.vn";

    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new BotOptions();
        configuration.GetSection("Mezon").Bind(options);
        configuration.GetSection("Mezube").Bind(options);

        options.BotId = ParseLong(configuration["MEZON_BOT_ID"]) ?? options.BotId;
        options.Token = FirstNonEmpty(configuration["MEZON_BOT_TOKEN"], options.Token) ?? string.Empty;
        options.ServerKey = FirstNonEmpty(configuration["MEZON_SERVER_KEY"], options.ServerKey) ?? "defaultkey";
        options.Host = FirstNonEmpty(configuration["MEZON_HOST"], options.Host)!;
        options.Port = FirstNonEmpty(configuration["MEZON_PORT"], options.Port)!;
        if (bool.TryParse(configuration["MEZON_USE_SSL"], out var useSsl))
        {
            options.UseSsl = useSsl;
        }

        options.CommandPrefix = FirstNonEmpty(configuration["MEZUBE_COMMAND_PREFIX"], options.CommandPrefix)!;
        options.DefaultClanId = ParseLong(configuration["MEZUBE_DEFAULT_CLAN_ID"]) ?? options.DefaultClanId;
        options.DefaultStreamChannelId = ParseLong(configuration["MEZUBE_DEFAULT_STREAM_CHANNEL_ID"]) ?? options.DefaultStreamChannelId;
        options.StnBaseUrl = FirstNonEmpty(configuration["MEZUBE_STN_BASE_URL"], options.StnBaseUrl) ?? string.Empty;
        options.YtDlpPath = FirstNonEmpty(configuration["MEZUBE_YTDLP_PATH"], options.YtDlpPath)!;
        options.FfmpegPath = FirstNonEmpty(configuration["MEZUBE_FFMPEG_PATH"], options.FfmpegPath)!;
        options.TempDir = FirstNonEmpty(configuration["MEZUBE_TEMP_DIR"], options.TempDir)!;
        options.TracksDbPath = FirstNonEmpty(configuration["MEZUBE_TRACKS_DB_PATH"], options.TracksDbPath)!;
        options.BotDisplayName = FirstNonEmpty(configuration["MEZUBE_BOT_DISPLAY_NAME"], options.BotDisplayName)!;
        options.BotAvatarUrl = FirstNonEmpty(configuration["MEZUBE_BOT_AVATAR_URL"], options.BotAvatarUrl)!;
        options.VizImageUrl = FirstNonEmpty(configuration["MEZUBE_VIZ_IMAGE_URL"], options.VizImageUrl) ?? string.Empty;
        options.VizPositionUrl = FirstNonEmpty(configuration["MEZUBE_VIZ_POSITION_URL"], options.VizPositionUrl) ?? string.Empty;
        options.CdnBaseUrl = FirstNonEmpty(configuration["MEZUBE_CDN_BASE_URL"], options.CdnBaseUrl)!;

        if (string.IsNullOrWhiteSpace(options.CommandPrefix))
        {
            options.CommandPrefix = "!";
        }

        ApplyStnDefaults(options);
        return options;
    }

    /// <summary>
    /// Dev Mezon (*.nccsoft.vn) → stn.nccsoft.vn; production → stn.mezon.ai.
    /// </summary>
    private static void ApplyStnDefaults(BotOptions options)
    {
        var isDev = options.Host.Contains("nccsoft", StringComparison.OrdinalIgnoreCase)
                    || options.Host.Contains("dev-mezon", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(options.StnBaseUrl))
        {
            options.StnBaseUrl = isDev ? "https://stn.nccsoft.vn" : "https://stn.mezon.ai";
        }

        options.StnBaseUrl = StnUrl.NormalizeBase(options.StnBaseUrl);
    }

    public void Validate()
    {
        if (BotId == 0)
        {
            throw new InvalidOperationException("MEZON_BOT_ID is required.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new InvalidOperationException("MEZON_BOT_TOKEN is required.");
        }
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value, out var parsed) ? parsed : null;

    private static string? FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : b;
}
