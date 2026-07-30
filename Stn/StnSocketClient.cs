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
    private TaskCompletionSource? _publisherEnded;

    public StnSocketClient(BotOptions options, ILogger<StnSocketClient> logger)
    {
        _options = options;
        _logger = logger;
        _wsBase = StnUrl.WebSocketBase(options.StnBaseUrl);
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task EnsureConnectedAsync(
        string authToken,
        long botUserId,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        _botUserId = botUserId;
        if (IsConnected && string.Equals(_token, authToken, StringComparison.Ordinal))
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _token = authToken;

        var displayName = string.IsNullOrWhiteSpace(username) ? _options.BotDisplayName : username;
        var uri = BuildWsUri(_wsBase, authToken, displayName);
        var socket = CreateSocket();
        _logger.LogDebug("Connecting STN publisher WS {Uri}", RedactToken(uri));
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
            _logger.LogInformation("STN publisher WS connected via {Base}", _wsBase);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                "Không kết nối được STN streaming WebSocket (cùng môi trường với Mezon host).\n" +
                DescribeFailure(_wsBase, ex) +
                "\nDev bot → cần stn.nccsoft.vn. Không dùng stn.mezon.ai với JWT nccsoft.",
                ex);
        }
    }

    public async Task PlayAsync(long clanId, long streamChannelId, string fileUrl, CancellationToken cancellationToken = default)
    {
        await SendKeyAndWaitAsync(
                "connect_publisher",
                "connect_publisher",
                clanId,
                streamChannelId,
                fileUrl,
                cancellationToken)
            .ConfigureAwait(false);
        // Arm end-waiter only after a successful publish ack so reconnect/disconnect
        // during EnsureConnected cannot complete it early.
        ResetPublisherEnded();
    }

    public async Task StopAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsConnected)
            {
                await SendKeyAsync("stop_publisher", clanId, streamChannelId, fileUrl: string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            CompletePublisherEnded();
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task WaitUntilPublisherEndedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _publisherEnded;
        if (tcs is null)
        {
            // PlayAsync arms this after connect ack; if missing, fall back to duration-only wait.
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private void ResetPublisherEnded()
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _publisherEnded, next);
        previous?.TrySetCanceled();
    }

    private void CompletePublisherEnded()
    {
        _publisherEnded?.TrySetResult();
    }

    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        // Some STN proxies mishandle HTTP/2 upgrade negotiations.
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
        if (msg.Contains("'502'", StringComparison.Ordinal))
        {
            return $"- {baseUrl}: 502 Bad Gateway (service STN down / nginx không proxy được backend).";
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
        CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ackWaiters.TryAdd(ackKey, waiter))
        {
            throw new InvalidOperationException($"STN command already in flight: {ackKey}");
        }

        try
        {
            await SendKeyAsync(key, clanId, streamChannelId, fileUrl, cancellationToken).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandAckTimeout);
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
                writer.WriteString("Key"u8, key);
                writer.WritePropertyName("Value"u8);
                writer.WriteStartObject();
                writer.WriteString("ChannelId"u8, streamChannelId.ToString());
                writer.WriteString("Password"u8, "");
                writer.WriteString("FileUrl"u8, fileUrl);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            _logger.LogDebug(
                "STN send key={Key} channel={ChannelId} via={Base}",
                key,
                streamChannelId,
                _wsBase);
            await _socket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
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

            if (string.Equals(key, "error", StringComparison.Ordinal))
            {
                var error = ReadJsonValueAsString(doc.RootElement);
                CompleteAck("connect_publisher", error ?? "unknown STN error");
                CompletePublisherEnded();
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
