using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Stn;

public sealed class StnSocketClient : IAsyncDisposable
{
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PublisherAckTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly BotOptions _options;
    private readonly ILogger<StnSocketClient> _logger;
    private readonly string _wsBase;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(512);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StnPublisherException?>> _ackWaiters = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private long _generation;
    private long _connectedAtTimestamp;
    private long _botUserId;
    private long _channelId;
    private int _state = (int)StnSessionState.Disconnected;
    private int _disposeState;
    private TaskCompletionSource? _trackEnded;
    private TaskCompletionSource? _publisherEnded;
    private volatile bool _paused;

    public StnSocketClient(BotOptions options, ILogger<StnSocketClient> logger)
    {
        _options = options;
        _logger = logger;
        _wsBase = StnUrl.WebSocketBase(options.StnBaseUrl);
    }

    public StnSessionState State => (StnSessionState)Volatile.Read(ref _state);

    public bool IsConnected
        => State == StnSessionState.Ready
           && _socket?.State == WebSocketState.Open
           && _receiveTask is { IsCompleted: false };

    public bool IsPaused => _paused;

    /// <returns><see langword="true"/> when a new WebSocket was opened this call.</returns>
    public async Task<bool> EnsureConnectedAsync(
        string credential,
        long botUserId,
        string? username = null,
        CancellationToken cancellationToken = default,
        StnCredentialKind credentialKind = StnCredentialKind.Jwt,
        int attempt = 1,
        long streamChannelId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _botUserId = botUserId;
            if (streamChannelId != 0)
            {
                _channelId = streamChannelId;
            }
            if (IsConnected)
            {
                return false;
            }

            await DisconnectTransportCoreAsync(graceful: false).ConfigureAwait(false);
            SetState(StnSessionState.Connecting);

            var generation = Interlocked.Increment(ref _generation);
            var displayName = string.IsNullOrWhiteSpace(username) ? _options.BotDisplayName : username;
            var uri = BuildWsUri(_wsBase, credential, displayName);
            var socket = CreateSocket();
            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "STN lifecycle state={State} phase={Phase} channel={ChannelId} sessionGeneration={SessionGeneration} attempt={Attempt} credentialKind={CredentialKind}",
                StnSessionState.Connecting, "handshake", _channelId, generation, attempt, credentialKind);

