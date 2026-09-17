using Mezube.Bot;
using Mezube.Media;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Mezube.Sfu;

public sealed class SfuClientHolder
{
    private MezonClient? _client;

    public void SetClient(MezonClient client) => _client = client;

    public MezonClient GetClient() => _client ?? throw new InvalidOperationException("Mezon client is not ready.");
}

/// <summary>
/// In-process SFU publisher. It owns the signaling WebSocket, the WebRTC
/// peer connection and the Ogg/Opus playback loop for one stream channel.
/// </summary>
public sealed class SfuPublisherSessionManager : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SfuPublisherSessionManager> _logger;
    private readonly ConcurrentDictionary<long, SfuPublisherSession> _sessions = new();
    private readonly object _gate = new();

    public SfuPublisherSessionManager(
        BotOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<SfuPublisherSessionManager> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public SfuPublisherSession GetOrCreate(long channelId)
    {
        if (channelId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId));
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(channelId, out var existing))
            {
                return existing;
            }

            if (_sessions.Count >= _options.SfuMaxSessions)
            {
                throw new InvalidOperationException("SFU publisher session capacity is exhausted.");
            }

            var session = new SfuPublisherSession(
                _options,
                _httpClientFactory.CreateClient(nameof(MezonCdnUploader)),
                _logger,
                channelId);
            _sessions[channelId] = session;
            return session;
        }
    }

    public bool TryGet(long channelId, out SfuPublisherSession session)
        => _sessions.TryGetValue(channelId, out session!);

    public async Task RemoveAndDisposeAsync(long channelId)
    {
        if (_sessions.TryRemove(channelId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task DisposeAllAsync()
    {
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(DisposeAllAsync());
}

public sealed class SfuPublisherSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly BotOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TaskCompletionSource _connected = NewSignal();
    private Task? _runTask;
    private CancellationTokenSource? _runCts;
    private DirectSfuConnection? _connection;
    private TaskCompletionSource? _trackEnded;
    private CancellationTokenSource? _trackCts;
    private string? _trackId;
    private Func<CancellationToken, Task<string>>? _tokenProvider;
    private bool _started;
    private bool _stopped;
    private bool _paused;

    internal SfuPublisherSession(BotOptions options, HttpClient httpClient, ILogger logger, long channelId)
    {
        _options = options;
        _httpClient = httpClient;
        _logger = logger;
        ChannelId = channelId;
        SessionId = $"mezube-{channelId}-{Guid.NewGuid():N}";
    }

    public long ChannelId { get; }
    public string SessionId { get; }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    internal async Task StartAsync(
        string token,
        Func<CancellationToken, Task<string>> tokenProvider,
        CancellationToken cancellationToken)
    {
        Task connectedTask;
        var startedHere = false;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(SfuPublisherSession));
            }

            _tokenProvider = tokenProvider;
            connectedTask = _connected.Task;
            if (!_started)
            {
                _started = true;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                _runTask = RunReconnectLoopAsync(token, _runCts.Token);
                startedHere = true;
            }
        }

        try
        {
            await connectedTask.WaitAsync(
                    TimeSpan.FromMilliseconds(_options.SfuConnectTimeoutMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (startedHere)
            {
                await StopConnectionLoopAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task PlayAsync(
        string trackId,
        string mediaUrl,
        string token,
        Func<CancellationToken, Task<string>> tokenProvider,
        CancellationToken cancellationToken)
    {
        await StartAsync(token, tokenProvider, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(trackId))
        {
            throw new ArgumentException("An SFU track id is required.", nameof(trackId));
        }

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri)
            || (mediaUri.Scheme != Uri.UriSchemeHttp && mediaUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("SFU playback requires an absolute HTTP(S) media URL.", nameof(mediaUrl));
        }

        lock (_gate)
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(SfuPublisherSession));
            }

            _trackCts?.Cancel();
            _trackCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _trackId = trackId;
            _trackEnded = NewSignal();
            _ = StreamAudioAsync(trackId, mediaUri, _trackCts.Token);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task WaitUntilTrackEndedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_trackEnded is null)
            {
                return Task.FromException(new InvalidOperationException("No active SFU track."));
            }

            return _trackEnded.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task PauseAsync(bool paused, CancellationToken cancellationToken)
    {
        DirectSfuConnection? connection;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _paused = paused;
            connection = _connection;
        }

        if (connection is { IsConnected: true })
        {
            await SendJsonAsync(connection, new { type = "mute", is_mute = paused }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task EndTrackAsync(string? trackId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (trackId is not null && _trackId is not null
                && !string.Equals(trackId, _trackId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            _trackCts?.Cancel();
            _trackEnded?.TrySetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? runTask;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _trackCts?.Cancel();
            _trackEnded?.TrySetCanceled();
            _runCts?.Cancel();
            _connection?.Close();
            runTask = _runTask;
        }

        // Stop is a resource teardown operation. Once the connection has been
        // closed, wait for the run loop to finish before disposing the shared
        // write gate; otherwise a late heartbeat/mute callback can race the
        // semaphore disposal when the caller token is already cancelled.
        await AwaitQuietlyAsync(runTask, CancellationToken.None).ConfigureAwait(false);
        _lifetimeCts.Cancel();
        _runCts?.Dispose();
        _trackCts?.Dispose();
        _writeGate.Dispose();
        _lifetimeCts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task RunReconnectLoopAsync(string initialToken, CancellationToken cancellationToken)
    {
        var token = initialToken;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "SFU publisher connection failed channel={ChannelId}; reconnecting error_type={ErrorType}",
                    ChannelId,
                    ex.GetType().Name);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var provider = GetTokenProvider();
            if (provider is null)
            {
                return;
            }

            try
            {
                // Never reuse the previous JWT after a WebSocket/PeerConnection failure.
                token = await provider(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("Mezon returned an empty SFU meet token.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "SFU publisher token refresh failed channel={ChannelId} error_type={ErrorType}",
                    ChannelId,
                    ex.GetType().Name);
                await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            ResetConnectionSignal();
            await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAndRunAsync(string token, CancellationToken cancellationToken)
    {
        using var websocket = new ClientWebSocket();
        websocket.Options.SetRequestHeader("User-Agent", "mezube/1.0");
        await websocket.ConnectAsync(new Uri(_options.SfuWebSocketUrl), cancellationToken).ConfigureAwait(false);

        using var peerConnection = CreateAudioPublisherPeerConnection();
        var connection = new DirectSfuConnection(websocket, peerConnection);
        lock (_gate)
        {
            if (_stopped)
            {
                connection.Close();
                return;
            }

            _connection = connection;
        }

        peerConnection.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                lock (_gate)
                {
                    _connected.TrySetResult();
                }

                _ = SendMuteAfterConnectAsync(connection, cancellationToken);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                connection.SetFailure(new InvalidOperationException("SFU WebRTC connection failed."));
                connection.Close();
            }
        };

        try
        {
            await SendJsonAsync(
                    connection,
                    new
                    {
                        type = "join",
                        room = ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        token,
                        role = "speaker"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = HeartbeatLoopAsync(connection, heartbeatCts.Token);
            try
            {
                while (true)
                {
                    var message = await ReceiveTextAsync(websocket, cancellationToken).ConfigureAwait(false);
                    if (message is null)
                    {
                        throw connection.Failure ?? new InvalidOperationException("SFU signaling connection closed.");
                    }

                    await HandleSignalingMessageAsync(connection, message, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                heartbeatCts.Cancel();
                await AwaitQuietlyAsync(heartbeatTask, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.Close();
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
        }
    }

    internal static RTCPeerConnection CreateAudioPublisherPeerConnection()
    {
        var peerConnection = new RTCPeerConnection(null);
        var audioTrack = new MediaStreamTrack(
            new List<AudioFormat> { new(AudioCodecsEnum.OPUS, 111, 48000, 2) },
            MediaStreamStatusEnum.SendOnly)
        {
            NoDtmfSupport = true
        };
        peerConnection.addTrack(audioTrack);
        return peerConnection;
    }

    private async Task HandleSignalingMessageAsync(
        DirectSfuConnection connection,
        string payload,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "ping":
                await SendJsonAsync(connection, new { type = "pong" }, cancellationToken).ConfigureAwait(false);
                break;
            case "pong":
            case "joined":
            case "peer_joined":
            case "peer_left":
            case "peer_updated":
            case "mute_changed":
                break;
            case "offer":
                await AnswerOfferAsync(connection, document.RootElement, cancellationToken).ConfigureAwait(false);
                break;
            case "error":
                throw new InvalidOperationException("SFU rejected publisher signaling.");
        }
    }

    private async Task AnswerOfferAsync(
        DirectSfuConnection connection,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var sdp = message.TryGetProperty("sdp", out var sdpElement) ? sdpElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(sdp))
        {
            throw new InvalidOperationException("SFU sent an empty offer.");
        }

        var answer = CreateAudioOnlyAnswer(connection.PeerConnection, sdp);
        await connection.PeerConnection.setLocalDescription(answer).ConfigureAwait(false);

        var generation = message.TryGetProperty("offer_generation", out var generationElement)
            && generationElement.TryGetUInt64(out var parsedGeneration)
            ? parsedGeneration
            : 0UL;
        await SendJsonAsync(
                connection,
                new { type = "answer", sdp = answer.sdp, offer_generation = generation },
                cancellationToken)
            .ConfigureAwait(false);
        await connection.PeerConnection.Start().ConfigureAwait(false);
    }

    internal static RTCSessionDescriptionInit CreateAudioOnlyAnswer(RTCPeerConnection peerConnection, string offerSdp)
    {
        var result = peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"SFU offer was rejected: {result}.");
        }

        var answer = peerConnection.createAnswer();
        var answerSdp = SDP.ParseSDPDescription(answer.sdp);
        ValidateAndDisableVideo(answerSdp, offerSdp);
        answer.sdp = answerSdp.ToString();
        return answer;
    }

    internal static void ValidateAndDisableVideo(SDP answer, string offerSdp)
    {
        var offer = SDP.ParseSDPDescription(offerSdp);
        if (answer.Media.Count != offer.Media.Count)
        {
            throw new InvalidOperationException("SFU answer changed the number of media sections.");
        }

        for (var index = 0; index < answer.Media.Count; index++)
        {
            var offerMedia = offer.Media[index];
            var answerMedia = answer.Media[index];
            if (offerMedia.Media != answerMedia.Media
                || !string.Equals(offerMedia.MediaID, answerMedia.MediaID, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SFU answer changed media section order or mid.");
            }

            if (answerMedia.Media == SDPMediaTypesEnum.video)
            {
                answerMedia.MediaStreamStatus = MediaStreamStatusEnum.Inactive;
            }
        }
    }

    private async Task StreamAudioAsync(string trackId, Uri mediaUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                    mediaUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var reader = new OggOpusReader(body);

            while (true)
            {
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                var packet = await reader.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    CompleteTrack(trackId);
                    return;
                }

                var duration = OpusPacket.GetDurationSamples(packet);
                await WaitForConnectedAsync(cancellationToken).ConfigureAwait(false);
                var connection = GetConnection();
                if (connection is null)
                {
                    throw new InvalidOperationException("SFU publisher connection is unavailable.");
                }

                connection.PeerConnection.SendAudio(duration, packet);
                await Task.Delay(TimeSpan.FromSeconds(duration / 48000d), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // HttpClient/WebSocket exception messages may contain a request URI.
            // Keep media URLs and authentication material out of operational logs.
            _logger.LogWarning(
                "SFU audio track failed channel={ChannelId} track={TrackId} error_type={ErrorType}",
                ChannelId,
                trackId,
                ex.GetType().Name);
            FailTrack(trackId, new InvalidOperationException("SFU audio playback failed.", ex));
        }
    }

    private async Task WaitForConnectedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetConnection() is { IsConnected: true })
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(DirectSfuConnection connection, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendJsonAsync(connection, new { type = "ping" }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendMuteAfterConnectAsync(DirectSfuConnection connection, CancellationToken cancellationToken)
    {
        bool paused;
        lock (_gate)
        {
            paused = _paused;
        }

        if (!paused)
        {
            return;
        }

        try
        {
            await SendJsonAsync(connection, new { type = "mute", is_mute = true }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(
                "SFU mute state could not be restored channel={ChannelId} error_type={ErrorType}",
                ChannelId,
                ex.GetType().Name);
        }
    }

    private async Task SendJsonAsync(
        DirectSfuConnection connection,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.WebSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("SFU signaling WebSocket is not open.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SendTimeout);
            await connection.WebSocket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket websocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        await using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await websocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            }
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private DirectSfuConnection? GetConnection()
    {
        lock (_gate)
        {
            return _connection;
        }
    }

    private Func<CancellationToken, Task<string>>? GetTokenProvider()
    {
        lock (_gate)
        {
            return _tokenProvider;
        }
    }

    private void ResetConnectionSignal()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted)
            {
                _connected = NewSignal();
            }
        }
    }

    private async Task StopConnectionLoopAsync()
    {
        CancellationTokenSource? runCts;
        Task? runTask;
        lock (_gate)
        {
            _started = false;
            runCts = _runCts;
            runTask = _runTask;
            _connection?.Close();
            _runCts = null;
            _runTask = null;
            _connected = NewSignal();
        }

        runCts?.Cancel();
        await AwaitQuietlyAsync(runTask, CancellationToken.None).ConfigureAwait(false);
        runCts?.Dispose();
    }

    private async Task DelayReconnectAsync(CancellationToken cancellationToken)
    {
        if (_options.SfuReconnectBackoffMs > 0)
        {
            await Task.Delay(_options.SfuReconnectBackoffMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteTrack(string trackId)
    {
        lock (_gate)
        {
            if (string.Equals(_trackId, trackId, StringComparison.Ordinal))
            {
                _trackEnded?.TrySetResult();
            }
        }
    }

    private void FailTrack(string trackId, Exception exception)
    {
        lock (_gate)
        {
            if (string.Equals(_trackId, trackId, StringComparison.Ordinal))
            {
                _trackEnded?.TrySetException(exception);
            }
        }
    }

    private async Task AwaitQuietlyAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "SFU publisher task stopped with an error channel={ChannelId} error_type={ErrorType}",
                ChannelId,
                ex.GetType().Name);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DirectSfuConnection : IDisposable
    {
        private int _closed;
        private Exception? _failure;

        public DirectSfuConnection(ClientWebSocket websocket, RTCPeerConnection peerConnection)
        {
            WebSocket = websocket;
            PeerConnection = peerConnection;
        }

        public ClientWebSocket WebSocket { get; }
        public RTCPeerConnection PeerConnection { get; }
        public bool IsConnected => PeerConnection.connectionState == RTCPeerConnectionState.connected;
        public Exception? Failure => _failure;

        public void SetFailure(Exception failure) => Interlocked.CompareExchange(ref _failure, failure, null);

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                WebSocket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                PeerConnection.close();
            }
            catch (Exception)
            {
            }
        }

        public void Dispose() => Close();
    }
}
