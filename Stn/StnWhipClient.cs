using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Stn;

/// <summary>
/// STN WHIP bridge control-plane client. Supports many rooms in one bot process.
/// Media is pushed by the caller to <see cref="WhipSession.WhipUrl"/> with the returned token.
/// </summary>
public sealed class StnWhipClient
{
    private readonly HttpClient _http;
    private readonly ILogger<StnWhipClient> _logger;
    private readonly Uri _startUri;
    private readonly Uri _stopUri;
    private readonly ConcurrentDictionary<string, WhipSession> _rooms = new(StringComparer.Ordinal);

    public StnWhipClient(HttpClient http, BotOptions options, ILogger<StnWhipClient> logger)
    {
        _http = http;
        _logger = logger;
        _startUri = StnUrl.VoiceWhipStartUri(options.StnBaseUrl);
        _stopUri = StnUrl.VoiceWhipStopUri(options.StnBaseUrl);
    }

    public ICollection<string> ActiveRooms => _rooms.Keys;

    public async Task<WhipSession> StartAsync(
        string authToken,
        string roomName,
        string participantIdentity,
        string participantName,
        CancellationToken cancellationToken = default)
    {
        using var content = CreateStartContent(roomName, participantIdentity, participantName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _startUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw StnFailure.From("whip start", response.StatusCode, body);
        }

        var started = await ReadStartAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(started.SessionId) || string.IsNullOrWhiteSpace(started.WhipUrl) || string.IsNullOrWhiteSpace(started.Token))
        {
            throw new InvalidOperationException("whip start returned incomplete credentials");
        }

        var session = new WhipSession(
            started.SessionId!,
            started.RoomName ?? roomName,
            started.WhipUrl!,
            started.Token!,
            started.ParticipantIdentity ?? participantIdentity,
            started.ExpiresAtUnixMs);

        _rooms[session.RoomName] = session;
        _logger.LogDebug(
            "WHIP session started room={Room} session={Session} whip={Whip}",
            session.RoomName,
            session.SessionId,
            session.WhipUrl);
        return session;
    }

    public async Task StopAsync(string authToken, string roomName, CancellationToken cancellationToken = default)
    {
        _rooms.TryRemove(roomName, out var session);

        using var content = CreateStopContent(session?.SessionId ?? string.Empty, roomName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _stopUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw StnFailure.From("whip stop", response.StatusCode, body);
        }
    }

    public bool TryGetSession(string roomName, out WhipSession? session)
        => _rooms.TryGetValue(roomName, out session);

    private static HttpContent CreateStartContent(string roomName, string participantIdentity, string participantName)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("room_name"u8, roomName);
            writer.WriteString("participant_identity"u8, participantIdentity);
            writer.WriteString("participant_name"u8, participantName);
            writer.WriteEndObject();
        }

        return new PooledJsonContent(buffer.WrittenSpan);
    }

    private static HttpContent CreateStopContent(string sessionId, string roomName)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id"u8, sessionId);
            writer.WriteString("room_name"u8, roomName);
            writer.WriteEndObject();
        }

        return new PooledJsonContent(buffer.WrittenSpan);
    }

    private static async Task<StartResponse> ReadStartAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<StartResponse>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? new StartResponse();
    }

    private sealed class StartResponse
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("room_name")]
        public string? RoomName { get; set; }

        [JsonPropertyName("whip_url")]
        public string? WhipUrl { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("participant_identity")]
        public string? ParticipantIdentity { get; set; }

        [JsonPropertyName("expires_at_unix_ms")]
        public long ExpiresAtUnixMs { get; set; }
    }
}

public sealed record WhipSession(
    string SessionId,
    string RoomName,
    string WhipUrl,
    string Token,
    string ParticipantIdentity,
    long ExpiresAtUnixMs);
