using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mezube.Bot;
using System.Diagnostics;

namespace Mezube.Stn;

/// <summary>
/// Legacy STN voice client for <c>/api/playmedia</c> + <c>/api/stopmedia</c> (LiveKit URL_INPUT).
/// Mezube no longer uses this path — use <see cref="StnRestClientV2"/> instead.
/// Kept only as a reference for other clients that still call the legacy STN API.
/// </summary>
[Obsolete("Use StnRestClientV2 (STN /api/v2/voice/*). Legacy URL_INPUT playmedia is for other clients only.")]
public sealed class StnRestClient
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _http;
    private readonly ILogger<StnRestClient> _logger;
    private readonly Uri _playUri;
    private readonly Uri _stopUri;
    private readonly object _gate = new();
    private string? _activeRoom;
    private string? _activeIdentity;
    private string? _activeIngressId;

    public StnRestClient(HttpClient http, BotOptions options, ILogger<StnRestClient> logger)
    {
        _http = http;
        _logger = logger;
        _playUri = StnUrl.PlayMediaUri(options.StnBaseUrl);
        _stopUri = StnUrl.StopMediaUri(options.StnBaseUrl);
    }

    /// <summary>Unique per play; LiveKit username max length is 32.</summary>
    public static string NewPublisherIdentity(long botUserId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{botUserId}-mz-{suffix}";
    }

    public async Task PlayAsync(
        string authToken,
        string roomName,
        string mediaUrl,
        string trackName,
        long botUserId,
        string botDisplayName,
        CancellationToken cancellationToken = default)
    {
        var participantIdentity = NewPublisherIdentity(botUserId);
        using var content = CreatePlayContent(roomName, participantIdentity, botDisplayName, mediaUrl, trackName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _playUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "playMedia POST {Endpoint} room={Room} identity={Identity} name={Name} url={Url}",
            _playUri,
            roomName,
            participantIdentity,
            trackName,
            mediaUrl);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                DescribeFailure("playMedia", _playUri.ToString(), (int)response.StatusCode, body));
        }

        var ingressId = await TryReadIngressIdAsync(response.Content, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _activeRoom = roomName;
            _activeIdentity = participantIdentity;
            _activeIngressId = ingressId;
        }

        _logger.LogInformation(
            "playMedia ok room={Room} ingressId={IngressId} elapsedMs={ElapsedMs}",
            roomName,
            ingressId ?? "(none)",
            stopwatch.ElapsedMilliseconds);
    }

    public async Task StopAsync(
        string authToken,
        string roomName,
        CancellationToken cancellationToken = default)
    {
        string? identity;
        string? ingressId;
        lock (_gate)
        {
            if (!string.Equals(_activeRoom, roomName, StringComparison.Ordinal)
                || (string.IsNullOrWhiteSpace(_activeIdentity) && string.IsNullOrWhiteSpace(_activeIngressId)))
            {
                _logger.LogDebug("No active voice publisher for room={Room}", roomName);
                return;
            }

            // Claim before HTTP so concurrent StopAsync callers become no-ops.
            identity = _activeIdentity;
            ingressId = _activeIngressId;
            _activeRoom = null;
            _activeIdentity = null;
            _activeIngressId = null;
        }

        using var content = CreateStopContent(ingressId ?? string.Empty, roomName, identity ?? string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Post, _stopUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;

        _logger.LogInformation(
            "stopMedia POST {Endpoint} room={Room} identity={Identity} ingressId={IngressId}",
            _stopUri,
            roomName,
            identity,
            ingressId);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                DescribeFailure("stopMedia", _stopUri.ToString(), (int)response.StatusCode, body));
        }

        _logger.LogInformation("stopMedia ok room={Room}", roomName);
    }

    private static ByteArrayContent CreatePlayContent(
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

        var content = new ByteArrayContent(buffer.WrittenMemory.ToArray());
        content.Headers.ContentType = JsonMediaType;
        return content;
    }

    private static ByteArrayContent CreateStopContent(string ingressId, string roomName, string participantIdentity)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ingress_id"u8, ingressId);
            writer.WriteString("room_name"u8, roomName);
            writer.WriteString("participant_identity"u8, participantIdentity);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(buffer.WrittenMemory.ToArray());
        content.Headers.ContentType = JsonMediaType;
        return content;
    }

    private static async Task<string?> TryReadIngressIdAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("ingress_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }

            if (root.TryGetProperty("ingressId", out var camel) && camel.ValueKind == JsonValueKind.String)
            {
                return camel.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string DescribeFailure(string op, string endpoint, int status, string body)
    {
        if (status == 401)
        {
            return $"{op} 401 on {endpoint}: JWT must match STN environment. body={Truncate(body, 200)}";
        }

        if (status is 502 or 503)
        {
            return $"{op} {status} on {endpoint}: STN unavailable. body={Truncate(body, 200)}";
        }

        return $"{op} {status} on {endpoint}: {Truncate(body, 400)}";
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
