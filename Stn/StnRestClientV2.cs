using Mezube.Bot;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mezube.Stn;

public sealed class StnRestClientV2
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan StatusPollIntervalMin = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StatusPollIntervalMax = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _http;
    private readonly ILogger<StnRestClientV2> _logger;
    private readonly Uri _playUri;
    private readonly Uri _stopUri;
    private readonly Uri _statusUri;
    private readonly ConcurrentDictionary<string, string> _roomJobs = new(StringComparer.Ordinal);

    public StnRestClientV2(HttpClient http, BotOptions options, ILogger<StnRestClientV2> logger)
    {
        _http = http;
        _logger = logger;
        _playUri = StnUrl.VoiceV2PlayUri(options.StnBaseUrl);
        _stopUri = StnUrl.VoiceV2StopUri(options.StnBaseUrl);
        _statusUri = StnUrl.VoiceV2StatusUri(options.StnBaseUrl);
    }

    public ICollection<string> ActiveRooms => _roomJobs.Keys;

    public bool TryGetJobId(string roomName, out string jobId)
        => _roomJobs.TryGetValue(roomName, out jobId!);

    /// <summary>Unique per play; LiveKit username max length is 32.</summary>
    public static string NewPublisherIdentity(long botUserId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{botUserId}-mz-{suffix}";
    }

    public async Task PlayUntilPublishingAsync(
        string authToken,
        string roomName,
        string participantIdentity,
        string participantName,
        string mediaUrl,
        string trackName,
        CancellationToken cancellationToken = default)
    {
        using var content = CreatePlayContent(roomName, participantIdentity, participantName, mediaUrl, trackName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _playUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new StnVoiceException("voiceV2 play", response.StatusCode, body);
        }

        var accepted = await ReadAcceptedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accepted.JobId))
        {
            throw new InvalidOperationException("voiceV2 play returned no job_id");
        }

        _roomJobs[roomName] = accepted.JobId;

        try
        {
            await WaitForPublishingAsync(authToken, accepted.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryStopAcceptedAsync(authToken, accepted.JobId, roomName).ConfigureAwait(false);
            _roomJobs.TryRemove(roomName, out _);
            throw;
        }
    }

    /// <summary>Poll until terminal status (completed/failed/stopped). Returns status string.</summary>
    public async Task<string> WaitUntilTerminalAsync(
        string authToken,
        string roomName,
        CancellationToken cancellationToken = default)
    {
        if (!_roomJobs.TryGetValue(roomName, out var jobId) || string.IsNullOrWhiteSpace(jobId))
        {
            return "stopped";
        }

        var delay = StatusPollIntervalMin;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_statusUri}?job_id={Uri.EscapeDataString(jobId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _roomJobs.TryRemove(roomName, out _);
                return "stopped";
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new StnVoiceException("voiceV2 status", response.StatusCode, body);
            }

            var snapshot = await ReadStatusAsync(response.Content, cancellationToken).ConfigureAwait(false);
            switch (snapshot.Status)
            {
                case "completed":
                case "failed":
                case "stopped":
                    _roomJobs.TryRemove(roomName, out _);
                    return snapshot.Status ?? "stopped";
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var nextMs = Math.Min(delay.TotalMilliseconds * 1.5, StatusPollIntervalMax.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(nextMs);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return "stopped";
    }

    public async Task StopAsync(string authToken, string roomName, CancellationToken cancellationToken = default)
    {
        _roomJobs.TryRemove(roomName, out var jobId);

        using var content = CreateStopContent(jobId ?? string.Empty, roomName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _stopUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new StnVoiceException("voiceV2 stop", response.StatusCode, body);
        }
    }

    private async Task WaitForPublishingAsync(string authToken, string jobId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PublishTimeout);

        var delay = StatusPollIntervalMin;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_statusUri}?job_id={Uri.EscapeDataString(jobId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                throw new StnVoiceException("voiceV2 status", response.StatusCode, body);
            }

            var snapshot = await ReadStatusAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
            switch (snapshot.Status)
            {
                case "publishing":
                    return;
                case "failed":
                    throw new InvalidOperationException(
                        $"voiceV2 publish failed job={jobId} code={snapshot.LastErrorCode} err={snapshot.LastErrorMessage}");
                case "stopped":
                case "completed":
                    throw new InvalidOperationException($"voiceV2 ended before publishing job={jobId} status={snapshot.Status}");
            }

            await Task.Delay(delay, timeoutCts.Token).ConfigureAwait(false);
            var nextMs = Math.Min(delay.TotalMilliseconds * 1.5, StatusPollIntervalMax.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(nextMs);
        }
    }

    private async Task TryStopAcceptedAsync(string authToken, string jobId, string roomName)
    {
        try
        {
            using var content = CreateStopContent(jobId, roomName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _stopUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Content = content;
            using var _ = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static HttpContent CreatePlayContent(
        string roomName,
        string participantIdentity,
        string participantName,
        string url,
        string name)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("room_name"u8, roomName);
            writer.WriteString("participant_identity"u8, participantIdentity);
            writer.WriteString("participant_name"u8, participantName);
            writer.WriteString("url"u8, url);
            writer.WriteString("name"u8, name);
            writer.WriteEndObject();
        }

        return new PooledJsonContent(buffer.WrittenSpan);
    }

    private static HttpContent CreateStopContent(string jobId, string roomName)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("job_id"u8, jobId);
            writer.WriteString("room_name"u8, roomName);
            writer.WriteEndObject();
        }

        return new PooledJsonContent(buffer.WrittenSpan);
    }

    private static async Task<AcceptedResponse> ReadAcceptedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var accepted = await JsonSerializer.DeserializeAsync<AcceptedResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return accepted ?? new AcceptedResponse();
    }

    private static async Task<VoiceStatusResponse> ReadStatusAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var status = await JsonSerializer.DeserializeAsync<VoiceStatusResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return status ?? new VoiceStatusResponse();
    }

    private sealed class AcceptedResponse
    {
        [JsonPropertyName("job_id")]
        public string? JobId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class VoiceStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("last_error_code")]
        public string? LastErrorCode { get; set; }

        [JsonPropertyName("last_error_message")]
        public string? LastErrorMessage { get; set; }
    }
}
