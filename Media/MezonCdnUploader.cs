using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Media;

/// <summary>
/// Uploads a local file via Mezon presigned URL, returning a public CDN URL STN can fetch.
/// </summary>
public sealed class MezonCdnUploader
{
    private readonly BotOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MezonCdnUploader> _logger;

    public MezonCdnUploader(
        BotOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<MezonCdnUploader> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> UploadAsync(
        MezonClient client,
        string localPath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(localPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new FileNotFoundException("Media file missing for CDN upload.", localPath);
        }

        var filename = SanitizeFilename(fileInfo.Name);
        Exception? last = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var upload = await client.UploadAttachmentFileAsync(
                        new UploadAttachmentParams(
                            filename: filename,
                            filetype: contentType,
                            size: (int)Math.Min(fileInfo.Length, int.MaxValue)),
                        new RequestOptions { SocketSendTimeout = 120_000 })
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(upload.Url) || string.IsNullOrWhiteSpace(upload.Filename))
                {
                    throw new InvalidOperationException("UploadAttachmentFile returned empty URL/filename.");
                }

                await PutFileAsync(upload.Url, localPath, cancellationToken).ConfigureAwait(false);

                var publicUrl = BuildPublicUrl(upload.Filename);
                _logger.LogInformation("Uploaded media to CDN {Url} ({Bytes} bytes)", publicUrl, fileInfo.Length);
                return publicUrl;
            }
            catch (Exception ex) when (attempt < 3 && IsRetryable(ex))
            {
                last = ex;
                _logger.LogWarning(ex, "CDN upload attempt {Attempt}/3 failed; retrying…", attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("CDN upload failed.");
    }

    private async Task PutFileAsync(string presignedUrl, string localPath, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(nameof(MezonCdnUploader));
        await using var stream = File.OpenRead(localPath);
        using var content = new StreamContent(stream);
        // MinIO/R2 presigned PUTs often expect empty/no content-type.
        content.Headers.ContentType = null;

        using var request = new HttpRequestMessage(HttpMethod.Put, presignedUrl) { Content = content };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"CDN PUT failed {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
    }

    private string BuildPublicUrl(string objectKey)
    {
        var cdnBase = string.IsNullOrWhiteSpace(_options.CdnBaseUrl)
            ? "https://cdn.komu.vn"
            : _options.CdnBaseUrl.TrimEnd('/');
        return $"{cdnBase}/{objectKey.TrimStart('/')}";
    }

    private static bool IsRetryable(Exception ex)
        => ex is TimeoutException
           || ex.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFilename(string name)
    {
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? $"mezube_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.m4a" : safe;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
