using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Media;

    /// <summary>
    /// Uploads media via Mezon presigned URL to Cloudflare R2 (single PUT or multipart),
    /// returning a public CDN URL STN can fetch.
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

    /// <summary>
    /// Streams an Ogg body into Mezon multipart upload against Cloudflare R2 (S3-compatible):
    /// Start → PUT parts → Finish. Part size defaults to 5 MiB (R2 multipart minimum except last part).
    /// </summary>
    public async Task<(string PublicUrl, long BytesUploaded)> UploadMultipartFromStreamAsync(
        MezonClient client,
        Stream content,
        string filename,
        string contentType,
        CancellationToken cancellationToken = default,
        long? maxBytes = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        filename = SanitizeFilename(filename);
        var sizeCap = maxBytes is > 0 ? maxBytes.Value : _options.MaxAudioBytes;
        var partSize = Math.Max(_options.MultipartUploadPartBytes, 5 * 1024 * 1024);
        var maxParts = (int)Math.Clamp(
            (sizeCap + partSize - 1) / partSize + 1,
            1,
            10_000);

        var started = await client.MultipartUploadAttachmentFileStartAsync(
                new UploadAttachmentParams(
                    filename: filename,
                    filetype: contentType,
                    size: (int)Math.Min(sizeCap, int.MaxValue),
                    partCount: maxParts),
                new RequestOptions { SocketSendTimeout = 120_000 })
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(started.UploadId)
            || string.IsNullOrWhiteSpace(started.Filename)
            || started.Urls.Count == 0)
        {
            throw new InvalidOperationException("MultipartUploadAttachmentFileStart returned incomplete credentials.");
        }

        var urls = started.Urls.ToList();
        var completedParts = new List<MultipartUploadAttachmentPartParams>();
        var buffer = new byte[partSize];
        var filled = 0;
        var partNumber = 1;
        long totalBytes = 0;

        async Task FlushPartAsync(bool final)
        {
            if (filled == 0)
            {
                if (final && completedParts.Count == 0)
                {
                    throw new InvalidOperationException("Multipart upload received empty stream.");
                }

                return;
            }

            if (partNumber > urls.Count)
            {
                throw new InvalidOperationException(
                    $"Multipart upload exceeded reserved part count ({urls.Count}).");
            }

            if (totalBytes + filled > sizeCap)
            {
                throw new InvalidOperationException(
                    $"Prepared media exceeds max bytes ({sizeCap}).");
            }

            var payload = new byte[filled];
            Buffer.BlockCopy(buffer, 0, payload, 0, filled);
            var putUrl = urls[partNumber - 1];
            var eTag = await PutPartAsync(putUrl, payload, cancellationToken).ConfigureAwait(false);
            completedParts.Add(new MultipartUploadAttachmentPartParams(partNumber, eTag));
            totalBytes += filled;
            filled = 0;
            partNumber++;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await content.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
            if (filled == buffer.Length)
            {
                await FlushPartAsync(final: false).ConfigureAwait(false);
            }
        }

        await FlushPartAsync(final: true).ConfigureAwait(false);

        var finished = await client.MultipartUploadAttachmentFileFinishAsync(
                new MultipartUploadAttachmentFinishParams(
                    uploadId: started.UploadId,
                    parts: completedParts,
                    filename: started.Filename),
                new RequestOptions { SocketSendTimeout = 120_000 })
            .ConfigureAwait(false);

        var objectKey = !string.IsNullOrWhiteSpace(finished.Filename) ? finished.Filename : started.Filename;
        var publicUrl = !string.IsNullOrWhiteSpace(finished.Url)
            ? ToPublicCdnUrl(finished.Url, objectKey)
            : ToPublicCdnUrl(urls[0], objectKey);

        await EnsureReachableAsync(publicUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Multipart CDN upload ready url={Url} parts={Parts} bytes={Bytes}",
            publicUrl,
            completedParts.Count,
            totalBytes);
        return (publicUrl, totalBytes);
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

    private async Task<string> PutPartAsync(string presignedUrl, byte[] payload, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(nameof(MezonCdnUploader));
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = null;

        using var request = new HttpRequestMessage(HttpMethod.Put, presignedUrl) { Content = content };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"CDN multipart PUT failed {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        if (response.Headers.ETag is { Tag: { Length: > 0 } tag })
        {
            return tag.Trim('"');
        }

        if (response.Headers.TryGetValues("ETag", out var values))
        {
            var raw = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim().Trim('"');
            }
        }

        // Some S3-compatible endpoints omit ETag; finish may still accept empty and rely on part number.
        return string.Empty;
    }

    private async Task PutFileAsync(string presignedUrl, string localPath, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(nameof(MezonCdnUploader));
        await using var stream = File.OpenRead(localPath);
        using var content = new StreamContent(stream);
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
        return string.IsNullOrWhiteSpace(safe) ? $"mezube_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.ogg" : safe;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
