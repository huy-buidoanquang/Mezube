using Mezube.Stn;
using Microsoft.Extensions.Configuration;

namespace Mezube.Bot;

public sealed class BotOptions
{
    public long BotId { get; set; }
    public string Token { get; set; } = string.Empty;
    /// <summary>Gateway Basic-Auth server key (dev: defaultkey, prod: HTTP3m3zonPr0dkey).</summary>
    public string ServerKey { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
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
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string TempDir { get; set; } = "temp";
    public string TracksDbPath { get; set; } = "data/tracks.db";
    public string BotDisplayName { get; set; } = "Mezube";
    /// <summary>Bot avatar used for embed author icon and as thumbnail fallback.</summary>
    public string BotAvatarUrl { get; set; } = string.Empty;
    /// <summary>Equalizer sprite sheet for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizImageUrl { get; set; } = string.Empty;
    /// <summary>Equalizer TexturePacker JSON for !np embed Animation (optional; auto-upload if empty).</summary>
    public string VizPositionUrl { get; set; } = string.Empty;
    /// <summary>Public CDN base used after UploadAttachmentFile PUT.</summary>
    public string CdnBaseUrl { get; set; } = string.Empty;

    public static BotOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new BotOptions();
        configuration.GetSection("Mezon").Bind(options);
        configuration.GetSection("Mezube").Bind(options);

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
    }
}
