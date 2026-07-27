using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mezube.Bot;

namespace Mezube.Stn;

public sealed class StnSocketClient : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly ILogger<StnSocketClient> _logger;
    private readonly string _wsBase;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(512);
    private readonly byte[] _receiveBuffer = new byte[8 * 1024];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private string? _token;
    private long _botUserId;

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
        _logger.LogInformation("Connecting STN publisher WS {Uri}", RedactToken(uri));
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
            _logger.LogInformation("STN connected via {Base}", _wsBase);
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

    public Task PlayAsync(long clanId, long streamChannelId, string fileUrl, CancellationToken cancellationToken = default)
        => SendKeyAsync("connect_publisher", clanId, streamChannelId, fileUrl, cancellationToken);

    public Task StopAsync(long clanId, long streamChannelId, CancellationToken cancellationToken = default)
        => SendKeyAsync("stop_publisher", clanId, streamChannelId, fileUrl: string.Empty, cancellationToken);

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

            _logger.LogInformation(
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
                    break;
                }

                if (result.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    var text = Encoding.UTF8.GetString(_receiveBuffer, 0, result.Count);
                    _logger.LogDebug("STN WS message: {Message}", text);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STN receive loop ended");
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
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
