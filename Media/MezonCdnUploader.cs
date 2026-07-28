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
                var publicUrl = ToPublicCdnUrl(upload.Url, upload.Filename);
                await EnsureReachableAsync(publicUrl, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Uploaded media to CDN {Url} ({Bytes} bytes)", publicUrl, fileInfo.Length);
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

    /// <summary>True if a previously cached playable URL still responds (STN will download it).</summary>
    public async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureReachableAsync(url, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PutFileAsync(string presignedUrl, string localPath, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(nameof(MezonCdnUploader));
        await using var stream = File.OpenRead(localPath);
        using var content = new StreamContent(stream);
        // MinIO/R2 S3-compatible presigned PUTs often expect empty/no content-type.
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

    private async Task EnsureReachableAsync(string publicUrl, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(nameof(MezonCdnUploader));
        using var head = new HttpRequestMessage(HttpMethod.Head, publicUrl);
        using var headResponse = await http.SendAsync(
                head,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (headResponse.IsSuccessStatusCode)
        {
            return;
        }

        // Public CDN may reject HEAD / return 400; probe with a 1-byte ranged GET.
        using var get = new HttpRequestMessage(HttpMethod.Get, publicUrl);
        get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var getResponse = await http.SendAsync(
                get,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (getResponse.IsSuccessStatusCode
            || getResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            return;
        }

        throw new InvalidOperationException(
            $"CDN object not reachable after upload head={(int)headResponse.StatusCode} get={(int)getResponse.StatusCode}: {publicUrl}");
    }

    /// <summary>
    /// Presigned PUT hosts (R2 S3 API) are not publicly fetchable.
    /// Map object key onto <see cref="BotOptions.CdnBaseUrl"/> (e.g. pub-*.r2.dev).
    /// </summary>
    internal string ToPublicCdnUrl(string presignedUrl, string objectKey)
    {
        var key = ResolveObjectKey(presignedUrl, objectKey);
        return $"{_options.CdnBaseUrl.TrimEnd('/')}/{key.TrimStart('/')}";
    }

    private static string ResolveObjectKey(string presignedUrl, string objectKey)
    {
        if (!string.IsNullOrWhiteSpace(objectKey)
            && objectKey.Contains('/', StringComparison.Ordinal)
            && !objectKey.Contains('\\', StringComparison.Ordinal))
        {
            return objectKey.TrimStart('/');
        }

        if (Uri.TryCreate(presignedUrl, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        return objectKey.TrimStart('/');
    }

    private static bool IsRetryable(Exception ex)
        => ex is TimeoutException
           || ex.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("CDN PUT failed", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFilename(string name)
    {
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? $"mezube_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.m4a" : safe;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