            try
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                SetState(StnSessionState.Disconnected);
                var statusCode = GetStatusCode(ex);
                var retryable = IsRetryableConnectionFailure(ex, statusCode);
                _logger.LogWarning(
                    "STN handshake failed channel={ChannelId} sessionGeneration={SessionGeneration} phase={Phase} attempt={Attempt} credentialKind={CredentialKind} failureKind={FailureKind} statusCode={StatusCode} exceptionType={ExceptionType} elapsedMs={ElapsedMs}",
                    _channelId, generation, "handshake", attempt, credentialKind,
                    retryable ? "transient" : "permanent", statusCode, ex.GetType().Name,
                    stopwatch.ElapsedMilliseconds);
                throw new StnConnectionException(
                    $"Không kết nối được STN streaming WebSocket. {DescribeFailure(_wsBase, ex, credential)}",
                    "handshake", retryable, statusCode);
            }

            _socket = socket;
            Volatile.Write(ref _connectedAtTimestamp, Stopwatch.GetTimestamp());
            _receiveCts = new CancellationTokenSource();
            _publisherEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetState(StnSessionState.Ready);
            _receiveTask = ReceiveLoopAsync(socket, generation, _receiveCts.Token);

            _logger.LogInformation(
                "STN lifecycle state={State} phase={Phase} channel={ChannelId} sessionGeneration={SessionGeneration} attempt={Attempt} credentialKind={CredentialKind} elapsedMs={ElapsedMs}",
                StnSessionState.Ready, "handshake", _channelId, generation, attempt, credentialKind,
                stopwatch.ElapsedMilliseconds);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task PlayAsync(
        long clanId,
        long streamChannelId,
        string fileUrl,
        CancellationToken cancellationToken = default,
        int attempt = 1)
    {
        EnsureReady();
        _channelId = streamChannelId;
        _paused = false;
        ResetTrackEnded();

        _logger.LogInformation(
            "STN publish phase={Phase} channel={ChannelId} sessionGeneration={SessionGeneration} attempt={Attempt}",
            "connect_publisher", streamChannelId, Volatile.Read(ref _generation), attempt);

        await SendKeyAndWaitAsync(
                "connect_publisher", "connect_publisher", clanId, streamChannelId, fileUrl,
                cancellationToken, ackTimeout: PublisherAckTimeout)
            .ConfigureAwait(false);
    }

    public async Task EndTrackAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _trackEnded?.Task.IsCompleted == true)
        {
            return;
        }

        try
        {
            await SendKeyAsync(
                    "stream_track_ended", clanId, streamChannelId, string.Empty, null, cancellationToken)
                .ConfigureAwait(false);

            var waiter = _trackEnded;
            if (waiter is null)
            {
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "STN end-track ack timed out channel={ChannelId} sessionGeneration={SessionGeneration}",
                    streamChannelId, Volatile.Read(ref _generation));
                CompleteTrackEnded();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "STN end-track failed channel={ChannelId} sessionGeneration={SessionGeneration}",
                streamChannelId, Volatile.Read(ref _generation));
        }
    }

    public async Task SetPausedAsync(long clanId, long streamChannelId, bool paused, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await SendKeyAndWaitAsync(
                "stream_track_paused", "stream_track_paused", clanId, streamChannelId, string.Empty,
                cancellationToken, pauseValue: paused)
            .ConfigureAwait(false);
        _paused = paused;
    }

    public async Task StopPublisherAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsConnected)
            {
                await SendKeyAsync(
                        "stop_publisher", clanId, streamChannelId, string.Empty, null, cancellationToken)
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
        var waiter = _trackEnded;
        if (waiter is null)
        {
            return Task.FromCanceled(cancellationToken.CanBeCanceled
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    public Task WaitUntilPublisherEndedAsync(CancellationToken cancellationToken = default)
    {
        var waiter = _publisherEnded;
        if (waiter is null)
        {
            return Task.FromCanceled(cancellationToken.CanBeCanceled
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    public Task InvalidateAndDisconnectAsync(string reason)
    {
        InvalidateSession(
            Volatile.Read(ref _generation),
            new StnPublisherException(reason, "recovery", retryable: true));
        return DisconnectAsync();
    }

    private void ResetTrackEnded()
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _trackEnded, next);
        previous?.TrySetCanceled();
    }

    private void CompleteTrackEnded() => _trackEnded?.TrySetResult();

    private void FailTrack(Exception exception) => _trackEnded?.TrySetException(exception);

    private void CompletePublisherEnded()
    {
        _publisherEnded?.TrySetResult();
        CompleteTrackEnded();
    }

    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        return socket;
    }

    private static Uri BuildWsUri(string baseUrl, string credential, string username)
    {
        var query = $"username={Uri.EscapeDataString(username)}&token={Uri.EscapeDataString(credential)}";
        return new Uri($"{baseUrl}?{query}");
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
        var waiter = new TaskCompletionSource<StnPublisherException?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var failure = await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StnPublisherException(
                $"Timed out waiting for STN {ackKey} ack.", ackKey, "ack_timeout", retryable: true);
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
        EnsureReady();
        var socket = _socket!;
        var generation = Volatile.Read(ref _generation);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected || !ReferenceEquals(socket, _socket) || generation != Volatile.Read(ref _generation))
            {
                throw new StnPublisherException(
                    "STN websocket generation is no longer usable.",
                    "send", "session_invalidated", retryable: true);
            }

            _sendBuffer.Clear();
            using (var writer = new Utf8JsonWriter(_sendBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("ClanId"u8, clanId.ToString());
                writer.WriteString("ChannelId"u8, streamChannelId.ToString());
                writer.WriteString("UserId"u8, _botUserId.ToString());
                writer.WriteString("ClientId"u8, $"{_botUserId}-mezube-g{generation}");
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
                "STN send phase={Phase} channel={ChannelId} sessionGeneration={SessionGeneration}",
                key, streamChannelId, generation);
            await socket.SendAsync(
                    _sendBuffer.WrittenMemory,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or SocketException)
        {
            var wrapped = new StnPublisherException(
                $"STN send failed: {ex.Message}", "send", "transport_error", retryable: true);
            InvalidateSession(generation, wrapped);
            throw wrapped;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, long generation, CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var failure = new StnPublisherException(
                        $"STN websocket closed by server ({result.CloseStatus}: {result.CloseStatusDescription}).",
                        "receive", "server_close", retryable: true);
                    _logger.LogWarning(
                        "STN receive ended channel={ChannelId} sessionGeneration={SessionGeneration} phase={Phase} failureKind={FailureKind} closeStatus={CloseStatus} connectedMs={ConnectedMs}",
                        _channelId, generation, "receive", failure.Code, result.CloseStatus,
                        GetConnectedMilliseconds());
                    InvalidateSession(generation, failure);
                    return;
                }

                if (result.Count <= 0)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                _logger.LogTrace(
                    "STN message channel={ChannelId} sessionGeneration={SessionGeneration} payload={Payload}",
                    _channelId, generation, text);
                HandleServerMessage(text, generation);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var failure = new StnPublisherException(
                $"STN receive loop ended: {ex.Message}", "receive", "transport_error", retryable: true);
            _logger.LogWarning(
                ex,
                "STN receive ended channel={ChannelId} sessionGeneration={SessionGeneration} phase={Phase} failureKind={FailureKind}",
                _channelId, generation, "receive", failure.Code);
            InvalidateSession(generation, failure);
        }
    }

    private void HandleServerMessage(string text, long generation)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            _logger.LogDebug(
                "Ignoring STN message from stale generation={StaleGeneration} currentGeneration={CurrentGeneration}",
                generation, Volatile.Read(ref _generation));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("Key", out var keyElement))
            {
                return;
            }

            switch (keyElement.GetString())
            {
                case "connect_publisher":
                    CompleteAck("connect_publisher", null);
                    return;
                case "stream_track_ended":
                    _paused = false;
                    CompleteTrackEnded();
                    return;
                case "stream_track_paused":
                    _paused = ReadPausedValue(document.RootElement) ?? _paused;
                    CompleteAck("stream_track_paused", null);
                    return;
                case "password_required":
                    return;
                case "stream_publish_failed":
                    InvalidateSession(generation, ReadStructuredPublishFailure(document.RootElement));
                    return;
                case "error":
                {
                    var error = ReadJsonValueAsString(document.RootElement) ?? "unknown STN error";
                    var failure = CreatePublisherException(error, "server");
                    CompleteAck("connect_publisher", failure);
                    CompleteAck("stream_track_paused", failure);
                    InvalidateSession(generation, failure);
                    return;
                }
                case "info":
                {
                    var info = ReadJsonValueAsString(document.RootElement);
                    if (string.Equals(info, "stream_publisher_ended", StringComparison.Ordinal)
                        || string.Equals(info, "stream publish failed", StringComparison.Ordinal)
                        || (info?.Contains("stream publish failed", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        InvalidateSession(
                            generation,
                            new StnPublisherException(
                                $"STN publisher ended: {info}", "streaming", "publisher_ended"));
                    }

                    return;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to parse STN message channel={ChannelId} sessionGeneration={SessionGeneration}",
                _channelId, generation);
        }
    }

    private void InvalidateSession(long generation, StnPublisherException failure)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        var previous = (StnSessionState)Interlocked.Exchange(ref _state, (int)StnSessionState.Invalidated);
        if (previous is StnSessionState.Invalidated or StnSessionState.Disconnected or StnSessionState.Disposing)
        {
            return;
        }

        _paused = false;
        _logger.LogWarning(
            "STN lifecycle state={State} previousState={PreviousState} channel={ChannelId} sessionGeneration={SessionGeneration} connectionId={ConnectionId} phase={Phase} failureKind={FailureKind} retryable={Retryable} connectedMs={ConnectedMs}",
            StnSessionState.Invalidated, previous, _channelId, generation,
            failure.ConnectionId, failure.Phase, failure.Code, failure.Retryable,
            GetConnectedMilliseconds());
        FailPending(failure);
        FailTrack(failure);
        _publisherEnded?.TrySetResult();
    }

    private static StnPublisherException ReadStructuredPublishFailure(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new StnPublisherException("STN stream publish failed.", "streaming", "publish_failed");
        }

        var phase = value.TryGetProperty("phase", out var phaseElement)
            ? phaseElement.GetString() ?? "streaming"
            : "streaming";
        var code = value.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString() ?? "publish_failed"
            : "publish_failed";
        var message = value.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "STN stream publish failed."
            : "STN stream publish failed.";
        var retryable = value.TryGetProperty("retryable", out var retryableElement)
                        && retryableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && retryableElement.GetBoolean();
        var connectionId = value.TryGetProperty("connection_id", out var connectionIdElement)
            ? connectionIdElement.GetString()
            : null;
        return new StnPublisherException(message, phase, code, retryable, connectionId);
    }

    private static bool? ReadPausedValue(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        return value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty("paused", out var paused)
               && paused.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? paused.GetBoolean()
            : null;
    }

    private static string? ReadJsonValueAsString(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString(),
        };
    }

    private static StnPublisherException CreatePublisherException(string error, string phase)
    {
        var sessionClosed = error.Contains("publisher session closed", StringComparison.OrdinalIgnoreCase);
        var retryable = sessionClosed
                        || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("502", StringComparison.Ordinal)
                        || error.Contains("503", StringComparison.Ordinal)
                        || error.Contains("504", StringComparison.Ordinal);
        var code = sessionClosed
            ? "publisher_session_closed"
            : retryable ? "transient_publish_error" : "publish_error";
        return new StnPublisherException($"STN {phase} failed: {error}", phase, code, retryable);
    }

    private void CompleteAck(string ackKey, StnPublisherException? failure)
    {
        if (_ackWaiters.TryRemove(ackKey, out var waiter))
        {
            waiter.TrySetResult(failure);
        }
    }

    private void FailPending(StnPublisherException failure)
    {
        foreach (var key in _ackWaiters.Keys)
        {
            if (_ackWaiters.TryRemove(key, out var waiter))
            {
                waiter.TrySetResult(failure);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == StnSessionState.Disconnected && _socket is null)
            {
                return;
            }

            var graceful = State == StnSessionState.Ready;
            _logger.LogInformation(
                "STN lifecycle state={State} channel={ChannelId} sessionGeneration={SessionGeneration} phase={Phase} connectedMs={ConnectedMs}",
                StnSessionState.Disposing, _channelId, Volatile.Read(ref _generation), "disconnect",
                GetConnectedMilliseconds());
            SetState(StnSessionState.Disposing);
            await DisconnectTransportCoreAsync(graceful).ConfigureAwait(false);
            SetState(StnSessionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DisconnectTransportCoreAsync(bool graceful)
    {
        var receiveCts = _receiveCts;
        var receiveTask = _receiveTask;
        var socket = _socket;
        _receiveCts = null;
        _receiveTask = null;
        _socket = null;

        if (receiveCts is not null)
        {
            await receiveCts.CancelAsync().ConfigureAwait(false);
        }

        if (socket is not null)
        {
            try
            {
                if (graceful && socket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(CloseTimeout);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    socket.Abort();
                }
            }
            catch
            {
                socket.Abort();
            }
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // Receive failures have already invalidated and completed waiters.
            }
        }

        receiveCts?.Dispose();
        socket?.Dispose();
        Volatile.Write(ref _connectedAtTimestamp, 0);
        FailPending(new StnPublisherException(
            "STN websocket disconnected", "disconnect", "session_disconnected", retryable: true));
    }

    private void EnsureReady()
    {
        if (!IsConnected)
        {
            throw new StnPublisherException(
                $"STN websocket is not usable (state={State}).",
                "control", "session_not_ready", retryable: true);
        }
    }

    private void SetState(StnSessionState state) => Volatile.Write(ref _state, (int)state);

    private long GetConnectedMilliseconds()
    {
        var started = Volatile.Read(ref _connectedAtTimestamp);
        return started == 0 ? 0 : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private static int? GetStatusCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } statusCode })
            {
                return (int)statusCode;
            }

            foreach (var candidate in new[] { 200, 401, 403, 404, 408, 429, 500, 502, 503, 504 })
            {
                if (current.Message.Contains($"'{candidate}'", StringComparison.Ordinal)
                    || current.Message.Contains($" {candidate} ", StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsRetryableConnectionFailure(Exception exception, int? statusCode)
    {
        if (statusCode is 408 or 429 or 500 or 502 or 503 or 504)
        {
            return true;
        }

        if (statusCode is 200 or 401 or 403 or 404)
        {
            return false;
        }

        return exception is TimeoutException or WebSocketException or SocketException
               || exception.InnerException is TimeoutException or WebSocketException or SocketException;
    }

    private static string DescribeFailure(string baseUrl, Exception exception, string credential)
    {
        var statusCode = GetStatusCode(exception);
        return statusCode switch
        {
            503 => $"{baseUrl}: HTTP 503 — STN unavailable or at capacity.",
            502 => $"{baseUrl}: HTTP 502 — the proxy cannot reach STN.",
            404 => $"{baseUrl}: HTTP 404 — the /ws route is unavailable.",
            401 or 403 => $"{baseUrl}: authentication rejected ({statusCode}).",
            200 => $"{baseUrl}: HTTP 200 instead of WebSocket 101; authentication or routing was rejected.",
            _ => $"{baseUrl}: {exception.Message.Replace(credential, "[REDACTED]", StringComparison.Ordinal)}",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        _lifecycleGate.Dispose();
    }
}
