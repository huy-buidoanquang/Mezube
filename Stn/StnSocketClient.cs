using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mezube.Bot;

namespace Mezube.Stn;

public sealed class StnSocketClient : IAsyncDisposable
{
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(20);
    /// <summary>
    /// STN fetches the whole FileUrl and validates WebM GOP before <c>connect_publisher</c> ack.
    /// </summary>
    private static readonly TimeSpan PublisherAckTimeout = TimeSpan.FromMinutes(3);

    private readonly BotOptions _options;
    private readonly ILogger<StnSocketClient> _logger;
    private readonly string _wsBase;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(512);
    private readonly byte[] _receiveBuffer = new byte[8 * 1024];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _ackWaiters = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private string? _token;
    private long _botUserId;
    private long _channelId;
    private TaskCompletionSource? _trackEnded;
    private TaskCompletionSource? _publisherEnded;
    private volatile bool _paused;

    public StnSocketClient(BotOptions options, ILogger<StnSocketClient> logger)
    {
        _options = options;
        _logger = logger;
        _wsBase = StnUrl.WebSocketBase(options.StnBaseUrl);
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public bool IsPaused => _paused;

    /// <returns><see langword="true"/> when a new WebSocket was opened this call.</returns>
    public async Task<bool> EnsureConnectedAsync(
        string authToken,
        long botUserId,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        _botUserId = botUserId;
        // Token may refresh between tracks (GetOrRefreshAuthTokenAsync). The STN
        // socket was already authenticated at upgrade — reconnecting would run
        // Conn.Close → leavePublisherPresence → RemovePublisher → channel_closed
        // for every listener. Keep the open socket and only remember the newer token.
        if (IsConnected)
        {
            if (!string.Equals(_token, authToken, StringComparison.Ordinal))
            {
                _logger.LogDebug("STN publisher WS kept across auth token refresh");
                _token = authToken;
            }

            // Receive loop must outlive per-track CTS (skip cancels trackCts). If it
            // somehow exited while the socket stayed Open, restart it on this session.
            EnsureReceiveLoop();
            return false;
        }

        _token = authToken;

        var displayName = string.IsNullOrWhiteSpace(username) ? _options.BotDisplayName : username;
        var uri = BuildWsUri(_wsBase, authToken, displayName);
        var socket = CreateSocket();
        _logger.LogDebug("Connecting STN publisher WS {Uri}", RedactToken(uri));
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            // Session-lifetime CTS only — NEVER link to Play's trackCts. Cancelling
            // ReceiveAsync aborts ClientWebSocket (1006) → STN leavePublisherPresence
            // (ws_close) → kicks every listener on skip/next.
            EnsureReceiveLoop();
            _logger.LogInformation("STN publisher WS connected via {Base}", _wsBase);
            return true;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                "Không kết nối được STN streaming WebSocket (cùng môi trường với Mezon host).\n" +
                DescribeFailure(_wsBase, ex) +
                "\nDev STN Rust listen :8081 (vd. http://172.16.100.158:8081). Không dùng stn.mezon.ai với JWT nccsoft.",
                ex);
        }
    }

    private void EnsureReceiveLoop()
    {
        if (_receiveTask is { IsCompleted: false })
        {
            return;
        }

        _receiveCts?.Dispose();
        _receiveCts = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public async Task PlayAsync(long clanId, long streamChannelId, string fileUrl, CancellationToken cancellationToken = default)
    {
        _channelId = streamChannelId;
        _paused = false;
        // Arm before connect ack so a fast stream_track_ended cannot be missed.
        ResetTrackEnded();
        await SendKeyAndWaitAsync(
                "connect_publisher",
                "connect_publisher",
                clanId,
                streamChannelId,
                fileUrl,
                cancellationToken,
                ackTimeout: PublisherAckTimeout)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Abort the current URL early (skip) without tearing down the publisher session.
    /// STN keeps listeners and emits <c>stream_track_ended</c>.
    /// </summary>
    public async Task EndTrackAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            CompleteTrackEnded();
            return;
        }

        // Already finished (server EOF) — do not send another end that could race Play.
        if (_trackEnded?.Task.IsCompleted == true)
        {
            return;
        }

        try
        {
            await SendKeyAsync(
                    "stream_track_ended",
                    clanId,
                    streamChannelId,
                    fileUrl: string.Empty,
                    pauseValue: null,
                    cancellationToken)
                .ConfigureAwait(false);

            // Wait briefly for STN to release the publisher claim before the next connect_publisher.
            var tcs = _trackEnded;
            if (tcs is not null)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("STN end-track ack timed out channel={ChannelId}; continuing", streamChannelId);
                    CompleteTrackEnded();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "STN end-track send failed channel={ChannelId}", streamChannelId);
            CompleteTrackEnded();
        }
    }

    public async Task SetPausedAsync(long clanId, long streamChannelId, bool paused, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("STN WebSocket is not connected.");
        }

        await SendKeyAndWaitAsync(
                "stream_track_paused",
                "stream_track_paused",
                clanId,
                streamChannelId,
                fileUrl: string.Empty,
                cancellationToken,
                pauseValue: paused)
            .ConfigureAwait(false);
        _paused = paused;
    }

    /// <summary>Tear down the publisher session and kick listeners (<c>stop_publisher</c>).</summary>
    public async Task StopPublisherAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsConnected)
            {
                await SendKeyAsync(
                        "stop_publisher",
                        clanId,
                        streamChannelId,
                        fileUrl: string.Empty,
                        pauseValue: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _paused = false;
            CompleteTrackEnded();
            CompletePublisherEnded();
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task WaitUntilTrackEndedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _trackEnded;
        if (tcs is null)
        {
            return Task.FromCanceled(cancellationToken.CanBeCanceled
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    public Task WaitUntilPublisherEndedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _publisherEnded;
        if (tcs is null)
        {
            return Task.FromCanceled(cancellationToken.CanBeCanceled
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private void ResetTrackEnded()
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _trackEnded, next);
        previous?.TrySetCanceled();
    }

    private void CompleteTrackEnded()
    {
        _trackEnded?.TrySetResult();
    }

    private void CompletePublisherEnded()
    {
        _publisherEnded ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _publisherEnded.TrySetResult();
        CompleteTrackEnded();
    }

    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return socket;
    }

    private static Uri BuildWsUri(string baseUrl, string authToken, string username)
    {
        var query =
            $"username={Uri.EscapeDataString(username)}" +
            $"&token={Uri.EscapeDataString(authToken)}";
        return new Uri($"{baseUrl}?{query}");
    }

    private static string DescribeFailure(string baseUrl, Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("'503'", StringComparison.Ordinal) || StnServerLoad.MentionsCapacity(msg))
        {
            return $"- {baseUrl}: 503 — STN đang quá tải (hết slot listener / áp lực bộ nhớ) hoặc đang shutdown. Thử lại sau ít phút.";
        }

        if (msg.Contains("'502'", StringComparison.Ordinal))
        {
            return $"- {baseUrl}: 502 Bad Gateway (service STN down / nginx không proxy được backend).";
        }

        if (msg.Contains("'404'", StringComparison.Ordinal))
        {
            return $"- {baseUrl}: 404 — sai host/port hoặc không có route /ws. STN Rust mặc định TCP 8081 (không phải :80).";
        }

        if (msg.Contains("'200'", StringComparison.Ordinal))
        {
            return $"- {baseUrl}: HTTP 200 thay vì 101 — endpoint /ws hiện không accept WebSocket upgrade.";
        }

        return $"- {baseUrl}: {msg}";
    }

    private static string RedactToken(Uri uri)
    {
        var text = uri.ToString();
        var idx = text.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return text;
        }

        var end = text.IndexOf('&', idx);
        return end < 0
            ? text[..(idx + 6)] + "***"
            : text[..(idx + 6)] + "***" + text[end..];
    }

    private async Task SendKeyAndWaitAsync(
        string key,
        string ackKey,
        long clanId,
        long streamChannelId,
        string fileUrl,
        CancellationToken cancellationToken,
        bool? pauseValue = null,
        TimeSpan? ackTimeout = null)
    {
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ackWaiters.TryAdd(ackKey, waiter))
        {
            throw new InvalidOperationException($"STN command already in flight: {ackKey}");
        }

        try
        {
            await SendKeyAsync(key, clanId, streamChannelId, fileUrl, pauseValue, cancellationToken)
                .ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ackTimeout ?? CommandAckTimeout);
            var error = await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException($"STN {ackKey} failed: {error}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for STN {ackKey} ack.");
        }
        finally
        {
            _ackWaiters.TryRemove(ackKey, out _);
        }
    }

    private async Task SendKeyAsync(
        string key,
        long clanId,
        long streamChannelId,
        string fileUrl,
        bool? pauseValue,
        CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("STN WebSocket is not connected.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sendBuffer.Clear();
            using (var writer = new Utf8JsonWriter(_sendBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("ClanId"u8, clanId.ToString());
                writer.WriteString("ChannelId"u8, streamChannelId.ToString());
                writer.WriteString("UserId"u8, _botUserId.ToString());
                writer.WriteString("ClientId"u8, $"{_botUserId}-mezube");
                writer.WriteBoolean("IsPublisher"u8, true);
                writer.WriteString("Key"u8, key);
                writer.WritePropertyName("Value"u8);
                if (pauseValue is { } paused)
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("paused"u8, paused);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("ChannelId"u8, streamChannelId.ToString());
                    writer.WriteString("Password"u8, _options.StnPublisherPassword ?? string.Empty);
                    writer.WriteString("FileUrl"u8, fileUrl);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            _logger.LogDebug(
                "STN send key={Key} channel={ChannelId} via={Base}",
                key,
                streamChannelId,
                _wsBase);
            // Do not pass caller's CT into SendAsync — cancelling it aborts ClientWebSocket
            // (same 1006 / ws_close kick as a cancelled ReceiveAsync).
            await _socket.SendAsync(
                    _sendBuffer.WrittenMemory,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket is { State: WebSocketState.Open })
            {
                var result = await _socket.ReceiveAsync(_receiveBuffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("STN WS closed by server: {Status}", result.CloseStatus);
                    FailPending("STN websocket closed by server");
                    CompletePublisherEnded();
                    break;
                }

                if (result.Count <= 0)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(_receiveBuffer, 0, result.Count);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogTrace("STN WS message: {Message}", text);
                }

                HandleServerMessage(text);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STN receive loop ended");
            FailPending(ex.Message);
            CompletePublisherEnded();
        }
    }

    private void HandleServerMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("Key", out var keyElement))
            {
                return;
            }

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (string.Equals(key, "connect_publisher", StringComparison.Ordinal))
            {
                CompleteAck("connect_publisher", error: null);
                return;
            }

            if (string.Equals(key, "stream_track_ended", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "STN stream_track_ended channel={ChannelId}",
                    _channelId);
                _paused = false;
                CompleteTrackEnded();
                return;
            }

            if (string.Equals(key, "stream_track_paused", StringComparison.Ordinal))
            {
                _paused = ReadPausedValue(doc.RootElement) ?? _paused;
                CompleteAck("stream_track_paused", error: null);
                _logger.LogDebug("STN stream_track_paused paused={Paused} channel={ChannelId}", _paused, _channelId);
                return;
            }

            if (string.Equals(key, "password_required", StringComparison.Ordinal))
            {
                _logger.LogDebug("STN password_required for publisher (using configured StnPublisherPassword)");
                return;
            }

            if (string.Equals(key, "error", StringComparison.Ordinal))
            {
                var error = ReadJsonValueAsString(doc.RootElement);
                CompleteAck("connect_publisher", error ?? "unknown STN error");
                CompleteAck("stream_track_paused", error ?? "unknown STN error");
                CompleteTrackEnded();
                return;
            }

            if (string.Equals(key, "info", StringComparison.Ordinal))
            {
                var info = ReadJsonValueAsString(doc.RootElement);
                if (string.Equals(info, "stream_publisher_ended", StringComparison.Ordinal)
                    || string.Equals(info, "stream publish failed", StringComparison.Ordinal)
                    || (info?.Contains("stream publish failed", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    _logger.LogInformation("STN publisher ended info={Info}", info);
                    CompletePublisherEnded();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse STN WS message");
        }
    }

    private static bool? ReadPausedValue(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var valueElement))
        {
            return null;
        }

        if (valueElement.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (valueElement.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (valueElement.ValueKind == JsonValueKind.Object
            && valueElement.TryGetProperty("paused", out var pausedElement)
            && (pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return pausedElement.GetBoolean();
        }

        return null;
    }

    private static string? ReadJsonValueAsString(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var valueElement))
        {
            return null;
        }

        return valueElement.ValueKind switch
        {
            JsonValueKind.String => valueElement.GetString(),
            JsonValueKind.Null => null,
            _ => valueElement.ToString(),
        };
    }

    private void CompleteAck(string ackKey, string? error)
    {
        if (_ackWaiters.TryRemove(ackKey, out var waiter))
        {
            waiter.TrySetResult(error);
        }
    }

    private void FailPending(string error)
    {
        foreach (var key in _ackWaiters.Keys)
        {
            if (_ackWaiters.TryRemove(key, out var waiter))
            {
                waiter.TrySetResult(error);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
            _receiveCts.Dispose();
            _receiveCts = null;
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _receiveTask = null;
        }

        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // ignored
            }

            _socket.Dispose();
            _socket = null;
        }

        FailPending("STN websocket disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
