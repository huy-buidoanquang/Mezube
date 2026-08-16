namespace Mezube.Bot;

/// <summary>yt-dlp, ffmpeg, CDN, and prepared-media knobs.</summary>
public sealed class MediaOptions
{
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string YtDlpPlayerClients { get; set; } = "android,web,mweb";
    public string YtDlpJsRuntime { get; set; } = "deno";
    public string YtDlpJsRuntimePath { get; set; } = string.Empty;
    public string YtDlpCookiesPath { get; set; } = string.Empty;
    public int YtDlpDownloadRetries { get; set; } = 3;
    public int YtDlpRetryDelayMs { get; set; } = 5000;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string TempDir { get; set; } = "temp";
    public string CdnBaseUrl { get; set; } = string.Empty;
    public int MultipartUploadPartBytes { get; set; } = 5 * 1024 * 1024;
    public int PreparedAudioBitrateKbps { get; set; } = 128;
    public int PreparedAudioChannels { get; set; } = 2;
    public int PreparedAudioSampleRate { get; set; } = 48000;
    public int PreparedVideoBitrateKbps { get; set; } = 1000;
    public int PreparedVideoHeight { get; set; } = 720;
    public int PreparedVideoFps { get; set; } = 30;
    public string StnBaseUrl { get; set; } = string.Empty;
    public string StnPublisherPassword { get; set; } = string.Empty;
}
